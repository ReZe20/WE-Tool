using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Serilog;
using WE_Tool.Controls;
using WE_Tool.Helper;
using WE_Tool.Json;
using WE_Tool.Service;
using WinUIEx;

namespace WE_Tool.Views;

/// <summary>
/// 移动版转换队列的副窗口(母进程带 <c>--mpkg-window</c> 自我启动的子进程)。
/// 内容和 Papers 页覆盖层里那块是同一个 <see cref="MpkgQueuePanel"/>,区别只在宿主:
/// 队列整份搬过来之后,<b>编辑、探测、开转都发生在这一侧</b>,转换子进程也由本进程起 ——
/// 母进程不再同时持有第二份可改的状态,所以不需要双向同步,只需要"交回"。
///
/// 交回的三个时机:每次改动之后节流存一份(子进程被打掉时母进程不至于整批丢失)、
/// 点「贴回」时带着 Dock 交回(母进程顺带把页面面板展开)、窗口关闭时再交回一次。
/// </summary>
public sealed partial class MpkgQueueWindow : WindowEx
{
    private readonly MpkgWindowSnapshot _snapshot;
    private readonly MpkgQueuePanel _panel;
    private PropertyWindowChannel? _channel;
    private string _theme = "";

    // 队列改动的存盘节流:一行 slider 拖一下会连着报好几条改动,那不需要每改一下就发一整份队列
    private DispatcherQueueTimer? _syncDebounce;
    private bool _handedBack;

    public MpkgQueueWindow(MpkgWindowSnapshot snapshot)
    {
        _snapshot = snapshot;
        _theme = snapshot.Theme ?? "";
        InitializeComponent();

        // 标题走 LanguageHelper:MRT Core 默认构造不依赖视图上下文,取不到时兜底一句中文,
        // 保证任务栏/Alt+Tab 上是个能读懂的名字而不是空串(属性副窗口同一条理由)。
        string title = "移动版转换队列";
        try
        {
            var t = LanguageHelper.GetResource("MpkgQueueWindowTitle.Title");
            if (!string.IsNullOrEmpty(t)) title = t;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[移动版副窗] 读取窗口标题资源失败,用兜底文字");
        }
        Title = title;

        // 标题栏照属性窗口那条改:去掉系统标题栏,顶部按 Tall 高度(48)由面板头部本身充当标题带,
        // 背景透出亚克力。带里那颗「贴回」键要能点,所以它那块矩形另外登记成 Passthrough(见 UpdateTitleBarInputRegions)。
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        // 尺寸由母进程带过来(它只在本次会话里记上一次的值,不写 config.json);0 = 用默认。
        int w = _snapshot.Width > 0 ? _snapshot.Width : 620;
        int h = _snapshot.Height > 0 ? _snapshot.Height : 760;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
        ApplyTheme();

        _panel = QueuePanel;
        _panel.HostedInWindow = true;
        _panel.OutputRoot = _snapshot.OutputRoot;
        _panel.ProcessPriority = _snapshot.ProcessPriority;
        // 模糊开关要在建行之前落定:ImportState 建的那批行是在 AttachRow 里判该不该糊的
        _panel.SetBlurFlags(_snapshot.BlurEveryone, _snapshot.BlurTeen, _snapshot.BlurAdult);
        // 队列与面板状态要在挂事件之前落定:ImportState 会改模式、建行,那些不该再触发一次"存回母进程"
        _panel.ImportState(_snapshot.Panel, _snapshot.Rows);
        _panel.NotifyOpened();

        _panel.DockRequested += OnDockRequested;
        _panel.CloseRequested += Close;
        _panel.QueueChanged += ScheduleHandBack;
        _panel.ConversionBusyChanged += busy =>
            Send(new PropertyWindowMessage { Kind = PropertyWindowLink.KindMpkgBusy, Busy = busy });

        // 尺寸每变一次报一次(母进程把它留着,下次 detach 就用这个尺寸开)。
        // 母进程退出时本进程被 Job Object 带走,那一次的报不回,与属性副窗口同一处已知降级。
        AppWindow.Changed += (_, _) =>
        {
            Send(new PropertyWindowMessage
            {
                Kind = PropertyWindowLink.KindSize,
                Width = AppWindow.Size.Width,
                Height = AppWindow.Size.Height,
            });
            UpdateTitleBarInputRegions();
        };

        // 标题栏那套量(带高、系统按钮区宽、那颗键的位置)要等布局落定才取得到,
        // 所以挂在首次加载与尺寸变化上,不在构造函数里算 —— 那时 ActualWidth 还是 0、RightInset 也还没值。
        RootGrid.Loaded += (_, _) => UpdateTitleBarInputRegions();
        RootGrid.SizeChanged += (_, _) => UpdateTitleBarInputRegions();

        Closed += (_, _) =>
        {
            // 先掐掉本进程还在跑的探测/转换,再把队列交回去:反过来的话,母进程收到的会是一份
            // 后面还会被本进程改动的状态,而 repkg 子进程没人收尸。
            _panel.Shutdown();
            HandBack(dock: false, final: true);
            _channel?.Dispose();
            // 本进程只有这一个窗口:关掉就退,别留一个没有窗口的空进程等 Job Object 收尸
            Application.Current.Exit();
        };

        string blur = $"{(_snapshot.BlurEveryone ? "E" : "")}{(_snapshot.BlurTeen ? "T" : "")}{(_snapshot.BlurAdult ? "A" : "")}";
        Log.Information("[移动版副窗] 装配完成: {Rows} 行, 自定义={Custom}, 输出={Output}, 尺寸={W}x{H}, 预览模糊={Blur}(全空=不糊)",
            _snapshot.Rows.Count, _snapshot.Panel.CustomMode ? "开" : "关", _snapshot.OutputRoot, w, h,
            string.IsNullOrEmpty(blur) ? "关" : blur);

        StartChannel();
    }

    /// <summary>接母进程的管道:本进程是 server(管名来自载荷)。连不上只是交不回队列与跟随不了主题,窗口照常能用。</summary>
    private void StartChannel()
    {
        string pipeName = _snapshot.PipeName;
        if (string.IsNullOrEmpty(pipeName)) return;

        _ = Task.Run(async () =>
        {
            var channel = await PropertyWindowChannel
                .AcceptAsServerAsync(pipeName, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (channel == null)
            {
                Log.Warning("[移动版副窗] 管道未建成,主题跟随与队列交回不可用:母进程若在退出前没接上,这批待转项会跟着本进程没掉");
                return;
            }

            // 本窗口的 DispatcherQueue 必须在 UI 线程上取(Window.DispatcherQueue 是自由线程可读的)
            var queue = DispatcherQueue;
            channel.MessageReceived += message => queue?.TryEnqueue(() => OnLinkMessage(message));
            _channel = channel;
            Log.Information("[移动版副窗] 与母进程的管道已连通");
        });
    }

    /// <summary>母进程发来的消息:主题、前置、新入队的行。</summary>
    private void OnLinkMessage(PropertyWindowMessage message)
    {
        switch (message.Kind)
        {
            case PropertyWindowLink.KindTheme:
                // 这一条消息同时捎主题与预览模糊三档。两者各自判变:主题没变时模糊也可能刚被勾上,
                // 不能因为主题相等就整条跳过(开关本身会在没变时直接返回,所以这里每次都送)。
                _panel.SetBlurFlags(message.BlurEveryone, message.BlurTeen, message.BlurAdult);
                string newTheme = message.Theme ?? "";
                if (newTheme == _theme) break;
                _theme = newTheme;
                ApplyTheme();
                break;
            case PropertyWindowLink.KindFocus:
                Activate();
                break;
            case PropertyWindowLink.KindMpkgRows:
                if (message.Rows is { Count: > 0 }) _panel.AddRows(message.Rows);
                // 母进程又投了几张进来 = 有新任务。这时不管窗口是被压在后面还是最小化了,都要弹到人面前:
                // 队列在独立窗口里之后,页面覆盖层不再出现,不唤一下人就看不出"点下去的那几张去哪了"。
                Wake();
                break;
        }
    }

    /// <summary>把这扇窗口推到前面;最小化的先恢复。唤这一步不看管道是否连通,新任务来了就要人看得见。</summary>
    private void Wake()
    {
        bool wasMinimized = false;
        if (AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized)
        {
            p.Restore();
            wasMinimized = true;
        }
        Activate();
        Log.Information("[移动版副窗] 唤出窗口: {From}", wasMinimized ? "最小化恢复" : "前置");
    }

    /// <summary>
    /// 标题栏输入区:顶部那条带归系统(可拖动、右边一排是最小化/最大化/关闭),
    /// 而带里那颗「贴回」是 XAML 内容 —— 不登记 Passthrough 的话点它会落在标题栏上、到不了按钮。
    /// 只登记按钮那一条矩形,带上的其它地方继续能拖(主窗口的顶部导航同一条办法,见 MainWindow.UpdateTopNavInputRegions)。
    /// 让位宽与矩形都用算术取,不去可视树里 transform:margin 刚改完那一帧位置还没落定,
    /// 而"按钮贴在让位区的左边"这件事本身就是我们摆的,算出来和量出来是同一个数。
    /// </summary>
    private void UpdateTitleBarInputRegions()
    {
        try
        {
            if (Content is not FrameworkElement root || root.XamlRoot is not { } xamlRoot) return;
            double scale = xamlRoot.RasterizationScale;
            if (scale <= 0) return;

            double reserve = AppWindow.TitleBar.RightInset / scale + 8;
            _panel.TitleBarRightReserve = reserve;

            double widthDip = root.ActualWidth;
            if (_panel.HeaderActions is not { ActualWidth: > 0 } actions || widthDip <= 0) return;

            // 系统三键(最小化/最大化/关闭)就住在带子右端 RightInset 那一段非客户区里。
            // Passthrough 一旦盖过它们的左缘,点击就被判给客户区的 XAML,三键全成哑的(2026-10-01 实测)。
            // 所以矩形右界取"三键区左缘"这条硬线,按钮两侧那点余量只准落在让位带留出的 8 DIP 空隙里。
            double captionLeftDip = widthDip - AppWindow.TitleBar.RightInset / scale;
            double xDip = Math.Max(0, widthDip - reserve - actions.ActualWidth + 4);
            double wDip = Math.Min(actions.ActualWidth + 8, Math.Max(0, captionLeftDip - xDip));
            var rect = new Windows.Graphics.RectInt32(
                (int)Math.Round(xDip * scale),
                0,
                (int)Math.Ceiling(wDip * scale),
                AppWindow.TitleBar.Height);

            InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
                .SetRegionRects(NonClientRegionKind.Passthrough, [rect]);
            Log.Information("[移动版副窗] 标题栏输入区: 让位 {Reserve} DIP, 可点区 {X}+{W} DIP(止于 {Stop}), 三键区左缘 {Caption} DIP, 带高 {Bar} px",
                reserve, xDip, wDip, xDip + wDip, captionLeftDip, AppWindow.TitleBar.Height);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[移动版副窗] 设置标题栏输入区域失败");
        }
    }

    /// <summary>主题由母进程经快照+管道给出:独立窗口不继承主窗口根元素的 RequestedTheme,
    /// 而本进程根本没有主窗口可读(主 VM 只在母进程里)。</summary>
    private void ApplyTheme()
    {
        if (Content is not FrameworkElement root) return;
        root.RequestedTheme = _theme switch
        {
            "Dark" => ElementTheme.Dark,
            "Light" => ElementTheme.Light,
            _ => ElementTheme.Default
        };
    }

    /// <summary>「贴回」:整份队列带着 Dock 交回母进程,然后关掉自己的窗口。</summary>
    private void OnDockRequested()
    {
        HandBack(dock: true, final: true);
        Close();
    }

    /// <summary>往母进程发一条与队列无关的小消息(转换忙闲、窗口尺寸)。管道没通就丢掉,不值得为它排队重发。</summary>
    private void Send(PropertyWindowMessage message)
    {
        var channel = _channel;
        if (channel == null) return;
        // 管道写可能在母进程不读时阻塞,一律离开 UI 线程
        _ = Task.Run(() => channel.Send(message));
    }

    private void ScheduleHandBack()
    {
        if (_handedBack) return;
        _syncDebounce ??= CreateSyncTimer();
        _syncDebounce.Stop();
        _syncDebounce.Start();
    }

    private DispatcherQueueTimer CreateSyncTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => HandBack(dock: false, final: false);
        return timer;
    }

    /// <summary>把整份队列 + 面板状态交回母进程。
    /// final=true 表示这是最后一次(贴回或关窗),之后不再发;
    /// 中间那些只是"存一份",母进程收下但不弹面板。</summary>
    private void HandBack(bool dock, bool final)
    {
        // 最后一次交回之后本进程不再发队列:再发就是往一个已经不持有队列的母进程里灌第二份状态。
        if (_handedBack) return;
        var channel = _channel;
        if (channel == null)
        {
            if (final) Log.Warning("[移动版副窗] 交回队列失败:管道还没连通,母进程没有这批待转项的副本({Rows} 行)", _panel.Items.Count);
            return;
        }

        var message = new PropertyWindowMessage
        {
            Kind = PropertyWindowLink.KindMpkgQueue,
            Dock = dock,
            Rows = _panel.ExportRows(),
            Panel = _panel.ExportPanelState(),
        };

        if (final)
        {
            _handedBack = true;
            // 最后一次必须在退出前坐实:这时进程还活着,而同步写只在母进程不读时才会阻塞,
            // 母进程的读循环是一路读到底的,所以这里值得让 UI 线程等这一次(几十行也就几 KB)。
            try
            {
                channel.Send(message);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[移动版副窗] 最后一次交回没送出去: {Rows} 行", message.Rows?.Count ?? 0);
            }
        }
        else
        {
            // 平时离开 UI 线程:管道写在对方不读时会阻塞,不该把界面钉住
            _ = Task.Run(() => channel.Send(message));
        }

        Log.Information("[移动版副窗] 队列交回母进程: {Rows} 行, Dock={Dock}, {Why}",
            message.Rows?.Count ?? 0, dock, final ? "收尾那一次" : "改动存一份");
    }
}

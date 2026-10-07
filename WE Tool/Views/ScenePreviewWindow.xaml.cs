using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Serilog;
using WE_Tool.Helper;
using WE_Tool.Json;
using WE_Tool.Models;
using WE_Tool.Service;
using WinUIEx;

namespace WE_Tool.Views;

/// <summary>
/// 场景壁纸预览副窗口(母进程带 <c>--scene-preview-window</c> 自我启动的子进程)。
/// 本进程只干一件事:开一块 WebView2,让 WebWallGL 在里面把那张场景壁纸画出来。
/// 关窗即退进程,浏览器进程随之回收——这是把它做出进程的全部理由:
/// 留在主进程里,那几百 MB 要背到主程序退出为止。
///
/// 单窗口:再预览别的壁纸是母进程发来 preview 消息让这块换内容,不再起第二个进程
/// (一个进程 = 一份 XAML 运行时 + 一套浏览器进程,起两个纯浪费)。
/// </summary>
public sealed partial class ScenePreviewWindow : WindowEx
{
    private const int DefaultWidth = 1120;
    private const int DefaultHeight = 630;

    /// <summary>可控制属性模式下右栏占多宽(XAML 里那个初值要与此一致)。</summary>
    private const double PropsColumnWidth = 320;

    private readonly ScenePreviewWindowSnapshot _snapshot;
    private readonly string _titleLabel;
    private PropertyWindowChannel? _channel;
    private string _theme = "";
    private string _mode = ScenePreviewModes.Properties;
    private string _folder = "";

    /// <summary>比例模式的目标预览面尺寸(物理 px)。null = 不在对比例,窗口尺寸归用户。</summary>
    private (double W, double H)? _fitTarget;
    private int _fitPass;

    /// <summary>右栏当前挂着的行:换壁纸时要先摘掉它们的 PropertyChanged,
    /// 否则旧壁纸的编辑会把键值发到新实例上(属性名逐壁纸不同,那是一次纯污染)。</summary>
    private readonly List<WallpaperProperty> _tracked = [];

    /// <summary>等待发出的属性改动(属性名 → JSON 字面量)。同一行连拖只留最后一个值。</summary>
    private readonly Dictionary<string, string> _pendingProps = new();

    private DispatcherQueueTimer? _propTimer;
    private int _propsVersion;

    /// <summary>滑杆一次拖拽会出几十到几百个中间值,而每次 setProperties 都要过一遍消息通道、
    /// 并让场景里所有声明了 applyUserProperties 的脚本各跑一次。停手 60ms 再发,手感上仍是跟手的。</summary>
    private const int PropertySendDelayMs = 60;

    /// <summary>值成员名 → 会被这些成员改动的行才需要发出去(其余 PropertyChanged 是显示用的计算属性)</summary>
    private static readonly HashSet<string> ValueMembers = new(StringComparer.Ordinal)
    {
        nameof(WallpaperProperty.BoolValue),
        nameof(WallpaperProperty.SliderValue),
        nameof(WallpaperProperty.ComboIndex),
        nameof(WallpaperProperty.ColorValue),
        nameof(WallpaperProperty.TextValue),
    };

    public ScenePreviewWindow(ScenePreviewWindowSnapshot snapshot)
    {
        _snapshot = snapshot;
        _theme = snapshot.Theme ?? "";
        _titleLabel = ReadTitleLabel();
        // 旧载荷没有 Mode 字段:按可控制属性模式处理,那是这套窗口的原始形态
        _mode = string.IsNullOrEmpty(snapshot.Mode) ? ScenePreviewModes.Properties : snapshot.Mode;
        _folder = snapshot.Folder ?? "";
        InitializeComponent();

        Title = BuildTitle(snapshot.Title);
        // 自己画标题带:整条 IsHitTestVisible=False,所以能拖;WebView2 住在第二行,不进这条带,
        // 系统三键不会被它的窗口层压住(这是本窗口不需要主窗口那套 Passthrough 的原因)
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        HeaderText.Text = Title;

        // 尺寸由母进程带过来(它只在本次会话里记上一次的值,不写 config.json);0 = 用默认。
        // 比例模式会紧接着把它改掉:那两种的尺寸是按屏幕/手机比例算出来的,套上"上次尺寸"比例就歪了
        int w = snapshot.Width > 0 ? snapshot.Width : DefaultWidth;
        int h = snapshot.Height > 0 ? snapshot.Height : DefaultHeight;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
        ApplyTheme();
        ApplyLayoutForMode();
        FitToCurrentMode();

        RootGrid.Loaded += (_, _) =>
        {
            SyncTitleBarRowToCaptionButtons();
            FitStep();
            _ = ShowAsync(_folder, snapshot.Title);
        };
        // 标题带行高与比例对位都要等布局落定才量得到,所以每次尺寸变化补一次(同主窗口那条校准)
        RootGrid.SizeChanged += (_, _) =>
        {
            SyncTitleBarRowToCaptionButtons();
            FitStep();
        };

        // 预览宿主的状态读数:首帧耗时、库的诊断、渲染失败都落到底下那一行
        ScenePreviewHost.StatusChanged += () => DispatcherQueue.TryEnqueue(() =>
            StatusText.Text = ScenePreviewHost.StatusLine);

        // 尺寸每变一次报一次,母进程留着下次开就用这个尺寸。
        // 母进程退出时本进程被 Job Object 带走,那一次报不回——与另三扇窗同一处已知降级。
        AppWindow.Changed += (_, _) => Send(new PropertyWindowMessage
        {
            Kind = PropertyWindowLink.KindSize,
            Width = AppWindow.Size.Width,
            Height = AppWindow.Size.Height,
        });

        Closed += (_, _) =>
        {
            _channel?.Dispose();
            // 本进程只有这一个窗口:关掉就退,别留一个没有窗口的空进程顶着整套浏览器进程
            Application.Current.Exit();
        };

        Log.Information("[预览副窗] 装配完成: Folder={Folder} 尺寸={W}x{H}", snapshot.Folder, w, h);
        StartChannel();
    }

    /// <summary>接母进程的管道:本进程是 server(管名来自载荷)。连不上只是跟随不了主题与换壁纸,预览照常。</summary>
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
                Log.Warning("[预览副窗] 管道未建成,主题跟随与换壁纸不可用: Pid={Pid}", Environment.ProcessId);
                return;
            }
            var queue = DispatcherQueue;
            channel.MessageReceived += message => queue?.TryEnqueue(() => OnLinkMessage(message));
            _channel = channel;
            Log.Information("[预览副窗] 与母进程的管道已连通");
        });
    }

    /// <summary>母进程发来的消息:换一张、前置、跟主题。</summary>
    private void OnLinkMessage(PropertyWindowMessage message)
    {
        switch (message.Kind)
        {
            case PropertyWindowLink.KindPreview:
                if (!string.IsNullOrEmpty(message.Folder))
                {
                    // 空 Mode = 沿用当前模式:右键再点同一栏的"预览"只是换一张,不该顺手改布局
                    if (!string.IsNullOrEmpty(message.Mode) && message.Mode != _mode)
                    {
                        _mode = message.Mode!;
                        ApplyLayoutForMode();
                        FitToCurrentMode();
                    }
                    _ = ShowAsync(message.Folder!, message.PreviewTitle ?? "");
                }
                break;
            case PropertyWindowLink.KindFocus:
                Wake();
                break;
            case PropertyWindowLink.KindTheme:
                string newTheme = message.Theme ?? "";
                if (newTheme == _theme) break;
                _theme = newTheme;
                ApplyTheme();
                break;
        }
    }

    /// <summary>装载这张壁纸:整窗铺满、帧率给到 60(详情页那块小图是 30)。</summary>
    private async Task ShowAsync(string folder, string title)
    {
        _folder = folder;
        if (!string.IsNullOrEmpty(title)) Title = BuildTitle(title);

        if (!ScenePreviewHost.RuntimeAvailable)
        {
            NeedRuntimeNotice.Visibility = Visibility.Visible;
            StatusText.Text = ScenePreviewHost.StatusLine;
            Log.Warning("[预览副窗] 系统没有 WebView2 运行时,只提醒不预览");
            return;
        }

        NeedRuntimeNotice.Visibility = Visibility.Collapsed;
        var shown = await ScenePreviewHost.TryShowAsync(SurfaceHost, folder, fillHost: true, fps: 60);
        StatusText.Text = ScenePreviewHost.StatusLine;
        // 装载失败就留提醒与读数,不要把一块黑面摆在窗口里
        if (!shown) { ScenePreviewHost.Hide(); return; }

        HeaderText.Text = Title;
        Wake();
        // 右栏与画面并行开跑:属性行读盘+建行只要几十 ms,而包解析要几秒,
        // 先建出来的行由宿主攒到首帧之后一次性补发。比例模式没有右栏,连读盘都省了。
        if (_mode == ScenePreviewModes.Properties) _ = LoadPropsAsync(folder);
        else ClearPropsRows();
    }

    // ========== 三种预览模式 ==========

    /// <summary>按当前模式摆布局:比例模式把右栏整列收成 0。收列而不是只藏面板,是因为那一列
    /// 只要还占着 320,预览面就被挤窄,看到的就不是那张屏幕/手机的比例了。</summary>
    private void ApplyLayoutForMode()
    {
        bool withProps = _mode == ScenePreviewModes.Properties;
        PropsPane.Visibility = withProps ? Visibility.Visible : Visibility.Collapsed;
        PropsColumn.Width = new GridLength(withProps ? PropsColumnWidth : 0);
        // 属性栏那边要 720 才不至于把预览挤没;两种比例模式不设下限 ——
        // 它们的尺寸是算出来的,任何下限都可能把比例顶歪(手机那一档在 1080 高的屏上只有 ~360 宽)
        MinWidth = withProps ? 720 : 0;
        MinHeight = withProps ? 360 : 0;
    }

    /// <summary>比例模式定尺寸;可控制属性模式不锁比例,窗口归用户自己拖。</summary>
    private void FitToCurrentMode()
    {
        var (mw, mh) = MonitorSizePx();
        if (_mode == ScenePreviewModes.DesktopRatio) FitSurface(mw, mh, mw, mh);
        else if (_mode == ScenePreviewModes.PhoneRatio) FitSurface(ScenePreviewModes.PhoneRatioW, ScenePreviewModes.PhoneRatioH, mw, mh);
        else _fitTarget = null;
    }

    /// <summary>本窗口所在那块显示器的面板像素尺寸。
    /// DisplayArea 只给工作区(扣掉任务栏),拿它的比例当「电脑比例」会在 16:9 的屏上算出 1.86,
    /// 所以这里只能走 Win32 取 rcMonitor。读不到就退回 16:9 —— 那是绝大多数面板的形状。</summary>
    private (int W, int H) MonitorSizePx()
    {
        try
        {
            IntPtr mon = MonitorFromWindow(this.GetWindowHandle(), MonitorDefaultToNearest);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref info))
                return (info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[预览副窗] 读显示器面板尺寸失败");
        }
        Log.Warning("[预览副窗] 显示器尺寸读不到,电脑比例按 16:9 兜底");
        return (1920, 1080);
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    /// <summary>把预览面定成 ratioW : ratioH,并夹在屏高 72% / 屏宽 80% 里 ——
    /// 9:19.5 在 16:9 的屏上不夹就会长到屏幕外。</summary>
    private void FitSurface(double ratioW, double ratioH, double boxW, double boxH)
    {
        if (ratioW <= 0 || ratioH <= 0 || boxW <= 0 || boxH <= 0) return;
        double h = boxH * 0.72;
        double w = h * (ratioW / ratioH);
        double maxW = boxW * 0.8;
        if (w > maxW)
        {
            w = maxW;
            h = w * (ratioH / ratioW);
        }
        _fitTarget = (Math.Round(w), Math.Round(h));
        _fitPass = 0;
        FitStep();
    }

    /// <summary>把窗口外框调到「预览面正好等于目标尺寸」。外框与预览面之间隔着标题带、状态行和窗口边框,
    /// 这三样都没有能直接查的量,所以只能量一次改一次:按实测差值修一版,最多三版(每版都会引起
    /// SizeChanged,再由那个回调推进下一版)。三版还不收敛就说明用户在同时拖窗口,那就让给他。</summary>
    private void FitStep()
    {
        if (_fitTarget is not { } target) return;
        if (SurfaceHost.ActualWidth <= 0 || SurfaceHost.ActualHeight <= 0) return;  // 还没布局,等下一次 SizeChanged
        if (_fitPass >= 3) { _fitTarget = null; return; }
        _fitPass++;
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
        int dw = (int)Math.Round(target.W - SurfaceHost.ActualWidth * scale);
        int dh = (int)Math.Round(target.H - SurfaceHost.ActualHeight * scale);
        if (Math.Abs(dw) <= 2 && Math.Abs(dh) <= 2) { _fitTarget = null; return; }
        // 只挡退化值(0/负),不当第二道下限:比例模式那边连窗口下限都不设,这里更不该偷偷留一个
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            Math.Max(1, AppWindow.Size.Width + dw),
            Math.Max(1, AppWindow.Size.Height + dh)));
        Log.Information("[预览副窗] 对比例第 {Pass} 版: 差 {Dw}x{Dh}px", _fitPass, dw, dh);
    }

    /// <summary>[同主窗口那条校准] 系统按物理像素给标题栏高度,XAML 按 DIP:写死的 48 在非 100% 缩放下
    /// 会和右边那三个按钮差 1~2px。读 TitleBar.Height ÷ RasterizationScale 换成 DIP 设回这一行。</summary>
    private void SyncTitleBarRowToCaptionButtons()
    {
        try
        {
            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 0;
            if (scale <= 0) return;
            double titleBarDip = AppWindow.TitleBar.Height / scale;
            if (titleBarDip < 20) return;                 // 标题栏还没初始化时会给出极小值
            double target = Math.Ceiling(titleBarDip);    // 向上取整,免得内容条矮于按钮区露白
            if (Math.Abs(TitleBarRow.ActualHeight - target) > 1)
                TitleBarRow.Height = new GridLength(target);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[预览副窗] 同步标题栏高度失败");
        }
    }

    // ========== 右栏:属性行 → 画面热更新 ==========

    /// <summary>读这张壁纸的属性行并建进右栏。读取与建行都用属性副窗那一份实现
    /// (WeWallpaperSettings.ReadRows + WallpaperPropertyRowBuilder),所以两栏的行、类型分支、条件显隐一致。</summary>
    private async Task LoadPropsAsync(string folderPath)
    {
        ClearPropsRows();
        int version = _propsVersion;

        string? folder = ResolveItemFolder(folderPath);
        if (folder is null)
        {
            PropsEmptyHint.Visibility = Visibility.Visible;
            Log.Warning("[预览副窗] 属性栏打不开:壁纸目录不存在 {Path}", folderPath);
            return;
        }

        PropsRing.Visibility = Visibility.Visible;
        PropsEmptyHint.Visibility = Visibility.Collapsed;
        int rowCount = 0;
        try
        {
            var rows = await Task.Run(() => WeWallpaperSettings.ReadRows(folder));
            if (version != _propsVersion) return;   // 期间又换了一张,这批行已经不属于当前画面

            // 分批建行:大壁纸(200+ 行)一次全建会把 UI 冻住,一批之间让出一帧(同属性副窗)
            foreach (var chunk in rows.Chunk(10))
            {
                foreach (var row in chunk)
                {
                    PropertyItemsHost.Children.Add(WallpaperPropertyRowBuilder.BuildPropertyRow(row));
                    Track(row);
                }
                rowCount += chunk.Length;
                await Task.Delay(16);
                if (version != _propsVersion) return;
            }

            PropsEmptyHint.Visibility = _tracked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Log.Information("[预览副窗] 属性栏: {Rows} 行 / 可热更 {Watched} 行 Folder={Folder}",
                rowCount, _tracked.Count, folder);

            // 开栏先把当前值整体发一遍:库的默认值取的是 project.json 里的存盘快照,而 WE 播的是
            // config.json 里用户改过的值。不补这一步,右栏显示的和画面在播的会各说各话。
            foreach (var p in _tracked) Queue(p);
            FlushProps();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[预览副窗] 读取壁纸属性失败: {Folder}", folder);
        }
        finally
        {
            if (version == _propsVersion) PropsRing.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>摘掉右栏的行与订阅,并作废正在跑的上一批读取(换壁纸、换模式都走这里)。
    /// 不摘订阅的话,旧壁纸那一栏的编辑会把键值发到新实例上 —— 属性名逐壁纸不同,那是一次纯污染。</summary>
    private void ClearPropsRows()
    {
        _propsVersion++;
        foreach (var p in _tracked) p.PropertyChanged -= OnRowPropertyChanged;
        _tracked.Clear();
        _pendingProps.Clear();
        PropertyItemsHost.Children.Clear();
        PropsRing.Visibility = Visibility.Collapsed;
        PropsEmptyHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>挂上一行(含组内子行)的值改动。组行自己不带值,所以按 IsEditable 筛过再订。</summary>
    private void Track(WallpaperProperty row)
    {
        foreach (var p in row.Children.Prepend(row))
        {
            if (!p.IsEditable) continue;
            p.PropertyChanged += OnRowPropertyChanged;
            _tracked.Add(p);
        }
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 只认值成员:ColorValue 一变还会连带报 ColorBrush/ColorHexText,按名字过一遍才不会发三遍
        if (e.PropertyName is null || !ValueMembers.Contains(e.PropertyName)) return;
        if (sender is not WallpaperProperty p) return;
        Queue(p);
        ScheduleFlush();
    }

    /// <summary>把一行的当前值记成待发项(等定时器合并)。</summary>
    private void Queue(WallpaperProperty p)
    {
        if (TryLiteral(p, out var key, out var literal)) _pendingProps[key] = literal;
    }

    private void FlushProps()
    {
        if (_pendingProps.Count == 0) return;
        var batch = new Dictionary<string, string>(_pendingProps);
        _pendingProps.Clear();
        ScenePreviewHost.ApplyProperties(batch);
    }

    /// <summary>起/续一个一次性合并窗:每次改动都把到期时间往后推,停手后才真发。</summary>
    private void ScheduleFlush()
    {
        if (DispatcherQueue is not { } dq) return;
        var timer = _propTimer ??= dq.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(PropertySendDelayMs);
        timer.IsRepeating = false;
        timer.Stop();
        timer.Tick -= OnFlushTick;
        timer.Tick += OnFlushTick;
        timer.Start();
    }

    private void OnFlushTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        FlushProps();
    }

    /// <summary>行值 → 库认的 JSON 字面量。发不出去的类型一律返回 false(留在这栏里看,不假装生效)。</summary>
    private static bool TryLiteral(WallpaperProperty p, out string key, out string literal)
    {
        key = p.Key;
        literal = "";
        // WE 内置那批(wec_* 等)是 WE 合成器层的滤镜,库的属性面里没有这些键;
        // scenetexture 是磁盘绝对路径,而库取资源只认 httpSource 那个基址 —— 两种发过去都是空响
        if (key.Length == 0 || p.IsWeBuiltin || p.IsUnset) return false;

        switch (p.Type)
        {
            case "bool":
                literal = JsonSerializer.Serialize(p.BoolValue, JsonContext.Default.Boolean);
                return true;
            case "slider":
                literal = JsonSerializer.Serialize(p.SliderValue, JsonContext.Default.Double);
                return true;
            case "combo":
                // 库的 combo 收选项值而不是序号;选项是整数时整数字符串也认(见库的用户属性一节)
                if (p.ComboValue.Length == 0) return false;
                literal = JsonSerializer.Serialize(p.ComboValue, JsonContext.Default.String);
                return true;
            case "color":
                literal = JsonSerializer.Serialize(ColorTriplet(p.ColorValue), JsonContext.Default.String);
                return true;
            case "textinput":
                literal = JsonSerializer.Serialize(p.TextValue, JsonContext.Default.String);
                return true;
            default:
                return false;
        }
    }

    /// <summary>库的 color 口径:三个 0..1 浮点、空格分隔(不是 #RRGGBB,也不是 0..255)。</summary>
    private static string ColorTriplet(Windows.UI.Color c) => $"{Unit(c.R)} {Unit(c.G)} {Unit(c.B)}";

    // 不变文化:小数点是句点。区域设置成逗号的地方,"0,5 0,2 0,8" 会被库当成三个非法数
    private static string Unit(byte v) => (v / 255.0).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>快照里的路径可能是目录,也可能直接指到工程文件(与 ScenePreviewHost.ResolveFolder 同一形态判断)。</summary>
    private static string? ResolveItemFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (File.Exists(trimmed)) trimmed = Path.GetDirectoryName(trimmed) ?? trimmed;
        return Directory.Exists(trimmed) ? trimmed : null;
    }

    /// <summary>把这扇窗推到前面;最小化的先恢复。换了壁纸就要人看得见,不看管道是否连通。</summary>
    private void Wake()
    {
        if (AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized)
            p.Restore();
        Activate();
    }

    private string BuildTitle(string title)
        => string.IsNullOrEmpty(title) ? _titleLabel : $"{_titleLabel} · {title}";

    /// <summary>标题走 LanguageHelper:与另三扇副窗口同一条理由——取不到资源也要有个读得懂的名字。</summary>
    private static string ReadTitleLabel()
    {
        try
        {
            var t = LanguageHelper.GetResource("ScenePreviewWindowTitle.Title");
            if (!string.IsNullOrEmpty(t)) return t;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[预览副窗] 读取窗口标题资源失败,用兜底文字");
        }
        return "场景壁纸预览";
    }

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

    /// <summary>管道写可能在对方不读时阻塞,一律离开 UI 线程。</summary>
    private void Send(PropertyWindowMessage message)
    {
        var channel = _channel;
        if (channel == null) return;
        _ = Task.Run(() => channel.Send(message));
    }
}

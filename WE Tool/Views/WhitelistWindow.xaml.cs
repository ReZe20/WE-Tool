using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Serilog;
using WE_Tool.ViewModels;
using WE_Tool.Helper;
using WE_Tool.Json;
using WE_Tool.Service;
using WinUIEx;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Windowing;
using Windows.System;
using Windows.UI.Core;

namespace WE_Tool.Views;

public sealed partial class WhitelistWindow : WindowEx
{
    /// <summary>本地化取值:无参数直接取,有参数则 string.Format。</summary>
    private static string L(string key, params object[] args)
    {
        string s = LanguageHelper.GetResource(key);
        return args.Length == 0 ? s : string.Format(s, args);
    }

    /// <summary>母进程发来的启动载荷:白名单条目 + 工坊路径 + 主题/日志级别。本进程不读配置与白名单文件。</summary>
    private readonly WhitelistWindowSnapshot _snapshot;
    /// <summary>本进程自己的一份白名单集合:母进程把 cleanup_whitelist.json 的内容灌进载荷,
    /// 这里只用来渲染/增删卡片。文件仍然只有母进程一个写者——本窗口删条目是"报意图",不落盘。</summary>
    private readonly HashSet<string> _whitelist;
    private readonly string _workshopPath;
    private ObservableCollection<CleanupCardViewModel> _cards = new();

    private PropertyWindowChannel? _channel;
    private string _theme = "";

    // [列表键盘可达 2026-09-22,同步残留清理页] 卡片 = Tab/方向键停留点,Ctrl+L 直达,Enter/空格深入一层到卡内控件,
    // Esc 退回卡片。与 Cleanup 页同一套模型,两点差异:
    //   1) 这是独立窗口(WindowEx)不是 Page —— 没有 Frame,也没有 MainWindow 的 RootGrid_KeyDown 分发,
    //      收键挂在窗口自己的根 Grid 上(焦点必然落在它子树内),不需要对外的 HandleShortcutKey 入口;
    //   2) XamlRoot 必须取本窗口的(主窗口 Content 的 XamlRoot 与本窗口无关,读错会恒返回 null)。
    private int _listAnchorIndex = -1;   // 列表里最后停留过的卡下标:Ctrl+L 的落点

    public WhitelistWindow(WhitelistWindowSnapshot snapshot)
    {
        _snapshot = snapshot;
        _whitelist = new HashSet<string>(snapshot.Entries);
        _workshopPath = snapshot.WorkshopPath;
        _theme = snapshot.Theme ?? "";
        InitializeComponent();
        // 自定义标题栏:去系统标题栏,顶部 48px 留空当标题栏(Tall 高度)
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        Title = LanguageHelper.GetResource("WhitelistWindowTitle.Title");
        ApplyTheme();
        CardRepeater.ItemsSource = _cards;
        LoadWhitelistCards();

        Closed += (s, e) =>
        {
            _channel?.Dispose();
            // 本进程只有这一个窗口:关掉就退,别留一个没有窗口的空进程等 Job Object 收尸
            Application.Current.Exit();
        };

        StartChannel();
    }

    /// <summary>接母进程的管道:本进程是 server(管名来自快照)。
    /// 连不上只丢主题/增量加卡的实时同步,窗口本身照常工作。</summary>
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
                Log.Warning("[白名单副窗] 管道未建成,主题与增量加卡不可用");
                return;
            }

            // 本窗口的 DispatcherQueue 必须在 UI 线程上取(Window.DispatcherQueue 是自由线程可读的)
            var queue = DispatcherQueue;
            channel.MessageReceived += message => queue?.TryEnqueue(() => OnLinkMessage(message));
            channel.Broken += () => { };
            _channel = channel;
        });
    }

    /// <summary>母进程推来的设置变化/前置/加卡请求。UI 线程执行。</summary>
    private void OnLinkMessage(PropertyWindowMessage message)
    {
        switch (message.Kind)
        {
            case PropertyWindowLink.KindTheme:
                string newTheme = message.Theme ?? "";
                if (newTheme == _theme) break;
                _theme = newTheme;
                ApplyTheme();
                break;
            case PropertyWindowLink.KindFocus:
                Activate();
                break;
            case PropertyWindowLink.KindAdd:
                if (!string.IsNullOrEmpty(message.EntryId)) AddWhitelistCard(message.EntryId!);
                break;
        }
    }

    /// <summary>主题由母进程经快照+管道给出:独立窗口不继承主窗口根元素的 RequestedTheme,
    /// 而本进程根本没有主窗口可读(主 VM 只在母进程里)。</summary>
    private void ApplyTheme()
    {
        if (Content is not FrameworkElement root) return;
        root.RequestedTheme = _theme switch
        {
            "Dark" => Microsoft.UI.Xaml.ElementTheme.Dark,
            "Light" => Microsoft.UI.Xaml.ElementTheme.Light,
            _ => Microsoft.UI.Xaml.ElementTheme.Default
        };
    }

    /// <summary>把"移出白名单"的意图报给母进程:改集合、写 cleanup_whitelist.json、把壁纸退回清理列表都由它做。</summary>
    private void ReportRemoved(string id)
    {
        var channel = _channel;
        if (channel == null)
        {
            Log.Warning("[白名单副窗] 管道未连通,{Id} 的移除没能上报,主程序仍保留该项", id);
            return;
        }
        // 管道写可能在母进程不读时阻塞,一律离开 UI 线程
        _ = Task.Run(() => channel.Send(new PropertyWindowMessage
        {
            Kind = PropertyWindowLink.KindRemoved,
            EntryId = id,
        }));
    }

    private void LoadWhitelistCards()
    {
        _cards.Clear();
        foreach (var id in _whitelist.OrderBy(x => x))
        {
            var dir = Path.Combine(_workshopPath, id);
            if (!Directory.Exists(dir)) continue;
            var card = MakeCard(dir, id);
            if (card != null) _cards.Add(card);
        }
        UpdateVisibility();
    }


    /// <summary>母进程增量通知:清理页把某 ID 加进了白名单。</summary>
    public void AddWhitelistCard(string id)
    {
        _whitelist.Add(id);
        if (_cards.Any(c => c.FolderId == id)) return; // 已存在
        var dir = Path.Combine(_workshopPath, id);
        if (!Directory.Exists(dir)) return;
        var card = MakeCard(dir, id);
        if (card != null)
        {
            _cards.Add(card); // ObservableCollection 触发动画
            UpdateVisibility();
        }
    }

    /// <summary>本窗口内删除条目:先移除卡片(乐观更新),再把意图报给母进程落盘。</summary>
    private void RemoveCard(string id)
    {
        _whitelist.Remove(id);
        var card = _cards.FirstOrDefault(c => c.FolderId == id);
        if (card != null)
        {
            _cards.Remove(card); // ObservableCollection 触发动画
            UpdateVisibility();
        }
        ReportRemoved(id);
    }

    private void UpdateVisibility()
    {
        bool has = _cards.Count > 0;
        CardScrollView.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (has)
        {
            long total = _cards.Sum(c => c.Files.Sum(f => f.Size));
            SubtitleText.Text = L("WhitelistWindow_Subtitle", _cards.Count, FormatSize(total));
            UpdateCardLayoutMinWidth();
        }
        else
        {
            SubtitleText.Text = "";
        }
    }


    // [全迁 ItemsRepeater] resize 不再全量重排(虚拟化),原淡入淡出遮羞动画删除;
    // SizeChanged 只钳制 UniformGridLayout.MinItemWidth(防除零崩溃 #10539)
    private void CardScrollView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCardLayoutMinWidth();
    }

    private void UpdateCardLayoutMinWidth()
    {
        if (CardUniformLayout is not UniformGridLayout layout) return;
        double viewport = CardScrollView.ActualWidth;
        int desired = 350; // 卡片档位(原 350 分列)
        if (viewport > 0)
        {
            double effective = Math.Max(1, Math.Min(desired, viewport - 8));
            if (Math.Abs(layout.MinItemWidth - effective) > 0.5)
                layout.MinItemWidth = effective;
        }
        else if (layout.MinItemWidth <= 0)
        {
            layout.MinItemWidth = desired;
        }
    }

    private CleanupCardViewModel? MakeCard(string dir, string id)
    {
        bool installed = File.Exists(Path.Combine(dir, "project.json"));
        var std = GetStdFiles(dir);
        var excess = new List<CleanupFileItem>();
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, f);
            var firstSeg = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (firstSeg.Equals("shaders", StringComparison.OrdinalIgnoreCase)
                || firstSeg.Equals("shader", StringComparison.OrdinalIgnoreCase)) continue;
            if (!std.Contains(rel, StringComparer.OrdinalIgnoreCase))
                excess.Add(new CleanupFileItem
                {
                    Name = rel,
                    SizeText = FormatSize(new FileInfo(f).Length),
                    Size = new FileInfo(f).Length,
                    FullPath = f
                });
        }

        if (installed)
        {
            if (excess.Count == 0) return null;
            return new CleanupCardViewModel
            {
                FolderId = id, TypeLabel = L("Cleanup_TypeExcess"), FullPath = dir, IsUnloaded = false,
                StatsText = L("Cleanup_StatsExcess", excess.Count, FormatSize(excess.Sum(f => f.Size))),
                Files = excess
            };
        }
        else
        {
            var files = excess.Count > 0 ? excess : new List<CleanupFileItem> { new() { Name = L("Cleanup_EmptyFolderName"), SizeText = "", FullPath = "" } };
            return new CleanupCardViewModel
            {
                FolderId = id, TypeLabel = L("Cleanup_TypeUnloaded"), FullPath = dir, IsUnloaded = true,
                StatsText = L("Cleanup_StatsUnloaded", files.Count, FormatSize(DirSize(dir))),
                Files = files
            };
        }
    }

    private HashSet<string> GetStdFiles(string dir)
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "project.json", "scene.pkg" };
        var p = Path.Combine(dir, "project.json");
        if (!File.Exists(p)) return s;
        try
        {
            var o = JsonNode.Parse(File.ReadAllText(p)) as JsonObject;
            if (o?["file"]?.GetValue<string>() is string f) s.Add(f);
            if (o?["preview"]?.GetValue<string>() is string pr) s.Add(pr);
        }
        catch { }
        return s;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not CleanupCardViewModel card) return;
        try { Process.Start("explorer.exe", $"\"{card.FullPath}\""); } catch { }
    }

    private void SelectCheckBox_Click(object sender, RoutedEventArgs e)
    {
        UpdateBatchButtons();
    }

    private void UpdateBatchButtons()
    {
        int selected = _cards.Count(c => c.IsSelected);
        BatchRemoveButton.IsEnabled = selected > 0;
        BatchRemoveButton.Label = selected > 0 ? L("WhitelistWindow_BatchRemoveCount", selected) : L("WhitelistWindow_BatchRemove.Label");
    }

    private void BatchRemove_Click(object sender, RoutedEventArgs e)
    {
    AnimatedIconPlayer.PlayOnce(sender);   // [删除图标动画 2026-09]
        foreach (var card in _cards.Where(c => c.IsSelected).ToList())
            RemoveCard(card.FolderId);
        UpdateBatchButtons();
    }

    private void RemoveFromWhitelist_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not CleanupCardViewModel card) return;
        RemoveCard(card.FolderId);
    }

    // ---------- 列表键盘可达(2026-09-22,同步残留清理页) ----------

    /// <summary>容器就绪即把卡片设成 Tab/方向键停留点并给朗读名(与 Cleanup 页同法)。</summary>
    private void CardRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement card) return;

        // item 认定用 args.Index 兜底:ElementPrepared 时 DataContext 可能还没推送(壁纸备份页就因此
        // 卡片从来没成为过 Tab 停留点),而本窗口的卡片是构造时就灌进 ItemsSource 的,更吃这个时机。
        CleanupCardViewModel? vm = card.DataContext as CleanupCardViewModel
            ?? (args.Index >= 0 && args.Index < _cards.Count ? _cards[args.Index] : null);
        if (vm is null)
        {
            Log.Warning("[白名单] 卡片第 {Index} 项取不到数据项,焦点停留点与朗读名均未设置", args.Index);
            return;
        }

        card.IsTabStop = true;
        card.UseSystemFocusVisuals = true;
        AutomationProperties.SetName(card, vm.FolderId);
        // 卡片根已念 FolderId,卡内那行 FolderId 文字设为 Raw,否则讲述人停在卡上按方向键会读两遍
        if (card.FindName("CardFolderIdText") is TextBlock folderIdText)
            AutomationProperties.SetAccessibilityView(folderIdText, AccessibilityView.Raw);
        else
            Log.Warning("[白名单] 未取到卡片标题节点 CardFolderIdText,朗读去重未生效");
        // 勾选框没有文字内容(纯框),不给名字讲述人只会念"复选框";用同一个 FolderId 当它的朗读名
        if (card.FindName("CardSelectBox") is CheckBox selectBox)
            AutomationProperties.SetName(selectBox, vm.FolderId);

        card.GotFocus -= WhitelistCard_GotFocus;   // 幂等:容器回收复用会重复走到这里
        card.GotFocus += WhitelistCard_GotFocus;
    }

    /// <summary>记住"最后停留过的卡":Ctrl+L 再进列表时回到这里,而不是回列表头。</summary>
    private void WhitelistCard_GotFocus(object sender, RoutedEventArgs e)
    {
        int idx = CardIndex(sender as UIElement);
        if (idx >= 0) _listAnchorIndex = idx;
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e) => KeyDown_Core(e);

    private void KeyDown_Core(KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.L or VirtualKey.Enter or VirtualKey.Space or VirtualKey.Escape)) return;
        // 本窗口的 XamlRoot:无参 FocusManager.GetFocusedElement() 在 WinUI 3 桌面恒返回 null,
        // 而拿错成主窗口的 XamlRoot 也一样读不到本窗口的焦点
        var xamlRoot = (Content as FrameworkElement)?.XamlRoot;
        if (xamlRoot is null) return;
        var focused = FocusManager.GetFocusedElement(xamlRoot) as FrameworkElement;
        bool ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down)
            == CoreVirtualKeyStates.Down;

        if (e.Key == VirtualKey.L && ctrl)
        {
            if (FocusWhitelistList()) e.Handled = true;
            return;
        }

        if (e.Key is VirtualKey.Enter or VirtualKey.Space && FindOwnCard(focused) is { } card)
        {
            if (EnterCardControls(card)) e.Handled = true;
            return;
        }

        // Esc 只在"焦点停在某张卡的控件上"时接管(退回卡片);别处的 Esc 原样交出去
        // ButtonBase 覆盖本页会用到 Enter 的控件:CheckBox(继承 ToggleButton)与 Button
        if (e.Key == VirtualKey.Escape && focused is ButtonBase && FindOwnCard(focused) is { } ownCard)
        {
            if (ownCard.Focus(FocusState.Keyboard)) e.Handled = true;
            return;
        }
    }

    /// <summary>焦点元素是不是"某张卡片的容器根本身":是则返回下标,否则 -1。
    /// 只认容器根本身 —— 卡内控件的 DataContext 与卡片同一个,按 DataContext 反查会把控件误判成卡片。</summary>
    private int CardIndex(UIElement? el)
    {
        if (el is null) return -1;
        try
        {
            // GetElementIndex 对"容器后代"的语义文档没承诺(可能给祖先下标、可能抛),所以要再比对身份确认它给的就是这一格
            int idx = CardRepeater.GetElementIndex(el);
            if (idx >= 0 && ReferenceEquals(CardRepeater.TryGetElement(idx), el)) return idx;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[白名单] 反查卡片下标异常");
        }
        for (int i = 0; i < _cards.Count; i++)
            if (ReferenceEquals(CardRepeater.TryGetElement(i), el)) return i;
        return -1;
    }

    /// <summary>从焦点元素上溯,找它所属的那张卡片(卡内控件→卡片);不在任何卡片内则返回 null。</summary>
    private FrameworkElement? FindOwnCard(FrameworkElement? from)
    {
        DependencyObject? cur = from;
        for (int hops = 0; cur != null && hops < 8; hops++)
        {
            if (cur is FrameworkElement fe && CardIndex(fe) >= 0) return fe;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return null;
    }

    /// <summary>Enter/空格的"深入一层":把焦点交给这张卡的第一个控件(勾选框),之后 Tab 在勾选框与两按钮间走。</summary>
    private bool EnterCardControls(FrameworkElement card)
    {
        ButtonBase? first = card.FindName("CardSelectBox") as ButtonBase
            ?? card.FindName("CardOpenFolderButton") as ButtonBase;
        if (first is null)
        {
            Log.Warning("[白名单] 进入卡内控件失败: 第 {Index} 张卡里找不到 CardSelectBox/CardOpenFolderButton", CardIndex(card));
            return false;
        }
        return first.Focus(FocusState.Keyboard);
    }

    /// <summary>Ctrl+L 的落点:优先回到上次停留过的卡,其次第一张已实化的卡;都没有就写日志,不静默失败。</summary>
    private bool FocusWhitelistList()
    {
        if (_listAnchorIndex >= 0 && _listAnchorIndex < _cards.Count
            && CardRepeater.TryGetElement(_listAnchorIndex) is FrameworkElement anchor
            && anchor.Focus(FocusState.Keyboard)) return true;
        if (FocusFirstRealizedCard()) return true;

        Log.Warning("[白名单] Ctrl+L 未找到可聚焦的卡片(列表为空或容器全部未实化)");
        return false;
    }

    private bool FocusFirstRealizedCard()
    {
        for (int i = 0; i < _cards.Count; i++)
        {
            if (CardRepeater.TryGetElement(i) is FrameworkElement card && card.Focus(FocusState.Keyboard))
            {
                _listAnchorIndex = i;
                return true;
            }
        }
        return false;
    }

    private static long DirSize(string path)
    {
        try { return Directory.GetFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }

    private static string FormatSize(long b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1048576) return $"{b / 1024.0:F1} KB";
        if (b < 1073741824) return $"{b / 1048576.0:F1} MB";
        return $"{b / 1073741824.0:F2} GB";
    }
}

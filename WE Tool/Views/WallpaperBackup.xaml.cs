using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;
using WE_Tool.Controls;
using WE_Tool.Helper;
using WE_Tool.Service;
using WE_Tool.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace WE_Tool.Views;

public sealed partial class WallpaperBackup : Page
{
    private string WorkshopPath => ((App)Application.Current).ViewModel.PathManagementVM.WorkshopPath;
    private string BackupRoot => BackupService.GetBackupRoot(WorkshopPath);

    public ObservableCollection<BackupItemViewModel> BackupItems { get; } = new();
    private bool _initialScanDone;
    private readonly AutoBackupServiceManager _serviceManager = new();
    private bool _isApplyingUi;   // 避免 UI 初始化时的 Checked 事件触发保存
    private bool _isBackingUp;    // 「立即备份」进行中(防重入)
    private CancellationTokenSource? _saveDebounceCts; // 配置变更防抖:500ms 只写最后一次

    // [a11y 2026-09,同步 Papers] 讲述人支持:备份卡片是 Tab 停留点,并能被读出名字。
    // ElementPrepared 里给卡片根 Grid 设 IsTabStop + UseSystemFocusVisuals + 朗读名(=标题),
    // 并把卡片里的标题 TextBlock 归到 Raw 视图(避免同一条信息被念两遍)。
    // 观感:Tab 从上方工具栏进入列表时停在第一张卡;方向键在卡片之间移动焦点
    // (ItemsRepeater 官方文档写明它的 XYFocusKeyboardNavigation 默认就是 Enabled,不用另写代码)。
    // 范围:只做讲述人这一件事——不接管 Ctrl/Shift 等快捷键,也不做"焦点即选中"(本页没有选中模型)。

    // [列表键盘可达 2026-09-21,同步 Papers] 进列表的快捷键 + 本页特有的一层"深入卡片"焦点模型:
    //   Ctrl+L            → 焦点从工具栏/自动备份面板/外壳导航直接落到备份卡片(优先回到上次停留那张);
    //   Enter / 空格      → 焦点在卡片本身时,把焦点交给这张卡里的两个按钮(顺序同 Tab 序:删除、打开备份目录);
    //   Esc               → 焦点在卡内按钮上时退回该卡片。
    // 分层是天然的:按钮自己会消费 Enter/空格(那是"执行这个按钮"),页面只可能收到"焦点在卡片上"时按下的这两个键。
    // 本页没有选中模型(卡片不对应"选中项"),所以不做 Papers 那套"焦点即选中"。
    private int _listAnchorIndex = -1;   // 列表里最后停留过的卡下标:Ctrl+L 的落点

    /// <summary>自动备份配置(页面持有副本,变化时回写 config.json)。</summary>
    private Models.AutoBackupConfig? _autoCfg;


    public WallpaperBackup()
    {
        InitializeComponent();
        BackupRepeater.ItemsSource = BackupItems;
        Loaded += (s, e) => _ = InitializeAutoBackupSettingsAsync();
        UpdateSortLabel();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!_initialScanDone)
        {
            _initialScanDone = true;
            ScanButton_Click(null, null);
        }
        // 页面缓存切回:容器复用不触发 ContainerContentChanging → 延迟一帧重启可见 GIF 播放(与 Papers 一致)
        DispatcherQueue.TryEnqueue(RestartVisibleGifPlayback);
    }

    /// <summary>遍历可见容器重启 GIF 播放(页面缓存切回时;容器未就绪/无项时无害)。
    /// [全迁 ItemsRepeater] 改为可视树遍历:找可见 SkiaGifView,用其 DataContext 重启。</summary>
    private void RestartVisibleGifPlayback()
    {
        RestartVisibleSkiaGifs(this);
    }

    private static void RestartVisibleSkiaGifs(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is SkiaGifView skia && skia.Visibility == Visibility.Visible)
            {
                if (skia.DataContext is BackupItemViewModel vm
                    && !string.IsNullOrEmpty(vm.PreviewPath)
                    && vm.PreviewPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                {
                    skia.Start(vm.PreviewPath);
                }
            }
            else
            {
                RestartVisibleSkiaGifs(child);
            }
        }
    }

    private void ScanButton_Click(object? sender, RoutedEventArgs? e) => _ = LoadBackupsAsync();

    private async Task LoadBackupsAsync()
    {
        EmptyState.Visibility = Visibility.Collapsed;
        ScanProgress.IsActive = true;
        BackupScrollView.Visibility = Visibility.Collapsed;

        // 代次号:本次扫描的代;期间若有删除等变更,回填时按代丢弃旧结果
        int gen = ++_scanGeneration;

        List<BackupItemViewModel>? collected = null;
        long totalBytes = 0;
        try
        {
            (collected, totalBytes) = await Task.Run(CollectBackups);
        }
        catch (Exception ex)
        {
            // 扫描与删除并发时后台可能撞上已删除目录/文件;兜底复位 UI,不留永久转圈
            Log.Error(ex, "[备份] 扫描备份失败");
            if (gen == _scanGeneration)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    ScanProgress.IsActive = false;
                    EmptyState.Visibility = Visibility.Visible;
                    EmptyStateText.Text = L("BackupPage_ScanFailed.Text");
                    BackupScrollView.Visibility = Visibility.Collapsed;
                });
            }
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (gen != _scanGeneration) return; // 期间有删除等变更,旧结果作废

            _allItems.Clear();
            _allItems.AddRange(collected!);

            int missingCount = _allItems.Count(it => it.IsSourceMissing);
            SummaryText.Text = _allItems.Count > 0
                ? missingCount > 0
                    ? $"共 {_allItems.Count} 个备份 · {FormatSize(totalBytes)} · {missingCount} 个源已删除"
                    : $"共 {_allItems.Count} 个备份 · {FormatSize(totalBytes)}"
                : "";
            if (_allItems.Count == 0) ShowEmpty();

            // 应用筛选+排序后刷新可见集合
            ApplyFilterAndSort();

            ScanProgress.IsActive = false;
            BackupScrollView.Visibility = BackupItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            // 强制刷新一次卡片宽度(Visibility 变化触发的 SizeChanged 时序不可靠)
            UpdateBackupLayoutMinWidth();
        });
    }

    /// <summary>后台收集全部备份(同步执行:标题/预览/大小/备份时间/源删除标记)。目录不存在返回空列表。</summary>
    private (List<BackupItemViewModel> Collected, long TotalBytes) CollectBackups()
    {
        var collected = new List<BackupItemViewModel>();
        long totalBytes = 0;
        if (!Directory.Exists(BackupRoot)) return (collected, 0);

        foreach (var dir in Directory.GetDirectories(BackupRoot))
        {
            var id = Path.GetFileName(dir);
            var marker = Path.Combine(dir, BackupService.MarkerFileName);
            if (!File.Exists(marker)) continue; // 未完成备份

            // 读取标题：优先从 project.json，否则用 ID
            string title = id;
            string projectPath = Path.Combine(dir, "project.json");
            if (File.Exists(projectPath))
            {
                try
                {
                    var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(projectPath));
                    if (json.RootElement.TryGetProperty("title", out var titleProp))
                        title = titleProp.GetString() ?? id;
                }
                catch { /* 忽略解析失败 */ }
            }

            // 读取预览图路径;找不到用占位图(避免 UriSource 绑定 null/空串抛 Uri 转换异常,与 Papers 一致)
            string? previewPath = null;
            foreach (var ext in new[] { "preview.png", "preview.jpg", "preview.gif" })
            {
                var p = Path.Combine(dir, ext);
                if (File.Exists(p)) { previewPath = p; break; }
            }
            if (string.IsNullOrEmpty(previewPath))
                previewPath = "ms-appx:///Assets/NoPreview.png";

            // 计算总大小
            long totalSize = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                totalSize += new FileInfo(f).Length;
            totalBytes += totalSize;

            // 读取备份时间
            string backupTimeText = "";
            DateTime? backupTime = null;
            try
            {
                var lines = File.ReadAllLines(marker);
                var createdLine = lines.FirstOrDefault(l => l.StartsWith("created="));
                if (createdLine != null)
                {
                    backupTimeText = createdLine.Substring("created=".Length).Trim();
                    if (DateTime.TryParse(backupTimeText, out var parsed))
                        backupTime = parsed;
                }
            }
            catch { }

            // 源文件是否已删除:content/431960/<id> 目录不存在 = 取消订阅/下架,仅剩备份
            bool sourceMissing = !Directory.Exists(Path.Combine(WorkshopPath, id));

            collected.Add(new BackupItemViewModel
            {
                WorkshopId = id,
                Title = title,
                PreviewPath = previewPath,
                SizeText = FormatSize(totalSize),
                SizeBytes = totalSize,
                BackupTimeText = backupTimeText,
                BackupTime = backupTime,
                FullPath = dir,
                IsSourceMissing = sourceMissing,
            });
        }
        return (collected, totalBytes);
    }

    private void ShowEmpty()
    {
        ScanProgress.IsActive = false;
        EmptyState.Visibility = Visibility.Visible;
        EmptyStateText.Text = L("BackupPage_Empty.Text");
        BackupScrollView.Visibility = Visibility.Collapsed;
    }

    private async void DeleteBackup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not BackupItemViewModel item) return;
        AnimatedIconPlayer.PlayOnce(sender);   // [删除图标动画 2026-09] 卡片上的删除按钮
        if (string.IsNullOrEmpty(item.FullPath) || !Directory.Exists(item.FullPath)) return;

        bool confirmed = await DialogHelper.ShowConfirmDialogAsync("删除备份",
            $"确定要删除「{item.Title}」的备份吗？\n\n删除后无法恢复。",
            "删除", "取消");
        if (!confirmed) return;

        try
        {
            Directory.Delete(item.FullPath, true);
            _scanGeneration++; // 作废在途扫描,防止其旧结果回填把刚删的卡片变回来
            _allItems.Remove(item);

            // 增量移除:播单项移除+补位动画(Remove 通知,与 Papers 的删除同路)。
            // 旧做法走 ApplyFilterAndSort 的 Clear+Add 全量重建(Reset=整页刷新,无单项动画)。
            if (BackupItems.Remove(item))
            {
                // 可见列表被删空但全量还有项 → 筛选空态(按 ApplyFilterAndSort 语义补齐;全空由下方尾部 ShowEmpty 兜底)
                if (BackupItems.Count == 0 && _allItems.Count > 0)
                {
                    BackupScrollView.Visibility = Visibility.Collapsed;
                    EmptyState.Visibility = Visibility.Visible;
                    EmptyStateText.Text = L("BackupPage_FilterEmpty.Text");
                }
            }
            else
            {
                // 项被筛掉不在可见集合,无动画可播;走旧路径刷新
                ApplyFilterAndSort();
            }

            // 内存求和(SizeBytes 扫描时已存),不再重扫全部备份目录
            long remaining = _allItems.Sum(it => it.SizeBytes);
            int missingCount = _allItems.Count(it => it.IsSourceMissing);
            SummaryText.Text = _allItems.Count > 0
                ? missingCount > 0
                    ? $"共 {_allItems.Count} 个备份 · {FormatSize(remaining)} · {missingCount} 个源已删除"
                    : $"共 {_allItems.Count} 个备份 · {FormatSize(remaining)}"
                : "";
            if (_allItems.Count == 0) ShowEmpty();
        }
        catch (Exception ex)
        {
            await DialogHelper.ShowMessageAsync("删除失败", ex.Message);
        }
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not BackupItemViewModel item) return;
        if (string.IsNullOrEmpty(item.FullPath) || !Directory.Exists(item.FullPath)) return;
        try
        {
            Process.Start("explorer.exe", $"\"{item.FullPath}\"");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开备份目录失败: {Path}", item.FullPath);
        }
    }

    // 弹层(菜单/Flyout)不自动继承主窗口运行时主题,打开时显式应用(公共逻辑见 App.ApplyFlyoutTheme)
    private void FlyoutThemeRefresh_Opened(object sender, object e) => App.ApplyFlyoutTheme(sender, e);

    private async void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        if (_allItems.Count == 0) return;
        AnimatedIconPlayer.PlayOnce(sender);   // [删除图标动画 2026-09]

        bool confirmed = await DialogHelper.ShowConfirmDialogAsync("删除全部备份",
            $"确定要删除全部 {_allItems.Count} 个备份吗？\n\n删除后无法恢复。",
            "全部删除", "取消");
        if (!confirmed) return;

        int success = 0, failed = 0;
        foreach (var item in _allItems.ToList())
        {
            try
            {
                if (Directory.Exists(item.FullPath))
                {
                    Directory.Delete(item.FullPath, true);
                    success++;
                }
            }
            catch { failed++; }
        }

        _scanGeneration++; // 作废在途扫描
        _allItems.Clear();
        ApplyFilterAndSort();
        SummaryText.Text = "";
        ShowEmpty();
        await DialogHelper.ShowMessageAsync("删除完成",
            failed > 0
                ? $"成功删除 {success} 个备份,{failed} 个删除失败(占用或权限)。"
                : $"成功删除 {success} 个备份。");
    }

    // [全迁 ItemsRepeater] 元素容器就绪:Skia GIF 切换(替代 GridView ContainerContentChanging);
    // GIF 启动延迟到下一帧(避免滚动时同步解码大量 GIF 卡死 UI 线程)
    private void BackupRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement content) return;

        // [列表键盘可达 2026-09-22] item 认定改成 Papers 同法(DataContext 优先、回退 args.Index):
        // ElementPrepared 时 DataContext 可能还没推送(见 Papers.xaml.cs:680 的同一处教训),原先这道
        // `if (content.DataContext is not BackupItemViewModel) return;` 就发生在设置 IsTabStop 之前
        // → 卡片从来没成为 Tab 停留点,Ctrl+L/Tab 只能落到卡里那两个按钮上(日志里"备份卡片获得焦点"零条读数)。
        BackupItemViewModel? vm = content.DataContext as BackupItemViewModel
            ?? (args.Index >= 0 && args.Index < BackupItems.Count ? BackupItems[args.Index] : null);

        // resw 附加属性经 x:Uid 在 WinUI3 不生效(已知限制),tooltip 需代码显式设置
        // 两个图标按钮只看图标看不出语义,悬浮时给出对应提示(见 Papers.xaml.cs 同法)
        // [列表键盘可达 2026-09] 同一份文案同时用作朗读名:Enter/空格 会把焦点送进这两个按钮,
        // 而纯图标按钮没有文本,讲述人停在上面只会念"按钮" —— 键盘到达后必须听得懂到达了什么。
        if (content.FindName("CardDeleteButton") is Button cardDelBtn)
        {
            var tip = L("BackupPage_CardDelete.ToolTipService.ToolTip");
            ToolTipService.SetToolTip(cardDelBtn, tip);
            AutomationProperties.SetName(cardDelBtn, tip);
        }
        if (content.FindName("CardOpenFolderButton") is Button cardOpenBtn)
        {
            var tip = L("BackupPage_CardOpenFolder.ToolTipService.ToolTip");
            ToolTipService.SetToolTip(cardOpenBtn, tip);
            AutomationProperties.SetName(cardOpenBtn, tip);
        }

        if (vm is null)
        {
            // 不静默跳过:焦点停留点与 GIF 预览都依赖数据项,拿不到就是页面列表与 ItemsSource 对不上
            Log.Warning("[备份列表] 卡片第 {Index} 项取不到数据项,焦点停留点与 GIF 预览均未设置", args.Index);
            return;
        }

        // [a11y 2026-09] 让备份卡片可被 Tab 聚焦,并由讲述人读出标题
        content.IsTabStop = true;               // WinUI3 里 IsTabStop 在 UIElement 上,非 Control 的 Grid 也能进 Tab 序
        content.UseSystemFocusVisuals = true;   // 让系统画焦点框
        // 朗读名用壁纸标题(数据,非文案),不走 resw
        AutomationProperties.SetName(content, string.IsNullOrEmpty(vm.Title) ? "(无标题)" : vm.Title);
        // 卡片根已带朗读名(=标题),卡片里的标题 TextBlock 仍是独立可读节点:讲述人停在卡片上按方向键会把它再念一遍
        // → 一项读两次。官方文档原话就是"composed UI 会引入 duplicate 节点,用 AccessibilityView 归置",
        // 故把这条文字设为 Raw(只留在 raw 视图,不进讲述人主要遍历的 control/content 视图)。
        // 只动 UIA 树:渲染/布局/点击/悬停/tooltip 都不受影响;其余三行补充信息(工坊 ID/大小/备份时间)保持可读。
        if (content.FindName("ItemTitleText") is TextBlock cardTitleText)
            AutomationProperties.SetAccessibilityView(cardTitleText, AccessibilityView.Raw);
        else
            Log.Warning("[备份列表] 未取到卡片标题节点 ItemTitleText,朗读去重未生效");
        content.GotFocus -= BackupCard_GotFocus;   // 幂等:容器回收复用会重复走到这里,先减后加避免订阅叠加
        content.GotFocus += BackupCard_GotFocus;

        var img = content.FindName("PreviewImage") as Image;
        var skia = content.FindName("PreviewSkiaGif") as SkiaGifView;
        if (img == null || skia == null) return;

        bool isGif = !string.IsNullOrEmpty(vm.PreviewPath)
            && vm.PreviewPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        if (isGif)
        {
            skia.Visibility = Visibility.Visible;
            img.Visibility = Visibility.Collapsed;
            var path = vm.PreviewPath;
            DispatcherQueue.TryEnqueue(() =>
            {
                // 容器可能已被回收(Unloaded 会 Stop),重入时 IsPlaying=false 且 CurrentPath 已清 → 安全
                if (skia.Visibility == Visibility.Visible && !string.IsNullOrEmpty(path))
                    skia.Start(path);
            });
        }
        else
        {
            skia.Stop();
            skia.Visibility = Visibility.Collapsed;
            img.Visibility = Visibility.Visible;
        }
    }

    /// <summary>记住"最后停留过的卡":Ctrl+L 再进列表时回到这里,而不是回列表头。</summary>
    private void BackupCard_GotFocus(object sender, RoutedEventArgs e)
    {
        int idx = CardIndex(sender as UIElement);
        if (idx >= 0) _listAnchorIndex = idx;
    }

    // ===================== 列表键盘可达(2026-09-21) =====================
    // 与 Papers/组件页同一套进入方式(Ctrl+L + 卡片是 Tab 停留点),但本页没有选中模型 —— 卡片里没有勾选框,
    // 动作全在卡内那两个按钮上。所以本页的"选中项操作"用一层深入的焦点模型来表达:
    //   卡片(读标题/ID/大小/时间) --Enter 或 空格--> 卡内按钮(删除备份 / 打开备份目录) --Esc--> 卡片。
    // 分层靠事件消费顺序天然成立:按钮会自己消费 Enter/空格并标记已处理,那一下就是"执行按钮";
    // 只有卡片持有焦点时这两个键才会冒泡到页面,由页面把焦点送进按钮里。
    private void Page_KeyDown(object sender, KeyRoutedEventArgs e) => Page_KeyDown_Core(e);

    /// <summary>供 MainWindow 在焦点不在本页子树内(例如停在外壳导航栏)时分发快捷键,与 Papers 同法。</summary>
    public void HandleShortcutKey(KeyRoutedEventArgs e) => Page_KeyDown_Core(e);

    private void Page_KeyDown_Core(KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.L or VirtualKey.Enter or VirtualKey.Space or VirtualKey.Escape)) return;
        // 读焦点必须用带 XamlRoot 的重载:无参版本在 WinUI 3 桌面恒返回 null(Papers 那边实测过)
        var focused = FocusManager.GetFocusedElement(XamlRoot) as FrameworkElement;
        bool ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down)
            == CoreVirtualKeyStates.Down;

        if (e.Key == VirtualKey.L && ctrl)
        {
            if (FocusBackupList()) e.Handled = true;
            return;
        }

        if (e.Key is VirtualKey.Enter or VirtualKey.Space && FindOwnCard(focused) is { } card)
        {
            if (EnterCardButtons(card)) e.Handled = true;
            return;
        }

        // Esc 只在"焦点停在某张卡里的按钮上"时接管(退回卡片);别处的 Esc 原样交出去(关弹层/后退)
        if (e.Key == VirtualKey.Escape && focused is Button && FindOwnCard(focused) is { } ownCard)
        {
            if (ownCard.Focus(FocusState.Keyboard)) e.Handled = true;
            return;
        }
    }

    /// <summary>焦点元素是不是"某张卡片的容器根本身":是则返回下标,否则 -1。
    /// 只认容器根本身 —— 卡内按钮的 DataContext 与卡片同一个(按钮从卡片继承),凡按 DataContext 反查都会把
    /// 按钮误判成卡片,那样 Esc 就"退回"到自己身上、退不出去。</summary>
    private int CardIndex(UIElement? el)
    {
        if (el is null) return -1;
        try
        {
            // GetElementIndex 对"容器后代"的语义文档没承诺(可能给祖先下标、可能抛),所以要再比对身份确认它给的就是这一格
            int idx = BackupRepeater.GetElementIndex(el);
            if (idx >= 0 && ReferenceEquals(BackupRepeater.TryGetElement(idx), el)) return idx;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[备份列表] 反查卡片下标异常");
        }
        // 兜底:实化容器逐个比身份(未实化的下标 TryGetElement 直接给 null,很便宜)
        for (int i = 0; i < BackupItems.Count; i++)
            if (ReferenceEquals(BackupRepeater.TryGetElement(i), el)) return i;
        return -1;
    }

    /// <summary>从焦点元素上溯,找它所属的那张卡片(按钮→卡片);不在任何卡片内则返回 null。</summary>
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

    /// <summary>Enter/空格的"深入一层":把焦点交给这张卡的第一个按钮(删除备份),之后 Tab/Shift+Tab 在两按钮间走。</summary>
    private bool EnterCardButtons(FrameworkElement card)
    {
        // 顺序按 XAML 里的 Tab 序:删除备份在前、打开备份目录在后
        if ((card.FindName("CardDeleteButton") as Button ?? card.FindName("CardOpenFolderButton") as Button) is not Button first)
        {
            Log.Warning("[备份列表] 进入卡片按钮失败: 第 {Index} 张卡里找不到 CardDeleteButton/CardOpenFolderButton", CardIndex(card));
            return false;
        }
        return first.Focus(FocusState.Keyboard);
    }

    /// <summary>Ctrl+L 的落点:优先回到上次停留过的卡,其次第一张已实化的卡;都没有就写日志,不静默失败。</summary>
    private bool FocusBackupList()
    {
        if (_listAnchorIndex >= 0 && _listAnchorIndex < BackupItems.Count
            && BackupRepeater.TryGetElement(_listAnchorIndex) is FrameworkElement anchor
            && anchor.Focus(FocusState.Keyboard)) return true;
        if (FocusFirstRealizedCard()) return true;

        Log.Warning("[备份列表] Ctrl+L 未找到可聚焦的备份卡片(列表为空或容器全部未实化)");
        return false;
    }

    private bool FocusFirstRealizedCard()
    {
        for (int i = 0; i < BackupItems.Count; i++)
        {
            if (BackupRepeater.TryGetElement(i) is FrameworkElement card && card.Focus(FocusState.Keyboard))
            {
                _listAnchorIndex = i;
                return true;
            }
        }
        return false;
    }

    // 元素移出(回收/滚动走远):停 GIF
    private void BackupRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is FrameworkElement content
            && content.FindName("PreviewSkiaGif") is SkiaGifView skia)
            skia.Stop();
    }

    /// <summary>ScrollView 尺寸变化:钳制 UniformGridLayout.MinItemWidth(防除零崩溃)。</summary>
    private void BackupScrollView_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateBackupLayoutMinWidth();

    private void UpdateBackupLayoutMinWidth()
    {
        if (BackupUniformLayout is not UniformGridLayout layout) return;
        double viewport = BackupScrollView.ActualWidth;
        int desired = 320;
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

    // ====================== 排序与筛选 ======================

    /// <summary>排序方式:0名称 1备份时间 2大小。</summary>
    private int _sortOrder = 1; // 默认按备份时间
    /// <summary>true=降序(时间/大小默认最新/最大在前;名称默认 A→Z 升序)。</summary>
    private bool _sortDescending;

    /// <summary>源状态筛选:null=不过滤(两框同态);true=仅源已删除;false=仅源未删除。</summary>
    private bool? _missingFilter;

    /// <summary>全部备份(筛选前的完整数据)。</summary>
    private readonly List<BackupItemViewModel> _allItems = new();

    /// <summary>扫描代次号:删除等变更递增,回填时旧代结果直接丢弃,防止旧扫描覆盖删除后的状态。</summary>
    private int _scanGeneration;

    /// <summary>应用筛选+排序并刷新可见集合。</summary>
    private void ApplyFilterAndSort()
    {
        if (_allItems.Count == 0)
        {
            BackupItems.Clear();
            return;
        }

        IEnumerable<BackupItemViewModel> visible = _allItems;
        if (_missingFilter is bool f)
            visible = visible.Where(it => it.IsSourceMissing == f);

        List<BackupItemViewModel> sorted = _sortOrder switch
        {
            0 => _sortDescending
                ? visible.OrderByDescending(it => it.Title, StringComparer.CurrentCultureIgnoreCase).ToList()
                : visible.OrderBy(it => it.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
            1 => _sortDescending
                ? visible.OrderByDescending(it => it.BackupTime ?? DateTime.MinValue).ToList()
                : visible.OrderBy(it => it.BackupTime ?? DateTime.MinValue).ToList(),
            2 => _sortDescending
                ? visible.OrderByDescending(it => it.SizeBytes).ToList()
                : visible.OrderBy(it => it.SizeBytes).ToList(),
            _ => visible.ToList(),
        };

        BackupItems.Clear();
        foreach (var it in sorted) BackupItems.Add(it);

        // 可见集合为空时:全量非空→筛选无结果提示;全量空→空状态
        if (BackupItems.Count == 0)
        {
            if (_allItems.Count > 0)
            {
                BackupScrollView.Visibility = Visibility.Collapsed;
                EmptyState.Visibility = Visibility.Visible;
                EmptyStateText.Text = L("BackupPage_FilterEmpty.Text");
            }
            else
            {
                ShowEmpty();
            }
        }
        else
        {
            BackupScrollView.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>排序单选变化。</summary>
    private void SortMenu_ItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item || item.Tag is not string tag) return;
        _sortOrder = tag switch
        {
            "name" => 0,
            "time" => 1,
            "size" => 2,
            _ => _sortOrder,
        };
        UpdateSortLabel();
        ApplyFilterAndSort();
    }

    /// <summary>源状态筛选复选项:两框同态→不过滤;只勾一个→按该状态过滤。</summary>
    private void FilterMenu_ItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem) return;
        bool m = FilterMissingItem.IsChecked;
        bool x = FilterExistItem.IsChecked;
        _missingFilter = m == x ? null : m;
        ApplyFilterAndSort();
    }

    private void SortDescendingItem_Click(object sender, RoutedEventArgs e)
    {
        _sortDescending = SortDescendingItem.IsChecked;
        UpdateSortLabel();
        ApplyFilterAndSort();
    }

    /// <summary>打开菜单前同步各控件状态。</summary>
    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        SortByNameItem.IsChecked = _sortOrder == 0;
        SortByTimeItem.IsChecked = _sortOrder == 1;
        SortBySizeItem.IsChecked = _sortOrder == 2;
        SortDescendingItem.IsChecked = _sortDescending;
        FilterMissingItem.IsChecked = _missingFilter == true;
        FilterExistItem.IsChecked = _missingFilter == false;
    }

    /// <summary>按钮文字显示当前排序方式(如"排序:备份时间")。</summary>
    private void UpdateSortLabel()
    {
        string name = _sortOrder switch
        {
            0 => L("SortByName.Text"),
            1 => L("BackupPage_SortByTime.Text"),
            2 => L("SortByFileSize.Text"),
            _ => "",
        };
        SortLabelText.Text = $"{L("Toolbar_Sort.ToolTipService.ToolTip")}: {name}";
    }

    private static string L(string key, params object[] args)
    {
        string s = LanguageHelper.GetResource(key);
        return args.Length == 0 ? s : string.Format(s, args);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }

    // ====================== 自动备份设置区 ======================

    private async Task InitializeAutoBackupSettingsAsync()
    {
        try
        {
            var settings = await new ConfigService().LoadAsync();
            _autoCfg = settings.AutoBackup ?? new Models.AutoBackupConfig();
            _isApplyingUi = true;

            ModeOffRadio.IsChecked = !_autoCfg.Enabled;
            ModeServiceRadio.IsChecked = _autoCfg.Enabled && _autoCfg.ServiceEnabled;
            ModeOnStartupRadio.IsChecked = _autoCfg.Enabled && !_autoCfg.ServiceEnabled;
            // 仅"后台服务"模式显示服务管理面板
            ServicePanel.Visibility = ModeServiceRadio.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;

            TypeSceneCheck.IsChecked = _autoCfg.TypeScene;
            TypeVideoCheck.IsChecked = _autoCfg.TypeVideo;
            TypeWebCheck.IsChecked = _autoCfg.TypeWeb;
            TypeAppCheck.IsChecked = _autoCfg.TypeApplication;
            TypePresetCheck.IsChecked = _autoCfg.TypePreset;
            TypeUnknownCheck.IsChecked = _autoCfg.TypeUnknown;
            RatingGCheck.IsChecked = _autoCfg.RatingG;
            RatingPgCheck.IsChecked = _autoCfg.RatingPg;
            RatingRCheck.IsChecked = _autoCfg.RatingR;

            _isApplyingUi = false;
            RefreshServicePanel();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "初始化自动备份设置失败");
        }
    }

    /// <summary>刷新服务管理面板的可用状态 + 状态文本。</summary>
    private void RefreshServicePanel()
    {
        bool installed = _serviceManager.IsInstalled();
        bool running = _serviceManager.IsRunning();
        bool enabled = _autoCfg?.Enabled ?? false;

        InstallServiceButton.IsEnabled = !installed;
        UninstallServiceButton.IsEnabled = installed;
        StartServiceButton.IsEnabled = installed && !running;
        StopServiceButton.IsEnabled = running;

        if (!installed)
            ServiceStatusText.Text = L("AutoBackup_ServiceNotInstalled.Text");
        else if (running)
            ServiceStatusText.Text = L("AutoBackup_ServiceRunning.Text");
        else
            ServiceStatusText.Text = L("AutoBackup_ServiceInstalled.Text");
    }

    private async void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingUi || _autoCfg == null) return;
        // 三态:关闭 = 全 false;后台服务 = Enabled+ServiceEnabled;启动时备份 = 仅 Enabled
        _autoCfg.Enabled = ModeServiceRadio.IsChecked == true || ModeOnStartupRadio.IsChecked == true;
        _autoCfg.ServiceEnabled = ModeServiceRadio.IsChecked == true;
        // 仅"后台服务"模式显示服务管理面板
        ServicePanel.Visibility = ModeServiceRadio.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        ScheduleSaveAutoBackupConfig();
        RefreshServicePanel();
    }

    private async void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_isApplyingUi || _autoCfg == null) return;
        _autoCfg.TypeScene = TypeSceneCheck.IsChecked == true;
        _autoCfg.TypeVideo = TypeVideoCheck.IsChecked == true;
        _autoCfg.TypeWeb = TypeWebCheck.IsChecked == true;
        _autoCfg.TypeApplication = TypeAppCheck.IsChecked == true;
        _autoCfg.TypePreset = TypePresetCheck.IsChecked == true;
        _autoCfg.TypeUnknown = TypeUnknownCheck.IsChecked == true;
        _autoCfg.RatingG = RatingGCheck.IsChecked == true;
        _autoCfg.RatingPg = RatingPgCheck.IsChecked == true;
        _autoCfg.RatingR = RatingRCheck.IsChecked == true;
        ScheduleSaveAutoBackupConfig();
    }

    /// <summary>500ms 防抖保存配置:连续勾选/切模式只落盘最后一次(LoadPapers 同款先例)。</summary>
    private void ScheduleSaveAutoBackupConfig()
    {
        var cts = new CancellationTokenSource();
        _saveDebounceCts?.Cancel();
        _saveDebounceCts = cts;
        _ = SaveWithDebounceAsync(cts.Token);
    }

    private async Task SaveWithDebounceAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(500, ct);
        }
        catch (TaskCanceledException)
        {
            return;
        }
        await SaveAutoBackupConfigAsync();
    }

    private async Task SaveAutoBackupConfigAsync()
    {
        if (_autoCfg == null) return;
        try
        {
            var svc = new ConfigService();
            var settings = await svc.LoadAsync();
            settings.AutoBackup = _autoCfg;
            await svc.SaveAsync(settings);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "保存自动备份配置失败");
        }
    }

    private async void ServiceAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string? error = null;
        switch ((string)btn.Tag)
        {
            case "install":
                // [2026-09] 硬链接备份依赖 NTFS:库盘非 NTFS(或 ReFS)时阻止安装,避免装上空壳服务
                if (!IsWorkshopDriveHardLinkSupported())
                {
                    error = L("AutoBackup_NotNtfs");
                    break;
                }
                error = _serviceManager.Install();
                if (error == null && _autoCfg != null)
                {
                    _autoCfg.Enabled = true;
                    _autoCfg.ServiceEnabled = true;
                    await SaveAutoBackupConfigAsync();
                }
                break;
            case "uninstall":
                error = _serviceManager.Uninstall();
                if (error == null && _autoCfg != null)
                {
                    _autoCfg.ServiceEnabled = false;
                    await SaveAutoBackupConfigAsync();
                }
                break;
            case "start":
                error = _serviceManager.Start();
                break;
            case "stop":
                _serviceManager.StopProcess();
                break;
        }
        RefreshServicePanel();
        if (error != null)
            await DialogHelper.ShowMessageAsync(L("AutoBackup_OperationFailed.Text"), error);
    }

    /// <summary>
    /// [2026-09] 创意工坊库所在盘是否支持硬链接备份:硬链接(CreateHardLink)是 NTFS/ReFS 特性,
    /// FAT32/exFAT 不支持。库路径不存在/无法判定时返回 true(不误伤——服务安装后启动自检兜底)。
    /// </summary>
    private static bool IsWorkshopDriveHardLinkSupported()
    {
        try
        {
            var vm = ((App)Application.Current).ViewModel;
            string workshop = vm.PathManagementVM.WorkshopPath;
            if (string.IsNullOrWhiteSpace(workshop) || !Directory.Exists(workshop))
                return true; // 路径未配置/不存在:不阻止,交给服务运行时兜底
            var root = Path.GetPathRoot(workshop);
            if (string.IsNullOrEmpty(root)) return true;
            string fs = new DriveInfo(root).DriveFormat;
            return string.Equals(fs, "NTFS", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fs, "ReFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "检查创意工坊库盘文件系统失败,放行安装");
            return true; // 判定失败不阻止(宁可装后由服务兜底,不误伤正常场景)
        }
    }

    private void AutoBackupButton_Click(object sender, RoutedEventArgs e)
    {
        _ = InitializeAutoBackupSettingsAsync();
        AutoBackupFlyout.ShowAt(sender as FrameworkElement);
    }

    // ====================== 立即备份 ======================

    /// <summary>[立即备份 2026-09] 手动触发一次补齐备份:沿用自动备份的类型/分级筛选,对工坊里
    /// 「未备份且命中筛选」的壁纸各建一次硬链接。复用 BackupService.BackupAllMissing(与「启动时备份」
    /// 同一实现),完成后刷新列表把刚补上的备份显示出来。只删副本不动源文件,故无破坏性提示。</summary>
    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        if (_isBackingUp) return;
        _isBackingUp = true;
        BackupNowButton.IsEnabled = false;
        ScanProgress.IsActive = true;
        try
        {
            var cfg = _autoCfg;
            if (cfg is null || !cfg.Enabled)
            {
                await DialogHelper.ShowMessageAsync("立即备份",
                    "自动备份尚未启用。请先点左边的「自动备份」选择备份方式（后台服务或 WE Tool 启动时备份），并确认创意工坊路径。");
                return;
            }

            var workshopPath = WorkshopPath;
            if (string.IsNullOrEmpty(workshopPath) || !Directory.Exists(workshopPath))
            {
                await DialogHelper.ShowMessageAsync("立即备份",
                    "创意工坊目录不存在，请先在设置中检查路径是否有效。");
                return;
            }

            // 补齐可能涉及几百个壁纸:回调里每 10 项刷一次汇总文本(免得看着像卡住),完成后由 LoadBackupsAsync 重写
            int backed = await Task.Run(() => BackupService.BackupAllMissing(workshopPath, cfg, (done, total) =>
            {
                if (done == total || done % 10 == 0)
                    DispatcherQueue.TryEnqueue(() => SummaryText.Text = $"立即备份中 {done}/{total}…");
            }));

            await LoadBackupsAsync(); // 刷新列表(把刚补上的备份显示出来)

            await DialogHelper.ShowMessageAsync("立即备份",
                backed > 0
                    ? $"立即备份完成：新增 {backed} 个备份。"
                    : "没有需要备份的壁纸（都已经有备份，或没有命中当前的类型/分级筛选）。");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[备份] 立即备份失败");
            await DialogHelper.ShowMessageAsync("立即备份失败", ex.Message);
        }
        finally
        {
            ScanProgress.IsActive = false;
            BackupNowButton.IsEnabled = true;
            _isBackingUp = false;
        }
    }
}

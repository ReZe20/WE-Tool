using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using WE_Tool.Controls;
using WE_Tool.Helper;
using WE_Tool.Service;
using WE_Tool.ViewModels;

namespace WE_Tool.Views;

public sealed partial class WallpaperBackup : Page
{
    private string WorkshopPath => ((App)Application.Current).ViewModel.PathManagementVM.WorkshopPath;
    private string BackupRoot => BackupService.GetBackupRoot(WorkshopPath);

    public ObservableCollection<BackupItemViewModel> BackupItems { get; } = new();
    private bool _initialScanDone;
    private readonly AutoBackupServiceManager _serviceManager = new();
    private bool _isApplyingUi;   // 避免 UI 初始化时的 Checked 事件触发保存
    private CancellationTokenSource? _saveDebounceCts; // 配置变更防抖:500ms 只写最后一次

    /// <summary>自动备份配置(页面持有副本,变化时回写 config.json)。</summary>
    private Models.AutoBackupConfig? _autoCfg;


    public WallpaperBackup()
    {
        InitializeComponent();
        BackupGridView.ItemsSource = BackupItems;
        Loaded += (s, e) => InitializeAutoBackupSettingsAsync();
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

    /// <summary>遍历可见容器重启 GIF 播放(页面缓存切回时;容器未就绪/无项时无害)。</summary>
    private void RestartVisibleGifPlayback()
    {
        // 非反射(AOT 兼容):ItemsPanelRoot 返回类型是 Panel(基类),Children 是 Panel 属性,直接访问即可,不强转 ItemsWrapGrid
        if (BackupGridView.ItemsPanelRoot is not { } panelRoot) return;
        foreach (var child in panelRoot.Children)
        {
            if (child is not GridViewItem container) continue;
            if (container.ContentTemplateRoot is not FrameworkElement content) continue;
            if (BackupGridView.ItemFromContainer(container) is not BackupItemViewModel vm) continue;
            if (!string.IsNullOrEmpty(vm.PreviewPath)
                && vm.PreviewPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                if (content.FindName("PreviewSkiaGif") is SkiaGifView skia
                    && skia.Visibility == Visibility.Visible)
                {
                    skia.Start(vm.PreviewPath);
                }
            }
        }
    }

    private void ScanButton_Click(object? sender, RoutedEventArgs? e) => _ = LoadBackupsAsync();

    private async Task LoadBackupsAsync()
    {
        EmptyState.Visibility = Visibility.Collapsed;
        ScanProgress.IsActive = true;
        BackupGridView.Visibility = Visibility.Collapsed;

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
                    BackupGridView.Visibility = Visibility.Collapsed;
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
            BackupGridView.Visibility = BackupItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            // 强制刷新一次卡片宽度(Visibility 变化触发的 SizeChanged 时序不可靠)
            UpdateCardWidths();
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
        BackupGridView.Visibility = Visibility.Collapsed;
    }

    private async void DeleteBackup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not BackupItemViewModel item) return;
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
                    BackupGridView.Visibility = Visibility.Collapsed;
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

    private void BackupGridView_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateCardWidths();

    /// <summary>卡片容器生成/复用时:仅切换可见性;GIF 启动延迟到下一帧(避免滚动时同步解码大量 GIF 卡死 UI 线程)。</summary>
    private void BackupGridView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not BackupItemViewModel vm) return;
        if (args.Phase != 0) return;

        var container = args.ItemContainer as GridViewItem;
        var content = container?.ContentTemplateRoot as FrameworkElement;
        if (content == null) return;

        var img = content.FindName("PreviewImage") as Image;
        var skia = content.FindName("PreviewSkiaGif") as SkiaGifView;
        if (img == null || skia == null) return;

        bool isGif = !string.IsNullOrEmpty(vm.PreviewPath)
            && vm.PreviewPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        if (isGif)
        {
            skia.Visibility = Visibility.Visible;
            img.Visibility = Visibility.Collapsed;
            // 延迟到下一帧再启动:滚动时容器大量进出,同步 Start 会批量解码 GIF 阻塞 UI 线程
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

    /// <summary>按 GridView 实际宽度计算列数,设置 ItemsWrapGrid.ItemWidth(原生排布,无布局循环)。</summary>
    private void UpdateCardWidths()
    {
        if (BackupGridView.ActualWidth <= 0 || BackupItems.Count == 0) return;
        // 间距来自 GridViewItem Margin="5"(左右共 10),列宽公式与 Papers 一致
        int cols = Math.Max(1, (int)(BackupGridView.ActualWidth / 320));
        double itemWidth = BackupGridView.ActualWidth / cols - 2;
        // 宽度未变时跳过,避免 SizeChanged 递归
        if (Math.Abs(itemWidth - _lastCardWidth) < 0.5) return;
        _lastCardWidth = itemWidth;
        // 非反射(AOT 兼容):ItemsPanelRoot 在 ItemsWrapGrid 面板下必定是 ItemsWrapGrid,用 DP SetValue 直接灌值,不走 C# 类型匹配
        if (BackupGridView.ItemsPanelRoot is { } panelRoot)
            panelRoot.SetValue(ItemsWrapGrid.ItemWidthProperty, itemWidth);
    }

    private double _lastCardWidth;

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
                BackupGridView.Visibility = Visibility.Collapsed;
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
            BackupGridView.Visibility = Visibility.Visible;
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
            await DialogHelper.ShowMessageAsync(L("AutoBackup_OperationFailed"), error);
    }

    private void AutoBackupButton_Click(object sender, RoutedEventArgs e)
    {
        InitializeAutoBackupSettingsAsync();
        AutoBackupFlyout.ShowAt(sender as FrameworkElement);
    }
}

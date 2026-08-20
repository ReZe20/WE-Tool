using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    /// <summary>自动备份配置(页面持有副本,变化时回写 config.json)。</summary>
    private Models.AutoBackupConfig? _autoCfg;


    public WallpaperBackup()
    {
        InitializeComponent();
        BackupGridView.ItemsSource = BackupItems;
        Loaded += (s, e) => InitializeAutoBackupSettingsAsync();
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
        if (BackupGridView.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        foreach (var child in panel.Children)
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

        await Task.Run(() =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                BackupItems.Clear();
                long totalBytes = 0;
                if (!Directory.Exists(BackupRoot)) { ShowEmpty(); return; }

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
                    try
                    {
                        var lines = File.ReadAllLines(marker);
                        var createdLine = lines.FirstOrDefault(l => l.StartsWith("created="));
                        if (createdLine != null)
                            backupTimeText = createdLine.Substring("created=".Length).Trim();
                    }
                    catch { }

                    // 源文件是否已删除:content/431960/<id> 目录不存在 = 取消订阅/下架,仅剩备份
                    bool sourceMissing = !Directory.Exists(Path.Combine(WorkshopPath, id));

                    BackupItems.Add(new BackupItemViewModel
                    {
                        WorkshopId = id,
                        Title = title,
                        PreviewPath = previewPath,
                        SizeText = FormatSize(totalSize),
                        BackupTimeText = backupTimeText,
                        FullPath = dir,
                        IsSourceMissing = sourceMissing,
                    });
                }

                int missingCount = BackupItems.Count(it => it.IsSourceMissing);
                SummaryText.Text = BackupItems.Count > 0
                    ? missingCount > 0
                        ? $"共 {BackupItems.Count} 个备份 · {FormatSize(totalBytes)} · {missingCount} 个源已删除"
                        : $"共 {BackupItems.Count} 个备份 · {FormatSize(totalBytes)}"
                    : "";
                if (BackupItems.Count == 0) ShowEmpty();
                ScanProgress.IsActive = false;
                BackupGridView.Visibility = BackupItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                // 强制刷新一次卡片宽度(Visibility 变化触发的 SizeChanged 时序不可靠)
                UpdateCardWidths();
            });
        });
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
            BackupItems.Remove(item);
            long remaining = 0;
            foreach (var it in BackupItems)
            {
                if (Directory.Exists(it.FullPath))
                    foreach (var f in Directory.EnumerateFiles(it.FullPath, "*", SearchOption.AllDirectories))
                        remaining += new FileInfo(f).Length;
            }
            SummaryText.Text = BackupItems.Count > 0
                ? $"共 {BackupItems.Count} 个备份 · {FormatSize(remaining)}"
                : "";
            if (BackupItems.Count == 0) ShowEmpty();
        }
        catch (Exception ex)
        {
            await DialogHelper.ShowMessageAsync("删除失败", ex.Message);
        }
    }

    private async void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        if (BackupItems.Count == 0) return;

        bool confirmed = await DialogHelper.ShowConfirmDialogAsync("删除全部备份",
            $"确定要删除全部 {BackupItems.Count} 个备份吗？\n\n删除后无法恢复。",
            "全部删除", "取消");
        if (!confirmed) return;

        int success = 0;
        foreach (var item in BackupItems.ToList())
        {
            try
            {
                if (Directory.Exists(item.FullPath))
                {
                    Directory.Delete(item.FullPath, true);
                    success++;
                }
            }
            catch { /* 跳过删除失败项 */ }
        }

        BackupItems.Clear();
        SummaryText.Text = "";
        ShowEmpty();
        await DialogHelper.ShowMessageAsync("删除完成", $"成功删除 {success} 个备份。");
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
        if (BackupGridView.ItemsPanelRoot is ItemsWrapGrid wrap)
            wrap.ItemWidth = itemWidth;
    }

    private double _lastCardWidth;

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
        await SaveAutoBackupConfigAsync();
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

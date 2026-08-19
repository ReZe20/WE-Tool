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

                    // 读取预览图路径
                    string previewPath = "";
                    foreach (var ext in new[] { "preview.png", "preview.jpg", "preview.gif" })
                    {
                        var p = Path.Combine(dir, ext);
                        if (File.Exists(p)) { previewPath = p; break; }
                    }

                    // 计算总大小
                    long totalSize = 0;
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        totalSize += new FileInfo(f).Length;

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

                    BackupItems.Add(new BackupItemViewModel
                    {
                        WorkshopId = id,
                        Title = title,
                        PreviewPath = previewPath,
                        SizeText = FormatSize(totalSize),
                        BackupTimeText = backupTimeText,
                        FullPath = dir,
                    });
                }

                SummaryText.Text = BackupItems.Count > 0
                    ? $"共 {BackupItems.Count} 个备份"
                    : "";
                if (BackupItems.Count == 0) ShowEmpty();
                ScanProgress.IsActive = false;
                BackupGridView.Visibility = BackupItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
            SummaryText.Text = BackupItems.Count > 0 ? $"共 {BackupItems.Count} 个备份" : "";
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
    {
        if (BackupGridView.ActualWidth <= 0) return;
        int cols = Math.Max(1, (int)(BackupGridView.ActualWidth / 320));
        double itemWidth = (BackupGridView.ActualWidth - (cols + 1) * 10) / cols;
        foreach (var item in BackupItems)
            item.ParentWidth = itemWidth;
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
            ModeServiceRadio.IsChecked = _autoCfg.Enabled;

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
        _autoCfg.Enabled = ModeServiceRadio.IsChecked == true;
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

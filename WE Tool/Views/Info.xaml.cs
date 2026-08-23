using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Models;
using WE_Tool.Service;
using WE_Tool.ViewModels;
using Windows.UI;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WE_Tool;

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class Info : Page
{
    public SettingsViewModel ViewModel { get; }
    public string VersionText { get; } = GetVersionText();
    public string ConfigPathText => ViewModel.AppSettingsVM.ConfigPath;
    public string LogPathText => ViewModel.AppSettingsVM.LogPath;
    public string CachePathText => ViewModel.AppSettingsVM.CachePath;
    public ObservableCollection<Contributor> Contributors { get; } = new();
    public ObservableCollection<Contributor> RepkgContributors { get; } = new();

    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string? _logPath;
    private long _logPosition;
    private bool _logAtBottom = true;
    // 日志面板拖拽调高:手柄拖动改 LogScrollViewer.Height(不持久化,重启回默认 220)
    private bool _logResizing;
    private double _logResizeStartY;
    private double _logResizeStartHeight;
    private const double MinLogHeight = 60;
    private const double MaxLogHeight = 800;
    // RePKG_Re 日志面板(独立文件 repkg.log,RepkgCliService 写入,每次提取开始清空)
    private readonly string? _repkgLogPath;
    private long _repkgLogPosition;
    private bool _repkgLogAtBottom = true;
    private bool _repkgLogResizing;
    private double _repkgLogResizeStartY;
    private double _repkgLogResizeStartHeight;
    // 自动备份服务日志面板(独立文件 AutoBackupService.log,服务进程写入)
    private readonly string? _autoBackupLogPath;
    private long _autoBackupLogPosition;
    private bool _autoBackupLogAtBottom = true;
    private bool _autoBackupLogResizing;
    private double _autoBackupLogResizeStartY;
    private double _autoBackupLogResizeStartHeight;
    private int _lastSteamState = -1; // -1=未检查 0=正常 1=初始化失败 2=中途断开
    private Task? _steamInitTask;

    // 控制台风格配色(Windows Terminal 色板;日志面板在两种主题下均保持深色)
    private static readonly SolidColorBrush LogInfoBrush = new(Color.FromArgb(0xFF, 0xCC, 0xCC, 0xCC));
    private static readonly SolidColorBrush LogWarnBrush = new(Color.FromArgb(0xFF, 0xF9, 0xF1, 0xA5));
    private static readonly SolidColorBrush LogErrorBrush = new(Color.FromArgb(0xFF, 0xF1, 0x4C, 0x4C));
    private static readonly SolidColorBrush LogDebugBrush = new(Color.FromArgb(0xFF, 0x76, 0x76, 0x76));

    private static Brush GetLogLevelBrush(string line)
    {
        if (line.Contains("[ERR]", StringComparison.Ordinal) || line.Contains("[FTL]", StringComparison.Ordinal))
            return LogErrorBrush;
        if (line.Contains("[WRN]", StringComparison.Ordinal))
            return LogWarnBrush;
        if (line.Contains("[DBG]", StringComparison.Ordinal) || line.Contains("[VRB]", StringComparison.Ordinal))
            return LogDebugBrush;
        return LogInfoBrush;
    }

    /// <summary>RePKG_Re 后端版本:读取随包 exe 的文件版本(0.5.0.0 → 0.5.0),自动跟随后端发布</summary>
    public string RepkgVersionText
    {
        get
        {
            try
            {
                var exePath = Path.Combine(AppContext.BaseDirectory, "repkg", "RePKG_Re.exe");
                if (!File.Exists(exePath)) return string.Empty;
                var version = FileVersionInfo.GetVersionInfo(exePath).FileVersion;
                return string.IsNullOrEmpty(version) ? string.Empty : TrimFileVersion(version);
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>随包 RePKG_Re 是否为目标版本(静态信息,运行时不变;Info 页 InfoBar 与导航徽标共用)</summary>
    public static bool IsRepkgStatusOk()
    {
        try
        {
            var required = RepkgVersionInfo.Required;
            if (string.IsNullOrEmpty(required)) return false; // 构建时未注入(external 缺失)
            var exePath = Path.Combine(AppContext.BaseDirectory, "repkg", "RePKG_Re.exe");
            if (!File.Exists(exePath)) return false;
            var version = FileVersionInfo.GetVersionInfo(exePath).FileVersion;
            var current = string.IsNullOrEmpty(version) ? string.Empty : TrimFileVersion(version);
            return current == required;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 文件版本 "0.5.0.0" → "0.5.0":只裁掉 FileVersion 自动补的第四段(.0),保留 Major.Minor.Build。
    /// 不能用 TrimEnd('0', '.')——它会把 0.5.0.0 误剪成 0.5(第三段为 0 时),与注入的 Required("0.5.0")比对失败。
    /// </summary>
    private static string TrimFileVersion(string fileVersion)
    {
        var parts = fileVersion.Split('.');
        if (parts.Length == 4 && parts[3] == "0")
            return $"{parts[0]}.{parts[1]}.{parts[2]}";
        return fileVersion;
    }

    /// <summary>校验随包 RePKG_Re 是否为目标版本(目标版本构建时从 external/repkg_Re 仓库 csproj 注入)</summary>
    private void UpdateRepkgStatus()
    {
        try
        {
            // 目标版本:构建时由 InjectRepkgVersion 目标生成 RepkgVersion.g.cs 常量,
            // 单源来自 external/repkg_Re/RePKG_Re/RePKG_Re.csproj
            var required = RepkgVersionInfo.Required;
            if (string.IsNullOrEmpty(required)) return; // 构建时未注入(如 external 缺失),不做校验

            var exePath = Path.Combine(AppContext.BaseDirectory, "repkg", "RePKG_Re.exe");
            if (!File.Exists(exePath))
            {
                RepkgStatusBar.Severity = InfoBarSeverity.Error;
                RepkgStatusBar.Title = LanguageHelper.GetResource("Info_RepkgVersionMissing.Title.Text");
                RepkgStatusBar.Message = LanguageHelper.GetResource("Info_RepkgVersionMissing.Message.Text");
            }
            else if (IsRepkgStatusOk())
            {
                RepkgStatusBar.Severity = InfoBarSeverity.Success;
                RepkgStatusBar.Title = LanguageHelper.GetResource("Info_RepkgVersionOk.Title.Text");
                RepkgStatusBar.Message = string.Format(
                    LanguageHelper.GetResource("Info_RepkgVersionOk.Message.Text"), RepkgVersionText);
            }
            else
            {
                RepkgStatusBar.Severity = InfoBarSeverity.Error;
                RepkgStatusBar.Title = LanguageHelper.GetResource("Info_RepkgVersionMismatch.Title.Text");
                RepkgStatusBar.Message = string.Format(
                    LanguageHelper.GetResource("Info_RepkgVersionMismatch.Message.Text"),
                    string.IsNullOrEmpty(RepkgVersionText) ? "?" : RepkgVersionText, required);
            }
            RepkgStatusBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "检查 RePKG_Re 版本失败");
        }
    }

    public ObservableCollection<TranslationStatusItem> TranslationStatus { get; } = new();

    public Info()
    {
        var app = Application.Current as App;
        ViewModel = app?.ViewModel ?? new SettingsViewModel(new ConfigService(), new PickerService());
        InitializeComponent();
        // 贡献者数据硬编码(ContributorsData.cs),发布包不再携带 CSV 文件
        LoadContributors(Contributors, ContributorsData.Main);
        LoadContributors(RepkgContributors, ContributorsData.Repkg);

        // 当前日志文件固定为 logs/log.txt(Serilog 统一单文件,不做滚动;启动时清理历史序号文件)。
        // 兜底:若目录里只有历史滚动文件(log_001.txt 等),读最新的那个,避免面板空白
        _logPath = ResolveMainLogPath(ViewModel.AppSettingsVM.LogPath);
        // RePKG_Re 日志:RepkgCliService 写入 logs/repkg.log(每次提取开始清空)
        _repkgLogPath = Path.Combine(ViewModel.AppSettingsVM.LogPath, "repkg.log");
        // 自动备份服务日志:AutoBackupService 写入 %LOCALAPPDATA%/WE_Tool/AutoBackupService.log
        _autoBackupLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WE_Tool", "AutoBackupService.log");
        _logTimer.Tick += OnLogTimerTick;
        _logTimer.Start();
        // Steamworks 首次初始化放后台线程,避免页面加载卡顿;完成后立即反映状态
        _steamInitTask = SteamWorkshopService.InitializeOnBackground();
        _ = RefreshSteamStatusAsync();
        UpdateRepkgStatus(); // RePKG_Re 后端版本校验(静态信息,加载时查一次)

        // 翻译完成度(构建时统计,加载时填充一次)
        foreach (var item in TranslationStatusInfo.Items)
            TranslationStatus.Add(item);
    }

    /// <summary>解析主日志路径:优先 logs/log.txt;若不存在(旧版本滚动遗留),取最新的 log_*.txt。</summary>
    private static string ResolveMainLogPath(string logDir)
    {
        var primary = Path.Combine(logDir, "log.txt");
        if (File.Exists(primary)) return primary;
        try
        {
            var latest = Directory.GetFiles(logDir, "log_*.txt")
                .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                .FirstOrDefault();
            if (latest != null) return latest;
        }
        catch { }
        return primary; // 目录不存在/无文件时仍指向 log.txt(Serilog 启动时会创建)
    }

    private static string GetVersionText()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return version == null ? string.Empty : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>InfoBar 动作按钮:正常态=关闭 Steamworks,异常/已关闭态=重试。按当前状态分发。</summary>
    private async void SteamActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSteamState == 0)
        {
            // 正常态:关闭 Steamworks
            SteamWorkshopService.GetInstance().Shutdown();
            _lastSteamState = -1; // 强制刷新:立即反映"已关闭"状态
            UpdateSteamStatus();
            return;
        }

        // 异常/已关闭态:重试
        SteamActionButton.Visibility = Visibility.Collapsed;
        RetryProgressBar.Visibility = Visibility.Visible;
        SteamStatusBar.Severity = InfoBarSeverity.Warning;
        SteamStatusBar.Title = LanguageHelper.GetResource("Info_SteamRetrying.Title.Text");
        SteamStatusBar.Message = LanguageHelper.GetResource("Info_SteamRetrying.Message.Text");
        SteamStatusBar.IsOpen = true;

        await Task.Run(() => SteamWorkshopService.GetInstance().Reinitialize());
        // 给子进程一点时间完成 Steamworks 初始化(成功则响应状态,失败则退出)
        await Task.Delay(800);

        RetryProgressBar.Visibility = Visibility.Collapsed;
        _lastSteamState = -1; // 强制刷新:成功翻绿,失败应用红条 + 恢复重试按钮
        await SteamWorkshopService.GetInstance().RefreshStatusAsync();
        UpdateSteamStatus();
    }

    /// <summary>等待后台初始化完成后刷新状态(初始化期间 UI 不阻塞)</summary>
    private async Task RefreshSteamStatusAsync()
    {
        if (_steamInitTask is { } init)
        {
            _steamInitTask = null;
            await init;
        }
        await SteamWorkshopService.GetInstance().RefreshStatusAsync();
        UpdateSteamStatus();
    }

    /// <summary>反映 Steamworks 工作状况(经桥接子进程):正常=绿、初始化失败=红、断开(Steam 关闭桥接被杀)=黄;
    /// 状态变化才更新 UI;随日志轮询每秒复查</summary>
    private void UpdateSteamStatus()
    {
        try
        {
            var service = SteamWorkshopService.GetInstance();
            int state = service.Status switch
            {
                SteamworksStatus.Running => 0,
                SteamworksStatus.Disconnected => 2,
                SteamworksStatus.Stopped => 3,
                _ => 1,
            };
            if (state == _lastSteamState) return;
            _lastSteamState = state;

            // 正常态显示"关闭 Steamworks"按钮,异常/已关闭态显示"重试"按钮(单按钮双角色)
            if (state == 0)
            {
                SteamActionButton.Content = LanguageHelper.GetResource("Info_SteamShutdown.Content");
                SteamActionButton.Visibility = Visibility.Visible;
            }
            else
            {
                SteamActionButton.Content = LanguageHelper.GetResource("Info_SteamRetry.Content");
                SteamActionButton.Visibility = Visibility.Visible;
            }
            switch (state)
            {
                case 0:
                    SteamStatusBar.Severity = InfoBarSeverity.Success;
                    SteamStatusBar.Title = LanguageHelper.GetResource("Info_SteamStatusOk.Title.Text");
                    SteamStatusBar.Message = LanguageHelper.GetResource("Info_SteamStatusOk.Message.Text");
                    break;
                case 1:
                    SteamStatusBar.Severity = InfoBarSeverity.Error;
                    SteamStatusBar.Title = LanguageHelper.GetResource("Info_SteamStatusFail.Title.Text");
                    SteamStatusBar.Message = LanguageHelper.GetResource("Info_SteamStatusFail.Message.Text");
                    break;
                case 2:
                    SteamStatusBar.Severity = InfoBarSeverity.Warning;
                    SteamStatusBar.Title = LanguageHelper.GetResource("Info_SteamStatusLost.Title.Text");
                    SteamStatusBar.Message = LanguageHelper.GetResource("Info_SteamStatusLost.Message.Text");
                    break;
                case 3:
                    SteamStatusBar.Severity = InfoBarSeverity.Warning;
                    SteamStatusBar.Title = LanguageHelper.GetResource("Info_SteamStatusStopped.Title.Text");
                    SteamStatusBar.Message = LanguageHelper.GetResource("Info_SteamStatusStopped.Message.Text");
                    break;
            }
            SteamStatusBar.IsOpen = true;
        }
        catch
        {
            // Steamworks 未就绪(库缺失等)时保持隐藏
        }
    }

    /// <summary>每秒轮询 log.txt 尾部,追加新内容;文件被截断/重建时从头重读。
    /// 主日志文件不存在时仅跳过自身轮询,repkg/自动备份面板照常更新(否则一个文件缺失三个面板全空白)。</summary>
    private void OnLogTimerTick(object? sender, object e)
    {
        _ = RefreshSteamStatusAsync();
        PollRepkgLog();
        PollAutoBackupLog();

        if (_logPath == null || !File.Exists(_logPath)) return;
        try
        {
            // 首次加载只读尾部 64KB,避免刷出整屏历史
            if (_logPosition == 0 && LogTextBlock.Inlines.Count == 0)
                _logPosition = Math.Max(0, new FileInfo(_logPath).Length - 64 * 1024);

            using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _logPosition)
            {
                _logPosition = 0;
                LogTextBlock.Inlines.Clear();
            }
            if (fs.Length == _logPosition) return;

            fs.Seek(_logPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var chunk = reader.ReadToEnd();
            _logPosition = fs.Position;
            if (chunk.Length == 0) return;

            AppendLogChunk(chunk, LogTextBlock);
            if (_logAtBottom)
            {
                // 先强制布局再滚动:直接 ChangeView 时 ScrollableHeight 可能尚未更新,首次加载会停在顶部
                LogScrollViewer.UpdateLayout();
                LogScrollViewer.ChangeView(null, double.MaxValue, null, true);
            }
        }
        catch
        {
            // 文件暂时被占用,跳过本次轮询
        }
    }

    /// <summary>RePKG_Re 日志面板:同一 tick 轮询 repkg.log(RepkgCliService 写入,每次提取开始清空——截断检测自动清面板)</summary>
    private void PollRepkgLog()
    {
        if (_repkgLogPath == null || !File.Exists(_repkgLogPath)) return;
        try
        {
            if (_repkgLogPosition == 0 && RepkgLogTextBlock.Inlines.Count == 0)
                _repkgLogPosition = Math.Max(0, new FileInfo(_repkgLogPath).Length - 64 * 1024);

            using var fs = new FileStream(_repkgLogPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _repkgLogPosition)
            {
                _repkgLogPosition = 0;
                RepkgLogTextBlock.Inlines.Clear();
            }
            if (fs.Length == _repkgLogPosition) return;

            fs.Seek(_repkgLogPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var chunk = reader.ReadToEnd();
            _repkgLogPosition = fs.Position;
            if (chunk.Length == 0) return;

            AppendLogChunk(chunk, RepkgLogTextBlock);
            if (_repkgLogAtBottom)
            {
                RepkgLogScrollViewer.UpdateLayout();
                RepkgLogScrollViewer.ChangeView(null, double.MaxValue, null, true);
            }
        }
        catch
        {
            // 文件暂时被占用,跳过本次轮询
        }
    }

    private void RepkgLogScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => _repkgLogAtBottom = RepkgLogScrollViewer.VerticalOffset >= RepkgLogScrollViewer.ScrollableHeight - 4;

    private void RepkgLogResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _repkgLogResizing = true;
        _repkgLogResizeStartY = e.GetCurrentPoint(null).Position.Y;
        _repkgLogResizeStartHeight = RepkgLogScrollViewer.Height;
        RepkgLogResizeHandle.CapturePointer(e.Pointer);
    }

    private void RepkgLogResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_repkgLogResizing) return;
        var delta = e.GetCurrentPoint(null).Position.Y - _repkgLogResizeStartY;
        RepkgLogScrollViewer.Height = Math.Clamp(_repkgLogResizeStartHeight + delta, MinLogHeight, MaxLogHeight);
    }

    private void RepkgLogResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _repkgLogResizing = false;
        RepkgLogResizeHandle.ReleasePointerCapture(e.Pointer);
    }

    private void RepkgLogResizeHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => _repkgLogResizing = false; // 捕获意外丢失(系统中断等)时兜底,避免卡在拖拽态

    /// <summary>按行追加日志,按级别着色([ERR]/[FTL] 红、[WRN] 黄、[DBG]/[VRB] 灰、其余亮灰);
    /// 文件尾部的半行(未写完)不加换行,等下一个 tick 续接</summary>
    /// <summary>轮询自动备份服务日志(AutoBackupService.log,增量追加 + 自动滚动)。</summary>
    private void PollAutoBackupLog()
    {
        if (_autoBackupLogPath == null || !File.Exists(_autoBackupLogPath)) return;
        try
        {
            if (_autoBackupLogPosition == 0 && AutoBackupLogTextBlock.Inlines.Count == 0)
                _autoBackupLogPosition = Math.Max(0, new FileInfo(_autoBackupLogPath).Length - 64 * 1024);

            using var fs = new FileStream(_autoBackupLogPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _autoBackupLogPosition)
            {
                _autoBackupLogPosition = 0;
                AutoBackupLogTextBlock.Inlines.Clear();
            }
            if (fs.Length == _autoBackupLogPosition) return;

            fs.Seek(_autoBackupLogPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var chunk = reader.ReadToEnd();
            _autoBackupLogPosition = fs.Position;
            if (chunk.Length == 0) return;

            AppendLogChunk(chunk, AutoBackupLogTextBlock);
            if (_autoBackupLogAtBottom)
            {
                AutoBackupLogScrollViewer.UpdateLayout();
                AutoBackupLogScrollViewer.ChangeView(null, double.MaxValue, null, true);
            }
        }
        catch
        {
            // 文件暂时被占用,跳过本次轮询
        }
    }

    private void AutoBackupLogScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => _autoBackupLogAtBottom = AutoBackupLogScrollViewer.VerticalOffset >= AutoBackupLogScrollViewer.ScrollableHeight - 4;

    private void AutoBackupLogResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _autoBackupLogResizing = true;
        _autoBackupLogResizeStartY = e.GetCurrentPoint(null).Position.Y;
        _autoBackupLogResizeStartHeight = AutoBackupLogScrollViewer.Height;
        AutoBackupLogResizeHandle.CapturePointer(e.Pointer);
    }

    private void AutoBackupLogResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_autoBackupLogResizing) return;
        double delta = e.GetCurrentPoint(null).Position.Y - _autoBackupLogResizeStartY;
        double h = Math.Clamp(_autoBackupLogResizeStartHeight + delta, MinLogHeight, MaxLogHeight);
        AutoBackupLogScrollViewer.Height = h;
    }

    private void AutoBackupLogResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _autoBackupLogResizing = false;
        AutoBackupLogResizeHandle.ReleasePointerCapture(e.Pointer);
    }

    private void AutoBackupLogResizeHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => _autoBackupLogResizing = false;

    private void AppendLogChunk(string chunk, TextBlock target)
    {
        var lines = chunk.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var isLast = i == lines.Length - 1;
            if (isLast && line.Length == 0) continue; // 末尾换行产生的空元素

            var complete = !isLast || chunk.EndsWith('\n');
            var text = complete ? line + "\n" : line;
            if (text.Length > 0)
                target.Inlines.Add(new Run { Text = text, Foreground = GetLogLevelBrush(line) });
        }
    }

    private void LogScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        => _logAtBottom = LogScrollViewer.VerticalOffset >= LogScrollViewer.ScrollableHeight - 4;

    /// <summary>日志面板拖拽调高:按下时记录起点,捕获指针后按窗口坐标算增量(指针离开手柄也能继续拖)</summary>
    private void LogResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _logResizing = true;
        _logResizeStartY = e.GetCurrentPoint(null).Position.Y;
        _logResizeStartHeight = LogScrollViewer.Height;
        LogResizeHandle.CapturePointer(e.Pointer);
    }

    private void LogResizeHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_logResizing) return;
        var delta = e.GetCurrentPoint(null).Position.Y - _logResizeStartY;
        LogScrollViewer.Height = Math.Clamp(_logResizeStartHeight + delta, MinLogHeight, MaxLogHeight);
    }

    private void LogResizeHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _logResizing = false;
        LogResizeHandle.ReleasePointerCapture(e.Pointer);
    }

    private void LogResizeHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => _logResizing = false; // 捕获意外丢失(系统中断等)时兜底,避免卡在拖拽态

    /// <summary>从 CSV 加载贡献者(照抄 BetterLyrics 的 CSV 解析)</summary>
    private void LoadContributors(ObservableCollection<Contributor> target, IEnumerable<Contributor> source)
    {
        try
        {
            foreach (var c in source)
                target.Add(new Contributor
                {
                    Header = c.Header,
                    AvatarSource = c.AvatarSource,
                    Badges = c.Badges,
                    Description = c.Description
                });
        }
        catch
        {
            // 贡献者加载失败不影响页面
        }
    }

    private async void LicenseButton_Click(object sender, RoutedEventArgs e)
        => await ShowTextFileDialogAsync(
            LanguageHelper.GetResource("Info_License.Header"),
            Path.Combine(AppContext.BaseDirectory, "LICENSE"),
            "https://github.com/ReZe20/WE-Tool/blob/master/LICENSE");

    private async void ThirdPartyButton_Click(object sender, RoutedEventArgs e)
        => await ShowTextFileDialogAsync(
            LanguageHelper.GetResource("Info_ThirdPartyButton.Content"),
            Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt"),
            "https://github.com/ReZe20/WE-Tool/blob/master/THIRD-PARTY-NOTICES.txt");

    private async void RepkgLicenseButton_Click(object sender, RoutedEventArgs e)
        => await ShowTextFileDialogAsync(
            LanguageHelper.GetResource("Info_License.Header"),
            Path.Combine(AppContext.BaseDirectory, "repkg", "LICENSE"),
            "https://github.com/ReZe20/repkg-Re/blob/master/LICENSE");

    private async void RepkgThirdPartyButton_Click(object sender, RoutedEventArgs e)
        => await ShowTextFileDialogAsync(
            LanguageHelper.GetResource("Info_RepkgThirdPartyButton.Content"),
            Path.Combine(AppContext.BaseDirectory, "repkg", "THIRD-PARTY-NOTICES.txt"),
            "https://github.com/ReZe20/repkg-Re/blob/master/THIRD-PARTY-NOTICES.txt");

    /// <summary>在应用内对话框显示许可证/第三方组件全文(可选中、可滚动);viewUrl 非空时在"关闭"左边加"在浏览器中查看"按钮</summary>
    private async Task ShowTextFileDialogAsync(string title, string filePath, string? viewUrl = null)
    {
        var xamlRoot = App.MainWindowInstance?.Content?.XamlRoot;
        if (xamlRoot == null) return;

        string text;
        try
        {
            text = File.Exists(filePath) ? await File.ReadAllTextAsync(filePath) : LanguageHelper.GetResource("Info_LicenseFileMissing.Text");
        }
        catch (Exception ex)
        {
            text = $"{LanguageHelper.GetResource("Info_LicenseFileMissing.Text")}\n{ex.Message}";
        }

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new ScrollViewer
        {
            Content = new TextBlock
            {
                Text = text,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
            },
            MaxHeight = 400,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        // 底部按钮行(右对齐):[在浏览器中查看](HyperlinkButton,左) [关闭](右)
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        if (viewUrl != null)
        {
            buttonRow.Children.Add(new HyperlinkButton
            {
                Content = LanguageHelper.GetResource("Info_ViewInBrowser.Text"),
                NavigateUri = new Uri(viewUrl),
                // 样式在 App.xaml(应用级):页面 Resources 索引器不参与资源链,必须从 Application.Current.Resources 取
                Style = Application.Current.Resources["ExternalLinkButtonStyle"] as Style,
            });
        }

        var closeButton = new Button
        {
            Content = LanguageHelper.GetResource("Info_DialogClose.Content"),
        };
        buttonRow.Children.Add(closeButton);
        content.Children.Add(buttonRow);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            XamlRoot = xamlRoot,
        };
        closeButton.Click += (_, _) => dialog.Hide();

        await dialog.ShowAsync();
    }

    private void CopyConfigPath_Click(object sender, RoutedEventArgs e) => CopyToClipboard(ConfigPathText);

    private void CopyLogPath_Click(object sender, RoutedEventArgs e) => CopyToClipboard(LogPathText);

    private void CopyCachePath_Click(object sender, RoutedEventArgs e) => CopyToClipboard(CachePathText);

    private void OpenLogPath_Click(object sender, RoutedEventArgs e)
    {
        // 打开日志目录(不存在则先创建),方便用户直接查看日志
        var logDir = LogPathText;
        if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
        Process.Start(new ProcessStartInfo(logDir) { UseShellExecute = true });
    }

    private static void CopyToClipboard(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }
}

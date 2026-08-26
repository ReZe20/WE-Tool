using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WE_Tool.Views;

/// <summary>
/// 日志页:三个日志源(主日志 log.txt / RePKG_Re 提取日志 repkg.log / 自动备份服务日志 AutoBackupService.log)
/// 由顶部 Pivot 驱动切换,每个源独立面板与滚动位置(切换不丢内容)。定时器仅在页面可见时运行
/// (OnNavigatedTo/From 启停),且只轮询当前选中的源。
/// </summary>
public sealed partial class Logs : Page
{
    /// <summary>当前选中日志源:Pivot 索引 0=main / 1=repkg / 2=autobackup。</summary>
    private int _currentIndex;

    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string? _mainLogPath;
    private long _mainLogPosition;
    private bool _mainLogAtBottom = true;
    private readonly string? _repkgLogPath;
    private long _repkgLogPosition;
    private bool _repkgLogAtBottom = true;
    private readonly string? _autoBackupLogPath;
    private long _autoBackupLogPosition;
    private bool _autoBackupLogAtBottom = true;

    // 控制台风格配色(Windows Terminal 色板;日志面板在两种主题下均保持深色)——照抄 Info 页
    private static readonly SolidColorBrush LogInfoBrush = new(Color.FromArgb(0xFF, 0xCC, 0xCC, 0xCC));
    private static readonly SolidColorBrush LogWarnBrush = new(Color.FromArgb(0xFF, 0xE5, 0xC0, 0x7B));
    private static readonly SolidColorBrush LogErrorBrush = new(Color.FromArgb(0xFF, 0xF1, 0x4C, 0x4C));
    private static readonly SolidColorBrush LogDebugBrush = new(Color.FromArgb(0xFF, 0x76, 0x76, 0x76));

    public Logs()
    {
        InitializeComponent();

        var logDir = ((App)Application.Current).ViewModel.AppSettingsVM.LogPath;
        // 主日志固定 logs/log.txt(Serilog 单文件);旧版本滚动遗留(log_001.txt 等)兜底取最新——照抄 Info 页解析
        _mainLogPath = ResolveMainLogPath(logDir);
        _repkgLogPath = Path.Combine(logDir, "repkg.log");
        _autoBackupLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WE_Tool", "AutoBackupService.log");

        _logTimer.Tick += (_, _) => PollCurrentSource();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // 进入页面才开始轮询(离开即停;页面缓存复用不重建实例)
        PollCurrentSource();      // 立即刷一次,不等第一个 tick
        _logTimer.Start();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logTimer.Stop();
    }

    /// <summary>Pivot 切换日志源:更新当前索引并立即拉取该源(各面板独立,切换不丢内容)。</summary>
    private void SourcePivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _currentIndex = SourcePivot.SelectedIndex;
        PollCurrentSource();      // 立即显示该源的尾部
    }

    /// <summary>轮询当前选中源的日志文件,追加到对应面板 + 跟随滚动。</summary>
    private void PollCurrentSource()
    {
        switch (_currentIndex)
        {
            case 1: PollLog(_repkgLogPath, ref _repkgLogPosition, ref _repkgLogAtBottom, RepkgLogTextBlock, RepkgLogScrollViewer); break;
            case 2: PollLog(_autoBackupLogPath, ref _autoBackupLogPosition, ref _autoBackupLogAtBottom, AutoBackupLogTextBlock, AutoBackupLogScrollViewer); break;
            default: PollLog(_mainLogPath, ref _mainLogPosition, ref _mainLogAtBottom, MainLogTextBlock, MainLogScrollViewer); break;
        }
    }

    /// <summary>通用增量轮询:首读只取尾部 64KB;文件被截断/重建时清空重读;文件暂时被占用时跳过本次。</summary>
    private void PollLog(string? path, ref long position, ref bool atBottom,
                         TextBlock target, ScrollViewer scrollViewer)
    {
        if (path == null || !File.Exists(path)) return;
        try
        {
            if (position == 0 && target.Inlines.Count == 0)
                position = Math.Max(0, new FileInfo(path).Length - 64 * 1024);

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < position)
            {
                position = 0;
                target.Inlines.Clear();
            }
            if (fs.Length == position) return;

            fs.Seek(position, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var chunk = reader.ReadToEnd();
            position = fs.Position;
            if (chunk.Length == 0) return;

            AppendLogChunk(chunk, target);
            if (atBottom)
            {
                // 先强制布局再滚动:直接 ChangeView 时 ScrollableHeight 可能尚未更新,首次加载会停在顶部
                scrollViewer.UpdateLayout();
                scrollViewer.ChangeView(null, double.MaxValue, null, true);
            }
        }
        catch
        {
            // 文件暂时被占用,跳过本次轮询
        }
    }

    /// <summary>按行追加日志,按级别着色([ERR]/[FTL] 红、[WRN] 黄、[DBG]/[VRB] 灰、其余亮灰);
    /// 文件尾部的半行(未写完)不加换行,等下一个 tick 续接。逻辑照抄 Info 页 AppendLogChunk。</summary>
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

    /// <summary>三个面板共用:按触发源更新对应面板的"是否在底部"状态。</summary>
    private void LogScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        bool atBottom = sv.VerticalOffset >= sv.ScrollableHeight - 4;
        if (ReferenceEquals(sv, MainLogScrollViewer)) _mainLogAtBottom = atBottom;
        else if (ReferenceEquals(sv, RepkgLogScrollViewer)) _repkgLogAtBottom = atBottom;
        else if (ReferenceEquals(sv, AutoBackupLogScrollViewer)) _autoBackupLogAtBottom = atBottom;
    }

    /// <summary>解析主日志路径:优先 logs/log.txt;若不存在(旧版本滚动遗留),取最新的 log_*.txt。照抄 Info 页。</summary>
    private static string ResolveMainLogPath(string logDir)
    {
        var primary = Path.Combine(logDir, "log.txt");
        if (File.Exists(primary)) return primary;

        try
        {
            var latest = Directory.GetFiles(logDir, "log_*.txt")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return latest?.FullName ?? primary;
        }
        catch
        {
            return primary;
        }
    }
}
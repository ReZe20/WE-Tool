using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml.Media.Animation;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;
using WE_Tool.ViewModels;
using WE_Tool.Helper;
using WE_Tool.Json;

namespace WE_Tool.Views;

public sealed partial class Cleanup : Page
{
    /// <summary>本地化取值:无参数直接取,有参数则 string.Format。</summary>
    private static string L(string key, params object[] args)
    {
        string s = LanguageHelper.GetResource(key);
        return args.Length == 0 ? s : string.Format(s, args);
    }

    /// <summary>创意工坊壁纸根目录(从设置读取,不再硬编码)。</summary>
    private string WorkshopPath => ((App)Application.Current).ViewModel.PathManagementVM.WorkshopPath;
    private static readonly string WhitelistFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WE_Tool", "cleanup_whitelist.json");

    private readonly HashSet<string> _whitelist = new(StringComparer.OrdinalIgnoreCase);
    private WhitelistWindow? _whitelistWin;
    private bool _isResizing;
    private DispatcherTimer? _resizeEndTimer;
    private bool _initialScanDone;

    public ObservableCollection<CleanupCardViewModel> Cards { get; } = new();

    public Cleanup()
    {
        InitializeComponent();
        ResultGridView.ItemsSource = Cards;
        LoadWhitelist();
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
    }

    // ---------- 白名单持久化 ----------

    private void LoadWhitelist()
    {
        try
        {
            if (!File.Exists(WhitelistFile)) return;
            var list = JsonSerializer.Deserialize(File.ReadAllText(WhitelistFile), JsonContext.Default.ListString);
            if (list != null)
                foreach (var id in list)
                    _whitelist.Add(id);
        }
        catch { }
    }

    private void SaveWhitelist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WhitelistFile)!);
            File.WriteAllText(WhitelistFile, JsonSerializer.Serialize(_whitelist.ToList(), JsonContext.Default.ListString));
        }
        catch { }
    }

    // ---------- 扫描 ----------

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        SetScanning(true);
        Cards.Clear();

        try
        {
            var list = await Task.Run(() => Scan());
            foreach (var card in list)
                Cards.Add(card);

            // 应用当前排序
            ApplySort();

            bool has = Cards.Count > 0;
            ResultGridView.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            ActionBar.Visibility = Visibility.Visible;
            if (has) { UpdateWrapGridItemWidth(); UpdateSummary(); }
        }
        catch (Exception ex)
        {
            EmptyState.Visibility = Visibility.Visible;
            ResultGridView.Visibility = Visibility.Collapsed;
            ActionBar.Visibility = Visibility.Collapsed;
            EmptyStateText.Text = L("Cleanup_ScanFailed", ex.Message);
        }
        finally
        {
            SetScanning(false);
        }
    }

    private void SetScanning(bool scanning)
    {
        ScanProgress.IsActive = scanning;
        ResultGridView.IsEnabled = !scanning;
        ActionBar.IsEnabled = !scanning;
    }


    private void ResultGridView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ResultGridView.Visibility != Visibility.Visible) return;
        // 首次触发:卡片内容淡出动画
        if (!_isResizing)
        {
            _isResizing = true;
            AnimateCardContent(false);
        }
        // 列宽实时更新(不阻塞)
        UpdateWrapGridItemWidth();
        UpdateSummary();
        // 重置计时器;停止 500ms 后淡入
        _resizeEndTimer?.Stop();
        _resizeEndTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _resizeEndTimer.Tick -= OnResizeEndTick;
        _resizeEndTimer.Tick += OnResizeEndTick;
        _resizeEndTimer.Start();
    }

    private void OnResizeEndTick(object? sender, object e)
    {
        _resizeEndTimer?.Stop();
        _isResizing = false;
        AnimateCardContent(true); // 卡片内容淡入动画
    }

    /// <summary>对所有可见卡片的 CardRootGrid 执行淡入/淡出动画。</summary>
    private void AnimateCardContent(bool fadeIn)
    {
        for (int i = 0; i < ResultGridView.Items.Count; i++)
        {
            if (ResultGridView.ContainerFromIndex(i) is GridViewItem gvi
                && gvi.ContentTemplateRoot is Border border
                && border.Child is FrameworkElement child)
            {
                var anim = fadeIn
                    ? (Timeline)new FadeInThemeAnimation()
                    : new FadeOutThemeAnimation();
                anim.Duration = TimeSpan.FromMilliseconds(300);
                var sb = new Storyboard();
                sb.Children.Add(anim);
                Storyboard.SetTarget(anim, child);
                sb.Begin();
            }
        }
    }

    /// <summary>复刻 Papers:布局脏标记同帧合并,拖动中列宽实时更新。</summary>
    private void UpdateWrapGridItemWidth()
    {
        if (ResultGridView.ItemsPanelRoot is not ItemsWrapGrid wrap) return;
        wrap.CacheLength = 0;
        double available = ResultGridView.ActualWidth;
        if (available <= 0) return;
        int cols = Math.Max(1, (int)(available / (270 + 10)));
        wrap.ItemWidth = available / cols - 2;
    }

    private List<CleanupCardViewModel> Scan()
    {
        var cards = new List<CleanupCardViewModel>();
        if (!Directory.Exists(WorkshopPath)) return cards;

        foreach (var dir in Directory.GetDirectories(WorkshopPath))
        {
            var id = Path.GetFileName(dir);
            if (id == ".we_backup") continue; // 备份目录不进入清理扫描
            if (_whitelist.Contains(id)) continue;
            var installed = File.Exists(Path.Combine(dir, "project.json"));
            var card = MakeCard(dir, id, installed);
            if (card != null) cards.Add(card);
        }
        return cards;
    }

    /// <summary>生成单个壁纸文件夹的残留卡片;无残留返回 null。</summary>
    private CleanupCardViewModel? MakeCard(string dir, string id, bool installed)
    {
        if (installed)
        {
            // 组件(project.json category=Asset)不做多余文件检测,已安装组件非残留
            if (IsComponentFolder(dir)) return null;

            // 壁纸:多余文件(对比 project.json 引用)
            var std = GetStdFiles(dir);
            var excess = new List<CleanupFileItem>();
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, f);
                // 标准场景文件:shaders/shader 文件夹整体、scene.pkg 不算残留
                var firstSeg = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (firstSeg.Equals("shaders", StringComparison.OrdinalIgnoreCase)
                    || firstSeg.Equals("shader", StringComparison.OrdinalIgnoreCase)) continue;
                if (!std.Contains(rel, StringComparer.OrdinalIgnoreCase))
                {
                    excess.Add(new CleanupFileItem
                    {
                        Name = rel,
                        SizeText = FormatSize(new FileInfo(f).Length),
                        Size = new FileInfo(f).Length,
                        FullPath = f
                    });
                }
            }
            if (excess.Count == 0) return null;

            return new CleanupCardViewModel
            {
                FolderId = id,
                TypeLabel = L("Cleanup_TypeExcess"),
                FullPath = dir,
                IsUnloaded = false,
                TotalSize = excess.Sum(f => f.Size),
                StatsText = L("Cleanup_StatsExcess", excess.Count, FormatSize(excess.Sum(f => f.Size))),
                Files = excess
            };
        }
        else
        {
            // 已卸载壁纸残留(整个文件夹)
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => new CleanupFileItem
                {
                    Name = Path.GetRelativePath(dir, f),
                    SizeText = FormatSize(new FileInfo(f).Length),
                    Size = new FileInfo(f).Length,
                    FullPath = f
                }).ToList();

            if (files.Count == 0)
                files.Add(new CleanupFileItem { Name = L("Cleanup_EmptyFolderName"), SizeText = "", Size = 0, FullPath = "" });

            return new CleanupCardViewModel
            {
                FolderId = id,
                TypeLabel = L("Cleanup_TypeUnloaded"),
                FullPath = dir,
                IsUnloaded = true,
                TotalSize = DirSize(dir),
                StatsText = L("Cleanup_StatsUnloaded", files.Count, FormatSize(DirSize(dir))),
                Files = files
            };
        }
    }

    /// <summary>判断 workshop 文件夹是否为组件(project.json category=Asset)。</summary>
    private bool IsComponentFolder(string dir)
    {
        try
        {
            var p = Path.Combine(dir, "project.json");
            if (!File.Exists(p)) return false;
            var o = JsonNode.Parse(File.ReadAllText(p)) as JsonObject;
            var cat = o?["category"]?.GetValue<string>();
            return !string.IsNullOrEmpty(cat)
                && cat.Equals("Asset", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>白名单移除后把该文件夹加回列表(不用整个重扫)。</summary>
    private void RescanCardForId(string id)
    {
        var dir = Path.Combine(WorkshopPath, id);
        if (!Directory.Exists(dir)) return;
        var card = MakeCard(dir, id, File.Exists(Path.Combine(dir, "project.json")));
        if (card == null) return;

        Cards.Add(card);
        ApplySort();
        ResultGridView.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        ActionBar.Visibility = Visibility.Visible;
        UpdateWrapGridItemWidth();
    }

    private HashSet<string> GetStdFiles(string dir)
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "project.json",
            "scene.pkg" // 场景壁纸核心文件
        };
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

    // ---------- 卡片操作 ----------

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not CleanupCardViewModel card) return;
        try
        {
            Process.Start("explorer.exe", $"\"{card.FullPath}\"");
        }
        catch { }
    }

    private async void CleanCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not CleanupCardViewModel card) return;

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L("Cleanup_ConfirmClean.Title", card.FolderId),
            Content = L("Cleanup_ConfirmClean.Content", card.Files.Count),
            PrimaryButtonText = L("Cleanup_ConfirmClean.Ok"),
            CloseButtonText = L("Cleanup_CommonCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            if (card.IsUnloaded)
                Directory.Delete(card.FullPath, true);
            else
                foreach (var f in card.Files)
                {
                    try { File.Delete(f.FullPath); } catch { }
                }

            RemoveCard(card);
        }
        catch (Exception ex)
        {
            var err = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L("Cleanup_CleanFail.Title"),
                Content = ex.Message,
                CloseButtonText = L("Cleanup_CommonOk")
            };
            await err.ShowAsync();
        }
    }

    private void SelectCheckBox_Click(object sender, RoutedEventArgs e)
    {
        UpdateBatchButtons();
    }

    private void UpdateBatchButtons()
    {
        int selected = Cards.Count(c => c.IsSelected);
        BatchWhitelistButton.IsEnabled = selected > 0;
        BatchWhitelistButton.Label = selected > 0 ? L("Cleanup_BatchWhitelistCount", selected) : L("Cleanup_BatchWhitelist.Label");
        BatchDeleteButton.IsEnabled = selected > 0;
        BatchDeleteButton.Label = selected > 0 ? L("Cleanup_BatchDeleteCount", selected) : L("Cleanup_BatchDelete.Label");
    }

    private void BatchWhitelist_Click(object sender, RoutedEventArgs e)
    {
        foreach (var card in Cards.Where(c => c.IsSelected).ToList())
        {
            _whitelist.Add(card.FolderId);
            _whitelistWin?.AddWhitelistCard(card.FolderId);
            RemoveCard(card);
        }
        SaveWhitelist();
        UpdateBatchButtons();
    }

    private async void BatchDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = Cards.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) return;

        int totalFiles = selected.Sum(c => c.Files.Count);
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L("Cleanup_ConfirmBatchDelete.Title", selected.Count),
            Content = L("Cleanup_ConfirmClean.Content", totalFiles),
            PrimaryButtonText = L("Cleanup_CommonDelete"),
            CloseButtonText = L("Cleanup_CommonCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        foreach (var card in selected)
        {
            try
            {
                if (card.IsUnloaded)
                    Directory.Delete(card.FullPath, true);
                else
                    foreach (var f in card.Files)
                        try { File.Delete(f.FullPath); } catch { }
                RemoveCard(card);
            }
            catch { }
        }
        UpdateBatchButtons();
    }

    private void WhitelistCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not CleanupCardViewModel card) return;

        _whitelist.Add(card.FolderId);
        SaveWhitelist();
        _whitelistWin?.AddWhitelistCard(card.FolderId); // 通知窗口增量添加
        RemoveCard(card);
    }

    private void WhitelistButton_Click(object sender, RoutedEventArgs e)
    {
        _whitelistWin = new WhitelistWindow(_whitelist, WorkshopPath);
        var win = _whitelistWin;
        // 白名单项被移除时立即把该壁纸加回列表
        win.WhitelistItemRemoved += id =>
        {
            DispatcherQueue.TryEnqueue(() => RescanCardForId(id));
        };
        // 窗口关闭后刷新列表(可能有其他变化)
        win.Closed += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                Cards.Clear();
                foreach (var card in Scan())
                    Cards.Add(card);
                ApplySort();
                UpdateSummary();
            });
        };
        win.Activate();
    }

    private async void DeleteAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (Cards.Count == 0) return;

        int totalFiles = 0;
        foreach (var c in Cards)
            totalFiles += c.Files.Count;

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L("Cleanup_ConfirmDeleteAll.Title"),
            Content = L("Cleanup_ConfirmDeleteAll.Content", Cards.Count, totalFiles),
            PrimaryButtonText = L("Cleanup_ConfirmDeleteAll.Ok"),
            CloseButtonText = L("Cleanup_CommonCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        int ok = 0;
        foreach (var card in Cards.ToList())
        {
            try
            {
                if (card.IsUnloaded)
                    Directory.Delete(card.FullPath, true);
                else
                    foreach (var f in card.Files)
                    {
                        try { File.Delete(f.FullPath); } catch { }
                    }
                Cards.Remove(card);
                ok++;
            }
            catch { }
        }

        bool has = Cards.Count > 0;
        ResultGridView.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
        EmptyStateText.Text = L("Cleanup_CleanedComplete", ok);
        if (!has) ActionBar.Visibility = Visibility.Collapsed;
        UpdateSummary();
    }

    /// <summary>更新总结:总项数·总大小。</summary>
    private void UpdateSummary()
    {
        if (Cards.Count == 0)
        {
            SummaryText.Text = "";
            return;
        }
        int count = Cards.Sum(c => c.Files.Count);
        long total = Cards.Sum(c => c.Files.Sum(f => f.Size));
        SummaryText.Text = L("Cleanup_Summary", count, FormatSize(total));
    }

    private void RemoveCard(CleanupCardViewModel card)
    {
        Cards.Remove(card);
        bool has = Cards.Count > 0;
        ResultGridView.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (!has) ActionBar.Visibility = Visibility.Collapsed;
        UpdateSummary();
    }

    // ---------- 排序 ----------

    /// <summary>排序方式:0名称 1类型 2大小。</summary>
    private int _sortOrder = 0; // 默认按名称
    /// <summary>true=降序(大小默认最大在前;名称默认 A→Z 升序)。</summary>
    private bool _sortDescending;

    /// <summary>按当前排序方式重排集合。</summary>
    private void ApplySort()
    {
        if (Cards.Count == 0) return;

        List<CleanupCardViewModel> sorted = _sortOrder switch
        {
            0 => _sortDescending
                ? Cards.OrderByDescending(c => c.FolderId, StringComparer.CurrentCultureIgnoreCase).ToList()
                : Cards.OrderBy(c => c.FolderId, StringComparer.CurrentCultureIgnoreCase).ToList(),
            1 => _sortDescending
                ? Cards.OrderByDescending(c => c.IsUnloaded).ThenBy(c => c.FolderId, StringComparer.CurrentCultureIgnoreCase).ToList()
                : Cards.OrderBy(c => c.IsUnloaded).ThenBy(c => c.FolderId, StringComparer.CurrentCultureIgnoreCase).ToList(),
            2 => _sortDescending
                ? Cards.OrderByDescending(c => c.TotalSize).ToList()
                : Cards.OrderBy(c => c.TotalSize).ToList(),
            _ => Cards.ToList(),
        };

        Cards.Clear();
        foreach (var card in sorted) Cards.Add(card);
    }

    private void SortMenu_ItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item || item.Tag is not string tag) return;
        _sortOrder = tag switch
        {
            "name" => 0,
            "type" => 1,
            "size" => 2,
            _ => _sortOrder,
        };
        UpdateSortLabel();
        ApplySort();
    }

    private void SortDescendingItem_Click(object sender, RoutedEventArgs e)
    {
        _sortDescending = SortDescendingItem.IsChecked;
        UpdateSortLabel();
        ApplySort();
    }

    /// <summary>打开菜单前同步各控件状态。</summary>
    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        SortByNameItem.IsChecked = _sortOrder == 0;
        SortByTypeItem.IsChecked = _sortOrder == 1;
        SortBySizeItem.IsChecked = _sortOrder == 2;
        SortDescendingItem.IsChecked = _sortDescending;
    }

    /// <summary>按钮文字显示当前排序方式(如"排序:名称")。</summary>
    private void UpdateSortLabel()
    {
        string name = _sortOrder switch
        {
            0 => L("SortByName.Text"),
            1 => L("Cleanup_SortByType.Text"),
            2 => L("SortByFileSize.Text"),
            _ => "",
        };
        SortLabelText.Text = $"{L("Toolbar_Sort.ToolTipService.ToolTip")}: {name}";
    }

    // ---------- 工具 ----------

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
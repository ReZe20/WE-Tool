using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using WE_Tool.ViewModels;
using WE_Tool.Helper;
using WE_Tool.Json;
using WinUIEx;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Windowing;

namespace WE_Tool.Views;

public sealed partial class WhitelistWindow : WindowEx
{
    /// <summary>本地化取值:无参数直接取,有参数则 string.Format。</summary>
    private static string L(string key, params object[] args)
    {
        string s = LanguageHelper.GetResource(key);
        return args.Length == 0 ? s : string.Format(s, args);
    }

    private readonly HashSet<string> _whitelist;
    private readonly string _workshopPath;
    private ObservableCollection<CleanupCardViewModel> _cards = new();
    private bool _isResizing;

    /// <summary>白名单发生变化时触发(参数=被移除的 ID)。</summary>
    public event Action<string>? WhitelistItemRemoved;

    public WhitelistWindow(HashSet<string> whitelist, string workshopPath)
    {
        _whitelist = whitelist;
        _workshopPath = workshopPath;
        InitializeComponent();
        // 自定义标题栏:去系统标题栏,顶部 48px 留空当标题栏(Tall 高度)
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        Title = LanguageHelper.GetResource("WhitelistWindowTitle.Title");
        CardGridView.ItemsSource = _cards;
        LoadWhitelistCards();
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


    /// <summary>外部调用:增量添加白名单卡片。</summary>
    public void AddWhitelistCard(string id)
    {
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

    /// <summary>外部调用:增量移除白名单卡片。</summary>
    public void RemoveWhitelistCard(string id)
    {
        var card = _cards.FirstOrDefault(c => c.FolderId == id);
        if (card != null)
        {
            _cards.Remove(card); // ObservableCollection 触发动画
            UpdateVisibility();
        }
    }

    private void UpdateVisibility()
    {
        bool has = _cards.Count > 0;
        CardGridView.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (has)
        {
            long total = _cards.Sum(c => c.Files.Sum(f => f.Size));
            SubtitleText.Text = L("WhitelistWindow_Subtitle", _cards.Count, FormatSize(total));
            UpdateItemWidth();
        }
        else
        {
            SubtitleText.Text = "";
        }
    }


    private void CardGridView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (CardGridView.Visibility != Visibility.Visible) return;
        if (!_isResizing)
        {
            _isResizing = true;
            AnimateCardContent(false);
        }
        UpdateItemWidth();
        // 500ms 后恢复
        _ = ResetAnimationAsync();
    }

    private async System.Threading.Tasks.Task ResetAnimationAsync()
    {
        await System.Threading.Tasks.Task.Delay(500);
        _isResizing = false;
        AnimateCardContent(true);
    }

    private void AnimateCardContent(bool fadeIn)
    {
        for (int i = 0; i < CardGridView.Items.Count; i++)
        {
            if (CardGridView.ContainerFromIndex(i) is GridViewItem gvi
                && gvi.ContentTemplateRoot is Border border
                && border.Child is FrameworkElement child)
            {
                var anim = fadeIn ? (Timeline)new FadeInThemeAnimation() : new FadeOutThemeAnimation();
                anim.Duration = TimeSpan.FromMilliseconds(300);
                var sb = new Storyboard();
                sb.Children.Add(anim);
                Storyboard.SetTarget(anim, child);
                sb.Begin();
            }
        }
    }

    private void UpdateItemWidth()
    {
        // 非反射(AOT 兼容):ItemsPanelRoot 在 ItemsWrapGrid 面板下必定是 ItemsWrapGrid,用 DP SetValue 直接灌值,不走 C# 类型匹配
        if (CardGridView.ItemsPanelRoot is not { } panelRoot) return;
        double available = CardGridView.ActualWidth;
        if (available <= 0) return;
        int cols = Math.Max(1, (int)(available / 350));
        panelRoot.SetValue(ItemsWrapGrid.ItemWidthProperty, available / cols - 2);
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
        foreach (var card in _cards.Where(c => c.IsSelected).ToList())
        {
            _whitelist.Remove(card.FolderId);
            WhitelistItemRemoved?.Invoke(card.FolderId);
            _cards.Remove(card);
        }
        SaveWhitelistLocal();
        UpdateBatchButtons();
        UpdateVisibility();
    }

    private void SaveWhitelistLocal()
    {
        try
        {
            var file = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WE_Tool", "cleanup_whitelist.json");
            File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(_whitelist.ToList(), JsonContext.Default.ListString));
        }
        catch { }
    }

    private void RemoveFromWhitelist_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not CleanupCardViewModel card) return;
        _whitelist.Remove(card.FolderId);
        WhitelistItemRemoved?.Invoke(card.FolderId);
        SaveWhitelistLocal();
        _cards.Remove(card); // 增量移除(ObservableCollection 触发动画)
        UpdateVisibility();
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

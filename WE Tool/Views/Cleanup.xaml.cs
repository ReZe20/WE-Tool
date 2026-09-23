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
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Serilog;
using WE_Tool.ViewModels;
using WE_Tool.Helper;
using WE_Tool.Json;
using Windows.System;
using Windows.UI.Core;

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
        // 统一走 App.GetAppDataRoot():便携模式落在包内 Data\,否则 %LOCALAPPDATA%\WE_Tool
        App.GetAppDataRoot(), "cleanup_whitelist.json");

    private readonly HashSet<string> _whitelist = new(StringComparer.OrdinalIgnoreCase);
    private WhitelistWindow? _whitelistWin;
    private bool _initialScanDone;

    public ObservableCollection<CleanupCardViewModel> Cards { get; } = new();

    // [列表键盘可达 2026-09-22,同步 Papers/壁纸备份页] 与那两页同一套两层焦点模型:
    //   Ctrl+L           → 焦点从工具栏/底部命令栏/外壳导航落到残留卡片(优先回到上次停留那张);
    //   Enter / 空格     → 焦点在卡片上时,深入一层交给卡内第一个控件(勾选框;没有它则"打开文件夹");
    //                      之后 Tab/Shift+Tab 在勾选框与三个按钮之间走,空格=切换勾选,Enter=执行按钮;
    //   Esc              → 焦点在卡内控件上时退回该卡片。
    // 分层靠事件消费顺序天然成立:勾选框/按钮自己消费 Enter·空格,页面只可能收到"焦点在卡片上"时按下的这两个键。
    private int _listAnchorIndex = -1;   // 列表里最后停留过的卡下标:Ctrl+L 的落点

    public Cleanup()
    {
        InitializeComponent();
        ResultRepeater.ItemsSource = Cards;
        LoadWhitelist();
        UpdateSortLabel();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!_initialScanDone)
        {
            _initialScanDone = true;
            _ = PerformScan();
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
        catch (Exception ex)
        {
            Log.Warning(ex, "[Cleanup] 白名单读取失败");
        }
    }

    private void SaveWhitelist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WhitelistFile)!);
            File.WriteAllText(WhitelistFile, JsonSerializer.Serialize(_whitelist.ToList(), JsonContext.Default.ListString));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Cleanup] 白名单写入失败");
        }
    }

    // ---------- 扫描 ----------

    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await PerformScan();

    private async Task PerformScan()
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

            if (Cards.Count == 0)
            {
                // [2026-09] 扫描完成无残留:显示"未发现残留"提示(此前此场景无文案,空态空白)
                EmptyStateText.Text = L("Cleanup_NoResidue");
                EmptyStateDesc.Visibility = Visibility.Visible;
            }
            SyncView();
        }
        catch (Exception ex)
        {
            EmptyStateText.Text = L("Cleanup_ScanFailed", ex.Message);
            EmptyStateDesc.Visibility = Visibility.Collapsed; // 失败≠无残留,隐藏副描述
            SyncView();   // 失败态同样保留命令栏,否则无法重试扫描
        }
        finally
        {
            SetScanning(false);
        }
    }

    private void SetScanning(bool scanning)
    {
        ScanProgress.IsActive = scanning;
        ResultScrollView.IsEnabled = !scanning;
        ActionBar.IsEnabled = !scanning;
    }


    // [全迁 ItemsRepeater] resize 不再全量重排(虚拟化),原淡入淡出遮羞动画/防抖删除;
    // SizeChanged 只钳制 UniformGridLayout.MinItemWidth(防除零崩溃 #10539)
    private void ResultScrollView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResultLayoutMinWidth();
        UpdateSummary();
    }

    private void UpdateResultLayoutMinWidth()
    {
        if (ResultUniformLayout is not UniformGridLayout layout) return;
        double viewport = ResultScrollView.ActualWidth;
        int desired = 270; // 卡片档位
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
            // 单趟枚举:EnumerateFiles 惰性 + FileInfo 自带 Length,免二次 stat
            var std = GetStdFiles(dir);
            var excess = new List<CleanupFileItem>();
            foreach (var fi in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                                         .Select(f => new FileInfo(f)))
            {
                var rel = Path.GetRelativePath(dir, fi.FullName);
                // 标准场景文件:shaders/shader 文件夹整体、scene.pkg 不算残留
                var firstSeg = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (firstSeg.Equals("shaders", StringComparison.OrdinalIgnoreCase)
                    || firstSeg.Equals("shader", StringComparison.OrdinalIgnoreCase)) continue;
                if (!std.Contains(rel, StringComparer.OrdinalIgnoreCase))
                {
                    excess.Add(new CleanupFileItem
                    {
                        Name = rel,
                        SizeText = FormatSize(fi.Length),
                        Size = fi.Length,
                        FullPath = fi.FullName
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
            // 已卸载壁纸残留(整个文件夹):一次遍历同时产出文件列表与总大小
            var files = new List<CleanupFileItem>();
            long total = 0;
            foreach (var fi in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                                         .Select(f => new FileInfo(f)))
            {
                total += fi.Length;
                files.Add(new CleanupFileItem
                {
                    Name = Path.GetRelativePath(dir, fi.FullName),
                    SizeText = FormatSize(fi.Length),
                    Size = fi.Length,
                    FullPath = fi.FullName
                });
            }

            if (files.Count == 0)
                files.Add(new CleanupFileItem { Name = L("Cleanup_EmptyFolderName"), SizeText = "", Size = 0, FullPath = "" });

            return new CleanupCardViewModel
            {
                FolderId = id,
                TypeLabel = L("Cleanup_TypeUnloaded"),
                FullPath = dir,
                IsUnloaded = true,
                TotalSize = total,
                StatsText = L("Cleanup_StatsUnloaded", files.Count, FormatSize(total)),
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
        SyncView();
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

    // 弹层(菜单/Flyout)不自动继承主窗口运行时主题,打开时显式应用(公共逻辑见 App.ApplyFlyoutTheme)
    private void FlyoutThemeRefresh_Opened(object sender, object e) => App.ApplyFlyoutTheme(sender, e);

    private async void CleanCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not CleanupCardViewModel card) return;

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = App.GetPopupTheme(),
            Title = L("Cleanup_ConfirmClean.Title", card.FolderId),
            Content = L("Cleanup_ConfirmClean.Content", card.Files.Count),
            PrimaryButtonText = L("Cleanup_ConfirmClean.Ok"),
            CloseButtonText = L("Cleanup_CommonCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        int failed = 0;
        try
        {
            if (card.IsUnloaded)
            {
                Directory.Delete(card.FullPath, true);
            }
            else
            {
                foreach (var f in card.Files)
                {
                    try { File.Delete(f.FullPath); } catch { failed++; }
                }
            }

            RemoveCard(card);

            // 有文件删失败(占用/权限):提示失败数,磁盘残留下次扫描会再出现
            if (failed > 0)
            {
                var partial = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    RequestedTheme = App.GetPopupTheme(),
                    Title = L("Cleanup_CleanFail.Title"),
                    Content = L("Cleanup_CleanPartialFail", failed),
                    CloseButtonText = L("Cleanup_CommonOk")
                };
                await partial.ShowAsync();
            }
        }
        catch (Exception ex)
        {
            var err = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = App.GetPopupTheme(),
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
        AnimatedIconPlayer.PlayOnce(sender);   // [删除图标动画 2026-09]

        int totalFiles = selected.Sum(c => c.Files.Count);
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = App.GetPopupTheme(),
            Title = L("Cleanup_ConfirmBatchDelete.Title", selected.Count),
            Content = L("Cleanup_ConfirmClean.Content", totalFiles),
            PrimaryButtonText = L("Cleanup_CommonDelete"),
            CloseButtonText = L("Cleanup_CommonCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        int failed = 0;
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
            catch { failed++; }
        }
        UpdateBatchButtons();

        if (failed > 0)
        {
            var err = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = App.GetPopupTheme(),
                Title = L("Cleanup_CleanFail.Title"),
                Content = L("Cleanup_BatchDeletePartialFail", failed),
                CloseButtonText = L("Cleanup_CommonOk")
            };
            await err.ShowAsync();
        }
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
        // 单实例守卫:窗口已开(未关闭)则聚焦已有窗口,不再叠加
        if (_whitelistWin != null)
        {
            try { _whitelistWin.Activate(); }
            catch { _whitelistWin = null; }
            return;
        }
        _whitelistWin = new WhitelistWindow(_whitelist, WorkshopPath);
        var win = _whitelistWin;
        // 白名单项被移除时立即把该壁纸加回列表
        win.WhitelistItemRemoved += id =>
        {
            DispatcherQueue.TryEnqueue(() => RescanCardForId(id));
        };
        // 窗口关闭后清引用 + 刷新列表(可能有其他变化);Scan 走后台线程,完成后回 UI 线程填集合
        win.Closed += async (_, _) =>
        {
            _whitelistWin = null;
            var list = await Task.Run(Scan);
            DispatcherQueue.TryEnqueue(() =>
            {
                Cards.Clear();
                foreach (var card in list)
                    Cards.Add(card);
                ApplySort();
                if (Cards.Count == 0)
                {
                    EmptyStateText.Text = L("Cleanup_NoResidue");
                    EmptyStateDesc.Visibility = Visibility.Visible;
                }
                SyncView();
            });
        };
        win.Activate();
    }

    private async void DeleteAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (Cards.Count == 0) return;
        AnimatedIconPlayer.PlayOnce(sender);   // [删除图标动画 2026-09]

        int totalFiles = 0;
        foreach (var c in Cards)
            totalFiles += c.Files.Count;

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = App.GetPopupTheme(),
            Title = L("Cleanup_ConfirmDeleteAll.Title"),
            Content = L("Cleanup_ConfirmDeleteAll.Content", Cards.Count, totalFiles),
            PrimaryButtonText = L("Cleanup_ConfirmDeleteAll.Ok"),
            CloseButtonText = L("Cleanup_CommonCancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        int ok = 0, failed = 0;
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
            catch { failed++; }
        }

        EmptyStateText.Text = L("Cleanup_CleanedComplete", ok);
        EmptyStateDesc.Visibility = Visibility.Collapsed; // 清理完成场景不显示"未发现残留"副描述
        SyncView();

        if (failed > 0)
        {
            var err = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = App.GetPopupTheme(),
                Title = L("Cleanup_CleanFail.Title"),
                Content = L("Cleanup_BatchDeletePartialFail", failed),
                CloseButtonText = L("Cleanup_CommonOk")
            };
            await err.ShowAsync();
        }
    }

    /// <summary>卡片数 → 列表/空态/按钮可用性的唯一同步点。空态文案由调用方先设。</summary>
    private void SyncView()
    {
        bool has = Cards.Count > 0;
        ResultScrollView.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        // [2026-09-22] 命令栏不再随列表清空而折叠(见 XAML):整条隐藏会让"白名单/重新扫描"
        // 再无入口——页面有缓存且 _initialScanDone 不再自动重扫,用户会被困在空态。
        // 需要卡片才能执行的按钮改为按项数禁用。
        DeleteAllButton.IsEnabled = has;
        UpdateBatchButtons();
        UpdateResultLayoutMinWidth();
        UpdateSummary();
        Log.Debug("[残留清理] 视图同步:卡片 {Count} 项,空态 {Empty},清理全部可用 {DeleteAllEnabled}",
            Cards.Count, !has, has);
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
        if (Cards.Count == 0)
        {
            // [2026-09] 卡被移空(白名单/单卡清理后):回到"无残留"空态文案
            EmptyStateText.Text = L("Cleanup_NoResidue");
            EmptyStateDesc.Visibility = Visibility.Visible;
        }
        SyncView();
    }

    // ---------- 列表键盘可达(2026-09-22) ----------

    /// <summary>容器就绪即把卡片设成 Tab/方向键停留点并给朗读名(与壁纸备份页同法)。</summary>
    private void ResultRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement card) return;

        // item 认定与 Papers 同法(DataContext 优先、回退 args.Index):ElementPrepared 时 DataContext 可能还没推送,
        // 壁纸备份页就是因为只认 DataContext,每次实化都提前 return,卡片从来没成为过 Tab 停留点。
        CleanupCardViewModel? vm = card.DataContext as CleanupCardViewModel
            ?? (args.Index >= 0 && args.Index < Cards.Count ? Cards[args.Index] : null);
        if (vm is null)
        {
            Log.Warning("[残留清理] 卡片第 {Index} 项取不到数据项,焦点停留点与朗读名均未设置", args.Index);
            return;
        }

        card.IsTabStop = true;
        card.UseSystemFocusVisuals = true;
        AutomationProperties.SetName(card, vm.FolderId);   // 朗读名用工坊 ID(非文案),不需要走 resw
        // 卡片根已念 FolderId,卡内那行 FolderId 文字设为 Raw,否则讲述人停在卡上按方向键会读两遍
        if (card.FindName("CardFolderIdText") is TextBlock folderIdText)
            AutomationProperties.SetAccessibilityView(folderIdText, AccessibilityView.Raw);
        else
            Log.Warning("[残留清理] 未取到卡片标题节点 CardFolderIdText,朗读去重未生效");
        // 勾选框没有文字内容(纯框),不给名字讲述人只会念"复选框";用同一个 FolderId 当它的朗读名
        if (card.FindName("CardSelectBox") is CheckBox selectBox)
            AutomationProperties.SetName(selectBox, vm.FolderId);

        card.GotFocus -= CleanupCard_GotFocus;   // 幂等:容器回收复用会重复走到这里
        card.GotFocus += CleanupCard_GotFocus;
    }

    /// <summary>记住"最后停留过的卡":Ctrl+L 再进列表时回到这里,而不是回列表头。</summary>
    private void CleanupCard_GotFocus(object sender, RoutedEventArgs e)
    {
        int idx = CardIndex(sender as UIElement);
        if (idx >= 0) _listAnchorIndex = idx;
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e) => Page_KeyDown_Core(e);

    /// <summary>供 MainWindow 在焦点不在本页子树内(例如停在外壳导航栏)时分发快捷键,与 Papers 同法。</summary>
    public void HandleShortcutKey(KeyRoutedEventArgs e) => Page_KeyDown_Core(e);

    private void Page_KeyDown_Core(KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.L or VirtualKey.Enter or VirtualKey.Space or VirtualKey.Escape)) return;
        // 读焦点必须用带 XamlRoot 的重载:无参版本在 WinUI 3 桌面恒返回 null
        var focused = FocusManager.GetFocusedElement(XamlRoot) as FrameworkElement;
        bool ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down)
            == CoreVirtualKeyStates.Down;

        if (e.Key == VirtualKey.L && ctrl)
        {
            if (FocusCleanupList()) e.Handled = true;
            return;
        }

        if (e.Key is VirtualKey.Enter or VirtualKey.Space && FindOwnCard(focused) is { } card)
        {
            if (EnterCardControls(card)) e.Handled = true;
            return;
        }

        // Esc 只在"焦点停在某张卡的控件上"时接管(退回卡片);别处的 Esc 原样交出去(关弹层/后退)
        // ButtonBase 覆盖这一页会用到 Enter 的三种控件:CheckBox(继承 ToggleButton)、Button、AppBarButton
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
            int idx = ResultRepeater.GetElementIndex(el);
            if (idx >= 0 && ReferenceEquals(ResultRepeater.TryGetElement(idx), el)) return idx;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[残留清理] 反查卡片下标异常");
        }
        for (int i = 0; i < Cards.Count; i++)
            if (ReferenceEquals(ResultRepeater.TryGetElement(i), el)) return i;
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

    /// <summary>Enter/空格的"深入一层":把焦点交给这张卡的第一个控件(勾选框),之后 Tab 在勾选框与三按钮间走。</summary>
    private bool EnterCardControls(FrameworkElement card)
    {
        // 顺序按 XAML 里的 Tab 序:勾选框 → 打开文件夹 → 加入白名单 → 删除
        ButtonBase? first = card.FindName("CardSelectBox") as ButtonBase
            ?? card.FindName("CardOpenFolderButton") as ButtonBase;
        if (first is null)
        {
            Log.Warning("[残留清理] 进入卡内控件失败: 第 {Index} 张卡里找不到 CardSelectBox/CardOpenFolderButton", CardIndex(card));
            return false;
        }
        return first.Focus(FocusState.Keyboard);
    }

    /// <summary>Ctrl+L 的落点:优先回到上次停留过的卡,其次第一张已实化的卡;都没有就写日志,不静默失败。</summary>
    private bool FocusCleanupList()
    {
        if (_listAnchorIndex >= 0 && _listAnchorIndex < Cards.Count
            && ResultRepeater.TryGetElement(_listAnchorIndex) is FrameworkElement anchor
            && anchor.Focus(FocusState.Keyboard)) return true;
        if (FocusFirstRealizedCard()) return true;

        Log.Warning("[残留清理] Ctrl+L 未找到可聚焦的卡片(列表为空或容器全部未实化)");
        return false;
    }

    private bool FocusFirstRealizedCard()
    {
        for (int i = 0; i < Cards.Count; i++)
        {
            if (ResultRepeater.TryGetElement(i) is FrameworkElement card && card.Focus(FocusState.Keyboard))
            {
                _listAnchorIndex = i;
                return true;
            }
        }
        return false;
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

    private static string FormatSize(long b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1048576) return $"{b / 1024.0:F1} KB";
        if (b < 1073741824) return $"{b / 1048576.0:F1} MB";
        return $"{b / 1073741824.0:F2} GB";
    }
}
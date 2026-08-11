using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Models;
using WE_Tool.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace WE_Tool;

public sealed partial class InstalledComponents : Page, INotifyPropertyChanged
{
    private List<ComponentInfo> _allComponents = [];
    private string _searchText = "";
    private bool _isUpdating;
    private bool _isLeftMouseButtonPressed;
    private bool _isComponentItemTapped;
    private bool _isMultiSelectMode;
    private bool _isBatchUpdating;
    private FrameworkElement? _rightClickedComponentElement;
    private DateTime _lastDrillInAnimationTime;
    private CancellationTokenSource? _filterCts;
    private readonly Service.PickerService _pickerService = new();
    /// <summary>导航离开时属性面板正打开的选中项（返回后恢复，对齐 Papers）</summary>
    private ComponentInfo? _restorePropertiesComponent;

    public SettingsViewModel ViewModel { get; }
    public ObservableCollection<ComponentInfo> FilteredComponents { get; } = [];
    public ObservableCollection<ComponentInfo> SelectedComponents { get; } = [];
    public ObservableCollection<ComponentInfo> DisplayedSelectedComponents { get; } = [];
    private List<ComponentInfo> _filteredComponents = [];
    private int _currentPage = 1;

    /// <summary>当前页码（1 起）</summary>
    public int CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (_currentPage == value) return;
            _currentPage = value;
            NotifyPagerStateChanged();
        }
    }

    public bool ComponentsCanGoPrevious => CurrentPage > 1;

    public bool ComponentsCanGoNext => CurrentPage < ComputeTotalPages(_filteredComponents.Count);

    private int ComputeTotalPages(int itemCount)
    {
        int size = ViewModel.ComponentsDisplayVM.PageSize;
        if (size <= 0) size = 30;
        return Math.Max(1, (int)Math.Ceiling(itemCount / (double)size));
    }

    private void NotifyPagerStateChanged()
    {
        OnPropertyChanged(nameof(ComponentsCanGoPrevious));
        OnPropertyChanged(nameof(ComponentsCanGoNext));
        RebuildComponentsPageNumberButtons();
    }

    /// <summary>重建底部翻页栏的页码按钮（当前页高亮，超出窗口显示省略号；照抄 Papers）</summary>
    private void RebuildComponentsPageNumberButtons()
    {
        if (ComponentsPageNumbersPanel == null) return;
        ComponentsPageNumbersPanel.Children.Clear();

        int total = ComputeTotalPages(_filteredComponents.Count);
        var subtle = Application.Current.Resources["SubtleButtonStyle"] as Style;
        var accent = Application.Current.Resources["AccentButtonStyle"] as Style;

        foreach (int page in GetVisiblePages(CurrentPage, total))
        {
            if (page < 0)
            {
                // 省略号分隔
                ComponentsPageNumbersPanel.Children.Add(new TextBlock
                {
                    Text = "…",
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 14,
                    Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush
                                 ?? new SolidColorBrush(Microsoft.UI.Colors.Gray)
                });
                continue;
            }

            var button = new Button
            {
                Content = page.ToString(),
                Tag = page,
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                FontSize = 13,
                Style = page == CurrentPage ? accent : subtle
            };
            button.Click += ComponentsPageNumber_Click;
            ComponentsPageNumbersPanel.Children.Add(button);
        }
    }

    /// <summary>页码窗口：始终含首页/末页，当前页 ±2，中间用负数占位表示省略号（照抄 Papers）</summary>
    private static IEnumerable<int> GetVisiblePages(int current, int total)
    {
        if (total <= 1) return [1];

        var pages = new List<int>();
        pages.Add(1);

        int start = Math.Max(2, current - 2);
        int end = Math.Min(total - 1, current + 2);

        if (start > 2) pages.Add(-1);   // 左省略号
        for (int i = start; i <= end; i++) pages.Add(i);
        if (end < total - 1) pages.Add(-2);  // 右省略号
        if (total > 1) pages.Add(total);

        return pages;
    }

    private void ComponentsPageNumber_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int page })
        {
            GoToPage(page);
        }
    }

    private void ComponentsPrevPage_Click(object sender, RoutedEventArgs e)
    {
        GoToPage(CurrentPage - 1);
    }

    private void ComponentsNextPage_Click(object sender, RoutedEventArgs e)
    {
        GoToPage(CurrentPage + 1);
    }

    /// <summary>跳转到指定页并重填列表（分页模式；照抄 Papers.GoToPage）</summary>
    private void GoToPage(int page)
    {
        int totalPages = ComputeTotalPages(_filteredComponents.Count);
        page = Math.Clamp(page, 1, totalPages);
        if (page == CurrentPage) return;

        CurrentPage = page;
        var pageItems = GetCurrentPageItems(_filteredComponents);
        FilteredComponents.Clear();
        foreach (var item in pageItems)
        {
            FilteredComponents.Add(item);
        }
        ComponentsScrollView.ChangeView(0, 0, null);
    }

    /// <summary>取当前页应显示的组件；分页关闭时返回完整列表（照抄 Papers）</summary>
    private List<ComponentInfo> GetCurrentPageItems(List<ComponentInfo> source)
    {
        if (!ViewModel.ComponentsDisplayVM.PaginationEnabled) return source;
        int size = ViewModel.ComponentsDisplayVM.PageSize;
        if (size <= 0) size = 30;
        int skip = (CurrentPage - 1) * size;
        return source.Skip(skip).Take(size).ToList();
    }

    private ComponentInfo? _selectedComponent;
    public ComponentInfo? SelectedComponent
    {
        get => _selectedComponent;
        set
        {
            if (Set(ref _selectedComponent, value))
            {
                UpdateDetailPanel();
                OnPropertyChanged(nameof(IsComponentButtonEnabled));
            }
        }
    }

    /// <summary>有选中项时顶部栏按钮才可用（照抄 Papers.IsButtonInGridColumnEnabled）</summary>
    public bool IsComponentButtonEnabled
        => SelectedComponents.Count > 0 || SelectedComponent != null;

    public bool IsMultiSelectMode
    {
        get => _isMultiSelectMode;
        set
        {
            if (_isMultiSelectMode != value)
            {
                _isMultiSelectMode = value;
                OnPropertyChanged();

                if (FilteredComponents != null)
                {
                    foreach (var item in FilteredComponents)
                        item.IsInMultiSelectMode = value;
                }
                UpdateStackVisuals();
                _ = ToggleMultiSelectVisuals(_isMultiSelectMode);
                UpdateAllVisibleCheckBoxes();
            }
        }
    }

    public InstalledComponents()
    {
        this.InitializeComponent();

        var app = Application.Current as App;
        ViewModel = app?.ViewModel ?? new SettingsViewModel(new Service.ConfigService(), new Service.PickerService());
        // 让角标可见性等 {Binding ... ElementName=PageRoot} 能解析到 ViewModel（照抄 Papers）
        this.DataContext = this;

        // 全局跟踪鼠标按下状态，用于拖拽滑过多选
        this.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Global_PointerPressed), true);
        this.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Global_PointerReleased), true);
        this.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Global_PointerReleased), true);

        ViewModel.ComponentsFilterVM.PropertyChanged += (s, e) =>
        {
            // 批量操作（全选/无/重置/右键全选反选）期间跳过中间事件，结束后统一触发一次
            if (_isUpdating || ViewModel._isBatchUpdating) return;
            _ = ApplyFilters();
        };

        // ViewModel 批量方法结束时只发一次 ComponentsFilterVM 通知，在这里统一响应（照抄 Papers）
        ViewModel.PropertyChanged += (s, e) =>
        {
            if (ViewModel._isBatchUpdating) return;
            if (e.PropertyName == nameof(SettingsViewModel.ComponentsFilterVM))
                _ = ApplyFilters();
        };

        ViewModel.ComponentsDisplayVM.PropertyChanged += (s, e) =>
        {
            if (_isUpdating) return;
            if (e.PropertyName == nameof(ComponentsDisplayViewModel.AutoPlayGif))
            {
                // 仅刷新本页可见动图，不清其它页面缓存（方案 A：页面订阅 VM 变化自刷新）
                UiHelper.ReloadGifImages(this);
                return;
            }
            if (e.PropertyName == nameof(ComponentsDisplayViewModel.SortOrder)
                || e.PropertyName == nameof(ComponentsDisplayViewModel.IsSortAscending))
            {
                _ = ApplyFilters();
            }
            else if (e.PropertyName == nameof(ComponentsDisplayViewModel.PaginationMode))
            {
                // 分页开关/每页数量变化：立即刷新翻页栏状态（ApplyFilters 有延迟，先同步一次；照抄 Papers）
                NotifyPagerStateChanged();
                _ = ApplyFilters();
            }
            else if (e.PropertyName == nameof(ComponentsDisplayViewModel.LeftSplitViewPaneOpen)
                     || e.PropertyName == nameof(ComponentsDisplayViewModel.RightSplitViewPaneOpen))
            {
                ApplyPaneState();
            }
        };

        // 多选集合变化时刷新计数、堆叠图与面板（批量操作时抑制，避免逐项触发）
        SelectedComponents.CollectionChanged += (s, e) =>
        {
            if (_isBatchUpdating) return;
            RefreshDisplayedSelectedComponents();
            UpdateStackVisuals();
            UpdateMultiSelectCount();
            OnPropertyChanged(nameof(IsComponentButtonEnabled));
        };
    }

    // ===================== 全局鼠标状态 =====================
    private void Global_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(null).Properties;
        if (props.IsLeftButtonPressed)
        {
            _isLeftMouseButtonPressed = true;
        }
    }

    private void Global_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isLeftMouseButtonPressed = false;
    }

    // ===================== INotifyPropertyChanged =====================
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ===================== 生命周期 =====================
    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = LoadComponents();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // 页面缓存模式下返回时 LoadComponents 会清空 SelectedComponent；
        // 若属性面板正打开，记录选中项以便返回后恢复（对齐 Papers：切页回来面板保持打开且有内容）
        if (PropertiesOverlay.Visibility == Visibility.Visible && SelectedComponent != null)
        {
            _restorePropertiesComponent = SelectedComponent;
        }
    }

    private async Task LoadComponents()
    {
        try
        {
            // 等待初始扫描链路完成（读配置 → 启动扫描 → 扫描完成），确保 LastComponents 已填充。
            // 注意：不能只 await App.ScanTask —— 启动时它可能还是 Task.CompletedTask
            //（ScanWallpaperWhenStart 需先读完配置才赋值），会导致拿到空数据。
            if (App.InitialScanTask != null)
            {
                await App.InitialScanTask;
            }
            else if (App.ScanTask.IsCompleted && WallpaperScanner.LastComponents == null)
            {
                // 无初始扫描链路兜底：主动触发一次扫描
                App.StartBackgroundScan(
                    ViewModel.PathManagementVM.WorkshopPath,
                    ViewModel.PathManagementVM.OfficialPath,
                    ViewModel.PathManagementVM.ProjectPath,
                    ViewModel.PathManagementVM.AcfPath,
                    ViewModel.PathManagementVM.VdfPath,
                    ViewModel.AppSettingsVM.ScanCacheEnabled == "1");
            }
            await App.ScanTask;

            _isUpdating = true;

            var components = WallpaperScanner.LastComponents;
            _allComponents = components ?? [];

            _isUpdating = false;

            // 清理多选状态（照抄 Papers.RefreshWallpaperList）
            foreach (var item in SelectedComponents)
                item.IsSelected = false;
            SelectedComponents.Clear();
            DisplayedSelectedComponents.Clear();
            IsMultiSelectMode = false;
            SelectedComponent = null;

            // 不再先清空显示列表:ApplyFilters 内部有"结果未变化则跳过"的优化(IsComponentListEqual),
            // 先 Clear 会把比较对象清空,导致切页回来(内容没变)也全量重建,观感像"没有缓存"。
            // 内容真变化时 ApplyFilters 内部照常 Clear + 重填。
            ApplyPaneState();
            await ApplyFilters();

            // 恢复导航前打开的属性面板（对齐 Papers：切页回来面板保持打开且有内容）
            if (_restorePropertiesComponent != null)
            {
                var saved = _restorePropertiesComponent;
                _restorePropertiesComponent = null;

                var match = _allComponents.FirstOrDefault(c =>
                    c.FolderPath == saved.FolderPath);
                if (match != null)
                {
                    SelectedComponent = match;
                    PropertiesOverlay.Visibility = Visibility.Visible;
                    _ = PopulateFileTreeAsync(match.FolderPath ?? "");
                }
                else
                {
                    // 组件已不存在（被删除/路径变化），面板随之关闭
                    PropertiesOverlay.Visibility = Visibility.Collapsed;
                }
            }

            Log.Information("已加载 {Count} 个组件", _allComponents.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载组件失败");
        }
    }

    private void ApplyPaneState()
    {
        if (LeftSplitView != null)
            LeftSplitView.IsPaneOpen = ViewModel.ComponentsDisplayVM.LeftSplitViewPaneOpen;
        if (RightSplitView != null)
            RightSplitView.IsPaneOpen = ViewModel.ComponentsDisplayVM.RightSplitViewPaneOpen;
    }

    // ===================== 详情面板 =====================
    private void UpdateDetailPanel()
    {
        // 多选模式：显示堆叠图 + 多选面板
        if (IsMultiSelectMode || SelectedComponents.Count > 0)
        {
            StackedImagesControl.Visibility = DisplayedSelectedComponents.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            SinglePreviewBorder.Visibility = Visibility.Collapsed;
            SingleSelectionInfoPanel.Visibility = Visibility.Collapsed;
            MultiSelectionInfoPanel.Visibility = Visibility.Visible;
            NoSelectionHintText.Visibility = Visibility.Collapsed;
            MultiSelectCountText.Text = $"已选择 {SelectedComponents.Count} 项";
            return;
        }

        // 单选模式
        if (SelectedComponent is ComponentInfo item)
        {
            SinglePreviewBorder.Visibility = Visibility.Visible;
            ComponentPreviewImage.Source = new BitmapImage(new Uri(item.Preview ?? "ms-appx:///Assets/NoPreview.png"));
            ComponentTitle.Text = item.Title ?? "";
            ComponentFolderPath.Text = item.FolderPath ?? "";
            ComponentDescription.Text = string.IsNullOrEmpty(item.Description)
                ? "无描述"
                : item.Description;

            // 元信息行
            ComponentFileSizeText.Text = new Converters.FileSizeToString()
                .Convert(item.FileSize, null, "", "")?.ToString() ?? "";
            ComponentTypeText.Text = new Converters.ComponentTypeToDisplay()
                .Convert(item.ComponentType, null, "", "")?.ToString() ?? "";
            ComponentRatingText.Text = new Converters.RatingToDisplay()
                .Convert(item.ContentRating ?? "Everyone", null, "", "")?.ToString() ?? "";

            // 标签徽章
            bool hasTags = !string.IsNullOrEmpty(item.Tags) && item.Tags != "Unspecified";
            ComponentTagsBorder.Visibility = hasTags ? Visibility.Visible : Visibility.Collapsed;
            if (hasTags)
            {
                ComponentTagsText.Text = new Converters.TagToDisplay()
                    .Convert(item.Tags, null, "", "")?.ToString() ?? item.Tags!;
            }

            SingleSelectionInfoPanel.Visibility = Visibility.Visible;
            MultiSelectionInfoPanel.Visibility = Visibility.Collapsed;
            StackedImagesControl.Visibility = Visibility.Collapsed;
            NoSelectionHintText.Visibility = Visibility.Collapsed;
        }
        else
        {
            SinglePreviewBorder.Visibility = Visibility.Collapsed;
            StackedImagesControl.Visibility = Visibility.Collapsed;
            SingleSelectionInfoPanel.Visibility = Visibility.Collapsed;
            MultiSelectionInfoPanel.Visibility = SelectedComponents.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoSelectionHintText.Visibility = SelectedComponent == null && SelectedComponents.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ===================== 筛选逻辑 =====================
    private async Task ApplyFilters()
    {
        if (_isUpdating) return;

        _filterCts?.Cancel();
        _filterCts?.Dispose();
        _filterCts = new CancellationTokenSource();
        var token = _filterCts.Token;

        try
        {
            await Task.Delay(ViewModel.ComponentsDisplayVM.FilterResultResponseDelay, token);

            var filter = ViewModel.ComponentsFilterVM;

            // 先捕获筛选状态（UI 线程），再在后台线程执行查询，避免大列表阻塞界面（照抄 Papers）
            var activeTypes = new HashSet<string>();
            if (filter.Layers) activeTypes.Add("Layers");
            if (filter.Scripts) activeTypes.Add("scripts");
            if (filter.Effects) activeTypes.Add("effects");

            var activeRatings = new HashSet<string>();
            if (filter.Everyone) activeRatings.Add("Everyone");
            if (filter.Questionable) activeRatings.Add("Questionable");
            if (filter.Mature) activeRatings.Add("Mature");

            var activeTags = GetActiveTags();
            var searchText = _searchText;
            var sortOrder = ViewModel.ComponentsDisplayVM.SortOrder;
            var isSortAscending = ViewModel.ComponentsDisplayVM.IsSortAscending;

            var filteredResult = await Task.Run(() =>
            {
                var filtered = _allComponents.AsEnumerable();

                // 类型
                filtered = filtered.Where(c =>
                {
                    var typeName = c.ComponentType switch
                    {
                        ComponentType.Layer => "Layers",
                        ComponentType.Script => "scripts",
                        ComponentType.Effect => "effects",
                        _ => ""
                    };
                    return activeTypes.Contains(typeName);
                });

                // 年龄
                filtered = filtered.Where(c =>
                    activeRatings.Contains(c.ContentRating ?? "Everyone"));

                // 标签：无勾选时不显示任何组件（与 Papers 一致）
                filtered = filtered.Where(c =>
                {
                    if (string.IsNullOrEmpty(c.Tags)) return false;
                    return activeTags.Any(tag =>
                        c.Tags!.Contains(tag, StringComparison.OrdinalIgnoreCase));
                });

                // 搜索
                if (!string.IsNullOrEmpty(searchText))
                {
                    filtered = filtered.Where(c =>
                        c.Title?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true);
                }

                // 排序（索引与 Papers 同步：0名称 1订阅时间 2最后使用 3文件大小 4ACF更新时间）
                filtered = sortOrder switch
                {
                    0 => isSortAscending
                       ? filtered.OrderBy(c => c.Title ?? "")
                       : filtered.OrderByDescending(c => c.Title ?? ""),
                    1 => isSortAscending
                       ? filtered.OrderBy(c => c.CreationTime)
                       : filtered.OrderByDescending(c => c.CreationTime),
                    2 => isSortAscending
                       ? filtered.OrderBy(c => c.InstallDate)
                       : filtered.OrderByDescending(c => c.InstallDate),
                    3 => isSortAscending
                       ? filtered.OrderBy(c => c.FileSize)
                       : filtered.OrderByDescending(c => c.FileSize),
                    4 => isSortAscending
                       ? filtered.OrderBy(c => c.AcfUpdateTime)
                       : filtered.OrderByDescending(c => c.AcfUpdateTime),
                    _ => filtered
                };

                return filtered.ToList();
            }, token);

            if (token.IsCancellationRequested) return;

            // 未扫描到任何组件：显示引导并结束（对齐 Papers 独立分支）
            if (_allComponents.Count == 0)
            {
                NoScanResultTip.Visibility = Visibility.Visible;
                NoResultTip.Visibility = Visibility.Collapsed;
                return;
            }

            // === 分页 ===
            bool listUnchanged = IsComponentListEqual(_filteredComponents, filteredResult);
            _filteredComponents = filteredResult;

            // 筛选/排序变化后回到第一页
            if (!listUnchanged) CurrentPage = 1;
            // 每页数量变小等情况下钳制页码
            int totalPages = ComputeTotalPages(_filteredComponents.Count);
            if (CurrentPage > totalPages) CurrentPage = totalPages;
            NotifyPagerStateChanged();

            var pageItems = GetCurrentPageItems(_filteredComponents);
            // 结果未变化时跳过，避免 Clear + 逐项 Add 的布局风暴（照抄 Papers.IsListEqual）
            if (listUnchanged && IsComponentListEqual(FilteredComponents, pageItems)) return;

            FilteredComponents.Clear();

            // 筛选无结果时显示提示（未扫描到组件的引导已在上方独立分支处理）
            NoResultTip.Visibility = filteredResult.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;

            // 填充当前页（分页模式每页最多 90 项，无需分批；照抄 Papers）
            var uiQueue = DispatcherQueue;
            uiQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested) return;
                foreach (var item in pageItems)
                    FilteredComponents.Add(item);
            });
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>比较当前结果与新一轮筛选结果是否一致（照抄 Papers.IsListEqual）</summary>
    private static bool IsComponentListEqual(IReadOnlyList<ComponentInfo> current, IReadOnlyList<ComponentInfo> next)
    {
        if (current.Count != next.Count) return false;
        for (int i = 0; i < current.Count; i++)
        {
            if (current[i].FolderPath != next[i].FolderPath) return false;
        }
        return true;
    }

    private HashSet<string> GetActiveTags()
    {
        var f = ViewModel.ComponentsFilterVM;
        var tags = new HashSet<string>();
        if (f.UnspecifiedGenre) { tags.Add("Unspecified genre"); tags.Add("Unspecified"); }
        if (f.Abstract) tags.Add("Abstract");
        if (f.Anime) tags.Add("Anime");
        if (f.AudioVisualizer) tags.Add("Audio visualizer");
        if (f.Background) tags.Add("Background");
        if (f.Cgi) tags.Add("CGI");
        if (f.Character) tags.Add("Character");
        if (f.Clock) tags.Add("Clock");
        if (f.Fire) tags.Add("Fire");
        if (f.Interactive) tags.Add("Interactive");
        if (f.Magic) tags.Add("Magic");
        if (f.Memes) tags.Add("Memes");
        if (f.Nature) tags.Add("Nature");
        if (f.PostProcessing) tags.Add("Post-processing");
        if (f.Smoke) tags.Add("Smoke");
        if (f.Space) tags.Add("Space");
        if (f.Sports) tags.Add("Sports");
        if (f.Technology) tags.Add("Technology");
        if (f.Vehicle) tags.Add("Vehicle");
        return tags;
    }

    // ===================== 按钮事件 =====================
    private void ResetFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetComponentsFilters();
        _searchText = "";
        ComponentSearchBox.Text = "";
        _ = ApplyFilters();
    }

    private void SelectAllTags_Click(object sender, RoutedEventArgs e)
    {
        // SetAllComponentTags 内部是批量操作，结束后通过 ViewModel.PropertyChanged 统一触发一次筛选
        ViewModel.SetAllComponentTags(true);
    }

    private void DeselectAllTags_Click(object sender, RoutedEventArgs e)
    {
        // SetAllComponentTags 内部是批量操作，结束后通过 ViewModel.PropertyChanged 统一触发一次筛选
        ViewModel.SetAllComponentTags(false);
    }

    private void ComponentSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _searchText = sender.Text ?? "";
        _ = ApplyFilters();
    }

    private void RightToggleFilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle)
            RightSplitView.IsPaneOpen = toggle.IsChecked == true;
    }

    private void SortDirectionToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ComponentsDisplayVM.IsSortAscending = !ViewModel.ComponentsDisplayVM.IsSortAscending;
    }

    // ===================== Expander ContextFlyout =====================
    private Expander? _currentFilterExpander;

    private void FilterExpanderContextMenu_Opening(object sender, object e)
    {
        if (sender is MenuFlyout flyout)
            _currentFilterExpander = flyout.Target as Expander;
    }

    private void FilterExpanderSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFilterExpander == null) return;
        // 右键全选：批处理期间抑制逐项 PropertyChanged，结束后只触发一次筛选（照抄 Papers）
        ViewModel._isBatchUpdating = true;
        try
        {
            SetExpandCheckBoxes(_currentFilterExpander, true);
        }
        finally
        {
            ViewModel._isBatchUpdating = false;
        }
        _ = ApplyFilters();
    }

    private void FilterExpanderInvert_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFilterExpander == null) return;
        // 右键反选：批处理期间抑制逐项 PropertyChanged，结束后只触发一次筛选（照抄 Papers）
        ViewModel._isBatchUpdating = true;
        try
        {
            SetExpandCheckBoxes(_currentFilterExpander, null);
        }
        finally
        {
            ViewModel._isBatchUpdating = false;
        }
        _ = ApplyFilters();
    }

    private static void SetExpandCheckBoxes(Expander expander, bool? isChecked)
    {
        if (expander.Content is not Panel panel) return;
        foreach (var child in panel.Children)
        {
            if (child is CheckBox cb)
            {
                cb.IsChecked = isChecked switch
                {
                    true => true,
                    false => false,
                    _ => !cb.IsChecked
                };
            }
        }
    }

    // ===================== 列表交互 =====================
    /// <summary>隐藏右键菜单（照抄 Papers.HideWallpaperContextMenu）</summary>
    public void HideComponentContextMenu()
    {
        ComponentContextMenuFlyout?.Hide();
    }

    /// <summary>获取操作目标：多选时返回全部选中项，否则返回单选（照抄 Papers 模式）</summary>
    private List<ComponentInfo> GetTargetItems()
        => SelectedComponents.Count > 0
            ? SelectedComponents.ToList()
            : SelectedComponent is not null ? [SelectedComponent] : [];

    private bool _isRefreshing;

    private async void ComponentsRefresh_Click(object sender, RoutedEventArgs e)
    {
        // 防连按：刷新进行中时忽略再次触发（按钮已禁用，F5/菜单入口由此兜底）
        if (_isRefreshing) return;
        _isRefreshing = true;
        RefreshButton.IsEnabled = false;
        HideComponentContextMenu();

        try
        {
            // 触发后台扫描（更新 WallpaperScanner.LastComponents），完成后重新加载
            App.StartBackgroundScan(
                ViewModel.PathManagementVM.WorkshopPath,
                ViewModel.PathManagementVM.OfficialPath,
                ViewModel.PathManagementVM.ProjectPath,
                ViewModel.PathManagementVM.AcfPath,
                ViewModel.PathManagementVM.VdfPath,
                ViewModel.AppSettingsVM.ScanCacheEnabled == "1");
            await App.ScanTask;
            await LoadComponents();
        }
        finally
        {
            _isRefreshing = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private async void CopyComponent_Click(object sender, RoutedEventArgs e)
    {
        var items = GetTargetItems();
        if (items.Count == 0) return;

        var folders = new List<Windows.Storage.StorageFolder>();
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.FolderPath)) continue;
            try
            {
                folders.Add(await Windows.Storage.StorageFolder.GetFolderFromPathAsync(item.FolderPath));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "获取组件文件夹失败: {Path}", item.FolderPath);
            }
        }

        if (folders.Count == 0) return;

        var dataPackage = new DataPackage();
        dataPackage.RequestedOperation = DataPackageOperation.Copy;
        dataPackage.SetStorageItems(folders);
        Clipboard.SetContent(dataPackage);
    }

    private async void ExtractComponent_Click(object sender, RoutedEventArgs e)
    {
        var items = GetTargetItems();
        if (items.Count == 0) return;

        var downloadPath = ViewModel.PathManagementVM.DownloadPath;
        if (string.IsNullOrEmpty(downloadPath))
        {
            downloadPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "WE_OutPut");
        }

        try
        {
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.FolderPath)) continue;

                var targetDir = Path.Combine(downloadPath, item.Title ?? item.WorkshopID ?? "Component");
                if (Directory.Exists(targetDir))
                {
                    bool confirmed = await Helper.DialogHelper.ShowConfirmDialogAsync("提取",
                        $"目标目录已存在：\n{targetDir}\n\n是否覆盖？", "覆盖", "取消");
                    if (!confirmed) continue;
                    Directory.Delete(targetDir, true);
                }

                Directory.CreateDirectory(targetDir);
                foreach (var file in Directory.EnumerateFiles(item.FolderPath))
                {
                    File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
                }
                Log.Information("组件 {Title} 已提取到 {Path}", item.Title, targetDir);
            }

            await Helper.DialogHelper.ShowMessageAsync("提取完成",
                $"已提取 {items.Count} 个组件到：\n{downloadPath}");
        }
        catch (Exception ex)
        {
            await Helper.DialogHelper.ShowMessageAsync("提取失败", ex.Message);
            Log.Error(ex, "提取组件失败");
        }
    }

    private async void UnsubscribeComponent_Click(object sender, RoutedEventArgs e)
    {
        // 照抄 Papers：执行前先收起右键菜单，避免菜单停留在确认对话框上方
        HideComponentContextMenu();

        var items = GetTargetItems().Where(i => !string.IsNullOrEmpty(i.WorkshopID)).ToList();
        if (items.Count == 0) return;

        bool confirmed = await Helper.DialogHelper.ShowConfirmDialogAsync(
            "取消订阅",
            $"确定要取消订阅选中的 {items.Count} 个组件吗？\n\n操作将同步删除本地的组件文件。",
            "确定",
            "取消");
        if (!confirmed) return;

        var service = Service.SteamWorkshopService.GetInstance();
        if (!service.IsAvailable)
        {
            await Helper.DialogHelper.ShowMessageAsync(
                "Steamworks 初始化失败",
                "无法连接到 Steam，请确认 Steam 已在运行。\n\n如果问题持续，请尝试以管理员身份运行本程序。");
            return;
        }

        int success = 0;
        foreach (var item in items)
        {
            if (ulong.TryParse(item.WorkshopID, out var wid) && await service.UnsubscribeAsync(wid))
                success++;
        }

        await Helper.DialogHelper.ShowMessageAsync("取消订阅完成",
            $"成功向 Steam 发送取消订阅请求: {success}/{items.Count} 个组件。\n\n正在同步删除本地组件文件...");

        foreach (var item in items)
        {
            await DeleteComponentCoreAsync(item, skipConfirm: true);
        }
    }

    private async void DeleteComponent_Click(object sender, RoutedEventArgs e)
    {
        var items = GetTargetItems();
        if (items.Count == 0) return;

        bool confirmed = await Helper.DialogHelper.ShowConfirmDialogAsync("删除组件",
            $"确定要删除选中的 {items.Count} 个组件吗？",
            "删除",
            "取消");
        if (!confirmed) return;

        foreach (var item in items)
        {
            await DeleteComponentCoreAsync(item, skipConfirm: items.Count > 1);
        }
    }

    private async Task DeleteComponentCoreAsync(ComponentInfo item, bool skipConfirm = false)
    {
        if (item == null || item.WorkshopID == null || item.FolderPath == null) return;

        try
        {
            await ViewModel.PathManagementVM.RemoveWorkshopKeyFromAcfAsync(item.WorkshopID, ViewModel.PathManagementVM.AcfPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "从 ACF 移除组件 {Title} 的键失败", item.Title);
        }

        bool isFolderDeleted = await _pickerService.DeleteFolderAsync(item.FolderPath);
        if (isFolderDeleted)
        {
            _allComponents.Remove(item);
            _filteredComponents.Remove(item);
            FilteredComponents.Remove(item);
            SelectedComponents.Remove(item);
            if (SelectedComponent == item) SelectedComponent = null;

            // 当前页被删空且不是第一页时回退一页（分页模式）
            if (FilteredComponents.Count == 0 && CurrentPage > 1)
            {
                CurrentPage--;
                foreach (var it in GetCurrentPageItems(_filteredComponents))
                {
                    FilteredComponents.Add(it);
                }
            }
            NotifyPagerStateChanged();

            UpdateMultiSelectCount();
            Log.Information("组件 {Title} 已从列表和磁盘中彻底移除", item.Title);
        }
    }

    private async void OpenComponentFolder_Click(object sender, RoutedEventArgs e)
    {
        var items = GetTargetItems();
        if (items.Count == 0) return;

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.FolderPath)) continue;

            if (!Directory.Exists(item.FolderPath))
            {
                await Helper.DialogHelper.ShowMessageAsync("打开目录", "目录不存在：" + item.FolderPath);
                continue;
            }

            try
            {
                await Launcher.LaunchFolderPathAsync(item.FolderPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "打开组件目录失败: {Path}", item.FolderPath);
            }
        }
    }

    private void ComponentProperties_Click(object sender, RoutedEventArgs e)
    {
        ShowProperties();
    }

    // ===================== 属性面板（照抄 Papers） =====================
    private void ShowProperties()
    {
        HideComponentContextMenu();
        if (SelectedComponent != null)
        {
            PropertiesOverlay.Visibility = Visibility.Visible;
            // 等一帧让布局完成，然后启动动画
            _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                // 用页面根 ActualHeight 而非 Overlay 的——overlay 刚从 Collapsed 变 Visible，
                // 布局可能尚未运行（ActualHeight=0），会把 MaxHeight 永久钉成 0 导致面板第一次打不开
                PropertiesPanel.MaxHeight = Math.Max(0, ActualHeight * 0.85);
                PropertiesPanel.UpdateLayout();
                AnimatePropertiesPanelOpen();
                _ = PopulateFileTreeAsync(SelectedComponent!.FolderPath ?? "");
            });
        }
    }

    private void AnimatePropertiesPanelOpen()
        => AnimatePanelOpen(PropertiesPanel, PropertiesOverlayBackground);

    private void AnimatePropertiesPanelClose(Action onCompleted)
        => AnimatePanelClose(PropertiesPanel, PropertiesOverlayBackground, () =>
        {
            PropertiesOverlay.Visibility = Visibility.Collapsed;
            onCompleted?.Invoke();
        });

    private void PropertiesOverlayBackground_Tapped(object sender, TappedRoutedEventArgs e)
        => AnimatePropertiesPanelClose(() => { });

    private void PropertiesCloseButton_Click(object sender, RoutedEventArgs e)
        => AnimatePropertiesPanelClose(() => { });

    private static void AnimatePanelOpen(FrameworkElement panel, FrameworkElement background)
    {
        var panelVisual = ElementCompositionPreview.GetElementVisual(panel);
        var backgroundVisual = ElementCompositionPreview.GetElementVisual(background);
        var compositor = panelVisual.Compositor;

        panelVisual.Opacity = 0f;
        panelVisual.Scale = new Vector3(0.85f, 0.85f, 1f);
        panelVisual.CenterPoint = new Vector3(
            (float)(panel.ActualWidth / 2),
            (float)(panel.ActualHeight / 2), 0f);

        var bgFadeIn = compositor.CreateScalarKeyFrameAnimation();
        bgFadeIn.InsertKeyFrame(0f, 0f);
        bgFadeIn.InsertKeyFrame(1f, 1f);
        bgFadeIn.Duration = TimeSpan.FromMilliseconds(200);

        var scaleAnim = compositor.CreateSpringVector3Animation();
        scaleAnim.Target = "Scale";
        scaleAnim.FinalValue = new Vector3(1f, 1f, 1f);
        scaleAnim.DampingRatio = 0.6f;
        scaleAnim.Period = TimeSpan.FromMilliseconds(50);

        var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.InsertKeyFrame(0f, 0f);
        opacityAnim.InsertKeyFrame(1f, 1f);
        opacityAnim.Duration = TimeSpan.FromMilliseconds(200);

        backgroundVisual.StartAnimation("Opacity", bgFadeIn);
        panelVisual.StartAnimation("Scale", scaleAnim);
        panelVisual.StartAnimation("Opacity", opacityAnim);
    }

    private static void AnimatePanelClose(FrameworkElement panel, FrameworkElement background, Action onCompleted)
    {
        var panelVisual = ElementCompositionPreview.GetElementVisual(panel);
        var backgroundVisual = ElementCompositionPreview.GetElementVisual(background);
        var compositor = panelVisual.Compositor;

        var bgFadeOut = compositor.CreateScalarKeyFrameAnimation();
        bgFadeOut.InsertKeyFrame(0f, 1f);
        bgFadeOut.InsertKeyFrame(1f, 0f);
        bgFadeOut.Duration = TimeSpan.FromMilliseconds(150);

        var scaleAnim = compositor.CreateScalarKeyFrameAnimation();
        scaleAnim.Target = "Scale.X";
        scaleAnim.InsertKeyFrame(0f, 1f);
        scaleAnim.InsertKeyFrame(1f, 0.85f);
        scaleAnim.Duration = TimeSpan.FromMilliseconds(150);

        var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.InsertKeyFrame(0f, 1f);
        opacityAnim.InsertKeyFrame(1f, 0f);
        opacityAnim.Duration = TimeSpan.FromMilliseconds(150);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        batch.Completed += (s, e) => onCompleted?.Invoke();

        backgroundVisual.StartAnimation("Opacity", bgFadeOut);
        panelVisual.StartAnimation("Scale.X", scaleAnim);
        panelVisual.StartAnimation("Scale.Y", scaleAnim);
        panelVisual.StartAnimation("Opacity", opacityAnim);

        batch.End();
    }

    // ===================== 文件结构树（照抄 Papers） =====================
    private async Task PopulateFileTreeAsync(string folderPath)
    {
        FileStructureTree.RootNodes.Clear();

        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            return;

        var rootName = Path.GetFileName(folderPath);
        var rootNode = new TreeViewNode
        {
            Content = new FileItem { Name = rootName, ItemType = FileItemType.Folder },
            IsExpanded = true
        };

        await PopulateTreeNodeChildrenAsync(rootNode, folderPath);
        FileStructureTree.RootNodes.Add(rootNode);
    }

    private async Task PopulateTreeNodeChildrenAsync(TreeViewNode node, string directoryPath)
    {
        node.HasUnrealizedChildren = false;

        try
        {
            // 添加子目录（延迟加载）
            foreach (var subDir in Directory.EnumerateDirectories(directoryPath))
            {
                var dirName = Path.GetFileName(subDir);
                var dirNode = new TreeViewNode
                {
                    Content = new FileItem { Name = dirName, ItemType = FileItemType.Folder },
                    HasUnrealizedChildren = true
                };
                node.Children.Add(dirNode);
            }

            // 添加文件（含大小）
            foreach (var file in Directory.EnumerateFiles(directoryPath))
            {
                var fileInfo = new FileInfo(file);
                var ext = Path.GetExtension(file).ToLowerInvariant();
                var type = ext switch
                {
                    ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => FileItemType.Image,
                    ".mp4" or ".webm" or ".avi" or ".mov" or ".mkv" => FileItemType.Video,
                    ".json" or ".txt" or ".xml" or ".html" or ".htm" or ".css" or ".js" or ".md" => FileItemType.Document,
                    _ => FileItemType.Other
                };
                var fileNode = new TreeViewNode
                {
                    Content = new FileItem { Name = fileInfo.Name, ItemType = type, Size = fileInfo.Length }
                };
                node.Children.Add(fileNode);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, $"填充 TreeView 节点时异常: {directoryPath}");
            node.Children.Add(new TreeViewNode
            {
                Content = new FileItem { Name = "(访问被拒绝)", ItemType = FileItemType.Other }
            });
        }

        await Task.CompletedTask;
    }

    private async void FileStructureTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        var node = args.Node;

        // 查找对应的文件夹路径
        if (node.Content is FileItem fileItem && fileItem.ItemType == FileItemType.Folder && node.Parent != null)
        {
            var path = GetNodePath(node);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                await PopulateTreeNodeChildrenAsync(node, path);
            }
        }
    }

    private string? GetNodePath(TreeViewNode node)
    {
        var segments = new List<string>();
        var current = node;

        // 从叶子节点向上收集路径段
        while (current != null)
        {
            if (current.Content is FileItem fi && !string.IsNullOrEmpty(fi.Name))
            {
                segments.Insert(0, fi.Name);
            }
            current = current.Parent;
        }

        if (segments.Count == 0) return null;

        // 根节点 = 组件文件夹名，需要找到对应的完整路径
        var root = segments[0];
        var basePath = SelectedComponent?.FolderPath;
        if (string.IsNullOrEmpty(basePath) || Path.GetFileName(basePath) != root)
            return null;

        var relative = segments.Count > 1
            ? string.Join(Path.DirectorySeparatorChar.ToString(), segments.Skip(1))
            : "";
        return Path.Combine(basePath, relative);
    }

    private void FileStructureTree_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // 找到双击的 TreeViewItem
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source is not TreeViewItem)
            source = VisualTreeHelper.GetParent(source);
        if (source is not TreeViewItem treeViewItem) return;

        var node = FileStructureTree.NodeFromContainer(treeViewItem);
        if (node?.Content is not FileItem fileItem || fileItem.ItemType == FileItemType.Folder)
            return;

        // 构造完整文件路径并打开
        var fullPath = GetNodePath(node);
        if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"打开文件失败: {fullPath}");
            }
        }
    }

    private void OnTagDisplayChanged(object sender, RoutedEventArgs e)
    {
        // 切换标签显示模式后重置 ItemsSource，强制重新生成项以刷新右上角角标
        foreach (var repeater in new ItemsRepeater[] { ComponentsRepeater, ComponentsContentRepeater, ComponentsListRepeater })
        {
            if (repeater == null) continue;
            repeater.ItemsSource = null;
            repeater.ItemsSource = FilteredComponents;
        }
    }

    // ===================== 键盘快捷键（对齐 Papers） =====================
    /// <summary>页面级快捷键统一入口（原 KeyboardAccelerator 在部分焦点/输入法环境下 Ctrl+I 等组合键不触发，改用 KeyDown 路由事件）。
    /// 焦点在 TextBox 时 Ctrl+A/C、Delete 会被文本框消费并标记 Handled，此处收不到，自动让位。</summary>
    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        var menu = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

        if (ctrl)
        {
            switch (e.Key)
            {
                case VirtualKey.A:
                    SelectAllComponents_Accelerator_Invoked(null, null);
                    e.Handled = true;
                    return;
                case VirtualKey.I:
                    InvertSelection_Accelerator_Invoked(null, null);
                    e.Handled = true;
                    return;
                case VirtualKey.C:
                    Copy_Accelerator_Invoked(null, null);
                    e.Handled = true;
                    return;
            }
        }
        else if (menu && e.Key == VirtualKey.Enter)
        {
            Properties_Accelerator_Invoked(null, null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Delete)
        {
            Delete_Accelerator_Invoked(null, null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.F5)
        {
            // F5 刷新（刷新进行中时由 ComponentsRefresh_Click 内部防连按兜底）
            ComponentsRefresh_Click(null, null);
            e.Handled = true;
        }
    }

    private void SelectAllComponents_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
        => SelectAllComponents_Click(sender, null);

    private void InvertSelection_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
        => InvertSelection_Click(sender, null);

    private void Copy_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
        => CopyComponent_Click(sender, null);

    private void Delete_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
        => DeleteComponent_Click(sender, null);

    private void Properties_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
        => ComponentProperties_Click(sender, null);

    private void GoToSettings_Click(object sender, RoutedEventArgs e)
    {
        Frame?.Navigate(typeof(Settings));
    }

    // ===================== 多选面板按钮 =====================
    private void SelectAllComponents_Click(object sender, RoutedEventArgs e)
    {
        if (!IsMultiSelectMode) IsMultiSelectMode = true;

        _isBatchUpdating = true;
        foreach (var item in FilteredComponents)
        {
            if (!item.IsSelected)
            {
                item.IsSelected = true;
                SelectedComponents.Add(item);
            }
        }
        _isBatchUpdating = false;

        RefreshDisplayedSelectedComponents(forceRebuild: true);
        UpdateMultiSelectCount();
    }

    private void InvertSelection_Click(object sender, RoutedEventArgs e)
    {
        if (!IsMultiSelectMode) IsMultiSelectMode = true;

        _isBatchUpdating = true;
        foreach (var item in FilteredComponents)
        {
            item.IsSelected = !item.IsSelected;
            if (item.IsSelected && !SelectedComponents.Contains(item))
                SelectedComponents.Add(item);
            else if (!item.IsSelected)
                SelectedComponents.Remove(item);
        }
        _isBatchUpdating = false;

        RefreshDisplayedSelectedComponents(forceRebuild: true);
        UpdateMultiSelectCount();
    }

    private void CancelMultiSelect_Click(object sender, RoutedEventArgs e)
    {
        IsMultiSelectMode = false;

        _isBatchUpdating = true;
        foreach (var item in SelectedComponents.ToList())
            item.IsSelected = false;
        SelectedComponents.Clear();
        _isBatchUpdating = false;

        DisplayedSelectedComponents.Clear();
        SelectedComponent = null;
        UpdateMultiSelectCount();
    }

    private void ComponentsList_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_isComponentItemTapped == true)
        {
            _isComponentItemTapped = false;
            return;
        }

        SelectedComponent = null;
    }

    private void ShadowRect_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement casterElement)
        {
            if (casterElement.Shadow is ThemeShadow themeShadow)
            {
                if (VisualTreeHelper.GetParent(casterElement) is Grid parentContainer)
                {
                    var receiverGrid = parentContainer.FindName("ShadowCastGrid") as Grid;

                    if (receiverGrid != null)
                    {
                        if (!themeShadow.Receivers.Contains(receiverGrid))
                        {
                            themeShadow.Receivers.Add(receiverGrid);
                        }
                    }
                }
            }
            if (casterElement is Grid grid && grid.DataContext is ComponentInfo item)
            {
                UpdateItemCheckBoxOpacity(grid, item);
            }
        }
    }

    private void SelectionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is ComponentInfo item)
        {
            if (!IsMultiSelectMode)
            {
                IsMultiSelectMode = true;
            }
            if (cb.IsChecked == true && !SelectedComponents.Contains(item))
            {
                SelectedComponents.Add(item);
            }
            else if (cb.IsChecked == false)
            {
                SelectedComponents.Remove(item);
            }
            UpdateMultiSelectCount();
        }
    }

    private void ContentItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            var checkBox = FindCheckBoxInGrid(grid);
            if (checkBox != null) checkBox.Opacity = 1;

            Visual visual = ElementCompositionPreview.GetElementVisual(grid);
            visual.CenterPoint = new Vector3((float)grid.ActualWidth / 2, (float)grid.ActualHeight / 2, 0f);

            if (_isLeftMouseButtonPressed && grid.DataContext is ComponentInfo item)
            {
                ContentItem_PointerPressed(sender, e);
                if (!_isMultiSelectMode)
                {
                    // 拖拽滑过时更新预览图和标题，但不播放钻入动画避免卡顿
                    SelectedComponent = item;
                }
                if (IsMultiSelectMode)
                {
                    item.IsSelected = !item.IsSelected;
                    if (item.IsSelected && !SelectedComponents.Contains(item))
                        SelectedComponents.Add(item);
                    else if (!item.IsSelected)
                        SelectedComponents.Remove(item);
                    UpdateMultiSelectCount();
                }
                return;
            }
        }
    }

    private void ContentItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid && grid.DataContext is ComponentInfo item)
        {
            UpdateItemCheckBoxOpacity(grid, item);
            ApplyScaleAnimation(grid, 1.0f);
            UpdateItemCheckBoxOpacity(grid, item);

            Visual visual = ElementCompositionPreview.GetElementVisual(grid);
            var scaleAnim = visual.Compositor.CreateSpringVector3Animation();
            scaleAnim.Target = "Scale";
            scaleAnim.FinalValue = new Vector3(1.0f, 1.0f, 1.0f);
            scaleAnim.DampingRatio = 0.6f;
            scaleAnim.Period = TimeSpan.FromMilliseconds(50);
            visual.StartAnimation("Scale", scaleAnim);
        }
    }

    private void ContentItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            _isComponentItemTapped = true;

            Visual visual = ElementCompositionPreview.GetElementVisual(grid);
            visual.CenterPoint = new Vector3((float)grid.ActualWidth / 2, (float)grid.ActualHeight / 2, 0f);

            var scaleAnim = visual.Compositor.CreateSpringVector3Animation();
            scaleAnim.Target = "Scale";
            scaleAnim.FinalValue = new Vector3(0.95f, 0.95f, 1.0f);
            scaleAnim.DampingRatio = 0.8f;
            scaleAnim.Period = TimeSpan.FromMilliseconds(50);
            visual.StartAnimation("Scale", scaleAnim);

            var pointerPoint = e.GetCurrentPoint(sender as UIElement);
            var properties = pointerPoint.Properties;

            if (properties.PointerUpdateKind is Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed)
            {
                if (sender is FrameworkElement element && element.DataContext is ComponentInfo item)
                {
                    var modifiers = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
                    if (modifiers && !_isMultiSelectMode)
                    {
                        IsMultiSelectMode = true;
                        item.IsSelected = !item.IsSelected;
                        if (item.IsSelected && !SelectedComponents.Contains(item))
                            SelectedComponents.Add(item);
                    }

                    if (_isMultiSelectMode)
                    {
                        item.IsSelected = !item.IsSelected;
                        if (item.IsSelected && !SelectedComponents.Contains(item))
                            SelectedComponents.Add(item);
                        else if (!item.IsSelected)
                            SelectedComponents.Remove(item);
                        UpdateMultiSelectCount();
                        if (sender is Grid g)
                        {
                            var cb = FindCheckBoxInGrid(g);
                            if (cb != null) cb.Opacity = 1;
                        }
                        return;
                    }
                }
            }
        }
    }

    private void ContentItem_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(grid);
            var scaleAnim = visual.Compositor.CreateSpringVector3Animation();
            scaleAnim.Target = "Scale";
            scaleAnim.FinalValue = new Vector3(1.0f, 1.0f, 1.0f);
            scaleAnim.DampingRatio = 0.6f;
            scaleAnim.Period = TimeSpan.FromMilliseconds(50);
            visual.StartAnimation("Scale", scaleAnim);
        }

        var pointerPoint = e.GetCurrentPoint(sender as UIElement);
        var properties = pointerPoint.Properties;

        if (properties.PointerUpdateKind is Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased)
        {
            if (sender is FrameworkElement element && element.DataContext is ComponentInfo item)
            {
                if (!_isMultiSelectMode)
                {
                    if (SelectedComponent != item)
                    {
                        SelectedComponent = item;
                        PlayDrillInAnimation();
                    }
                }
                e.Handled = true;
            }
        }
    }

    private void ContentItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ComponentInfo item)
        {
            _rightClickedComponentElement = element;
            if (!_isMultiSelectMode)
            {
                SelectedComponent = item;
            }
        }
    }

    private void Item_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            var checkBox = FindCheckBoxInGrid(grid);
            if (checkBox != null) checkBox.Opacity = 1;

            Visual visual = ElementCompositionPreview.GetElementVisual(grid);
            Compositor compositor = visual.Compositor;

            visual.CenterPoint = new Vector3(
                (float)grid.ActualWidth / 2,
                (float)grid.ActualHeight / 2,
                0f);

            var parent = VisualTreeHelper.GetParent(grid) as UIElement;
            if (parent != null)
            {
                Canvas.SetZIndex(parent, 10000);
            }

            if (_isLeftMouseButtonPressed && grid.DataContext is ComponentInfo item)
            {
                Item_PointerPressed(sender, e);
                if (!_isMultiSelectMode)
                {
                    // 拖拽滑过时更新预览图和标题，但不播放钻入动画避免卡顿
                    SelectedComponent = item;
                }
                if (IsMultiSelectMode)
                {
                    item.IsSelected = !item.IsSelected;

                    if (item.IsSelected && !SelectedComponents.Contains(item))
                        SelectedComponents.Add(item);
                    else if (!item.IsSelected)
                        SelectedComponents.Remove(item);

                    UpdateMultiSelectCount();
                }
                return;
            }

            if (ViewModel.ComponentsDisplayVM.IsComponentEnterAnimationEnabled)
            {
                var scaleAnimation = compositor.CreateSpringVector3Animation();
                scaleAnimation.Target = "Scale";
                scaleAnimation.FinalValue = new Vector3(1.15f, 1.15f, 1.15f);
                scaleAnimation.DampingRatio = 0.6f;
                scaleAnimation.Period = TimeSpan.FromMilliseconds(50);
                visual.StartAnimation("Scale", scaleAnimation);

                // Enable ThemeShadow on hover
                if (grid.Shadow is not ThemeShadow)
                {
                    var themeShadow = new ThemeShadow();
                    if (VisualTreeHelper.GetParent(grid) is Grid parentContainer)
                    {
                        var receiverGrid = parentContainer.FindName("ShadowCastGrid") as Grid;
                        if (receiverGrid != null)
                        {
                            themeShadow.Receivers.Add(receiverGrid);
                        }
                    }
                    grid.Shadow = themeShadow;
                }
            }

            Visual itemVisual = ElementCompositionPreview.GetElementVisual(grid);
            if (itemVisual?.Parent is ContainerVisual parentVisual)
            {
                parentVisual.Children.Remove(itemVisual);
                parentVisual.Children.InsertAtTop(itemVisual);
            }
        }
    }

    private void Item_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid && grid.DataContext is ComponentInfo item)
        {
            UpdateItemCheckBoxOpacity(grid, item);

            ApplyScaleAnimation(grid, 1.0f);
            UpdateItemCheckBoxOpacity(grid, item);

            grid.Shadow = null;

            Visual visual = ElementCompositionPreview.GetElementVisual(grid);
            Compositor compositor = visual.Compositor;

            var scaleAnimation = compositor.CreateSpringVector3Animation();
            scaleAnimation.Target = "Scale";
            scaleAnimation.FinalValue = new Vector3(1.0f, 1.0f, 1.0f);
            scaleAnimation.DampingRatio = 0.6f;
            scaleAnimation.Period = TimeSpan.FromMilliseconds(50);

            var capturedParent = VisualTreeHelper.GetParent(grid) as UIElement;

            visual.StartAnimation("Scale", scaleAnimation);

            DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(20);

                Canvas.SetZIndex(grid, 0);
                if (capturedParent != null)
                {
                    Canvas.SetZIndex(capturedParent, 0);
                }
                grid.Translation = new Vector3(0f, 0f, 64f);
            });

            var pointerPoint = e.GetCurrentPoint(sender as UIElement);
            var properties = pointerPoint.Properties;
        }
    }

    private void Item_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(grid);
            Compositor compositor = visual.Compositor;

            var scaleAnimation = compositor.CreateSpringVector3Animation();
            scaleAnimation.Target = "Scale";

            if (!ViewModel.ComponentsDisplayVM.IsComponentEnterAnimationEnabled)
            {
                scaleAnimation.FinalValue = new Vector3(1f, 1f, 1f);
            }
            else
            {
                scaleAnimation.FinalValue = new Vector3(1.15f, 1.15f, 1.15f);
            }
            scaleAnimation.DampingRatio = 0.6f;
            scaleAnimation.Period = TimeSpan.FromMilliseconds(50);
            visual.StartAnimation("Scale", scaleAnimation);
        }

        var pointerPoint = e.GetCurrentPoint(sender as UIElement);
        var properties = pointerPoint.Properties;

        if (properties.PointerUpdateKind is Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased)
        {
            if (sender is FrameworkElement element && element.DataContext is ComponentInfo item)
            {
                if (!_isMultiSelectMode)
                {
                    if (SelectedComponent != item)
                    {
                        SelectedComponent = item;
                        PlayDrillInAnimation();
                    }
                }
                e.Handled = true;
            }
        }
    }

    private void Item_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            _isComponentItemTapped = true;

            Visual visual = ElementCompositionPreview.GetElementVisual(grid);
            Compositor compositor = visual.Compositor;

            visual.CenterPoint = new Vector3((float)grid.ActualWidth / 2, (float)grid.ActualHeight / 2, 0f);

            // 创建缩小动画（模拟按下）
            var scaleAnimation = compositor.CreateSpringVector3Animation();
            scaleAnimation.Target = "Scale";
            scaleAnimation.FinalValue = new Vector3(0.92f, 0.92f, 1.0f); // 缩小到 92%
            scaleAnimation.DampingRatio = 0.8f;
            scaleAnimation.Period = TimeSpan.FromMilliseconds(50);

            visual.StartAnimation("Scale", scaleAnimation);

            var pointerPoint = e.GetCurrentPoint(sender as UIElement);
            var properties = pointerPoint.Properties;

            if (properties.PointerUpdateKind is Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed)
            {
                if (sender is FrameworkElement element && element.DataContext is ComponentInfo item)
                {
                    var modifiers = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
                    if (modifiers && !_isMultiSelectMode)
                    {
                        IsMultiSelectMode = true;
                        item.IsSelected = !item.IsSelected;

                        if (item.IsSelected && !SelectedComponents.Contains(item))
                            SelectedComponents.Add(item);
                    }

                    if (_isMultiSelectMode)
                    {
                        item.IsSelected = !item.IsSelected;

                        if (item.IsSelected && !SelectedComponents.Contains(item))
                            SelectedComponents.Add(item);
                        else if (!item.IsSelected)
                            SelectedComponents.Remove(item);

                        UpdateMultiSelectCount();

                        if (sender is Grid g)
                        {
                            var cb = FindCheckBoxInGrid(g);
                            if (cb != null) cb.Opacity = 1;
                        }
                        return;
                    }
                }
            }
        }
    }

    private void ComponentItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ComponentInfo item)
        {
            if (!_isMultiSelectMode)
            {
                if (SelectedComponent != item)
                {
                    SelectedComponent = item;
                    PlayDrillInAnimation();
                }
            }
            _rightClickedComponentElement = element;
        }
    }

    // ===================== 辅助方法 =====================
    private static void ApplyScaleAnimation(FrameworkElement fe, float targetScale)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(fe);
        Compositor compositor = visual.Compositor;

        float width = (float)fe.ActualWidth;
        float height = (float)fe.ActualHeight;
        if (width <= 0) width = 200f;
        if (height <= 0) height = 150f;

        visual.CenterPoint = new Vector3(width / 2, height / 2, 0f);

        var scaleAnimation = compositor.CreateSpringVector3Animation();
        scaleAnimation.Target = "Scale";
        scaleAnimation.FinalValue = new Vector3(targetScale, targetScale, 1.0f);
        scaleAnimation.DampingRatio = 0.6f;
        scaleAnimation.Period = TimeSpan.FromMilliseconds(50);

        visual.StartAnimation("Scale", scaleAnimation);
    }

    private void PlayDrillInAnimation()
    {
        // 防抖：距上次播放不足 200ms 时跳过，避免快速连续调用造成 compositor 资源竞争
        var now = DateTime.UtcNow;
        if ((now - _lastDrillInAnimationTime).TotalMilliseconds < 200) return;
        _lastDrillInAnimationTime = now;

        Visual imageVisual = ElementCompositionPreview.GetElementVisual(ComponentPreviewImage);
        Compositor compositor = imageVisual.Compositor;

        imageVisual.CenterPoint = new Vector3(125f, 125f, 0f);

        // 创建缩放动画 (从 0.8 放大到 1.0)
        var scaleAnim = compositor.CreateScalarKeyFrameAnimation();
        scaleAnim.InsertKeyFrame(0.0f, 0.85f);
        scaleAnim.InsertKeyFrame(1.0f, 1.0f);
        scaleAnim.Duration = TimeSpan.FromMilliseconds(400);
        scaleAnim.Target = "Scale.X";

        // 创建透明度动画
        var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.InsertKeyFrame(0.0f, 0.0f);
        opacityAnim.InsertKeyFrame(0.2f, 1.0f);
        opacityAnim.Duration = TimeSpan.FromMilliseconds(400);

        imageVisual.StartAnimation("Scale.X", scaleAnim);
        imageVisual.StartAnimation("Scale.Y", scaleAnim);
        imageVisual.StartAnimation("Opacity", opacityAnim);
    }

    private void UpdateItemCheckBoxOpacity(Grid grid, ComponentInfo item)
    {
        if (grid == null || item == null) return;

        var checkBox = FindCheckBoxInGrid(grid);
        checkBox.Opacity = (IsMultiSelectMode || item.IsSelected) ? 1 : 0;
    }

    private static CheckBox? FindCheckBoxInGrid(Grid grid)
    {
        // 先查直接子级
        var cb = grid.Children.OfType<CheckBox>().FirstOrDefault();
        if (cb != null) return cb;
        // 查 StackPanel 子级
        foreach (var sp in grid.Children.OfType<StackPanel>())
        {
            cb = sp.Children.OfType<CheckBox>().FirstOrDefault();
            if (cb != null) return cb;
        }
        // 再递归查子 Grid
        foreach (var childGrid in grid.Children.OfType<Grid>())
        {
            cb = FindCheckBoxInGrid(childGrid);
            if (cb != null) return cb;
        }
        return null;
    }

    private void UpdateAllVisibleCheckBoxes()
    {
        foreach (var repeater in new ItemsRepeater[] { ComponentsRepeater, ComponentsContentRepeater, ComponentsListRepeater })
        {
            if (repeater == null || repeater.ItemsSourceView == null) continue;
            for (int i = 0; i < repeater.ItemsSourceView.Count; i++)
            {
                var element = repeater.TryGetElement(i) as FrameworkElement;
                if (element == null) continue;
                var grid = element as Grid ?? FindChildGrid(element);
                if (grid?.DataContext is ComponentInfo item)
                    UpdateItemCheckBoxOpacity(grid, item);
            }
        }
    }

    private static Grid? FindChildGrid(FrameworkElement element)
    {
        if (element is Grid g) return g;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i) as FrameworkElement;
            if (child != null)
            {
                var result = FindChildGrid(child);
                if (result != null) return result;
            }
        }
        return null;
    }

    private void UpdateMultiSelectCount()
    {
        MultiSelectCountText?.Text = $"已选择 {SelectedComponents.Count} 项";
        if (SelectedComponents.Count == 0)
        {
            IsMultiSelectMode = false;
        }
        UpdateDetailPanel();
        OnPropertyChanged(nameof(IsComponentButtonEnabled));
    }

    // ===================== 多选堆叠图（照抄 Papers） =====================
    private void RefreshDisplayedSelectedComponents(bool forceRebuild = false)
    {
        // 全选/反选/退出多选 等批量操作时强制重建
        if (forceRebuild)
        {
            StopAllStackAnimations();
            RebuildDisplayedFromLast5();
            return;
        }

        // 单张选择/取消 时走增量更新（最自然）
        // 如果当前显示的最后一张不是 Selected 的最后一张 → 说明新增了
        if (DisplayedSelectedComponents.Count == 0 ||
            !DisplayedSelectedComponents.Last().Equals(SelectedComponents.LastOrDefault()))
        {
            if (SelectedComponents.Count <= 5)
            {
                StopAllStackAnimations();
                RebuildDisplayedFromLast5();
            }
            else
            {
                // 增量：挤掉最旧的一张，加入最新的一张（前4张容器保持不变！）
                if (DisplayedSelectedComponents.Count >= 5)
                {
                    DisplayedSelectedComponents.RemoveAt(0);   // 移除最底层（最早的）
                }
                DisplayedSelectedComponents.Add(SelectedComponents.Last()); // 加入最新（最顶层）
            }
        }
    }

    private void RebuildDisplayedFromLast5()
    {
        DisplayedSelectedComponents.Clear();
        int total = SelectedComponents.Count;
        int start = Math.Max(0, total - 5);
        for (int i = start; i < total; i++)
        {
            DisplayedSelectedComponents.Add(SelectedComponents[i]);
        }
    }

    private void UpdateStackVisuals()
    {
        int count = DisplayedSelectedComponents.Count;
        for (int i = 0; i < count; i++)
        {
            var container = StackedImagesControl.ContainerFromIndex(i) as FrameworkElement;
            if (container == null) continue;

            container.Visibility = Visibility.Visible;
            ApplyStackAnimation(container, i);
            Canvas.SetZIndex(container, i);
        }
    }

    private void StopAllStackAnimations()
    {
        for (int i = 0; i < DisplayedSelectedComponents.Count; i++)
        {
            var container = StackedImagesControl.ContainerFromIndex(i) as FrameworkElement;
            if (container == null) continue;

            var visual = ElementCompositionPreview.GetElementVisual(container);
            if (visual != null)
            {
                visual.StopAnimation("Offset");
                visual.StopAnimation("RotationAngleInDegrees");
                visual.StopAnimation("Scale");
            }
        }
    }

    private void StackedImage_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            visual.Scale = new Vector3(1.0f, 1.0f, 1.0f);

            // 触发位置计算
            UpdateStackVisuals();
        }
    }

    private static void ApplyStackAnimation(FrameworkElement element, int relativeIndex)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        Compositor compositor = visual.Compositor;

        // 1:1 正方形中心点
        float size = 200f;
        visual.CenterPoint = new Vector3(size / 2, size / 2, 0f);

        // 基于相对位置计算位移和旋转
        // relativeIndex 越大（越新），偏移越多
        float offsetY = relativeIndex * -12f;
        float offsetX = relativeIndex * 8f;
        float rotation = (relativeIndex % 2 == 0) ? relativeIndex * 2.5f : relativeIndex * -2.5f;

        // 使用动画平滑移动到新位置（防止新增图片时旧图片位置突跳）
        var offsetAnim = compositor.CreateSpringVector3Animation();
        offsetAnim.Target = "Offset";
        offsetAnim.FinalValue = new Vector3(offsetX, offsetY, 0f);
        offsetAnim.DampingRatio = 0.7f;

        var rotationAnim = compositor.CreateSpringScalarAnimation();
        rotationAnim.Target = "RotationAngleInDegrees";
        rotationAnim.FinalValue = rotation;
        rotationAnim.DampingRatio = 0.7f;

        visual.StartAnimation("Offset", offsetAnim);
        visual.StartAnimation("RotationAngleInDegrees", rotationAnim);
    }

    /// <summary>多选/单选视觉切换（照抄 Papers.ToggleMultiSelectVisuals，去掉 GC 收集）</summary>
    private async Task ToggleMultiSelectVisuals(bool isMulti)
    {
        if (isMulti)
        {
            // 如果单选有焦点，顺便加入多选
            if (SelectedComponent != null && !SelectedComponents.Contains(SelectedComponent))
            {
                SelectedComponent.IsSelected = true;
                SelectedComponents.Add(SelectedComponent);
                RefreshDisplayedSelectedComponents(forceRebuild: true);
            }
            else if (SelectedComponents.Count > 0)
            {
                RefreshDisplayedSelectedComponents(forceRebuild: true);
            }

            // 核心视觉：缩小 + 圆角
            SinglePreviewBorder.CornerRadius = new CornerRadius(8);
            var visual = ElementCompositionPreview.GetElementVisual(SinglePreviewBorder);
            visual.CenterPoint = new Vector3((float)SinglePreviewBorder.ActualWidth / 2, (float)SinglePreviewBorder.ActualHeight / 2, 0f);

            var scaleAnimation = visual.Compositor.CreateSpringVector3Animation();
            scaleAnimation.Target = "Scale";
            scaleAnimation.FinalValue = new Vector3(0.6f, 0.6f, 1.0f);
            scaleAnimation.DampingRatio = 0.6f;
            visual.StartAnimation("Scale", scaleAnimation);

            await Task.Delay(150);
            StackedImagesControl.Visibility = Visibility.Visible;
            SinglePreviewBorder.Visibility = Visibility.Collapsed;
            SingleSelectionInfoPanel.Visibility = Visibility.Collapsed;
            MultiSelectionInfoPanel.Visibility = Visibility.Visible;
            NoSelectionHintText.Visibility = Visibility.Collapsed;

            UpdateMultiSelectCount();
        }
        else
        {
            StopAllStackAnimations();

            SinglePreviewBorder.Visibility = Visibility.Visible;
            SingleSelectionInfoPanel.Visibility = SelectedComponent != null
                ? Visibility.Visible : Visibility.Collapsed;
            NoSelectionHintText.Visibility = SelectedComponent != null
                ? Visibility.Collapsed : Visibility.Visible;

            StackedImagesControl.Visibility = Visibility.Collapsed;
            MultiSelectionInfoPanel.Visibility = Visibility.Collapsed;

            var visual = ElementCompositionPreview.GetElementVisual(SinglePreviewBorder);
            visual.CenterPoint = new Vector3((float)SinglePreviewBorder.ActualWidth / 2, (float)SinglePreviewBorder.ActualHeight / 2, 0f);

            // 缩放回 1.0 (250px)
            var scaleReturnAnim = visual.Compositor.CreateSpringVector3Animation();
            scaleReturnAnim.Target = "Scale";
            scaleReturnAnim.FinalValue = new Vector3(1.0f, 1.0f, 1.0f);
            scaleReturnAnim.DampingRatio = 0.7f;
            scaleReturnAnim.Period = TimeSpan.FromMilliseconds(50);

            // 位置归零 (防止在堆叠时有微小的 Offset)
            var offsetReturnAnim = visual.Compositor.CreateSpringVector3Animation();
            offsetReturnAnim.Target = "Offset";
            offsetReturnAnim.FinalValue = new Vector3(0f, 0f, 0f);
            offsetReturnAnim.DampingRatio = 1.0f;

            SinglePreviewBorder.CornerRadius = new CornerRadius(0);

            visual.StartAnimation("Scale", scaleReturnAnim);
            visual.StartAnimation("Offset", offsetReturnAnim);

            foreach (var item in SelectedComponents)
            {
                item.IsSelected = false;
            }
            SelectedComponents.Clear();
            DisplayedSelectedComponents.Clear();

            UpdateMultiSelectCount();
        }
    }
}

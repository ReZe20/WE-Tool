using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Controls;
using WE_Tool.Converters;
using WE_Tool.Models;
using WE_Tool.Service;
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
    private bool _isFirstLoad = true;
    private bool _isLeftMouseButtonPressed;
    private AppBarButton? _pressedButton; // 当前被按下的 CommandBar 按钮(指针捕获后释放弹回用)
    private bool _isComponentItemTapped;
    private bool _isMultiSelectMode;
    private bool _isBatchUpdating;

    /// <summary>导航徽标是否处于失败(红)状态:失败后保持红色,直到下次提取开始才复位。</summary>
    private bool _navBadgeError;
    private int _lastStackCount;
    private FrameworkElement? _rightClickedComponentElement;
    private DateTime _lastDrillInAnimationTime;
    private CancellationTokenSource? _filterCts;
    private readonly Service.PickerService _pickerService = new();

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
        ScrollVisibleComponentGridToTop();
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

    // ============= GridView 列表辅助(自适应列宽/回顶,照抄 Papers) =============

    /// <summary>三个组件 GridView(图标/内容/列表模式)</summary>
    private GridView[] AllComponentGridViews => new[] { ComponentsGridView, ComponentsContentGridView, ComponentsListGridView };

    /// <summary>窗口尺寸变化:实时重算 ItemWidth</summary>
    private void ComponentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateAllComponentGridItemWidths();

    /// <summary>按各模式档位重算 ItemWidth(照抄 Papers:ItemWidth = 槽位步长,容器 = ItemWidth - 10,留 2px 余量)</summary>
    private void UpdateAllComponentGridItemWidths()
    {
        UpdateGridItemWidth(ComponentsGridView, ViewModel.ComponentsDisplayVM.ComponentListMinWidth, 10);
        UpdateGridItemWidth(ComponentsListGridView, 400, 10);
        UpdateGridItemWidth(ComponentsContentGridView, 0, 10); // 内容模式单列
    }

    /// <param name="itemMarginTotal">容器左右 Margin 总和(判断列数用)</param>
    private static void UpdateGridItemWidth(GridView gridView, int minItemWidth, int itemMarginTotal)
    {
        // 非反射(AOT 兼容):ItemsPanelRoot 在 ItemsWrapGrid 面板下必定是 ItemsWrapGrid。
        // AOT 裁剪下 is/强转都可能失败(类型元数据缺失/类型被投影),用 DP SetValue 直接灌值,
        // 走 WinUI 原生 DP 系统,不经过 C# 类型匹配,最稳。
        if (gridView?.ItemsPanelRoot is not { } panelRoot) return;
        panelRoot.SetValue(ItemsWrapGrid.CacheLengthProperty, 0); // 不预渲染(仅实化可见项;滚动时即时实化,Skia 流式打开快)
        double available = gridView.ActualWidth;
        if (available <= 0) return;
        if (minItemWidth <= 0) // 单列(内容模式):槽位 = 可用宽,卡片 = 可用宽 - 10
        {
            panelRoot.SetValue(ItemsWrapGrid.ItemWidthProperty, available);
            return;
        }
        int cols = Math.Max(1, (int)(available / (minItemWidth + itemMarginTotal)));
        // ItemsWrapGrid 语义(已实测):ItemWidth = 槽位步长(含容器 Margin),容器实际宽 = ItemWidth - 10;
        // 换行判断为严格比较,ItemWidth = available/cols 会因浮点误差恰好放不下最后一列(空一列),
        // 故留 2px/列余量;卡片 = available/cols - 12,行尾余量 cols×2px 不可见
        panelRoot.SetValue(ItemsWrapGrid.ItemWidthProperty, available / cols - 2);
    }

    /// <summary>当前可见的 GridView(滚动回顶用)</summary>
    private GridView? GetVisibleComponentGridView()
    {
        foreach (var gv in AllComponentGridViews)
            if (gv.Visibility == Visibility.Visible)
                return gv;
        return null;
    }

    /// <summary>可见 GridView 滚动回顶(分页/刷新后)</summary>
    private void ScrollVisibleComponentGridToTop()
    {
        if (GetVisibleComponentGridView() is GridView gv && FindScrollViewer(gv) is ScrollViewer sv)
            sv.ChangeView(0, 0, null);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is ScrollViewer found) return found;
        }
        return null;
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
                ToggleMultiSelectVisuals(_isMultiSelectMode);
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
        // resw 附加属性经 x:Uid 在 WinUI3 不生效(已知限制),tooltip 需代码显式设置
        ToolTipService.SetToolTip(SortToolbarButton, LanguageHelper.GetResource("Toolbar_Sort.ToolTipService.ToolTip"));

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
            else if (e.PropertyName == nameof(ComponentsDisplayViewModel.ComponentListMinWidth))
            {
                // 小/中/大档位变化:列宽公式随档位值联动重算(照抄 Papers)
                UpdateAllComponentGridItemWidths();
            }
        };

        // 首次布局后重算列宽 + 挂补位移动动画(照抄 Papers)
        this.Loaded += (s, e) =>
        {
            UpdateAllComponentGridItemWidths();
            var reorderDuration = TimeSpan.FromMilliseconds(100);
            foreach (var gv in AllComponentGridViews)
                ItemsReorderAnimation.SetDuration(gv, reorderDuration);
        };

        // Skia 流式播放 + 角标:容器绑定时刷新。订阅放构造函数(页面有导航缓存,
        // Loaded 每次切回都重发,放里面会造成订阅数随访问次数翻倍,容器实化时处理 N 遍)
        ComponentsGridView.ContainerContentChanging += (s2, e2) =>
        {
            if (e2.InRecycleQueue) return;
            if (e2.Item is ComponentInfo cItem &&
                e2.ItemContainer.ContentTemplateRoot is Grid cRoot)
            {
                UpdateSkiaGif(cRoot, cItem);
                UpdateTagBadge(cRoot, cItem);
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
        // CommandBar 内按钮按下反馈:按钮缩小(AddHandler handledEventsToo:true 能收到 Button 内部的 handled 事件)
        if (e.OriginalSource is FrameworkElement fe && IsDescendantOf(fe, ToolbarCommands))
        {
            if (FindAncestorButton(fe) is { } btn)
            {
                _pressedButton = btn;                       // 记录按下的按钮(供释放时弹回)
                btn.CapturePointer(e.Pointer);              // 捕获指针:移开按钮后释放仍收到事件
                PlayPressScale(btn, 0.88f);
            }
        }
    }

    private void Global_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isLeftMouseButtonPressed = false;
        // 弹回按下的按钮(指针捕获保证即使移开后释放也触发)
        if (_pressedButton is { } pressedBtn)
        {
            _pressedButton = null;
            pressedBtn.ReleasePointerCapture(e.Pointer);
            PlayPressScale(pressedBtn, 1f);
            // 刷新按钮:图标旋转动画在鼠标松开后播放(按下只缩小)
            if (pressedBtn == RefreshButton)
            {
                PlayRefreshSpin();
            }
        }
    }

    // 刷新图标旋转动画:按下后快速转几圈(Composition RotationAngleInDegrees)
    private void PlayRefreshSpin()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var visual = ElementCompositionPreview.GetElementVisual(RefreshIcon);
            var compositor = visual.Compositor;
            visual.StopAnimation("RotationAngleInDegrees");
            visual.CenterPoint = new Vector3(8f, 8f, 0); // FontIcon 约 16px,中心固定 8,8
            visual.RotationAngleInDegrees = 0f;

            // 转 2 圈(720°),2000ms,带缓出
            var spin = compositor.CreateScalarKeyFrameAnimation();
            spin.Target = "RotationAngleInDegrees";
            spin.InsertKeyFrame(0f, 0f);
            spin.InsertKeyFrame(1f, 720f,
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.17f, 0.67f), new Vector2(0.83f, 0.67f)));
            spin.Duration = TimeSpan.FromMilliseconds(2000);
            visual.StartAnimation("RotationAngleInDegrees", spin);
        });
    }

    // 从事件源向上找最近的 AppBarButton/AppBarToggleButton(CommandBar 命令按钮)
    private static AppBarButton? FindAncestorButton(DependencyObject? current)
    {
        while (current != null)
        {
            if (current is AppBarButton abb) return abb;
            if (current is AppBarToggleButton) return null; // 开关按钮不加缩放
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // 判断 element 是否是 ancestor 的后代(含自身)
    private static bool IsDescendantOf(FrameworkElement element, FrameworkElement ancestor)
    {
        DependencyObject? current = element;
        while (current != null)
        {
            if (current == ancestor) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    // 按钮缩放反馈(Composition Scale,固定 CenterPoint 避免 NaN)
    private void PlayPressScale(AppBarButton button, float targetScale)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var visual = ElementCompositionPreview.GetElementVisual(button);
            var compositor = visual.Compositor;
            visual.StopAnimation("Scale");
            visual.CenterPoint = new Vector3((float)button.ActualWidth / 2, (float)button.ActualHeight / 2, 0);
            visual.Scale = Vector3.One;

            var anim = compositor.CreateVector3KeyFrameAnimation();
            anim.Target = "Scale";
            anim.InsertKeyFrame(0f, Vector3.One);
            anim.InsertKeyFrame(1f, new Vector3(targetScale, targetScale, 1f),
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.17f, 0.67f), new Vector2(0.83f, 0.67f)));
            anim.Duration = TimeSpan.FromMilliseconds(120);
            visual.StartAnimation("Scale", anim);
        });
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
            // 首载标志(对齐 Papers):只需首次进入完整加载;切走再切回只重启 GIF,
            // 不重建列表也不清空选择。手动刷新按钮/F5 始终走 LoadComponents 完整重载。
            if (_isFirstLoad)
            {
                _isFirstLoad = false;
                _ = LoadComponents();
            }
            // 页面缓存:切走时 Unloaded 停播,切回后容器不重新绑定 → 延迟一帧重启可见 GIF 动画
            DispatcherQueue.TryEnqueue(() => RestartVisibleGifPlayback());
        }

        /// <summary>遍历可见容器重启 GIF 播放(页面缓存切回时;容器未就绪/无项时无害)</summary>
        private void RestartVisibleGifPlayback()
        {
            // 非反射(AOT 兼容):ItemsPanelRoot 返回类型是 Panel(基类),Children 是 Panel 属性,直接访问即可,不强转 ItemsWrapGrid
            if (ComponentsGridView.ItemsPanelRoot is not { } panelRoot) return;
            foreach (var child in panelRoot.Children)
            {
                if (child is not GridViewItem container) continue;
                if (container.ContentTemplateRoot is not Grid root) continue;
                if (ComponentsGridView.ItemFromContainer(container) is ComponentInfo item)
                    UpdateSkiaGif(root, item);
            }
        }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
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
                .Convert(item.FileSize, null!, "", "")?.ToString() ?? "";
            ComponentTypeText.Text = new Converters.ComponentTypeToDisplay()
                .Convert(item.ComponentType, null!, "", "")?.ToString() ?? "";
            ComponentRatingText.Text = new Converters.RatingToDisplay()
                .Convert(item.ContentRating ?? "Everyone", null!, "", "")?.ToString() ?? "";

            // 标签徽章
            bool hasTags = !string.IsNullOrEmpty(item.Tags) && item.Tags != "Unspecified";
            ComponentTagsBorder.Visibility = hasTags ? Visibility.Visible : Visibility.Collapsed;
            if (hasTags)
            {
                ComponentTagsText.Text = new Converters.TagToDisplay()
                    .Convert(item.Tags!, null!, "", "")?.ToString() ?? item.Tags!;
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
                ShowTip(NoScanResultTip, true);
                ShowTip(NoResultTip, false);
                return;
            }

            // === 分页 ===
            bool listUnchanged = IsComponentListEqual(_filteredComponents, filteredResult);
            int pageBefore = CurrentPage; // 记录翻页判断基准
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

            // 翻页(页码变化)整页替换:Reset 无动画;同页筛选:增量 diff,动画只作用于真实变化的项(照抄 Papers)
            bool pageChanged = CurrentPage != pageBefore;

            // 筛选无结果时显示提示（未扫描到组件的引导已在上方独立分支处理）
            ShowTip(NoResultTip, filteredResult.Count == 0);

            // 填充当前页(分页模式每页最多 90 项,无需分批;照抄 Papers)
            var uiQueue = DispatcherQueue;
            uiQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested) return;
                if (pageChanged)
                {
                    FilteredComponents.Clear();
                    foreach (var item in pageItems)
                        FilteredComponents.Add(item);
                }
                else
                {
                    ApplyComponentListDiff(FilteredComponents, pageItems);
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "筛选组件时出现异常。");
        }
    }

    /// <summary>增量同步列表:删除/插入/移动只作用于真实变化的项,触发 GridView 补位动画(照抄 Papers.ApplyListDiff)</summary>
    private static void ApplyComponentListDiff(ObservableCollection<ComponentInfo> target, IReadOnlyList<ComponentInfo> desired)
    {
        // 1) 删除:目标有、期望没有的项(移除后剩余项自动补位动画)
        var desiredSet = new HashSet<ComponentInfo>(desired);
        for (int i = target.Count - 1; i >= 0; i--)
            if (!desiredSet.Contains(target[i]))
                target.RemoveAt(i);

        // 2) 重排 + 新增:按期望顺序双指针同步(删除后 target 是 desired 的子序列;Move 触发容器平移动画,Insert 为新增)
        int targetIdx = 0;
        for (int i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (targetIdx < target.Count && ReferenceEquals(target[targetIdx], item))
            {
                targetIdx++;
                continue;
            }
            int found = -1;
            for (int j = targetIdx + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], item)) { found = j; break; }
            }
            if (found >= 0)
            {
                target.Move(found, targetIdx);
                targetIdx++;
            }
            else
            {
                target.Insert(targetIdx, item);
                targetIdx++;
            }
        }
    }

    /// <summary>淡入淡出切换空状态提示(120ms,匹配列表动画节奏;照抄 Papers.ShowTip)</summary>
    private static void ShowTip(FrameworkElement tip, bool show)
    {
        if (show)
        {
            if (tip.Visibility == Visibility.Visible) return;
            tip.Opacity = 0;
            tip.Visibility = Visibility.Visible;
            AnimateTipOpacity(tip, 1, null);
        }
        else
        {
            if (tip.Visibility == Visibility.Collapsed) return;
            AnimateTipOpacity(tip, 0, () => tip.Visibility = Visibility.Collapsed);
        }
    }

    private static void AnimateTipOpacity(FrameworkElement tip, double to, Action? onCompleted)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(120),
        };
        Storyboard.SetTarget(animation, tip);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        if (onCompleted != null)
            storyboard.Completed += (s, e) => onCompleted();
        storyboard.Begin();
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

    // 弹层(菜单/Flyout)不自动继承主窗口运行时主题,打开时显式应用(公共逻辑见 App.ApplyFlyoutTheme)
    private void FlyoutThemeRefresh_Opened(object sender, object e) => App.ApplyFlyoutTheme(sender, e);

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
        var pressTime = DateTime.Now; // 记录按下时刻(旋转动画 2 秒)
        HideComponentContextMenu();

        // 先清空列表再扫描：旧数据先撤下，扫描完成后由 LoadComponents 回填。
        // 注意不能复用 LoadComponents 里的清理——那边刻意不清显示列表（切页缓存优化，见其 497 行注释），
        // 因此这里手动清 UI 集合 + 筛选管道 + 多选状态 + 页码（顺序照抄 LoadComponents 490-495 行）。
        FilteredComponents.Clear();
        _filteredComponents = [];
        _allComponents = [];
        foreach (var item in SelectedComponents)
            item.IsSelected = false;
        SelectedComponents.Clear();
        DisplayedSelectedComponents.Clear();
        IsMultiSelectMode = false;
        SelectedComponent = null;
        CurrentPage = 1;
        NotifyPagerStateChanged();
        ShowTip(NoResultTip, false);
        ShowTip(NoScanResultTip, false);
        Log.Information("刷新组件：已清空显示列表，等待扫描回填");

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
        catch (Exception ex)
        {
            Log.Error(ex, "刷新组件列表失败");
        }
        finally
        {
            _isRefreshing = false;
            // 等旋转动画播完(按下后 2 秒)再启用按钮,保证动画完整播放
            var elapsed = (DateTime.Now - pressTime).TotalMilliseconds;
            if (elapsed < 2000)
            {
                await Task.Delay((int)(2000 - elapsed));
            }
            RefreshButton.IsEnabled = true;
        }
    }

    private async void CopyComponent_Click(object sender, RoutedEventArgs e)
    {
        try
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
        catch (Exception ex)
        {
            Log.Error(ex, "复制组件文件夹失败");
        }
        finally
        {
            // 动画不依赖复制结果,即使复制抛异常/无选中项也执行
            // 目标图标:CommandBar 按钮/右键菜单 → ToolbarCopyIcon;详情面板按钮 → DetailCopyIcon
            var targetIcon = sender is AppBarButton ? ToolbarCopyIcon : DetailCopyIcon;
            if (targetIcon != null)
                await PlayCopyCheckAnimationAsync(targetIcon);
        }
    }


    // 复制成功反馈(单 FontIcon 序列):淡出 → 切勾 → 从左往右扫出 → 停留 → 淡出 → 切回复制 → 淡入
    // (与 Papers 页复制按钮动画一致)
    private int _copyCheckAnimationGeneration;
    private Microsoft.UI.Composition.InsetClip? _copyCheckClip; // 勾扫出的 clip

    private async Task PlayCopyCheckAnimationAsync(FontIcon targetIcon)
    {
        int gen = ++_copyCheckAnimationGeneration;

        // 点击处理器同一帧做了大量同步变更,此帧内 StartAnimation 会被 Composition
        // 帧调度丢弃/延迟(项目已定位根因)。整体包进 DispatcherQueue.TryEnqueue 排到下一个空闲帧起跑。
        var tcs = new TaskCompletionSource();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (gen != _copyCheckAnimationGeneration) { tcs.TrySetResult(); return; } // 排队期间已作废

            var visual = ElementCompositionPreview.GetElementVisual(targetIcon);
            var compositor = visual.Compositor;

            // 复位:可见、无裁剪
            visual.StopAnimation("Opacity");
            visual.Opacity = 1f;
            visual.Clip = null;
            _copyCheckClip?.StopAnimation("RightInset");
            _copyCheckClip = null;

            // 淡出(Opacity 1→0)
            var fadeOut = compositor.CreateScalarKeyFrameAnimation();
            fadeOut.Target = "Opacity";
            fadeOut.InsertKeyFrame(0f, 1f);
            fadeOut.InsertKeyFrame(1f, 0f,
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.17f, 0.67f), new Vector2(0.83f, 0.67f)));
            fadeOut.Duration = TimeSpan.FromMilliseconds(150);
            visual.StartAnimation("Opacity", fadeOut);

            tcs.TrySetResult();
        });
        await tcs.Task;
        if (gen != _copyCheckAnimationGeneration) return; // 过期续体直接作废(代次守卫)

        await Task.Delay(150); // 淡出完成
        if (gen != _copyCheckAnimationGeneration) return;

        // 切为勾
        targetIcon.Glyph = "\uE73E";

        // 勾从左往右扫出(InsetClip RightInset 20→0)
        var tcs2 = new TaskCompletionSource();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (gen != _copyCheckAnimationGeneration) { tcs2.TrySetResult(); return; }

            var visual = ElementCompositionPreview.GetElementVisual(targetIcon);
            var compositor = visual.Compositor;
            visual.Clip = null;
            _copyCheckClip?.StopAnimation("RightInset");
            var clip = compositor.CreateInsetClip();
            clip.RightInset = 20f;
            visual.Clip = clip;
            _copyCheckClip = clip;

            var reveal = compositor.CreateScalarKeyFrameAnimation();
            reveal.Target = "RightInset";
            reveal.InsertKeyFrame(0f, 20f);
            reveal.InsertKeyFrame(1f, 0f,
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.17f, 0.67f), new Vector2(0.83f, 0.67f)));
            reveal.Duration = TimeSpan.FromMilliseconds(300);
            clip.StartAnimation("RightInset", reveal);

            tcs2.TrySetResult();
        });
        await tcs2.Task;
        if (gen != _copyCheckAnimationGeneration) return;

        // 勾显示时淡入到完全可见(Opacity 0→1,与扫出并行)
        DispatcherQueue.TryEnqueue(() =>
        {
            if (gen != _copyCheckAnimationGeneration) return;
            var visual = ElementCompositionPreview.GetElementVisual(targetIcon);
            var compositor = visual.Compositor;
            visual.Opacity = 0f;
            var fadeIn = compositor.CreateScalarKeyFrameAnimation();
            fadeIn.Target = "Opacity";
            fadeIn.InsertKeyFrame(0f, 0f);
            fadeIn.InsertKeyFrame(1f, 1f,
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.17f, 0.67f), new Vector2(0.83f, 0.67f)));
            fadeIn.Duration = TimeSpan.FromMilliseconds(300);
            visual.StartAnimation("Opacity", fadeIn);
        });

        await Task.Delay(1200); // 勾停留(300ms 扫出+淡入 + 900ms 停留)
        if (gen != _copyCheckAnimationGeneration) return; // 过期续体直接作废(代次守卫)

        // 勾淡出(Opacity 1→0)
        var tcs3 = new TaskCompletionSource();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (gen != _copyCheckAnimationGeneration) { tcs3.TrySetResult(); return; }
            var visual = ElementCompositionPreview.GetElementVisual(targetIcon);
            var compositor = visual.Compositor;
            visual.StopAnimation("Opacity");
            visual.Opacity = 1f;
            var fadeOut2 = compositor.CreateScalarKeyFrameAnimation();
            fadeOut2.Target = "Opacity";
            fadeOut2.InsertKeyFrame(0f, 1f);
            fadeOut2.InsertKeyFrame(1f, 0f,
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.17f, 0.67f), new Vector2(0.83f, 0.67f)));
            fadeOut2.Duration = TimeSpan.FromMilliseconds(150);
            visual.StartAnimation("Opacity", fadeOut2);
            tcs3.TrySetResult();
        });
        await tcs3.Task;
        if (gen != _copyCheckAnimationGeneration) return;

        await Task.Delay(150); // 淡出完成
        if (gen != _copyCheckAnimationGeneration) return;

        // 切回复制图标 + 移除裁剪 + 淡入
        targetIcon.Glyph = "\uE8C8";
        _copyCheckClip?.StopAnimation("RightInset");
        _copyCheckClip = null;
        var v = ElementCompositionPreview.GetElementVisual(targetIcon);
        v.StopAnimation("Opacity");
        v.Clip = null;
        v.Opacity = 0f;
        var fadeIn2 = v.Compositor.CreateScalarKeyFrameAnimation();
        fadeIn2.Target = "Opacity";
        fadeIn2.InsertKeyFrame(0f, 0f);
        fadeIn2.InsertKeyFrame(1f, 1f,
            v.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.17f, 0.67f), new Vector2(0.83f, 0.67f)));
        fadeIn2.Duration = TimeSpan.FromMilliseconds(150);
        v.StartAnimation("Opacity", fadeIn2);
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

        // 导航栏徽标:显示本次待提取组件数(新任务开始,复位失败红标)
        _navBadgeError = false;
        NavBadgeService.SetBadge("InstalledComponents", items.Count);

        int successCount = 0;
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
                successCount++;
                // 导航栏徽标:剩余 = 总数 - 成功数
                NavBadgeService.SetBadge("InstalledComponents", items.Count - successCount);
                Log.Information("组件 {Title} 已提取到 {Path}", item.Title, targetDir);
            }

            await Helper.DialogHelper.ShowMessageAsync("提取完成",
                $"已提取 {successCount}/{items.Count} 个组件到：\n{downloadPath}");
        }
        catch (Exception ex)
        {
            await Helper.DialogHelper.ShowMessageAsync("提取失败", ex.Message);
            Log.Error(ex, "提取组件失败");
            _navBadgeError = true;
        }
        finally
        {
            // 导航栏徽标:失败 → 红色保留剩余数;正常结束(完成/取消覆盖)隐藏
            if (_navBadgeError)
                NavBadgeService.SetBadge("InstalledComponents", Math.Max(1, items.Count - successCount), NavBadgeState.Error);
            else
                NavBadgeService.SetBadge("InstalledComponents", null);
        }
    }

    private async void UninstallComponent_Click(object sender, RoutedEventArgs e)
    {
        // 照抄 Papers：执行前先收起右键菜单，避免菜单停留在确认对话框上方
        try
        {
            HideComponentContextMenu();

            var items = GetTargetItems();
            if (items.Count == 0) return;

            // 拆分创意工坊(有 WorkshopID,需取消订阅)与非创意工坊(直接删文件)
            var workshopItems = items.Where(i => !string.IsNullOrEmpty(i.WorkshopID)).ToList();
            var nonWorkshopItems = items.Where(i => string.IsNullOrEmpty(i.WorkshopID)).ToList();

            bool confirmed = await Helper.DialogHelper.ShowConfirmDialogAsync("卸载",
                $"确定要卸载选中的 {items.Count} 个组件吗？\n\n" +
                (workshopItems.Count > 0
                    ? $"创意工坊组件 {workshopItems.Count} 个:将取消订阅并删除本地文件。\n"
                    : "") +
                (nonWorkshopItems.Count > 0
                    ? $"非创意工坊组件 {nonWorkshopItems.Count} 个:将直接删除本地文件。"
                    : ""),
                "卸载",
                "取消");
            if (!confirmed) return;

            await UninstallComponentsAsync(workshopItems, nonWorkshopItems);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "卸载组件失败");
        }
    }

    /// <summary>
    /// 卸载:创意工坊组件先取消订阅(Steamworks 不可用时弹窗让用户选择是否继续删非创意工坊项),
    /// 然后删除本地文件并清 acf 键值;非创意工坊组件直接删本地文件。
    /// </summary>
    private async Task UninstallComponentsAsync(List<ComponentInfo> workshopItems, List<ComponentInfo> nonWorkshopItems)
    {
        var service = Service.SteamWorkshopService.GetInstance();

        // 创意工坊项:逐个取消订阅,收集成功的(失败的不删文件,避免 Steam 重新下载后文件缺失)
        var unsubscribedWorkshopItems = new List<ComponentInfo>();
        if (workshopItems.Count > 0)
        {
            if (!service.IsAvailable)
            {
                // Steamworks 不可用:无法取消订阅,弹窗让用户选择(决策点,需用户确认)
                bool continueDelete = await Helper.DialogHelper.ShowConfirmDialogAsync(
                    "无法取消订阅",
                    $"Steamworks 不可用,无法取消订阅 {workshopItems.Count} 个创意工坊组件(请确认 Steam 正在运行)。\n\n" +
                    (nonWorkshopItems.Count > 0
                        ? $"是否继续卸载 {nonWorkshopItems.Count} 个非创意工坊组件?"
                        : "是否仍要删除本地文件?"),
                    nonWorkshopItems.Count > 0 ? "继续卸载其它" : "仍要删除",
                    "取消");
                if (!continueDelete) return;

                // 用户选择继续:跳过创意工坊项,只删非创意工坊项
                foreach (var item in nonWorkshopItems)
                {
                    await DeleteComponentCoreAsync(item, skipConfirm: true);
                }
                return;
            }

            foreach (var item in workshopItems)
            {
                if (ulong.TryParse(item.WorkshopID, out var wid) && await service.UnsubscribeAsync(wid))
                    unsubscribedWorkshopItems.Add(item);
            }

            if (unsubscribedWorkshopItems.Count == 0 && workshopItems.Count > 0)
            {
                // 全部取消订阅失败:询问是否继续删非创意工坊项(决策点,需用户确认)
                bool continueDelete = await Helper.DialogHelper.ShowConfirmDialogAsync(
                    "取消订阅失败",
                    $"向 Steam 发送取消订阅请求失败(0/{workshopItems.Count} 个组件)。\n\n" +
                    (nonWorkshopItems.Count > 0
                        ? $"是否继续卸载 {nonWorkshopItems.Count} 个非创意工坊组件?"
                        : "是否仍要删除本地文件?"),
                    nonWorkshopItems.Count > 0 ? "继续卸载其它" : "仍要删除",
                    "取消");
                if (!continueDelete) return;

                foreach (var item in nonWorkshopItems)
                {
                    await DeleteComponentCoreAsync(item, skipConfirm: true);
                }
                return;
            }
        }

        // 删除取消订阅成功的创意工坊组件本地文件(成功即自动继续,不再弹模态框打断)
        foreach (var item in unsubscribedWorkshopItems)
        {
            await DeleteComponentCoreAsync(item, skipConfirm: true);
        }

        // 非创意工坊项:直接删本地文件
        foreach (var item in nonWorkshopItems)
        {
            await DeleteComponentCoreAsync(item, skipConfirm: true);
        }
    }

    private async Task DeleteComponentCoreAsync(ComponentInfo item, bool skipConfirm = false)
    {
        if (item == null || item.FolderPath == null) return;

        try
        {
            // 创意工坊组件才有 WorkshopID,非创意工坊为 null(RemoveWorkshopKeyFromAcfAsync 内部对空值安全返回)
            if (!string.IsNullOrEmpty(item.WorkshopID))
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

    private async Task ComponentPropertiesAsync()
    {
        try
        {
        HideComponentContextMenu();
        // 多选模式:为每个选中组件打开独立属性窗口(组件无 project.json 可配置属性,只显示文件属性页)
        var items = IsMultiSelectMode && SelectedComponents.Count > 0
            ? SelectedComponents.ToList()
            : SelectedComponent != null
                ? new List<ComponentInfo> { SelectedComponent }
                : [];
        if (items.Count == 0) return;
        // 超过5个弹窗确认(去重由 PropertiesWindow.Open 内部处理)
        if (PropertiesWindow.OpenWindowCount + items.Count > 5)
        {
            var dlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = App.GetPopupTheme(),
                Title = "打开多个属性窗口",
                Content = $"将打开 {items.Count} 个属性窗口（当前已有 {PropertiesWindow.OpenWindowCount} 个），是否继续？",
                PrimaryButtonText = "打开",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        }
        foreach (var c in items)
            PropertiesWindow.Open(ToWallpaperItem(c), showPropsPage: false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开属性窗口失败");
        }
    }
    private void ComponentProperties_Click(object sender, RoutedEventArgs e)
    {
        _ = ComponentPropertiesAsync();
    }

    /// <summary>ComponentInfo → WallpaperItem 映射(组件没有的字段置空,独立属性窗口文件页显示 "-")</summary>
    private static WallpaperItem ToWallpaperItem(ComponentInfo c) => new()
    {
        Title = c.Title,
        Preview = c.Preview,
        Type = c.ComponentType switch
        {
            ComponentType.Layer => "图层",
            ComponentType.Script => "脚本",
            ComponentType.Effect => "特效",
            _ => "未知"
        },
        ContentRating = c.ContentRating,
        Tags = c.Tags,
        Description = c.Description,
        FileSize = c.FileSize,
        FolderPath = c.FolderPath,
        WorkshopID = c.WorkshopID,
        CreationTime = c.CreationTime,
        UpdateTime = c.CreationTime,
        AcfUpdateTime = c.AcfUpdateTime
    };

    private void OnTagDisplayChanged(object sender, RoutedEventArgs e)
    {
        // 优化:不再重置 ItemsSource 重建全列表——只遍历可见容器手动刷新角标(照 Papers)
        // 非反射(AOT 兼容):ItemsPanelRoot 返回类型是 Panel(基类),Children 是 Panel 属性,直接访问即可,不强转 ItemsWrapGrid
        if (ComponentsGridView.ItemsPanelRoot is not { } panelRoot) return;
        foreach (var child in panelRoot.Children)
        {
            if (child is not GridViewItem container) continue;
            if (container.ContentTemplateRoot is not Grid root) continue;
            if (ComponentsGridView.ItemFromContainer(container) is ComponentInfo item)
                UpdateTagBadge(root, item);
        }
    }

    /// <summary>更新卡片右上角标签(按当前标签模式;容器绑定时也调用,照 Papers)</summary>
    private void UpdateTagBadge(Grid root, ComponentInfo item)
    {
        if (root.FindName("TagDisplayBorder") is not Border border) return;
        int index = ViewModel.ComponentsDisplayVM.ComponentTagDisplayIndex;
        bool visible = index != 4; // 模式 4=None:隐藏(与 VM TagDisplayVisibility 一致)
        border.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        if (border.Child is TextBlock tb)
            tb.Text = new ComponentsTagContentChoose().Convert(item, null, "", "") as string ?? "";
    }

    /// <summary>Skia 流式 GIF 播放(GIF 时覆盖 BitmapImage,照 Papers)</summary>
    private static void UpdateSkiaGif(Grid root, ComponentInfo item)
    {
        if (root.FindName("ItemPreviewImage") is not Image img) return;
        if (root.FindName("SkiaGifCanvas") is not SkiaGifView skia) return;
        bool isGif = !string.IsNullOrEmpty(item.Preview) && item.Preview.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        if (isGif)
        {
            skia.Visibility = Visibility.Visible;
            img.Visibility = Visibility.Collapsed; // 隐藏 BitmapImage,避免双解码
            skia.Start(item.Preview!);
        }
        else
        {
            skia.Stop();
            skia.Visibility = Visibility.Collapsed;
            img.Visibility = Visibility.Visible;
        }
    }

    /// <summary>窗口级快捷键分发入口:MainWindow.RootGrid_KeyDown 把按键转交到本方法(焦点不在页面子树时页面自身 KeyDown 收不到)。
    /// e.Handled 标记由本方法负责;返回后窗口不再重复处理。</summary>
    public void HandleShortcutKey(KeyRoutedEventArgs e) => Page_KeyDown_Core(e);

    /// <summary>页面级快捷键统一入口（原 KeyboardAccelerator 在部分焦点/输入法环境下 Ctrl+I 等组合键不触发，改用 KeyDown 路由事件）。
    /// 焦点在 TextBox 时 Ctrl+A/C、Delete 会被文本框消费并标记 Handled，此处收不到，自动让位。</summary>
    private void Page_KeyDown(object sender, KeyRoutedEventArgs e) => Page_KeyDown_Core(e);

    /// <summary>快捷键分支共用核心:页面自身 KeyDown 与窗口分发两条路径都汇到这里,避免逻辑重复。</summary>
    private void Page_KeyDown_Core(KeyRoutedEventArgs e)
    {
        var ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

        if (ctrl)
        {
            switch (e.Key)
            {
                case VirtualKey.A:
                    SelectAllComponents_Accelerator_Invoked(null!, null!);
                    e.Handled = true;
                    return;
                case VirtualKey.I:
                    InvertSelection_Accelerator_Invoked(null!, null!);
                    e.Handled = true;
                    return;
                case VirtualKey.C:
                    Copy_Accelerator_Invoked(null!, null!);
                    e.Handled = true;
                    return;
                case VirtualKey.E:
                    Properties_Accelerator_Invoked(null!, null!);
                    e.Handled = true;
                    return;
            }
        }
        else if (e.Key == VirtualKey.Delete)
        {
            Delete_Accelerator_Invoked(null!, null!);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.F5)
        {
            // F5 刷新（刷新进行中时由 ComponentsRefresh_Click 内部防连按兜底）
            ComponentsRefresh_Click(null!, null!);
            e.Handled = true;
        }
    }

    private void SelectAllComponents_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
        => SelectAllComponents_Click(sender, null!);

    private void InvertSelection_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
        => InvertSelection_Click(sender, null!);

    private async void Copy_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        try
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
        catch (Exception ex)
        {
            Log.Error(ex, "复制组件文件夹失败");
        }
        finally
        {
            // 快捷键无点击按钮,动画作用于 CommandBar 复制图标
            await PlayCopyCheckAnimationAsync(ToolbarCopyIcon);
        }
    }

    private void Delete_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
        => UninstallComponent_Click(sender, null!);

    private void Properties_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        _ = ComponentPropertiesAsync();
    }

    private void GoToSettings_Click(object sender, RoutedEventArgs e)
    {
        Frame?.Navigate(typeof(Settings));
    }

    // ===================== 多选面板按钮 =====================
    private void SelectAllComponents_Click(object sender, RoutedEventArgs e)
    {
        // 先填充选中集合,后进多选模式:Toggle 期间 Count==0 会被 UpdateMultiSelectCount
        // 的"0 项自动退出"立刻翻回 false(原因同 Papers.SelectAllWallpapers_Click)
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

        if (!IsMultiSelectMode) IsMultiSelectMode = true;

        RefreshDisplayedSelectedComponents(forceRebuild: true);
        UpdateMultiSelectCount();
    }

    private void InvertSelection_Click(object sender, RoutedEventArgs e)
    {
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

        if (!IsMultiSelectMode) IsMultiSelectMode = true;

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
            if (cb.IsChecked == true && !SelectedComponents.Contains(item))
            {
                SelectedComponents.Add(item);
                // 勾选成功(Count>0)才进入多选;取消到 0 项时由 UpdateMultiSelectCount 自动退出,
                // 不再无条件重进——避免快速连点时"自动退出"与"强制进入"互搏导致模式横跳(与 Papers 修复一致)
                if (!IsMultiSelectMode)
                {
                    IsMultiSelectMode = true;
                }
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
            if (grid.DataContext is ComponentInfo hovered) hovered.IsHovered = true; // 数据层标记悬停
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
                    // 多选下按住左键刷选 = 取反经过的壁纸(划过选中的取消,划过未选中的选中)
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
            item.IsHovered = false; // 鼠标离开,清除悬停标记(数据层)
            UpdateItemCheckBoxOpacity(grid, item);
            ApplyScaleAnimation(grid, 1.0f);

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
                    var modifiers = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control); // Pointer 事件用 KeyModifiers,GetKeyStateForCurrentThread 会读到过期状态
                    if (modifiers && !_isMultiSelectMode)
                    {
                        // CTRL+按下:先选中再加入集合,最后进多选——若先进多选,setter 里 UpdateAllVisibleCheckBoxes
                        // 等同步调用会以 Count=0 触发 UpdateMultiSelectCount 立即退出多选
                        item.IsSelected = true;
                        if (!SelectedComponents.Contains(item))
                            SelectedComponents.Add(item);
                        UpdateMultiSelectCount();
                        IsMultiSelectMode = true;
                        return;
                    }

                    if (_isMultiSelectMode)
                    {
                        // 点击目标是 CheckBox 时,勾选已由 SelectionCheckBox_Click 全权处理,
                        // 这里不再翻转,避免一次点击被两条路径重复处理(与 Papers 修复一致)
                        if (IsEventSourceInCheckBox(e.OriginalSource)) return;

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
            // 数据层标记悬停:checkbox 绑定 CheckBoxOpacity 自动保持显示(避开 UI 实例/虚拟化问题)
            if (grid.DataContext is ComponentInfo hovered) hovered.IsHovered = true;
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
                    // 多选下按住左键刷选 = 取反经过的壁纸(划过选中的取消,划过未选中的选中)
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
            item.IsHovered = false; // 鼠标离开,清除悬停标记(数据层)
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
                    var modifiers = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control); // Pointer 事件用 KeyModifiers,GetKeyStateForCurrentThread 会读到过期状态
                    if (modifiers && !_isMultiSelectMode)
                    {
                        // CTRL+按下:先选中再加入集合,最后进多选——若先进多选,setter 里 UpdateAllVisibleCheckBoxes
                        // 等同步调用会以 Count=0 触发 UpdateMultiSelectCount 立即退出多选
                        item.IsSelected = true;
                        if (!SelectedComponents.Contains(item))
                            SelectedComponents.Add(item);
                        UpdateMultiSelectCount();
                        IsMultiSelectMode = true;
                        return;
                    }

                    if (_isMultiSelectMode)
                    {
                        // 点击目标是 CheckBox 时,勾选已由 SelectionCheckBox_Click 全权处理(同 ContentItem_PointerPressed)
                        if (IsEventSourceInCheckBox(e.OriginalSource)) return;

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

        // checkbox 可见性由绑定 CheckBoxOpacity(IsSelected || IsInMultiSelectMode || IsHovered)驱动,
        // 这里只做最终兜底同步(非多选、未选中、未悬停 → 隐藏)
        var checkBox = FindCheckBoxInGrid(grid);
        if (checkBox != null)
        {
            if (!IsMultiSelectMode && !item.IsSelected && !item.IsHovered)
            {
                checkBox.Opacity = 0;
            }
            else
            {
                checkBox.Opacity = 1;
            }
        }
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

    /// <summary>判断指针事件源是否位于卡片 CheckBox 内(含其内部元素),用于拦截 PointerPressed 的双路径翻转。</summary>
    private static bool IsEventSourceInCheckBox(object? originalSource)
    {
        if (originalSource is not DependencyObject current) return false;
        while (current != null)
        {
            if (current is CheckBox) return true;
            current = VisualTreeHelper.GetParent(current) as DependencyObject;
        }
        return false;
    }

    private void UpdateAllVisibleCheckBoxes()
    {
        foreach (var gv in AllComponentGridViews)
        {
            if (gv == null) continue;
            for (int i = 0; i < gv.Items.Count; i++)
            {
                if (gv.ContainerFromIndex(i) is not GridViewItem container) continue;
                // 容器 Content = DataTemplate 根(Grid,DataContext = ComponentInfo)
                if (container.Content is Grid grid && grid.DataContext is ComponentInfo item)
                    UpdateItemCheckBoxOpacity(grid, item);
            }
        }
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
        bool grew = count > _lastStackCount && _lastStackCount > 0; // 新增了卡片(初始化不算)
        for (int i = 0; i < count; i++)
        {
            var container = StackedImagesControl.ContainerFromIndex(i) as FrameworkElement;
            if (container == null) continue;

            container.Visibility = Visibility.Visible;
            int depth = count - 1 - i; // 集合尾=最新:深度 0 居中,越老越深(朝左上)
            ApplyStackAnimation(container, depth, entering: grew && i == count - 1); // 最后一张=新卡
            Canvas.SetZIndex(container, i); // 新卡 i 最大 => 最上层
        }
        _lastStackCount = count;
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
                visual.StopAnimation("Opacity");
                visual.Scale = Vector3.One;      // 复位缩放,防深度缩小残留到容器复用
                visual.Opacity = 1f;             // 复位透明度(历史动画保险)
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

    private static void ApplyStackAnimation(FrameworkElement element, int depth, bool entering = false)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        Compositor compositor = visual.Compositor;

        // 1:1 正方形中心点
        float size = 200f;
        visual.CenterPoint = new Vector3(size / 2, size / 2, 0f);

        // 整齐 deck 层叠(Papers 同步):所有卡片正对(0°)。调用方传入 depth(距最新层数,最新=0):
        // 最新卡居中原位,越旧的卡越朝左上退 8px——新卡加入时全部旧卡深度+1,整摞向左上平移一格;
        // 新卡自身由 entering 从右下(+2 步)滑入居中位。
        const float StepX = 8f, StepY = 8f;
        float offsetX = -depth * StepX;
        float offsetY = -depth * StepY;

        if (entering)
        {
            // 入场起点:右下两步之外,随后动画滑入 d0 原位(插值从当前值出发,无需起始帧)
            visual.Offset = new Vector3(StepX * 2, StepY * 2, 0f);
        }

        // 深度缩放:距最新越远越小(1.0 → -3%/层)——"近大远小"透视层级,卡片保持完全不透明
        const float ScaleStep = 0.03f;
        float depthScale = Math.Clamp(1f - depth * ScaleStep, 0.88f, 1f);

        // 使用动画平滑移动到新位置（防止新增图片时旧图片位置突跳)——KeyFrame 确定性时间轴
        var offsetAnim = compositor.CreateVector3KeyFrameAnimation();
        offsetAnim.Target = "Offset";
        offsetAnim.InsertKeyFrame(1f, new Vector3(offsetX, offsetY, 0f),
            compositor.CreateCubicBezierEasingFunction(new Vector2(0.17f, 0.67f), new Vector2(0.83f, 0.67f)));
        offsetAnim.Duration = TimeSpan.FromMilliseconds(150);

        // 历史卡片可能带旋转残留,统一动画归零(整齐层叠要求正对)
        var rotationZeroAnim = compositor.CreateScalarKeyFrameAnimation();
        rotationZeroAnim.Target = "RotationAngleInDegrees";
        rotationZeroAnim.InsertKeyFrame(1f, 0f);
        rotationZeroAnim.Duration = TimeSpan.FromMilliseconds(150);

        // 缩放同步动画(与位移同缓动同时长;CenterPoint 已设为卡片中心,向内收缩)
        var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
        scaleAnim.Target = "Scale";
        scaleAnim.InsertKeyFrame(1f, new Vector3(depthScale, depthScale, 1f),
            compositor.CreateCubicBezierEasingFunction(new Vector2(0.17f, 0.67f), new Vector2(0.83f, 0.67f)));
        scaleAnim.Duration = TimeSpan.FromMilliseconds(150);

        visual.StartAnimation("Offset", offsetAnim);
        visual.StartAnimation("RotationAngleInDegrees", rotationZeroAnim);
        visual.StartAnimation("Scale", scaleAnim);
    }

    /// <summary>打断 SinglePreviewBorder 上的残留动画(与 Papers.CancelAllAnimations 对齐的精简版)。</summary>
    private void CancelAllAnimations()
    {
        var singleVisual = ElementCompositionPreview.GetElementVisual(SinglePreviewBorder);
        if (singleVisual != null)
        {
            singleVisual.StopAnimation("Scale");
            singleVisual.StopAnimation("Offset");
            singleVisual.StopAnimation("Opacity");
        }
        StopAllStackAnimations();
    }

    /// <summary>多选/单选视觉切换（与 Papers.ToggleMultiSelectVisuals 同步:Storyboard 交叉淡入淡出）</summary>
    private void ToggleMultiSelectVisuals(bool isMulti)
    {
        // 过渡 = 淡入淡出:Composition 逐值动画在本环境概率性延迟提交,
        // 改用 XAML Storyboard 双动画交叉——单图淡出 / 堆叠图淡入,时间轴由框架保证(与 Papers 一致)
        CancelAllAnimations();

        if (isMulti)
        {
            NoSelectionHintText.Visibility = Visibility.Collapsed; // 最高优先级:进入多选即刻隐藏无结果提示

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

            SinglePreviewBorder.CornerRadius = new CornerRadius(8);

            // 堆叠图先摆到可见状态但全透明,再淡入;淡入完成后隐藏单图面板
            StackedImagesControl.Opacity = 0;
            StackedImagesControl.Visibility = Visibility.Visible;

            var fadeInStack = new DoubleAnimation { To = 1.0, Duration = TimeSpan.FromMilliseconds(180) };
            Storyboard.SetTarget(fadeInStack, StackedImagesControl);
            Storyboard.SetTargetProperty(fadeInStack, "Opacity");
            var stackBoard = new Storyboard();
            stackBoard.Children.Add(fadeInStack);
            stackBoard.Completed += (s, e) =>
            {
                SinglePreviewBorder.Visibility = Visibility.Collapsed;
                SingleSelectionInfoPanel.Visibility = Visibility.Collapsed;
            };
            stackBoard.Begin();

            // 单图同时淡出(180ms 同速交叉),完成后复位透明度并隐藏单图
            var fadeOutSingle = new DoubleAnimation { To = 0.0, Duration = TimeSpan.FromMilliseconds(180) };
            Storyboard.SetTarget(fadeOutSingle, SinglePreviewBorder);
            Storyboard.SetTargetProperty(fadeOutSingle, "Opacity");
            var singleBoard = new Storyboard();
            singleBoard.Children.Add(fadeOutSingle);
            singleBoard.Completed += (s, e) =>
            {
                SinglePreviewBorder.Opacity = 1; // 复位透明度供下次显示
                SinglePreviewBorder.Visibility = Visibility.Collapsed;
            };
            singleBoard.Begin();

            MultiSelectionInfoPanel.Visibility = Visibility.Visible;
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

            SinglePreviewBorder.CornerRadius = new CornerRadius(0);
            SinglePreviewBorder.Opacity = 1;  // 复位,防上次淡出被快速操作打断后残留半透明
            StackedImagesControl.Opacity = 1;

            foreach (var item in SelectedComponents)
            {
                item.IsSelected = false;
            }
            SelectedComponents.Clear();
            DisplayedSelectedComponents.Clear();

            RefreshDisplayedSelectedComponents(forceRebuild: true);
            UpdateMultiSelectCount();
        }
    }
}

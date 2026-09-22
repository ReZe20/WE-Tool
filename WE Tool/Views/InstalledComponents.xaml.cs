using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
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
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using WE_Tool.AnimatedVisuals;
using WE_Tool.Helper;
using WE_Tool.Controls;
using WE_Tool.Converters;
using WE_Tool.Models;
using WE_Tool.Service;
using WE_Tool.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace WE_Tool;

public sealed partial class InstalledComponents : Page, INotifyPropertyChanged
{
    private List<ComponentInfo> _allComponents = [];
    private string _searchText = "";
    private bool _isUpdating;
    private bool _isFirstLoad = true;

    // [同步 Papers 2026-09] 图标卡片静态预览的解码宽度上限(物理像素)。
    // 卡片档位最大 300 DIP,高 DPI(150%)下约 450 物理像素,取 480 覆盖并留余量。
    // 库里有 1024×1024 的 preview.jpg(全尺寸解码约 4MB/张),按卡片实际尺寸解码可大幅降低实化开销与内存。
    // 注意:DecodePixelWidth 必须在 UriSource 赋值之前设置才生效。
    private const int IconPreviewDecodeWidth = 480;

    // [内容模式走 Skia 2026-09] 内容模式缩略图(80×80 DIP)静态图的解码宽度上限。
    // 80 DIP 在 150% DPI 下约 120 物理像素、200% 下约 160;取 200 覆盖并留余量。
    // 内容模式一屏可见行数多,全尺寸解码(库里有 1024×1024 的 preview.jpg)收益比图标模式更明显。
    private const int ContentPreviewDecodeWidth = 200;

    // [同步 Papers 2026-09] ItemsRepeater 预渲染缓冲(视口倍数),三套模式列表统一设置。
    // 背景:Papers 侧迁移 ItemsRepeater 时丢掉了 GridView 时代的 CacheLength 设置,一直走系统默认(约 4 屏),
    // 每次实化/回收的容器数翻数倍;窗口化(列少 → 内容极高)时这笔固定开销会被放大成可见掉帧。
    // 取值:0 太激进(滚动时现造容器),沿用定稿的"备货 1 屏"。
    private const double RepeaterCacheLength = 1;

    private bool _componentsCacheApplied;
    private bool _isLeftMouseButtonPressed;
    // ===== [Shift 区间刷选,同步 Papers] 图标模式;Shift+拖动从锚点延伸连续区间 =====
    private ComponentInfo? _shiftAnchorItem;   // Shift 区间锚点(按下处)
    private bool _shiftDragActive;             // Shift 区间刷选进行中
    private bool _suppressItemReleased;        // 区间刷选结束抑制 Item 单选释放
    // [区间改追加 2026-09-22,同步 Papers] 本次区间手势"自己亲手加进去"的项:区间往回缩时只回收这些,
    // 手势开始前就已选中的(全选/Ctrl 攒下的)一概不动 —— 这就是"追加"与旧的"替换"的分界。
    private readonly HashSet<ComponentInfo> _shiftRangePicked = new();
    // ===== [右键释放检测,同步 Papers] 右键按下→松开手动弹菜单(绕开系统"右键带移动抑制"手势判定) =====
    private bool _isRightButtonPressed;       // 右键是否按下(按下置位,松开检测消费)
    private bool _rightMenuShownThisGesture;  // 本次右键手势是否已弹菜单(防双弹)
    private Point _rightPressPagePoint;        // [空白区右键 2026-09-21] 右键按下点(根坐标),空白判定用
    private AppBarButton? _pressedButton; // 当前被按下的 CommandBar 按钮(指针捕获后释放弹回用)
    private bool _isComponentItemTapped;
    private bool _isMultiSelectMode;
    private bool _isBatchUpdating;

    // ===================== [a11y 2026-09,同步 Papers] 列表键盘可达 =====================
    // 这一组行为合起来构成"纯键盘 + 讲述人"可用的列表(实现与 Papers.xaml.cs 同名同法):
    //   卡片进 Tab 序            卡片是停留点,带朗读名(=标题),卡内标题文字归 Raw 视图免重复朗读
    //   点击卡片交焦点 / Ctrl+L  点谁焦点就落谁;Ctrl+L 从导航栏/工具栏直达列表,落点=上次停留过的卡
    //   焦点即选中               单选模式下键盘焦点落到哪张卡就选中哪张
    //   Ctrl+方向键 / Shift+方向键 逐张加选 / 从锚点延伸区间(区间是追加,不抹已有选择)
    // 相互依赖:后三条都要"卡片是 Tab 停留点",否则 Focus() 直接返回 false。
    private int _listAnchorIndex = -1;   // 列表里最后停留过的卡下标:供 Ctrl+L 使用
    private bool _suppressCtrlFocusMultiSelect;   // Ctrl+L 程序化搬焦点这一下,不当作 Ctrl 划选
    private ComponentInfo? _shiftKeyAnchorItem;   // 键盘区间锚点:Shift 没按住时,每聚焦一张就刷新成这张

    /// <summary>导航徽标是否处于失败(红)状态:失败后保持红色,直到下次提取开始才复位。</summary>
    private bool _navBadgeError;
    private int _lastStackCount;
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

    /// <summary>[同步 Papers 2026-09] 给三套模式列表设置预渲染缓冲(一次性)。
    /// 迁移 ItemsRepeater 时丢掉 GridView 时代的 CacheLength 设置,一直走系统默认(约 4 屏)——
    /// 每次实化/回收的容器数翻数倍;窗口化(列少 → 内容极高)时这笔固定开销被放大成可见掉帧。
    /// 三个 repeater 均为纵向滚动,故设 VerticalCacheLength(单位 = 视口倍数)。</summary>
    private void ApplyComponentsRepeaterCacheLength()
    {
        if (_componentsCacheApplied) return;
        // 控件未挂载时先不设(Loaded 内调用,正常都已就绪);未设成则下次 Loaded 再试
        if (ComponentsRepeater == null || ComponentsContentRepeater == null || ComponentsListRepeater == null)
            return;
        ComponentsRepeater.VerticalCacheLength = RepeaterCacheLength;        // 图标模式(UniformGridLayout)
        ComponentsContentRepeater.VerticalCacheLength = RepeaterCacheLength; // 内容模式(StackLayout 单列)
        ComponentsListRepeater.VerticalCacheLength = RepeaterCacheLength;    // 列表模式(UniformGridLayout)
        _componentsCacheApplied = true;
    }

    // ============= ItemsRepeater 列宽钳制(全迁;GridView 全部移除) =============

    /// <summary>[全迁] 列表模式 UniformGridLayout 钳制(400 档位):MinItemWidth ≤ 可用宽,
    /// 否则 itemsPerLine=0 除零崩溃(WinUI #10539)。</summary>
    private void ComponentsListScrollViewExp_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateComponentsListLayoutMinWidth();

    /// <summary>内容模式 ScrollView 尺寸变化(单列 StackLayout 无 MinItemWidth,无需钳制;占位)</summary>
    private void ComponentsContentScrollViewExp_SizeChanged(object sender, SizeChangedEventArgs e)
    {
    }

    /// <summary>列表模式 UniformGridLayout 钳制:MinItemWidth = Min(400, 可用宽-8)。</summary>
    private void UpdateComponentsListLayoutMinWidth()
    {
        if (ComponentsListUniformLayoutExp is not UniformGridLayout layout) return;
        double viewport = ComponentsListScrollViewExp.ActualWidth;
        int desired = 400; // 列表模式档位固定 400
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

    // ============ [同步 Papers] ItemsRepeater UniformGridLayout 防崩钳制 ============

    /// <summary>ScrollView 尺寸变化:钳制 UniformGridLayout.MinItemWidth ≤ 可用宽,
    /// 否则可用宽 &lt; MinItemWidth 时 itemsPerLine=0 除零崩溃(WinUI #10539)。图标模式专用。</summary>
    private void ComponentsScrollViewExp_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateComponentsUniformLayoutMinWidth();

    /// <summary>读用户档位(ComponentListMinWidth),钳到可用宽内写回 UniformGridLayout.MinItemWidth。
    /// 可用宽 = ScrollView 内容宽 - 左右 Margin(4+4);ScrollView 未布局时兜底只钳下限。</summary>
    private void UpdateComponentsUniformLayoutMinWidth()
    {
        if (ComponentsUniformLayoutExp is not UniformGridLayout layout) return;
        double viewport = ComponentsScrollViewExp.ActualWidth;
        int desired = ViewModel.ComponentsDisplayVM.ComponentListMinWidth;
        if (viewport > 0)
        {
            // 防崩核心:MinItemWidth 必须 ≤ 可用宽(margin 8)
            double effective = Math.Max(1, Math.Min(desired, viewport - 8));
            if (Math.Abs(layout.MinItemWidth - effective) > 0.5)
                layout.MinItemWidth = effective;
        }
        else if (layout.MinItemWidth <= 0)
        {
            layout.MinItemWidth = desired; // 未布局首帧:先给档位值,等 SizeChanged 再钳
        }
    }

    // ===== [Ctrl+滚轮,同步 Papers] =====
    // ScrollView(新控件)原生把 Ctrl+滚轮当"缩放"消费(ZoomMode=Disabled 也吞事件,官方设计:
    // "pressing Ctrl while scrolling mouse wheel" = zoom),导致 Ctrl+滚轮不滚动。
    // 方案:拦截点放内层 ItemsRepeater(滚轮冒泡先经它,后到 ScrollView 原生处理):
    //   Ctrl+滚轮(未按左键)      = 循环切换图标尺寸档位 + Handled(不缩放下传)
    //   Ctrl+左键按住+滚轮         = 手动滚动(此时左键拖动语义被滚轮替代)
    //   无修饰键滚轮              = 不 Handled,放行给 ScrollView 原生平滑滚动
    private void ComponentsRepeater_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(sender as UIElement).Properties.MouseWheelDelta;
        if (delta == 0) return;

        bool ctrlHeld = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control); // Pointer 事件用 KeyModifiers
        bool leftPressed = _isLeftMouseButtonPressed;

        // Ctrl 且未按左键:切换图标档位
        if (ctrlHeld && !leftPressed)
        {
            var vm = ViewModel.ComponentsDisplayVM;
            int cur = vm.ComponentViewIndex;
            int next = delta > 0 ? (cur + 1) % 3 : (cur + 2) % 3; // 上滚升档,下滚降档,循环 0..2
            vm.ComponentViewIndex = next;
            e.Handled = true;
            return;
        }

        // Ctrl+左键按住:手动垂直滚动(CtrlAwareScrollView 子类已接管,此处兜底;不 Handled 让子类处理)
    }

    /// <summary>当前可见的组件滚动容器(滚动回顶用;三个模式都已迁 ScrollView)</summary>
    private ScrollView? GetVisibleComponentScrollView()
    {
        if (ComponentsScrollViewExp.Visibility == Visibility.Visible) return ComponentsScrollViewExp;
        if (ComponentsContentScrollViewExp.Visibility == Visibility.Visible) return ComponentsContentScrollViewExp;
        if (ComponentsListScrollViewExp.Visibility == Visibility.Visible) return ComponentsListScrollViewExp;
        return null;
    }

    /// <summary>[内容/列表模式焦点可达 2026-09] 当前可见模式对应的 ItemsRepeater。三个 repeater 绑同一个
    /// FilteredComponents 列表,下标通用;但"按某下标取容器""XY 焦点搜索范围"必须按可见模式来 ——
    /// 隐藏模式的容器根本没实化,向其要容器只会拿到 null。</summary>
    private ItemsRepeater? GetVisibleComponentRepeater()
    {
        if (ComponentsScrollViewExp.Visibility == Visibility.Visible) return ComponentsRepeater;
        if (ComponentsContentScrollViewExp.Visibility == Visibility.Visible) return ComponentsContentRepeater;
        if (ComponentsListScrollViewExp.Visibility == Visibility.Visible) return ComponentsListRepeater;
        return null;
    }

    /// <summary>可见模式滚动回顶(分页/刷新后)</summary>
    private void ScrollVisibleComponentGridToTop()
    {
        GetVisibleComponentScrollView()?.ScrollTo(0, 0);
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

        // [同步 Papers] Lottie 动画图标接线:按下/松开分段驱动(见下方各"图标动画"区块)
        ViewIcon_WirePointer();          // 视图图标:按下/松开直接挂按钮自己
        LeftFilterIcon_WirePointer();    // 筛选结果(工具栏最左)图标:与视图同一素材、同一接线
        LeftFilterIcon_WireColor();      // 筛选结果图标颜色同步(选中反相)
        SortIcon_WirePointer();          // 排序图标同款:直接挂按钮自己
        SortDirectionIcon_WirePointer(); // 排序方向图标:两份素材按当前方向换源
        DetailIcons_WirePointer();       // 详情面板复制按钮 + 详情面板开关:按下/松开/勾动画收尾

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
            // [同步 Papers] 排序方向图标:换源只能在"画面正好等于该素材第 0 帧"时做,过渡周期内(按下段+松开段)一律不动
            // 素材,否则会把方向播反或把过渡截断;周期外(如启动读设置/点击切换方向)才在这里同步。不 return:方向变化还要重排列表。
            if (e.PropertyName == nameof(ComponentsDisplayViewModel.SortDirectionGlyph))
            {
                if (!_sortDirectionIconCycleActive) SortDirectionIcon_SyncSource("方向变化");
            }
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
                // 小/中/大档位变化:UniformGridLayout 随档位值联动钳制(照抄 Papers)
                UpdateComponentsUniformLayoutMinWidth(); // 图标模式
                UpdateComponentsListLayoutMinWidth();   // [全迁] 列表模式
            }
        };

        // 首次布局后钳制列宽(防崩;GridView 已全迁 ItemsRepeater)
        this.Loaded += (s, e) =>
        {
            ApplyComponentsRepeaterCacheLength();    // [同步 Papers 2026-09] 先设预渲染缓冲(减少实化/回收容器数)
            UpdateComponentsUniformLayoutMinWidth(); // 图标模式首帧钳制(防崩)
            UpdateComponentsListLayoutMinWidth();    // [全迁] 列表模式首帧钳制
            // [同步 Papers] 排序方向图标:设置已在 App 启动时读入,按真实方向选素材(升序=尖朝下 / 降序=尖朝上);
            // 合成树可能要到本帧末才挂上,隔一拍再补一次。
            SortDirectionIcon_SyncSource("Loaded");
            DispatcherQueue.TryEnqueue(() => SortDirectionIcon_SyncSource("Loaded+队列"));
        };

        // [同步 Papers] ItemsRepeater 容器就绪:设 Image.Source + Skia GIF 切换 + 阴影/角标初始化。
        // 替代原 GridView 的 ContainerContentChanging。元素回收复用也触发。
        ComponentsRepeater.ElementPrepared += (s, e) =>
        {
            if (e.Element is not Grid root) return;
            // 用 e.Index 从 ItemsSource 拿 item(不依赖 DataContext 时机;ElementPrepared 时绑定可能未推送)
            ComponentInfo? item = null;
            if (root.DataContext is ComponentInfo dcItem) item = dcItem;
            else if (e.Index >= 0 && e.Index < FilteredComponents.Count) item = FilteredComponents[e.Index];
            if (item == null) return;
            // [a11y 2026-09,同步 Papers] 卡片 = Tab 停留点 + 朗读名 + 标题去重。
            // [内容/列表模式焦点可达 2026-09] 另外两个 repeater 的同类接线见下方 PrepareRowCardForFocus
            root.IsTabStop = true;               // WinUI3 里 IsTabStop 在 UIElement 上,非 Control 的 Grid 也能进 Tab 序
            root.UseSystemFocusVisuals = true;   // 让系统画焦点框
            // 朗读名用组件标题(数据,非文案),不走 resw
            AutomationProperties.SetName(root, string.IsNullOrEmpty(item.Title) ? "(无标题)" : item.Title);
            // [去重 2026-09] 卡片根已带朗读名(=标题),卡片里的标题 TextBlock 仍是独立可读节点:
            // 讲述人停在卡片上按方向键会把它再念一遍 → 一项读两次。官方文档原话就是"composed UI 会引入
            // duplicate 节点,用 AccessibilityView 归置",故把这条文字设为 Raw(只留在 raw 视图,
            // 不进讲述人主要遍历的 control/content 视图)。只动 UIA 树:渲染/布局/点击/悬停/右键/多选框都不受影响。
            if (root.FindName("ItemTitleText") is TextBlock iconTitleText)
                AutomationProperties.SetAccessibilityView(iconTitleText, AccessibilityView.Raw);
            else
                Log.Warning("[A11y] 未取到卡片标题节点 ItemTitleText,朗读去重未生效");
            root.GotFocus -= CardRoot_GotFocus;  // 幂等:容器回收复用会重复走到这里,先减后加避免订阅叠加
            root.GotFocus += CardRoot_GotFocus;
            // [外观] ThemeShadow 初始化(原 ShadowRect_Loaded 的阴影部分):ItemRootGrid 投影到 ShadowCastGrid
            if (root.FindName("ItemRootGrid") is Grid itemRootGrid && itemRootGrid.Shadow is not ThemeShadow)
            {
                var shadow = new ThemeShadow();
                if (root.FindName("ShadowCastGrid") is Grid shadowCastGrid)
                    shadow.Receivers.Add(shadowCastGrid);
                itemRootGrid.Shadow = shadow;
            }
            // [同步 Papers 2026-09] 卡片图源统一装载(GIF → Skia 流式播放,静态图 → 按卡片尺寸解码);
            // 图标模式与内容模式走同一实现,仅解码宽度不同。
            ApplyComponentPreview(root, item, IconPreviewDecodeWidth);
            UpdateTagBadge(root, item); // 角标按当前标签模式设置
        };

        // [内容/列表模式焦点可达 2026-09,同步 Papers] 把图标模式那套"卡片=Tab 停留点 + 朗读名 + 焦点即选中"
        // 复制到另外两个 repeater。三种模式共用同一个 FilteredComponents 列表,所以下标与选中逻辑全部复用
        // CardRoot_GotFocus,这里只补"停留点 + 系统焦点框 + 朗读名 + 标题节点去重"。
        ComponentsContentRepeater.ElementPrepared += (s, e) => PrepareRowCardForFocus(e, "ContentTitleText");
        ComponentsListRepeater.ElementPrepared += (s, e) => PrepareRowCardForFocus(e, "ListTitleText");

        void PrepareRowCardForFocus(ItemsRepeaterElementPreparedEventArgs e, string titleNodeName)
        {
            if (e.Element is not FrameworkElement root) return;
            ComponentInfo? item = e.Index >= 0 && e.Index < FilteredComponents.Count
                ? FilteredComponents[e.Index]
                : root.DataContext as ComponentInfo;
            if (item == null) return;

            root.IsTabStop = true;
            root.UseSystemFocusVisuals = true;
            AutomationProperties.SetName(root, string.IsNullOrEmpty(item.Title) ? "(无标题)" : item.Title);
            // 行根已经念标题,行内的标题文字要设为 Raw,否则讲述人停在行上按方向键会把它再念一遍
            if (root.FindName(titleNodeName) is TextBlock rowTitleText)
                AutomationProperties.SetAccessibilityView(rowTitleText, AccessibilityView.Raw);
            else
                Log.Warning("[A11y] 未取到行卡标题节点 {Name},朗读去重未生效", titleNodeName);
            root.GotFocus -= CardRoot_GotFocus;   // 幂等:容器回收复用会重复走到这里
            root.GotFocus += CardRoot_GotFocus;
        }

        // [a11y 2026-09,同步 Papers] 焦点落在哪张卡 = 键盘"当前位置":记住锚点,并按模式驱动选中。
        void CardRoot_GotFocus(object sender, RoutedEventArgs e)
        {
            // [内容/列表模式焦点可达 2026-09] item 认定改成三模式通用:内容/列表的行根
            // (ContentItemContainer/ListItemContainer)自己设了 DataContext="{x:Bind}",图标模式的模板根 ItemContainer
            // 没设(item 在里层 ItemRootGrid 上)→ 先读本元素 DataContext,读不到再探里层那一格。
            // 下标不再向某一个 repeater 要:三种模式 ItemsSource 是同一个 FilteredComponents 列表。
            var focusedCard = sender as FrameworkElement;
            ComponentInfo? focusedItem = (focusedCard?.DataContext as ComponentInfo)
                ?? (focusedCard?.FindName("ItemRootGrid") as FrameworkElement)?.DataContext as ComponentInfo;
            var focusedIndex = focusedItem != null ? FilteredComponents.IndexOf(focusedItem) : -1;
            // [列表键盘可达 2026-09] 记住"最后停留过的卡":Ctrl+L 再进列表时回到这里,而不是回列表头
            if (focusedIndex >= 0) _listAnchorIndex = focusedIndex;
            // [Ctrl 焦点多选 2026-09] Ctrl+L 的一次性屏蔽令牌在这里消费:GotFocus 是异步事件(官方文档明示),
            // 所以不能用"Focus() 调用前后复位"来屏蔽,只能由下一次 GotFocus 自己清零。
            var suppressCtrlSelectOnce = _suppressCtrlFocusMultiSelect;
            _suppressCtrlFocusMultiSelect = false;

            // Ctrl/Shift 状态用 GetKeyStateForCurrentThread:本路径是键盘引起的聚焦,读到的是实时按键状态
            // (文件里那条"会读到过期状态"的告诫针对 Pointer 事件);指针路径已被下面的 FocusState 判据排除。
            var ctrlHeldOnFocus = !suppressCtrlSelectOnce
                && (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            var shiftHeldOnFocus = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

            // 区间锚点维护:Shift 没按住时,锚点 = 刚聚焦的这张(所以 Ctrl 连选之后再按 Shift,锚点落在 Ctrl 停住的那张,
            // 而不是 Ctrl 之前那张);Shift 按住时不动锚点,区间才始终是"锚点 → 当前焦点"这一段。
            if (focusedItem != null && !shiftHeldOnFocus)
            {
                _shiftKeyAnchorItem = focusedItem;
                // 锚点一换 = 下一段区间是新的一轮:回收集清零,上一轮手势加进去的项从此归用户管
                _shiftRangePicked.Clear();
            }

            // [Shift 焦点区间 2026-09] 按住 Shift 移焦点 = 从锚点延伸区间(追加,同 Shift+拖动)。
            // 本分支显式排除 Ctrl 同按(!ctrlHeldOnFocus):Ctrl+Shift 按 Ctrl 处理(逐张加选,不动已有选择集合)。
            // 判据保留 FocusState != Pointer:Shift+点击/Shift+拖动走的是鼠标那条老路(Item_PointerPressed 的 shift 分支),
            // 这里不抢,否则区间会被算两遍。
            if (shiftHeldOnFocus && !ctrlHeldOnFocus && focusedCard != null && focusedItem != null
                && focusedCard.FocusState != FocusState.Pointer)
            {
                var rangeAnchor = _shiftKeyAnchorItem;   // 先落局部变量:可空分析对字段比对局部保守
                if (rangeAnchor != null && !ReferenceEquals(rangeAnchor, focusedItem))
                    SelectShiftRange(rangeAnchor, focusedItem);
                return;
            }
            // [Ctrl 焦点多选 2026-09] 按住 Ctrl 移焦点 = 累加多选(键盘版 Ctrl+点击 / Ctrl+划过)。
            // 顺序必须先"置选中 + 加入集合"再进多选模式:反过来会被多选 setter 里同步跑的 UpdateMultiSelectCount
            // 以 Count==0 立刻翻回 false(与"首次全选要按两次"是同一个旧根因),这里照抄既有 Ctrl+点击的顺序。
            if (ctrlHeldOnFocus && focusedCard != null && focusedItem != null
                && focusedCard.FocusState != FocusState.Pointer)
            {
                if (!focusedItem.IsSelected) focusedItem.IsSelected = true;
                if (!SelectedComponents.Contains(focusedItem)) SelectedComponents.Add(focusedItem);
                UpdateMultiSelectCount();
                if (!_isMultiSelectMode)
                {
                    IsMultiSelectMode = true;
                }
                return;
            }

            // [焦点即选中 2026-09] 焦点即选中(只看单选模式):Tab/方向键/Ctrl+L 走到哪张卡,右侧详情面板就切到哪张。
            // 判据用 FocusState != Pointer:指针交互引起的聚焦由 Item_PointerReleased 那条老路负责(带钻入动画),
            // 这里不重复处理,否则"点击某张卡"会因为选中已成事实而丢掉钻入动画。
            if (!_isMultiSelectMode
                && focusedCard != null && focusedItem != null
                && focusedCard.FocusState != FocusState.Pointer
                && SelectedComponent != focusedItem)
            {
                SelectedComponent = focusedItem;
            }
        }
        // 元素移出(回收/滚动走远):停 GIF
        ComponentsRepeater.ElementClearing += (s, e) =>
        {
            if (e.Element is not Grid root) return;
            if (root.FindName("SkiaGifCanvas") is SkiaGifView skia)
                skia.Stop();
        };

        // [内容模式走 Skia 2026-09] 内容模式缩略图(单列行卡左侧 80×80)改用与图标模式同一套图源逻辑:
        // GIF → Skia 流式播放(不再走 BitmapImage AutoPlay 那条 WIC 全帧解码重路径),
        // 静态图 → 按缩略图尺寸解码。回收/复用同样在此收敛(模板不再自带 Image.Source 绑定)。
        ComponentsContentRepeater.ElementPrepared += (s, e) =>
        {
            if (e.Element is not Grid root) return;
            // 用 e.Index 从 ItemsSource 拿 item(与图标模式一致,不依赖 DataContext 时机)
            ComponentInfo? item = null;
            if (root.DataContext is ComponentInfo dcItem) item = dcItem;
            else if (e.Index >= 0 && e.Index < FilteredComponents.Count) item = FilteredComponents[e.Index];
            if (item == null) return;
            ApplyComponentPreview(root, item, ContentPreviewDecodeWidth);
        };
        // 元素移出(回收/滚动走远):停 GIF(与图标模式一致)
        ComponentsContentRepeater.ElementClearing += (s, e) =>
        {
            if (e.Element is not Grid root) return;
            if (root.FindName("SkiaGifCanvas") is SkiaGifView skia)
                skia.Stop();
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
        // 每次新按下复位抑制标志(区间刷选结束的释放会触发 Item 释放/Tapped,需区分)
        _suppressItemReleased = false;

        var pt = e.GetCurrentPoint(null);
        var props = pt.Properties;
        if (props.IsLeftButtonPressed)
        {
            _isLeftMouseButtonPressed = true;
        }
        // [右键释放检测,同步 Papers] 右键按下:置标志(松开时手动弹菜单,绕开系统移动抑制)
        if (props.PointerUpdateKind is Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed)
        {
            _isRightButtonPressed = true;
            _rightMenuShownThisGesture = false;
            _rightPressPagePoint = pt.Position;
        }
        // CommandBar 内按钮按下:记录按钮/捕获指针,松开时按需触发图标动画
        // (AddHandler handledEventsToo:true 能收到 Button 内部的 handled 事件;按下缩小反馈已于 2026-09-16 取消)
        if (e.OriginalSource is FrameworkElement fe && IsDescendantOf(fe, ToolbarCommands))
        {
            if (FindAncestorButton(fe) is { } btn)
            {
                _pressedButton = btn;                       // 记录按下的按钮(供松开时触发图标动画)
                btn.CapturePointer(e.Pointer);              // 捕获指针:移开按钮后释放仍收到事件
                // [2026-09-16 取消顶部栏按下缩小] 用户要求去掉按钮按下缩到 88% 的反馈;PlayPressScale 方法保留未删。
                // PlayPressScale(btn, 0.88f);
                // 刷新/全选/反选/删除 四组图标:按下播第 0→10 帧;松开由 Global_PointerReleased 收尾
                // (在按钮上松开 = 播完后半段;拖出按钮外松开 = 倒放回退)。见下方"工具栏四组图标"区块。
                ToolbarIconSegments_Pressed(btn);
            }
        }
    }

    private void Global_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isLeftMouseButtonPressed = false;
        _shiftDragActive = false; // [Shift 区间刷选,同步 Papers] 释放结束区间模式
        _shiftRangePicked.Clear();  // [区间改追加,同步 Papers] 手势结束:这一轮加的项不再被后续区间回收

        // [右键释放检测,同步 Papers] 右键松开:命中测试找卡片 → 手动弹菜单
        var relPt = e.GetCurrentPoint(null);
        if (relPt.Properties.PointerUpdateKind is Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased
            && _isRightButtonPressed)
        {
            _isRightButtonPressed = false;
            HandleRightReleaseOpenMenu(relPt.Position);
        }

        // 弹回按下的按钮(指针捕获保证即使移开后释放也触发)
        if (_pressedButton is { } pressedBtn)
        {
            _pressedButton = null;
            pressedBtn.ReleasePointerCapture(e.Pointer);
            // [2026-09-16 取消顶部栏按下缩小] 松开也不再有弹回缩放(见 Global_PointerPressed 里的说明)
            // 刷新/全选/反选/删除 四组图标:在按钮上松开 → 播完后半段;拖出按钮外松开 → 倒放回退
            ToolbarIconSegments_Released(pressedBtn, e);
        }
    }

    // ===================== 视图图标动画(2026-09,同步 Papers) =====================
    // 视图按钮图标由静态字形 E71D 换成 Lottie 动画(素材 = WE_Tool.AnimatedVisuals.ViewIcon,
    // 见 AnimatedVisuals/ViewIcon.cs;回退字形仍是 E71D)。素材三行(每行 = 圆角方块 + 横杠):
    // 第 0→10 帧三行错开往下起步,第 10→20 帧旧行滑出、新行滑入归位;时间轴 20 帧(0.333s)。
    // 交互:鼠标按下 → 播第 0→10 帧;松开 → 从第 10 帧继续播到第 20 帧。两段各 10 帧 @60fps = 各 167ms。
    // [为什么直接挂在按钮自己身上] 页面级 Global_PointerPressed 靠 IsDescendantOf(fe, ToolbarCommands) 判定,
    // 而顶部栏开了 IsDynamicOverflowEnabled、视图按钮排在工具栏靠后,默认窗口宽度下会被收进"溢出"菜单 ——
    // 那时它不在 ToolbarCommands 的视觉子树里,判定落空。挂在按钮自己身上与它在栏内还是在溢出菜单无关都能收到;
    // 另补两处:① PointerCaptureLost;② 溢出菜单 Flyout.Opened(菜单一开就当作这次按压结束,免得图标卡在"按下"姿态)。

    private void ViewIcon_WirePointer()
    {
        ToolbarViewButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ViewIcon_ButtonPressed), true);
        ToolbarViewButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ViewIcon_ButtonReleased), true);
        ToolbarViewButton.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(ViewIcon_ButtonReleased), true);
        ToolbarViewFlyout.Opened += (_, _) => ViewIcon_SetState(pressed: false, trigger: "溢出菜单打开");
    }

    private void ViewIcon_ButtonPressed(object sender, PointerRoutedEventArgs e) => ViewIcon_PointerPressed();
    private void ViewIcon_ButtonReleased(object sender, PointerRoutedEventArgs e) => ViewIcon_PointerReleased();
    private void ViewIcon_PointerPressed() => ViewIcon_SetState(pressed: true, trigger: "按下");
    private void ViewIcon_PointerReleased() => ViewIcon_SetState(pressed: false, trigger: "松开");

    /// <summary>按下→Pressed(播第 0→10 帧);松开→Normal(从第 10 帧播到第 20 帧)。</summary>
    private void ViewIcon_SetState(bool pressed, string trigger)
    {
        string target = pressed ? "Pressed" : "Normal";
        if (ToolbarViewIcon is null) return;
        string before = ToolbarViewIcon.GetValue(AnimatedIcon.StateProperty) as string ?? "(未设置)";
        if (string.Equals(before, target, StringComparison.Ordinal))
        {
            return;
        }
        AnimatedIcon.SetState(ToolbarViewIcon, target);
    }

    // ===================== 筛选结果图标动画(2026-09-19,同步 Papers) =====================
    // 工具栏最左"筛选结果"开关的图标由静态字形 E71D 换成 Lottie —— 与视图按钮**同一份素材**(ViewIcon,零新类;
    // 回退字形仍是 E71D)。按下 = 第 0→10 帧、松开 = 第 10→20 帧。AppBarToggleButton 模板不驱动 AnimatedIcon.State,
    // 且可能被收进溢出菜单,故直接挂按钮自己身上;这枚没有 Flyout,只需按下/松开/CaptureLost 三处。
    // 开关本身的开合(IsChecked ←→ LeftSplitViewPaneOpen)与图标动画无关,两边互不干涉。

    private void LeftFilterIcon_WirePointer()
    {
        LeftToggleFilterButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => LeftFilterIcon_SetState(pressed: true, trigger: "按下")), true);
        LeftToggleFilterButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => LeftFilterIcon_SetState(pressed: false, trigger: "松开")), true);
        LeftToggleFilterButton.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => LeftFilterIcon_SetState(pressed: false, trigger: "捕获丢失")), true);
    }

    /// <summary>按下→Pressed(播第 0→10 帧);松开→Normal(从第 10 帧播到第 20 帧)。</summary>
    private void LeftFilterIcon_SetState(bool pressed, string trigger)
    {
        string target = pressed ? "Pressed" : "Normal";
        if (LeftToggleFilterIcon is null) return;
        string before = LeftToggleFilterIcon.GetValue(AnimatedIcon.StateProperty) as string ?? "(未设置)";
        if (string.Equals(before, target, StringComparison.Ordinal))
        {
            return;
        }
        AnimatedIcon.SetState(LeftToggleFilterIcon, target);
    }

    // ── 筛选结果图标颜色同步(2026-09-19,同步 Papers) ──
    // [为什么需要] AppBarToggleButton 模板的选中前景用 VisualState 设到图标宿主 Content 与 TextLabel,但 .Icon 槽里
    // IconElement.Foreground 实测拿不到该值(选中后图标始终只有未选中的白)。右侧普通 ToggleButton 的模板用 Storyboard
    // 驱动 ContentPresenter.Foreground 所以那枚一直正常。[怎么修] 选中态变化时把色值显式设到图标自己的 Foreground
    // (本地值必然生效,深浅主题自动跟)。色值不硬编码:借 XAML 里两个 Collapsed Border(LeftFilterColorProbe*)用 {ThemeResource} 让框架解析。
    private bool _leftFilterColorHooked;

    private void LeftFilterIcon_WireColor()
    {
        LeftToggleFilterButton.Checked += (_, _) => LeftFilterIcon_SyncColor();
        LeftToggleFilterButton.Unchecked += (_, _) => LeftFilterIcon_SyncColor();
        LeftToggleFilterButton.Loaded += (_, _) =>
        {
            if (!_leftFilterColorHooked && XamlRoot?.Content is FrameworkElement themeRoot)
            {
                _leftFilterColorHooked = true;
                themeRoot.ActualThemeChanged += (_, _) => LeftFilterIcon_SyncColor();   // 挂主题根: ThemedAnimatedIcon 同款机理
            }
            LeftFilterIcon_SyncColor();                                                  // 初始态(面板可能默认打开)
            DispatcherQueue.TryEnqueue(() => LeftFilterIcon_SyncColor());                // 主题可能到本帧末才落定, 再补一次
        };
    }

    /// <summary>把选中/未选中对应的框架前景色同步给筛选结果图标 —— 图标与开关文字同色的来源。</summary>
    private void LeftFilterIcon_SyncColor()
    {
        if (LeftToggleFilterIcon is null || LeftFilterColorProbeNormal is null || LeftFilterColorProbeChecked is null) return;
        var probe = LeftToggleFilterButton.IsChecked == true ? LeftFilterColorProbeChecked : LeftFilterColorProbeNormal;
        LeftToggleFilterIcon.Foreground = probe.Background;
    }

    // ===================== 排序图标动画(2026-09,同步 Papers) =====================
    // 排序按钮图标由静态字形 E8CB 换成 Lottie(素材 = WE_Tool.AnimatedVisuals.SortIcon;回退字形仍是 E8CB)。
    // 第 0→10 帧字形两半飞散、第 10→20 帧反向飞回归位;时间轴 20 帧(0.333s)。按下切 Pressed、松开切 Normal,
    // 钩子直接挂按钮自己身上(同视图,可能被收进溢出菜单),另补 PointerCaptureLost 与 Flyout.Opened 两处兜底。

    private void SortIcon_WirePointer()
    {
        SortToolbarButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SortIcon_ButtonPressed), true);
        SortToolbarButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(SortIcon_ButtonReleased), true);
        SortToolbarButton.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(SortIcon_ButtonReleased), true);
        ToolbarSortFlyout.Opened += (_, _) => SortIcon_SetState(pressed: false, trigger: "溢出菜单打开");
    }

    private void SortIcon_ButtonPressed(object sender, PointerRoutedEventArgs e) => SortIcon_PointerPressed();
    private void SortIcon_ButtonReleased(object sender, PointerRoutedEventArgs e) => SortIcon_PointerReleased();
    private void SortIcon_PointerPressed() => SortIcon_SetState(pressed: true, trigger: "按下");
    private void SortIcon_PointerReleased() => SortIcon_SetState(pressed: false, trigger: "松开");

    /// <summary>按下→Pressed(播第 0→10 帧);松开→Normal(从第 10 帧播到第 20 帧)。</summary>
    private void SortIcon_SetState(bool pressed, string trigger)
    {
        string target = pressed ? "Pressed" : "Normal";
        if (ToolbarSortIcon is null) return;
        string before = ToolbarSortIcon.GetValue(AnimatedIcon.StateProperty) as string ?? "(未设置)";
        if (string.Equals(before, target, StringComparison.Ordinal))
        {
            return;
        }
        AnimatedIcon.SetState(ToolbarSortIcon, target);
    }

    // ===================== 排序方向图标动画(2026-09,同步 Papers) =====================
    // 排序方向按钮的图标原本是"随方向绑定的静态字形"(升序 E70D 尖朝下 / 降序 E70E 尖朝上),现换成两个 Lottie
    // 按当前方向二选一:SortDirectionAscIcon(起=升序 → 终=降序)/ SortDirectionDescIcon(起=降序 → 终=升序)。
    // 时序:① 按下切 Pressed(第 0→10 帧压平);② 松开切 Normal(第 10→20 帧张开成新方向),按下段没播完就排队;
    // ③ 两段播完画面 = 新素材第 0 帧时换 Source(无感)并归零;④ 过渡期间再按下排队;⑤ 按住不放停在第 10 帧等松开。
    private const int SortDirectionIconFrameMs = 17;
    private const int SortDirectionIconPressMs = 10 * SortDirectionIconFrameMs;     // 按下段 第 0→10 帧
    private const int SortDirectionIconReleaseMs = 10 * SortDirectionIconFrameMs;   // 松开段 第 10→20 帧

    private bool _sortDirectionIconCycleActive;
    private bool _sortDirectionIconPressedSegDone;
    private bool _sortDirectionIconPendingRelease;
    private bool _sortDirectionIconPendingPress;
    private CancellationTokenSource? _sortDirectionIconCycleCts;

    private void SortDirectionIcon_WirePointer()
    {
        SortDirectionButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SortDirectionIcon_ButtonPressed), true);
        SortDirectionButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(SortDirectionIcon_ButtonReleased), true);
        SortDirectionButton.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(SortDirectionIcon_ButtonReleased), true);
        // 首次选素材放在 Loaded(见上),方向变化由 ComponentsDisplayVM.PropertyChanged 分支同步。
    }

    private void SortDirectionIcon_ButtonPressed(object sender, PointerRoutedEventArgs e) => SortDirectionIcon_PointerPressed();
    private void SortDirectionIcon_ButtonReleased(object sender, PointerRoutedEventArgs e) => SortDirectionIcon_PointerReleased();

    /// <summary>按下:空闲就开始一轮;过渡中则排队。</summary>
    private void SortDirectionIcon_PointerPressed()
    {
        if (_sortDirectionIconCycleActive)
        {
            _sortDirectionIconPendingPress = true;
            return;
        }
        SortDirectionIcon_StartCycle("按下");
    }

    /// <summary>松开:按下段已播完就立刻播第二段;还没播完就排队(绝不跳帧)。</summary>
    private void SortDirectionIcon_PointerReleased()
    {
        if (!_sortDirectionIconCycleActive)
        {
            return;   // 当前没有过渡周期,松开不参与
        }
        if (_sortDirectionIconPressedSegDone)
        {
            SortDirectionIcon_PlayReleaseSegment("松开");
            return;
        }
        _sortDirectionIconPendingRelease = true;
    }

    /// <summary>一轮过渡:先确认起点素材 = 当前方向(换源无感),再播按下段第 0→10 帧。</summary>
    private void SortDirectionIcon_StartCycle(string trigger)
    {
        _sortDirectionIconCycleActive = true;
        _sortDirectionIconPressedSegDone = false;
        _sortDirectionIconPendingRelease = false;
        _sortDirectionIconPendingPress = false;
        bool swapped = SortDirectionIcon_SyncSource(trigger);
        if (swapped)
        {
            DispatcherQueue.TryEnqueue(() => SortDirectionIcon_PlayPressSegment(trigger + "(换源后)"));
            return;
        }
        SortDirectionIcon_PlayPressSegment(trigger);
    }

    /// <summary>第一段(按下):Pressed → NormalToPressed = 第 0→10 帧(折角压平成一条线)。</summary>
    private void SortDirectionIcon_PlayPressSegment(string trigger)
    {
        SortDirectionIcon_SetState("Pressed", "按下:第 0→10 帧(折角压平)", trigger);
        SortDirectionIcon_WaitPressSegmentAsync();
    }

    /// <summary>第一段播完:松过手就接着播第二段;还按着就停在第 10 帧等松开。</summary>
    private async void SortDirectionIcon_WaitPressSegmentAsync()
    {
        _sortDirectionIconCycleCts?.Cancel();
        var cts = new CancellationTokenSource();
        _sortDirectionIconCycleCts = cts;
        try { await Task.Delay(SortDirectionIconPressMs, cts.Token); }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;

        _sortDirectionIconPressedSegDone = true;
        if (_sortDirectionIconPendingRelease)
        {
            _sortDirectionIconPendingRelease = false;
            SortDirectionIcon_PlayReleaseSegment("松开(排队)");
        }
    }

    /// <summary>第二段(松开):Normal → PressedToNormal = 第 10→20 帧(张开成新方向)。</summary>
    private void SortDirectionIcon_PlayReleaseSegment(string trigger)
    {
        SortDirectionIcon_SetState("Normal", "松开:第 10→20 帧(张开成新方向)", trigger);
        SortDirectionIcon_WaitReleaseSegmentAsync();
    }

    /// <summary>两段播完:换素材(此时画面 = 新素材第 0 帧,无感)→ 处理排队的按下。</summary>
    private async void SortDirectionIcon_WaitReleaseSegmentAsync()
    {
        _sortDirectionIconCycleCts?.Cancel();
        var cts = new CancellationTokenSource();
        _sortDirectionIconCycleCts = cts;
        try { await Task.Delay(SortDirectionIconReleaseMs, cts.Token); }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;

        _sortDirectionIconCycleActive = false;
        _sortDirectionIconPressedSegDone = false;
        SortDirectionIcon_SyncSource("过渡播完");   // 两段播完的瞬间画面 = 新素材第 0 帧,换上去无感
        if (_sortDirectionIconPendingPress)
        {
            _sortDirectionIconPendingPress = false;
            SortDirectionIcon_StartCycle("排队按下");
        }
    }

    /// <summary>按当前排序方向选素材(升序 → Asc / 降序 → Desc),返回是否真的换了源。</summary>
    private bool SortDirectionIcon_SyncSource(string trigger)
    {
        if (ToolbarSortDirectionIcon is null) return false;
        bool ascending = ViewModel?.ComponentsDisplayVM?.IsSortAscending ?? true;
        IAnimatedVisualSource2 wanted = ascending ? new SortDirectionAscIcon() : new SortDirectionDescIcon();   // AnimatedIcon.Source 类型是 IAnimatedVisualSource2
        if (ReferenceEquals(ToolbarSortDirectionIcon.Source?.GetType(), wanted.GetType())) return false;
        ToolbarSortDirectionIcon.Source = wanted;
        ToolbarSortDirectionIcon.FallbackIconSource = new FontIconSource { Glyph = ascending ? "" : "" };
        ToolbarSortDirectionIcon.RefreshColorAfterSourceChange();   // 新素材的画笔是全新对象,立刻重涂(否则浅色主题闪一下原色)

        SortDirectionIcon_ResetToFirstFrame(trigger);   // 归零:换源后把画面拨回起手帧(第 0 帧 = 当前方向)
        DispatcherQueue.TryEnqueue(() => { if (!_sortDirectionIconCycleActive) SortDirectionIcon_ResetToFirstFrame(trigger + "(隔拍)"); });
        return true;
    }

    /// <summary>归零:把画面拨回素材第 0 帧(= 起手帧 = 当前方向)。零长度标记 NormalToReset / ResetToNormal。</summary>
    private void SortDirectionIcon_ResetToFirstFrame(string trigger)
    {
        if (ToolbarSortDirectionIcon is null) return;
        SortDirectionIcon_SetState("Reset", "归零:拨回第 0 帧(画面不动)", trigger);
        SortDirectionIcon_SetState("Normal", "归零后恢复状态名", trigger);
    }

    /// <summary>切 AnimatedIcon 状态;状态没变化时 AnimatedIcon 不会播,故记一行。</summary>
    private void SortDirectionIcon_SetState(string target, string seg, string trigger)
    {
        if (ToolbarSortDirectionIcon is null) return;
        string before = ToolbarSortDirectionIcon.GetValue(AnimatedIcon.StateProperty) as string ?? "(未设置)";
        if (string.Equals(before, target, StringComparison.Ordinal))
        {
            return;
        }
        AnimatedIcon.SetState(ToolbarSortDirectionIcon, target);
    }

    // ===================== 详情面板按钮图标动画(2026-09-18,同步 Papers) =====================
    // 详情面板里带图标的按钮 + 工具栏那个"详情面板"开关,由静态字形换成 Lottie:
    //   提取 E72D → ExtractIcon   复制 E8C8 → CopyIcon(勾动画见下)   打开目录 E838 → OpenDirectoryIcon
    //   属性 E90F → PropertiesIcon   详情面板开关 E90D → DetailPanelToggleIcon
    // 素材 20 帧:按下 = 第 0→10 帧;松开在按钮上 = 第 10→20 帧,松开在按钮外(点空)= 倒放回第 0 帧。
    // [谁在切状态] 提取/打开目录/属性/卸载是普通 Button,WinUI DefaultButtonStyle 的 ContentPresenter 有
    // PointerOver/Pressed/Disabled 三个 Setter **驱动图标状态**,框架自己排队播放 —— 我们不该插手 SetState。
    // 唯一例外是"详情面板"开关(ToggleButton,模板无那三个 Setter)由代码切(RightToggleIcon_* 那套,含排队);
    // 复制按钮只记时间戳(供勾动画"等两段播完"),状态仍由按钮模板驱动。
    private const int DetailIconFrameMs = 17;
    private const int DetailIconSegMs = 10 * DetailIconFrameMs;   // 每段 10 帧 ≈ 170ms

    private DateTime _detailCopyPressAt;
    private DateTime _detailCopyReleaseAt;
    private bool _detailCopyPressed;
    private bool _detailCopyReleased;

    private void DetailIcons_WirePointer()
    {
        // 复制按钮:只记时间戳(状态由 Button 模板驱动),供勾动画"等两段播完"用
        DetailCopyButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => DetailCopyIcon_NotePress()), true);
        DetailCopyButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => DetailCopyIcon_NoteRelease()), true);
        DetailCopyButton.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => DetailCopyIcon_NoteRelease()), true);
        // 详情面板开关:ToggleButton 模板不驱动图标状态,这一枚由我们切(含排队)
        RightToggleFilterButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => RightToggleIcon_PointerPressed()), true);
        RightToggleFilterButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => RightToggleIcon_PointerReleased()), true);
        RightToggleFilterButton.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => RightToggleIcon_PointerReleased()), true);
    }

    private void DetailCopyIcon_NotePress()
    {
        _detailCopyPressAt = DateTime.Now;
        _detailCopyPressed = true;
        _detailCopyReleased = false;
    }

    private void DetailCopyIcon_NoteRelease()
    {
        if (!_detailCopyPressed || _detailCopyReleased) return;
        _detailCopyReleased = true;
        _detailCopyReleaseAt = DateTime.Now;
    }

    /// <summary>复制按钮用:等这一轮(按下段 + 松开段)播完 —— 勾动画要等它播完再开始。</summary>
    private async Task DetailCopyIcon_WaitCycleAsync()
    {
        if (!_detailCopyPressed) return;
        DateTime pressAt = _detailCopyPressAt;
        DateTime releaseAt = _detailCopyReleased ? _detailCopyReleaseAt : DateTime.Now;
        DateTime pressEnd = pressAt.AddMilliseconds(DetailIconSegMs);
        DateTime releaseStart = releaseAt > pressEnd ? releaseAt : pressEnd;
        int waitMs = (int)Math.Max(0, (releaseStart.AddMilliseconds(DetailIconSegMs) - DateTime.Now).TotalMilliseconds);
        if (waitMs > 0) await Task.Delay(waitMs);
        _detailCopyPressed = false;
        _detailCopyReleased = false;
    }

    /// <summary>复制按钮用:勾动画的淡出阶段已把 Lottie 淡掉,这里把画面交给承载勾的 FontIcon。</summary>
    private void DetailCopyIcon_SwapToFontIcon()
    {
        DetailCopyAnimatedIcon.Visibility = Visibility.Collapsed;
        DetailCopyIcon.Visibility = Visibility.Visible;
        ElementCompositionPreview.GetElementVisual(DetailCopyIcon).Opacity = 0f;   // 交给勾那段 fadeIn(0→1) 亮起来
    }

    /// <summary>复制按钮用:勾动画播完,把画面还给 Lottie 并把进度归零(零长度标记对,画面不动)。</summary>
    private void DetailCopyIcon_SwapBackToLottie()
    {
        DetailCopyIcon.Visibility = Visibility.Collapsed;
        DetailCopyAnimatedIcon.Visibility = Visibility.Visible;
        var v = ElementCompositionPreview.GetElementVisual(DetailCopyAnimatedIcon);
        v.StopAnimation("Opacity");
        v.Opacity = 1f;   // 勾动画开头把它淡掉过,必须复位,否则换回来的图标是透明的
        DetailIcon_SetState(DetailCopyAnimatedIcon, "复制", "Reset", "归零:拨回第 0 帧(画面不动)", "勾动画结束");
        DetailIcon_SetState(DetailCopyAnimatedIcon, "复制", "Normal", "归零后恢复状态名", "勾动画结束");
    }

    /// <summary>切 AnimatedIcon 状态;只给"详情面板开关"(框架不驱动的那枚)和复制按钮的归零用。</summary>
    private void DetailIcon_SetState(AnimatedIcon icon, string name, string target, string seg, string trigger)
    {
        if (icon is null) return;
        string before = icon.GetValue(AnimatedIcon.StateProperty) as string ?? "(未设置)";
        if (string.Equals(before, target, StringComparison.Ordinal))
        {
            return;
        }
        AnimatedIcon.SetState(icon, target);
    }

    // ---- 详情面板开关(ToggleButton):框架不驱动图标状态 → 由代码切,按下 0→10 / 松开 10→20,没播完就排队 ----
    private bool _rightToggleIconCycleActive;
    private bool _rightToggleIconPressSegDone;
    private bool _rightToggleIconPendingRelease;
    private bool _rightToggleIconPendingPress;
    private CancellationTokenSource? _rightToggleIconCts;

    private void RightToggleIcon_PointerPressed()
    {
        if (_rightToggleIconCycleActive)
        {
            _rightToggleIconPendingPress = true;
            return;
        }
        RightToggleIcon_StartCycle("按下");
    }

    private void RightToggleIcon_PointerReleased()
    {
        if (!_rightToggleIconCycleActive)
        {
            return;
        }
        if (_rightToggleIconPressSegDone)
        {
            _ = RightToggleIcon_PlayReleaseSegmentAsync("松开");
            return;
        }
        _rightToggleIconPendingRelease = true;
    }

    private void RightToggleIcon_StartCycle(string trigger)
    {
        _rightToggleIconCycleActive = true;
        _rightToggleIconPressSegDone = false;
        _rightToggleIconPendingRelease = false;
        _rightToggleIconPendingPress = false;
        _rightToggleIconCts?.Cancel();
        _rightToggleIconCts = new CancellationTokenSource();
        _ = RightToggleIcon_RunPressSegmentAsync(trigger, _rightToggleIconCts.Token);
    }

    private async Task RightToggleIcon_RunPressSegmentAsync(string trigger, CancellationToken token)
    {
        DetailIcon_SetState(RightToggleFilterIcon, "详情面板开关", "Pressed", "按下:第 0→10 帧", trigger);
        try { await Task.Delay(DetailIconSegMs, token); } catch (TaskCanceledException) { return; }
        if (token.IsCancellationRequested) return;
        _rightToggleIconPressSegDone = true;
        if (_rightToggleIconPendingRelease)
        {
            _rightToggleIconPendingRelease = false;
            _ = RightToggleIcon_PlayReleaseSegmentAsync("松开(排队后)");
            return;
        }
    }

    private async Task RightToggleIcon_PlayReleaseSegmentAsync(string trigger)
    {
        _rightToggleIconCts?.Cancel();
        _rightToggleIconCts = new CancellationTokenSource();
        var token = _rightToggleIconCts.Token;
        DetailIcon_SetState(RightToggleFilterIcon, "详情面板开关", "Normal", "松开:第 10→20 帧", trigger);
        try { await Task.Delay(DetailIconSegMs, token); } catch (TaskCanceledException) { return; }
        if (token.IsCancellationRequested) return;
        _rightToggleIconCycleActive = false;
        _rightToggleIconPressSegDone = false;
        if (_rightToggleIconPendingPress)
        {
            _rightToggleIconPendingPress = false;
            RightToggleIcon_StartCycle("排队后");
        }
    }

    // ===================== 工具栏四组图标 + 复制:按下 0→10 / 松开播完或回退(2026-09-18,同步 Papers) =====================
    // 刷新/全选/反选/删除 四组素材是"按下十帧"版(RefreshIcon 30 帧、SelectAllIcon 20 帧、InvertSelection 30 帧、
    // DeleteIcon 20 帧);复制同一份 CopyIcon(20 帧),点击接勾动画。按下 = 第 0→10 帧;在按钮上松开 = 第 10→末尾帧;
    // 在按钮外松开(点空)= 第 10→0 帧倒放(回退)。菜单项 / 弹出工具条没有"按住"概念,不经过这里,照旧点击播整段。
    // [为什么按"素材类型"认按钮] 工具条里反选/删除两枚没有 x:Name,按 AnimatedIcon 挂的 Source 类型分发即可。
    // [标志位] 按下时置"本次点击已由按下/松开驱动",Click 里的整段播放据此跳过。
    private bool _invertSelectionIconPointerDriven;   // 反选:工具条那枚已由按下/松开驱动
    private bool _deleteIconPointerDriven;            // 删除:工具条那枚已由按下/松开驱动

    /// <summary>工具条按下:四个图标之一 → 切 Pressed(按下:第 0→10 帧)。Global_PointerPressed 命中工具条按钮时调用。</summary>
    private void ToolbarIconSegments_Pressed(AppBarButton btn)
    {
        var icon = FindToolbarSegmentsIcon(btn);
        if (icon is null) return;
        switch (icon.Source)
        {
            case SelectAllIcon: _selectAllIconPointerDriven = true; _selectAllIconResetCts?.Cancel(); break;
            case InvertSelection: _invertSelectionIconPointerDriven = true; _invertSelectionIconResetCts?.Cancel(); break;
            case DeleteIcon: _deleteIconPointerDriven = true; break;
        }
        if (ReferenceEquals(btn, ToolbarCopyButton))   // 复制图标:记按下时刻,供勾动画"等两段播完"
        {
            _toolbarCopyPressAt = DateTime.Now;
            _toolbarCopyPressed = true;
            _toolbarCopyReleased = false;
        }
        AnimatedIcon.SetState(icon, "Pressed");
    }

    /// <summary>工具条松开:在按钮上 → 播完后半段(PointerOver);拖出按钮外松开 → 倒放回退(Normal)。Global_PointerReleased 调用。</summary>
    private void ToolbarIconSegments_Released(AppBarButton btn, PointerRoutedEventArgs e)
    {
        var icon = FindToolbarSegmentsIcon(btn);
        if (icon is null) return;
        bool inside = IsReleaseInsideButton(btn, e);
        if (ReferenceEquals(btn, ToolbarCopyButton))   // 复制图标:记松开时刻
        {
            _toolbarCopyReleased = true;
            _toolbarCopyReleaseAt = DateTime.Now;
        }
        switch (icon.Source)
        {
            case SelectAllIcon: _selectAllIconPointerDriven = false; break;
            case InvertSelection: _invertSelectionIconPointerDriven = false; break;
            case DeleteIcon: _deleteIconPointerDriven = false; break;
        }
        AnimatedIcon.SetState(icon, inside ? "PointerOver" : "Normal");
    }

    /// <summary>按钮里的图标是这几款之一才返回(其余工具条按钮:视图/排序等自己有接线,返回 null 不动它们)。</summary>
    private static AnimatedIcon? FindToolbarSegmentsIcon(AppBarButton btn)
    {
        var icon = AnimatedIconPlayer.FindAnimatedIcon(btn);
        return icon?.Source is RefreshIcon or SelectAllIcon or InvertSelection or DeleteIcon or CopyIcon ? icon : null;
    }

    /// <summary>松开点是否落在按钮区域内(判定"在按钮上松开"还是"点空")。</summary>
    private static bool IsReleaseInsideButton(FrameworkElement el, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(el).Position;
        return p.X >= 0 && p.Y >= 0 && p.X <= el.ActualWidth && p.Y <= el.ActualHeight;
    }

    // ===================== 工具栏复制图标:两段播完接勾(2026-09-18,同步 Papers) =====================
    // 复制图标是 Lottie(CopyIcon):状态由上面"工具栏四组图标"那套驱动。[勾动画怎么上来] AppBarButton 图标槽只放得下
    // 一个元素,勾没法像详情面板那样叠一个折叠 FontIcon —— 做法:勾动画开头把 Lottie 淡掉后,把按钮 Icon 换成代码备好的
    // FontIcon(承载勾),播完再换回 Lottie 并归零。[为什么记时间戳] 勾动画要等按下/松开两段播完再开始。
    private DateTime _toolbarCopyPressAt;
    private DateTime _toolbarCopyReleaseAt;
    private bool _toolbarCopyPressed;
    private bool _toolbarCopyReleased;

    /// <summary>复制按钮用:等这一轮(按下段 + 松开段)播完再播勾。走菜单/快捷键(没记过时刻)时直接返回。</summary>
    private async Task ToolbarCopyIcon_WaitCycleAsync()
    {
        if (!_toolbarCopyPressed) return;
        DateTime pressAt = _toolbarCopyPressAt;
        DateTime releaseAt = _toolbarCopyReleased ? _toolbarCopyReleaseAt : DateTime.Now;
        DateTime pressEnd = pressAt.AddMilliseconds(DetailIconSegMs);
        DateTime releaseStart = releaseAt > pressEnd ? releaseAt : pressEnd;
        int waitMs = (int)Math.Max(0, (releaseStart.AddMilliseconds(DetailIconSegMs) - DateTime.Now).TotalMilliseconds);
        if (waitMs > 0) await Task.Delay(waitMs);
        _toolbarCopyPressed = false;
        _toolbarCopyReleased = false;
    }

    /// <summary>承载勾的 FontIcon:首次创建即置透明(交给勾那段 fadeIn 亮起来),平时不在树上,换入图标槽才现身。</summary>
    private FontIcon? _toolbarCopyCheckFontIcon;
    private FontIcon GetToolbarCopyCheckFontIcon()
    {
        _toolbarCopyCheckFontIcon ??= new FontIcon { Glyph = "", FontSize = 16, Opacity = 0 };
        return _toolbarCopyCheckFontIcon;
    }

    /// <summary>复制图标用:勾动画的淡出阶段已把 Lottie 淡掉,这里把图标槽换给承载勾的 FontIcon。</summary>
    private void ToolbarCopyIcon_SwapToCheckFontIcon()
    {
        ToolbarCopyButton.Icon = GetToolbarCopyCheckFontIcon();
    }

    /// <summary>复制图标用:勾动画播完,图标槽还给 Lottie 并把进度归零(零长度标记对,画面不动)。</summary>
    private void ToolbarCopyIcon_SwapBackToLottie()
    {
        var cv = ElementCompositionPreview.GetElementVisual(GetToolbarCopyCheckFontIcon());
        cv.StopAnimation("Opacity");
        cv.Opacity = 0f;   // 下次换入直接从 0 亮起
        ToolbarCopyButton.Icon = ToolbarCopyIcon;
        var v = ElementCompositionPreview.GetElementVisual(ToolbarCopyIcon);
        v.StopAnimation("Opacity");
        v.Opacity = 1f;   // 勾动画开头把它淡掉过,必须复位,否则换回来的图标是透明的
        DetailIcon_SetState(ToolbarCopyIcon, "复制(工具栏)", "Reset", "归零:拨回第 0 帧(画面不动)", "勾动画结束");
        DetailIcon_SetState(ToolbarCopyIcon, "复制(工具栏)", "Normal", "归零后恢复状态名", "勾动画结束");
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

        /// <summary>遍历可见容器重启 GIF 播放(页面缓存切回时;容器未就绪/无项时无害)
        /// [同步 Papers 修复] ItemsRepeater 无 ItemsPanelRoot 遍历,改为可视树遍历:找可见的
        /// SkiaGifView,用其 DataContext(ComponentInfo)的 Preview 重启(切走时 Unloaded 已停)</summary>
        private void RestartVisibleGifPlayback()
        {
            RestartVisibleSkiaGifs(this);
        }

        private static void RestartVisibleSkiaGifs(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is SkiaGifView skia && skia.Visibility == Visibility.Visible)
                {
                    // 从 DataContext 取 GIF 路径重启(与 ElementPrepared 的启动条件一致)
                    if (skia.DataContext is ComponentInfo gifItem
                        && !string.IsNullOrEmpty(gifItem.Preview)
                        && gifItem.Preview.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                    {
                        skia.Start(gifItem.Preview);
                    }
                }
                else
                {
                    RestartVisibleSkiaGifs(child);
                }
            }
        }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
    }

    private async Task LoadComponents()
    {
        ShowScanProgress(true); // [同步 Papers 2026-09] 扫描/刷新期间显示列表区中央转圈
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
        finally
        {
            ShowScanProgress(false); // 扫描/刷新结束,隐藏转圈
        }
    }

    /// <summary>[同步 Papers 2026-09] 列表区中央转圈显隐(可被非 UI 线程调用)</summary>
    private void ShowScanProgress(bool show)
    {
        if (ScanProgressRing == null) return;
        var action = () =>
        {
            ScanProgressRing.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            ScanProgressRing.IsActive = show;
        };
        if (DispatcherQueue.HasThreadAccess) action();
        else DispatcherQueue.TryEnqueue(() => action());
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
        ShowScanProgress(true); // [2026-09] 按下立即显示转圈(扫描在 await ScanTask 期间,等 LoadComponents 才显示就晚了)

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
            ShowScanProgress(false); // 兜底隐藏(正常路径 LoadComponents 已隐藏;异常路径防转圈卡死)
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
            // 目标:CommandBar 按钮/右键菜单 → 工具栏那枚 Lottie(勾由临时换入的 FontIcon 承载);详情面板按钮 → 详情那枚
            if (sender is AppBarButton)
            {
                if (ToolbarCopyIcon is not null)
                {
                    await ToolbarCopyIcon_WaitCycleAsync();   // 等按下/松开两段播完再播勾
                    await PlayCopyCheckAnimationAsync(GetToolbarCopyCheckFontIcon(),
                        fadeOutElement: ToolbarCopyIcon,
                        swapIn: ToolbarCopyIcon_SwapToCheckFontIcon,
                        finished: ToolbarCopyIcon_SwapBackToLottie);
                }
            }
            else if (DetailCopyIcon is not null)
            {
                await DetailCopyIcon_WaitCycleAsync();
                await PlayCopyCheckAnimationAsync(DetailCopyIcon,
                    fadeOutElement: DetailCopyAnimatedIcon,
                    swapIn: DetailCopyIcon_SwapToFontIcon,
                    finished: DetailCopyIcon_SwapBackToLottie);
            }
        }
    }


    // 复制成功反馈(序列):淡出 → 切勾 → 从左往右扫出 → 停留 → 淡出 → 切回复制 → 淡入
    // [换 Lottie 后] fadeOutElement = 该淡出的元素(Lottie);swapIn = 淡出后把画面交给承载勾的 FontIcon;
    // finished = 收尾(把画面还给 Lottie 并归零)。三个都不传 = 老的"单 FontIcon"行为。
    private int _copyCheckAnimationGeneration;
    private Microsoft.UI.Composition.InsetClip? _copyCheckClip; // 勾扫出的 clip

    private async Task PlayCopyCheckAnimationAsync(FontIcon targetIcon, UIElement? fadeOutElement = null, Action? swapIn = null, Action? finished = null)
    {
        int gen = ++_copyCheckAnimationGeneration;

        // 点击处理器同一帧做了大量同步变更,此帧内 StartAnimation 会被 Composition
        // 帧调度丢弃/延迟(项目已定位根因)。整体包进 DispatcherQueue.TryEnqueue 排到下一个空闲帧起跑。
        var tcs = new TaskCompletionSource();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (gen != _copyCheckAnimationGeneration) { tcs.TrySetResult(); return; } // 排队期间已作废

            var visual = ElementCompositionPreview.GetElementVisual(fadeOutElement ?? (UIElement)targetIcon);
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

        // 上一步淡出的是 Lottie 时:这里把画面交给承载勾的 FontIcon(详情面板 = 取消折叠;工具栏 = 图标槽换元素)
        swapIn?.Invoke();

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

        // 详情面板按钮:把画面还给 Lottie(并归零);顶部栏按钮 finished = null,不走这段
        finished?.Invoke();
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
        // 删除图标动画:工具条那枚由按下/松开两段驱动(标志 _deleteIconPointerDriven),整段播一遍据此跳过;
        // 详情面板普通 Button 宿主 PlayOnce 内部本就跳过(避免与两段重复);右键菜单/弹窗工具条没有"按住"概念,照旧整段播一遍。
        if (!_deleteIconPointerDriven) AnimatedIconPlayer.PlayOnce(sender);
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
        // 组件类型(图层/脚本/特效)是组件语义,不在壁纸类型字典(scene/video/web...);
        // 属性窗口组件模式(ShowPropsPage=false)直显本字段,不查 TypeConv 字典,与详情面板 ComponentTypeToDisplay 同款
        Type = c.ComponentType switch
        {
            ComponentType.Layer => "图层",
            ComponentType.Script => "脚本",
            ComponentType.Effect => "特效",
            _ => "未知"
        },
        // 组件扫描仅 workshop 源:来源与壁纸 workshop 条目同路径,SourceConv 命中 Source_Workshop 显示"创意工坊"
        Source = "workshop",
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
        // [同步 Papers] 图标模式已换 ItemsRepeater(无 ItemsPanelRoot 容器遍历),
        // 原图标 GridView 角标刷新暂注释(ElementPrepared 已按当前标签模式刷新;滚动回收会重新实化)
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
            tb.Text = new ComponentsTagContentChoose().Convert(item, typeof(string), "", "") as string ?? "";
    }

    /// <summary>[同步 Papers 2026-09 + 内容模式走 Skia 2026-09] 卡片图源统一装载:图标模式与内容模式共用。
    /// GIF → Skia 流式播放(不建 BitmapImage,消掉"白解一遍"的 WIC 全帧解码);
    /// 静态图 → 按卡片实际显示尺寸解码(decodeWidth 上限;路径为空用占位图)。
    /// 原图标模式私有的 Skia 装载私有方法只切换可见性、不设图源,现并入本方法(容器复用也在此收敛)。</summary>
    private static void ApplyComponentPreview(Grid root, ComponentInfo item, int decodeWidth)
    {
        bool isGif = !string.IsNullOrEmpty(item.Preview)
            && item.Preview.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

        // 设静态图源:仅 Skia 未接管时建
        Image? img = root.FindName("ItemPreviewImage") as Image;
        if (img != null)
        {
            if (isGif)
            {
                // Skia 接管:不建 BitmapImage(避免白解一遍);顺带释放容器上次复用残留的解码
                img.Source = null;
            }
            else
            {
                var src = string.IsNullOrEmpty(item.Preview)
                    ? "ms-appx:///Assets/NoPreview.png"
                    : item.Preview;
                // Preview 是本地文件路径(非 URI),须转 file:///;ms-appx 等 URI 原样
                // [同步 Papers 2026-09] 按卡片实际尺寸解码(DecodePixelWidth 须在 UriSource 之前设才生效)
                var bmp = new BitmapImage { DecodePixelWidth = decodeWidth };
                bmp.UriSource = new Uri(
                    src.StartsWith("ms-appx", StringComparison.OrdinalIgnoreCase)
                        ? src
                        : "file:///" + src.Replace('\\', '/'));
                img.Source = bmp;
            }
        }
        // GIF → Skia 播放,其余 → 静态图
        if (root.FindName("SkiaGifCanvas") is SkiaGifView skia)
        {
            if (isGif)
            {
                skia.Visibility = Visibility.Visible;
                if (img != null) img.Visibility = Visibility.Collapsed;
                skia.Start(item.Preview!);
            }
            else
            {
                skia.Stop();
                skia.Visibility = Visibility.Collapsed;
                if (img != null) img.Visibility = Visibility.Visible;
            }
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
        // [上下文菜单键盘可达 2026-09,同步 Papers] 菜单键(物理"应用程序键")/ Shift+F10 / Enter → 在当前焦点卡片上
        // 弹组件操作菜单。排在 if (ctrl) 之前并自带 return:那条链一旦把 ctrl 判真就吞掉整条链(它的 switch 没有
        // default 分支)。WinUI 的 KeyRoutedEventArgs **没有** KeyModifiers(那是 Pointer 事件才有的),修饰键只能读
        // GetKeyStateForCurrentThread,而它在部分环境读到过期状态 → 这里对 Enter/菜单键不看任何修饰键,只有 F10 判 Shift。
        // 手动 ShowAt,不指望系统 ContextFlyout 的键盘链路(2026-09-21 实测三模式按 Shift+F10 都不响应)。
        if (e.Key == VirtualKey.Menu || e.Key == VirtualKey.Enter
            || (e.Key == VirtualKey.F10
                && (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down))
        {
            if (OpenComponentContextMenuForFocusedCard()) e.Handled = true;
            return;
        }

        // [空格=一次单击 2026-09-21,同步 Papers] 空格 = 对焦点卡片按一次鼠标左键(见 ActivateFocusedComponentCardByClick)。
        // 与 Enter 同一条分工原则:焦点停在 CheckBox 上时空格是勾选框自己的切换键(被消费掉,不会冒泡到本页处理器),
        // 停在真按钮上同理 —— 只有"焦点在卡片本身"上时空格才走到这里。
        if (e.Key == VirtualKey.Space)
        {
            if (ActivateFocusedComponentCardByClick()) e.Handled = true;
            return;
        }

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
                case VirtualKey.L:
                    // [列表键盘可达 2026-09,同步 Papers] Ctrl+L 直达组件列表:
                    // 列表上方有多个工具栏停留点,再加左侧筛选面板与外壳导航栏,按 Tab 到列表要按很多下。
                    // [Ctrl 焦点多选 2026-09] Ctrl+L 里的 Ctrl 是复合键的一部分:程序化搬焦点时要屏蔽"Ctrl 划选",
                    // 否则一按 Ctrl+L 就会平白进多选。注意 GotFocus 是异步事件(官方文档明示),不能用
                    // "Focus() 前后 try/finally 复位"——改成一次性令牌,由下一次 GotFocus 自己消费清零;
                    // 没搬动焦点(返回 false)就当场清掉,别让令牌悬着。
                    _suppressCtrlFocusMultiSelect = true;
                    if (!FocusComponentList())
                    {
                        _suppressCtrlFocusMultiSelect = false;
                    }
                    e.Handled = true;
                    return;
                // [Ctrl 焦点多选 2026-09] Ctrl+方向键:自己搬焦点,不赌"按住 Ctrl 时框架还做不做 2D 方向导航"这件事
                // (带修饰键的方向键会不会被框架消费,官方文档没给承诺)。SearchRoot 把候选限在列表内,
                // 策略用 Projection(与方向键原生导航同一套几何策略);搬完把事件标记 Handled,免得框架再搬一次
                // (那样一次按键会跳两格)。搬不动(已到边界/候选未实化)只写日志,不静默。
                // 注意:Override 枚举与 XYFocusNavigationStrategy 的数值不同(官方文档:Override 是
                // None=0/Auto=1/Projection=2,而 XYFocusNavigationStrategy 是 Auto=0/Projection=1),
                // 所以不能强转(强转成 1 会变成"继承祖先策略"而不是 Projection),必须取 Override 自己的成员。
                case VirtualKey.Left:
                case VirtualKey.Right:
                case VirtualKey.Up:
                case VirtualKey.Down:
                {
                    var navDirection = e.Key switch
                    {
                        VirtualKey.Left => FocusNavigationDirection.Left,
                        VirtualKey.Right => FocusNavigationDirection.Right,
                        VirtualKey.Up => FocusNavigationDirection.Up,
                        _ => FocusNavigationDirection.Down,
                    };
                    try
                    {
                        var candidate = FocusManager.FindNextElement(navDirection, new FindNextElementOptions
                        {
                            SearchRoot = GetVisibleComponentRepeater() ?? ComponentsRepeater,
                            XYFocusNavigationStrategyOverride = XYFocusNavigationStrategyOverride.Projection,
                        });
                        if (candidate is FrameworkElement next) next.Focus(FocusState.Keyboard);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[A11y] Ctrl+方向键 手动搬焦点异常");
                    }
                    e.Handled = true;
                    return;
                }
            }
        }
        // [Shift 焦点区间 2026-09] Shift+方向键:同样自己搬焦点(理由同上面 Ctrl 分支——带修饰键的方向键框架管不管,
        // 官方没承诺),搬完标记 Handled 免得框架再搬一次跳两格。选中区间不在这里做:焦点一变,就由 GotFocus 里的
        // Shift 分支按"锚点 → 当前焦点"重算(与 Ctrl 那条路径同构,选中逻辑只留一处)。
        // 注意 Ctrl+Shift+方向键到不了这里:上面 if (ctrl) 已先接管(GotFocus 里 Shift 分支也排除了 Ctrl 同按)。
        else if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down
            && (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down)
        {
            var rangeNavDirection = e.Key switch
            {
                VirtualKey.Left => FocusNavigationDirection.Left,
                VirtualKey.Right => FocusNavigationDirection.Right,
                VirtualKey.Up => FocusNavigationDirection.Up,
                _ => FocusNavigationDirection.Down,
            };
            try
            {
                var rangeCandidate = FocusManager.FindNextElement(rangeNavDirection, new FindNextElementOptions
                {
                    SearchRoot = GetVisibleComponentRepeater() ?? ComponentsRepeater,
                    XYFocusNavigationStrategyOverride = XYFocusNavigationStrategyOverride.Projection,
                });
                if (rangeCandidate is FrameworkElement rangeNext) rangeNext.Focus(FocusState.Keyboard);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[A11y] Shift+方向键 手动搬焦点异常");
            }
            e.Handled = true;
            return;
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

    /// <summary>[列表键盘可达 2026-09,同步 Papers] 把"当前键盘焦点落在哪张组件卡片上"认出来,交给调用方:
    /// 持有焦点的元素、卡片(携带 DataContext 的那一层)、组件项。两条认定路:上溯找 DataContext 覆盖内容/列表模式
    /// (行根设了 DataContext="{x:Bind}");按 repeater 下标反查覆盖图标模式(模板根 ItemContainer 不设 DataContext,
    /// item 在里层 ItemRootGrid 上),fromRepeaterIndex 告诉调用方走的是这条。
    /// 读焦点必须用带 XamlRoot 的重载:WinUI 3 桌面没有 CoreWindow,无参版本恒返回 null(2026-09-21 在 Papers 页实测)。
    /// 返回 false 时留一条 Debug 读数：它是"键到了页面、只是认不出卡片"与"键根本没到页面"的分流点。</summary>
    private bool TryResolveFocusedComponentCard(out FrameworkElement? focused, out FrameworkElement? card,
        [NotNullWhen(true)] out ComponentInfo? item, out bool fromRepeaterIndex)
    {
        focused = FocusManager.GetFocusedElement(XamlRoot) as FrameworkElement;
        card = null;
        item = null;
        fromRepeaterIndex = false;

        DependencyObject? cur = focused as DependencyObject;
        for (int hops = 0; cur != null && hops < 12; hops++)
        {
            if (cur is FrameworkElement fe && fe.DataContext is ComponentInfo ci)
            {
                card = fe;
                item = ci;
                return true;
            }
            cur = VisualTreeHelper.GetParent(cur);
        }

        if (focused is UIElement fel)
        {
            try
            {
                int idx = ComponentsRepeater.GetElementIndex(fel);
                // 归属确认:GetElementIndex 只对"本 repeater 生成的容器"有意义,反查回来的容器必须是同一个对象
                bool owned = idx >= 0 && idx < FilteredComponents.Count && ReferenceEquals(ComponentsRepeater.TryGetElement(idx), fel);
                if (owned)
                {
                    card = focused;
                    item = FilteredComponents[idx];
                    fromRepeaterIndex = true;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[A11y] 按 repeater 下标反查组件项抛异常");
            }
        }

        // 焦点停在工具栏/筛选框/外壳导航上是常态,不是故障:认不出卡片就什么也不做
        return false;
    }

    /// <summary>[上下文菜单键盘可达 2026-09,同步 Papers] 在当前持有键盘焦点的组件卡片上弹 ComponentContextMenuFlyout。
    /// 返回值 = 是否真的弹了。内容/列表模式卡片上挂着的 ContextFlyout 继续管鼠标;实测它不响应键盘,所以键盘
    /// 只有本方法一个出口,不会同时开火(IsOpen 闸门已删,它静默 return 反而可能正是"图标模式按了没菜单"的元凶)。</summary>
    private bool OpenComponentContextMenuForFocusedCard()
    {
        if (!TryResolveFocusedComponentCard(out var focused, out var card, out var item, out var itemFromRepeaterIndex))
            return false;

        // 与右键同一套选中语义:单选模式下把选择指到这张卡;多选模式不动已有集合(菜单里的命令作用于整组)
        if (!_isMultiSelectMode && SelectedComponent != item)
            SelectedComponent = item;

        // 锚点默认用"真正持有焦点的元素":内容/列表模式的行是整行宽,不设 Position 时菜单按整行居中 → 弹到行中间
        // (2026-09-21 实测),所以这两种模式把菜单钉到行的左下角。图标模式(认定走路 2,焦点即模板根 ItemContainer)
        // 改锚到里层设了 DataContext 的 ItemRootGrid —— 鼠标右键那条链路证明过 ShowAt 在这个元素上弹得出来,
        // 而它的位置已实测正常,故不加 Position。
        var target = itemFromRepeaterIndex
            ? (focused as FrameworkElement)?.FindName("ItemRootGrid") as FrameworkElement ?? focused as FrameworkElement ?? card
            : focused as FrameworkElement ?? card;
        var options = new FlyoutShowOptions { ShowMode = FlyoutShowMode.Standard };
        if (target?.Name is "ContentItemContainer" or "ListItemContainer")
            options.Position = new Point(0, target.ActualHeight);
        ComponentContextMenuFlyout.ShowAt(target, options);
        return true;
    }

    /// <summary>[空格=一次单击 2026-09-21,同步 Papers] 空格对焦点组件卡片等价于鼠标左键单击一次(按下+松开这一整下):
    /// 多选模式下切换这一项的勾选,单选模式下把选择指到它并播钻入动画 —— 与 Item_PointerPressed 的左键分支加上
    /// Item_PointerReleased 的结果同语义。不复用鼠标那段代码:它耦合 sender 与 PointerRoutedEventArgs。
    /// 单选模式下"焦点即选中"早就把选择做掉了,所以此时按空格多半看不出变化(与鼠标再点一次已选中的同一张卡一致);
    /// Ctrl+空格(加选)没做,键盘侧已有 Ctrl+方向键累加多选,空格只对应"单击"这一下。</summary>
    private bool ActivateFocusedComponentCardByClick()
    {
        if (!TryResolveFocusedComponentCard(out _, out _, out var item, out _)) return false;

        if (_isMultiSelectMode)
        {
            item.IsSelected = !item.IsSelected;
            if (item.IsSelected)
            {
                if (!SelectedComponents.Contains(item)) SelectedComponents.Add(item);
            }
            else SelectedComponents.Remove(item);
            UpdateMultiSelectCount();
        }
        else if (SelectedComponent != item)
        {
            SelectedComponent = item;
            PlayDrillInAnimation();
        }

        return true;
    }

    // [列表键盘可达 2026-09,同步 Papers] 聚焦某张组件卡片的容器(ItemContainer,ElementPrepared 里设成 Tab 停留点的那一层)。
    // 只用 TryGetElement(已实化的容器):用户点得到的卡必然已实化;跨越视口时 GetOrCreateElement 造出的容器
    // 要等一次布局才能接收焦点,那是"方向键一路走通"那一步(方案二)的事,本批不做。
    private bool FocusComponentCard(ComponentInfo item, FocusState state)
    {
        var index = FilteredComponents.IndexOf(item);
        if (index >= 0 && GetVisibleComponentRepeater() is { } repeater
            && repeater.TryGetElement(index) is FrameworkElement card && card.Focus(state))
        {
            _listAnchorIndex = index;
            return true;
        }
        return false;
    }

    // [列表键盘可达 2026-09,同步 Papers] Ctrl+L 的落点:优先回到上次停留过的卡,其次第一张已实化的卡;都没有就写日志,
    // 不做静默失败。官方文档:FrameworkElement 获得键盘焦点时由框架负责把它带进视野,故这里不写 StartBringIntoView。
    // 返回值 = 是否真的搬动了焦点(供 Ctrl 划选的一次性令牌判断要不要留,见 _suppressCtrlFocusMultiSelect)。
    private bool FocusComponentList()
    {
        // [内容/列表模式焦点可达 2026-09] 落点按当前可见模式取容器(原先写死图标 repeater,切到内容/列表模式时
        // 那里一个容器都没实化,Ctrl+L 只会落到"未找到可聚焦的组件卡片"那条日志)
        if (GetVisibleComponentRepeater() is not { } repeater)
        {
            Log.Warning("[A11y] Ctrl+L 取不到可见模式的列表容器");
            return false;
        }

        if (_listAnchorIndex >= 0 && _listAnchorIndex < FilteredComponents.Count
            && repeater.TryGetElement(_listAnchorIndex) is FrameworkElement anchor && anchor.Focus(FocusState.Keyboard))
            return true;

        for (int i = 0; i < FilteredComponents.Count; i++)
        {
            if (repeater.TryGetElement(i) is FrameworkElement card && card.Focus(FocusState.Keyboard))
            {
                _listAnchorIndex = i;
                return true;
            }
        }

        Log.Warning("[A11y] Ctrl+L 未找到可聚焦的组件卡片(列表为空或容器全部未实化)");
        return false;
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
            // 快捷键无点击按钮,动画作用于工具条复制图标(没记过按下/松开时刻,不等两段、直接播勾)
            // [2026-09-18 修] C# 不允许从 finally 里 return/goto(CS0157),空判一律包一层 if
            if (ToolbarCopyIcon is not null)
            {
                await ToolbarCopyIcon_WaitCycleAsync();
                await PlayCopyCheckAnimationAsync(GetToolbarCopyCheckFontIcon(),
                    fadeOutElement: ToolbarCopyIcon,
                    swapIn: ToolbarCopyIcon_SwapToCheckFontIcon,
                    finished: ToolbarCopyIcon_SwapBackToLottie);
            }
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

    // ===================== 全选图标动画(2026-09-18 换“按下十帧”版) =====================
    // 全选图标(字形 E8B3)素材 = WE_Tool.AnimatedVisuals.SelectAllIcon;2026-09-18 换成 20 帧新版:
    // 第 0→10 帧四个方框依次被填满、第 10→20 帧一起缩回空心(首末帧姿态相同)。
    // 本页工具条那枚由按下/松开两段驱动:按下 = 第 0→10 帧;松开 = “PointerOver” 那条(第 10→20 帧,播完)。
    // 注意新版素材把 PressedToNormal 改成了倒放回退段(给详情面板那类普通 Button 用),这里松开**不能**再切 Normal,
    // 否则松开会倒放;菜单项/快捷键入口照旧走 NormalToPlaying(0→20) 整段播一遍、播完归位。
    // [为什么不再来回 toggle] 旧写法在 Normal / Playing 之间反复切,只有状态真正【变化】的那次才播动画,
    // 于是每隔一次点击才看得到动画(日志里 Playing / Normal 逐行交替)——改成两段真实状态后,每次按下/松开都是真实切换。
    private CancellationTokenSource? _selectAllIconResetCts;   // 整段播放播完的归位令牌(连点时取消上一次)
    private bool _selectAllIconPointerDriven;                  // 本次点击已由按下/松开驱动,Click 里不再播整段

    private async void PlaySelectAllIconAnimation()
    {
        // 工具栏按钮的按下/松开已经驱动过动画时不再重复播整段(否则两段会互相打断)
        if (_selectAllIconPointerDriven) return;
        // 工具栏按钮可能被 CommandBar 收进溢出菜单,那种情况下图标还没实化(x:Name 字段为 null),直接跳过
        if (ToolbarSelectAllIcon is null) return;
        _selectAllIconResetCts?.Cancel();
        var cts = new CancellationTokenSource();
        _selectAllIconResetCts = cts;
        AnimatedIcon.SetState(ToolbarSelectAllIcon, "Playing");
        try
        {
            await Task.Delay(340, cts.Token);   // 素材整段约 0.33 秒(20 帧 @60fps)
        }
        catch (TaskCanceledException)
        {
            return;   // 期间又按下(或又点了一次),交给新的一次接管
        }
        if (cts.IsCancellationRequested) return;
        AnimatedIcon.SetState(ToolbarSelectAllIcon, "Normal");
    }

    // (原 SelectAllIcon_PointerPressed / SelectAllIcon_PointerReleased 已删除:按下/松开改由"工具栏四组图标"区块
    //  统一驱动;标志 _selectAllIconPointerDriven 与归位令牌 _selectAllIconResetCts 仍由那边和上面的整段播放共用。)

    // ===================== 多选面板按钮 =====================
    private void SelectAllComponents_Click(object sender, RoutedEventArgs e)
    {
        // 先填充选中集合,后进多选模式:Toggle 期间 Count==0 会被 UpdateMultiSelectCount
        // 的"0 项自动退出"立刻翻回 false(原因同 Papers.SelectAllWallpapers_Click)
        // 全选图标动画:播一遍(见 PlaySelectAllIconAnimation)
        PlaySelectAllIconAnimation();
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

    // ===================== 反选图标动画(2026-09-18 换"按下十帧"版,同步 Papers) =====================
    // 反选图标(字形 E8E6)素材 = WE_Tool.AnimatedVisuals.InvertSelection;30 帧新版:
    // 第 0→10 帧箭头被抹掉一截、第 10→20 帧虚线框擦除、第 20→30 帧虚线框重画并箭头长回(首末帧姿态相同)。
    // 触发点:工具条那枚由按下/松开两段驱动(见"工具栏四组图标"区块);其余入口(弹出工具条 / 右键菜单 / Ctrl+I)
    // 汇入 InvertSelection_Click(),在那里对工具条图标整段播一遍(Playing 对:第 0→30 帧),播完归位。
    // [为什么播完要归位] 状态只有真正变化时才播动画:播完切回 Normal,下一次点击才是真实切换。
    private CancellationTokenSource? _invertSelectionIconResetCts;  // 整段播完的归位令牌(连点时取消上一次)

    /// <summary>播一遍反选动画(整段:第 0→30 帧),播完归位 Normal。工具条按下/松开驱动过时会跳过。</summary>
    private async void PlayInvertSelectionIconAnimation()
    {
        // 工具条那枚已由按下/松开驱动时不再播整段(否则两段之后又整段重播一遍)
        if (_invertSelectionIconPointerDriven) return;
        // 工具栏按钮可能被 CommandBar 收进溢出菜单,那种情况下图标还没实化(x:Name 字段为 null),直接跳过
        if (ToolbarInvertSelectionIcon is null) return;
        _invertSelectionIconResetCts?.Cancel();
        var cts = new CancellationTokenSource();
        _invertSelectionIconResetCts = cts;
        AnimatedIcon.SetState(ToolbarInvertSelectionIcon, "Playing");
        try
        {
            await Task.Delay(500, cts.Token);   // 素材整段 0.5 秒(30 帧 @60fps)
        }
        catch (TaskCanceledException)
        {
            return;   // 期间又点了一次反选,交给新的一次接管
        }
        if (cts.IsCancellationRequested) return;
        AnimatedIcon.SetState(ToolbarInvertSelectionIcon, "Normal");
    }

    private void InvertSelection_Click(object sender, RoutedEventArgs e)
    {
        PlayInvertSelectionIconAnimation();   // 反选图标动画:播一遍(见 PlayInvertSelectionIconAnimation)
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
        // [Shift 区间刷选,同步 Papers] 区间刷选结束的释放可能触发 Tapped,抑制清空(选择已由区间定)
        if (_suppressItemReleased) return;

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

            // [内容/列表模式焦点可达 2026-09,同步图标模式 Item_PointerPressed] 点谁就把键盘焦点交给谁,
            // 此后的方向键/Enter 唤菜单都从这一行起算。图标模式一直有这段,内容/列表模式(本处理器)漏了。
            if (!_isLeftMouseButtonPressed && !_shiftDragActive
                && sender is FrameworkElement pressedRow && pressedRow.DataContext is ComponentInfo pressedRowItem)
                FocusComponentCard(pressedRowItem, FocusState.Pointer);

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

            // 按下拖动经过:左键按住 + 移动经过本卡片(仅选择逻辑,无视觉置顶/放大)
            if (_isLeftMouseButtonPressed && grid.DataContext is ComponentInfo item)
            {
                // [Shift 区间刷选,同步 Papers] Shift+拖动:从锚点向当前卡片延伸连续区间(追加,不抹已有选择)
                if (_shiftDragActive)
                {
                    ExtendShiftRange(item);
                    return;
                }
                // [Ctrl 刷选,同步 Papers] Ctrl+拖动:划过 = 直接设为选中(加选,不取反)
                var ctrlHeld = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control); // Pointer 事件用 KeyModifiers
                if (ctrlHeld)
                {
                    if (!item.IsSelected)
                    {
                        item.IsSelected = true;
                        if (!SelectedComponents.Contains(item))
                            SelectedComponents.Add(item);
                    }
                    UpdateMultiSelectCount();
                    return;
                }
                // 普通拖动:多选刷选(取反)或单选滑动预览
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

            // [悬停视觉,同步 Papers] 置顶+放大只在"鼠标在卡片上动画"设置开启时执行;
            // 关闭时悬停零视觉变化(阴影/绘制序/放大全不动),只有 checkbox 随 IsHovered 显示
            if (ViewModel.ComponentsDisplayVM.IsComponentEnterAnimationEnabled)
            {
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
                // 置顶:向上到 ItemsRepeater 止(最多 6 层)设 ZIndex,让放大卡片盖过相邻卡片
                {
                    DependencyObject topContainer = grid;
                    int hops = 0;
                    while (topContainer != null && topContainer is not ItemsRepeater && hops < 6)
                    {
                        topContainer = VisualTreeHelper.GetParent(topContainer);
                        hops++;
                    }
                    if (topContainer is UIElement uiEl)
                    {
                        Canvas.SetZIndex(uiEl, 10000);
                    }
                }

                var scaleAnimation = compositor.CreateSpringVector3Animation();
                scaleAnimation.Target = "Scale";
                scaleAnimation.FinalValue = new Vector3(1.15f, 1.15f, 1.15f);
                scaleAnimation.DampingRatio = 0.6f;
                scaleAnimation.Period = TimeSpan.FromMilliseconds(50);
                visual.StartAnimation("Scale", scaleAnimation);
                // [悬停阴影,同步 Papers] 不添加悬停阴影层:ElementPrepared 常驻阴影一层

                Visual itemVisual = ElementCompositionPreview.GetElementVisual(grid);
                if (itemVisual?.Parent is ContainerVisual parentVisual)
                {
                    parentVisual.Children.Remove(itemVisual);
                    parentVisual.Children.InsertAtTop(itemVisual);
                }
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

            // [阴影常驻,同步 Papers] 不再移除阴影(ElementPrepared 常驻创建)

            Visual visual = ElementCompositionPreview.GetElementVisual(grid);
            Compositor compositor = visual.Compositor;

            var scaleAnimation = compositor.CreateSpringVector3Animation();
            scaleAnimation.Target = "Scale";
            scaleAnimation.FinalValue = new Vector3(1.0f, 1.0f, 1.0f);
            scaleAnimation.DampingRatio = 0.6f;
            scaleAnimation.Period = TimeSpan.FromMilliseconds(50);

            var capturedParent = VisualTreeHelper.GetParent(grid) as UIElement;

            // 捕获容器用于复位置顶([同步 Papers] 无 GridViewItem,向上到 ItemsRepeater 止,最多 6 层)
            DependencyObject capturedContainer = grid;
            int exitHops = 0;
            while (capturedContainer != null && capturedContainer is not ItemsRepeater && exitHops < 6)
            {
                capturedContainer = VisualTreeHelper.GetParent(capturedContainer);
                exitHops++;
            }

            visual.StartAnimation("Scale", scaleAnimation);

            DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(20);

                Canvas.SetZIndex(grid, 0);
                if (capturedParent != null)
                {
                    Canvas.SetZIndex(capturedParent, 0);
                }
                if (capturedContainer is UIElement capturedUiElement)
                {
                    Canvas.SetZIndex(capturedUiElement, 0);
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
                // [Shift 区间刷选,同步 Papers] 区间刷选结束的释放抑制单选(区间结果已由 SelectShiftRange 定)
                if (!_isMultiSelectMode && !_suppressItemReleased)
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

            // [列表键盘可达 2026-09,同步 Papers] 点谁就把键盘焦点交给谁:此后的方向键/Shift+Tab 都从这张卡起算,
            // 而不是从上次停过的工具栏继续往下走。传 Pointer(不是 Programmatic)以免鼠标点击后冒出键盘焦点框。
            // 左键按住划过(拖拽刷选/区间延伸)时不重复挪焦点:一条手势只认最开始按下那张卡。
            if (!_isLeftMouseButtonPressed && !_shiftDragActive
                && sender is FrameworkElement pressedEl && pressedEl.DataContext is ComponentInfo pressedItem)
                FocusComponentCard(pressedItem, FocusState.Pointer);

            // [Shift 区间刷选,同步 Papers] Shift+按下 = 开始区间刷选:记锚点,等待拖动延伸
            if (sender is FrameworkElement shiftElement && shiftElement.DataContext is ComponentInfo shiftItem)
            {
                var shiftHeld = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift); // Pointer 事件用 KeyModifiers
                if (shiftHeld)
                {
                    _shiftAnchorItem = shiftItem;
                    _shiftDragActive = true;
                    _suppressItemReleased = true; // 区间刷选期间抑制释放单选
                    _shiftRangePicked.Clear();    // [区间改追加,同步 Papers] 新手势开始:回收集只记这一轮自己加的项
                    // 立即选中锚点(区间起点),后续拖动延伸
                    SelectShiftRange(shiftItem, shiftItem);
                    return; // 不进入常规选择逻辑
                }
            }

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
        }
    }

    // [右键释放检测,同步 Papers] 松开点命中图标卡片 → 选中 + 手动弹菜单。
    // [空白区右键 2026-09-21] 三种视图模式的列表容器都参与判定:命中卡片 → 卡片菜单;命中空白 → 背景菜单。
    private void HandleRightReleaseOpenMenu(Point releasePagePoint)
    {
        if (_rightMenuShownThisGesture) return; // 已弹过,防双弹

        var list = GetVisibleComponentScrollView();
        if (list == null || list.ActualWidth <= 0) return;

        FrameworkElement? card = FindComponentCardAt(releasePagePoint, list);

        // 命中卡片:只图标模式手动弹(绕开系统"移动抑制");内容/列表模式仍走卡片自己的 ContextFlyout,
        // 这里必须让开,否则松开时会和系统弹出的卡片菜单叠成两层。
        if (card != null)
        {
            if (!ReferenceEquals(list, ComponentsScrollViewExp)) return;
            if (card is not FrameworkElement fe || fe.DataContext is not ComponentInfo item) return;

            // 与右键菜单语义一致:选中逻辑(多选模式不切单选指针)
            if (!_isMultiSelectMode)
            {
                if (SelectedComponent != item)
                {
                    SelectedComponent = item;
                    PlayDrillInAnimation();
                }
            }
            if (!_isMultiSelectMode)
                SelectedComponent = item;

            _rightMenuShownThisGesture = true;
            // 在松开位置弹菜单(相对卡片定位)
            var posInCard = fe.TransformToVisual(null).TransformPoint(new Point(0, 0));
            var menuPos = new Point(releasePagePoint.X - posInCard.X, releasePagePoint.Y - posInCard.Y);
            ComponentContextMenuFlyout.ShowAt(fe, new FlyoutShowOptions
            {
                Position = menuPos,
                ShowMode = FlyoutShowMode.Standard
            });
            return;
        }

        // 空白处松开 → 弹背景菜单。再要求按下点也不落在卡片上:按住卡片拖到空白松开不该弹背景菜单
        if (FindComponentCardAt(_rightPressPagePoint, list) != null) return;
        ShowBackgroundMenuAt(releasePagePoint, list);
    }

    /// <summary>[空白区右键 2026-09-21] 松开点落在列表可视区内且未命中卡片 → 弹 ScrollViewBackgroundMenu。
    /// 走右键释放这条手动链路而不是挂 ContextFlyout:系统弹出在右键按下与松开之间移动哪怕 1px 也会被输入层
    /// 抑制。这个菜单原先挂在内容/列表模式的 GridView 上,v0.8.0 把 GridView 迁成 ScrollView+ItemsRepeater
    /// (2db62c4)时属性随控件一起丢了,资源本身一直留在 Page.Resources 里。</summary>
    private void ShowBackgroundMenuAt(Point releasePagePoint, FrameworkElement list)
    {
        // 换算到列表容器坐标,顺带用作"松开点是否在列表区内"的判定
        var listTopLeft = list.TransformToVisual(null).TransformPoint(new Point(0, 0));
        var pos = new Point(releasePagePoint.X - listTopLeft.X, releasePagePoint.Y - listTopLeft.Y);
        if (pos.X < 0 || pos.Y < 0 || pos.X > list.ActualWidth || pos.Y > list.ActualHeight) return;

        _rightMenuShownThisGesture = true;
        ScrollViewBackgroundMenu.ShowAt(list, new FlyoutShowOptions
        {
            Position = pos,
            ShowMode = FlyoutShowMode.Standard
        });
    }

    /// <summary>命中测试:页面坐标处命中的元素里,向上找 DataContext 是 ComponentInfo 的卡片根。
    /// container 传当前可见模式的列表 ScrollView。</summary>
    private FrameworkElement? FindComponentCardAt(Point pagePoint, FrameworkElement container)
    {
        var hits = VisualTreeHelper.FindElementsInHostCoordinates(pagePoint, container);
        foreach (var hit in hits)
        {
            DependencyObject cur = hit;
            int hops = 0;
            while (cur != null && hops < 10)
            {
                if (cur is FrameworkElement fel && fel.DataContext is ComponentInfo)
                    return fel;
                cur = VisualTreeHelper.GetParent(cur);
                hops++;
            }
        }
        return null;
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

    // ===== [Shift 区间刷选,同步 Papers] 图标模式:Shift+拖动从锚点延伸连续区间(追加,不抹掉已有选择) =====

    /// <summary>选中 [anchor, end] 区间(含两端)并**追加**到当前选择。按 FilteredComponents(当前筛选列表)索引计算。
    /// [区间改追加 2026-09-22,同步 Papers] 旧实现先清空"当前列表内已选项"再选区间 → 全选之后 Shift+拖一下,
    /// 整片全选就被抹掉只剩这一小段。现在只回收 _shiftRangePicked(本次手势自己加进去的项):
    /// 既有选择保留,来回拖动时区间照样正确收缩,不会留下刷过的尾巴。</summary>
    private void SelectShiftRange(ComponentInfo anchor, ComponentInfo end)
    {
        int a = FilteredComponents.IndexOf(anchor);
        int b = FilteredComponents.IndexOf(end);
        if (a < 0 || b < 0) return; // 不在当前列表(筛选/虚拟化边界),不处理
        int lo = Math.Min(a, b);
        int hi = Math.Max(a, b);

        // 选中区间(只加不动已有:原来就选着的项不进回收集,区间缩小时也不会被它取消)
        var inRange = new HashSet<ComponentInfo>();
        for (int i = lo; i <= hi; i++)
        {
            var item = FilteredComponents[i];
            inRange.Add(item);
            if (!item.IsSelected)
            {
                item.IsSelected = true;
                if (!SelectedComponents.Contains(item)) SelectedComponents.Add(item);
                _shiftRangePicked.Add(item);   // 本手势亲手加进去的,才允许被本手势回收
            }
        }
        // 回收:本手势早前加入、这次已落在区间外的项(往回拖时区间照样跟着缩,不留刷过的尾巴)
        foreach (var prev in _shiftRangePicked.ToList())
        {
            if (inRange.Contains(prev)) continue;
            prev.IsSelected = false;
            SelectedComponents.Remove(prev);
            _shiftRangePicked.Remove(prev);
        }
        UpdateMultiSelectCount();
        if (SelectedComponents.Count > 1 && !IsMultiSelectMode)
        {
            IsMultiSelectMode = true;
        }
        // 详情面板/堆叠视觉同步(区间>1 走堆叠,=1 走单选详情)
        RefreshDisplayedSelectedComponents(forceRebuild: true);
        DispatcherQueue.TryEnqueue(() => UpdateStackVisuals());
    }

    /// <summary>Shift+拖动经过 current:锚点不动,实时向 current 延伸区间。</summary>
    private void ExtendShiftRange(ComponentInfo current)
    {
        if (_shiftAnchorItem == null) { _shiftAnchorItem = current; }
        SelectShiftRange(_shiftAnchorItem, current);
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

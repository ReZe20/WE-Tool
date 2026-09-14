using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WE_Tool.Controls;
using WE_Tool.Converters;
using WE_Tool.Helper;
using WE_Tool.Models;
using WE_Tool.Service;
using WE_Tool.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WE_Tool;

public enum ExtractState
{
    Idle,
    Running,
    Paused,
    Completed
}

/// <summary>搜索建议项:Display = 建议列表显示文本(标题 + ID);Text = 选中后回填值(完整标题,保证可被筛选匹配)。</summary>
public sealed class SearchSuggestion
{
    public string Display { get; init; } = "";
    public string Text { get; init; } = "";
    public WallpaperItem Item { get; init; } = null!;

    // 保底显示(AutoSuggestBox 建议列表无 DisplayMemberPath 时走 ToString)
    public override string ToString() => Display;
}

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class Papers : Page, INotifyPropertyChanged
{
    private readonly IPickerService _pickerService;
    private List<WallpaperItem> _allWallpapers = [];
    private bool _isFirstLoad = true;

    // [性能 2026-09] 图标卡片静态预览的解码宽度上限(物理像素)。
    // 卡片档位最大 300 DIP,高 DPI(150%)下约 450 物理像素,取 480 覆盖并留余量。
    // 库里有 1024×1024 的 preview.jpg(全尺寸解码约 4MB/张),按卡片实际尺寸解码可大幅降低实化开销与内存。
    // 注意:DecodePixelWidth 必须在 UriSource 赋值之前设置才生效。
    private const int IconPreviewDecodeWidth = 480;

    // [性能 2026-09] ItemsRepeater 预渲染缓冲(视口倍数),三套模式列表统一设置。
    // 背景:v0.8.0"全页面列表迁移 ItemsRepeater"时丢掉了迁移前 GridView 的 CacheLength=0 设置
    // (原文:`panelRoot.SetValue(ItemsWrapGrid.CacheLengthProperty, 0); // 不预渲染`),之后一直走
    // 系统默认(约 4 屏)→ 每次实化/回收的容器数翻数倍。窗口化时列少、内容极高(321 项 4 列 ≈ 80 行
    // ≈ 40 屏),同一段滚动的实化次数是全屏(13 列 ≈ 25 行 ≈ 3~4 屏)的十倍量级 ——
    // knife1~4 已排除解码与重绘,矛头正指向这笔固定开销。
    // 取值:0 太激进(滚动时现造容器),沿用 GridView 时代定稿的"备货 1 屏"(当时 0.5 实测会卡)。
    private const double RepeaterCacheLength = 1;

    // [焦点探针 2026-09] 方案一实验:图标模式的卡片能不能成为 Tab 停留点、能不能被讲述人读出名字。
    // true  → ElementPrepared 里给卡片根 Grid 设 IsTabStop + UseSystemFocusVisuals + 朗读名(=标题),
    //         并挂 GotFocus 日志(Logs 页会出现 "[A11y] 卡片获得焦点 ...")。
    //         预期观感:Tab 从上方工具栏进入列表时停在第一张卡(卡片出系统焦点框),紧接着再按 Tab
    //         应跳出整个列表(ItemsRepeater 默认 TabFocusNavigation=Once = 整个列表算一个停留点);
    //         方向键【实测已可用】:ItemsRepeater 官方文档写明它的 XYFocusKeyboardNavigation 默认就是 Enabled,
    //         方向键走 XAML 的 2D 方向导航 —— 卡片一旦可聚焦,方向键就能在卡片之间移动焦点,不用另写代码。
    //         边界:框架只保证"元素获得键盘焦点时带进视野";虚拟化下视口外的容器没实化、里面没有可聚焦元素,
    //         所以方向键走到当前视线的边缘会停住,要继续走必须先滚动。
    // false → 完全不碰卡片的焦点与朗读名,行为与改动前一致(等于撤销本探针)。
    private const bool CardFocusProbe = true;

    // [列表键盘可达 2026-09] 本批总开关(用户诉求:"Tab 要很久才能到列表" + "点选后从那一项起算")。
    // true  → (1) 点击卡片时把键盘焦点交给该卡(传 FocusState.Pointer:官方文档指明"指针交互引起的聚焦传 Pointer",
    //             且它不会像 Programmatic 那样画出键盘焦点框,点击观感不变);
    //         (2) Ctrl+L 直达列表,落点 = 上次停留过的卡(_listAnchorIndex),没记录就用第一张已实化的卡。
    // false → 两条都不生效,行为与改动前完全一致(等于撤销本批改动)。
    // 依赖:CardFocusProbe 必须为 true —— 卡片不是 Tab 停留点时 Focus() 直接返回 false。
    private const bool ListKeyboardAccessProbe = true;
    private int _listAnchorIndex = -1;   // 列表里最后停留过的卡下标:供 Ctrl+L 与"从该项起算"使用
    // [焦点即选中 2026-09] 键盘焦点落到哪张卡,单选模式下就把哪张设为选中项(右侧单张预览/信息面板随之切换)。
    // true  -> Tab / 方向键 / Ctrl+L 走到某张卡 = 选中该卡(像资源管理器:焦点与选中同步);
    //          指针点击仍走 Item_PointerReleased 那条老路(它带钻入动画),这里不抢,否则点击同一张卡就不再播动画。
    // false -> 焦点只移动、不改选中项(回到改动前的行为)。
    private const bool FocusSelectsCardInSingleMode = true;

    // [Ctrl 焦点多选 2026-09] 按住 Ctrl 在列表里移动焦点 = 累加多选(键盘版的 Ctrl+点击 / Ctrl+划过)。
    // true  -> 按住 Ctrl 时焦点落到哪张卡,就把哪张置为选中并加入 SelectedWallpapers,并进入多选模式;
    //          只加选、不取反(与既有 Ctrl+拖动"划过即选中"同一语义);取消仍可点卡片上的复选框或 Ctrl+点击。
    //          进入多选时会把当时的单选壁纸一起带进选择——与 Ctrl+点击完全一致。
    // false -> 按住 Ctrl 移动焦点不改变选中集合(回到改动前的行为)。
    // 注意:Ctrl+L 搬焦点时,那个 Ctrl 是复合键的一部分,不算"按住 Ctrl 划选"(见 _suppressCtrlFocusMultiSelect)。
    private const bool CtrlFocusMultiSelect = true;
    private bool _suppressCtrlFocusMultiSelect;   // Ctrl+L 程序化搬焦点这一下,不当作 Ctrl 划选

    // [Shift 焦点区间 2026-09] 按住 Shift 在列表里移动焦点 = 从锚点延伸连续区间(键盘版的 Shift+拖动刷选)。
    // true  -> 按住 Shift 时焦点落到哪张卡,就把 [锚点, 该卡] 整段设为选中。语义与既有 Shift+拖动完全一致
    //          (SelectShiftRange):区间是"从哪到哪",往回走区间跟着缩,不是往选择里累加;
    //          锚点 = 按住 Shift 之前最后聚焦过的那张卡。
    // false -> 按住 Shift 移动焦点不改变选中集合(回到改动前的行为)。
    // 与 Ctrl 的关系:Ctrl 优先——Ctrl+Shift+方向键按 Ctrl 处理(逐张加选),不会同时走区间。
    private const bool ShiftFocusRangeSelect = true;
    private WallpaperItem? _shiftKeyAnchorItem;   // 键盘区间锚点:Shift 没按住时,每聚焦一张就刷新成这张


    private bool _repeaterCacheApplied;
    public SettingsViewModel ViewModel { get; }
    public ObservableCollection<WallpaperItem> Wallpapers { get; set; } = [];
    public ObservableCollection<WallpaperItem> SelectedWallpapers { get; set; } = [];
    private List<WallpaperItem> _filteredWallpapers = [];
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

    public bool CanGoPrevious => CurrentPage > 1;

    public bool CanGoNext => CurrentPage < ComputeTotalPages(_filteredWallpapers.Count);

    private int ComputeTotalPages(int itemCount)
    {
        int size = ViewModel.WallpaperDisplayVM.PageSize;
        if (size <= 0) size = 30;
        return Math.Max(1, (int)Math.Ceiling(itemCount / (double)size));
    }

    private void NotifyPagerStateChanged()
    {
        // 可能被后台线程的 VM PropertyChanged 直接调用(WallpaperDisplayVM.PaginationMode 等),
        // x:Bind 推送必须在 UI 线程,非 UI 线程时重排到 UI 线程执行
        if (!DispatcherQueue.HasThreadAccess)
        {
            _ = DispatcherQueue.EnqueueAsync(NotifyPagerStateChanged);
            return;
        }

        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        RebuildPageNumberButtons();
    }

    /// <summary>重建底部翻页栏的页码按钮（当前页高亮，超出窗口显示省略号）</summary>
    private void RebuildPageNumberButtons()
    {
        if (PageNumbersPanel == null) return;
        PageNumbersPanel.Children.Clear();

        int total = ComputeTotalPages(_filteredWallpapers.Count);
        var subtle = Application.Current.Resources["SubtleButtonStyle"] as Style;
        var accent = Application.Current.Resources["AccentButtonStyle"] as Style;

        foreach (int page in GetVisiblePages(CurrentPage, total))
        {
            if (page < 0)
            {
                // 省略号按钮:点击弹 Flyout 手动输入页数跳转(2026-09)
                var ellipsisBtn = new Button
                {
                    Content = "…",
                    Tag = total, // 传入总页数供跳转面板用
                    Width = 32,
                    Height = 32,
                    Padding = new Thickness(0),
                    FontSize = 14,
                    Style = subtle
                };
                ellipsisBtn.Click += EllipsisButton_Click;
                PageNumbersPanel.Children.Add(ellipsisBtn);
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
            button.Click += PageNumber_Click;
            PageNumbersPanel.Children.Add(button);
        }
    }

    /// <summary>页码窗口：始终含首页/末页，当前页 ±2，中间用负数占位表示省略号</summary>
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

    private void PageNumber_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int page })
        {
            GoToPage(page);
        }
    }

    // ===== [分页省略号跳转 2026-09] 省略号按钮 → Flyout 输入面板手动跳页 =====

    /// <summary>省略号按钮点击:弹 Flyout,内含输入框 + 跳转按钮,输入页数直接跳转。</summary>
    private void EllipsisButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        int totalPages = btn.Tag is int t ? t : ComputeTotalPages(_filteredWallpapers.Count);

        // ---- 构建 Flyout 内容(纯代码,与分页栏代码建按钮风格一致) ----
        var input = new NumberBox
        {
            Minimum = 1,
            Maximum = totalPages,
            Value = CurrentPage,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, // 带上下微调
            SmallChange = 1,
            Width = 200,
            VerticalAlignment = VerticalAlignment.Center
        };
        var goBtn = new Button
        {
            Content = "跳转",
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            Padding = new Thickness(4)
        };
        panel.Children.Add(input);
        panel.Children.Add(goBtn);

        var flyout = new Flyout { Content = panel };
        // 跳转:取 NumberBox 值并 GoToPage(范围由 NumberBox Minimum/Maximum + GoToPage clamp 保证)
        void DoJump()
        {
            if (!double.IsNaN(input.Value))
            {
                GoToPage((int)input.Value);
            }
            flyout.Hide();
        }
        goBtn.Click += (_, _) => DoJump();
        input.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter) DoJump();
        };
        // 打开后自动聚焦输入框,方便直接打字(NumberBox 聚焦后输入即替换当前值)
        flyout.Opened += (_, _) => input.Focus(FocusState.Programmatic);
        flyout.ShowAt(btn);
    }
    private static readonly Windows.Globalization.Collation.CharacterGroupings _zhGroupings = new Windows.Globalization.Collation.CharacterGroupings("zh-CN");
    private CancellationTokenSource? _filterCts;
    private CancellationTokenSource? _extractCts;
    private RepkgCliService? _extractService;
    private int _extractTotalCount;
    private int _extractCompletedCount;
    private HashSet<string> _extractCompletedNames = [];

    /// <summary>导航徽标是否处于失败(红)状态:失败后保持红色,直到下次提取开始才复位。</summary>
    private bool _navBadgeError;
    public IAsyncRelayCommand OpenSelectedFoldersCommand { get; }
    public IAsyncRelayCommand<WallpaperItem?> UninstallSelectedCommand { get; }
    public IAsyncRelayCommand ExtractSelectedCommand { get; }
    private bool _isWallpaperItemTapped = false;
    private string _searchText = string.Empty;
    private bool _isLeftMouseButtonPressed = false;
    // ===== [右键释放检测 2026-09] 右键按下→松开手动弹菜单(绕开系统"右键带移动抑制"手势判定) =====
    private bool _isRightButtonPressed;       // 右键是否按下(按下置位,松开检测消费)
    private bool _rightMenuShownThisGesture;  // 本次右键手势是否已弹菜单(防双弹)
    // ===== [Shift 区间刷选] 状态字段(图标模式;Shift+拖动从锚点延伸连续区间) =====
    private WallpaperItem? _shiftAnchorItem;    // Shift 区间锚点(按下处)
    private bool _shiftDragActive;              // Shift 区间刷选进行中
    private bool _suppressItemReleased;         // 区间刷选结束抑制 Item 单选释放
    private AppBarButton? _pressedButton; // 当前被按下的 CommandBar 按钮(指针捕获后释放弹回用)
    private Storyboard? _multiEnterStackBoard;   // 进入多选:堆叠图淡入(退出多选时需 Stop,防动画值残留)
    private Storyboard? _multiEnterSingleBoard;  // 进入多选:单图淡出(同上)
    private DateTime _lastDrillInAnimationTime = DateTime.MinValue;
    private bool _isExtracting;
    public bool IsExtracting
    {
        get => _isExtracting;
        set
        {
            if (_isExtracting == value) return;
            _isExtracting = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExtractPreviewVisibility));
            ExtractOverlayVisibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (!value) ExtractState = ExtractState.Completed;
            if (value)
            {
                // 等一帧让布局完成后播放展开动画
                _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    AnimateExtractPanelOpen());
            }
        }
    }

    private ExtractState _extractState = ExtractState.Idle;
    public ExtractState ExtractState
    {
        get => _extractState;
        set
        {
            if (_extractState == value) return;
            _extractState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanResume));
            OnPropertyChanged(nameof(CanStop));
            OnPropertyChanged(nameof(PauseButtonVisibility));
            OnPropertyChanged(nameof(ResumeButtonVisibility));
            OnPropertyChanged(nameof(StopButtonVisibility));
        }
    }

    public bool IsPaused => _extractState == ExtractState.Paused;
    public bool CanPause => _extractState == ExtractState.Running;
    public bool CanResume => _extractState == ExtractState.Paused;
    public bool CanStop => _extractState == ExtractState.Running || _extractState == ExtractState.Paused;
    public Visibility PauseButtonVisibility => CanPause ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResumeButtonVisibility => CanResume ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StopButtonVisibility => CanStop ? Visibility.Visible : Visibility.Collapsed;

    private Visibility _extractOverlayVisibility = Visibility.Collapsed;
    public Visibility ExtractOverlayVisibility
    {
        get => _extractOverlayVisibility;
        set
        {
            if (_extractOverlayVisibility == value) return;
            _extractOverlayVisibility = value;
            OnPropertyChanged();
        }
    }

    private string _extractStatus = string.Empty;
    public string ExtractStatus
    {
        get => _extractStatus;
        set
        {
            if (_extractStatus == value) return;
            _extractStatus = value;
            OnPropertyChanged();
            ExtractStatusVisibility = string.IsNullOrEmpty(value)
                ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private Visibility _extractStatusVisibility = Visibility.Collapsed;
    public Visibility ExtractStatusVisibility
    {
        get => _extractStatusVisibility;
        set
        {
            if (_extractStatusVisibility == value) return;
            _extractStatusVisibility = value;
            OnPropertyChanged();
        }
    }

    private double _extractProgress;
    public double ExtractProgress
    {
        get => _extractProgress;
        set
        {
            if (Math.Abs(_extractProgress - value) < 0.01) return;
            _extractProgress = value;
            OnPropertyChanged();
        }
    }

    public string ExtractProgressText => $"{_extractCompletedCount}/{_extractTotalCount}";

    private bool _isSingleExtract;

    private string _extractSubText = "";
    public string ExtractSubText
    {
        get => _extractSubText;
        set
        {
            if (_extractSubText != value)
            {
                _extractSubText = value;
                OnPropertyChanged();
            }
        }
    }

    private string _extractEntryText = "";
    public string ExtractEntryText
    {
        get => _extractEntryText;
        set
        {
            if (_extractEntryText != value)
            {
                _extractEntryText = value;
                OnPropertyChanged();
            }
        }
    }

    public Visibility ExtractEntryVisibility => _isSingleExtract ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ExtractPreviewVisibility => IsExtracting && _isSingleExtract ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>导入壁纸编辑器按钮可用性（仅场景类且非项目的壁纸）</summary>
    public bool IsImportToEditorEnabled
    {
        get
        {
            if (ViewModel?.SelectedWallpaper is WallpaperItem item)
                return item.IsTypeScene && !item.IsSourceMine;
            return false;
        }
    }
    public ObservableCollection<WallpaperItem> DisplayedSelectedWallpapers { get; } = [];

    /// <summary>多壁纸提取进行中列表数据源:每项 = 一个正在提取的壁纸(名称/预览图/实时进度)</summary>
    public ObservableCollection<ExtractProgressItem> ExtractProgressItems { get; } = [];

    /// <summary>壁纸名 → 进度项索引(事件按名路由,避免集合线性查找)</summary>
    private Dictionary<string, ExtractProgressItem> _extractProgressByName = [];

    private bool _isMultiSelectMode = false;
    private bool _isScanning = false;
    private FrameworkElement? _rightClickedWallpaperElement;
    private static readonly FrozenDictionary<string, Func<SettingsViewModel, bool>> _tagGetters = new Dictionary<string, Func<SettingsViewModel, bool>>
    {
        ["Abstract"] = vm => vm.FilterExpanderVM.Abstract,
        ["Animal"] = vm => vm.FilterExpanderVM.Animal,
        ["Anime"] = vm => vm.FilterExpanderVM.Anime,
        ["Cartoon"] = vm => vm.FilterExpanderVM.Cartoon,
        ["Cgi"] = vm => vm.FilterExpanderVM.Cgi,
        ["Cyberpunk"] = vm => vm.FilterExpanderVM.Cyberpunk,
        ["Fantasy"] = vm => vm.FilterExpanderVM.Fantasy,
        ["Game"] = vm => vm.FilterExpanderVM.Game,
        ["Girls"] = vm => vm.FilterExpanderVM.Girls,
        ["Guys"] = vm => vm.FilterExpanderVM.Guys,
        ["Landscape"] = vm => vm.FilterExpanderVM.Landscape,
        ["Medieval"] = vm => vm.FilterExpanderVM.Medieval,
        ["Memes"] = vm => vm.FilterExpanderVM.Memes,
        ["Mmd"] = vm => vm.FilterExpanderVM.Mmd,
        ["Music"] = vm => vm.FilterExpanderVM.Music,
        ["Nature"] = vm => vm.FilterExpanderVM.Nature,
        ["Pixelart"] = vm => vm.FilterExpanderVM.Pixelart,
        ["Relaxing"] = vm => vm.FilterExpanderVM.Relaxing,
        ["Retro"] = vm => vm.FilterExpanderVM.Retro,
        ["SciFi"] = vm => vm.FilterExpanderVM.SciFi,
        ["Sports"] = vm => vm.FilterExpanderVM.Sports,
        ["Technology"] = vm => vm.FilterExpanderVM.Technology,
        ["Television"] = vm => vm.FilterExpanderVM.Television,
        ["Vehicle"] = vm => vm.FilterExpanderVM.Vehicle,
        ["Unspecified"] = vm => vm.FilterExpanderVM.Unspecified,
    }.ToFrozenDictionary();
    public  bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (_isScanning == value) return;
            _isScanning = value;
            OnPropertyChanged();
        }
    }
    public bool IsMultiSelectMode
    {
        get => _isMultiSelectMode;
        set
        {
            if (_isMultiSelectMode != value)
            {
                _isMultiSelectMode = value;
                OnPropertyChanged();

                if (Wallpapers != null)
                {
                    foreach (var item in Wallpapers)
                    {
                        item.IsInMultiSelectMode = value;
                    }
                }
                UpdateStackVisuals();
                ToggleMultiSelectVisuals(_isMultiSelectMode);
            }
        }
    }
    public IAsyncRelayCommand<WallpaperItem> DeleteWallpaperCommand { get; } = null!;

    /// <summary>卸载按钮是否可用(单选/多选中包含任意壁纸)</summary>
    public bool IsUninstallEnabled
    {
        get
        {
            if (SelectedWallpapers.Count > 0)
                return true;
            return ViewModel?.SelectedWallpaper != null;
        }
    }


    public Papers()
    {
        var app = Application.Current as App;
        if (app?.ViewModel != null)
        {
            ViewModel = app.ViewModel;
            ViewModel.SelectedWallpapers = SelectedWallpapers;
        }
        else
        {
            ViewModel = new SettingsViewModel(new ConfigService(), new PickerService())
            {
                SelectedWallpapers = SelectedWallpapers
            };
        }

        this.InitializeComponent();
        this.DataContext = this;
        // resw 附加属性经 x:Uid 在 WinUI3 不生效(已知限制),tooltip 需代码显式设置
        ToolTipService.SetToolTip(SortToolbarButton, LanguageHelper.GetResource("Toolbar_Sort.ToolTipService.ToolTip"));
        App.ScanCompleted += App_ScanCompleted;

        this.Unloaded += (s, e) =>
        {
            App.ScanCompleted -= App_ScanCompleted;
        };

        this.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Global_PointerPressed), true);
        this.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Global_PointerReleased), true);
        this.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Global_PointerReleased), true);

        // CommandBarFlyout.SecondaryCommands 内部的 AppBarButton 位于独立弹窗中，
        // 不自动随 rootElement 主题变更，打开时应用当前主题
        WallpaperContextMenu.Opened += (s, e) =>
        {
            var theme = App.MainWindowInstance?.Content is FrameworkElement root
                ? root.ActualTheme
                : ElementTheme.Default;
            foreach (var item in WallpaperContextMenu.SecondaryCommands)
            {
                if (item is AppBarButton btn)
                    btn.RequestedTheme = theme;
            }
            UpdateBackupButtonState();
        };

        ViewModel.PropertyChanged += (s, e) =>
        {
            if (ViewModel._isBatchUpdating) return;


            if (e.PropertyName == "SteamWorkshopPath"
                || e.PropertyName?.EndsWith("Expander") == true
                || e.PropertyName?.Contains("Pane") == true
                || e.PropertyName == "SortIndex"
                || e.PropertyName == nameof(ViewModel.SelectedWallpaper))
            {
                if (e.PropertyName == nameof(ViewModel.SelectedWallpaper))
                {
                    OnPropertyChanged(nameof(IsUninstallEnabled));
                    OnPropertyChanged(nameof(IsImportToEditorEnabled));
                    // 多选模式下详情面板的显示/提示由 ToggleMultiSelectVisuals 全权接管:
                    // 此处不得重新点亮无选择提示(否则勾选引发的 SelectedWallpaper 变动会把提示盖回堆叠视图上)
                    if (_isMultiSelectMode) return;
                    SingleSelectionInfoPanel.Visibility = ViewModel.SelectedWallpaper != null
                        ? Visibility.Visible : Visibility.Collapsed;
                    NoSelectionHintText.Visibility = ViewModel.SelectedWallpaper != null
                        ? Visibility.Collapsed : Visibility.Visible;
                    UpdateDetailBlur(); // 详情大图模糊层与列表预览同步
                }
                return;
            }

            _ = ApplyFilters();
        };

        SelectedWallpapers.CollectionChanged += SelectedWallpapers_CollectionChanged;

        ViewModel.FilterExpanderVM.PropertyChanged += (s, e) =>
        {
            if (ViewModel._isBatchUpdating) return;
            _ = ApplyFilters();
        };

        // [实验] 图标模式已换 ItemsRepeater(只显示 preview),以下 GridView 容器逻辑暂注释
        // 虚拟化容器每次数据绑定(含回收复用)都触发——弥补 Loaded 在容器复用时不重发导致的模糊层缺失
        //WallpapersGridView.ContainerContentChanging += (s, e) =>
        //{
        //    if (e.Item is WallpaperItem changingItem &&
        //        FindDescendantGrid(e.ItemContainer, "ItemRootGrid") is Grid changingRoot)
        //    {
        //        // 先设原图组件(按类型:GIF→Skia,其余→静态图),后应用模糊——模糊会隐藏原图,顺序颠倒会被 UpdateSkiaGif 抵消
        //        UpdateSkiaGif(changingRoot, changingItem); // 实验分支:Skia 流式播放(GIF 时覆盖 BitmapImage)
        //        UpdateItemBlur(changingRoot, changingItem);
        //        UpdateTagBadge(changingRoot, changingItem); // 角标按当前标签模式设置(替代 x:Bind OneTime+重建)
        //    }
        //};

        // [实验] ItemsRepeater 容器就绪(绑定到 item 后):设 Image.Source + Skia GIF 切换 + 外观初始化。
        // 替代原 GridView 的 ContainerContentChanging + ShadowRect_Loaded。元素回收复用也触发。
        WallpapersRepeater.ElementPrepared += (s, e) =>
        {
            if (e.Element is not Grid root) return;
            // 用 e.Index 从 ItemsSource 拿 item(不依赖 DataContext 时机;ElementPrepared 时绑定可能未推送)
            WallpaperItem? item = null;
            if (root.DataContext is WallpaperItem dcItem) item = dcItem;
            else if (e.Index >= 0 && e.Index < Wallpapers.Count) item = Wallpapers[e.Index];
            if (item == null) return;
            // [焦点探针 2026-09] 见 CardFocusProbe 说明:只挂图标模式这一个 repeater,内容/列表模式不受影响
            if (CardFocusProbe)
            {
                root.IsTabStop = true;               // WinUI3 里 IsTabStop 在 UIElement 上,非 Control 的 Grid 也能进 Tab 序
                root.UseSystemFocusVisuals = true;   // 让系统画焦点框(非 Control 到底画不画,正是本实验要看的第一件事)
                AutomationProperties.SetName(root, string.IsNullOrEmpty(item.Title) ? "(无标题)" : item.Title); // 探针阶段硬编码中文,留用需走 resw
                // [去重 2026-09] 卡片根已带朗读名(=标题),卡片里的标题 TextBlock 仍是独立可读节点:
                // 讲述人停在卡片上按方向键会把它再念一遍 → 一项读两次。官方文档原话就是"composed UI 会引入
                // duplicate 节点,用 AccessibilityView 归置",故把这条文字设为 Raw(只留在 raw 视图,
                // 不进讲述人主要遍历的 control/content 视图)。只动 UIA 树:渲染/布局/点击/悬停/右键/多选框都不受影响。
                if (root.FindName("ItemTitleText") is TextBlock iconTitleText)
                    AutomationProperties.SetAccessibilityView(iconTitleText, AccessibilityView.Raw);
                else
                    Serilog.Log.Warning("[A11y] 未取到卡片标题节点 ItemTitleText,朗读去重未生效");
                root.GotFocus -= CardRoot_GotFocus;  // 幂等:容器回收复用会重复走到这里,先减后加避免日志与订阅叠加
                root.GotFocus += CardRoot_GotFocus;
            }
            // [外观] ThemeShadow 初始化(原 ShadowRect_Loaded 的阴影部分):ItemRootGrid 投影到 ShadowCastGrid
            if (root.FindName("ItemRootGrid") is Grid itemRootGrid && itemRootGrid.Shadow is not ThemeShadow)
            {
                var shadow = new ThemeShadow();
                if (root.FindName("ShadowCastGrid") is Grid shadowCastGrid)
                    shadow.Receivers.Add(shadowCastGrid);
                itemRootGrid.Shadow = shadow;
            }
            // [性能 2026-09] 先判类型再决定走哪条图路:Skia 接管的 GIF 不再建 BitmapImage。
            // 原实现无条件 new BitmapImage 解一遍、紧接着又 Collapsed 把它藏起来 —— 库里 247 张 GIF
            // 每次实化都白解一次(WIC 解码 + 驻留),纯浪费。
            bool isGif = !string.IsNullOrEmpty(item.Preview)
                && item.Preview.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

            // 设静态图源:仅 Skia 未接管时建(路径为空用占位图)
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
                    // [性能 2026-09] 按卡片实际尺寸解码(DecodePixelWidth 须在 UriSource 之前设才生效)
                    var bmp = new BitmapImage { DecodePixelWidth = IconPreviewDecodeWidth };
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
            // [修复 2026-09] 容器复用(ElementPrepared 每次重绑都触发):按当前 item 重算模糊层。
            // ShadowRect_Loaded(模板根 Loaded)在容器回收复用时不再触发,模糊状态只在这里收敛:
            // 该模糊的补上、不该模糊的清除残留(此前的残留来自 ElementClearing 未清理)。
            if (root.FindName("ItemRootGrid") is Grid blurRootGrid)
                UpdateItemBlur(blurRootGrid, item);
        };
        // [焦点探针 2026-09] 卡片拿到键盘焦点时写一条日志:即使一时听不出讲述人念什么,
        // 也能从 Logs 页确认"Tab 确实停到了卡片上"(这就是本实验的客观读数)。
        void CardRoot_GotFocus(object sender, RoutedEventArgs e)
        {
            // [修复 2026-09] 取 item 别看本元素的 DataContext:真正设了 DataContext 的是里层 ItemRootGrid
            // (DataContext="{x:Bind}"),模板根 ItemContainer 上没设 —— 所以此前日志一律打"(无标题)",
            // "焦点即选中"也因取不到 item 而整条判据不成立。改按 repeater 下标从 ItemsSource 取(与 ElementPrepared 同源)。
            var focusedCard = sender as FrameworkElement;
            WallpaperItem? focusedItem = null;
            var focusedIndex = -1;
            if (focusedCard != null)
            {
                focusedIndex = WallpapersRepeater.GetElementIndex(focusedCard);
                if (focusedIndex >= 0 && focusedIndex < Wallpapers.Count)
                    focusedItem = Wallpapers[focusedIndex];
                // 兜底:下标取不到时(理论上不该发生)退回里层 ItemRootGrid 的 DataContext
                focusedItem ??= (focusedCard.FindName("ItemRootGrid") as FrameworkElement)?.DataContext as WallpaperItem;
            }
            Serilog.Log.Information("[A11y] 卡片获得焦点: {Title}", focusedItem?.Title ?? "(无标题)");
            // [列表键盘可达 2026-09] 记住"最后停留过的卡":Ctrl+L 再进列表时回到这里,而不是回列表头
            if (ListKeyboardAccessProbe && focusedIndex >= 0)
                _listAnchorIndex = focusedIndex;
            // [焦点即选中 2026-09] 焦点即选中(只看单选模式):Tab/方向键/Ctrl+L 走到哪张卡,右侧预览与信息面板就切到哪张。
            // 判据用 FocusState != Pointer:指针交互引起的聚焦由 Item_PointerReleased 那条老路负责(带钻入动画),
            // 这里不重复处理,否则"点击某张卡"会因为选中已成事实而丢掉钻入动画;方向键一路扫过也不逐个播动画(会闪)。
            // [Ctrl 焦点多选 2026-09] 按住 Ctrl 移焦点 = 累加多选(键盘版 Ctrl+点击 / Ctrl+划过)。
            // 顺序必须先"置选中 + 加入集合"再进多选模式:反过来会被多选 setter 里同步跑的 UpdateMultiSelectCount
            // 以 Count==0 立刻翻回 false(与"首次全选要按两次"是同一个旧根因),这里照抄既有 Ctrl+点击的顺序。
            // Ctrl 状态用 GetKeyStateForCurrentThread:本路径是键盘引起的聚焦,读到的是实时按键状态(文件里那条
            // "会读到过期状态"的告诫针对 Pointer 事件);指针路径已被上面的 FocusState 判据排除,Ctrl+点击不会重复处理。
            // [Ctrl 焦点多选 2026-09] Ctrl+L 的一次性屏蔽令牌在这里消费:GotFocus 是异步事件(官方文档明示),
            // 所以不能用"Focus() 调用前后复位"来屏蔽,只能由下一次 GotFocus 自己清零。
            var suppressCtrlSelectOnce = _suppressCtrlFocusMultiSelect;
            _suppressCtrlFocusMultiSelect = false;

            var ctrlHeldOnFocus = CtrlFocusMultiSelect && !suppressCtrlSelectOnce
                && (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

            // [Shift 焦点区间 2026-09] Shift 状态同样走 GetKeyStateForCurrentThread(本路径是键盘引起的聚焦)。
            var shiftHeldOnFocus = ShiftFocusRangeSelect
                && (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

            // 区间锚点维护:Shift 没按住时,锚点 = 刚聚焦的这张(所以 Ctrl 连选之后再按 Shift,锚点落在 Ctrl 停住的那张,
            // 而不是 Ctrl 之前那张);Shift 按住时不动锚点,区间才始终是"锚点 → 当前焦点"这一段。
            if (focusedItem != null && !shiftHeldOnFocus)
                _shiftKeyAnchorItem = focusedItem;

            // [Shift 焦点区间 2026-09] 按住 Shift 移焦点 = 从锚点延伸区间(替换选择,同 Shift+拖动)。
            // 本分支排在 Ctrl 分支前面,所以显式排除 Ctrl 同按(!ctrlHeldOnFocus):Ctrl+Shift 按 Ctrl 处理
            // (逐张加选,不动已有选择集合)——区间是"替换选择",不破坏用户已有选择更安全。
            // 判据保留 FocusState != Pointer:Shift+点击/Shift+拖动走的是鼠标那条老路(Item_PointerPressed 的 shift 分支),
            // 这里不抢,否则区间会被算两遍。
            if (shiftHeldOnFocus && !ctrlHeldOnFocus && focusedCard != null && focusedItem != null
                && focusedCard.FocusState != FocusState.Pointer)
            {
                var rangeAnchor = _shiftKeyAnchorItem;   // 先落局部变量:可空分析对字段比对局部保守
                if (rangeAnchor != null && !ReferenceEquals(rangeAnchor, focusedItem))
                {
                    SelectShiftRange(rangeAnchor, focusedItem);
                    // [临时探针 2026-09] 定位完可删:Logs 出现本行 = Shift+方向键确实完成了区间延伸。
                    Serilog.Log.Information("[A11y] Shift 焦点区间: {Anchor} → {End}",
                        rangeAnchor.Title ?? "(无标题)", focusedItem.Title ?? "(无标题)");
                }
                else
                {
                    // [临时探针 2026-09] 定位完可删:锚点还没建立(第一次就是 Shift 按下)或停在原地,看这两个布尔。
                    Serilog.Log.Debug("[A11y] Shift 焦点区间跳过: 有锚点={HasAnchor} 锚点即本项={Same}",
                        _shiftKeyAnchorItem != null, ReferenceEquals(_shiftKeyAnchorItem, focusedItem));
                }
                return;
            }
            if (ctrlHeldOnFocus && focusedCard != null && focusedItem != null
                && focusedCard.FocusState != FocusState.Pointer)
            {
                bool newlySelected = false;
                if (!focusedItem.IsSelected)
                {
                    focusedItem.IsSelected = true;
                    newlySelected = true;
                }
                if (!SelectedWallpapers.Contains(focusedItem))
                {
                    SelectedWallpapers.Add(focusedItem);
                    newlySelected = true;
                }
                UpdateMultiSelectCount();
                if (!_isMultiSelectMode)
                {
                    IsMultiSelectMode = true;
                }
                // [临时探针 2026-09] 定位完可删:Logs 出现本行 = Ctrl+方向键确实搬动了焦点并完成加选。
                Serilog.Log.Information("[A11y] Ctrl 焦点多选: {Title} (新增={Added})",
                    focusedItem.Title ?? "(无标题)", newlySelected);
                return;
            }

            if (FocusSelectsCardInSingleMode && ListKeyboardAccessProbe && !_isMultiSelectMode
                && focusedCard != null && focusedItem != null
                && focusedCard.FocusState != FocusState.Pointer
                && ViewModel.SelectedWallpaper != focusedItem)
            {
                ViewModel.SelectedWallpaper = focusedItem;
                // [临时探针 2026-09] 确认"确实选中了":Logs 页若出现本行而右侧面板没跟着变,
                // 问题就在选中之后的可视化(面板/绑定),不在本判据。定位完可删。
                Serilog.Log.Information("[A11y] 焦点即选中: {Title}", focusedItem.Title ?? "(无标题)");
            }
            else if (FocusSelectsCardInSingleMode && ListKeyboardAccessProbe)
            {
                // [临时探针 2026-09] 没选中时把每条判据的取值一次打全(定位完可删)。
                Serilog.Log.Debug("[A11y] 焦点即选中跳过: 多选={Multi} 指针焦点={Ptr} 取到item={Got} 已是本项={Same}",
                    _isMultiSelectMode,
                    focusedCard?.FocusState == FocusState.Pointer,
                    focusedItem != null,
                    focusedItem != null && ViewModel.SelectedWallpaper == focusedItem);
            }
        }
        // 元素移出(回收/滚动走远):停 GIF + 清除模糊层残留,保证容器回池时是干净状态
        // [修复 2026-09] 原实现只停 GIF;模糊层不清理,卡片带着模糊层回池 → 重绑到不需模糊的新 item 时残留模糊
        WallpapersRepeater.ElementClearing += (s, e) =>
        {
            if (e.Element is not Grid root) return;
            if (root.FindName("SkiaGifCanvas") is SkiaGifView skia)
                skia.Stop();
            // 隐藏并清空模糊层;原图可见性由下一次 ElementPrepared 按新 item 重设,不在此处理
            if (root.FindName("ItemBlurOverlay") is Image blurOv)
            {
                blurOv.Visibility = Visibility.Collapsed;
                blurOv.Source = null;
                _blurOverlayOwner.Remove(blurOv); // [修复] 同步清归属,防回收复用后旧归属误放行
            }
        };
        ViewModel.WallpaperDisplayVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(WallpaperDisplayViewModel.AutoPlayGif))
            {
                // 仅刷新本页可见动图，不清其它页面缓存（方案 A：页面订阅 VM 变化自刷新）
                UiHelper.ReloadGifImages(this);
                return;
            }
            if (e.PropertyName == nameof(WallpaperDisplayViewModel.PaginationMode))
            {
                // 分页开关/每页数量变化：立即刷新翻页栏状态（ApplyFilters 有延迟，先同步一次）
                NotifyPagerStateChanged();
            }
            if (e.PropertyName == nameof(WallpaperDisplayViewModel.WallpaperListMinWidth))
            {
                // 小/中/大档位变化:列宽公式随档位值联动重算(切换档位立即生效,不等窗口 resize)
                UpdateExpUniformLayoutMinWidth(); // [实验] 图标模式 UniformGridLayout 钳制
                UpdateWallpapersListLayoutMinWidth(); // [全迁] 列表模式 UniformGridLayout 钳制
                return;
            }
            if (e.PropertyName is nameof(WallpaperDisplayViewModel.BlurEveryone)
                or nameof(WallpaperDisplayViewModel.BlurTeen)
                or nameof(WallpaperDisplayViewModel.BlurAdult))
            {
                // 预览模糊年龄段开关变化:刷新所有可见卡片的模糊层
                RefreshAllItemBlurs();
                UpdateDetailBlur(); // 详情大图同步
                return;
            }
            _ = ApplyFilters();
        };

        this.Loaded += async (s, e) =>
        {

            if (_isFirstLoad)
            {
                _isFirstLoad = false;
                await ViewModel.InitializeAsync();
                await RefreshWallpaperList();
            }

            // [性能 2026-09] 先设预渲染缓冲(减少实化/回收容器数),再钳列宽
            ApplyRepeaterCacheLength();
            // ItemsRepeater 首次布局后钳制列宽(防崩;GridView 已全迁 ItemsRepeater)
            UpdateExpUniformLayoutMinWidth(); // [实验] 图标模式首帧钳制 MinItemWidth(防崩)
            UpdateWallpapersListLayoutMinWidth(); // [全迁] 列表模式首帧钳制
        };

        OpenSelectedFoldersCommand = new AsyncRelayCommand(async () =>
        {
            HideWallpaperContextMenu();
            await ViewModel.PathManagementVM.OpenSelectedWallpapersFoldersAsync();
        });
        UninstallSelectedCommand = new AsyncRelayCommand<WallpaperItem?>(async item =>
        {
            HideWallpaperContextMenu();

            var itemsToUninstall = ViewModel.SelectedWallpapers.Count > 0
                ? SelectedWallpapers.ToList()
                : ViewModel.SelectedWallpaper is not null ? [ViewModel.SelectedWallpaper] : [];

            if (itemsToUninstall.Count == 0) return;

            // 拆分创意工坊(需取消订阅)与非创意工坊(直接删文件)
            var workshopItems = itemsToUninstall.Where(w => w.Source == "workshop").ToList();
            var nonWorkshopItems = itemsToUninstall.Where(w => w.Source != "workshop").ToList();

            bool confirmed = await DialogHelper.ShowConfirmDialogAsync("卸载",
                $"确定要卸载选中的 {itemsToUninstall.Count} 个壁纸吗？\n\n" +
                (workshopItems.Count > 0
                    ? $"创意工坊壁纸 {workshopItems.Count} 个:将取消订阅并删除本地文件。\n"
                    : "") +
                (nonWorkshopItems.Count > 0
                    ? $"非创意工坊壁纸 {nonWorkshopItems.Count} 个:将直接删除本地文件。"
                    : ""),
                "卸载",
                "取消");
            if (!confirmed) return;

            await UninstallWallpapersAsync(workshopItems, nonWorkshopItems);

            Log.Information("已卸载 {Count} 个壁纸: {Titles}", itemsToUninstall.Count,
                string.Join("; ", itemsToUninstall.Select(w => w.Title ?? w.WorkshopID ?? "未知")));

            ViewModel.SelectedWallpaper = null;
        });

        ExtractSelectedCommand = new AsyncRelayCommand(async () =>
        {
            HideWallpaperContextMenu();
            await ExtractSelectedWallpapersAsync();
        });

        _pickerService = new PickerService();
    }
    private void SelectedWallpapers_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RefreshDisplayedSelectedWallpapers();
        UpdateStackVisuals();
        OnPropertyChanged(nameof(IsUninstallEnabled));
    }
    private int _lastStackCount; // 上次布局的卡片数,用于识别"新增了卡片"

    private void UpdateStackVisuals()
    {
        int count = DisplayedSelectedWallpapers.Count;
        bool grew = count > _lastStackCount && _lastStackCount > 0; // 新增了卡片(初始化不算)
        for (int i = 0; i < count; i++)
        {
            var container = StackedImagesControl.ContainerFromIndex(i) as FrameworkElement;
            if (container == null) continue;

            container.Visibility = Visibility.Visible;
            int depth = count - 1 - i; // 集合尾=最新:深度 0 居中,越老越深(朝左上)
            ApplyStackAnimation(container, depth, entering: grew && i == count - 1); // 最后一张=新卡
            Canvas.SetZIndex(container, i); // 新卡 i 最大 => 最上层
            if (DisplayedSelectedWallpapers[i] is WallpaperItem stackItem)
                UpdateStackItemBlur(container, stackItem); // 预览模糊同步到堆叠卡片
        }
        _lastStackCount = count;
    }
    /// <summary>实验分支(feature/skia-gif):GIF 卡片用 Skia 流式播放覆盖 BitmapImage 直播(验证流畅度/内存/CPU)</summary>
    private static void UpdateSkiaGif(Grid root, WallpaperItem item)
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

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // 页面缓存:切走时 Unloaded 停播,切回后容器不重新绑定 → 延迟一帧重启可见 GIF 动画
        DispatcherQueue.TryEnqueue(() => RestartVisibleGifPlayback());
    }

    /// <summary>遍历可见容器重启 GIF 播放+角标(页面缓存切回时;容器未就绪/无项时无害)。
    /// [实验] 图标模式已换 ItemsRepeater,此逻辑(基于 GridViewItem 容器)暂禁用,方法体留空。</summary>
    private void RestartVisibleGifPlayback()
    {
        // [实验] 图标 GridView 已移除,原 ItemsPanelRoot 遍历逻辑暂注释
        // 非反射(AOT 兼容):ItemsPanelRoot 返回类型是 Panel(基类),Children 是 Panel 属性,直接访问即可,不强转 ItemsWrapGrid
        //if (WallpapersGridView.ItemsPanelRoot is not { } panelRoot) return;
        //foreach (var child in panelRoot.Children)
        //{
        //    if (child is not GridViewItem container) continue;
        //    if (container.ContentTemplateRoot is not Grid root) continue;
        //    if (WallpapersGridView.ItemFromContainer(container) is WallpaperItem item)
        //    {
        //        UpdateSkiaGif(root, item);
        //        UpdateTagBadge(root, item);
        //    }
        //}
    }

    private void StackedImage_Loaded(object sender, RoutedEventArgs e)
    {
        // 从 SelectedWallpapers 集合计算相对位置
        if (sender is FrameworkElement fe && fe.DataContext is WallpaperItem item)
        {
            int idx = SelectedWallpapers.IndexOf(item);
            if (idx < 0) return;
            fe.Visibility = Visibility.Visible;
            int depth = Math.Min(4, SelectedWallpapers.Count - 1 - idx); // 深度封顶 4,保持原有可视展开范围
            ApplyStackAnimation(fe, depth, entering: true); // 容器刚 Loaded=新卡,右下滑入居中
            Canvas.SetZIndex(fe, idx); // idx 大=新卡=上层
            UpdateStackItemBlur(fe, item); // 预览模糊同步到堆叠卡片
        }
    }

    private void StopAllStackAnimations()
    {
        for (int i = 0; i < DisplayedSelectedWallpapers.Count; i++)
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
    private async void App_ScanCompleted(object? sender, EventArgs e)
    {
        await DispatcherQueue.EnqueueAsync(async () =>
        {
            await RefreshWallpaperList();
        });
    }
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
        // [右键释放检测 2026-09] 右键按下:置标志(松开时手动弹菜单,绕开系统移动抑制)
        if (pt.Properties.PointerUpdateKind is Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed)
        {
            _isRightButtonPressed = true;
            _rightMenuShownThisGesture = false;
        }
        // CommandBar 内按钮按下反馈:按钮缩小(AddHandler handledEventsToo:true 能收到 Button 内部的 handled 事件)
        if (e.OriginalSource is FrameworkElement fe && IsDescendantOf(fe, ToolbarCommands))
        {
            if (FindAncestorButton(fe) is { } btn)
            {
                _pressedButton = btn;                       // 记录按下的按钮(供释放时弹回)
                btn.CapturePointer(e.Pointer);              // 捕获指针:移开按钮后释放仍收到事件
                PlayPressScale(btn, 0.88f);
                // 全选图标:按下就开始播【填满】那一段(松开接着播回程,见 SelectAllIcon_PointerReleased)
                if (btn == ToolbarSelectAllButton) SelectAllIcon_PointerPressed();
            }
        }
    }
    private void Global_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isLeftMouseButtonPressed = false;
        _shiftDragActive = false; // [Shift 区间刷选] 释放结束区间模式

        // [右键释放检测 2026-09] 右键松开:命中测试找卡片 → 手动弹菜单(绕开系统"移动抑制")
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
            PlayPressScale(pressedBtn, 1f);
            // 刷新按钮:图标旋转动画在鼠标松开后播放(按下只缩小)
            if (pressedBtn == RefreshButton)
            {
                PlayRefreshSpin();
            }
            // 全选图标:松开时从第 30 帧接着播到第 60 帧(四个方框缩回空心)
            if (pressedBtn == ToolbarSelectAllButton) SelectAllIcon_PointerReleased();
        }
    }

    // ===================== 刷新图标动画(2026-09) =====================
    // 刷新图标由静态字形 E777 换成 Lottie 动画:XAML 里 <AnimatedIcon x:Name="ToolbarRefreshIcon">,
    // 素材 = WE_Tool.AnimatedVisuals.RefreshIcon(AnimatedVisuals/RefreshIcon.cs,LottieGen 生成),回退字形仍是 E777。
    // 素材内容 = 整层旋转 0°→360°(关键帧在第 0/27/40/59 帧),1 秒;标记对 NormalToPlaying_Start/_End(第 0→60 帧)就是这一整段。
    // 触发点没变:仍然是鼠标松开时播一遍(按下只有按钮缩小反馈)。
    // 原 Composition 旋转(2 圈/2000ms)已被素材自带的旋转取代,整段删掉;要退回旧行为用 git 即可。
    // [为什么播完要归位] 状态只有真正变化时才播动画:播完切回 Normal,下一次点击才是真实切换
    //(全选那边踩过这个坑,见 PlaySelectAllIconAnimation 的注释)。
    private const bool RefreshIconAnimationProbe = true;   // false = 回到"静止图标"(不播动画)
    private CancellationTokenSource? _refreshIconResetCts;  // 整段播完的归位令牌(连点时取消上一次)

    /// <summary>播一遍刷新动画(第 0→60 帧转一圈),播完归位 Normal。调用点:Global_PointerReleased 里刷新按钮松开时。</summary>
    private async void PlayRefreshSpin()
    {
        if (!RefreshIconAnimationProbe) return;
        // 工具栏按钮可能被 CommandBar 收进溢出菜单,那种情况下图标还没实化(x:Name 字段为 null),直接跳过
        if (ToolbarRefreshIcon is null) return;
        _refreshIconResetCts?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshIconResetCts = cts;
        Log.Information("[动画] 刷新图标状态切换 → Playing(整段:第 0→60 帧)");
        AnimatedIcon.SetState(ToolbarRefreshIcon, "Playing");
        try
        {
            await Task.Delay(1000, cts.Token);   // 素材整段 1 秒(60 帧 @60fps)
        }
        catch (TaskCanceledException)
        {
            return;   // 期间又点了刷新,交给新的一次接管
        }
        if (cts.IsCancellationRequested) return;
        Log.Information("[动画] 刷新图标状态归位 → Normal");
        AnimatedIcon.SetState(ToolbarRefreshIcon, "Normal");
    }

    // ===================== 反选图标动画(2026-09) =====================
    // 反选图标由静态字形 E8E6 换成 Lottie 动画:XAML 里 4 处 <AnimatedIcon>,Source = WE_Tool.AnimatedVisuals.InvertSelection,
    // 回退字形仍是 E8E6(系统关掉动画效果时自动退回)。素材内容:第 0~30 帧箭头"从头部向尾部"被逐片抹掉(生成式消失),
    // 第 30~60 帧再"从尾部向头部"逐片长回来(生成式出现),灰色虚线框全程不动;
    // 标记对 NormalToPlaying_Start/_End(第 0→60 帧)就是这一整段。
    // 触发点:所有反选入口(工具栏按钮 / 弹出工具条 / 滚动区右键菜单 / Ctrl+I)都汇入 InternalInvertSelection(),在那里播一遍。
    // [为什么播完要归位] 状态只有真正变化时才播动画:播完切回 Normal,下一次点击才是真实切换。
    private const bool InvertSelectionIconAnimationProbe = true;   // false = 回到"静止图标"(不播动画)
    private CancellationTokenSource? _invertSelectionIconResetCts;  // 整段播完的归位令牌(连点时取消上一次)

    /// <summary>播一遍反选动画(第 0→60 帧),播完归位 Normal。调用点:InternalInvertSelection()。</summary>
    private async void PlayInvertSelectionIconAnimation()
    {
        if (!InvertSelectionIconAnimationProbe) return;
        // 工具栏按钮可能被 CommandBar 收进溢出菜单,那种情况下图标还没实化(x:Name 字段为 null),直接跳过
        if (ToolbarInvertSelectionIcon is null) return;
        _invertSelectionIconResetCts?.Cancel();
        var cts = new CancellationTokenSource();
        _invertSelectionIconResetCts = cts;
        Log.Information("[动画] 反选图标状态切换 → Playing(整段:第 0→60 帧)");
        AnimatedIcon.SetState(ToolbarInvertSelectionIcon, "Playing");
        try
        {
            await Task.Delay(1000, cts.Token);   // 素材整段 1 秒(60 帧 @60fps)
        }
        catch (TaskCanceledException)
        {
            return;   // 期间又点了一次反选,交给新的一次接管
        }
        if (cts.IsCancellationRequested) return;
        Log.Information("[动画] 反选图标状态归位 → Normal");
        AnimatedIcon.SetState(ToolbarInvertSelectionIcon, "Normal");
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

    private void RefreshDisplayedSelectedWallpapers(bool forceRebuild = false)
    {
        // 全选/反选/退出多选 等批量操作时强制重建
        if (forceRebuild)
        {
            StopAllStackAnimations();
            RebuildDisplayedFromLast5();
            return;
        }

        // 单张选择/取消 时走增量更新（最自然）
        // 这里我们不传 EventArgs，所以用简单判断：如果当前显示的最后一张不是 Selected 的最后一张 → 说明新增了
        if (DisplayedSelectedWallpapers.Count == 0 ||
            !DisplayedSelectedWallpapers.Last().Equals(SelectedWallpapers.LastOrDefault()))
        {
            if (SelectedWallpapers.Count <= 5)
            {
                StopAllStackAnimations();
                RebuildDisplayedFromLast5();
            }
            else
            {
                // 增量：挤掉最旧的一张，加入最新的一张（前4张容器保持不变！）
                if (DisplayedSelectedWallpapers.Count >= 5)
                {
                    DisplayedSelectedWallpapers.RemoveAt(0);   // 移除最底层（最早的）
                }
                DisplayedSelectedWallpapers.Add(SelectedWallpapers.Last()); // 加入最新（最顶层）
            }
        }
    }

    private void RebuildDisplayedFromLast5()
    {
        DisplayedSelectedWallpapers.Clear();
        int total = SelectedWallpapers.Count;
        int start = Math.Max(0, total - 5);
        for (int i = start; i < total; i++)
        {
            DisplayedSelectedWallpapers.Add(SelectedWallpapers[i]);
        }
    }

    private static void ApplyStackAnimation(FrameworkElement element, int depth, bool entering = false)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        Compositor compositor = visual.Compositor;

        // 1:1 正方形中心点
        float size = 200f;
        visual.CenterPoint = new Vector3(size / 2, size / 2, 0f);

        // 整齐 deck 层叠(用户指定):所有卡片正对(0°)。调用方传入 depth(距最新层数,最新=0):
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

        // 深度缩放:距最新越远越小(1.0 → -3%/层)——"近大远小"透视层级,卡片保持完全不透明,
        // 比透明度退让更干净(旧卡不发虚),叠层轮廓也更清晰
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

private void ToggleMultiSelectVisuals(bool isMulti)
    {
        // 过渡 = 淡入淡出(用户指定):Composition 逐值动画在本环境概率性延迟提交,
        // 改用 XAML Storyboard 双动画交叉——单图淡出 / 堆叠图淡入,时间轴由框架保证
        CancelAllAnimations();

        if (isMulti)
        {
            NoSelectionHintText.Visibility = Visibility.Collapsed; // 最高优先级:进入多选即刻隐藏无结果提示

            // 如果单选有焦点，顺便加入多选
            if (ViewModel.SelectedWallpaper != null && !SelectedWallpapers.Contains(ViewModel.SelectedWallpaper))
            {
                ViewModel.SelectedWallpaper.IsSelected = true;
                SelectedWallpapers.Add(ViewModel.SelectedWallpaper);
                RefreshDisplayedSelectedWallpapers(forceRebuild: true);
            }
            else if (SelectedWallpapers.Count > 0)
            {
                RefreshDisplayedSelectedWallpapers(forceRebuild: true);
            }

            SinglePreviewBorder.CornerRadius = new CornerRadius(8);

            // 堆叠图先摆到可见状态但全透明,再淡入;淡入完成后隐藏单图面板
            StackedImagesControl.Opacity = 0;
            StackedImagesControl.Visibility = Visibility.Visible;

            var fadeInStack = new DoubleAnimation { To = 1.0, Duration = TimeSpan.FromMilliseconds(180) };
            Storyboard.SetTarget(fadeInStack, StackedImagesControl);
            Storyboard.SetTargetProperty(fadeInStack, "Opacity");
            var stackBoard = new Storyboard();
            _multiEnterStackBoard = stackBoard;
            stackBoard.Children.Add(fadeInStack);
            stackBoard.Completed += (s, e) =>
            {
                // 竞态防护:快速进出多选时,本回调(180ms 后)可能晚于退出多选执行,
                // 若已不在多选模式则不得隐藏单图面板(否则 preview/文字被错误隐藏)
                if (!_isMultiSelectMode) return;
                SinglePreviewBorder.Visibility = Visibility.Collapsed;
                SingleSelectionInfoPanel.Visibility = Visibility.Collapsed;
            };
            stackBoard.Begin();

            // 单图同时淡出(180ms 同速交叉),完成后复位透明度并隐藏单图
            var fadeOutSingle = new DoubleAnimation { To = 0.0, Duration = TimeSpan.FromMilliseconds(180) };
            Storyboard.SetTarget(fadeOutSingle, SinglePreviewBorder);
            Storyboard.SetTargetProperty(fadeOutSingle, "Opacity");
            var singleBoard = new Storyboard();
            _multiEnterSingleBoard = singleBoard;
            singleBoard.Children.Add(fadeOutSingle);
            singleBoard.Completed += (s, e) =>
            {
                // 竞态防护:同 stackBoard.Completed——快速进出多选时晚到的回调不得隐藏单图
                if (!_isMultiSelectMode) return;
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
            // Stop 进入多选的 Storyboard:它们在 180ms 后仍会把 Opacity 拉向目标值(0),
            // 快速进出多选时,退出复位 Opacity=1 会被动画值覆盖(border.Opacity=0 → preview 不可见)
            _multiEnterStackBoard?.Stop();
            _multiEnterSingleBoard?.Stop();
            _multiEnterStackBoard = null;
            _multiEnterSingleBoard = null;
            SelectedWallpapers.CollectionChanged -= SelectedWallpapers_CollectionChanged;
            ViewModel.SuspendSelectedWallpapersCollectionChanged();

            SinglePreviewBorder.Visibility = Visibility.Visible;
            SingleSelectionInfoPanel.Visibility = ViewModel.SelectedWallpaper != null
                ? Visibility.Visible : Visibility.Collapsed;
            NoSelectionHintText.Visibility = ViewModel.SelectedWallpaper != null
                ? Visibility.Collapsed : Visibility.Visible;

            StackedImagesControl.Visibility = Visibility.Collapsed;
            MultiSelectionInfoPanel.Visibility = Visibility.Collapsed;

            SinglePreviewBorder.CornerRadius = new CornerRadius(0);
            SinglePreviewBorder.Opacity = 1;  // 复位,防上次淡出被快速操作打断后残留半透明
            StackedImagesControl.Opacity = 1;

            foreach (var wp in SelectedWallpapers)
            {
                wp.IsSelected = false;
            }
            SelectedWallpapers.Clear();

            SelectedWallpapers.CollectionChanged += SelectedWallpapers_CollectionChanged;
            ViewModel.ResumeSelectedWallpapersCollectionChanged();

            RefreshDisplayedSelectedWallpapers(forceRebuild: true);
            UpdateMultiSelectCount();
        }
    }
    private void CancelAllAnimations()
    {
        // 打断单选主面板（SinglePreviewBorder）
        var singleVisual = ElementCompositionPreview.GetElementVisual(SinglePreviewBorder);
        if (singleVisual != null)
        {
            singleVisual.StopAnimation("Scale");
            singleVisual.StopAnimation("Offset");
        }

        // 打断单图钻入动画（PlayDrillInAnimation 用的）
        var imageVisual = ElementCompositionPreview.GetElementVisual(SinglePreviewImage);
        if (imageVisual != null)
        {
            imageVisual.StopAnimation("Scale.X");
            imageVisual.StopAnimation("Scale.Y");
            imageVisual.StopAnimation("Opacity");
            // Stop 会把属性冻结在动画中间态(如 Opacity=0.3、Scale=0.9)——必须复位到正常值,
            // 否则下次显示时图片半透明/微缩,且视觉残留导致后续动画"看起来没触发"
            imageVisual.Scale = Vector3.One;
            imageVisual.Opacity = 1f;
        }

        // 打断堆叠图片的所有动画（复用你已有的方法）
        StopAllStackAnimations();

        // 额外保险：把所有堆叠容器动画也停掉（防止残留）
        for (int i = 0; i < DisplayedSelectedWallpapers.Count; i++)
        {
            var container = StackedImagesControl.ContainerFromIndex(i) as FrameworkElement;
            if (container == null) continue;
            var visual = ElementCompositionPreview.GetElementVisual(container);
            if (visual != null)
            {
                visual.StopAnimation("Scale");
                visual.StopAnimation("Offset");
                visual.StopAnimation("RotationAngleInDegrees");
            }
        }
    }
    private void UpdateMultiSelectCount()
    {
        MultiSelectCountText?.Text = $"已选择 {SelectedWallpapers.Count} 项";
        if (SelectedWallpapers.Count == 0)
        {
            IsMultiSelectMode = false;
        }
    }

    // ===== [Shift 区间刷选 2026-09] 图标模式:Shift+拖动从锚点延伸连续区间(替换选择) =====

    /// <summary>选中 [anchor, end] 区间(含两端)并替换当前选择。按 Wallpapers(当前筛选列表)索引计算。</summary>
    private void SelectShiftRange(WallpaperItem anchor, WallpaperItem end)
    {
        int a = Wallpapers.IndexOf(anchor);
        int b = Wallpapers.IndexOf(end);
        if (a < 0 || b < 0) return; // 不在当前列表(筛选/虚拟化边界),不处理
        int lo = Math.Min(a, b);
        int hi = Math.Max(a, b);

        // 清空现有选择(只清当前列表内已选的,避免破坏列表外多选)
        foreach (var sel in SelectedWallpapers.ToList())
        {
            if (Wallpapers.Contains(sel))
            {
                sel.IsSelected = false;
                SelectedWallpapers.Remove(sel);
            }
        }
        // 选中区间
        for (int i = lo; i <= hi; i++)
        {
            var item = Wallpapers[i];
            if (!item.IsSelected)
            {
                item.IsSelected = true;
                SelectedWallpapers.Add(item);
            }
        }
        UpdateMultiSelectCount();
        if (SelectedWallpapers.Count > 1 && !IsMultiSelectMode)
        {
            IsMultiSelectMode = true;
        }
        // 详情面板/堆叠视觉同步(区间>1 走堆叠,=1 走单选详情)
        RefreshDisplayedSelectedWallpapers(forceRebuild: true);
        DispatcherQueue.TryEnqueue(() => UpdateStackVisuals());
    }

    /// <summary>Shift+拖动经过 current:锚点不动,实时向 current 延伸区间。</summary>
    private void ExtendShiftRange(WallpaperItem current)
    {
        if (_shiftAnchorItem == null) { _shiftAnchorItem = current; }
        SelectShiftRange(_shiftAnchorItem, current);
    }

    private bool _refreshInFlight; // [性能 2026-09] 刷新防重入:Loaded 首载 + ScanCompleted 几乎同时触发,避免并发两次全量过滤
    public async Task RefreshWallpaperList()
    {
        // 已在刷新(等扫描/过滤)则跳过——同一次扫描的重复触发数据相同,并发只会浪费一次全量过滤
        if (_refreshInFlight) return;
        _refreshInFlight = true;
        ShowScanProgress(true); // [扫描进度 2026-09] 扫描/刷新期间显示列表区中央转圈
        try
        {
            // 等待初始扫描链路完成（读配置 → 启动扫描 → 扫描完成），确保 GlobalAllWallpapers 已填充。
            // 注意：不能只 await App.ScanTask —— 启动时它可能还是 Task.CompletedTask
            //（ScanWallpaperWhenStart 需先读完配置才赋值），会导致拿到空数据。
            if (App.InitialScanTask != null)
            {
                await App.InitialScanTask;
            }
            else if (App.ScanTask.IsCompleted && App.GlobalAllWallpapers.Count == 0)
            {
                App.StartBackgroundScan(ViewModel.PathManagementVM.WorkshopPath, ViewModel.PathManagementVM.OfficialPath, ViewModel.PathManagementVM.ProjectPath, ViewModel.PathManagementVM.AcfPath, ViewModel.PathManagementVM.VdfPath, ViewModel.AppSettingsVM.ScanCacheEnabled == "1");
            }
            await App.ScanTask;
            _allWallpapers = [.. App.GlobalAllWallpapers];

            // 此段必须在 UI 线程执行(调用点已全部核实);清选中同步做,
            // 避免 TryEnqueue 异步回调在并发刷新时晚到、清掉新列表的选中状态
            Wallpapers.Clear();
            SelectedWallpapers.Clear();
            IsMultiSelectMode = false;
            ViewModel.SelectedWallpaper = null;

            await ApplyFilters(skipDebounce: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex,"筛选结果时出现异常。");
        }
        finally
        {
            _refreshInFlight = false;
            ShowScanProgress(false); // 扫描/刷新结束,隐藏转圈
        }
    }

    /// <summary>[扫描进度 2026-09] 列表区中央转圈显隐(可被非 UI 线程调用)</summary>
    private void ShowScanProgress(bool show)
    {
        if (ScanProgressRing == null) return;
        var action = () =>
        {
            ScanProgressRing.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            ScanProgressRing.IsActive = show;
        };
        if (DispatcherQueue.HasThreadAccess) action();
        else DispatcherQueue.EnqueueAsync(action);
    }

    private static bool IsListEqual(IReadOnlyList<WallpaperItem> current, IReadOnlyList<WallpaperItem> next)
    {
        if (current.Count != next.Count) return false;
        for (int i = 0; i < current.Count; i++)
        {
            if (current[i].FolderPath != next[i].FolderPath) return false;
        }
        return true;
    }
    private HashSet<string> GetSelectedTags()
    {
        var selectedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return _tagGetters
            .Where(kvp => kvp.Value(ViewModel))
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    private void WallpaperSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // UserInput:用户输入 → 实时筛选 + 更新建议;
        // SuggestionChosen:从建议列表选中 → 输入框文本被替换,同样要重新筛选
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput &&
            args.Reason != AutoSuggestionBoxTextChangeReason.SuggestionChosen)
        {
            return;
        }

        _searchText = sender.Text;
        _ = ApplyFilters();

        // 建议:标题/ID 匹配的壁纸,最多 8 条(仅用户输入时刷新建议,选中时保持)
        var query = sender.Text.Trim();
        if (string.IsNullOrWhiteSpace(query) || args.Reason == AutoSuggestionBoxTextChangeReason.SuggestionChosen)
        {
            if (string.IsNullOrWhiteSpace(query)) sender.ItemsSource = null;
            return;
        }
        // 建议:在当前筛选结果(类型/分级/来源/标签限定)内匹配标题/ID,最多 8 条
        // (基于 _filteredWallpapers 而非全量,保证建议不超出用户设定的筛选边界)
        var suggestions = _filteredWallpapers
            .Where(w => (w.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (w.WorkshopID?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(8)
            .Select(w => new SearchSuggestion
            {
                // 显示:标题 (ID) 便于区分;回填:完整标题,保证筛选 Contains 能命中
                Display = string.IsNullOrEmpty(w.WorkshopID)
                    ? w.Title ?? ""
                    : $"{w.Title}  ({w.WorkshopID})",
                Text = w.Title ?? "",
                Item = w
            })
            .ToList();

        sender.ItemsSource = suggestions;
    }

    private async Task ApplyFilters(bool skipDebounce = false)
    {
        // ApplyFilters 可能被后台线程触发(VM PropertyChanged 事件),但分页状态通知、
        // x:Bind 推送(IsEnabled 等)和列表重建都要求 UI 线程——非 UI 线程会抛
        // COMException 0x8000FFFF(实测:筛选结果时 CanGoPrevious 绑定更新崩溃)。
        // await 后续代码会回到捕获的 SynchronizationContext,这里统一切回 UI 线程。
        if (!DispatcherQueue.HasThreadAccess)
            await DispatcherQueue.EnqueueAsync(() => { });

        if (_filterCts != null)
        {
            _filterCts.Cancel();
            _filterCts.Dispose();
            _filterCts = null;
        }

        _filterCts = new CancellationTokenSource();
        var token = _filterCts.Token;

        try
        {
            // 防抖延迟:用户连续操作(打字搜索/切筛选)时合并请求;首载/刷新列表是单次全量操作,
            // 跳过防抖直接算——否则每次进页面白等 1 秒(FilterResultResponseDelay 默认 1000ms)
            if (!skipDebounce)
                await Task.Delay(ViewModel.WallpaperDisplayVM.FilterResultResponseDelay, token);

            var selectedTags = GetSelectedTags();
            int sortIndex = ViewModel.WallpaperDisplayVM.SortOrder;
            bool isAscending = ViewModel.WallpaperDisplayVM.IsSortAscending;

            var filteredResult = await Task.Run(() =>
            {
                var query = _allWallpapers.Where(w =>
                {
                    bool typeMatch = false;
                    string t = w.Type?.ToLower() ?? string.Empty;
                    if (ViewModel.FilterExpanderVM.Scene && t == "scene") typeMatch = true;
                    if (ViewModel.FilterExpanderVM.Video && t == "video") typeMatch = true;
                    if (ViewModel.FilterExpanderVM.Web && t == "web") typeMatch = true;
                    if (ViewModel.FilterExpanderVM.Application && t == "application") typeMatch = true;
                    if (ViewModel.FilterExpanderVM.Preset && t == "preset") typeMatch = true;
                    if (ViewModel.FilterExpanderVM.Unknown && t == "unknown") typeMatch = true;

                    bool ratingMatch = false;
                    string r = w.ContentRating?.ToLower() ?? string.Empty;
                    if (ViewModel.FilterExpanderVM.G && r == "everyone") ratingMatch = true;
                    if (ViewModel.FilterExpanderVM.Pg && r == "questionable") ratingMatch = true;
                    if (ViewModel.FilterExpanderVM.R && r == "mature") ratingMatch = true;

                    bool source = false;
                    string s = w.Source?.ToLower() ?? string.Empty;
                    if (ViewModel.FilterExpanderVM.Official && s == "official") source = true;
                    if (ViewModel.FilterExpanderVM.Workshop && s == "workshop") source = true;
                    if (ViewModel.FilterExpanderVM.Mine && s == "mine") source = true;

                    // 订阅状态:ShouldNotExist 已含"未订阅(取消/本地停用)+ 被下架(visibility=private)"两类异常;非工坊壁纸恒为 false,自然归入"正常"侧
                    bool subscriptionMatch = false;
                    if (ViewModel.FilterExpanderVM.Subscribed && !w.ShouldNotExist) subscriptionMatch = true;
                    if (ViewModel.FilterExpanderVM.Unsubscribed && w.ShouldNotExist) subscriptionMatch = true;

                    var rawTag = w.Tags ?? "";
                    var normalizedTag = rawTag.Replace(" ", "").Replace("-", "");
                    bool tagsMatch = selectedTags.Count > 0 && selectedTags.Contains(normalizedTag);

                    bool searchMatch = string.IsNullOrWhiteSpace(_searchText) ||
                                        (w.Title?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                        (w.WorkshopID?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ?? false);

                    return typeMatch && ratingMatch && tagsMatch && source && searchMatch && subscriptionMatch;
                });

                IOrderedEnumerable<WallpaperItem> sortedQuery;
                sortedQuery = sortIndex switch
                {
                    0 => isAscending ? query.OrderBy(w => w.Title) : query.OrderByDescending(w => w.Title),
                    1 => isAscending ? query.OrderBy(w => w.CreationTime) : query.OrderByDescending(w => w.CreationTime),
                    2 => isAscending ? query.OrderBy(w => w.UpdateTime) : query.OrderByDescending(w => w.UpdateTime),
                    3 => isAscending ? query.OrderBy(w => w.FileSize) : query.OrderByDescending(w => w.FileSize),
                    4 => isAscending ? query.OrderBy(w => w.AcfUpdateTime) : query.OrderByDescending(w => w.AcfUpdateTime),
                    _ => query.OrderByDescending(w => w.UpdateTime)
                };
                return sortedQuery.ToList();
            }, token);

            if (_allWallpapers.Count == 0)
            {
                _filteredWallpapers = filteredResult;
                NotifyPagerStateChanged();
                Wallpapers.Clear();
                DispatcherQueue.TryEnqueue(() =>
                {
                    ShowTip(NoScanResultTip, true);
                    ShowTip(NoResultTip, false);
                });
                return;
            }

            // === 分页 ===
            bool listUnchanged = IsListEqual(_filteredWallpapers, filteredResult);
            int pageBefore = CurrentPage; // 记录翻页判断基准
            _filteredWallpapers = filteredResult;

            // 筛选/排序变化后回到第一页
            if (!listUnchanged) CurrentPage = 1;
            // 每页数量变小等情况下钳制页码
            int totalPages = ComputeTotalPages(_filteredWallpapers.Count);
            if (CurrentPage > totalPages) CurrentPage = totalPages;
            NotifyPagerStateChanged();

            var pageItems = GetCurrentPageItems(_filteredWallpapers);
            if (listUnchanged && IsListEqual(Wallpapers, pageItems)) return;

            if (!token.IsCancellationRequested)
            {
                // 翻页(页码变化)或列表为空(首载)整页替换:Reset 无动画;同页筛选:增量 diff,动画只作用于真实变化的项
                // [性能 2026-09] 空集合首载必须走整页替换——ApplyListDiff 对 N 条是 O(N²)(内层线性查找 + 逐条 Move/Insert),
                // 万条分页关闭时白屏卡死数秒的元凶;diff 仅用于已填充列表的小增量刷新
                bool pageChanged = CurrentPage != pageBefore || Wallpapers.Count == 0;

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_allWallpapers.Count == 0)
                    {
                        ShowTip(NoScanResultTip, true);
                        ShowTip(NoResultTip, false);
                    }
                    else if (filteredResult.Count == 0)
                    {
                        ShowTip(NoScanResultTip, false);
                        ShowTip(NoResultTip, true);
                    }
                    else
                    {
                        ShowTip(NoScanResultTip, false);
                        ShowTip(NoResultTip, false);
                    }
                });

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested) return;
                    if (pageChanged)
                    {
                        Wallpapers.Clear();
                        foreach (var item in pageItems)
                        {
                            Wallpapers.Add(item);
                        }
                    }
                    else
                    {
                        ApplyListDiff(Wallpapers, pageItems);
                    }
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex,"筛选结果时出现异常。");
        }
    }

    /// <summary>增量同步列表:删除/插入/移动只作用于真实变化的项,触发 GridView 补位动画(翻页不走这里,用 Reset)</summary>
    private static void ApplyListDiff(ObservableCollection<WallpaperItem> target, IReadOnlyList<WallpaperItem> desired)
    {
        // 1) 删除:目标有、期望没有的项(移除后剩余项自动补位动画)
        var desiredSet = new HashSet<WallpaperItem>(desired);
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

    /// <summary>淡入淡出切换空状态提示(120ms,匹配列表动画节奏)</summary>
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

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        GoToPage(CurrentPage - 1);
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        GoToPage(CurrentPage + 1);
    }

    /// <summary>跳转到指定页并重填列表（分页模式）</summary>
    private void GoToPage(int page)
    {
        int totalPages = ComputeTotalPages(_filteredWallpapers.Count);
        page = Math.Clamp(page, 1, totalPages);
        if (page == CurrentPage) return;

        CurrentPage = page;
        var pageItems = GetCurrentPageItems(_filteredWallpapers);
        Wallpapers.Clear();
        foreach (var item in pageItems)
        {
            Wallpapers.Add(item);
        }
        ScrollVisibleGridToTop();
    }

    // ============= ItemsRepeater 列宽钳制(全迁;GridView 全部移除) =============

    // ============ [实验] ItemsRepeater UniformGridLayout 防崩钳制 ============

    /// <summary>ScrollView 尺寸变化:钳制 UniformGridLayout.MinItemWidth ≤ 可用宽,
    /// 否则可用宽 &lt; MinItemWidth 时 itemsPerLine=0 除零崩溃(WinUI #10539,微软未修)。
    /// ItemsRepeater 图标模式专用(实验)。</summary>
    private void WallpapersScrollViewExp_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateExpUniformLayoutMinWidth();

    /// <summary>读用户档位(180/240/300),钳到可用宽内写回 UniformGridLayout.MinItemWidth。
    /// 可用宽 = ScrollView 内容宽 - 左右 Margin(4+4);ScrollView 未布局时兜底只钳下限。</summary>
    private void UpdateExpUniformLayoutMinWidth()
    {
        if (WallpapersUniformLayoutExp is not UniformGridLayout layout) return;
        double viewport = WallpapersScrollViewExp.ActualWidth;
        int desired = ViewModel.WallpaperDisplayVM.WallpaperListMinWidth;
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

    /// <summary>[性能 2026-09] 给三套模式列表设置预渲染缓冲(一次性)。
    /// v0.8.0 迁移 ItemsRepeater 时丢掉了迁移前 GridView 的 CacheLength 设置,一直走系统默认(约 4 屏),
    /// 每次实化/回收的容器数翻数倍;窗口化(列少 → 内容高约 40 屏)时这笔固定开销被放大成可见掉帧。
    /// 三个 repeater 均为纵向滚动,故设 VerticalCacheLength。</summary>
    private void ApplyRepeaterCacheLength()
    {
        if (_repeaterCacheApplied) return;
        // 控件未挂载时先不设(Loaded 内调用,正常都已就绪);未设成则下次 Loaded 再试
        if (WallpapersRepeater == null || WallpapersContentRepeater == null || WallpapersListRepeater == null)
            return;
        WallpapersRepeater.VerticalCacheLength = RepeaterCacheLength;        // 图标模式(UniformGridLayout)
        WallpapersContentRepeater.VerticalCacheLength = RepeaterCacheLength; // 内容模式(StackLayout 单列)
        WallpapersListRepeater.VerticalCacheLength = RepeaterCacheLength;    // 列表模式(UniformGridLayout)
        _repeaterCacheApplied = true;
    }

    // ===== [全迁 ItemsRepeater] 列表模式 UniformGridLayout 钳制(400 档位) =====

    /// <summary>列表模式 ScrollView 尺寸变化 → 钳制 MinItemWidth(防 itemsPerLine=0 除零崩溃)</summary>
    private void WallpapersListScrollViewExp_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateWallpapersListLayoutMinWidth();

    /// <summary>内容模式 ScrollView 尺寸变化(单列 StackLayout 无 MinItemWidth,无需钳制;占位)</summary>
    private void WallpapersContentScrollViewExp_SizeChanged(object sender, SizeChangedEventArgs e)
    {
    }

    /// <summary>列表模式 UniformGridLayout 钳制:MinItemWidth = Min(400 档位, 可用宽-8)。
    /// 可用宽 = ScrollView 内容宽 - 左右 Margin(4+4);未布局首帧给档位值。</summary>
    private void UpdateWallpapersListLayoutMinWidth()
    {
        if (WallpapersListUniformLayoutExp is not UniformGridLayout layout) return;
        double viewport = WallpapersListScrollViewExp.ActualWidth;
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

    // ===== [Ctrl+滚轮 2026-09] =====
    // ScrollView(新控件)原生把 Ctrl+滚轮当"缩放"消费(ZoomMode=Disabled 也吞事件,官方设计:
    // "pressing Ctrl while scrolling mouse wheel" = zoom),导致 Ctrl+滚轮不滚动。
    // 方案:拦截点放内层 ItemsRepeater(滚轮冒泡先经它,后到 ScrollView 原生处理):
    //   Ctrl+滚轮(未按左键)      = 循环切换图标尺寸档位 + Handled(不缩放下传)
    //   Ctrl+左键按住+滚轮         = 手动滚动(此时左键拖动语义被滚轮替代)
    //   无修饰键滚轮              = 不 Handled,放行给 ScrollView 原生平滑滚动(手感完整保留)
    private void WallpapersRepeater_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(sender as UIElement).Properties.MouseWheelDelta;
        if (delta == 0) return;

        bool ctrlHeld = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control); // Pointer 事件用 KeyModifiers
        bool leftPressed = _isLeftMouseButtonPressed;

        // Ctrl 且未按左键:切换图标档位
        if (ctrlHeld && !leftPressed)
        {
            var vm = ViewModel.WallpaperDisplayVM;
            int cur = vm.WallpaperViewIndex;
            int next = delta > 0 ? (cur + 1) % 3 : (cur + 2) % 3; // 上滚升档,下滚降档,循环 0..2
            vm.WallpaperViewIndex = next;
            e.Handled = true;
            return;
        }

        // Ctrl+左键按住:不 Handled,放行给 ScrollView 原生滚轮——验证 ZoomMode=Disabled 下
        // 官方是否本就会滚动(若会,则拿到官方手感;若仍吞,再考虑子类化/手动)
    }

    // ============ 预览内容过滤(高斯模糊) ============

    /// <summary>该壁纸在当前预览模糊开关下是否需要模糊:勾选哪个年龄段,该年龄段分级的壁纸就模糊</summary>
    private bool ShouldBlurPreview(WallpaperItem item)
        => BlurPreviewService.ShouldBlur(item.ContentRating,
            ViewModel.WallpaperDisplayVM.BlurEveryone,
            ViewModel.WallpaperDisplayVM.BlurTeen,
            ViewModel.WallpaperDisplayVM.BlurAdult);

    /// <summary>按当前年龄段设置切换单个卡片的模糊叠加层</summary>
    private void UpdateItemBlur(Grid itemRootGrid, WallpaperItem item)
    {
        if (itemRootGrid.FindName("ItemBlurOverlay") is not Image blurOverlay) return;
        if (ShouldBlurPreview(item))
        {
            // 先隐藏原图组件(静态图 Image / 动态图 SkiaGifView):模糊位图为异步生成,期间不露原图、避免"先原图后模糊"两段闪现
            HideCardRawPreview(itemRootGrid);
            _ = ShowBlurOverlayAsync(blurOverlay, item, () => UpdateSkiaGif(itemRootGrid, item));
        }
        else
        {
            blurOverlay.Visibility = Visibility.Collapsed;
            blurOverlay.Source = null;
            _blurOverlayOwner.Remove(blurOverlay); // 同步清归属,防过期 await 误上屏
            UpdateSkiaGif(itemRootGrid, item); // 恢复原图显示(按类型:GIF → Skia 播放,其余 → 静态图)
        }
    }

    /// <summary>隐藏卡片原图组件(静态图+动态图),画面由模糊层接管</summary>
    private static void HideCardRawPreview(Grid root)
    {
        if (root.FindName("ItemPreviewImage") is Image img) img.Visibility = Visibility.Collapsed;
        if (root.FindName("SkiaGifCanvas") is SkiaGifView skia)
        {
            skia.Stop();
            skia.Visibility = Visibility.Collapsed;
        }
    }

    // [修复 2026-09] 模糊层宿主(Image)当前归属的壁纸预览路径:防异步竞态——旧壁纸的模糊位图
    // await 完成后覆盖到已被回收复用成新壁纸的同一 Image 上(在错误的项上加模糊的直接来源)。
    private static readonly Dictionary<Image, string> _blurOverlayOwner = [];

    private async Task ShowBlurOverlayAsync(Image blurOverlay, WallpaperItem item, Action restoreRawPreviews)
    {
        try
        {
            if (string.IsNullOrEmpty(item.Preview) || !File.Exists(item.Preview))
            {
                restoreRawPreviews();
                return;
            }
            // 记录本次归属:仅当此 Image 仍归属此预览路径时,模糊结果才允许上屏
            _blurOverlayOwner[blurOverlay] = item.Preview;
            var blurred = await BlurPreviewService.GetBlurredPreviewAsync(item.Preview);
            if (blurred == null)
            {
                restoreRawPreviews();
                return;
            }
            // 竞态防护:await 期间勾选状态可能已变(取消勾选)或容器已换绑到别的壁纸,复查后再上屏
            if (!ShouldBlurPreview(item))
            {
                restoreRawPreviews(); // 模糊层不上屏时恢复原图,避免卡片空白
                return;
            }
            // [修复 2026-09] 归属校验:await 期间容器可能已被回收复用成别的壁纸(ElementClearing/ElementPrepared
            // 会清空/重设 Source)。此时此 Image 的归属已不是发起时的 item,丢弃过期结果,不覆盖新壁纸。
            if (!_blurOverlayOwner.TryGetValue(blurOverlay, out var owner) || owner != item.Preview)
            {
                return; // 过期结果:卡片已换绑,静默丢弃(新归属的模糊流程会自行上屏)
            }
            blurOverlay.Source = blurred;
            blurOverlay.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            restoreRawPreviews();
            Log.Warning(ex, "创建预览模糊层失败: {Path}", item.Preview);
        }
    }

    /// <summary>年龄段设置变化:遍历当前页所有可见卡片刷新模糊层</summary>
    private void RefreshAllItemBlurs()
    {
        // [修复 2026-09] 图标 GridView → ItemsRepeater 后无 ContainerFromItem,改可视树遍历:
        // 从三个模式滚动容器深搜找模板根 Grid(ItemRootGrid/ContentItemContainer/ListItemContainer),
        // 对已实化的卡片按当前开关刷新模糊层(勾选开关立即生效,不再只对新实化卡片生效)
        RefreshRepeaterBlur(WallpapersScrollViewExp, "ItemRootGrid");
        RefreshRepeaterBlur(WallpapersContentScrollViewExp, "ContentItemContainer");
        RefreshRepeaterBlur(WallpapersListScrollViewExp, "ListItemContainer");

        // 多选堆叠视图同步:逐张重算模糊层
        for (int i = 0; i < DisplayedSelectedWallpapers.Count; i++)
        {
            if (StackedImagesControl.ContainerFromIndex(i) is FrameworkElement stackContainer
                && DisplayedSelectedWallpapers[i] is WallpaperItem stackItem)
            {
                UpdateStackItemBlur(stackContainer, stackItem);
            }
        }
    }

    /// <summary>[2026-09] 可视树遍历某滚动容器内所有已实化的卡片根 Grid,刷新模糊层。</summary>
    private void RefreshRepeaterBlur(DependencyObject? root, string rootName)
    {
        if (root == null) return;
        foreach (var cardRoot in FindDescendantGrids(root, rootName))
        {
            if (cardRoot.DataContext is WallpaperItem item)
                UpdateItemBlur(cardRoot, item);
        }
    }

    /// <summary>[2026-09] 深搜全部指定名 Grid(ItemsRepeater 实化多卡,FindDescendantGrid 只返回首个不够)。</summary>
    private static IEnumerable<Grid> FindDescendantGrids(DependencyObject root, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Grid g && g.Name == name)
                yield return g;
            foreach (var found in FindDescendantGrids(child, name))
                yield return found;
        }
    }

    /// <summary>详情面板大图的模糊层:与查看菜单"预览模糊"选项同步</summary>
    private void UpdateDetailBlur()
    {
        if (SinglePreviewBlurOverlay is not Image blurOverlay) return;
        if (ViewModel.SelectedWallpaper is WallpaperItem item && ShouldBlurPreview(item))
        {
            // 同卡片:先隐藏详情大图原图,避免模糊位图异步生成期间"先原图后模糊"
            SinglePreviewImage.Visibility = Visibility.Collapsed;
            _ = ShowBlurOverlayAsync(blurOverlay, item, () => SinglePreviewImage.Visibility = Visibility.Visible);
        }
        else
        {
            blurOverlay.Visibility = Visibility.Collapsed;
            blurOverlay.Source = null;
            _blurOverlayOwner.Remove(blurOverlay);
            SinglePreviewImage.Visibility = Visibility.Visible;
        }
    }

    /// <summary>堆叠卡片模糊层:按各模式档位,应模糊的壁纸隐藏背景图、显示高斯模糊位图(与列表卡片同源缓存)</summary>
    private void UpdateStackItemBlur(FrameworkElement cardRoot, WallpaperItem item)
    {
        // 调用方传入的就是模板根 Border(StackedImage_Loaded 的 sender / ContainerFromIndex 的容器内容)
        var cardBorder = cardRoot as Border;
        if (cardBorder == null) return;
        if (FindInCardNamescope(cardBorder, "StackBlurOverlay") is not Image blurOverlay) return;

        // 背景 ImageBrush 挂在卡片 Border 自身
        if (ShouldBlurPreview(item))
        {
            // 先隐藏原图(ImageBrush 置空),避免模糊位图异步生成期间"先原图后模糊"
            cardBorder.Background = null;
            _ = ShowBlurOverlayAsync(blurOverlay, item, () => { });
        }
        else
        {
            blurOverlay.Visibility = Visibility.Collapsed;
            blurOverlay.Source = null;
            _blurOverlayOwner.Remove(blurOverlay);
            RestoreStackBackground(cardBorder, item);
        }
    }

    /// <summary>恢复堆叠卡片的原图背景(ImageBrush 被模糊流程置空后回填)</summary>
    private static void RestoreStackBackground(Border cardBorder, WallpaperItem item)
    {
        if (cardBorder.Background is null && !string.IsNullOrEmpty(item.Preview))
        {
            cardBorder.Background = new ImageBrush
            {
                ImageSource = new BitmapImage(new Uri(item.Preview)),
                Stretch = Stretch.UniformToFill,
            };
        }
    }

    /// <summary>视觉树查找指定名元素(DataTemplate namescope 通用兜底:从模板根 Border 递归子树)</summary>
    private static FrameworkElement? FindInCardNamescope(DependencyObject root, string name)
    {
        if (root is FrameworkElement fe && fe.Name == name) return fe;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var hit = FindInCardNamescope(VisualTreeHelper.GetChild(root, i), name);
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>视觉树深搜指定名字的 Grid(从容器进模板根,规避模板命名作用域)</summary>
    private static Grid? FindDescendantGrid(DependencyObject root, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Grid g && g.Name == name) return g;
            var found = FindDescendantGrid(child, name);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>当前可见的壁纸滚动容器(滚动回顶用;三个模式都已迁 ScrollView)</summary>
    private ScrollView? GetVisibleWallpaperScrollView()
    {
        if (WallpapersScrollViewExp.Visibility == Visibility.Visible) return WallpapersScrollViewExp;
        if (WallpapersContentScrollViewExp.Visibility == Visibility.Visible) return WallpapersContentScrollViewExp;
        if (WallpapersListScrollViewExp.Visibility == Visibility.Visible) return WallpapersListScrollViewExp;
        return null;
    }

    /// <summary>可见模式滚动回顶(分页/刷新后)</summary>
    private void ScrollVisibleGridToTop()
    {
        GetVisibleWallpaperScrollView()?.ScrollTo(0, 0);
    }

    /// <summary>取当前页应显示的壁纸；分页关闭时返回完整列表</summary>
    private List<WallpaperItem> GetCurrentPageItems(List<WallpaperItem> source)
    {
        if (!ViewModel.WallpaperDisplayVM.PaginationEnabled) return source;
        int size = ViewModel.WallpaperDisplayVM.PageSize;
        if (size <= 0) size = 30;
        int skip = (CurrentPage - 1) * size;
        return source.Skip(skip).Take(size).ToList();
    }

    private void GoToSettings_Click(object sender, RoutedEventArgs e)
    {
        Frame?.Navigate(typeof(Settings));
    }

    /// <summary>长 description 折叠行数(超出显示"展开全文"按钮)</summary>
    private const int DescriptionCollapsedLines = 5;

    /// <summary>上次处理的 description 文本(用于区分"内容切换"与"仅尺寸变化")</summary>
    private string? _lastDescriptionText;

    /// <summary>
    /// 长 description 折叠:文本变化时复位为折叠态(切换壁纸后新描述从收起开始);
    /// 折叠态下实际高度达到 5 行高即视为超长,显示"展开全文"按钮;展开态(不限行)不判断。
    /// </summary>
    private void DescriptionText_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not TextBlock tb) return;

        // 内容换了 → 复位折叠(窗口宽度变化等场景 Text 不变,保持当前展开/收起状态)
        if (tb.Text != _lastDescriptionText)
        {
            _lastDescriptionText = tb.Text;
            tb.MaxLines = DescriptionCollapsedLines;
        }

        if (tb.MaxLines <= 0) return; // 展开态:按钮保持"收起"

        double lineHeight = tb.LineHeight > 0 ? tb.LineHeight : tb.FontSize * 1.333;
        bool overflow = tb.ActualHeight >= lineHeight * DescriptionCollapsedLines - 0.5;
        if (overflow)
        {
            ExpandDescriptionButton.Visibility = Visibility.Visible;
            ExpandDescriptionButton.Content = LanguageHelper.GetResource("RightPanel_ExpandDescription.Text");
        }
        else
        {
            ExpandDescriptionButton.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>展开/收起长 description(MaxLines 5 ↔ 不限)</summary>
    private void ExpandDescriptionButton_Click(object sender, RoutedEventArgs e)
    {
        bool expand = DescriptionText.MaxLines > 0; // 当前折叠 → 展开
        DescriptionText.MaxLines = expand ? 0 : DescriptionCollapsedLines;
        ExpandDescriptionButton.Content = LanguageHelper.GetResource(
            expand ? "RightPanel_CollapseDescription.Text" : "RightPanel_ExpandDescription.Text");
    }

    private void SortDirectionToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.WallpaperDisplayVM.IsSortAscending = !ViewModel.WallpaperDisplayVM.IsSortAscending;
    }

    private void ShadowRect_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement casterElement)
        {
            // 内容过滤:按年龄段为露骨壁纸创建/移除预览模糊层(容器重挂载时同步)
            if (casterElement is Grid itemRootGrid && itemRootGrid.DataContext is WallpaperItem blurItem)
            {
                UpdateItemBlur(itemRootGrid, blurItem);
            }

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
            if (casterElement is Grid grid && grid.DataContext is WallpaperItem item)
            {
                UpdateItemCheckBoxOpacity(grid, item);
            }
        }
    }

    private async void ResetFilter_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.ResetFiltersAsync(1,true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "重置筛选失败");
        }
    }
    private async void SelectAllTags_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.ResetFiltersAsync(2, true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "重置筛选失败");
        }
    } 
    private async void DeselectAllTags_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.ResetFiltersAsync(2, false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "重置筛选失败");
        }
    }

    private Expander? _currentFilterExpander;

    private void FilterExpanderContextMenu_Opening(object sender, object e)
    {
        if (sender is MenuFlyout flyout)
        {
            _currentFilterExpander = flyout.Target as Expander;
            // Popup 不自动继承运行时主题变更，需要在打开时显式应用当前主题
            flyout.Opened -= OnContextFlyoutThemeRefresh;
            flyout.Opened += OnContextFlyoutThemeRefresh;
        }
    }

    private void OnContextFlyoutThemeRefresh(object? sender, object e)
    {
        if (sender is MenuFlyout flyout)
        {
            flyout.Opened -= OnContextFlyoutThemeRefresh;
            var theme = App.MainWindowInstance?.Content is FrameworkElement root
                ? root.ActualTheme
                : ElementTheme.Default;
            foreach (var item in flyout.Items)
            {
                if (item is MenuFlyoutItem menuItem)
                    menuItem.RequestedTheme = theme;
            }
        }
    }

    // 弹层(菜单/Flyout)不自动继承主窗口运行时主题,打开时显式应用(公共逻辑见 App.ApplyFlyoutTheme)
    private void FlyoutThemeRefresh_Opened(object sender, object e) => App.ApplyFlyoutTheme(sender, e);

    private void FilterExpanderSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFilterExpander == null) return;
        ViewModel._isBatchUpdating = true;
        ExpandCheckBoxes(_currentFilterExpander, true);
        ViewModel._isBatchUpdating = false;
        _ = ApplyFilters();
    }

    private void FilterExpanderInvert_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFilterExpander == null) return;
        ViewModel._isBatchUpdating = true;
        ExpandCheckBoxes(_currentFilterExpander, null);
        ViewModel._isBatchUpdating = false;
        _ = ApplyFilters();
    }

    private static void ExpandCheckBoxes(Expander expander, bool? isChecked)
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
    
    private void UpdateItemCheckBoxOpacity(Grid grid, WallpaperItem item)
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

    private void WallpaperList_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_isWallpaperItemTapped == true)
        {
            _isWallpaperItemTapped = false;
            return;
        }
        // [Shift 区间刷选] 区间刷选结束的释放可能触发 Tapped,抑制清空(选择已由区间定)
        if (_suppressItemReleased) return;

        ViewModel.SelectedWallpaper = null;
    }
    private void SelectionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is WallpaperItem item)
        {
            if (cb.IsChecked == true && !SelectedWallpapers.Contains(item))
            {
                SelectedWallpapers.Add(item);
                // 勾选成功(Count>0)才进入多选;取消到 0 项时由 UpdateMultiSelectCount 自动退出,
                // 不再无条件重进——避免快速连点时"自动退出"与"强制进入"互搏导致模式横跳
                if (!IsMultiSelectMode)
                {
                    IsMultiSelectMode = true;
                }
            }
            else if (cb.IsChecked == false)
            {
                SelectedWallpapers.Remove(item);
            }
            UpdateMultiSelectCount();
        }
    }
    private void ContentItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            if (grid.DataContext is WallpaperItem hovered) hovered.IsHovered = true; // 数据层标记悬停
            var checkBox = FindCheckBoxInGrid(grid);
            if (checkBox != null) checkBox.Opacity = 1;

            Visual visual = ElementCompositionPreview.GetElementVisual(grid);
            visual.CenterPoint = new Vector3((float)grid.ActualWidth / 2, (float)grid.ActualHeight / 2, 0f);

            if (_isLeftMouseButtonPressed && grid.DataContext is WallpaperItem item)
            {
                ContentItem_PointerPressed(sender, e);
                if (!_isMultiSelectMode)
                {
                    // 拖拽滑过时更新预览图和标题，但不播放钻入动画避免卡顿
                    ViewModel.SelectedWallpaper = item;
                }
                if (IsMultiSelectMode)
                {
                    // 多选下按住左键刷选 = 取反经过的壁纸(划过选中的取消,划过未选中的选中)
                    item.IsSelected = !item.IsSelected;
                    if (item.IsSelected && !SelectedWallpapers.Contains(item))
                        SelectedWallpapers.Add(item);
                    else if (!item.IsSelected)
                        SelectedWallpapers.Remove(item);
                    UpdateMultiSelectCount();
                }
                return;
            }
        }
    }
    private void ContentItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid && grid.DataContext is WallpaperItem item)
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
            _isWallpaperItemTapped = true;

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
                if (sender is FrameworkElement element && element.DataContext is WallpaperItem item)
                {
                    var modifiers = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control); // Pointer 事件用 KeyModifiers,GetKeyStateForCurrentThread 会读到过期状态
                    if (modifiers && !_isMultiSelectMode)
                    {
                        // CTRL+按下:先选中再加入集合,最后进多选——若先进多选,setter 里 UpdateAllVisibleCheckBoxes
                        // 等同步调用会以 Count=0 触发 UpdateMultiSelectCount 立即退出多选
                        item.IsSelected = true;
                        if (!SelectedWallpapers.Contains(item))
                            SelectedWallpapers.Add(item);
                        UpdateMultiSelectCount();
                        IsMultiSelectMode = true;
                        return;
                    }

                    if (_isMultiSelectMode)
                    {
                        // 点击目标是 CheckBox 时,勾选已由 SelectionCheckBox_Click 全权处理,
                        // 这里不再翻转,避免一次点击被两条路径重复处理(快速点击计数归零的根因)
                        if (IsEventSourceInCheckBox(e.OriginalSource)) return;

                        item.IsSelected = !item.IsSelected;
                        if (item.IsSelected && !SelectedWallpapers.Contains(item))
                            SelectedWallpapers.Add(item);
                        else if (!item.IsSelected)
                            SelectedWallpapers.Remove(item);
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
            if (sender is FrameworkElement element && element.DataContext is WallpaperItem item)
            {
                if (!_isMultiSelectMode)
                {
                    if (ViewModel.SelectedWallpaper != item)
                    {
                        ViewModel.SelectedWallpaper = item;
                        PlayDrillInAnimation();
                    }
                }
                e.Handled = true;
            }
        }
    }
    private void ContentItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is WallpaperItem item)
        {
            _rightClickedWallpaperElement = element;
            if (!_isMultiSelectMode)
            {
                ViewModel.SelectedWallpaper = item;
            }
        }
    }
    private void Item_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            // 数据层标记悬停:checkbox 绑定 CheckBoxOpacity 自动保持显示(避开 UI 实例/虚拟化问题)
            if (grid.DataContext is WallpaperItem hovered) hovered.IsHovered = true;
            var checkBox = FindCheckBoxInGrid(grid);
            if (checkBox != null) checkBox.Opacity = 1;

            // 按下拖动经过:左键按住 + 移动经过本卡片(仅选择逻辑,无视觉置顶/放大)
            if (_isLeftMouseButtonPressed && grid.DataContext is WallpaperItem item)
            {
                // [Shift 区间刷选 2026-09] Shift+拖动:从锚点向当前卡片延伸连续区间(替换选择)
                if (_shiftDragActive)
                {
                    ExtendShiftRange(item);
                    return;
                }
                // [Ctrl 刷选 2026-09] Ctrl+拖动:划过 = 直接设为选中(加选,不取反)——同文件管理器 Ctrl 语义
                var ctrlHeld = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control); // Pointer 事件用 KeyModifiers
                if (ctrlHeld)
                {
                    if (!item.IsSelected)
                    {
                        item.IsSelected = true;
                        if (!SelectedWallpapers.Contains(item))
                            SelectedWallpapers.Add(item);
                    }
                    UpdateMultiSelectCount();
                    return;
                }
                // 普通拖动:多选刷选(取反)或单选滑动预览
                Item_PointerPressed(sender, e);
                if (!_isMultiSelectMode)
                {
                    // 拖拽滑过时更新预览图和标题，但不播放钻入动画避免卡顿
                    ViewModel.SelectedWallpaper = item;
                }
                if (IsMultiSelectMode)
                {
                    // 多选下按住左键刷选 = 取反经过的壁纸(划过选中的取消,划过未选中的选中)
                    item.IsSelected = !item.IsSelected;
                    if (item.IsSelected && !SelectedWallpapers.Contains(item))
                        SelectedWallpapers.Add(item);
                    else if (!item.IsSelected)
                        SelectedWallpapers.Remove(item);
                    UpdateMultiSelectCount();
                }
                return;
            }

            // [悬停视觉 2026-09] 置顶+放大只在"鼠标在卡片上动画"设置开启时执行;
            // 关闭时悬停零视觉变化(阴影/绘制序/放大全不动),只有 checkbox 随 IsHovered 显示
            if (ViewModel.WallpaperDisplayVM.IsWallpaperEnterAnimationEnabled)
            {
                Visual visual = ElementCompositionPreview.GetElementVisual(grid);
                Compositor compositor = visual.Compositor;

                visual.CenterPoint = new System.Numerics.Vector3(
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
                // [悬停阴影 2026-09] 不添加悬停阴影层:ElementPrepared 常驻阴影一层,
                // 悬停只做 Scale 放大(阴影随卡片放大自然增强),避免两层阴影叠加

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
        if (sender is Grid grid && grid.DataContext is WallpaperItem item)
        {
            item.IsHovered = false; // 鼠标离开,清除悬停标记(数据层)
            UpdateItemCheckBoxOpacity(grid, item);

            ApplyScaleAnimation(grid, 1.0f);
            UpdateItemCheckBoxOpacity(grid, item);

            // [阴影常驻] 不再移除阴影(ElementPrepared 常驻创建;鼠标经过浮起效果保留阴影观感)

            Visual visual = ElementCompositionPreview.GetElementVisual(grid);
            Compositor compositor = visual.Compositor;

            var scaleAnimation = compositor.CreateSpringVector3Animation();
            scaleAnimation.Target = "Scale";
            scaleAnimation.FinalValue = new Vector3(1.0f, 1.0f, 1.0f);
            scaleAnimation.DampingRatio = 0.6f;
            scaleAnimation.Period = TimeSpan.FromMilliseconds(50);

            var capturedParent = VisualTreeHelper.GetParent(grid) as UIElement;

            // 捕获容器用于复位置顶([ItemsRepeater 适配] 无 GridViewItem,向上到 ItemsRepeater 止,最多 6 层)
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
                grid.Translation = new System.Numerics.Vector3(0f, 0f, 64f);
            });

        }
    }
    private void Item_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {

            Visual visual = ElementCompositionPreview.GetElementVisual(grid);
            Compositor compositor = visual.Compositor;

            // 恢复到正常大小或悬停大小
            var scaleAnimation = compositor.CreateSpringVector3Animation();
            scaleAnimation.Target = "Scale";

            if (!ViewModel.WallpaperDisplayVM.IsWallpaperEnterAnimationEnabled)
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
            if (sender is FrameworkElement element && element.DataContext is WallpaperItem item)
            {
                // [Shift 区间刷选] 区间刷选结束的释放抑制单选(区间结果已由 SelectShiftRange 定)
                if (!_isMultiSelectMode && !_suppressItemReleased)
                {
                    if (ViewModel.SelectedWallpaper != item)
                    {
                        ViewModel.SelectedWallpaper = item;
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
            _isWallpaperItemTapped = true;

            // [列表键盘可达 2026-09] 点谁就把键盘焦点交给谁:此后的方向键/Shift+Tab 都从这张卡起算,
            // 而不是从上次停过的工具栏继续往下走。传 Pointer(不是 Programmatic)以免鼠标点击后冒出键盘焦点框。
            // 左键按住划过(拖拽刷选/区间延伸)时不重复挪焦点:一条手势只认最开始按下那张卡。
            if (ListKeyboardAccessProbe && !_isLeftMouseButtonPressed && !_shiftDragActive
                && sender is FrameworkElement pressedEl && pressedEl.DataContext is WallpaperItem pressedItem)
                FocusWallpaperCard(pressedItem, FocusState.Pointer);

            // [Shift 区间刷选 2026-09] Shift+按下 = 开始区间刷选:记锚点,等待拖动延伸
            if (sender is FrameworkElement shiftElement && shiftElement.DataContext is WallpaperItem shiftItem)
            {
                var shiftHeld = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift); // Pointer 事件用 KeyModifiers
                if (shiftHeld)
                {
                    _shiftAnchorItem = shiftItem;
                    _shiftDragActive = true;
                    _suppressItemReleased = true; // 区间刷选期间抑制释放单选
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
                if (sender is FrameworkElement element && element.DataContext is WallpaperItem item)
                {
                    var modifiers = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control); // Pointer 事件用 KeyModifiers,GetKeyStateForCurrentThread 会读到过期状态
                    if (modifiers && !_isMultiSelectMode)
                    {
                        // CTRL+按下:先选中再加入集合,最后进多选——若先进多选,setter 里 UpdateAllVisibleCheckBoxes
                        // 等同步调用会以 Count=0 触发 UpdateMultiSelectCount 立即退出多选
                        item.IsSelected = true;
                        if (!SelectedWallpapers.Contains(item))
                            SelectedWallpapers.Add(item);
                        UpdateMultiSelectCount();
                        IsMultiSelectMode = true;
                        return;
                    }

                    if (_isMultiSelectMode)
                    {
                        // 点击目标是 CheckBox 时,勾选已由 SelectionCheckBox_Click 全权处理(同 ContentItem_PointerPressed)
                        if (IsEventSourceInCheckBox(e.OriginalSource)) return;

                        item.IsSelected = !item.IsSelected;

                        if (item.IsSelected && !SelectedWallpapers.Contains(item))
                            SelectedWallpapers.Add(item);
                        else if (!item.IsSelected)
                            SelectedWallpapers.Remove(item);

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

    /// <summary>[右键菜单 2026-09] ContextRequested 手动弹菜单——替代 ContextFlyout 系统弹出。
    /// 系统 ContextFlyout/RightTapped 在右键按下与松开间鼠标移动(哪怕 1px)时会被 Windows 输入层
    /// 抑制(判定为潜在拖拽);ContextRequested 是"请求上下文"底层事件,不受该移动检测限制。</summary>
    // [右键释放检测 2026-09] 松开点命中图标卡片 → 选中 + 手动弹菜单。
    // 只处理图标模式(ItemsRepeater);内容/列表模式仍走各自 ContextFlyout。
    private void HandleRightReleaseOpenMenu(Point releasePagePoint)
    {
        if (_rightMenuShownThisGesture) return; // 已弹过(如 ContextFlyout 兜底触发),防双弹

        // 仅图标模式可见时命中图标卡片
        if (WallpapersScrollViewExp.Visibility != Visibility.Visible
            || WallpapersScrollViewExp.ActualWidth <= 0) return;

        FrameworkElement? card = FindWallpaperCardAt(releasePagePoint);
        // 只认松开点命中的卡片:拖出卡片/列表外松开不弹(符合常理)
        if (card == null) return;
        if (card is not FrameworkElement fe || fe.DataContext is not WallpaperItem item) return;

        // 与右键菜单语义一致:选中逻辑(多选模式不切单选指针)
        if (!_isMultiSelectMode)
        {
            if (ViewModel.SelectedWallpaper != item)
            {
                ViewModel.SelectedWallpaper = item;
                PlayDrillInAnimation();
            }
        }
        RefreshDisplayedSelectedWallpapers(forceRebuild: true);
        UpdateMultiSelectCount();
        if (!_isMultiSelectMode)
            ViewModel.SelectedWallpaper = item;
        _rightClickedWallpaperElement = fe;

        _rightMenuShownThisGesture = true;
        // 在松开位置弹菜单(相对卡片定位更稳:用卡片坐标换算)
        var posInCard = fe.TransformToVisual(null).TransformPoint(new Point(0, 0));
        var menuPos = new Point(releasePagePoint.X - posInCard.X, releasePagePoint.Y - posInCard.Y);
        WallpaperContextMenu.ShowAt(fe, new FlyoutShowOptions
        {
            Position = menuPos,
            ShowMode = FlyoutShowMode.Standard
        });
    }

    /// <summary>命中测试:页面坐标处命中的元素里,向上找 DataContext 是 WallpaperItem 的卡片根。
    /// 用 FindElementsInHostCoordinates 取该点所有命中元素(含被覆盖的),逐个查祖先链。</summary>
    private FrameworkElement? FindWallpaperCardAt(Point pagePoint)
    {
        var hits = VisualTreeHelper.FindElementsInHostCoordinates(pagePoint, WallpapersScrollViewExp);
        foreach (var hit in hits)
        {
            DependencyObject cur = hit;
            int hops = 0;
            while (cur != null && hops < 10)
            {
                if (cur is FrameworkElement fel && fel.DataContext is WallpaperItem)
                    return fel;
                cur = VisualTreeHelper.GetParent(cur);
                hops++;
            }
        }
        return null;
    }
    private void WallpaperItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is WallpaperItem item)
        {
            if (!_isMultiSelectMode)
            {
                if (ViewModel.SelectedWallpaper != item)
                {
                    ViewModel.SelectedWallpaper = item;
                    PlayDrillInAnimation();
                }
            }

            RefreshDisplayedSelectedWallpapers(forceRebuild: true);
            UpdateMultiSelectCount();
            if (!_isMultiSelectMode)
                ViewModel.SelectedWallpaper = item;
            _rightClickedWallpaperElement = element;
        }
    }
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

        // 获取 Visual 层进行高性能动画
        Visual imageVisual = ElementCompositionPreview.GetElementVisual(SinglePreviewImage);
        Compositor compositor = imageVisual.Compositor;

        // 复位透明度:上一轮动画若被中断(快速切换/状态变更),Opacity 可能残留为 0,
        // 导致 preview 全透明但文字正常(用户报告的"图不显示文字正常")
        imageVisual.StopAnimation("Opacity");
        imageVisual.Opacity = 1f;

        // 设置中心点 (280 / 2 = 140)
        imageVisual.CenterPoint = new Vector3(140f, 140f, 0f);

        // 创建缩放动画 (从 0.8 放大到 1.0)
        var scaleAnim = compositor.CreateScalarKeyFrameAnimation();
        scaleAnim.InsertKeyFrame(0.0f, 0.85f); // 起始稍微缩小
        scaleAnim.InsertKeyFrame(1.0f, 1.0f);  // 钻入到正常大小
        scaleAnim.Duration = TimeSpan.FromMilliseconds(400);
        scaleAnim.Target = "Scale.X";

        // 启动动画(只缩放,不做透明度动画——透明度动画被中断会残留透明状态)
        imageVisual.StartAnimation("Scale.X", scaleAnim);
        imageVisual.StartAnimation("Scale.Y", scaleAnim);
    }
    private void AnimateExtractPanelOpen()
    {
        AnimatePanelOpen(ExtractPanel, ExtractOverlayBackground);
    }
    private void AnimateExtractPanelClose(Action onCompleted)
    {
        AnimatePanelClose(ExtractPanel, ExtractOverlayBackground, () =>
        {
            ExtractOverlayVisibility = Visibility.Collapsed;
            onCompleted?.Invoke();
        });
    }

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

    // ===================== 全选图标动画(2026-09) =====================
    // 全选图标已由静态字形 E8B3 换成 Lottie 动画:XAML 里 4 处 <AnimatedIcon>,Source = WE_Tool.AnimatedVisuals.SelectAllIcon,
    // FallbackIconSource 仍是原字形(系统关掉动画效果时自动退回)。素材是"四个空心方框依次变实心"的 1 秒动画(第 0 帧 = 四个空心方框、第 0~19 帧四个方框依次被填满、第 30 帧起稳定填满、第 40~50 帧缩回空心),
    // 标记对:NormalToPressed_Start(第 0 帧)/_End(第 30 帧) = 按下播【填满】那一段,
    // PressedToNormal_Start(第 30 帧)/_End(第 60 帧) = 松开播【缩回空心】那一段;
    // NormalToPlaying_Start/_End(0→60) 留给菜单项/快捷键这些没有【按住】概念的入口,整段播一遍、播完归位。
    // [为什么不再来回 toggle] 旧写法在 Normal / Playing 之间反复切,只有状态真正【变化】的那次才播动画,
    // 于是每隔一次点击才看得到动画(日志里 Playing / Normal 逐行交替)——改成两段真实状态后,每次按下/松开都是真实切换。
    private const bool SelectAllIconAnimationProbe = true;   // false = 完全回到改动前(图标静止在第 0 帧,不播动画)
    private CancellationTokenSource? _selectAllIconResetCts;   // 整段播放播完的归位令牌(连点时取消上一次)
    private bool _selectAllIconPointerDriven;                  // 本次点击已由按下/松开驱动,Click 里不再播整段

    private async void PlaySelectAllIconAnimation()
    {
        if (!SelectAllIconAnimationProbe) return;
        // 工具栏按钮的按下/松开已经驱动过动画时不再重复播整段(否则两段会互相打断)
        if (_selectAllIconPointerDriven) return;
        // 工具栏按钮可能被 CommandBar 收进溢出菜单,那种情况下图标还没实化(x:Name 字段为 null),直接跳过
        if (ToolbarSelectAllIcon is null) return;
        _selectAllIconResetCts?.Cancel();
        var cts = new CancellationTokenSource();
        _selectAllIconResetCts = cts;
        Log.Information("[动画] 全选图标状态切换 → Playing(整段:第 0→60 帧)");
        AnimatedIcon.SetState(ToolbarSelectAllIcon, "Playing");
        try
        {
            await Task.Delay(1000, cts.Token);   // 素材整段 1 秒(60 帧 @60fps)
        }
        catch (TaskCanceledException)
        {
            return;   // 期间又按下(或又点了一次),交给新的一次接管
        }
        if (cts.IsCancellationRequested) return;
        Log.Information("[动画] 全选图标状态归位 → Normal");
        AnimatedIcon.SetState(ToolbarSelectAllIcon, "Normal");
    }

    /// <summary>按下:播到第 30 帧(四个方框依次被填满)。Global_PointerPressed 命中工具栏全选按钮时调用。</summary>
    private void SelectAllIcon_PointerPressed()
    {
        if (!SelectAllIconAnimationProbe) return;
        if (ToolbarSelectAllIcon is null) return;
        _selectAllIconPointerDriven = true;   // 本次点击由按下/松开驱动
        _selectAllIconResetCts?.Cancel();     // 取消可能还挂着的整段归位
        Log.Information("[动画] 全选图标状态切换 → Pressed(按下:第 0→30 帧)");
        AnimatedIcon.SetState(ToolbarSelectAllIcon, "Pressed");
    }

    /// <summary>松开:从第 30 帧播到第 60 帧(四个方框缩回空心)。Global_PointerReleased 命中工具栏全选按钮时调用。</summary>
    private void SelectAllIcon_PointerReleased()
    {
        _selectAllIconPointerDriven = false;
        if (!SelectAllIconAnimationProbe) return;
        if (ToolbarSelectAllIcon is null) return;
        Log.Information("[动画] 全选图标状态切换 → Normal(松开:第 30→60 帧)");
        AnimatedIcon.SetState(ToolbarSelectAllIcon, "Normal");
    }

    private void SelectAllWallpapers_Click(object sender, RoutedEventArgs e)
    {
        // 先填充选中集合,后进多选模式:Toggle(true) 期间若 Count==0,会被 UpdateMultiSelectCount
        // 的"0 项自动退出"立刻翻回 false,导致首次全选面板被收起、需按两次(旧顺序的根因)
        // 全选图标动画:播一遍(见 PlaySelectAllIconAnimation)
        PlaySelectAllIconAnimation();
        InternalSelectAllWallpapers();
        if (!IsMultiSelectMode)
        {
            IsMultiSelectMode = true;
        }
    }
    private void InvertSelection_CLick(object sender, RoutedEventArgs e)
    {
        InternalInvertSelection();
        if (!IsMultiSelectMode)
        {
            IsMultiSelectMode = true;
        }
    }
    private void ChangeSort(object sender, RoutedEventArgs e)
    {
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
                    SelectAllWallpaper_Accelerator_Invoked(null!, null!);
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
                    Property_Accelerator_Invoked(null!, null!);
                    e.Handled = true;
                    return;
                case VirtualKey.L:
                    // [列表键盘可达 2026-09] Ctrl+L 直达壁纸列表(见 ListKeyboardAccessProbe):
                    // 列表上方有近 20 个工具栏停留点,再加左侧筛选面板与外壳导航栏,按 Tab 到列表要按几十下。
                    if (ListKeyboardAccessProbe)
                    {
                        // [Ctrl 焦点多选 2026-09] Ctrl+L 里的 Ctrl 是复合键的一部分:程序化搬焦点时要屏蔽"Ctrl 划选",
                        // 否则一按 Ctrl+L 就会平白进多选。注意 GotFocus 是异步事件(官方文档明示),不能用
                        // "Focus() 前后 try/finally 复位"——改成一次性令牌,由下一次 GotFocus 自己消费清零;
                        // 没搬动焦点(返回 false)就当场清掉,别让令牌悬着。
                        _suppressCtrlFocusMultiSelect = true;
                        if (!FocusWallpaperList())
                        {
                            _suppressCtrlFocusMultiSelect = false;
                        }
                        e.Handled = true;
                    }
                    return;
                // [Ctrl 焦点多选 2026-09] Ctrl+方向键:自己搬焦点,不赌"按住 Ctrl 时框架还做不做 2D 方向导航"这件事
                // (带修饰键的方向键会不会被框架消费,官方文档没给承诺,实测前无法判定)。SearchRoot 把候选限在列表内,
                // 策略用 Projection(与方向键原生导航同一套几何策略);搬完把事件标记 Handled,免得框架再搬一次
                // (那样一次按键会跳两格)。搬不动(已到边界/候选未实化)只写日志,不静默。
                // 注意:Override 枚举与 XYFocusNavigationStrategy 的数值不同(官方文档:Override 是
                // None=0/Auto=1/Projection=2,而 XYFocusNavigationStrategy 是 Auto=0/Projection=1),
                // 所以不能强转(强转成 1 会变成 "继承祖先策略" 而不是 Projection),必须取 Override 自己的成员。
                case VirtualKey.Left:
                case VirtualKey.Right:
                case VirtualKey.Up:
                case VirtualKey.Down:
                    if (CtrlFocusMultiSelect)
                    {
                        var navDirection = e.Key switch
                        {
                            VirtualKey.Left => FocusNavigationDirection.Left,
                            VirtualKey.Right => FocusNavigationDirection.Right,
                            VirtualKey.Up => FocusNavigationDirection.Up,
                            _ => FocusNavigationDirection.Down,
                        };
                        bool moved;
                        try
                        {
                            var candidate = FocusManager.FindNextElement(navDirection, new FindNextElementOptions
                            {
                                SearchRoot = WallpapersRepeater,
                                XYFocusNavigationStrategyOverride = XYFocusNavigationStrategyOverride.Projection,
                            });
                            moved = candidate is FrameworkElement next && next.Focus(FocusState.Keyboard);
                        }
                        catch (Exception ex)
                        {
                            moved = false;
                            Serilog.Log.Warning(ex, "[A11y] Ctrl+方向键 手动搬焦点异常");
                        }
                        e.Handled = true;
                        // [临时探针 2026-09] 定位完可删:Moved=False 且已到列表边界属正常。
                        Serilog.Log.Information("[A11y] Ctrl+{Key} 手动搬焦点: {Moved}", e.Key, moved);
                    }
                    return;
            }
        }
        // [Shift 焦点区间 2026-09] Shift+方向键:同样自己搬焦点(理由同上面 Ctrl 分支——带修饰键的方向键框架管不管,
        // 官方没承诺),搬完标记 Handled 免得框架再搬一次跳两格。选中区间不在这里做:焦点一变,就由 GotFocus 里的
        // Shift 分支按"锚点 → 当前焦点"重算(与 Ctrl 那条路径同构,选中逻辑只留一处)。
        // 注意 Ctrl+Shift+方向键到不了这里:上面 if (ctrl) 已先接管(GotFocus 里 Shift 分支也排除了 Ctrl 同按)。
        else if (ShiftFocusRangeSelect
            && (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
            && (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down)
        {
            var rangeNavDirection = e.Key switch
            {
                VirtualKey.Left => FocusNavigationDirection.Left,
                VirtualKey.Right => FocusNavigationDirection.Right,
                VirtualKey.Up => FocusNavigationDirection.Up,
                _ => FocusNavigationDirection.Down,
            };
            bool rangeMoved;
            try
            {
                var rangeCandidate = FocusManager.FindNextElement(rangeNavDirection, new FindNextElementOptions
                {
                    SearchRoot = WallpapersRepeater,
                    XYFocusNavigationStrategyOverride = XYFocusNavigationStrategyOverride.Projection,
                });
                rangeMoved = rangeCandidate is FrameworkElement rangeNext && rangeNext.Focus(FocusState.Keyboard);
            }
            catch (Exception ex)
            {
                rangeMoved = false;
                Serilog.Log.Warning(ex, "[A11y] Shift+方向键 手动搬焦点异常");
            }
            e.Handled = true;
            // [临时探针 2026-09] 定位完可删:Moved=False 且已到列表边界属正常。
            Serilog.Log.Information("[A11y] Shift+{Key} 手动搬焦点: {Moved}", e.Key, rangeMoved);
            return;
        }
        else if (e.Key == VirtualKey.Delete)
        {
            Delete_Accelerator_Invoked(null!, null!);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.F5)
        {
            // F5 刷新（刷新进行中时由 WallpaperListRefresh_Click_ByCommandBarFlyout 内部防连按兜底）
            WallpaperListRefresh_Click_ByCommandBarFlyout(null!, null!);
            e.Handled = true;
        }
    }

    // [列表键盘可达 2026-09] 聚焦某张壁纸卡片的容器(ItemContainer,ElementPrepared 里设成 Tab 停留点的那一层)。
    // 只用 TryGetElement(已实化的容器):用户点得到的卡必然已实化;跨越视口时 GetOrCreateElement 造出的容器
    // 要等一次布局才能接收焦点,那是"方向键一路走通"那一步(方案二)的事,本批不做。
    private bool FocusWallpaperCard(WallpaperItem item, FocusState state)
    {
        var index = Wallpapers.IndexOf(item);
        if (index >= 0 && WallpapersRepeater.TryGetElement(index) is FrameworkElement card && card.Focus(state))
        {
            _listAnchorIndex = index;
            return true;
        }
        return false;
    }

    // [列表键盘可达 2026-09] Ctrl+L 的落点:优先回到上次停留过的卡,其次第一张已实化的卡;都没有就写日志,
    // 不做静默失败。官方文档:FrameworkElement 获得键盘焦点时由框架负责把它带进视野,故这里不写 StartBringIntoView。
    // 返回值 = 是否真的搬动了焦点(供 Ctrl 划选的一次性令牌判断要不要留,见 _suppressCtrlFocusMultiSelect)。
    private bool FocusWallpaperList()
    {
        if (_listAnchorIndex >= 0 && _listAnchorIndex < Wallpapers.Count
            && WallpapersRepeater.TryGetElement(_listAnchorIndex) is FrameworkElement anchor && anchor.Focus(FocusState.Keyboard))
            return true;

        for (int i = 0; i < Wallpapers.Count; i++)
        {
            if (WallpapersRepeater.TryGetElement(i) is FrameworkElement card && card.Focus(FocusState.Keyboard))
            {
                _listAnchorIndex = i;
                return true;
            }
        }

        Serilog.Log.Warning("[A11y] Ctrl+L 未找到可聚焦的壁纸卡片(列表为空或容器全部未实化)");
        return false;
    }

    private void SelectAllWallpaper_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        // 先填集合后进模式,原因同 SelectAllWallpapers_Click
        // 全选图标动画:播一遍(见 PlaySelectAllIconAnimation)
        PlaySelectAllIconAnimation();
        InternalSelectAllWallpapers();
        if (!IsMultiSelectMode)
        {
            IsMultiSelectMode = true;
        }
    }
    private void InvertSelection_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        InternalInvertSelection();
        if (!IsMultiSelectMode)
        {
            IsMultiSelectMode = true;
        }
    }
    private async void Copy_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        try
        {
            await CopyWallpapersAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "复制壁纸文件夹失败");
        }
        finally
        {
            // 快捷键无点击按钮,动画作用于 CommandBar 复制图标
            await PlayCopyCheckAnimationAsync(ToolbarCopyIcon);
        }
    }
    private async void Copy_Click_ByCommandBarFlyout(object sender, RoutedEventArgs e)
    {
        try
        {
            HideWallpaperContextMenu();
            await CopyWallpapersAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "复制壁纸文件夹失败");
        }
        finally
        {
            // 动画不依赖复制结果,即使复制抛异常/无选中项也执行
            // 目标图标:CommandBar 按钮 → ToolbarCopyIcon;详情面板按钮 → DetailCopyIcon
            var targetIcon = sender is AppBarButton ? ToolbarCopyIcon : DetailCopyIcon;
            if (targetIcon != null)
                await PlayCopyCheckAnimationAsync(targetIcon);
        }
    }

    // 复制成功反馈(单 FontIcon 序列):淡出 → 切勾 → 从左往右扫出 → 停留 → 淡出 → 切回复制 → 淡入
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
    private async Task<bool> CopyWallpapersAsync()
    {
        var items = SelectedWallpapers.Count > 0
            ? SelectedWallpapers.ToList()
            : ViewModel?.SelectedWallpaper is not null ? [ViewModel.SelectedWallpaper] : [];

        if (items.Count == 0) return false;

        var folders = new List<Windows.Storage.StorageFolder>();
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.FolderPath)) continue;
            try
            {
                var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(item.FolderPath);
                folders.Add(folder);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "获取文件夹失败: {Path}", item.FolderPath);
            }
        }
        if (folders.Count == 0) return false;

        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetStorageItems(folders);
        Clipboard.SetContent(dataPackage);
        return true;
    }
    private async void ImportToEditor_Click(object sender, RoutedEventArgs e)
    {
        var item = ViewModel.SelectedWallpaper;
        if (item == null || string.IsNullOrEmpty(item.FolderPath)) return;
        if (!Directory.Exists(item.FolderPath)) return;

        var projectPath = ViewModel.PathManagementVM?.ProjectPath;
        if (string.IsNullOrEmpty(projectPath))
        {
            Log.Warning("[导入编辑器] 项目路径未设置");
            return;
        }

        // 完全复用提取进度面板
        _isSingleExtract = true;
        _extractTotalCount = 1;
        _extractCompletedCount = 0;
        ExtractProgress = 0;
        ExtractSubText = "";
        ExtractEntryText = "";
        SetExtractPreviewImage(item.Preview, item.Title ?? item.WorkshopID ?? "壁纸");
        OnPropertyChanged(nameof(ExtractProgress));
        OnPropertyChanged(nameof(ExtractProgressText));
        OnPropertyChanged(nameof(ExtractSubText));
        OnPropertyChanged(nameof(ExtractEntryText));
        OnPropertyChanged(nameof(ExtractEntryVisibility));
        ExtractState = ExtractState.Running;
        IsExtracting = true;
        TaskbarProgressService.SetProgress(0);
        // 导航栏徽标:显示本次提取剩余数量(新任务开始,复位失败红标)
        _navBadgeError = false;
        NavBadgeService.SetBadge("Papers", 1);

        _extractService = new RepkgCliService();
        _extractCts = new CancellationTokenSource();

        var extractSettings = new ExtractSettings
        {
            OutputMode = 0,
            TexExportMode = 2,
            OutProjectJSON = true,
            UseProjectName = true,
            OneFolder = 0,
            CoverAllFiles = true,
            KeepSubfolderStructure = 0,
            LazyLoad = true,
        };

        try
        {
            await _extractService.ExtractWallpapersAsync(
                new[] { item },
                projectPath,
                extractSettings,
                msg =>
                {
                    var parts = msg.Split('|');
                    var action = parts.Length > 1 ? parts[1] : "";
                    double pct = parts.Length > 2 && double.TryParse(parts[2], out var parsed) ? parsed : 0;

                    _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    {
                        if (action == "解析PKG" && pct > 0)
                        {
                            ExtractProgress = pct;
                            OnPropertyChanged(nameof(ExtractProgress));
                            TaskbarProgressService.SetProgress(pct);
                        }
                        if (action == "完成")
                        {
                            _extractCompletedCount = 1;
                            ExtractProgress = 100;
                            OnPropertyChanged(nameof(ExtractProgress));
                            OnPropertyChanged(nameof(ExtractProgressText));
                            TaskbarProgressService.SetProgress(100);
                            // 导航栏徽标:剩余 0 → 隐藏
                            NavBadgeService.SetBadge("Papers", null);
                        }
                        ExtractSubText = action == "完成" ? "已完成" : $"{pct:F0}%";
                        OnPropertyChanged(nameof(ExtractSubText));
                    });
                },
                _extractCts.Token);

            var safeName = GetSafeWallpaperName(item.Title ?? item.WorkshopID ?? "untitled");
            Log.Information("[导入编辑器] 壁纸已导入到: {Path}", Path.Combine(projectPath, safeName));
            // 主窗口不在焦点时弹系统通知
            NotificationService.NotifyIfUnfocused("导入到编辑器完成", $"已导入: {safeName}");
        }
        catch (OperationCanceledException)
        {
            Log.Information("[导入编辑器] 用户取消");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[导入编辑器] 导入失败: {Name}", item.Title);
            _navBadgeError = true;
            NotificationService.NotifyIfUnfocused("导入到编辑器失败", $"导入失败:{item.Title}");
        }
        finally
        {
            _extractService = null;
            _extractCts = null;
            IsExtracting = false;
            TaskbarProgressService.Clear();
            // 导航栏徽标:正常结束(完成/停止)隐藏;失败 → 红色保留(像 InfoBar 错误条)
            if (_navBadgeError)
                NavBadgeService.SetBadge("Papers", 1, NavBadgeState.Error);
            else
                NavBadgeService.SetBadge("Papers", null);
        }
    }

    private static string GetSafeWallpaperName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name);
        for (int i = 0; i < sb.Length; i++)
            if (invalid.Contains(sb[i])) sb[i] = '_';
        return sb.ToString().Trim();
    }

    private async void Delete_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        // 经 Page_KeyDown_Core 由窗口分发调用,e 参数恒为 null,不可解引用(Handled 由调用方标记)
        if (UninstallSelectedCommand == null) return;

        try
        {
            if (IsMultiSelectMode)
            {
                await UninstallSelectedCommand.ExecuteAsync(null);
            }
            else
            {
                await UninstallSelectedCommand.ExecuteAsync(ViewModel.SelectedWallpaper);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "通过删除快捷键执行删除命令时发生异常。");
        }
    }

    private void SelectAllWallpapers_Click_ByCommandBarFlyout(object sender, RoutedEventArgs e)
    {
        // 先填集合后进模式,原因同 SelectAllWallpapers_Click
        // 全选图标动画:播一遍(见 PlaySelectAllIconAnimation)
        PlaySelectAllIconAnimation();
        InternalSelectAllWallpapers();
        if (!IsMultiSelectMode)
        {
            IsMultiSelectMode = true;
        }
    }

    // 全选按钮按下反馈:官方 PointerDown/UpThemeAnimation,Storyboard 定义在 XAML,
    // 触发走全局 Global_PointerPressed/Released(AddHandler handledEventsToo:true 能收到 Button 内部 handled 事件)
    private void InvertSelection_CLick_ByCommandBarFlyout(object sender, RoutedEventArgs e)
    {
        InternalInvertSelection();
        if (!IsMultiSelectMode)
        {
            IsMultiSelectMode = true;
        }
    }
    private bool _isRefreshing;

    private async void WallpaperListRefresh_Click_ByCommandBarFlyout(object sender, RoutedEventArgs e)
    {
        // 防连按：刷新进行中时忽略再次触发（按钮已禁用，F5/菜单入口由此兜底）
        if (_isRefreshing) return;
        _isRefreshing = true;
        RefreshButton.IsEnabled = false;
        var pressTime = DateTime.Now; // 记录按下时刻(旋转动画 2 秒)

        // [2026-09] 按下立即清列表 + 显示扫描转圈(不等扫描完成——旧数据先撤下,避免刷新期间还显示过期列表)
        ShowScanProgress(true);
        Wallpapers.Clear();
        SelectedWallpapers.Clear();
        IsMultiSelectMode = false;
        ViewModel.SelectedWallpaper = null;
        _filteredWallpapers = [];
        CurrentPage = 1;
        NotifyPagerStateChanged();
        ShowTip(NoScanResultTip, false);
        ShowTip(NoResultTip, false);

        try
        {
            App.StartBackgroundScan(ViewModel.PathManagementVM.WorkshopPath, ViewModel.PathManagementVM.OfficialPath, ViewModel.PathManagementVM.ProjectPath, ViewModel.PathManagementVM.AcfPath, ViewModel.PathManagementVM.VdfPath, ViewModel.AppSettingsVM.ScanCacheEnabled == "1");
            await RefreshWallpaperList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "刷新壁纸列表失败");
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
    private void Properties_Click(object sender, RoutedEventArgs e)
    {
        _ = PropertiesAsync();
    }
    private void Property_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        // 经 Page_KeyDown_Core 由窗口分发调用,e 参数恒为 null,不可解引用(Handled 由调用方标记)
        _ = PropertiesAsync();
    }
    private void Properties_Click_ByCommandBarFlyout(object sender, RoutedEventArgs e)
    {
        _ = PropertiesAsync();
    }
    private async Task PropertiesAsync()
    {
        try
        {
        HideWallpaperContextMenu();
        // 多选模式:为每个选中壁纸打开独立属性窗口
        var items = IsMultiSelectMode && SelectedWallpapers.Count > 0
            ? SelectedWallpapers.ToList()
            : ViewModel.SelectedWallpaper != null
                ? new List<WallpaperItem> { ViewModel.SelectedWallpaper }
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
        foreach (var wp in items)
            PropertiesWindow.Open(wp);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打开属性窗口失败");
        }
    }
    private async void OnIconSizeChanged(object sender, RoutedEventArgs e)
    {
        try
        {
            await Task.Delay(100);
            HideWallpaperContextMenu();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "图标尺寸变更处理失败");
        }
    }
    private void OnDisplayModeChanged(object sender, RoutedEventArgs e)
    {
        HideWallpaperContextMenu();
    }
    private async void OnTagDisplayChanged(object sender, RoutedEventArgs e)
        {
            HideWallpaperContextMenu();
            // [实验] 图标模式已换 ItemsRepeater(无 ItemsPanelRoot 容器遍历),原图标角标刷新暂注释
            // 优化:不再重置 ItemsSource 重建全列表——只遍历可见容器手动刷新角标(滚动位置保留、无容器 churn)
            // 非反射(AOT 兼容):ItemsPanelRoot 返回类型是 Panel(基类),Children 是 Panel 属性,直接访问即可,不强转 ItemsWrapGrid
            //if (WallpapersGridView.ItemsPanelRoot is not { } panelRoot) return;
            //foreach (var child in panelRoot.Children)
            //{
            //    if (child is not GridViewItem container) continue;
            //    if (container.ContentTemplateRoot is not Grid root) continue;
            //    if (WallpapersGridView.ItemFromContainer(container) is WallpaperItem item)
            //        UpdateTagBadge(root, item);
            //}
        }

        /// <summary>更新卡片右上角标签(按当前标签模式;容器绑定时也调用,滚动回来的新容器自动正确)</summary>
        private void UpdateTagBadge(Grid root, WallpaperItem item)
        {
            if (root.FindName("TagDisplayBorder") is not Border border) return;
            int index = ViewModel.WallpaperDisplayVM.WallpaperTagDisplayIndex;
            bool visible = index != 4; // 模式 4=None:隐藏(与 VM TagDisplayVisibility 一致)
            border.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) return;
            if (border.Child is TextBlock tb)
                tb.Text = new PapersTagContentChoose().Convert(item, typeof(string), "", "") as string ?? "";
        }
    private void CancelMultiSelect_Click(object sender, RoutedEventArgs e)
    {
        IsMultiSelectMode = false;
    }
    public void HideWallpaperContextMenu()
    {
        WallpaperContextMenu?.Hide();
    }

    private void InternalSelectAllWallpapers()
    {
        ViewModel.SuspendSelectedWallpapersCollectionChanged();
        SelectedWallpapers.CollectionChanged -= SelectedWallpapers_CollectionChanged;

        var itemsToAdd = Wallpapers.Where(w => !w.IsSelected).ToList();
        foreach (var item in Wallpapers.Where(w => !w.IsSelected))
        {
            item.IsSelected = true;
            SelectedWallpapers.Add(item);
        }
        // 同步单选壁纸:全选时单选壁纸也进入选中态,避免残留状态错乱
        if (ViewModel.SelectedWallpaper is { } single && !single.IsSelected)
        {
            single.IsSelected = true;
            if (!SelectedWallpapers.Contains(single))
                SelectedWallpapers.Add(single);
        }

        ViewModel.ResumeSelectedWallpapersCollectionChanged();
        SelectedWallpapers.CollectionChanged += SelectedWallpapers_CollectionChanged;
        RefreshDisplayedSelectedWallpapers(forceRebuild: true);

        DispatcherQueue.TryEnqueue(() => {
            UpdateStackVisuals();
            UpdateMultiSelectCount();
        });

    }
    private void InternalInvertSelection()
    {
        PlayInvertSelectionIconAnimation();   // 反选图标动画:播一遍(见 PlayInvertSelectionIconAnimation)
        ViewModel.SuspendSelectedWallpapersCollectionChanged();
        SelectedWallpapers.CollectionChanged -= SelectedWallpapers_CollectionChanged;
        var currentlySelected = SelectedWallpapers.ToList();
        foreach (var item in Wallpapers)
        {
            item.IsSelected = !item.IsSelected;
        }
        // 同步单选壁纸:反选时单选壁纸的选中态也翻转(它可能不在 Wallpapers 筛选列表里)
        if (ViewModel.SelectedWallpaper is { } single)
        {
            single.IsSelected = !single.IsSelected;
        }
        SelectedWallpapers.Clear();
        foreach (var item in Wallpapers)
        {
            if (item.IsSelected)
                SelectedWallpapers.Add(item);
        }
        // 单选壁纸翻转后若选中,加入集合(可能在 Wallpapers 之外)
        if (ViewModel.SelectedWallpaper is { } single2 && single2.IsSelected && !SelectedWallpapers.Contains(single2))
        {
            SelectedWallpapers.Add(single2);
        }
        ViewModel.ResumeSelectedWallpapersCollectionChanged();
        SelectedWallpapers.CollectionChanged += SelectedWallpapers_CollectionChanged;
        RefreshDisplayedSelectedWallpapers(forceRebuild: true);

        UpdateMultiSelectCount();
        UpdateStackVisuals();
    }
    private void SetExtractPreviewImage(string? previewPath, string title)
    {
        ExtractPreviewTitle.Text = title;
        if (string.IsNullOrEmpty(previewPath) || previewPath == "ms-appx:///Assets/NoPreview.png" || !File.Exists(previewPath))
        {
            ExtractPreviewImage.Source = null;
            return;
        }
        try
        {
            ExtractPreviewImage.Source = new BitmapImage(new Uri("file:///" + previewPath.Replace('\\', '/')));
        }
        catch
        {
            ExtractPreviewImage.Source = null;
        }
    }
    private async Task ExtractSelectedWallpapersAsync()
    {
        // Collect selected wallpapers
        var itemsToExtract = ViewModel.SelectedWallpapers.Count > 0
            ? SelectedWallpapers.ToList()
            : ViewModel.SelectedWallpaper is not null ? [ViewModel.SelectedWallpaper] : [];

        if (itemsToExtract.Count == 0)
        {
            await DialogHelper.ShowMessageAsync("提示", "请选择要提取的壁纸。");
            return;
        }

        var outputPath = ViewModel.PathManagementVM.DownloadPath;
        if (string.IsNullOrEmpty(outputPath))
        {
            outputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "WE_OutPut");
        }

        try
        {
            IsExtracting = true;
            ExtractState = ExtractState.Running;
            _extractTotalCount = itemsToExtract.Count;
            _extractCompletedCount = 0;
            _extractCompletedNames = [];
            _extractProgressByName = [];
            ExtractProgressItems.Clear();
            ExtractProgress = 0;
            ExtractStatus = "正在提取...";
            TaskbarProgressService.SetProgress(0);
            // 导航栏徽标:显示本次提取剩余数量(新任务开始,复位失败红标)
            _navBadgeError = false;
            NavBadgeService.SetBadge("Papers", itemsToExtract.Count);

            // 判断单/多模式
            _isSingleExtract = itemsToExtract.Count == 1;
            ExtractWallpaperList.Visibility = _isSingleExtract ? Visibility.Collapsed : Visibility.Visible;
            OnPropertyChanged(nameof(ExtractPreviewVisibility));
            ExtractSubText = _isSingleExtract ? "准备中..." : $"已完成 0/{itemsToExtract.Count} 个壁纸";
            ExtractEntryText = "";
            OnPropertyChanged(nameof(ExtractEntryVisibility));

            _extractService = new RepkgCliService();
            _extractCts = new CancellationTokenSource();

            var uiQueue = DispatcherQueue;

            // 构建 名称→WallpaperItem 映射，用于预览图切换
            var extractNameToItem = new Dictionary<string, WallpaperItem>(itemsToExtract.Count);
            foreach (var w in itemsToExtract)
            {
                var key = w.Title ?? w.WorkshopID ?? (w.FolderPath != null ? new DirectoryInfo(w.FolderPath).Name : "?");
                extractNameToItem[key] = w;
            }

            // 设置初始预览图
            var firstName = itemsToExtract[0].Title ?? itemsToExtract[0].WorkshopID ?? "壁纸";
            SetExtractPreviewImage(itemsToExtract[0].Preview, firstName);

            // 多壁纸模式：通过 _extractCompletedCount 跟踪总体进度

            Action<string> onProgress = msg =>
            {
                var parts = msg.Split('|');
                var name = parts[0];
                // 汇总类消息(如"提取完成，共 N 个壁纸")不含 '|',防御性取默认值,避免越界崩溃
                var action = parts.Length > 1 ? parts[1] : "";
                double pct = parts.Length > 2 && double.TryParse(parts[2], out var parsed) ? parsed : 0;

                uiQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (_isSingleExtract)
                    {
                        // 单壁纸模式：进度条跟随单壁纸内部的条目进度
                        if (action == "解析PKG" || action == "开始")
                        {
                            ExtractProgress = pct;
                            ExtractSubText = $"正在解析... {pct:F0}%";
                            TaskbarProgressService.SetProgress(pct);
                        }
                        else if (action == "跳过(已提取)")
                        {
                            ExtractProgress = 100;
                            ExtractSubText = "壁纸已提取，跳过";
                            TaskbarProgressService.SetProgress(100);
                        }
                        else if (action == "完成")
                        {
                            ExtractProgress = 100;
                            ExtractSubText = "提取完成";
                            TaskbarProgressService.SetProgress(100);
                            if (_extractCompletedNames.Add(name))
                            {
                                _extractCompletedCount = 1;
                                OnPropertyChanged(nameof(ExtractProgressText));
                                // 导航栏徽标:剩余 0 → 隐藏
                                NavBadgeService.SetBadge("Papers", null);
                            }
                        }
                        else if (action == "失败")
                        {
                            ExtractSubText = "提取失败";
                        }
                    }
                    else
                    {
                        // 多壁纸模式：进度条反映已完成壁纸数 / 总壁纸数
                        if ((action == "开始" || action == "解析PKG") && !_extractCompletedNames.Contains(name))
                        {
                            // 首次进入:创建进度项加入列表;后续 entry 事件持续刷新该壁纸进度
                            if (!_extractProgressByName.TryGetValue(name, out var progressItem))
                            {
                                progressItem = new ExtractProgressItem
                                {
                                    Name = name,
                                    Preview = extractNameToItem.TryGetValue(name, out var w) ? w.Preview : null
                                };
                                _extractProgressByName[name] = progressItem;
                                ExtractProgressItems.Add(progressItem);
                            }
                            progressItem.Progress = pct; // 0.5% 阈值防抖在 setter 内
                        }
                        else if (action == "完成" && _extractCompletedNames.Add(name))
                        {
                            // 已完成壁纸移出列表(剩余项自动上移)
                            if (_extractProgressByName.Remove(name, out var doneItem))
                                ExtractProgressItems.Remove(doneItem);
                            _extractCompletedCount++;
                            ExtractProgress = (double)_extractCompletedCount / _extractTotalCount * 100;
                            ExtractSubText = $"已完成 {_extractCompletedCount}/{_extractTotalCount} 个壁纸";
                            OnPropertyChanged(nameof(ExtractProgressText));
                            TaskbarProgressService.SetProgress(ExtractProgress);
                            // 导航栏徽标:剩余 = 总数 - 完成数
                            NavBadgeService.SetBadge("Papers", _extractTotalCount - _extractCompletedCount);
                        }
                        else if (action == "失败" && _extractProgressByName.Remove(name, out var failedItem))
                        {
                            // 崩溃跳过/失败的壁纸同样移出列表
                            ExtractProgressItems.Remove(failedItem);
                        }
                    }

                    // 尝试提取当前处理的条目名（单壁纸模式时显示在 ExtractEntryText）
                    if (action == "解析PKG" && parts.Length > 3)
                    {
                        ExtractEntryText = $"正在处理: {parts[3]}";
                    }
                });
            };

            // 监听 RePKG_Re 的进程输出，捕获当前条目名
            // 在 onProgress 回调中，如果有条目信息，通过额外段传入：
            // RepkgCliService.RunRepkgAsync 中 OutputDataReceived 已解析 "entry" 字段，
            // 但当前只传了 pos/total。需要修改 RunRepkgAsync 将 entry 名也传入 progressCb。
            // 临时方案：从 msg 中取第4段（如果有）
            // 已通过上述 parts[3] 逻辑支持

            var extractSettings = new ExtractSettings
            {
                UseProjectName = ViewModel.UseProjectName,
                OneFolder = ViewModel.OneFolder,
                FlatFileNamingMode = ViewModel.FlatFileNamingMode,
                KeepSubfolderStructure = ViewModel.KeepSubfolderStructure,
                CoverAllFiles = ViewModel.OneFolder == 1 ? ViewModel.CoverAllFiles : true,
                IgnoreExtension = ViewModel.IgnoreExtension,
                IgnoreExtensionList = ViewModel.IgnoreExtensionList,
                OnlyExtension = ViewModel.OnlyExtension,
                OnlyExtensionList = ViewModel.OnlyExtensionList,
                OutProjectJSON = ViewModel.OutProjectJSON,
                TexExportMode = ViewModel.TexExportMode,
                OutputMode = ViewModel.OutputMode,
                FilterEffectImagesThreshold = ViewModel.FilterEffectImagesThreshold,
                FilterEffectImagesEnabled = ViewModel.FilterEffectImagesEnabled,
                MediaExportImages = ViewModel.MediaExportImages,
                MediaExportVideos = ViewModel.MediaExportVideos,
                MediaExportAudios = ViewModel.MediaExportAudios,
                MediaFilterTransparentImages = ViewModel.MediaFilterTransparentImages,
                OnlyPaths = ViewModel.OnlyPaths,
                OnlyPathsList = ViewModel.OnlyPathsList,
                IgnorePaths = ViewModel.IgnorePaths,
                IgnorePathsList = ViewModel.IgnorePathsList,
                MaxConcurrentExtractions = ViewModel.MaxConcurrentExtractions,
                ProcessPriority = ViewModel.ProcessPriority,
                SkipExistingOutput = ViewModel.OneFolder == 1 ? ViewModel.SkipExistingOutput : false,
            };

            RepkgCliService.SetProcessPriorityLevel(ViewModel.ProcessPriority);

            await _extractService.ExtractWallpapersAsync(
                itemsToExtract, outputPath, extractSettings,
                onProgress, _extractCts.Token);

            if (!_extractCts.IsCancellationRequested)
            {
                ExtractProgress = 100;
                ExtractState = ExtractState.Completed;
                IsExtracting = false;
                ExtractStatus = "提取完成";
                TaskbarProgressService.SetProgress(100);
                if (!_isSingleExtract)
                    ExtractSubText = $"已完成 {_extractCompletedCount}/{_extractTotalCount} 个壁纸";
                Log.Information("提取完成: {Count} 个壁纸 → {Output}", itemsToExtract.Count, outputPath);
                // 主窗口不在焦点时弹系统通知
                NotificationService.NotifyIfUnfocused(
                    "提取完成",
                    _isSingleExtract ? "壁纸已提取完成" : $"已完成 {_extractCompletedCount}/{_extractTotalCount} 个壁纸");
            }
            else
            {
                ExtractState = ExtractState.Completed;
                IsExtracting = false;
                ExtractStatus = "提取已停止";
                TaskbarProgressService.Clear();
                Log.Information("提取被用户停止");
                NotificationService.NotifyIfUnfocused("提取已停止", "提取已停止");
            }
        }
        catch (OperationCanceledException)
        {
            ExtractStatus = "提取已停止";
            ExtractState = ExtractState.Completed;
            IsExtracting = false;
            TaskbarProgressService.Clear();
            Log.Information("提取被用户停止");
            NotificationService.NotifyIfUnfocused("提取已停止", "提取已停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "提取失败");
            ExtractState = ExtractState.Completed;
            IsExtracting = false;
            ExtractStatus = "提取失败，请查看日志";
            ExtractProgress = 0;
            TaskbarProgressService.SetError();
            _navBadgeError = true;
            NotificationService.NotifyIfUnfocused("提取失败", "提取失败，请查看日志");
        }

        // 收尾:清空进度列表(完成/取消/异常三路都经过这里)
        _extractProgressByName = [];
        ExtractProgressItems.Clear();
        // 导航栏徽标:提取结束(完成/停止)隐藏;失败 → 红色保留剩余数(像 InfoBar 错误条)
        if (_navBadgeError)
            NavBadgeService.SetBadge("Papers", Math.Max(1, _extractTotalCount - _extractCompletedCount), NavBadgeState.Error);
        else
            NavBadgeService.SetBadge("Papers", null);
    }

    private void PauseExtractButton_Click(object sender, RoutedEventArgs e)
    {
        _extractService?.Pause();
        ExtractState = ExtractState.Paused;
        ExtractStatus = "已暂停";
        TaskbarProgressService.SetPaused();
        // 导航栏徽标:暂停 → 黄色,剩余数不变
        NavBadgeService.SetBadge("Papers", _extractTotalCount - _extractCompletedCount, NavBadgeState.Paused);
    }

    private void ResumeExtractButton_Click(object sender, RoutedEventArgs e)
    {
        _extractCts?.Dispose();
        _extractCts = new CancellationTokenSource();
        _extractService?.Resume();
        ExtractState = ExtractState.Running;
        ExtractStatus = "正在提取...";
        // 恢复为绿色:值沿用当前进度(暂停不丢值,任务栏进度条只在暂停时变黄)
        TaskbarProgressService.SetProgress(ExtractProgress);
        // 导航栏徽标:恢复 → 绿色,剩余数不变
        NavBadgeService.SetBadge("Papers", _extractTotalCount - _extractCompletedCount, NavBadgeState.Running);
    }

    private void StopExtractButton_Click(object sender, RoutedEventArgs e)
    {
        _extractCts?.Cancel();
        _extractService?.Stop();
        ExtractStatus = "正在停止...";
    }

    private void ExtractCloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsExtracting)
        {
            _extractCts?.Cancel();
            _extractService?.Stop();
            _isExtracting = false; // 避免动画触发二次关闭
        }
        TaskbarProgressService.Clear();
        AnimateExtractPanelClose(() =>
        {
            ExtractOverlayVisibility = Visibility.Collapsed;
            ExtractState = ExtractState.Idle;
        });
    }

    private async Task DeleteItemAsync(WallpaperItem item, bool skipConfirm = false)
    {
        if (item == null || item.FolderPath == null) return;

        // 创意工坊项目才有 WorkshopID,非创意工坊项目为 null(RemoveWorkshopKeyFromAcfAsync 内部对空值安全返回)
        if (!string.IsNullOrEmpty(item.WorkshopID))
            await ViewModel.PathManagementVM.RemoveWorkshopKeyFromAcfAsync(item.WorkshopID, ViewModel.PathManagementVM.AcfPath);
        bool isFolderDeleted = await _pickerService.DeleteFolderAsync(item.FolderPath);

        if (isFolderDeleted)
        {
            App.GlobalAllWallpapers.Remove(item);
            _allWallpapers.Remove(item);
            _filteredWallpapers.Remove(item);
            Wallpapers.Remove(item);
            SelectedWallpapers.Remove(item);

            // 当前页被删空且不是第一页时回退一页（分页模式）
            if (Wallpapers.Count == 0 && CurrentPage > 1)
            {
                CurrentPage--;
                foreach (var it in GetCurrentPageItems(_filteredWallpapers))
                {
                    Wallpapers.Add(it);
                }
            }
            NotifyPagerStateChanged();
            UpdateMultiSelectCount();
            Log.Information($"壁纸 {item.Title} 已从列表和磁盘中彻底移除。");
        }
    }
    /// <summary>
    /// 卸载:创意工坊壁纸先取消订阅(Steamworks 不可用时弹窗让用户选择是否继续删非创意工坊项),
    /// 然后删除本地文件并清 acf 键值;非创意工坊壁纸直接删本地文件。
    /// </summary>
    private async Task UninstallWallpapersAsync(List<WallpaperItem> workshopItems, List<WallpaperItem> nonWorkshopItems)
    {
        var service = SteamWorkshopService.GetInstance();

        // 创意工坊项:逐个取消订阅,收集成功的(失败的不删文件,避免 Steam 重新下载后文件缺失)
        var unsubscribedWorkshopItems = new List<WallpaperItem>();
        if (workshopItems.Count > 0)
        {
            if (!service.IsAvailable)
            {
                // Steamworks 不可用:无法取消订阅,弹窗让用户选择(决策点,需用户确认)
                bool continueDelete = await DialogHelper.ShowConfirmDialogAsync(
                    "无法取消订阅",
                    $"Steamworks 不可用,无法取消订阅 {workshopItems.Count} 个创意工坊壁纸(请确认 Steam 正在运行)。\n\n" +
                    (nonWorkshopItems.Count > 0
                        ? $"是否继续卸载 {nonWorkshopItems.Count} 个非创意工坊壁纸?"
                        : "是否仍要删除本地文件?"),
                    nonWorkshopItems.Count > 0 ? "继续卸载其它" : "仍要删除",
                    "取消");
                if (!continueDelete) return;

                // 用户选择继续:跳过创意工坊项,只删非创意工坊项
                foreach (var item in nonWorkshopItems)
                {
                    await DeleteItemAsync(item, skipConfirm: true);
                }
                return;
            }

            foreach (var item in workshopItems)
            {
                if (ulong.TryParse(item.WorkshopID, out var wid))
                {
                    if (await service.UnsubscribeAsync(wid))
                        unsubscribedWorkshopItems.Add(item);
                }
            }

            if (unsubscribedWorkshopItems.Count == 0 && workshopItems.Count > 0)
            {
                // 全部取消订阅失败:询问是否继续删非创意工坊项(决策点,需用户确认)
                bool continueDelete = await DialogHelper.ShowConfirmDialogAsync(
                    "取消订阅失败",
                    $"向 Steam 发送取消订阅请求失败(0/{workshopItems.Count} 个壁纸)。\n\n" +
                    (nonWorkshopItems.Count > 0
                        ? $"是否继续卸载 {nonWorkshopItems.Count} 个非创意工坊壁纸?"
                        : "是否仍要删除本地文件?"),
                    nonWorkshopItems.Count > 0 ? "继续卸载其它" : "仍要删除",
                    "取消");
                if (!continueDelete) return;

                foreach (var item in nonWorkshopItems)
                {
                    await DeleteItemAsync(item, skipConfirm: true);
                }
                return;
            }
        }

        // 删除取消订阅成功的创意工坊壁纸本地文件(成功即自动继续,不再弹模态框打断)
        foreach (var item in unsubscribedWorkshopItems)
        {
            await DeleteItemAsync(item, skipConfirm: true);
        }

        // 非创意工坊项:直接删本地文件
        foreach (var item in nonWorkshopItems)
        {
            await DeleteItemAsync(item, skipConfirm: true);
        }
    }

    /// <summary>当前选中项中可用于备份的创意工坊壁纸。</summary>
    private List<WallpaperItem> GetBackupableItems()
        => SelectedWallpapers.Count > 0
            ? SelectedWallpapers.Where(w => w.Source == "workshop").ToList()
            : ViewModel.SelectedWallpaper is WallpaperItem wp && wp.Source == "workshop"
                ? [wp]
                : [];

    /// <summary>[备份子菜单 2026-09] 右键菜单打开时刷新「备份」子菜单:父按钮可用性 +
    /// 两条命令的文案(带数量)与可用性。选中态三分支:全未备份→只能「备份」/全已备份→只能「取消备份」/
    /// 混合→两条都可用(各自计数)。替代旧实现"全都已备份就禁用按钮+文案改成已备份"的做法——
    /// 那种做法在混合态下无法表达"取消",且文案切换容易让人误以为该按钮是"查看已备份列表"。</summary>
    private void UpdateBackupButtonState()
    {
        var items = GetBackupableItems();
        var workshopPath = ViewModel?.PathManagementVM?.WorkshopPath;
        bool pathOk = !string.IsNullOrEmpty(workshopPath) && Directory.Exists(workshopPath);

        List<WallpaperItem> backupable = pathOk
            ? items.Where(i => !string.IsNullOrEmpty(i.WorkshopID)).ToList()
            : [];
        int pending = backupable.Count(i => !BackupService.IsBackedUp(workshopPath!, i.WorkshopID!));
        int done = backupable.Count - pending;

        if (BackupWallpaperButton is AppBarButton btn)
            btn.IsEnabled = pending > 0 || done > 0;

        if (BackupSelectedMenuItem is MenuFlyoutItem backupItem)
        {
            backupItem.Text = string.Format(
                LanguageHelper.GetResource("MenuFlyoutItem_BackupSelected.Text"), pending);
            backupItem.IsEnabled = pending > 0;
        }
        if (UnbackupSelectedMenuItem is MenuFlyoutItem unbackupItem)
        {
            unbackupItem.Text = string.Format(
                LanguageHelper.GetResource("MenuFlyoutItem_UnbackupSelected.Text"), done);
            unbackupItem.IsEnabled = done > 0;
        }
    }

    /// <summary>[备份子菜单 2026-09] 命令一:备份选中项里「未备份」的那些(逻辑同原按钮 Click)。</summary>
    private async void BackupSelected_Click(object sender, RoutedEventArgs e)
    {
        HideWallpaperContextMenu();

        var items = GetBackupableItems();
        if (items.Count == 0) return;

        var workshopPath = ViewModel?.PathManagementVM?.WorkshopPath;
        if (string.IsNullOrEmpty(workshopPath) || !Directory.Exists(workshopPath))
        {
            await DialogHelper.ShowMessageAsync("备份失败",
                "无法确定创意工坊目录，请先在设置中检查路径是否有效。");
            return;
        }

        var toBackup = items.Where(i => !string.IsNullOrEmpty(i.WorkshopID)
            && !BackupService.IsBackedUp(workshopPath, i.WorkshopID!)).ToList();
        if (toBackup.Count == 0)
        {
            await DialogHelper.ShowMessageAsync("备份",
                "所选壁纸已经全部完成备份。");
            return;
        }

        bool confirmed = await DialogHelper.ShowConfirmDialogAsync("备份到工坊目录",
            $"确定要备份选中的 {toBackup.Count} 个创意工坊壁纸吗？\n\n将在创意工坊目录内创建隐藏的 .we_backup 文件夹，" +
            "以硬链接方式保留一份文件——不额外占用磁盘空间；Steam 删除原始文件后备份仍完整保留。",
            "开始备份",
            "取消");
        if (!confirmed) return;

        int success = 0, skippedAll = 0;
        var failures = new List<string>();
        foreach (var item in toBackup)
        {
            if (string.IsNullOrEmpty(item.WorkshopID) || string.IsNullOrEmpty(item.FolderPath)) continue;
            var result = BackupService.BackupWallpaperFolder(item.FolderPath, workshopPath, item.WorkshopID);
            skippedAll += result.Skipped;
            if (result.Error is null)
            {
                success++;
            }
            else
            {
                failures.Add($"{item.Title ?? item.WorkshopID}: {result.Error}");
            }
        }

        var msg = $"备份完成：成功 {success} / {toBackup.Count} 个壁纸";
        if (skippedAll > 0)
            msg += $"\n（其中 {skippedAll} 个文件此前已是链接，自动跳过）";
        if (failures.Count > 0)
            msg += "\n\n失败项：\n" + string.Join("\n", failures);
        await DialogHelper.ShowMessageAsync("备份完成", msg);
    }
    /// <summary>[备份子菜单 2026-09] 弹层不自动继承主窗口运行时主题(公共逻辑见 App.ApplyFlyoutTheme);
    /// 顺带再刷一次两条命令的数量(菜单打开后选中态不变,这里是兜底)。</summary>
    private void BackupSubMenu_Opening(object sender, object e)
    {
        App.ApplyFlyoutTheme(sender, e);
        UpdateBackupButtonState();
    }

    /// <summary>[备份子菜单 2026-09] 命令二:取消选中项里「已备份」的那些。
    /// 只删 .we_backup/&lt;id&gt; 备份目录(硬链接入口),创意工坊源目录一个文件都不动。
    /// [语义边界] 源目录已被删除的壁纸,备份是唯一副本 —— 确认框单独计数并明确警示永久丢失;
    /// 判定口径与壁纸备份页一致(content/&lt;id&gt; 不存在 = 源已删除)。</summary>
    private async void UnbackupSelected_Click(object sender, RoutedEventArgs e)
    {
        HideWallpaperContextMenu();

        var workshopPath = ViewModel?.PathManagementVM?.WorkshopPath;
        if (string.IsNullOrEmpty(workshopPath) || !Directory.Exists(workshopPath))
        {
            await DialogHelper.ShowMessageAsync("取消备份失败",
                "无法确定创意工坊目录，请先在设置中检查路径是否有效。");
            return;
        }

        var toRemove = GetBackupableItems()
            .Where(i => !string.IsNullOrEmpty(i.WorkshopID) && BackupService.IsBackedUp(workshopPath, i.WorkshopID!))
            .ToList();
        if (toRemove.Count == 0)
        {
            await DialogHelper.ShowMessageAsync("取消备份",
                "所选壁纸都没有备份。");
            return;
        }

        int missing = toRemove.Count(i => !Directory.Exists(Path.Combine(workshopPath, i.WorkshopID!)));

        var body = $"确定要取消选中的 {toRemove.Count} 个壁纸的备份吗？\n\n" +
                   "只删除 .we_backup 里的备份副本（硬链接），创意工坊目录里的壁纸文件不受影响。";
        if (missing > 0)
            body += $"\n\n注意：其中 {missing} 个壁纸的源文件已被删除，备份是唯一副本——取消后这些壁纸将永久丢失。";

        bool confirmed = await DialogHelper.ShowConfirmDialogAsync("取消备份", body, "取消备份", "返回");
        if (!confirmed) return;

        int success = 0, failed = 0;
        var failures = new List<string>();
        foreach (var item in toRemove)
        {
            try
            {
                var backupDir = BackupService.GetBackupDir(workshopPath, item.WorkshopID!);
                if (Directory.Exists(backupDir))
                {
                    Directory.Delete(backupDir, true);
                    success++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                failures.Add($"{item.Title ?? item.WorkshopID}: {ex.Message}");
            }
        }

        var msg = $"取消备份完成：成功 {success} / {toRemove.Count} 个壁纸";
        if (failed > 0)
            msg += "\n\n失败项：\n" + string.Join("\n", failures);
        await DialogHelper.ShowMessageAsync("取消备份完成", msg);
    }

    private void WallpaperScrollView_ContextRequested(FrameworkElement sender, ContextRequestedEventArgs args)
    {
        // 1. 阻止事件进一步冒泡，防止触发多次弹出逻辑
        args.Handled = true;

        // 2. 获取右键点击的具体坐标
        if (args.TryGetPosition(sender, out Point p))
        {
            // 如果是鼠标右键点击，在点击位置弹出
            WallpaperContextMenu.ShowAt(sender, new FlyoutShowOptions
            {
                Position = p,
                ShowMode = FlyoutShowMode.Standard
            });
        }
        else
        {
            // 如果是通过键盘（Shift+F10）触发，在元素中心弹出
            WallpaperContextMenu.ShowAt(sender);
        }
    }

    // ... INotifyPropertyChanged 标准实现 ...
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

}
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Frozen;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Controls;
using WE_Tool.Converters;
using WE_Tool.Models;
using WE_Tool.Service;
using WE_Tool.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.System;
using Windows.Storage;
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
                // 省略号分隔
                PageNumbersPanel.Children.Add(new TextBlock
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
    private static readonly Windows.Globalization.Collation.CharacterGroupings _zhGroupings = new Windows.Globalization.Collation.CharacterGroupings("zh-CN");
    private CancellationTokenSource? _filterCts;
    private CancellationTokenSource? _extractCts;
    private RepkgCliService? _extractService;
    private int _extractTotalCount;
    private int _extractCompletedCount;
    private HashSet<string> _extractCompletedNames = [];
    public IAsyncRelayCommand OpenSelectedFoldersCommand { get; }
    public IAsyncRelayCommand<WallpaperItem?> DeleteSelectedCommand { get; }
    public IAsyncRelayCommand ExtractSelectedCommand { get; }
    public IAsyncRelayCommand UnsubscribeSelectedCommand { get; }
    private bool _isWallpaperItemTapped = false;
    private string _searchText = string.Empty;
    private bool _isLeftMouseButtonPressed = false;
    private AppBarButton? _pressedButton; // 当前被按下的 CommandBar 按钮(指针捕获后释放弹回用)
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

    private string _extractTitleText = "";
    public string ExtractTitleText
    {
        get => _extractTitleText;
        set
        {
            if (_extractTitleText != value)
            {
                _extractTitleText = value;
                OnPropertyChanged();
            }
        }
    }

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
                UpdateAllVisibleCheckBoxes();
            }
        }
    }
    public IAsyncRelayCommand<WallpaperItem> DeleteWallpaperCommand { get; } = null!;

    /// <summary>取消订阅按钮是否可用（单选/多选中包含创意工坊壁纸）</summary>
    public bool IsUnsubscribeEnabled
    {
        get
        {
            if (SelectedWallpapers.Count > 0)
                return SelectedWallpapers.Any(w => w.Source == "workshop");
            return ViewModel?.SelectedWallpaper?.Source == "workshop";
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
                    OnPropertyChanged(nameof(IsUnsubscribeEnabled));
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

        // 虚拟化容器每次数据绑定(含回收复用)都触发——弥补 Loaded 在容器复用时不重发导致的模糊层缺失
        WallpapersGridView.ContainerContentChanging += (s, e) =>
        {
            if (e.Item is WallpaperItem changingItem &&
                FindDescendantGrid(e.ItemContainer, "ItemRootGrid") is Grid changingRoot)
            {
                // 先设原图组件(按类型:GIF→Skia,其余→静态图),后应用模糊——模糊会隐藏原图,顺序颠倒会被 UpdateSkiaGif 抵消
                UpdateSkiaGif(changingRoot, changingItem); // 实验分支:Skia 流式播放(GIF 时覆盖 BitmapImage)
                UpdateItemBlur(changingRoot, changingItem);
                UpdateTagBadge(changingRoot, changingItem); // 角标按当前标签模式设置(替代 x:Bind OneTime+重建)
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
                UpdateAllGridItemWidths();
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

            // GridView 首次布局后重算自适应列宽
            UpdateAllGridItemWidths();

            // 补位移动动画(Composition 隐式 Offset):列表项重排时平滑滑到新位置
            var reorderDuration = TimeSpan.FromMilliseconds(100);
            foreach (var gv in AllWallpaperGridViews)
                ItemsReorderAnimation.SetDuration(gv, reorderDuration);
        };

        OpenSelectedFoldersCommand = new AsyncRelayCommand(async () =>
        {
            HideWallpaperContextMenu();
            await ViewModel.PathManagementVM.OpenSelectedWallpapersFoldersAsync();
        });
        DeleteSelectedCommand = new AsyncRelayCommand<WallpaperItem?>(async item =>
        {
            HideWallpaperContextMenu();

            var itemsToDelete = ViewModel.SelectedWallpapers.Count > 0
            ? SelectedWallpapers.ToList()
            : ViewModel.SelectedWallpaper is not null ? [ViewModel.SelectedWallpaper] : [];

            if (itemsToDelete.Count == 0) return;
            bool confirmed = await DialogHelper.ShowConfirmDialogAsync("删除",
                $"确定要删除选中的 {itemsToDelete.Count} 个壁纸吗？\n\n可在日志中查看已删除标题。",
                "全部删除",
                "取消");
            if (!confirmed) return;

            foreach (var toDelete in itemsToDelete)
            {
                await DeleteItemAsync(toDelete, skipConfirm: itemsToDelete.Count > 1);
            }

            Log.Information("已删除 {Count} 个壁纸: {Titles}", itemsToDelete.Count,
                string.Join("; ", itemsToDelete.Select(w => w.Title ?? w.WorkshopID ?? "未知")));

            ViewModel.SelectedWallpaper = null;
        });

        ExtractSelectedCommand = new AsyncRelayCommand(async () =>
        {
            HideWallpaperContextMenu();
            await ExtractSelectedWallpapersAsync();
        });

        UnsubscribeSelectedCommand = new AsyncRelayCommand(async () =>
        {
            HideWallpaperContextMenu();

            var itemsToUnsubscribe = SelectedWallpapers.Count > 0
                ? SelectedWallpapers.Where(w => w.Source == "workshop").ToList()
                : ViewModel.SelectedWallpaper is WallpaperItem wp && wp.Source == "workshop"
                    ? [wp]
                    : [];

            if (itemsToUnsubscribe.Count == 0) return;

            bool confirmed = await DialogHelper.ShowConfirmDialogAsync(
                "取消订阅",
                $"确定要取消订阅选中的 {itemsToUnsubscribe.Count} 个创意工坊壁纸吗？\n\n操作将同步删除本地的壁纸文件。",
                "确定",
                "取消");
            if (!confirmed) return;

            await UnsubscribeWallpapersAsync(itemsToUnsubscribe);
        });

        _pickerService = new PickerService();
    }
    private void SelectedWallpapers_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RefreshDisplayedSelectedWallpapers();
        UpdateStackVisuals();
        OnPropertyChanged(nameof(IsUnsubscribeEnabled));
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

    /// <summary>遍历可见容器重启 GIF 播放+角标(页面缓存切回时;容器未就绪/无项时无害)</summary>
    private void RestartVisibleGifPlayback()
    {
        // 非反射(AOT 兼容):ItemsPanelRoot 返回类型是 Panel(基类),Children 是 Panel 属性,直接访问即可,不强转 ItemsWrapGrid
        if (WallpapersGridView.ItemsPanelRoot is not { } panelRoot) return;
        foreach (var child in panelRoot.Children)
        {
            if (child is not GridViewItem container) continue;
            if (container.ContentTemplateRoot is not Grid root) continue;
            if (WallpapersGridView.ItemFromContainer(container) is WallpaperItem item)
            {
                UpdateSkiaGif(root, item);
                UpdateTagBadge(root, item);
            }
        }
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

    public async Task RefreshWallpaperList()
    {
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

            await ApplyFilters();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex,"筛选结果时出现异常。");
        }
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

    private async Task ApplyFilters()
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
                // 翻页(页码变化)整页替换:Reset 无动画;同页筛选:增量 diff,动画只作用于真实变化的项
                bool pageChanged = CurrentPage != pageBefore;

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

    // ============= GridView 列表辅助(自适应列宽/滚动条/回顶) =============

    /// <summary>三个壁纸 GridView(图标/内容/列表模式)</summary>
    private GridView[] AllWallpaperGridViews => new[] { WallpapersGridView, WallpapersContentGridView, WallpapersListGridView };

    /// <summary>窗口尺寸变化:实时重算 ItemWidth(布局脏标记同帧合并,138 项毫秒级;拖动中列数与拉伸同步更新)</summary>
    private void WallpaperGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateAllGridItemWidths();

    // ============ 预览内容过滤(高斯模糊) ============

    /// <summary>模糊位图缓存(按预览路径,GPU 生成一次复用;138 张中仅露骨壁纸会进缓存)</summary>
    private readonly Dictionary<string, BitmapImage> _blurCache = [];

    /// <summary>该壁纸在当前预览模糊开关下是否需要模糊:勾选哪个年龄段,该年龄段分级的壁纸就模糊</summary>
    private bool ShouldBlurPreview(WallpaperItem item)
    {
        return item.ContentRating?.ToLower() switch
        {
            "everyone" => ViewModel.WallpaperDisplayVM.BlurEveryone,
            "questionable" => ViewModel.WallpaperDisplayVM.BlurTeen,
            "mature" => ViewModel.WallpaperDisplayVM.BlurAdult,
            _ => false
        };
    }

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

    private async Task ShowBlurOverlayAsync(Image blurOverlay, WallpaperItem item, Action restoreRawPreviews)
    {
        try
        {
            if (string.IsNullOrEmpty(item.Preview) || !File.Exists(item.Preview))
            {
                restoreRawPreviews();
                return;
            }
            var blurred = await GetBlurredPreviewAsync(item.Preview);
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
            blurOverlay.Source = blurred;
            blurOverlay.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            restoreRawPreviews();
            Log.Warning(ex, "创建预览模糊层失败: {Path}", item.Preview);
        }
    }

    /// <summary>Win2D GPU 高斯模糊:加载原图 → 降采样到 480 内 → GaussianBlurEffect → PNG 流 → BitmapImage(缓存复用)</summary>
    private async Task<BitmapImage?> GetBlurredPreviewAsync(string previewPath)
    {
        if (_blurCache.TryGetValue(previewPath, out var cached)) return cached;

        var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
        using var canvasBitmap = await Microsoft.Graphics.Canvas.CanvasBitmap.LoadAsync(device, previewPath);

        // 先缩放到 480 内再模糊:模糊半径按目标分辨率折算(在原图上模糊 18px 相对 1920 宽几乎不可见)
        var src = canvasBitmap.SizeInPixels;
        float scale = Math.Min(1f, 480f / Math.Max(src.Width, src.Height));
        int w = Math.Max(1, (int)(src.Width * scale));
        int h = Math.Max(1, (int)(src.Height * scale));

        var scaleEffect = new Microsoft.Graphics.Canvas.Effects.Transform2DEffect
        {
            Source = canvasBitmap,
            TransformMatrix = System.Numerics.Matrix3x2.CreateScale(scale)
        };
        // BorderEffect(clamp)让模糊在图像边缘也能采样到延伸像素,避免"中心糊边缘清晰"的不均匀
        var borderEffect = new Microsoft.Graphics.Canvas.Effects.BorderEffect
        {
            Source = scaleEffect,
            ExtendX = Microsoft.Graphics.Canvas.CanvasEdgeBehavior.Clamp,
            ExtendY = Microsoft.Graphics.Canvas.CanvasEdgeBehavior.Clamp
        };
        var blurEffect = new Microsoft.Graphics.Canvas.Effects.GaussianBlurEffect
        {
            BlurAmount = 26f,
            BorderMode = Microsoft.Graphics.Canvas.Effects.EffectBorderMode.Hard,
            Optimization = Microsoft.Graphics.Canvas.Effects.EffectOptimization.Speed,
            Source = borderEffect
        };

        using var target = new Microsoft.Graphics.Canvas.CanvasRenderTarget(device, w, h, 96);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Microsoft.UI.Colors.Transparent);
            ds.DrawImage(blurEffect);
        }

        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await target.SaveAsync(stream, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png);
        stream.Seek(0);
        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(stream);
        _blurCache[previewPath] = bmp;
        return bmp;
    }

    /// <summary>年龄段设置变化:遍历当前页所有可见卡片刷新模糊层</summary>
    private void RefreshAllItemBlurs()
    {
        foreach (var item in Wallpapers)
        {
            if (WallpapersGridView.ContainerFromItem(item) is FrameworkElement container)
            {
                var itemRootGrid = FindDescendantGrid(container, "ItemRootGrid");
                if (itemRootGrid != null) UpdateItemBlur(itemRootGrid, item);
            }
        }

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

    /// <summary>按各模式档位重算 ItemWidth,复刻 UniformGridLayout 的"拉伸填满行尾"</summary>
    private void UpdateAllGridItemWidths()
    {
        // 间距来自容器 Margin(ItemContainerStyle 左右各 5,共 10),在槽位内部;槽位宽 = available/列数 填满整行,行尾零留白
        UpdateGridItemWidth(WallpapersGridView, ViewModel.WallpaperDisplayVM.WallpaperListMinWidth, 10);
        UpdateGridItemWidth(WallpapersListGridView, 400, 10);
        UpdateGridItemWidth(WallpapersContentGridView, 0, 10); // 内容模式单列
    }

    /// <param name="itemMarginTotal">容器左右 Margin 总和(判断列数用;ItemWidth 不扣除,卡片占满槽位)</param>
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
        // 故留 1px/列余量;卡片 = available/cols - 11,行尾余量 cols×1px 不可见
        panelRoot.SetValue(ItemsWrapGrid.ItemWidthProperty, available / cols - 2);
    }

    /// <summary>当前可见的 GridView(滚动回顶用)</summary>
    private GridView? GetVisibleGridView()
    {
        foreach (var gv in AllWallpaperGridViews)
            if (gv.Visibility == Visibility.Visible)
                return gv;
        return null;
    }

    /// <summary>可见 GridView 滚动回顶(分页/刷新后)</summary>
    private void ScrollVisibleGridToTop()
    {
        if (GetVisibleGridView() is GridView gv && FindScrollViewer(gv) is ScrollViewer sv)
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

    private void UpdateAllVisibleCheckBoxes()
    {
        foreach (var gv in AllWallpaperGridViews)
        {
            if (gv == null) continue;
            for (int i = 0; i < gv.Items.Count; i++)
            {
                if (gv.ContainerFromIndex(i) is not GridViewItem container) continue;
                // 容器 Content = DataTemplate 根(Grid,DataContext = WallpaperItem)
                if (container.Content is Grid grid && grid.DataContext is WallpaperItem item)
                    UpdateItemCheckBoxOpacity(grid, item);
            }
        }
    }
    private void WallpaperList_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_isWallpaperItemTapped == true)
        {
            _isWallpaperItemTapped = false;
            return;
        }

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
                    // 拖拽滑过 = 连选:只选中(加入集合),不翻转——避免与 PointerPressed 的双重翻转抵消
                    if (!item.IsSelected)
                    {
                        item.IsSelected = true;
                        if (!SelectedWallpapers.Contains(item))
                            SelectedWallpapers.Add(item);
                    }
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

            // 真正置顶:GridView 的绘制顺序由 GridViewItem 容器的 Canvas.ZIndex 决定(设 ItemContainer 不生效)
            DependencyObject topContainer = grid;
            while (topContainer != null && topContainer is not GridViewItem)
            {
                topContainer = VisualTreeHelper.GetParent(topContainer);
            }
            if (topContainer is GridViewItem gridViewItem)
            {
                Canvas.SetZIndex(gridViewItem, 10000);
            }

            if (_isLeftMouseButtonPressed && grid.DataContext is WallpaperItem item)
            {
                Item_PointerPressed(sender,e);
                if (!_isMultiSelectMode)
                {
                    // 拖拽滑过时更新预览图和标题，但不播放钻入动画避免卡顿
                    ViewModel.SelectedWallpaper = item;
                }
                if (IsMultiSelectMode)
                {
                    // 拖拽滑过 = 连选:只选中(加入集合),不翻转——避免与 PointerPressed 的双重翻转抵消
                    if (!item.IsSelected)
                    {
                        item.IsSelected = true;
                        if (!SelectedWallpapers.Contains(item))
                            SelectedWallpapers.Add(item);
                    }
                    UpdateMultiSelectCount();
                }
                return;
            }

            if (ViewModel.WallpaperDisplayVM.IsWallpaperEnterAnimationEnabled)
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
        if (sender is Grid grid && grid.DataContext is WallpaperItem item)
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

            // 捕获 GridViewItem 容器用于复位置顶
            DependencyObject capturedContainer = grid;
            while (capturedContainer != null && capturedContainer is not GridViewItem)
            {
                capturedContainer = VisualTreeHelper.GetParent(capturedContainer);
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
                if (capturedContainer is GridViewItem capturedGridViewItem)
                {
                    Canvas.SetZIndex(capturedGridViewItem, 0);
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

    private void Item_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            _isWallpaperItemTapped = true;

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

        // 设置中心点 (280 / 2 = 140)
        imageVisual.CenterPoint = new Vector3(140f, 140f, 0f);

        // 创建缩放动画 (从 0.8 放大到 1.0)
        var scaleAnim = compositor.CreateScalarKeyFrameAnimation();
        scaleAnim.InsertKeyFrame(0.0f, 0.85f); // 起始稍微缩小
        scaleAnim.InsertKeyFrame(1.0f, 1.0f);  // 钻入到正常大小
        scaleAnim.Duration = TimeSpan.FromMilliseconds(400);
        scaleAnim.Target = "Scale.X";

        // 创建透明度动画
        var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
        opacityAnim.InsertKeyFrame(0.0f, 0.0f);
        opacityAnim.InsertKeyFrame(0.2f, 1.0f); // 快速显现
        opacityAnim.Duration = TimeSpan.FromMilliseconds(400);

        // 启动动画
        imageVisual.StartAnimation("Scale.X", scaleAnim);
        imageVisual.StartAnimation("Scale.Y", scaleAnim);
        imageVisual.StartAnimation("Opacity", opacityAnim);
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

    private void SelectAllWallpapers_Click(object sender, RoutedEventArgs e)
    {
        // 先填充选中集合,后进多选模式:Toggle(true) 期间若 Count==0,会被 UpdateMultiSelectCount
        // 的"0 项自动退出"立刻翻回 false,导致首次全选面板被收起、需按两次(旧顺序的根因)
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
            }
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
    private void SelectAllWallpaper_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        // 先填集合后进模式,原因同 SelectAllWallpapers_Click
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
            // 动画不依赖复制结果,即使复制抛异常/无选中项也执行
            await PlayCopyCheckAnimationAsync();
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
            await PlayCopyCheckAnimationAsync();
        }
    }

    // 复制成功反馈(单 FontIcon 序列):淡出 → 切勾 → 从左往右扫出 → 停留 → 淡出 → 切回复制 → 淡入
    private int _copyCheckAnimationGeneration;
    private Microsoft.UI.Composition.InsetClip? _copyCheckClip; // 勾扫出的 clip

    private async Task PlayCopyCheckAnimationAsync()
    {
        int gen = ++_copyCheckAnimationGeneration;

        // 点击处理器同一帧做了大量同步变更,此帧内 StartAnimation 会被 Composition
        // 帧调度丢弃/延迟(项目已定位根因)。整体包进 DispatcherQueue.TryEnqueue 排到下一个空闲帧起跑。
        var tcs = new TaskCompletionSource();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (gen != _copyCheckAnimationGeneration) { tcs.TrySetResult(); return; } // 排队期间已作废

            var visual = ElementCompositionPreview.GetElementVisual(ToolbarCopyIcon);
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
        ToolbarCopyIcon.Glyph = "\uE73E";

        // 勾从左往右扫出(InsetClip RightInset 20→0)
        var tcs2 = new TaskCompletionSource();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (gen != _copyCheckAnimationGeneration) { tcs2.TrySetResult(); return; }

            var visual = ElementCompositionPreview.GetElementVisual(ToolbarCopyIcon);
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
            var visual = ElementCompositionPreview.GetElementVisual(ToolbarCopyIcon);
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
            var visual = ElementCompositionPreview.GetElementVisual(ToolbarCopyIcon);
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
        ToolbarCopyIcon.Glyph = "\uE8C8";
        _copyCheckClip?.StopAnimation("RightInset");
        _copyCheckClip = null;
        var v = ElementCompositionPreview.GetElementVisual(ToolbarCopyIcon);
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
        ExtractTitleText = "正在导入壁纸编辑器";
        ExtractProgress = 0;
        ExtractSubText = "";
        ExtractEntryText = "";
        SetExtractPreviewImage(item.Preview, item.Title ?? item.WorkshopID ?? "壁纸");
        OnPropertyChanged(nameof(ExtractTitleText));
        OnPropertyChanged(nameof(ExtractProgress));
        OnPropertyChanged(nameof(ExtractProgressText));
        OnPropertyChanged(nameof(ExtractSubText));
        OnPropertyChanged(nameof(ExtractEntryText));
        OnPropertyChanged(nameof(ExtractEntryVisibility));
        ExtractState = ExtractState.Running;
        IsExtracting = true;

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
                        }
                        if (action == "完成")
                        {
                            _extractCompletedCount = 1;
                            ExtractProgress = 100;
                            OnPropertyChanged(nameof(ExtractProgress));
                            OnPropertyChanged(nameof(ExtractProgressText));
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
            NotificationService.NotifyIfUnfocused("导入到编辑器失败", $"导入失败:{item.Title}");
        }
        finally
        {
            _extractService = null;
            _extractCts = null;
            IsExtracting = false;
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
        if (DeleteSelectedCommand == null) return;

        try
        {
            if (IsMultiSelectMode)
            {
                await DeleteSelectedCommand.ExecuteAsync(null);
            }
            else
            {
                await DeleteSelectedCommand.ExecuteAsync(ViewModel.SelectedWallpaper);
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
            // 优化:不再重置 ItemsSource 重建全列表——只遍历可见容器手动刷新角标(滚动位置保留、无容器 churn)
            // 非反射(AOT 兼容):ItemsPanelRoot 返回类型是 Panel(基类),Children 是 Panel 属性,直接访问即可,不强转 ItemsWrapGrid
            if (WallpapersGridView.ItemsPanelRoot is not { } panelRoot) return;
            foreach (var child in panelRoot.Children)
            {
                if (child is not GridViewItem container) continue;
                if (container.ContentTemplateRoot is not Grid root) continue;
                if (WallpapersGridView.ItemFromContainer(container) is WallpaperItem item)
                    UpdateTagBadge(root, item);
            }
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
                tb.Text = new PapersTagContentChoose().Convert(item, null, "", "") as string ?? "";
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
        ViewModel.SuspendSelectedWallpapersCollectionChanged();
        SelectedWallpapers.CollectionChanged -= SelectedWallpapers_CollectionChanged;
        var currentlySelected = SelectedWallpapers.ToList();
        foreach (var item in Wallpapers)
        {
            item.IsSelected = !item.IsSelected;
        }
        SelectedWallpapers.Clear();
        foreach (var item in Wallpapers)
        {
            if (item.IsSelected)
                SelectedWallpapers.Add(item);
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

            // 判断单/多模式
            _isSingleExtract = itemsToExtract.Count == 1;
            ExtractWallpaperList.Visibility = _isSingleExtract ? Visibility.Collapsed : Visibility.Visible;
            OnPropertyChanged(nameof(ExtractPreviewVisibility));
            ExtractTitleText = "正在提取";
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
                        }
                        else if (action == "跳过(已提取)")
                        {
                            ExtractProgress = 100;
                            ExtractSubText = "壁纸已提取，跳过";
                        }
                        else if (action == "完成")
                        {
                            ExtractProgress = 100;
                            ExtractSubText = "提取完成";
                            if (_extractCompletedNames.Add(name))
                            {
                                _extractCompletedCount = 1;
                                OnPropertyChanged(nameof(ExtractProgressText));
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
                OnlyPaths = ViewModel.OnlyPaths,
                OnlyPathsList = ViewModel.OnlyPathsList,
                IgnorePaths = ViewModel.IgnorePaths,
                IgnorePathsList = ViewModel.IgnorePathsList,
                MaxConcurrentExtractions = ViewModel.MaxConcurrentExtractions,
                ProcessPriority = ViewModel.ProcessPriority,
                SkipExistingOutput = ViewModel.OneFolder == 1 ? ViewModel.SkipExistingOutput : false,
                LazyLoad = ViewModel.LazyLoad,
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
                Log.Information("提取被用户停止");
                NotificationService.NotifyIfUnfocused("提取已停止", "提取已停止");
            }
        }
        catch (OperationCanceledException)
        {
            ExtractStatus = "提取已停止";
            ExtractState = ExtractState.Completed;
            IsExtracting = false;
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
            NotificationService.NotifyIfUnfocused("提取失败", "提取失败，请查看日志");
        }

        // 收尾:清空进度列表(完成/取消/异常三路都经过这里)
        _extractProgressByName = [];
        ExtractProgressItems.Clear();
    }

    private void PauseExtractButton_Click(object sender, RoutedEventArgs e)
    {
        _extractService?.Pause();
        ExtractState = ExtractState.Paused;
        ExtractStatus = "已暂停";
    }

    private void ResumeExtractButton_Click(object sender, RoutedEventArgs e)
    {
        _extractCts?.Dispose();
        _extractCts = new CancellationTokenSource();
        _extractService?.Resume();
        ExtractState = ExtractState.Running;
        ExtractStatus = "正在提取...";
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
        AnimateExtractPanelClose(() =>
        {
            ExtractOverlayVisibility = Visibility.Collapsed;
            ExtractState = ExtractState.Idle;
        });
    }

    private async Task DeleteItemAsync(WallpaperItem item, bool skipConfirm = false)
    {
        if (item == null || item.WorkshopID == null || item.FolderPath == null) return;

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
    private async Task UnsubscribeWallpapersAsync(List<WallpaperItem> items)
    {
        var service = SteamWorkshopService.GetInstance();
        if (!service.IsAvailable)
        {
            await DialogHelper.ShowMessageAsync(
                "Steamworks 初始化失败",
                "无法连接到 Steam，请确认 Steam 已在运行。\n\n如果问题持续，请尝试以管理员身份运行本程序。");
            return;
        }

        int success = 0;
        foreach (var item in items)
        {
            if (ulong.TryParse(item.WorkshopID, out var wid))
            {
                if (await service.UnsubscribeAsync(wid))
                    success++;
            }
        }

        await DialogHelper.ShowMessageAsync("取消订阅完成",
            $"成功向 Steam 发送取消订阅请求: {success}/{items.Count} 个壁纸。\n\n正在同步删除本地壁纸文件...");

        // 删除本地文件并清出列表（沿用 DeleteItemAsync 的逻辑）
        foreach (var item in items)
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

    /// <summary>右键菜单打开时刷新「备份」按钮状态（已备份→禁用+文案切换）。</summary>
    private void UpdateBackupButtonState()
    {
        if (BackupWallpaperButton is not AppBarButton btn) return;

        var items = GetBackupableItems();
        var workshopPath = ViewModel?.PathManagementVM?.WorkshopPath;

        if (items.Count == 0 || string.IsNullOrEmpty(workshopPath) || !Directory.Exists(workshopPath))
        {
            btn.IsEnabled = false;
            btn.Label = LanguageHelper.GetResource("AppBarButton_Backup.Label");
            return;
        }

        bool allBackedUp = items.All(i => !string.IsNullOrEmpty(i.WorkshopID)
            && BackupService.IsBackedUp(workshopPath, i.WorkshopID!));
        btn.IsEnabled = !allBackedUp;
        btn.Label = allBackedUp
            ? LanguageHelper.GetResource("AppBarButton_BackupDone.Label")
            : LanguageHelper.GetResource("AppBarButton_Backup.Label");
    }

    private async void BackupWallpaper_Click_ByCommandBarFlyout(object sender, RoutedEventArgs e)
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
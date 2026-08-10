using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using WE_Tool.Helper;
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
    private Dictionary<string, ExtractProgressItem> _extractItemDict = [];
    public IAsyncRelayCommand OpenSelectedFoldersCommand { get; }
    public IAsyncRelayCommand<WallpaperItem?> DeleteSelectedCommand { get; }
    public IAsyncRelayCommand ExtractSelectedCommand { get; }
    public IAsyncRelayCommand UnsubscribeSelectedCommand { get; }
    private bool _isWallpaperItemTapped = false;
    private string _searchText = string.Empty;
    private bool _isLeftMouseButtonPressed = false;
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
    public ObservableCollection<ExtractProgressItem> ExtractItems { get; } = [];

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
    public IAsyncRelayCommand<WallpaperItem> DeleteWallpaperCommand { get; }

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
        };

        ViewModel.PropertyChanged += (s, e) =>
        {
            if (ViewModel._isBatchUpdating) return;

            if (e.PropertyName == nameof(SettingsViewModel.IsPropertyPanelLoading))
            {
                UpdatePropertyPanelState();
                return;
            }

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
                    SingleSelectionInfoPanel.Visibility = ViewModel.SelectedWallpaper != null
                        ? Visibility.Visible : Visibility.Collapsed;
                    NoSelectionHintText.Visibility = ViewModel.SelectedWallpaper != null
                        ? Visibility.Collapsed : Visibility.Visible;
                    UpdatePropertyPanelState();
                }
                return;
            }

            _ = ApplyFilters();
        };

        ViewModel.SelectedWallpaperProperties.CollectionChanged += (s, e) => UpdatePropertyPanelState();
        SelectedWallpapers.CollectionChanged += SelectedWallpapers_CollectionChanged;

        ViewModel.FilterExpanderVM.PropertyChanged += (s, e) =>
        {
            if (ViewModel._isBatchUpdating) return;
            _ = ApplyFilters();
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
            _ = ApplyFilters();
        };

        this.Loaded += async (s, e) =>
        {
            App.ScanCompleted += App_ScanCompleted;

            if (_isFirstLoad)
            {
                _isFirstLoad = false;
                await ViewModel.InitializeAsync();
                await RefreshWallpaperList();
            }

            var presenter = WallpapersScrollView.ScrollPresenter;
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

            if (item != null)
            {
                await DeleteItemAsync(item);
            }

            foreach (var toDelete in itemsToDelete)
            {
                await DeleteItemAsync(toDelete, skipConfirm: itemsToDelete.Count > 1);
            }

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

        Log.Information("正在检查变量: ", nameof(WallpapersScrollView.ScrollPresenter.VerticalScrollController));
    }
    /// <summary>
    /// 属性面板底部区状态切换（公共区/详情区由模式绑定与选中切换负责）：
    /// 加载中→空白；加载完→属性列表或"没有可配置属性"占位。
    /// </summary>
    private void UpdatePropertyPanelState()
    {
        bool hasSelection = ViewModel.SelectedWallpaper != null;
        bool hasProps = ViewModel.SelectedWallpaperProperties.Count > 0;
        bool loading = ViewModel.IsPropertyPanelLoading;

        PropertyPanelEmptyHint.Visibility = (hasSelection && !loading && !hasProps) ? Visibility.Visible : Visibility.Collapsed;
        // 加载期间列表也保持可见:增量填充的每批会立即布局渲染,把"显示瞬间的全量布局"分摊到填充过程,切换大壁纸不冻结
        PropertyItemsControl.Visibility = (hasSelection && hasProps) ? Visibility.Visible : Visibility.Collapsed;
        PropertySaveButton.IsEnabled = hasSelection && !loading && hasProps;
        PropertySaveButton.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void PropertySaveButton_Click(object sender, RoutedEventArgs e)
    {
        var (ok, error) = await ViewModel.SaveSelectedWallpaperPropertiesAsync();
        if (ok)
            await DialogHelper.ShowMessageAsync("保存属性", "属性已保存到 project.json。");
        else
            await DialogHelper.ShowMessageAsync("保存失败", error ?? "未知错误");
    }

    private void SelectedWallpapers_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RefreshDisplayedSelectedWallpapers();
        UpdateStackVisuals();
        OnPropertyChanged(nameof(IsUnsubscribeEnabled));
    }
    private void UpdateStackVisuals()
    {
        int count = DisplayedSelectedWallpapers.Count;
        for (int i = 0; i < count; i++)
        {
            var container = StackedImagesControl.ContainerFromIndex(i) as FrameworkElement;
            if (container == null) continue;

            container.Visibility = Visibility.Visible;
            ApplyStackAnimation(container, i);
            Canvas.SetZIndex(container, i);
        }
    }
    private void StackedImage_Loaded(object sender, RoutedEventArgs e)
    {
        // 从 SelectedWallpapers 集合计算相对位置
        if (sender is FrameworkElement fe && fe.DataContext is WallpaperItem item)
        {
            int idx = SelectedWallpapers.IndexOf(item);
            if (idx < 0) return;
            int relative = Math.Max(0, idx - Math.Max(0, SelectedWallpapers.Count - 5));
            fe.Visibility = Visibility.Visible;
            ApplyStackAnimation(fe, relative);
            Canvas.SetZIndex(fe, relative);
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
    }
    private void Global_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isLeftMouseButtonPressed = false;
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

    private async void ToggleMultiSelectVisuals(bool isMulti)
    {
        CancelAllAnimations();

        if (isMulti)
        {
            // 如果单选有焦点，顺便加入多选
            if (ViewModel.SelectedWallpaper != null && !SelectedWallpapers.Contains(ViewModel.SelectedWallpaper))
            {
                SelectedWallpapers.Add(ViewModel.SelectedWallpaper);
                RefreshDisplayedSelectedWallpapers(forceRebuild: true);
            }
            else if (SelectedWallpapers.Count > 0)
            {
                RefreshDisplayedSelectedWallpapers(forceRebuild: true);
            }

            // 1. 核心视觉：缩小 + 圆角
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
            SelectedWallpapers.CollectionChanged -= SelectedWallpapers_CollectionChanged;
            ViewModel.SuspendSelectedWallpapersCollectionChanged();

            SinglePreviewBorder.Visibility = Visibility.Visible;
            SingleSelectionInfoPanel.Visibility = ViewModel.SelectedWallpaper != null
                ? Visibility.Visible : Visibility.Collapsed;
            NoSelectionHintText.Visibility = ViewModel.SelectedWallpaper != null
                ? Visibility.Collapsed : Visibility.Visible;

            StackedImagesControl.Visibility = Visibility.Collapsed;
            MultiSelectionInfoPanel.Visibility = Visibility.Collapsed;

            var visual = ElementCompositionPreview.GetElementVisual(SinglePreviewBorder);
            visual.CenterPoint = new Vector3((float)SinglePreviewBorder.ActualWidth / 2, (float)SinglePreviewBorder.ActualHeight / 2, 0f);

            // 缩放回 1.0 (250px)
            var scaleReturnAnim = visual.Compositor.CreateSpringVector3Animation();
            scaleReturnAnim.Target = "Scale";
            scaleReturnAnim.FinalValue = new Vector3(1.0f, 1.0f, 1.0f);
            scaleReturnAnim.DampingRatio = 0.7f; // 略微有弹性的恢复
            scaleReturnAnim.Period = TimeSpan.FromMilliseconds(50);

            // 位置归零 (防止在堆叠时有微小的 Offset)
            var offsetReturnAnim = visual.Compositor.CreateSpringVector3Animation();
            offsetReturnAnim.Target = "Offset";
            offsetReturnAnim.FinalValue = new Vector3(0f, 0f, 0f);
            offsetReturnAnim.DampingRatio = 1.0f; // 平滑归位

            SinglePreviewBorder.CornerRadius = new CornerRadius(0);

            // 启动动画
            visual.StartAnimation("Scale", scaleReturnAnim);
            visual.StartAnimation("Offset", offsetReturnAnim);


            foreach (var wp in SelectedWallpapers)
            {
                wp.IsSelected = false;
            }
            SelectedWallpapers.Clear();

            SelectedWallpapers.CollectionChanged += SelectedWallpapers_CollectionChanged;
            ViewModel.ResumeSelectedWallpapersCollectionChanged();

            RefreshDisplayedSelectedWallpapers(forceRebuild: true);
            UpdateMultiSelectCount();

            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
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

            Wallpapers.Clear();
            DispatcherQueue.TryEnqueue(() =>
            {
                SelectedWallpapers.Clear();
                IsMultiSelectMode = false;
                ViewModel.SelectedWallpaper = null;
            });

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
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _searchText = sender.Text;
            _ = ApplyFilters();
        }
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

                    var rawTag = w.Tags ?? "";
                    var normalizedTag = rawTag.Replace(" ", "").Replace("-", "");
                    bool tagsMatch = selectedTags.Count > 0 && selectedTags.Contains(normalizedTag);

                    bool searchMatch = string.IsNullOrWhiteSpace(_searchText) ||
                                        (w.Title?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ?? false);

                    return typeMatch && ratingMatch && tagsMatch && source && searchMatch;
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
                    NoScanResultTip.Visibility = Visibility.Visible;
                    NoResultTip.Visibility = Visibility.Collapsed;
                });
                return;
            }

            // === 分页 ===
            bool listUnchanged = IsListEqual(_filteredWallpapers, filteredResult);
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
                Wallpapers.Clear();

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_allWallpapers.Count == 0)
                    {
                        NoScanResultTip.Visibility = Visibility.Visible;
                        NoResultTip.Visibility = Visibility.Collapsed;
                    }
                    else if (filteredResult.Count == 0)
                    {
                        NoScanResultTip.Visibility = Visibility.Collapsed;
                        NoResultTip.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        NoScanResultTip.Visibility = Visibility.Collapsed;
                        NoResultTip.Visibility = Visibility.Collapsed;
                    }
                });

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested) return;
                    foreach (var item in pageItems)
                    {
                        Wallpapers.Add(item);
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
        WallpapersScrollView.ScrollTo(0, 0);
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

    private void SortDirectionToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.WallpaperDisplayVM.IsSortAscending = !ViewModel.WallpaperDisplayVM.IsSortAscending;
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
            if (casterElement is Grid grid && grid.DataContext is WallpaperItem item)
            {
                UpdateItemCheckBoxOpacity(grid, item);
            }
        }
    }

    private async void ResetFilter_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ResetFiltersAsync(1,true);
    }
    private async void SelectAllTags_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ResetFiltersAsync(2, true);
    } 
    private async void DeselectAllTags_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ResetFiltersAsync(2, false);
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
        foreach (var repeater in new ItemsRepeater[] { WallpapersRepeater, WallpapersContentRepeater, WallpapersListRepeater })
        {
            if (repeater == null || repeater.ItemsSourceView == null) continue;
            for (int i = 0; i < repeater.ItemsSourceView.Count; i++)
            {
                var element = repeater.TryGetElement(i) as FrameworkElement;
                if (element == null) continue;
                var grid = element as Grid ?? FindChildGrid(element);
                if (grid?.DataContext is WallpaperItem item)
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
            if (!IsMultiSelectMode)
            {
                IsMultiSelectMode = true;
            }
            if (cb.IsChecked == true && !SelectedWallpapers.Contains(item))
            {
                SelectedWallpapers.Add(item);
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
                    var modifiers = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
                    if (modifiers && !_isMultiSelectMode)
                    {
                        IsMultiSelectMode = true;
                        item.IsSelected = !item.IsSelected;
                        if (item.IsSelected && !SelectedWallpapers.Contains(item))
                            SelectedWallpapers.Add(item);
                    }

                    if (_isMultiSelectMode)
                    {
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
                    item.IsSelected = !item.IsSelected;

                    if (item.IsSelected && !SelectedWallpapers.Contains(item))
                        SelectedWallpapers.Add(item);
                    else if (!item.IsSelected)
                        SelectedWallpapers.Remove(item);


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
                grid.Translation = new System.Numerics.Vector3(0f, 0f, 64f);
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
                    var modifiers = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
                    if (modifiers && !_isMultiSelectMode)
                    {
                        IsMultiSelectMode = true;
                        item.IsSelected = !item.IsSelected;

                        if (item.IsSelected && !SelectedWallpapers.Contains(item))
                            SelectedWallpapers.Add(item);
                    }

                    if (_isMultiSelectMode)
                    {
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
        if (!IsMultiSelectMode)
        {
            IsMultiSelectMode = true;
        }
        InternalSelectAllWallpapers();
    }
    private void InvertSelection_CLick(object sender, RoutedEventArgs e)
    {
        if (!IsMultiSelectMode)
        {
            IsMultiSelectMode = true;
        }
        InternalInvertSelection();
    }
    private void ChangeSort(object sender, RoutedEventArgs e)
    {
    }
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
                    SelectAllWallpaper_Accelerator_Invoked(null, null);
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
            Property_Accelerator_Invoked(null, null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Delete)
        {
            Delete_Accelerator_Invoked(null, null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.F5)
        {
            // F5 刷新（刷新进行中时由 WallpaperListRefresh_Click_ByCommandBarFlyout 内部防连按兜底）
            WallpaperListRefresh_Click_ByCommandBarFlyout(null, null);
            e.Handled = true;
        }
    }
    private void SelectAllWallpaper_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (!IsMultiSelectMode)
        {
            IsMultiSelectMode = true;
        }
        InternalSelectAllWallpapers();
    }
    private void InvertSelection_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (!IsMultiSelectMode)
        {
            IsMultiSelectMode = true;
        }
        InternalInvertSelection();
    }
    private async void Copy_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        await CopyWallpapersAsync();
    }
    private async void Copy_Click_ByCommandBarFlyout(object sender, RoutedEventArgs e)
    {
        HideWallpaperContextMenu();
        await CopyWallpapersAsync();
    }
    private async Task CopyWallpapersAsync()
    {
        var items = SelectedWallpapers.Count > 0
            ? SelectedWallpapers.ToList()
            : ViewModel?.SelectedWallpaper is not null ? [ViewModel.SelectedWallpaper] : [];

        if (items.Count == 0) return;

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
        if (folders.Count == 0) return;

        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetStorageItems(folders);
        Clipboard.SetContent(dataPackage);
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
        }
        catch (OperationCanceledException)
        {
            Log.Information("[导入编辑器] 用户取消");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[导入编辑器] 导入失败: {Name}", item.Title);
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
        foreach (var c in new[] { '<', '>', ':', '\"', '/', '\\', '|', '?', '*' })
            sb.Replace(c, '_');
        for (int i = 0; i < sb.Length; i++)
            if (invalid.Contains(sb[i])) sb[i] = '_';
        return sb.ToString().Trim();
    }

    private async void Delete_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
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
        if (!IsMultiSelectMode)
        {
            IsMultiSelectMode = true;
        }
        InternalSelectAllWallpapers();
    }
    private void InvertSelection_CLick_ByCommandBarFlyout(object sender, RoutedEventArgs e)
    {
        if (!IsMultiSelectMode)
        {
            IsMultiSelectMode = true;
        }
        InternalInvertSelection();
    }
    private bool _isRefreshing;

    private async void WallpaperListRefresh_Click_ByCommandBarFlyout(object sender, RoutedEventArgs e)
    {
        // 防连按：刷新进行中时忽略再次触发（按钮已禁用，F5/菜单入口由此兜底）
        if (_isRefreshing) return;
        _isRefreshing = true;
        RefreshButton.IsEnabled = false;

        try
        {
            App.StartBackgroundScan(ViewModel.PathManagementVM.WorkshopPath, ViewModel.PathManagementVM.OfficialPath, ViewModel.PathManagementVM.ProjectPath, ViewModel.PathManagementVM.AcfPath, ViewModel.PathManagementVM.VdfPath, ViewModel.AppSettingsVM.ScanCacheEnabled == "1");
            await RefreshWallpaperList();
        }
        finally
        {
            _isRefreshing = false;
            RefreshButton.IsEnabled = true;
        }
    }
    private void Property_Accelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        Properties();
        e.Handled = true;
    }
    private void Properties_Click_ByCommandBarFlyout(object sender, RoutedEventArgs e)
    {
        Properties();
    }
    private void Properties()
    {
        HideWallpaperContextMenu();
        if (ViewModel.SelectedWallpaper != null)
            PropertiesWindow.Open(ViewModel.SelectedWallpaper);
    }
    private async void OnIconSizeChanged(object sender, RoutedEventArgs e)
    {
        await Task.Delay(100);
        HideWallpaperContextMenu();
    }
    private void OnDisplayModeChanged(object sender, RoutedEventArgs e)
    {
        HideWallpaperContextMenu();
    }
    private async void OnTagDisplayChanged(object sender, RoutedEventArgs e)
    {
        HideWallpaperContextMenu();
        WallpapersRepeater.ItemsSource = null;
        WallpapersRepeater.ItemsSource = Wallpapers;
    }
    private async void ChangeAnnotatedScrollBarEnabled(object sender, RoutedEventArgs e)
    {
        HideWallpaperContextMenu();
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

        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
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
            _extractItemDict = [];

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
            }
            else
            {
                ExtractState = ExtractState.Completed;
                IsExtracting = false;
                ExtractStatus = "提取已停止";
                Log.Information("提取被用户停止");
            }
        }
        catch (OperationCanceledException)
        {
            ExtractStatus = "提取已停止";
            ExtractState = ExtractState.Completed;
            IsExtracting = false;
            Log.Information("提取被用户停止");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "提取失败");
            ExtractState = ExtractState.Completed;
            IsExtracting = false;
            ExtractStatus = "提取失败，请查看日志";
            ExtractProgress = 0;
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
    private void Papers_AnnotatedScrollBarControl_DetailLabelRequested(AnnotatedScrollBar sender, AnnotatedScrollBarDetailLabelRequestedEventArgs args)
    {
        // 如果列表为空，直接返回
        if (Wallpapers == null || Wallpapers.Count == 0) return;

        var presenter = WallpapersScrollView.ScrollPresenter;
        if (presenter == null) return;

        // 计算总可滚动高度 (总内容高度 - 视口可见高度)
        double maxOffset = presenter.ExtentHeight - presenter.ViewportHeight;
        if (maxOffset <= 0) return;

        // 计算当前滚动的百分比进度
        double fraction = args.ScrollOffset / maxOffset;

        // 严谨处理：限制比例在 0 到 1 之间，防止越界计算
        fraction = Math.Clamp(fraction, 0.0, 1.0);

        // 根据百分比映射到当前数据集合的 Index
        int index = (int)(fraction * (Wallpapers.Count - 1));
        index = Math.Clamp(index, 0, Wallpapers.Count - 1);

        var item = Wallpapers[index];

        // 根据当前的排序方式（SortOrder），动态决定悬浮标签应该显示什么内容
        string label = string.Empty;
        switch (ViewModel.WallpaperDisplayVM.SortOrder)
        {
            case 0: // 按名称排序
                label = string.IsNullOrWhiteSpace(item.Title) ? "#" : item.Title.Substring(0, 1).ToUpper();
                break;
            case 1: // 按订阅/创建时间排序
                label = item.CreationTime.ToString("yyyy/MM");
                break;
            case 2: // 按最后更新时间排序
                label = item.UpdateTime.ToString("yyyy/MM");
                break;
            case 3: // 按文件大小排序 (转换为 MB 显示)
                double mbSize = item.FileSize / 1048576.0;
                label = mbSize > 1024 ? $"{mbSize / 1024:F1} GB" : $"{mbSize:F0} MB";
                break;
            case 4: // 按修改时间排序
                label = item.AcfUpdateTime?.ToString("yyyy/MM") ?? "??";
                break;
            default:
                label = "•";
                break;
        }

        // 将计算好的标签赋值给事件参数
        args.Content = label;
    }

    // ... INotifyPropertyChanged 标准实现 ...
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

}
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
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Models;
using WE_Tool.Service;
using WE_Tool.ViewModels;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.System;
using Windows.UI.Core;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WE_Tool;

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class Papers : Page, INotifyPropertyChanged
{
    private readonly IPickerService _pickerService;
    private readonly WE_Tool.Converters.FileSizeToString _sizeConverter = new();
    private List<WallpaperItem> _allWallpapers = [];
    public SettingsViewModel ViewModel { get; }
    public ObservableCollection<WallpaperItem> Wallpapers { get; set; } = [];
    public ObservableCollection<WallpaperItem> SelectedWallpapers { get; set; } = [];
    public ObservableCollection<WallpaperItem> DisplayedSelectedWallpapers { get; } = [];
    private static readonly Windows.Globalization.Collation.CharacterGroupings _zhGroupings = new Windows.Globalization.Collation.CharacterGroupings("zh-CN");
    private CancellationTokenSource? _filterCts;
    public IAsyncRelayCommand OpenSelectedFoldersCommand { get; }
    public IAsyncRelayCommand<WallpaperItem?> DeleteSelectedCommand { get; }
    private string _searchText = string.Empty;
    private bool _isLeftMouseButtonPressed = false;
    private bool _isMultiSelectMode = false;
    private bool _isScanning = false;
    private Microsoft.UI.Xaml.Controls.Primitives.IScrollController? _originalVerticalScrollController;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _sizeChangedDebounceTimer;
    private static readonly FrozenDictionary<string, Func<SettingsViewModel, bool>> _tagGetters = new Dictionary<string, Func<SettingsViewModel, bool>>
    {
        ["Abstract"] = vm => vm.Abstract,
        ["Animal"] = vm => vm.Animal,
        ["Anime"] = vm => vm.Anime,
        ["Cartoon"] = vm => vm.Cartoon,
        ["Cgi"] = vm => vm.Cgi,
        ["Cyberpunk"] = vm => vm.Cyberpunk,
        ["Fantasy"] = vm => vm.Fantasy,
        ["Game"] = vm => vm.Game,
        ["Girls"] = vm => vm.Girls,
        ["Guys"] = vm => vm.Guys,
        ["Landscape"] = vm => vm.Landscape,
        ["Medieval"] = vm => vm.Medieval,
        ["Memes"] = vm => vm.Memes,
        ["Mmd"] = vm => vm.Mmd,
        ["Music"] = vm => vm.Music,
        ["Nature"] = vm => vm.Nature,
        ["Pixelart"] = vm => vm.Pixelart,
        ["Relaxing"] = vm => vm.Relaxing,
        ["Retro"] = vm => vm.Retro,
        ["SciFi"] = vm => vm.SciFi,
        ["Sports"] = vm => vm.Sports,
        ["Technology"] = vm => vm.Technology,
        ["Television"] = vm => vm.Television,
        ["Vehicle"] = vm => vm.Vehicle,
        ["Unspecified"] = vm => vm.Unspecified,
    }.ToFrozenDictionary();
    public  bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (_isScanning == value) return;
            _isScanning = value;
            OnPropertyChanged();

            // 在 UI 线程上更新覆盖层显示与交互拦截
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ScanOverlay != null && BackgroundProgressRing != null)
                {
                    ScanOverlay.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                    ScanOverlay.IsHitTestVisible = value; // 扫描时拦截点击，非扫描时允许交互
                    BackgroundProgressRing.IsActive = value;
                }
            });
        }
    }
    public IAsyncRelayCommand<WallpaperItem> DeleteWallpaperCommand { get; }
    public Papers()
    {
        ViewModel = new SettingsViewModel(new ConfigService(), new PickerService())
        {
            SelectedWallpapers = SelectedWallpapers
        };

        this.InitializeComponent();
        this.DataContext = this;
        _sizeChangedDebounceTimer = DispatcherQueue.CreateTimer();
        _sizeChangedDebounceTimer.Interval = TimeSpan.FromMilliseconds(500);
        _sizeChangedDebounceTimer.IsRepeating = false;
        _sizeChangedDebounceTimer.Tick += async (sender, args) =>
        {
            RefreshScrollBarLabels();
        };
        App.ScanCompleted += App_ScanCompleted;

        this.Unloaded += (s, e) => {
            App.ScanCompleted -= App_ScanCompleted;
        };

        this.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Global_PointerPressed), true);
        this.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Global_PointerReleased), true);
        this.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Global_PointerReleased), true);

        ViewModel.PropertyChanged += (s, e) => {
            if (ViewModel._isBatchUpdating) return;

            if (e.PropertyName == "SteamWorkshopPath" 
                || e.PropertyName?.EndsWith("Expander") == true
                || e.PropertyName?.Contains("Pane") == true
                || e.PropertyName == "SortIndex"
                || e.PropertyName == nameof(ViewModel.SelectedWallpaper))
                return;

            _ = ApplyFilters();
        };

        this.Loaded += async (s, e) =>
        {
            await ViewModel.InitializeAsync();
            await RefreshWallpaperList();

            var presenter = WallpapersScrollView.ScrollPresenter;
            _originalVerticalScrollController = presenter.VerticalScrollController;
            if (ViewModel.IsAnnotatedScrollBarEnabled && presenter != null)
            {
                presenter.VerticalScrollController = Papers_AnnotatedScrollBarControl.ScrollController;
            }
            else if (!ViewModel.IsAnnotatedScrollBarEnabled && presenter != null)
            {
                presenter.VerticalScrollController = _originalVerticalScrollController;
            }
        };

        OpenSelectedFoldersCommand = new AsyncRelayCommand(async () =>
        {
            HideWallpaperContextMenu();
            await ViewModel.OpenSelectedWallpapersFoldersAsync();
        });
        DeleteSelectedCommand = new AsyncRelayCommand<WallpaperItem?>(async item =>
        {
            HideWallpaperContextMenu();

            var itemsToDelete = SelectedWallpapers.Count > 0
            ? SelectedWallpapers.ToList()
            : item is not null ? [item] : [];

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
        });
        WallpapersScrollView.SizeChanged += (s, e) =>
        {
            // 使用 DispatcherQueue 确保在布局计算完成后再刷新标签
            DispatcherQueue.TryEnqueue(() =>
            {
                _sizeChangedDebounceTimer?.Stop();
                _sizeChangedDebounceTimer?.Start();
            });
        };
        
        _pickerService = new PickerService();
        SelectedWallpapers.CollectionChanged += SelectedWallpapers_CollectionChanged;

        Log.Information("正在检查变量: ", nameof(WallpapersScrollView.ScrollPresenter.VerticalScrollController));
    }
    private async void App_ScanCompleted(object? sender, EventArgs e)
    {
        await DispatcherQueue.EnqueueAsync(async () =>
        {
            await RefreshWallpaperList();
        });
    }
    private void SelectedWallpapers_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        RefreshDisplayedSelectedWallpapers();
        UpdateStackVisuals();
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
    public async Task RefreshWallpaperList()
    {
        bool scanRunning = App.ScanTask != null && !App.ScanTask.IsCompleted;
        if (scanRunning)
        {
            IsScanning = true;
        }

        try
        {
            if (App.ScanTask != null)
            {
                await App.ScanTask;
                _allWallpapers = [.. App.GlobalAllWallpapers];
            }
            else
            {
                App.StartBackgroundScan(ViewModel.WorkshopPath, ViewModel.OfficialPath, ViewModel.ProjectPath, ViewModel.AcfPath);
                await App.ScanTask;
                _allWallpapers = [.. App.GlobalAllWallpapers];
            }

            await ApplyFilters();
        }
        finally
        {
            if (scanRunning)
            {
                IsScanning = false;
            }
        }
    }

    private static bool IsListEqual(ObservableCollection<WallpaperItem> current, List<WallpaperItem> next)
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
            await Task.Delay(ViewModel.FilterResultResponseDelay, token);

            var selectedTags = GetSelectedTags();
            int sortIndex = ViewModel.SortOrder;
            bool isAscending = ViewModel.IsSortAscending;

            var filteredResult = await Task.Run(() =>
            {
                var query = _allWallpapers.Where(w =>
                {
                    bool typeMatch = false;
                    string t = w.Type?.ToLower() ?? string.Empty;
                    if (ViewModel.Scene && t == "scene") typeMatch = true;
                    if (ViewModel.Video && t == "video") typeMatch = true;
                    if (ViewModel.Web && t == "web") typeMatch = true;
                    if (ViewModel.Application && t == "application") typeMatch = true;
                    if (ViewModel.Preset && t == "preset") typeMatch = true;
                    if (ViewModel.Unknown && t == "unknown") typeMatch = true;

                    bool ratingMatch = false;
                    string r = w.ContentRating?.ToLower() ?? string.Empty;
                    if (ViewModel.G && r == "everyone") ratingMatch = true;
                    if (ViewModel.Pg && r == "questionable") ratingMatch = true;
                    if (ViewModel.R && r == "mature") ratingMatch = true;

                    bool source = false;
                    string s = w.Source?.ToLower() ?? string.Empty;
                    if (ViewModel.Official && s == "official") source = true;
                    if (ViewModel.Workshop && s == "workshop") source = true;
                    if (ViewModel.Mine && s == "mine") source = true;

                    bool tagsMatch = w.Tags.Any(t => selectedTags.Contains(t?.Replace(" ", "").Replace("-", "") ?? ""));
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
                    _ => query.OrderByDescending(w => w.UpdateTime)
                };
                return sortedQuery.ToList();
            }, token);

            if (IsListEqual(Wallpapers, filteredResult)) return;

            if (!token.IsCancellationRequested)
            {
                Wallpapers.Clear();

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (filteredResult.Count == 0)
                    {
                        NoResultTip.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        NoResultTip.Visibility = Visibility.Collapsed;
                    }
                });

                await DispatcherQueue.EnqueueAsync(async () =>
                {
                    int batchSize = 40;
                    for (int i = 0; i < filteredResult.Count; i += batchSize)
                    {
                        if (token.IsCancellationRequested) return;
                        var batch = filteredResult.Skip(i).Take(batchSize);
                        foreach (var item in batch)
                        {
                            Wallpapers.Add(item);
                        }
                        await Task.Delay(1);
                    }
                });
                if (!token.IsCancellationRequested)
                {
                    RefreshScrollBarLabels();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex,"筛选结果时出现异常。");
        }
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

    private void LeftToggleFilterButton_Click(object sender, RoutedEventArgs e)
    {
        LeftSplitView.IsPaneOpen = !LeftSplitView.IsPaneOpen;
    }
    private void RightToggleFilterButton_Click(object sender, RoutedEventArgs e)
    {
        RightSplitView.IsPaneOpen = !RightSplitView.IsPaneOpen;
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
    private void UpdateItemCheckBoxOpacity(Grid grid, WallpaperItem item)
    {
        if (grid == null || item == null) return;

        var checkBox = grid.Children.OfType<CheckBox>().FirstOrDefault();
        checkBox?.Opacity = (IsMultiSelectMode || item.IsSelected) ? 1 : 0;
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
                ViewModel.SelectedWallpaper.IsSelected = true;
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

            UpdateMultiSelectCount();
        }
        else
        {
            StopAllStackAnimations();

            SinglePreviewBorder.Visibility = Visibility.Visible;
            SingleSelectionInfoPanel.Visibility = Visibility.Visible;

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
            var compositor = visual.Compositor;

            visual.Scale = new Vector3(1.0f, 1.0f, 1.0f);

            // 触发位置计算
            UpdateStackVisuals();

            // 渐现动画
            var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(1.0f, 1.0f);
            opacityAnim.Duration = TimeSpan.FromMilliseconds(200);
            visual.StartAnimation("Opacity", opacityAnim);
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
    private void Item_PointerEntered(object sender, PointerRoutedEventArgs e)
    {

        if (sender is Grid grid)
        {
            var checkBox = GetCheckBoxOverlay(grid);
            checkBox?.Children.OfType<CheckBox>().First().Opacity = 1;

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
                ViewModel.SelectedWallpaper = item;
                PlayDrillInAnimation();
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

            var scaleAnimation = compositor.CreateSpringVector3Animation();
            scaleAnimation.Target = "Scale";
            scaleAnimation.FinalValue = new Vector3(1.15f, 1.15f, 1.15f);
            scaleAnimation.DampingRatio = 0.6f;
            scaleAnimation.Period = TimeSpan.FromMilliseconds(50);
            visual.StartAnimation("Scale", scaleAnimation);

            var overlay = GetCheckBoxOverlay(grid);
            if (overlay != null)
            {
                Visual overlayVisual = ElementCompositionPreview.GetElementVisual(overlay);
                // 同样设置中心点，保证 CheckBox 在原地缩放而不会发生偏移
                overlayVisual.CenterPoint = new Vector3((float)overlay.ActualWidth / 2, (float)overlay.ActualHeight / 2, 0f);
                // 将同一个 scaleAnimation 派发给 CheckBoxOverlay
                overlayVisual.StartAnimation("Scale", scaleAnimation);
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
            var checkBox = GetCheckBoxOverlay(grid);
            checkBox?.Children.OfType<CheckBox>().First().Opacity = IsMultiSelectMode? 1 : 0;

            ApplyScaleAnimation(grid, 1.0f);
            UpdateItemCheckBoxOpacity(grid, item);

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

            // 如果松开时鼠标还在范围内，恢复到 1.15f (悬停态)，否则恢复到 1.0f
            scaleAnimation.FinalValue = new Vector3(1.15f, 1.15f, 1.15f);
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
                        ViewModel.SelectedWallpaper = item;
                        IsMultiSelectMode = true;
                        item.IsSelected = !item.IsSelected;

                        if (item.IsSelected && !SelectedWallpapers.Contains(item))
                            SelectedWallpapers.Add(item);
                    }

                    if (_isMultiSelectMode)
                    {
                        ViewModel.SelectedWallpaper = item;
                        item.IsSelected = !item.IsSelected;

                        if (item.IsSelected && !SelectedWallpapers.Contains(item))
                            SelectedWallpapers.Add(item);
                        else if (!item.IsSelected)
                            SelectedWallpapers.Remove(item);

                        UpdateMultiSelectCount();

                        if (sender is Grid g)
                        {
                            var cb = g.Children.OfType<CheckBox>().FirstOrDefault();
                            cb?.Opacity = 1;
                        }
                        return;
                    }
                }
            }
        }
    }
    private static Grid? GetCheckBoxOverlay(Grid itemRootGrid)
    {
        if (VisualTreeHelper.GetParent(itemRootGrid) is Grid container)
        {
            return container.Children.OfType<Grid>().SingleOrDefault(c => c.Name == "ItemRootGrid")?.Children.OfType<Grid>().SingleOrDefault(g => g.Name == "CheckBoxOverlay");
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
            ViewModel.SelectedWallpaper = item;
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
        // 获取 Visual 层进行高性能动画
        Visual imageVisual = ElementCompositionPreview.GetElementVisual(SinglePreviewImage);
        Compositor compositor = imageVisual.Compositor;

        // 设置中心点 (250 / 2 = 125)
        imageVisual.CenterPoint = new Vector3(125f, 125f, 0f);

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
    private void InternalSelectAllWallpapers()
    {
        SelectedWallpapers.CollectionChanged -= SelectedWallpapers_CollectionChanged;

        var itemsToAdd = Wallpapers.Where(w => !w.IsSelected).ToList();
        foreach (var item in Wallpapers.Where(w => !w.IsSelected))
        {
            item.IsSelected = true;
            SelectedWallpapers.Add(item);
        }

        SelectedWallpapers.CollectionChanged += SelectedWallpapers_CollectionChanged;
        RefreshDisplayedSelectedWallpapers(forceRebuild: true);

        DispatcherQueue.TryEnqueue(() => {
            UpdateStackVisuals();
            UpdateMultiSelectCount();
        });

    }
    private void InternalInvertSelection()
    {
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
        SelectedWallpapers.CollectionChanged += SelectedWallpapers_CollectionChanged;
        RefreshDisplayedSelectedWallpapers(forceRebuild: true);

        UpdateMultiSelectCount();
        UpdateStackVisuals();

        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
    }

    private void SelectAllWallpapers_Click(object sender, RoutedEventArgs e)
    {
        InternalSelectAllWallpapers();
    }
    private void InvertSelection_CLick(object sender, RoutedEventArgs e)
    {
        InternalInvertSelection();
    }
    private void Copy_Click_ByCommandBarFlyout(object sender, RoutedEventArgs e)
    {
    }
    private void Cut_Click_ByCommandBarFlyout(object sender, RoutedEventArgs e)
    {
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
    private async void OnIconSizeChanged(object sender, RoutedEventArgs e)
    {
        await Task.Delay(100);
        HideWallpaperContextMenu();
    }
    private async void ChangeAnnotatedScrollBarEnabled(object sender, RoutedEventArgs e)
    {
        HideWallpaperContextMenu();

        var presenter = WallpapersScrollView.ScrollPresenter;
        if (ViewModel.IsAnnotatedScrollBarEnabled && presenter != null)
        {
            presenter.VerticalScrollController = Papers_AnnotatedScrollBarControl.ScrollController;
        }
        else if(!ViewModel.IsAnnotatedScrollBarEnabled && presenter != null)
        {
            presenter.VerticalScrollController = _originalVerticalScrollController;
        }
    }
    private void CancelMultiSelect_Click(object sender, RoutedEventArgs e)
    {
        IsMultiSelectMode = false;
    }
    public void HideWallpaperContextMenu()
    {
        WallpaperContextMenu?.Hide();
    }
    private async Task DeleteItemAsync(WallpaperItem item, bool skipConfirm = false)
    {
        if (item == null || item.WorkshopID == null || item.FolderPath == null) return;

        await ViewModel.RemoveWorkshopKeyFromAcfAsync(item.WorkshopID, ViewModel.AcfPath);
        bool isFolderDeleted = await _pickerService.DeleteFolderAsync(item.FolderPath);

        if (isFolderDeleted)
        {
            App.GlobalAllWallpapers.Remove(item);
            _allWallpapers.Remove(item);
            Wallpapers.Remove(item);
            SelectedWallpapers.Remove(item);

            UpdateMultiSelectCount();
            Log.Information($"壁纸 {item.Title} 已从列表和磁盘中彻底移除。");
        }
    }
    private void RefreshScrollBarLabels()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            // 1. 彻底清空现有标签
            Papers_AnnotatedScrollBarControl.Labels.Clear();

            if (Wallpapers == null || Wallpapers.Count == 0) return;

            var presenter = WallpapersScrollView.ScrollPresenter;
            // 关键：如果内容高度还没计算出来，标签无法定位，必须跳过
            if (presenter == null || presenter.ExtentHeight <= 0) return;

            // 计算可滚动的总行程
            double maxScroll = presenter.ExtentHeight - presenter.ViewportHeight;
            if (maxScroll <= 0) return;

            var labelGroups = new Dictionary<string, int>();

            for (int i = 0; i < Wallpapers.Count; i++)
            {
                var item = Wallpapers[i];
                string labelText = "#";

                switch (ViewModel.SortOrder)
                {
                    case 0: 
                        string group = _zhGroupings.Lookup(item.Title ?? "");
                        char letter = group.FirstOrDefault(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));
                        labelText = letter != default(char) ? char.ToUpper(letter).ToString() : "#";
                        break;
                    case 1:
                        labelText = item.CreationTime.ToString("yy/MM");
                        break;
                    case 2:
                        labelText = item.UpdateTime.ToString("yy/MM");
                        break;
                    case 3:
                        labelText = _sizeConverter.Convert(item.FileSize, typeof(string), "", "").ToString()?? "";
                        break;
                    default:
                        continue;
                }

                if (!labelGroups.ContainsKey(labelText))
                {
                    labelGroups[labelText] = i;
                }
            }

            // 2. 批量添加标签
            foreach (var kvp in labelGroups)
            {
                double ratio = (double)kvp.Value / Wallpapers.Count;
                double offset = ratio * maxScroll;
                // 使用构造函数初始化只读属性
                Papers_AnnotatedScrollBarControl.Labels.Add(new AnnotatedScrollBarLabel(kvp.Key, offset));
            }
        });
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
        double stableMaxOffset = Wallpapers.Count * 180;

        // 或者：确保计算结果在极短时间内不要剧烈变动
        double fraction = Math.Clamp(args.ScrollOffset / maxOffset, 0.0, 1.0);

        // 严谨处理：限制比例在 0 到 1 之间，防止越界计算
        fraction = Math.Clamp(fraction, 0.0, 1.0);

        // 根据百分比映射到当前数据集合的 Index
        int index = (int)(fraction * (Wallpapers.Count - 1));
        index = Math.Clamp(index, 0, Wallpapers.Count - 1);

        var item = Wallpapers[index];

        // 根据当前的排序方式（SortOrder），动态决定悬浮标签应该显示什么内容
        switch (ViewModel.SortOrder)
        {
            case 0:
                string group = _zhGroupings.Lookup(item.Title ?? "");
                char letter = group.FirstOrDefault(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));
                args.Content = letter != default(char) ? char.ToUpper(letter).ToString() : "#";
                break;
            case 1:
                args.Content = item.CreationTime.ToString("yy/MM");
                break;
            case 2:
                args.Content = item.UpdateTime.ToString("yy/MM");
                break;
            case 3:
                args.Content = _sizeConverter.Convert(item.FileSize, typeof(string), "", "").ToString() ?? "";
                break;
            default:
                break;
        }
    }

    // ... INotifyPropertyChanged 标准实现 ...
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
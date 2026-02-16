using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Numerics;
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
using Serilog;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WE_Tool;

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class Papers : Page
{
    private List<WallpaperItem> _allWallpapers = new List<WallpaperItem>();
    public SettingsViewModel ViewModel { get; }
    public ObservableCollection<WallpaperItem> Wallpapers { get; set; } = new ObservableCollection<WallpaperItem>();

    private CancellationTokenSource _filterCts;

    public Papers()
    {
        this.InitializeComponent();

        ViewModel = new SettingsViewModel(new ConfigService(), new PickerService());
        ViewModel.PropertyChanged += (s, e) => {
            if (ViewModel._isBatchUpdating) return;

            if (e.PropertyName == "SteamWorkshopPath" 
                || e.PropertyName.EndsWith("Expander")
                || e.PropertyName.Contains("Pane")
                || e.PropertyName == "SortIndex")
                return;

            _ = ApplyFilters();
        };

        this.Loaded += async (s, e) =>
        {
            await ViewModel.InitializeAsync();
            await RefreshWallpaperList();
        };
    }

    public async Task RefreshWallpaperList()
    {
        string path = ViewModel.WorkshopPath;

        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return;

        var list = await WallpaperScanner.ScanWallpapers(path);

        _allWallpapers = list;
        ApplyFilters();
    }

    private bool IsListEqual(ObservableCollection<WallpaperItem> current, List<WallpaperItem> next)
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

        string[] tagPropertyNames = 
        {
            "Abstract", "Animal", "Anime", "Cartoon", "CGI", "Cyberpunk",
            "Fantasy", "Game", "Girls", "Guys", "Landscape", "Medieval",
            "Memes", "MMD", "Music", "Nature", "Pixelart", "Relaxing", 
            "Retro", "SciFi", "Sports", "Technology", "Television", 
            "Vehicle", "Unspecified"
        };

        foreach (var propName in tagPropertyNames)
        {
            var prop = ViewModel.GetType().GetProperty(propName);
            if (prop != null && prop.PropertyType == typeof(bool))
            {
                bool isChecked = (bool)prop.GetValue(ViewModel);
                if (isChecked)
                {
                    selectedTags.Add(propName);
                }
            }
        }
        return selectedTags;
    }
    private async Task ApplyFilters()
    {
        _filterCts?.Cancel();
        _filterCts = new CancellationTokenSource();
        var token = _filterCts.Token;

        try
        {
            await Task.Delay(200, token);

            var selectedTags = GetSelectedTags();
            int sortIndex = ViewModel.SortOrder;
            bool isAscending = ViewModel.IsSortAscending;

            var filteredResult = await Task.Run(() =>
            {
                var query = _allWallpapers.Where(w =>
                {
                    bool typeMatch = false;
                    if (ViewModel.Scene && w.Type == "scene") typeMatch = true;
                    if (ViewModel.Video && w.Type == "video") typeMatch = true;
                    if (ViewModel.Web && w.Type == "web") typeMatch = true;
                    if (ViewModel.Application && w.Type == "application") typeMatch = true;
                    if (ViewModel.Unknown && w.Type == "unknown") typeMatch = true;

                    bool ratingMatch = false;
                    string r = w.ContentRating?.ToLower();
                    if (ViewModel.G && r == "everyone") ratingMatch = true;
                    if (ViewModel.PG && r == "questionable") ratingMatch = true;
                    if (ViewModel.R && r == "mature") ratingMatch = true;

                    bool source = false;

                    bool tags = false;
                    bool tagsMatch = w.Tags.Any(t => selectedTags.Contains(t));

                    return typeMatch && ratingMatch && tagsMatch;
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

            DispatcherQueue.TryEnqueue(() =>
            {
                Wallpapers.Clear();
                foreach (var item in filteredResult)
                {
                    Wallpapers.Add(item);
                }
            });
        }
        catch (TaskCanceledException) { }
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
        OnPropertyChanged(nameof(ViewModel.Abstract));
    } 
    private async void DeselectAllTags_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ResetFiltersAsync(2, false);
        OnPropertyChanged(nameof(ViewModel.Abstract));
    }
}

public sealed partial class Papers : Page, INotifyPropertyChanged
{
    private bool _isMultiSelectMode = false;

    public bool IsMultiSelectMode
    {
        get => _isMultiSelectMode;
        set
        {
            if (_isMultiSelectMode != value)
            {
                _isMultiSelectMode = value;
                OnPropertyChanged();

                // 当模式切换时，立即更新所有可见项目的视觉状态
                UpdateAllItemsVisualState();
            }
        }
    }
    private void UpdateItemCheckBoxOpacity(Grid grid, WallpaperItem item)
    {
        if (grid == null || item == null) return;

        var checkBox = grid.Children.OfType<CheckBox>().FirstOrDefault();
        if (checkBox != null)
        {
            checkBox.Opacity = (IsMultiSelectMode || item.IsSelected) ? 1 : 0;
        }
    }
    private void UpdateAllItemsVisualState()
    {
        if (WallpapersRepeater == null) return;

        for (int i = 0; i < Wallpapers.Count; i++)
        {
            var element = WallpapersRepeater.TryGetElement(i);

            if (element is Grid itemContainer)
            {
                foreach (var child in itemContainer.Children)
                {
                    if (child is Grid innerGrid && innerGrid.Name == "ItemRootGrid")
                    {
                        if (innerGrid.DataContext is WallpaperItem item)
                        {
                            UpdateItemCheckBoxOpacity(innerGrid, item);
                        }
                        break;
                    }
                }
            }
        }
    }
    private void SelectionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!IsMultiSelectMode)
        {
            IsMultiSelectMode = true;
        }
    }
    private void Item_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            var checkBox = grid.Children.OfType<CheckBox>().FirstOrDefault();

            if (checkBox != null)
            {
                checkBox.Opacity = 1;
            }

            Visual visual = ElementCompositionPreview.GetElementVisual(grid);
            Compositor compositor = visual.Compositor;

            visual.CenterPoint = new System.Numerics.Vector3(
            (float)grid.ActualWidth / 2,
            (float)grid.ActualHeight / 2,
            0f);

            var scaleAnimation = compositor.CreateSpringVector3Animation();
            scaleAnimation.Target = "Scale";
            scaleAnimation.FinalValue = new Vector3(1.1f, 1.1f, 1.1f);
            scaleAnimation.DampingRatio = 0.6f;
            scaleAnimation.Period = TimeSpan.FromMilliseconds(50);
            visual.StartAnimation("Scale", scaleAnimation);

            Canvas.SetZIndex(grid, 1);
        }
    }
    private void Item_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid && grid.DataContext is WallpaperItem item)
        {
            UpdateItemCheckBoxOpacity(grid, item);

            Visual visual = ElementCompositionPreview.GetElementVisual(grid);
            Compositor compositor = visual.Compositor;

            var scaleAnimation = compositor.CreateSpringVector3Animation();
            scaleAnimation.Target = "Scale";
            scaleAnimation.FinalValue = new Vector3(1.0f, 1.0f, 1.0f);
            scaleAnimation.DampingRatio = 0.6f;
            scaleAnimation.Period = TimeSpan.FromMilliseconds(50);
            visual.StartAnimation("Scale", scaleAnimation);

            Canvas.SetZIndex(grid, 0);
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

            // 如果松开时鼠标还在范围内，恢复到 1.1f (悬停态)，否则恢复到 1.0f
            scaleAnimation.FinalValue = new Vector3(1.1f, 1.1f, 1.1f);
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
                if (_isMultiSelectMode)
                {
                    item.IsSelected = !item.IsSelected;

                    if (sender is Grid g)
                    {
                        var cb = g.Children.OfType<CheckBox>().FirstOrDefault();
                        if (cb != null) cb.Opacity = 1;
                    }
                }
                else
                {
                    ViewModel.SelectedWallpaper = item;
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
        }
    }

    public static string MapTypeToChinese(string type)
    {
        if (string.IsNullOrEmpty(type)) return "未知";

        return type.ToLower() switch
        {
            "scene" => "场景",
            "video" => "视频",
            "web" => "网页",
            "application" => "应用程序",
            _ => type 
        };
    }

    // ... INotifyPropertyChanged 标准实现 ...
    public event PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
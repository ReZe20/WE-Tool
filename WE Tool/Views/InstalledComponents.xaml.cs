using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using WE_Tool.Helper;
using WE_Tool.Models;
using WE_Tool.ViewModels;

namespace WE_Tool;

public sealed partial class InstalledComponents : Page, INotifyPropertyChanged
{
    private List<ComponentInfo> _allComponents = [];
    private string _searchText = "";
    private bool _isUpdating;

    public SettingsViewModel ViewModel { get; }
    public ObservableCollection<ComponentInfo> FilteredComponents { get; } = [];

    public InstalledComponents()
    {
        this.InitializeComponent();

        var app = Application.Current as App;
        ViewModel = app?.ViewModel ?? new SettingsViewModel(new Service.ConfigService(), new Service.PickerService());

        ViewModel.ComponentsFilterVM.PropertyChanged += (s, e) =>
        {
            if (_isUpdating) return;
            ApplyFilters();
        };

        ViewModel.ComponentsDisplayVM.PropertyChanged += (s, e) =>
        {
            if (_isUpdating) return;
            if (e.PropertyName == nameof(ComponentsDisplayViewModel.SortOrder)
                || e.PropertyName == nameof(ComponentsDisplayViewModel.IsSortAscending))
            {
                ApplyFilters();
            }
            else if (e.PropertyName == nameof(ComponentsDisplayViewModel.ComponentViewIndex))
            {
                ApplyViewMode();
            }
            else if (e.PropertyName == nameof(ComponentsDisplayViewModel.LeftSplitViewPaneOpen)
                     || e.PropertyName == nameof(ComponentsDisplayViewModel.RightSplitViewPaneOpen))
            {
                ApplyPaneState();
            }
        };
    }

    // ===================== INotifyPropertyChanged =====================
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ===================== 生命周期 =====================
    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadComponents();
    }

    private async void LoadComponents()
    {
        try
        {
            _isUpdating = true;

            var components = WallpaperScanner.LastComponents;
            _allComponents = components ?? [];

            _isUpdating = false;

            ApplyViewMode();
            ApplyPaneState();
            ApplyFilters();

            Log.Information("已加载 {Count} 个组件", _allComponents.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载组件失败");
        }
    }

    private void ApplyViewMode()
    {
        var idx = ViewModel.ComponentsDisplayVM.ComponentViewIndex;
        if (ComponentsRepeater == null) return;
        ComponentsRepeater.Visibility = (idx <= 2) ? Visibility.Visible : Visibility.Collapsed;
        ComponentsContentRepeater.Visibility = (idx == 3) ? Visibility.Visible : Visibility.Collapsed;
        ComponentsListRepeater.Visibility = (idx == 4) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyPaneState()
    {
        if (LeftSplitView != null)
            LeftSplitView.IsPaneOpen = ViewModel.ComponentsDisplayVM.LeftSplitViewPaneOpen;
        if (RightSplitView != null)
            RightSplitView.IsPaneOpen = ViewModel.ComponentsDisplayVM.RightSplitViewPaneOpen;
    }

    // ===================== 筛选逻辑 =====================
    private void ApplyFilters()
    {
        if (_isUpdating) return;

        var filter = ViewModel.ComponentsFilterVM;
        var filtered = _allComponents.AsEnumerable();

        // 类型
        var activeTypes = new HashSet<string>();
        if (filter.Layers) activeTypes.Add("Layers");
        if (filter.Scripts) activeTypes.Add("scripts");
        if (filter.Effects) activeTypes.Add("effects");

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
        var activeRatings = new HashSet<string>();
        if (filter.Everyone) activeRatings.Add("Everyone");
        if (filter.Questionable) activeRatings.Add("Questionable");
        if (filter.Mature) activeRatings.Add("Mature");

        filtered = filtered.Where(c =>
            activeRatings.Contains(c.ContentRating ?? "Everyone"));

        // 标签
        var activeTags = GetActiveTags();
        if (activeTags.Count > 0)
        {
            filtered = filtered.Where(c =>
            {
                if (string.IsNullOrEmpty(c.Tags)) return false;
                return activeTags.Any(tag =>
                    c.Tags!.Contains(tag, StringComparison.OrdinalIgnoreCase));
            });
        }

        // 搜索
        if (!string.IsNullOrEmpty(_searchText))
        {
            filtered = filtered.Where(c =>
                c.Title?.Contains(_searchText, StringComparison.OrdinalIgnoreCase) == true);
        }

        // 排序
        var display = ViewModel.ComponentsDisplayVM;
        filtered = display.SortOrder switch
        {
            0 => display.IsSortAscending
                ? filtered.OrderBy(c => c.Title ?? "")
                : filtered.OrderByDescending(c => c.Title ?? ""),
            1 => display.IsSortAscending
                ? filtered.OrderBy(c => c.InstallDate)
                : filtered.OrderByDescending(c => c.InstallDate),
            2 => display.IsSortAscending
                ? filtered.OrderBy(c => c.FileSize)
                : filtered.OrderByDescending(c => c.FileSize),
            _ => filtered
        };

        FilteredComponents.Clear();
        foreach (var item in filtered)
            FilteredComponents.Add(item);

        NoResultTip.Visibility = FilteredComponents.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private HashSet<string> GetActiveTags()
    {
        var f = ViewModel.ComponentsFilterVM;
        var tags = new HashSet<string>();
        if (f.UnspecifiedGenre) tags.Add("Unspecified genre");
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
        ApplyFilters();
    }

    private void SelectAllTags_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SetAllComponentTags(true);
        ApplyFilters();
    }

    private void DeselectAllTags_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SetAllComponentTags(false);
        ApplyFilters();
    }

    private void ComponentSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _searchText = sender.Text ?? "";
        ApplyFilters();
    }

    private void RightToggleFilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle)
            RightSplitView.IsPaneOpen = toggle.IsChecked == true;
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
        _isUpdating = true;
        SetExpandCheckBoxes(_currentFilterExpander, true);
        _isUpdating = false;
        ApplyFilters();
    }

    private void FilterExpanderInvert_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFilterExpander == null) return;
        _isUpdating = true;
        SetExpandCheckBoxes(_currentFilterExpander, null);
        _isUpdating = false;
        ApplyFilters();
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
}

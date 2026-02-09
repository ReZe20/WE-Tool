using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Tool_for_WallpaperEngine.Service;
using Tool_for_WallpaperEngine.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Tool_for_WallpaperEngine;

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class Papers : Page
{
    public SettingsViewModel ViewModel { get; }

    public Papers()
    {
        InitializeComponent();
        // 确保加载数据
        Loaded += async (s, e) => await ViewModel.InitializeAsync();

        ViewModel = new SettingsViewModel(new ConfigService(), new PickerService());
    }

    private void LeftToggleFilterButton_Click(object sender, RoutedEventArgs e)
    {
        LeftSplitView.IsPaneOpen = !LeftSplitView.IsPaneOpen;
    }
    private void RightToggleFilterButton_Click(object sender, RoutedEventArgs e)
    {
        RightSplitView.IsPaneOpen = !RightSplitView.IsPaneOpen;
    }
    private void sortByname_CLick(object sender, RoutedEventArgs e)
    {
        sortButtonIcon.Glyph = "\uE8D2";
        sortButtonText.Text = "按名称排序";
    }
    private void sortBysubscribe_CLick(object sender, RoutedEventArgs e)
    {
        sortButtonIcon.Glyph = "\uED0E";
        sortButtonText.Text = "按订阅时间";
    }
    private void sortByupdate_CLick(object sender, RoutedEventArgs e)
    {
        sortButtonIcon.Glyph = "\uF738";
        sortButtonText.Text = "按更新时间";
    }
    private void sortBysize_CLick(object sender, RoutedEventArgs e)
    {
        sortButtonIcon.Glyph = "\uEDA2";
        sortButtonText.Text = "按大小排序";
    }

    private void btnSortDirection_Click(object sender, RoutedEventArgs e)
    {
        if (btnSortDirectionIcon.Glyph == "\uE70E")
            btnSortDirectionIcon.Glyph = "\uE70D";
        else
            btnSortDirectionIcon.Glyph = "\uE70E";
    }
    private async void ResetFilter_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ResetFiltersAsync(1);
    }
    private void SelectAllportraitmonitor_Click(object sender, RoutedEventArgs e)
    {
        
    }
    private void DeselectAllportraitmonitor_Click(object sneder, RoutedEventArgs e)
    {
        
    }
    private void SelectAlltripledisplay_Click(object sender, RoutedEventArgs e)
    {
        
    }
    private void DeselectAlltripledisplay_Click(object sneder, RoutedEventArgs e)
    {
        
    }
    private void SelectAlldualdisplay_Click(object sender, RoutedEventArgs e)
    {
        
    }
    private void DeselectAlldualdisplay_Click(object sneder, RoutedEventArgs e)
    {
        
    }
    private void SelectAllultrawide_Click(object sender, RoutedEventArgs e)
    {
        
    }
    private void DeselectAllultrawide_Click(object sneder, RoutedEventArgs e)
    {
        
    }
    private void SelectAllwidescreen_Click(object sender, RoutedEventArgs e)
    {
        
    }
    private void DeselectAllwidescreen_Click(object sneder, RoutedEventArgs e)
    {
        
    }
    private async void SelectAllTags_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ResetFiltersAsync(2);
    } 
    private async void DeselectAllTags_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DeselctAllAsync(1);
    }
}

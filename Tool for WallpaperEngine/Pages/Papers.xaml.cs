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

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Tool_for_WallpaperEngine;

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class Papers : Page
{

    public Papers()
    {
        InitializeComponent();
    }

    private void FoldLeftColumn_Click(object sender, RoutedEventArgs e)
    {
        LeftColumn.Width = new GridLength(0);
        ExpandLeftColumnButton.Visibility = Visibility.Visible;
        FoldLeftColumnButton.Visibility = Visibility.Collapsed;
    }
    private void ExpandLeftColumn_Click(object sender, RoutedEventArgs e)
    {
        LeftColumn.Width = new GridLength(195);
        ExpandLeftColumnButton.Visibility = Visibility.Collapsed;
        FoldLeftColumnButton.Visibility = Visibility.Visible;
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
    private void SelectAllportraitmonitor_Click(object sender, RoutedEventArgs e)
    {
        foreach (CheckBox cb in portraitmonitor.Children)
            cb.IsChecked = true;
    }
    private void DeselectAllportraitmonitor_Click(object sneder, RoutedEventArgs e)
    {
        foreach (CheckBox cb in portraitmonitor.Children)
            cb.IsChecked = false;
    }
    private void SelectAlltripledisplay_Click(object sender, RoutedEventArgs e)
    {
        foreach (CheckBox cb in tripledisplay.Children)
            cb.IsChecked = true;
    }
    private void DeselectAlltripledisplay_Click(object sneder, RoutedEventArgs e)
    {
        foreach (CheckBox cb in tripledisplay.Children)
            cb.IsChecked = false;
    }
    private void SelectAlldualdisplay_Click(object sender, RoutedEventArgs e)
    {
        foreach (CheckBox cb in dualdisplay.Children)
            cb.IsChecked = true;
    }
    private void DeselectAlldualdisplay_Click(object sneder, RoutedEventArgs e)
    {
        foreach (CheckBox cb in dualdisplay.Children)
            cb.IsChecked = false;
    }
    private void SelectAllultrawide_Click(object sender, RoutedEventArgs e)
    {
        foreach (CheckBox cb in ultrawide.Children)
            cb.IsChecked = true;
    }
    private void DeselectAllultrawide_Click(object sneder, RoutedEventArgs e)
    {
        foreach (CheckBox cb in ultrawide.Children)
            cb.IsChecked = false;
    }
    private void SelectAllwidescreen_Click(object sender, RoutedEventArgs e)
    {
        foreach (CheckBox cb in widescreen.Children)
            cb.IsChecked = true;
    }
    private void DeselectAllwidescreen_Click(object sneder, RoutedEventArgs e)
    {
        foreach (CheckBox cb in widescreen.Children)
            cb.IsChecked = false;
    }
    private void SelectAllTags_Click(object sender, RoutedEventArgs e)
    {
        foreach (var child in TagsContainer.Children)
        {
            if (child is CheckBox cb)
                cb.IsChecked = true;
        }
    }
    private void DeselectAllTags_Click(object sender, RoutedEventArgs e)
    {
        foreach (var child in TagsContainer.Children)
        {
            if (child is CheckBox cb)
                cb.IsChecked = false;
        }
    }
}

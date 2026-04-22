using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Models;

namespace WE_Tool.ViewModels
{
    public partial class PapersViewModel : ObservableObject
    {
        [ObservableProperty]
        public partial WallpaperItem? SelectedWallpaper { get; set; }

        [ObservableProperty]
        public partial ObservableCollection<WallpaperItem> SelectedWallpapers { get; set; } = [];

        public bool IsButtonInGridColumnEnabled
        {
            get => SelectedWallpapers.Count != 0 || SelectedWallpaper != null;
        }

        [ObservableProperty]
        public partial bool IsBottomBarOpen { get; set; }

        public Microsoft.UI.Xaml.GridLength BottomBarHeight
        {
            get => IsBottomBarOpen ?
                  new Microsoft.UI.Xaml.GridLength(50) :
                  new Microsoft.UI.Xaml.GridLength(0);
            set => SetProperty(ref field, value);
        }

        [ObservableProperty]
        public partial bool AutoPlayGif { get; set; }

        [ObservableProperty]
        public partial bool IsWallpaperEnterAnimationEnabled { get; set; }

        [ObservableProperty]
        public partial bool IsAnnotatedScrollBarEnabled { get; set; }

        public ScrollingScrollBarVisibility PapersScrollViewBarVisibility
        {
            get => IsAnnotatedScrollBarEnabled
                    ? ScrollingScrollBarVisibility.Hidden
                    : ScrollingScrollBarVisibility.Visible;
            set
            {
                SetProperty(ref field, value);
            }
        }

        public Microsoft.UI.Xaml.Thickness PapersScrollViewMargin
        {
            get => IsAnnotatedScrollBarEnabled ?
                new Microsoft.UI.Xaml.Thickness(4, 0, 44, 0) :
                new Microsoft.UI.Xaml.Thickness(4, 0, 4, 0);

            set => SetProperty(ref field, value);
        }

        [ObservableProperty]
        public partial int WallpaperTagDisplayIndex { get; set; }

        public Microsoft.UI.Xaml.Visibility TagDisplayVisibility
        {
            get
            {
                return WallpaperTagDisplayIndex != 4 ?
                    Microsoft.UI.Xaml.Visibility.Visible :
                    Microsoft.UI.Xaml.Visibility.Collapsed;
            }
        }

        [ObservableProperty]
        public partial int WallpaperViewIndex { get; set; }

        public bool SmallIconItem
        {
            get => WallpaperViewIndex == 0;
            set
            {
                if (value) WallpaperViewIndex = 0;
            }
        }
        public bool MediumIconItem
        {
            get => WallpaperViewIndex == 1;
            set
            {
                if (value) WallpaperViewIndex = 1;
            }
        }
        public bool LargeIconItem
        {
            get => WallpaperViewIndex == 2;
            set
            {
                if (value) WallpaperViewIndex = 2;
            }
        }

        [ObservableProperty]
        public partial int WallpaperListMinWidth { get; set; }

        [ObservableProperty]
        public partial bool LeftSplitViewPaneOpen { get; set; }

        [ObservableProperty]
        public partial bool RightSplitViewPaneOpen { get; set; }

        [ObservableProperty]
        public partial bool DetailSelectionEnabled { get; set; }

        [ObservableProperty]
        public partial int FilterResultResponseDelay { get; set; }

        [ObservableProperty]
        public partial int SortOrder { get; set; }

        public string SortGlyph
        {
            get => SortOrder switch
            {
                0 => "\uE8D2",
                1 => "\uED0E",
                2 => "\uF738",
                3 => "\uEDA2",
                _ => "\uE8D2"
            };
        }

        public string SortText
        {
            get => LanguageHelper.GetResource(SortOrder switch
            {
                0 => "SortByName.Text",
                1 => "SortBySubTime.Text",
                2 => "SortByLastTime.Text",
                3 => "SortByLastTime.Text",
                _ => "SortByName.Text"
            });
        }

        public bool SortByName
        {
            get => SortOrder == 0;
            set
            {
                if (value) SortOrder = 0;
            }
        }
        public bool SortBySubTime
        {
            get => SortOrder == 1;
            set
            {
                if (value) SortOrder = 1;
            }
        }
        public bool SortByLastTime
        {
            get => SortOrder == 2;
            set
            {
                if (value) SortOrder = 2;
            }
        }
        public bool SortByFileSize
        {
            get => SortOrder == 3;
            set
            {
                if (value) SortOrder = 3;
            }
        }
        [ObservableProperty]
        public partial bool IsSortAscending { get; set; }
    }
}

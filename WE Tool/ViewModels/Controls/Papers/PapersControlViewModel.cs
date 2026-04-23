using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using WE_Tool.Helper;
using WE_Tool.Models;

namespace WE_Tool.ViewModels.Controls.Papers
{
    public partial class PapersControlViewModel : ObservableObject
    {
        private ObservableCollection<WallpaperItem>? _previousSelectedWallpapers;
        public bool _isBatchUpdating = false;
        public int _wallpaperViewIndex;
        public bool _isAnnotatedScrollBarEnabled;

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
        public bool TypeDisplayInTag
        {
            get => WallpaperTagDisplayIndex == 0;
            set
            {
                if (value) WallpaperTagDisplayIndex = 0;
            }
        }
        public bool RatingDisplayInTag
        {
            get => WallpaperTagDisplayIndex == 1;
            set
            {
                if (value) WallpaperTagDisplayIndex = 1;
            }
        }
        public bool SourceDisplayInTag
        {
            get => WallpaperTagDisplayIndex == 2;
            set
            {
                if (value) WallpaperTagDisplayIndex = 2;
            }
        }
        public bool TagDisplayInTag
        {
            get => WallpaperTagDisplayIndex == 3;
            set
            {
                if (value) WallpaperTagDisplayIndex = 3;
            }
        }
        public bool NoneDisplayInTag
        {
            get => WallpaperTagDisplayIndex == 4;
            set
            {
                if (value) WallpaperTagDisplayIndex = 4;
            }
        }
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

        public void ExecuteChangeSort(string? parameter)
        {
            if (int.TryParse(parameter, out int newOrder))
            {
                SortOrder = newOrder;
            }
        }

        public void SuspendSelectedWallpapersCollectionChanged()
        {
            _previousSelectedWallpapers?.CollectionChanged -= OnSelectedWallpapersCollectionChanged;
        }
        public void ResumeSelectedWallpapersCollectionChanged()
        {
            _previousSelectedWallpapers?.CollectionChanged += OnSelectedWallpapersCollectionChanged;
            OnSelectedWallpaperChanged(SelectedWallpaper);
        }
        partial void OnSelectedWallpaperChanged(WallpaperItem value)
        {
            OnPropertyChanged(nameof(IsButtonInGridColumnEnabled));
        }
        public void OnSelectedWallpapersCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(IsButtonInGridColumnEnabled));
        }
        partial void OnSelectedWallpapersChanged(ObservableCollection<WallpaperItem> value)
        {
            if (_previousSelectedWallpapers != null)
            {
                _previousSelectedWallpapers.CollectionChanged -= OnSelectedWallpapersCollectionChanged;
            }

            _previousSelectedWallpapers = value;
            if (value != null)
            {
                value.CollectionChanged += OnSelectedWallpapersCollectionChanged;
            }

            OnPropertyChanged(nameof(IsButtonInGridColumnEnabled));
        }

        partial void OnWallpaperViewIndexChanged(int value)
        {
            WallpaperListMinWidth = value switch
            {
                0 => 180,
                1 => 240,
                2 => 300,
                _ => 180
            };
        }
        partial void OnWallpaperTagDisplayIndexChanged(int value)
        {
            OnPropertyChanged(nameof(TagDisplayVisibility));
        }
        partial void OnIsAnnotatedScrollBarEnabledChanged(bool value)
        {
            OnPropertyChanged(nameof(PapersScrollViewBarVisibility));
            OnPropertyChanged(nameof(PapersScrollViewMargin));
        }
        partial void OnSortOrderChanged(int value)
        {
            OnPropertyChanged(nameof(SortText));
            OnPropertyChanged(nameof(SortGlyph));
        }

        partial void OnIsSortAscendingChanged(bool value)
        {
            OnPropertyChanged(nameof(SortDirectionGlyph));
        }

        partial void OnIsBottomBarOpenChanged(bool value)
        {
            BottomBarHeight = value ? new Microsoft.UI.Xaml.GridLength(50) : new Microsoft.UI.Xaml.GridLength(0);
        }

        public string SortDirectionGlyph => IsSortAscending ? "\uE70D" : "\uE70E";

        public void LoadFromSettings(PapersConfig papersConfig)
        {
            _isBatchUpdating = true;

            IsBottomBarOpen = papersConfig.IsBottomBarOpen;
            AutoPlayGif = papersConfig.AutoPlayGif;
            IsWallpaperEnterAnimationEnabled = papersConfig.IsWallpaperEnterAnimationEnabled;
            IsAnnotatedScrollBarEnabled = papersConfig.IsAnnotatedScrollBarEnabled;
            WallpaperTagDisplayIndex = papersConfig.WallpaperTagDisplayIndex;
            WallpaperViewIndex = papersConfig.WallpaperViewIndex;
            WallpaperListMinWidth = papersConfig.WallpaperListMinWidth;
            LeftSplitViewPaneOpen = papersConfig.LeftSplitViewPaneOpen;
            RightSplitViewPaneOpen = papersConfig.RightSplitViewPaneOpen;
            SortOrder = papersConfig.SortOrder;
            IsSortAscending = papersConfig.IsSortAscending;
            DetailSelectionEnabled = papersConfig.DetailSelectionEnabled;
            FilterResultResponseDelay = papersConfig.FilterResultResponseDelay;

            _isBatchUpdating = false;
        }
        public void RaisePropertiesChanged()
        {
            OnPropertyChanged(string.Empty);
        }
    }
}

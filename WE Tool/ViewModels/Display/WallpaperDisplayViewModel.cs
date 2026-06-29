using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using WE_Tool.Helper;

namespace WE_Tool.ViewModels
{
    public partial class WallpaperDisplayViewModel : ObservableObject
    {
        [ObservableProperty]
        public partial bool IsBottomBarOpen { get; set; }

        partial void OnIsBottomBarOpenChanged(bool value)
        {
            BottomBarHeight = value ? new Microsoft.UI.Xaml.GridLength(50) : new Microsoft.UI.Xaml.GridLength(0);
        }

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

        partial void OnIsAnnotatedScrollBarEnabledChanged(bool value)
        {
            OnPropertyChanged(nameof(PapersScrollViewBarVisibility));
            OnPropertyChanged(nameof(PapersScrollViewMargin));
        }

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

        partial void OnWallpaperTagDisplayIndexChanged(int value)
        {
            OnPropertyChanged(nameof(TagDisplayVisibility));
        }

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

        partial void OnSortOrderChanged(int value)
        {
            OnPropertyChanged(nameof(SortText));
            OnPropertyChanged(nameof(SortGlyph));
        }

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
                3 => "SortByFileSize.Text",
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

        partial void OnIsSortAscendingChanged(bool value)
        {
            OnPropertyChanged(nameof(SortDirectionGlyph));
        }

        public string SortDirectionGlyph => IsSortAscending ? "\uE70D" : "\uE70E";

        public IRelayCommand<string> ChangeSortCommand { get; }

        public WallpaperDisplayViewModel()
        {
            ChangeSortCommand = new RelayCommand<string>(ExecuteChangeSort);
        }

        public void ExecuteChangeSort(string? parameter)
        {
            if (int.TryParse(parameter, out int newOrder))
            {
                SortOrder = newOrder;
            }
        }
    }
}

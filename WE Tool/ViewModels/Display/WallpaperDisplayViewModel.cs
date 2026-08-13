using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace WE_Tool.ViewModels
{
    public enum WallpaperDisplayModes
    {
        Icon = 0,
        Content = 1,
        List = 2
    }

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

        // === 分页 ===
        // PaginationMode: 0=不分页, 1=每页30, 2=每页50, 3=每页70, 4=每页90
        [ObservableProperty]
        public partial int PaginationMode { get; set; }

        partial void OnPaginationModeChanged(int value)
        {
            OnPropertyChanged(nameof(PaginationEnabled));
            OnPropertyChanged(nameof(PaginationBarVisibility));
            OnPropertyChanged(nameof(NoPaginationItem));
            OnPropertyChanged(nameof(PageSize30Item));
            OnPropertyChanged(nameof(PageSize50Item));
            OnPropertyChanged(nameof(PageSize70Item));
            OnPropertyChanged(nameof(PageSize90Item));
        }

        public bool PaginationEnabled => PaginationMode != 0;

        public Microsoft.UI.Xaml.Visibility PaginationBarVisibility
            => PaginationEnabled ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

        /// <summary>当前每页数量；不分页模式下返回 0（调用方已有 <=0 兜底）</summary>
        public int PageSize => PaginationMode switch
        {
            1 => 30,
            2 => 50,
            3 => 70,
            4 => 90,
            _ => 0
        };

        public bool NoPaginationItem
        {
            get => PaginationMode == 0;
            set { if (value) PaginationMode = 0; }
        }
        public bool PageSize30Item
        {
            get => PaginationMode == 1;
            set { if (value) PaginationMode = 1; }
        }
        public bool PageSize50Item
        {
            get => PaginationMode == 2;
            set { if (value) PaginationMode = 2; }
        }
        public bool PageSize70Item
        {
            get => PaginationMode == 3;
            set { if (value) PaginationMode = 3; }
        }
        public bool PageSize90Item
        {
            get => PaginationMode == 4;
            set { if (value) PaginationMode = 4; }
        }

        [ObservableProperty]
        public partial int WallpaperTagDisplayIndex { get; set; }

        partial void OnWallpaperTagDisplayIndexChanged(int value)
        {
            OnPropertyChanged(nameof(TagDisplayVisibility));
            OnPropertyChanged(nameof(TypeDisplayInTag));
            OnPropertyChanged(nameof(RatingDisplayInTag));
            OnPropertyChanged(nameof(SourceDisplayInTag));
            OnPropertyChanged(nameof(TagDisplayInTag));
            OnPropertyChanged(nameof(NoneDisplayInTag));
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
                1 => 210,
                2 => 240,
                _ => 180
            };
            OnPropertyChanged(nameof(SmallIconItem));
            OnPropertyChanged(nameof(MediumIconItem));
            OnPropertyChanged(nameof(LargeIconItem));

            if (!_isUpdatingViewMode)
            {
                _isUpdatingViewMode = true;
                ViewModeIndex = WallpaperDisplayMode switch
                {
                    (int)WallpaperDisplayModes.Content => 3,
                    (int)WallpaperDisplayModes.List => 4,
                    _ => value
                };
                _isUpdatingViewMode = false;
            }
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

        /// <summary>预览模糊年龄段开关(查看菜单"预览模糊"复选):勾选后该年龄段壁纸的预览图高斯模糊</summary>
        [ObservableProperty]
        public partial bool BlurEveryone { get; set; }

        [ObservableProperty]
        public partial bool BlurTeen { get; set; }

        [ObservableProperty]
        public partial bool BlurAdult { get; set; }

        private bool _isUpdatingViewMode;

        [ObservableProperty]
        public partial int ViewModeIndex { get; set; }

        partial void OnViewModeIndexChanged(int value)
        {
            if (_isUpdatingViewMode)
            {
                // 联动赋值路径（OnWallpaperViewIndexChanged/OnWallpaperDisplayModeChanged 内部）：
                // 属性通知同样必须发出，否则菜单选中状态不会刷新
                OnPropertyChanged(nameof(SmallIconViewItem));
                OnPropertyChanged(nameof(MediumIconViewItem));
                OnPropertyChanged(nameof(LargeIconViewItem));
                OnPropertyChanged(nameof(ContentViewItem));
                OnPropertyChanged(nameof(ListViewItem));
                return;
            }
            _isUpdatingViewMode = true;

            WallpaperDisplayMode = value switch
            {
                3 => (int)WallpaperDisplayModes.Content,
                4 => (int)WallpaperDisplayModes.List,
                _ => (int)WallpaperDisplayModes.Icon
            };

            WallpaperViewIndex = (value == 3 || value == 4) ? 0 : value;

            _isUpdatingViewMode = false;

            OnPropertyChanged(nameof(SmallIconViewItem));
            OnPropertyChanged(nameof(MediumIconViewItem));
            OnPropertyChanged(nameof(LargeIconViewItem));
            OnPropertyChanged(nameof(ContentViewItem));
            OnPropertyChanged(nameof(ListViewItem));
        }

        public bool SmallIconViewItem
        {
            get => ViewModeIndex == 0;
            set { if (value) ViewModeIndex = 0; }
        }
        public bool MediumIconViewItem
        {
            get => ViewModeIndex == 1;
            set { if (value) ViewModeIndex = 1; }
        }
        public bool LargeIconViewItem
        {
            get => ViewModeIndex == 2;
            set { if (value) ViewModeIndex = 2; }
        }
        public bool ContentViewItem
        {
            get => ViewModeIndex == 3;
            set { if (value) ViewModeIndex = 3; }
        }
        public bool ListViewItem
        {
            get => ViewModeIndex == 4;
            set { if (value) ViewModeIndex = 4; }
        }

        [ObservableProperty]
        public partial int WallpaperDisplayMode { get; set; }

        partial void OnWallpaperDisplayModeChanged(int value)
        {
            OnPropertyChanged(nameof(IsIconMode));
            OnPropertyChanged(nameof(IsContentMode));
            OnPropertyChanged(nameof(IsListMode));
            OnPropertyChanged(nameof(IconModeVisibility));
            OnPropertyChanged(nameof(ContentModeVisibility));
            OnPropertyChanged(nameof(ListModeVisibility));

            if (!_isUpdatingViewMode)
            {
                _isUpdatingViewMode = true;
                ViewModeIndex = value switch
                {
                    (int)WallpaperDisplayModes.Content => 3,
                    (int)WallpaperDisplayModes.List => 4,
                    _ => WallpaperViewIndex
                };
                _isUpdatingViewMode = false;
            }
        }

        public bool IsIconMode
        {
            get => WallpaperDisplayMode == (int)WallpaperDisplayModes.Icon;
            set
            {
                if (value) WallpaperDisplayMode = (int)WallpaperDisplayModes.Icon;
            }
        }

        public bool IsContentMode
        {
            get => WallpaperDisplayMode == (int)WallpaperDisplayModes.Content;
            set
            {
                if (value) WallpaperDisplayMode = (int)WallpaperDisplayModes.Content;
            }
        }

        public bool IsListMode
        {
            get => WallpaperDisplayMode == (int)WallpaperDisplayModes.List;
            set
            {
                if (value) WallpaperDisplayMode = (int)WallpaperDisplayModes.List;
            }
        }

        public Microsoft.UI.Xaml.Visibility IconModeVisibility
        {
            get => IsIconMode ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        public Microsoft.UI.Xaml.Visibility ContentModeVisibility
        {
            get => IsContentMode ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        public Microsoft.UI.Xaml.Visibility ListModeVisibility
        {
            get => IsListMode ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        [ObservableProperty]
        public partial int WallpaperListMinWidth { get; set; }

        [ObservableProperty]
        public partial bool LeftSplitViewPaneOpen { get; set; }

        [ObservableProperty]
        public partial bool RightSplitViewPaneOpen { get; set; }

        // === 右侧面板模式 ===
        // RightPanelIndex: 0=详情面板, 1=属性面板（查看菜单二选一，持久化进 Papers 配置段）
        [ObservableProperty]
        public partial int RightPanelIndex { get; set; }

        partial void OnRightPanelIndexChanged(int value)
        {
            OnPropertyChanged(nameof(DetailPanelViewItem));
            OnPropertyChanged(nameof(PropertyPanelViewItem));
            OnPropertyChanged(nameof(DetailPanelVisibility));
            OnPropertyChanged(nameof(PropertyPanelVisibility));
        }

        public bool DetailPanelViewItem
        {
            get => RightPanelIndex == 0;
            set
            {
                if (value) RightPanelIndex = 0;
            }
        }
        public bool PropertyPanelViewItem
        {
            get => RightPanelIndex == 1;
            set
            {
                if (value) RightPanelIndex = 1;
            }
        }

        public Microsoft.UI.Xaml.Visibility DetailPanelVisibility
            => RightPanelIndex == 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

        public Microsoft.UI.Xaml.Visibility PropertyPanelVisibility
            => RightPanelIndex == 1 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

        [ObservableProperty]
        public partial bool DetailSelectionEnabled { get; set; }

        [ObservableProperty]
        public partial int FilterResultResponseDelay { get; set; }

        [ObservableProperty]
        public partial int SortOrder { get; set; }

        partial void OnSortOrderChanged(int value)
        {
            OnPropertyChanged(nameof(SortByName));
            OnPropertyChanged(nameof(SortBySubTime));
            OnPropertyChanged(nameof(SortByLastTime));
            OnPropertyChanged(nameof(SortByFileSize));
            OnPropertyChanged(nameof(SortByAcfUpdateTime));
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
        public bool SortByAcfUpdateTime
        {
            get => SortOrder == 4;
            set
            {
                if (value) SortOrder = 4;
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

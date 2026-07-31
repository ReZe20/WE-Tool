using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace WE_Tool.ViewModels;

public enum ComponentsDisplayModes
{
    Icon = 0,
    Content = 1,
    List = 2
}

public partial class ComponentsDisplayViewModel : ObservableObject
{
    // ===================== 视图模式 =====================
    // ComponentViewIndex: 图标尺寸索引 (0=小, 1=中, 2=大)，仅图标模式有效
    [ObservableProperty]
    public partial int ComponentViewIndex { get; set; }

    partial void OnComponentViewIndexChanged(int value)
    {
        ComponentListMinWidth = value switch
        {
            0 => 180,
            1 => 240,
            2 => 300,
            _ => 180
        };
        OnPropertyChanged(nameof(SmallIconItem));
        OnPropertyChanged(nameof(MediumIconItem));
        OnPropertyChanged(nameof(LargeIconItem));

        if (!_isUpdatingViewMode)
        {
            _isUpdatingViewMode = true;
            ViewModeIndex = ComponentDisplayMode switch
            {
                (int)ComponentsDisplayModes.Content => 3,
                (int)ComponentsDisplayModes.List => 4,
                _ => value
            };
            _isUpdatingViewMode = false;
        }
    }

    public bool SmallIconItem
    {
        get => ComponentViewIndex == 0;
        set { if (value) ComponentViewIndex = 0; }
    }
    public bool MediumIconItem
    {
        get => ComponentViewIndex == 1;
        set { if (value) ComponentViewIndex = 1; }
    }
    public bool LargeIconItem
    {
        get => ComponentViewIndex == 2;
        set { if (value) ComponentViewIndex = 2; }
    }

    private bool _isUpdatingViewMode;

    // ViewModeIndex: 菜单索引 (0=小, 1=中, 2=大, 3=内容, 4=列表)
    [ObservableProperty]
    public partial int ViewModeIndex { get; set; }

    partial void OnViewModeIndexChanged(int value)
    {
        if (_isUpdatingViewMode)
        {
            // 联动赋值路径（OnComponentViewIndexChanged/OnComponentDisplayModeChanged 内部）：
            // 属性通知同样必须发出，否则菜单选中状态不会刷新
            OnPropertyChanged(nameof(SmallIconViewItem));
            OnPropertyChanged(nameof(MediumIconViewItem));
            OnPropertyChanged(nameof(LargeIconViewItem));
            OnPropertyChanged(nameof(ContentViewItem));
            OnPropertyChanged(nameof(ListViewItem));
            return;
        }
        _isUpdatingViewMode = true;

        ComponentDisplayMode = value switch
        {
            3 => (int)ComponentsDisplayModes.Content,
            4 => (int)ComponentsDisplayModes.List,
            _ => (int)ComponentsDisplayModes.Icon
        };

        ComponentViewIndex = (value == 3 || value == 4) ? 0 : value;

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

    // ComponentDisplayMode: 实际显示模式 (0=图标, 1=内容, 2=列表)
    [ObservableProperty]
    public partial int ComponentDisplayMode { get; set; }

    partial void OnComponentDisplayModeChanged(int value)
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
                (int)ComponentsDisplayModes.Content => 3,
                (int)ComponentsDisplayModes.List => 4,
                _ => ComponentViewIndex
            };
            _isUpdatingViewMode = false;
        }
    }

    public bool IsIconMode
    {
        get => ComponentDisplayMode == (int)ComponentsDisplayModes.Icon;
        set { if (value) ComponentDisplayMode = (int)ComponentsDisplayModes.Icon; }
    }

    public bool IsContentMode
    {
        get => ComponentDisplayMode == (int)ComponentsDisplayModes.Content;
        set { if (value) ComponentDisplayMode = (int)ComponentsDisplayModes.Content; }
    }

    public bool IsListMode
    {
        get => ComponentDisplayMode == (int)ComponentsDisplayModes.List;
        set { if (value) ComponentDisplayMode = (int)ComponentsDisplayModes.List; }
    }

    public Visibility IconModeVisibility
        => IsIconMode ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ContentModeVisibility
        => IsContentMode ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ListModeVisibility
        => IsListMode ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    public partial int ComponentListMinWidth { get; set; }

    [ObservableProperty]
    public partial bool AutoPlayGif { get; set; } = true;

    [ObservableProperty]
    public partial bool IsComponentEnterAnimationEnabled { get; set; } = true;

    // ===================== 标签显示模式 =====================
    [ObservableProperty]
    public partial int ComponentTagDisplayIndex { get; set; }

    partial void OnComponentTagDisplayIndexChanged(int value)
    {
        OnPropertyChanged(nameof(TypeDisplayInTag));
        OnPropertyChanged(nameof(RatingDisplayInTag));
        OnPropertyChanged(nameof(SourceDisplayInTag));
        OnPropertyChanged(nameof(TagDisplayInTag));
        OnPropertyChanged(nameof(NoneDisplayInTag));
        OnPropertyChanged(nameof(TagDisplayVisibility));
    }

    /// <summary>角标是否显示（0~3 显示，4=无 隐藏）</summary>
    public Visibility TagDisplayVisibility
        => ComponentTagDisplayIndex == 4 ? Visibility.Collapsed : Visibility.Visible;

    public bool TypeDisplayInTag
    {
        get => ComponentTagDisplayIndex == 0;
        set { if (value) ComponentTagDisplayIndex = 0; }
    }
    public bool RatingDisplayInTag
    {
        get => ComponentTagDisplayIndex == 1;
        set { if (value) ComponentTagDisplayIndex = 1; }
    }
    public bool SourceDisplayInTag
    {
        get => ComponentTagDisplayIndex == 2;
        set { if (value) ComponentTagDisplayIndex = 2; }
    }
    public bool TagDisplayInTag
    {
        get => ComponentTagDisplayIndex == 3;
        set { if (value) ComponentTagDisplayIndex = 3; }
    }
    public bool NoneDisplayInTag
    {
        get => ComponentTagDisplayIndex == 4;
        set { if (value) ComponentTagDisplayIndex = 4; }
    }

    // ===================== 排序 =====================
    [ObservableProperty]
    public partial int SortOrder { get; set; }

    partial void OnSortOrderChanged(int value)
    {
        OnPropertyChanged(nameof(SortByName));
        OnPropertyChanged(nameof(SortByUpdateTime));
        OnPropertyChanged(nameof(SortBySize));
    }

    public bool SortByName
    {
        get => SortOrder == 0;
        set { if (value) SortOrder = 0; }
    }
    public bool SortByUpdateTime
    {
        get => SortOrder == 1;
        set { if (value) SortOrder = 1; }
    }
    public bool SortBySize
    {
        get => SortOrder == 2;
        set { if (value) SortOrder = 2; }
    }

    [ObservableProperty]
    public partial bool IsSortAscending { get; set; } = true;

    partial void OnIsSortAscendingChanged(bool value)
    {
        OnPropertyChanged(nameof(SortDirectionGlyph));
    }

    public string SortDirectionGlyph => IsSortAscending ? "\uE70D" : "\uE70E";

    // ===================== 面板与滚动条 =====================
    [ObservableProperty]
    public partial bool IsBottomBarOpen { get; set; } = true;

    partial void OnIsBottomBarOpenChanged(bool value)
    {
        BottomBarHeight = value ? new GridLength(50) : new GridLength(0);
    }

    public GridLength BottomBarHeight
    {
        get => IsBottomBarOpen ? new GridLength(50) : new GridLength(0);
        set => SetProperty(ref field, value);
    }

    [ObservableProperty]
    public partial bool DetailSelectionEnabled { get; set; } = true;

    [ObservableProperty]
    public partial int FilterResultResponseDelay { get; set; } = 1000;

    [ObservableProperty]
    public partial bool LeftSplitViewPaneOpen { get; set; } = true;

    [ObservableProperty]
    public partial bool RightSplitViewPaneOpen { get; set; } = true;

    [ObservableProperty]
    public partial bool IsAnnotatedScrollBarEnabled { get; set; }
}

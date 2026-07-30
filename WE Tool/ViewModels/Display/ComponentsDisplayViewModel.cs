using CommunityToolkit.Mvvm.ComponentModel;

namespace WE_Tool.ViewModels;

public partial class ComponentsDisplayViewModel : ObservableObject
{
    // ===================== 视图模式 =====================
    [ObservableProperty]
    public partial int ComponentViewIndex { get; set; }

    partial void OnComponentViewIndexChanged(int value)
    {
        UpdateViewBoolProps();
    }

    private bool _smallIconViewItem = true;
    public bool SmallIconViewItem
    {
        get => _smallIconViewItem;
        set { if (value && SetProperty(ref _smallIconViewItem, true)) ComponentViewIndex = 0; }
    }

    private bool _mediumIconViewItem;
    public bool MediumIconViewItem
    {
        get => _mediumIconViewItem;
        set { if (value && SetProperty(ref _mediumIconViewItem, true)) ComponentViewIndex = 1; }
    }

    private bool _largeIconViewItem;
    public bool LargeIconViewItem
    {
        get => _largeIconViewItem;
        set { if (value && SetProperty(ref _largeIconViewItem, true)) ComponentViewIndex = 2; }
    }

    private bool _contentViewItem;
    public bool ContentViewItem
    {
        get => _contentViewItem;
        set { if (value && SetProperty(ref _contentViewItem, true)) ComponentViewIndex = 3; }
    }

    private bool _listViewItem;
    public bool ListViewItem
    {
        get => _listViewItem;
        set { if (value && SetProperty(ref _listViewItem, true)) ComponentViewIndex = 4; }
    }

    public void UpdateViewBoolProps()
    {
        SmallIconViewItem = ComponentViewIndex == 0;
        MediumIconViewItem = ComponentViewIndex == 1;
        LargeIconViewItem = ComponentViewIndex == 2;
        ContentViewItem = ComponentViewIndex == 3;
        ListViewItem = ComponentViewIndex == 4;
    }

    // ===================== 标签显示模式 =====================
    [ObservableProperty]
    public partial int ComponentTagDisplayIndex { get; set; }

    partial void OnComponentTagDisplayIndexChanged(int value)
    {
        UpdateTagBoolProps();
    }

    private bool _typeDisplayInTag = true;
    public bool TypeDisplayInTag
    {
        get => _typeDisplayInTag;
        set { if (value && SetProperty(ref _typeDisplayInTag, true)) ComponentTagDisplayIndex = 0; }
    }

    private bool _ratingDisplayInTag;
    public bool RatingDisplayInTag
    {
        get => _ratingDisplayInTag;
        set { if (value && SetProperty(ref _ratingDisplayInTag, true)) ComponentTagDisplayIndex = 1; }
    }

    private bool _sourceDisplayInTag;
    public bool SourceDisplayInTag
    {
        get => _sourceDisplayInTag;
        set { if (value && SetProperty(ref _sourceDisplayInTag, true)) ComponentTagDisplayIndex = 2; }
    }

    private bool _tagDisplayInTag;
    public bool TagDisplayInTag
    {
        get => _tagDisplayInTag;
        set { if (value && SetProperty(ref _tagDisplayInTag, true)) ComponentTagDisplayIndex = 3; }
    }

    private bool _noneDisplayInTag;
    public bool NoneDisplayInTag
    {
        get => _noneDisplayInTag;
        set { if (value && SetProperty(ref _noneDisplayInTag, true)) ComponentTagDisplayIndex = 4; }
    }

    public void UpdateTagBoolProps()
    {
        TypeDisplayInTag = ComponentTagDisplayIndex == 0;
        RatingDisplayInTag = ComponentTagDisplayIndex == 1;
        SourceDisplayInTag = ComponentTagDisplayIndex == 2;
        TagDisplayInTag = ComponentTagDisplayIndex == 3;
        NoneDisplayInTag = ComponentTagDisplayIndex == 4;
    }

    // ===================== 排序 =====================
    [ObservableProperty]
    public partial int SortOrder { get; set; }

    partial void OnSortOrderChanged(int value)
    {
        UpdateSortBoolProps();
    }

    private bool _sortByName = true;
    public bool SortByName
    {
        get => _sortByName;
        set { if (value && SetProperty(ref _sortByName, true)) SortOrder = 0; }
    }

    private bool _sortByUpdateTime;
    public bool SortByUpdateTime
    {
        get => _sortByUpdateTime;
        set { if (value && SetProperty(ref _sortByUpdateTime, true)) SortOrder = 1; }
    }

    private bool _sortBySize;
    public bool SortBySize
    {
        get => _sortBySize;
        set { if (value && SetProperty(ref _sortBySize, true)) SortOrder = 2; }
    }

    public void UpdateSortBoolProps()
    {
        SortByName = SortOrder == 0;
        SortByUpdateTime = SortOrder == 1;
        SortBySize = SortOrder == 2;
    }

    [ObservableProperty]
    public partial bool IsSortAscending { get; set; } = true;

    // ===================== 面板与滚动条 =====================
    [ObservableProperty]
    public partial bool LeftSplitViewPaneOpen { get; set; } = true;

    [ObservableProperty]
    public partial bool RightSplitViewPaneOpen { get; set; } = true;

    [ObservableProperty]
    public partial bool IsAnnotatedScrollBarEnabled { get; set; }
}

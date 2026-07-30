using CommunityToolkit.Mvvm.ComponentModel;

namespace WE_Tool.ViewModels;

public partial class ComponentsFilterViewModel : ObservableObject
{
    // --- Expander 展开状态 ---
    [ObservableProperty]
    public partial bool TypeExpander { get; set; } = true;

    [ObservableProperty]
    public partial bool RatingExpander { get; set; } = true;

    [ObservableProperty]
    public partial bool TagsExpander { get; set; } = true;

    // --- 类型 ---
    [ObservableProperty]
    public partial bool Layers { get; set; } = true;

    [ObservableProperty]
    public partial bool Scripts { get; set; } = true;

    [ObservableProperty]
    public partial bool Effects { get; set; } = true;

    // --- 年龄 ---
    [ObservableProperty]
    public partial bool Everyone { get; set; } = true;

    [ObservableProperty]
    public partial bool Questionable { get; set; } = true;

    [ObservableProperty]
    public partial bool Mature { get; set; } = true;

    // --- 标签 ---
    [ObservableProperty]
    public partial bool UnspecifiedGenre { get; set; }

    [ObservableProperty]
    public partial bool Abstract { get; set; }

    [ObservableProperty]
    public partial bool Anime { get; set; }

    [ObservableProperty]
    public partial bool AudioVisualizer { get; set; }

    [ObservableProperty]
    public partial bool Background { get; set; }

    [ObservableProperty]
    public partial bool Cgi { get; set; }

    [ObservableProperty]
    public partial bool Character { get; set; }

    [ObservableProperty]
    public partial bool Clock { get; set; }

    [ObservableProperty]
    public partial bool Fire { get; set; }

    [ObservableProperty]
    public partial bool Interactive { get; set; }

    [ObservableProperty]
    public partial bool Magic { get; set; }

    [ObservableProperty]
    public partial bool Memes { get; set; }

    [ObservableProperty]
    public partial bool Nature { get; set; }

    [ObservableProperty]
    public partial bool PostProcessing { get; set; }

    [ObservableProperty]
    public partial bool Smoke { get; set; }

    [ObservableProperty]
    public partial bool Space { get; set; }

    [ObservableProperty]
    public partial bool Sports { get; set; }

    [ObservableProperty]
    public partial bool Technology { get; set; }

    [ObservableProperty]
    public partial bool Vehicle { get; set; }
}

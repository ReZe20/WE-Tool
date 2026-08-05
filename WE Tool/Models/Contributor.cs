namespace WE_Tool.Models;

/// <summary>贡献者(照抄 BetterLyrics Contributor 模型)</summary>
public class Contributor
{
    public string Header { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string AvatarSource { get; set; } = string.Empty;
    public string Badges { get; set; } = string.Empty;
}

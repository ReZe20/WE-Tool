namespace WE_Tool;

/// <summary>
/// 贡献者数据(硬编码,替代外部 CSV 文件)。
/// 原数据源:Assets/Contributors.csv 与 Assets/ContributorsRepkg.csv(发布包不再分发这两个文件)。
/// 新增贡献者时:在下方两个数组中各加一项,并同步更新 Assets 下的 CSV(保持源码一致性)。
/// </summary>
internal static class ContributorsData
{
    /// <summary>WE Tool 贡献者(原 Contributors.csv)</summary>
    public static readonly Models.Contributor[] Main =
    [
        new() { Header = "ReZe20", AvatarSource = "https://github.com/ReZe20.png", Badges = "", Description = "https://github.com/ReZe20" },
        new() { Header = "GeraltVitas", AvatarSource = "https://github.com/GeraltVitas.png", Badges = "", Description = "https://github.com/GeraltVitas" }
    ];

    /// <summary>RePKG_Re 贡献者(原 ContributorsRepkg.csv)</summary>
    public static readonly Models.Contributor[] Repkg =
    [
        new() { Header = "notscuffed", AvatarSource = "https://github.com/notscuffed.png", Badges = "", Description = "https://github.com/notscuffed" },
        new() { Header = "ReZe20", AvatarSource = "https://github.com/ReZe20.png", Badges = "", Description = "https://github.com/ReZe20" },
        new() { Header = "vitalline", AvatarSource = "https://github.com/vitalline.png", Badges = "", Description = "https://github.com/vitalline" },
        new() { Header = "dependabot[bot]", AvatarSource = "https://github.com/dependabot.png", Badges = "", Description = "https://github.com/dependabot" },
        new() { Header = "onetiaoxianfish", AvatarSource = "https://github.com/onetiaoxianfish.png", Badges = "", Description = "https://github.com/onetiaoxianfish" },
    ];
}

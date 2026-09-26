namespace WE_Tool.Models;

/// <summary>
/// 「转为移动版」一条壁纸的打包参数,一律可空 —— null = 跟随全局,与 repkg 的 wallpapers[].options 同形。
/// 只有写了的键才会进清单,所以"和全局一样"与"特意改成全局那个值"在产物上是同一件事
/// (UI 侧把改成等于全局的那一格直接清成 null,见 <see cref="MpkgQueueItem"/>)。
/// </summary>
public sealed class MpkgEntryOptions
{
    /// <summary>缩小档位序号(0=原始 1=2× 2=4×),与设置页同一套;除数由 RepkgCliService 换算。</summary>
    public required int Tier { get; init; }

    public bool? KeepAudio { get; init; }
    public bool? UseLz4 { get; init; }
    public bool? ShaderCompat { get; init; }

    /// <summary>ETC2 编码:null = 由档位派生(÷1 不编、2× 起编)。</summary>
    public bool? EncodeEtc2 { get; init; }

    /// <summary>纹理照搬:true = 所有 .tex 逐字节搬运、不物化(repkg 的 mpkgNoDematerialize)。null = 物化。</summary>
    public bool? CopyTextures { get; init; }

    /// <summary>缩小 DXT:true = DXT 块格式也解码重缩,发出去的仍是 RGBA8(repkg 的 mpkgShrinkDx)。
    /// null = 只在同时发 ETC2 时缩(那是真机验过的那对)。</summary>
    public bool? ShrinkDx { get; init; }

    /// <summary>
    /// .mpkg 文件名主干用标题还是创意工坊 ID:0=标题 1=ID。null = 跟随面板总控那一格。
    /// 它不进 repkg 的 options —— 名字是我们这边算完交给 <c>outputName</c> 的,
    /// 但它是逐行覆盖,所以角标和"重置"都算它一份。
    /// 只影响磁盘上的文件名:手机读的是包内 project.json 的 title,与文件名无关(真机与 WE 自家导出都这么对)。
    /// </summary>
    public int? NameMode { get; init; }

    /// <summary>除了必发的档位之外,还有没有逐行覆盖。</summary>
    public bool HasOverride =>
        KeepAudio is not null || UseLz4 is not null || ShaderCompat is not null || EncodeEtc2 is not null
        || CopyTextures is not null || ShrinkDx is not null || NameMode is not null;
}

/// <summary>
/// mode=mpkg 那几个前端硬编码开关的"全局值",单一来源:清单里的 options 块和逐行弹层显示的回落值
/// 都从这里取,免得界面上写着"跟随全局"而全局其实是另一套数。
/// 语义与 repkg 侧的取反关系(keepAudio/noLz4/mpkgNoShaderCompat)在 RepkgCliService 里翻译。
/// </summary>
public static class MpkgPackingDefaults
{
    public const string Magic = "PKGM0019";

    /// <summary>移动端不放音频:WE 自己的导出也不放。</summary>
    public const bool KeepAudio = false;

    /// <summary>纹理 LZ4 压缩默认开。</summary>
    public const bool UseLz4 = true;

    /// <summary>着色器整数字面量补 <c>.0</c> 的兼容改写默认开(关了移动侧会编译失败画成白块)。</summary>
    public const bool ShaderCompat = true;

    /// <summary>纹理照搬默认关 —— 关着才是"物化成 RGBA8/ETC2"的正常路径。</summary>
    public const bool CopyTextures = false;

    /// <summary>DXT 解码重缩默认关:那条解码路以前只跟 ETC2 一起走(真机验过的配对),而且 DXT 比 RGBA8 省字节。</summary>
    public const bool ShrinkDx = false;

    /// <summary>
    /// 新入队那一行的默认档(0=原始 1=2× 2=4×)。设置页那格删了,所以这是队列面板之外的唯一默认值 ——
    /// 进队之后能改档的地方只有总控那根 slider 和逐行 slider。
    /// </summary>
    public const int Tier = 0;

    /// <summary>
    /// 这一格不是常量:它是面板总控那根「文件重命名」二选一的当前值(0=标题 1=创意工坊 ID),默认 0。
    /// 行上显示的是生效值,没覆盖过的行就报这个,所以它得是个能改的地方。
    /// </summary>
    public static int NameMode { get; set; }
}

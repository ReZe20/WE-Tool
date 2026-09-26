namespace WE_Tool.Models;

/// <summary>
/// repkg <c>mode: inspect</c> 对一张壁纸的只读结论(一个壁纸可能有多个包,这里已经按壁纸加总)。
///
/// 它存在的意义就一句话:<c>.tex</c> 被照搬时转换器什么都不说,产物和上一档一样大,
/// 而"这一档白选"这件事本来可以在点开始之前就说出来。数字全部来自 repkg 自己那份判据
/// (<c>MobileTextureMaterializer.WouldReduce</c>),我们只是搬运,不在这边重算一遍。
/// </summary>
public sealed class MpkgProbe
{
    /// <summary>探测过几个包。0 = 这一行没有找到任何 .pkg/.mpkg。</summary>
    public int Packages { get; init; }

    /// <summary>.tex 条目总数。</summary>
    public int Tex { get; init; }

    /// <summary>这一行的档位真会缩掉的条数。为 0 而 <see cref="Tex"/> 不为 0,就是"缩不动"。</summary>
    public int WouldReduce { get; init; }

    /// <summary>DXT1/3/5 的条数与字节:它就是"选了 4× 却没缩"最常见的那句解释。</summary>
    public int Dxt { get; init; }

    public long DxtBytes { get; init; }
    public long Bytes { get; init; }

    /// <summary>至少有一个包的表读不动。</summary>
    public bool Failed { get; init; }

    /// <summary>DXT 占整个包的字节比例(0-100)。包字节为 0 时给 0。</summary>
    public int DxtSharePercent => Bytes <= 0 ? 0 : (int)(DxtBytes * 100 / Bytes);
}

using System.Text.Json.Serialization;

namespace WE_Tool.Json
{
    /// <summary>Steamworks 桥接 IPC 命令(op/workshopId 键名保持小写,子模式按旧协议解析,不可改名)。</summary>
    public sealed record BridgeCommand(
        [property: JsonPropertyName("op")] string Op,
        [property: JsonPropertyName("workshopId")] string? WorkshopId = null);

    /// <summary>
    /// 桥接回包协议类型。键名是行协议的线上契约,主程序按小写键解析,不可改名。
    /// NativeAOT 关掉了反射式序列化,回包不能用匿名类型(实测 InvalidOperationException),
    /// 必须走源生成上下文(见 <see cref="BridgeJsonContext"/>)。
    /// </summary>
    public sealed record StatusReply(
        [property: JsonPropertyName("op")] string Op,
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("user")] string? User,
        [property: JsonPropertyName("steamId")] string? SteamId);

    public sealed record UnsubscribeReply(
        [property: JsonPropertyName("op")] string Op,
        [property: JsonPropertyName("ok")] bool Ok);

    public sealed record ErrorReply(
        [property: JsonPropertyName("op")] string Op,
        [property: JsonPropertyName("message")] string Message);

    /// <summary>LoadPapers 补写 project.json 的标题/类型条目(title/type 键名与原匿名类型一致)。</summary>
    public sealed record LoadPapersEntry(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("type")] string Type);
}
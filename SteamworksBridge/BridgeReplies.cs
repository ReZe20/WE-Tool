using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamworksBridge;

/// <summary>
/// 桥接回包协议类型。NativeAOT 关掉了反射式序列化,回包不能用匿名类型
/// (实测 AOT 下 InvalidOperationException: Reflection-based serialization has been disabled)。
/// 键名是行协议的线上契约,主程序按小写键解析,不可改名。
/// </summary>
internal sealed record StatusReply(
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("user")] string? User,
    [property: JsonPropertyName("steamId")] string? SteamId);

internal sealed record UnsubscribeReply(
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("ok")] bool Ok);

internal sealed record ErrorReply(
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("message")] string Message);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(StatusReply))]
[JsonSerializable(typeof(UnsubscribeReply))]
[JsonSerializable(typeof(ErrorReply))]
internal partial class BridgeReplyJsonContext : JsonSerializerContext { }

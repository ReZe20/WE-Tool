using System.Text.Json.Serialization;

namespace WE_Tool.Json
{
    /// <summary>SteamworksBridge.exe IPC 命令(op/workshopId 键名保持小写,子进程按旧协议解析,不可改名)。</summary>
    public sealed record BridgeCommand(
        [property: JsonPropertyName("op")] string Op,
        [property: JsonPropertyName("workshopId")] string? WorkshopId = null);

    /// <summary>LoadPapers 补写 project.json 的标题/类型条目(title/type 键名与原匿名类型一致)。</summary>
    public sealed record LoadPapersEntry(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("type")] string Type);
}
using System.Text.Json.Serialization;

namespace WE_Tool.Json
{
    /// <summary>
    /// SteamworksBridge IPC 专用序列化上下文:必须单行输出。
    /// 桥接协议为行协议(stdin 一行一个 JSON,桥接 Console.ReadLine 逐行解析),
    /// 全局 JsonContext 的 WriteIndented=true 会把命令拆成多行导致解析失败——不得改用它。
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(BridgeCommand))]
    internal partial class BridgeJsonContext : JsonSerializerContext { }
}

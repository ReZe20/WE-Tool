using System.Text.Json.Serialization;

namespace WE_Tool.Json
{
    /// <summary>
    /// 属性副窗口链路专用序列化上下文:必须单行输出(管道是行协议),
    /// 不得改用全局 JsonContext(它 WriteIndented=true,会把一条消息拆成多行)。
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(PropertyWindowSnapshot))]
    [JsonSerializable(typeof(PropertyWindowMessage))]
    [JsonSerializable(typeof(WhitelistWindowSnapshot))]
    internal partial class PropertyWindowJsonContext : JsonSerializerContext { }
}

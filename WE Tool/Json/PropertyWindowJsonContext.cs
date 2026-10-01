using System.Text.Json.Serialization;

namespace WE_Tool.Json
{
    /// <summary>
    /// 副窗口链路(属性 / 白名单 / 移动版队列)专用序列化上下文:必须单行输出(管道是行协议),
    /// 不得改用全局 JsonContext(它 WriteIndented=true,会把一条消息拆成多行)。
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(PropertyWindowSnapshot))]
    [JsonSerializable(typeof(PropertyWindowMessage))]
    [JsonSerializable(typeof(WhitelistWindowSnapshot))]
    [JsonSerializable(typeof(MpkgWindowSnapshot))]
    internal partial class PropertyWindowJsonContext : JsonSerializerContext { }
}

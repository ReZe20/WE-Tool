using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoBackupService;

/// <summary>
/// System.Text.Json 源生成器上下文:NativeAOT 裁剪下反射序列化被禁用,必须走源生成器。
/// 只注册服务需要反序列化的类型。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ServiceConfig))]
[JsonSerializable(typeof(VdfWatcher.ProjectMeta))]
internal partial class ServiceJsonContext : JsonSerializerContext
{
}

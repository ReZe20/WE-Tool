using System.Text.Json;
using System.Text.Json.Serialization;

namespace WE_Tool.Models
{
    /// <summary>
    /// project.json 元数据(源生成器序列化用,必须 internal/public 可见)。
    /// 键名全小写(contentrating/visibility/workshopid)由 JsonContext 的
    /// PropertyNameCaseInsensitive 统一匹配。
    /// </summary>
    internal sealed record ProjectMetadata
    {
        public string? Type { get; init; }
        public string? Category { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? Preset { get; init; }
        /// <summary>壁纸可见性:正常为 public,被 Steam 下架后变为 private(订阅异常判定用)。</summary>
        public string? Visibility { get; init; }
        public string? Dependency { get; init; }
        public string? File { get; init; }
        public string? Preview { get; init; }
        public string? Title { get; init; }
        public string? Contentrating { get; init; }
        public string? Description { get; init; }
        public JsonElement? Tags { get; init; }
        public string? Workshopid { get; init; }
    }
}
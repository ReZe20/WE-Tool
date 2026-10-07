using System.Text.Json.Serialization;
using System.Collections.Generic;
using WE_Tool.Helper;
using WE_Tool.Models;

namespace WE_Tool.Json
{
    /// <summary>
    /// System.Text.Json 源生成器上下文:本类只声明"哪些类型参与序列化",
    /// 实际的读写死码由编译器在构建期生成(零反射,裁剪/AOT 安全)。
    /// PropertyNameCaseInsensitive:project.json 的键全小写(contentrating/visibility),
    /// config.json 键与属性名一致(PascalCase)——两者统一开启大小写不敏感。
    /// WriteIndented:config.json 落盘缩进与旧反射行为一致。
    /// 注意:repkg manifest 因 options 含动态字典(object 多态),不注册类型,
    /// 改用 JsonNode 手写(见 RepkgCliService.WriteManifest)——DOM 零反射,AOT 安全。
    /// </summary>
    [JsonSourceGenerationOptions(
        PropertyNameCaseInsensitive = true,
        WriteIndented = true)]
    [JsonSerializable(typeof(AppSettings))]
    [JsonSerializable(typeof(ProjectMetadata))]
    [JsonSerializable(typeof(BridgeCommand))]
    [JsonSerializable(typeof(LoadPapersEntry))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(List<string>))]
    // 预览的属性热更消息要把单个值原样嵌进 JSON 字面量,三种值类型各占一个源生成的标量转换器
    // (拼字符串会写出非法 JSON,反射式 Serialize(object) 在 AOT 下是 IL2026)
    [JsonSerializable(typeof(bool))]
    [JsonSerializable(typeof(double))]
    [JsonSerializable(typeof(WallpaperScanner.CacheFile))]
    internal partial class JsonContext : JsonSerializerContext { }
}
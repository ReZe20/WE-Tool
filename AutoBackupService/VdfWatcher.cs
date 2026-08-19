using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoBackupService;

/// <summary>
/// VDF 订阅文件解析 + project.json 读取 + 类型/分级筛选匹配。
/// 与主程序 WallpaperScanner.GetActiveSubscribedIDs 使用相同的 VDF 正则与语义。
/// </summary>
public static class VdfWatcher
{
    /// <summary>与主程序一致:匹配 publishedfileid 与 disabled_locally。</summary>
    private static readonly Regex VdfEntryRegex = new(
        @"""publishedfileid""\s+""(\d+)""[^}]*""disabled_locally""\s+""(\d+)""",
        RegexOptions.Compiled);

    /// <summary>解析 VDF 中有效订阅(未本地禁用)的工坊壁纸 ID 集;文件缺/解析失败返回空集。</summary>
    public static HashSet<string> ParseSubscribedIds(string vdfPath)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(vdfPath) || !File.Exists(vdfPath))
        {
            Log.Write($"VDF 不存在: {vdfPath}");
            return result;
        }
        try
        {
            var content = File.ReadAllText(vdfPath);
            foreach (Match match in VdfEntryRegex.Matches(content))
            {
                var id = match.Groups[1].Value;
                var disabled = match.Groups[2].Value;
                if (disabled != "1")
                    result.Add(id);
            }
            Log.Write($"VDF 解析完成: 有效订阅 {result.Count} 个");
        }
        catch (Exception ex)
        {
            Log.Write(ex, $"解析 VDF 失败: {vdfPath}");
        }
        return result;
    }

    /// <summary>读取 project.json 的 type 与 contentrating;文件 absent/解析失败返回 null。</summary>
    public static ProjectMeta? ReadProjectMeta(string projectJsonPath)
    {
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(projectJsonPath),
                ServiceJsonContext.Default.ProjectMeta);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>轻量 project.json 元数据(只取筛选所需字段,容忍缺失)。</summary>
    public sealed class ProjectMeta
    {
        public string? Type { get; set; }
        public string? Contentrating { get; set; }

        /// <summary>类型归一化:Scene/Video/Web/Application/Preset/Unknown(大小写不敏感)。</summary>
        public string NormalizedType => Type switch
        {
            null => "unknown",
            _ => Type.Trim().ToLowerInvariant() switch
            {
                "scene" => "scene",
                "video" => "video",
                "web" => "web",
                "application" => "application",
                "preset" => "preset",
                _ => "unknown"
            }
        };

        /// <summary>分级归一化:Everyone→g, Teen→pg, Mature→r, 缺失→g(与主程序一致)。</summary>
        public string NormalizedRating => Contentrating switch
        {
            null => "g",
            _ => Contentrating.Trim().ToLowerInvariant() switch
            {
                "teen" => "pg",
                "mature" => "r",
                _ => "g"
            }
        };
    }
}

/// <summary>根据 AutoBackup 配置判定某项目是否命中筛选。</summary>
public static class AutoBackupFilter
{
    public static bool Matches(ServiceConfig.AutoBackupConfig? cfg, VdfWatcher.ProjectMeta? meta)
    {
        if (cfg == null || meta == null) return false;

        // 类型开关
        return meta.NormalizedType switch
        {
            "scene" => cfg.TypeScene,
            "video" => cfg.TypeVideo,
            "web" => cfg.TypeWeb,
            "application" => cfg.TypeApplication,
            "preset" => cfg.TypePreset,
            _ => cfg.TypeUnknown
        }
        // 分级开关(全部关闭时不备份,防御)
        && (meta.NormalizedRating switch
        {
            "pg" => cfg.RatingPg,
            "r" => cfg.RatingR,
            _ => cfg.RatingG
        });
    }
}

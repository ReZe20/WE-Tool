using System.IO;
using System.Text.Json;

namespace AutoBackupService;

/// <summary>
/// 服务侧配置模型:只取 WE Tool config.json 中服务需要的字段。
/// 字段名与主程序 AppSettings 对齐(PascalCase),读取时大小写不敏感。
/// </summary>
public sealed class ServiceConfig
{
    /// <summary>主程序 config.json(%LOCALAPPDATA%/WE_Tool/config.json)中 AutoBackup 段。</summary>
    public AutoBackupConfig? AutoBackup { get; set; }

    /// <summary>主程序 config.json 中 Path 段。</summary>
    public PathConfigDto? Path { get; set; }

    /// <summary>路径配置(与主程序 PathConfig 字段名对齐)。</summary>
    public sealed class PathConfigDto
    {
        public string VdfPath { get; set; } = "";
        public string WorkshopPath { get; set; } = "";
    }

    /// <summary>自动备份配置(与主程序 AutoBackupConfig 字段名对齐)。</summary>
    public sealed class AutoBackupConfig
    {
        public bool Enabled { get; set; } = false;
        public bool ServiceEnabled { get; set; } = false;
        public bool TypeScene { get; set; } = true;
        public bool TypeVideo { get; set; } = true;
        public bool TypeWeb { get; set; } = true;
        public bool TypeApplication { get; set; } = true;
        public bool TypePreset { get; set; } = true;
        public bool TypeUnknown { get; set; } = true;
        public bool RatingG { get; set; } = true;
        public bool RatingPg { get; set; } = true;
        public bool RatingR { get; set; } = true;
    }

    /// <summary>读取主程序 config.json;不存在或解析失败返回 null。</summary>
    public static ServiceConfig? Load()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string path = System.IO.Path.Combine(localAppData, "WE_Tool", "config.json");
        if (!File.Exists(path))
        {
            Log.Write($"config.json 不存在: {path}");
            return null;
        }
        try
        {
            var config = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                ServiceJsonContext.Default.ServiceConfig);
            if (config == null) return null;
            Log.Write($"已读配置: AutoBackup.Enabled={config.AutoBackup?.Enabled ?? false}, " +
                      $"ServiceEnabled={config.AutoBackup?.ServiceEnabled ?? false}");
            return config;
        }
        catch (Exception ex)
        {
            Log.Write($"配置解析失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>配置是否启用自动备份(总开关 + 服务标记 + 路径齐全)。</summary>
    public bool IsAutoBackupActive()
        => (AutoBackup?.Enabled ?? false)
           && (AutoBackup?.ServiceEnabled ?? false)
           && !string.IsNullOrEmpty(Path?.VdfPath)
           && !string.IsNullOrEmpty(Path?.WorkshopPath);
}

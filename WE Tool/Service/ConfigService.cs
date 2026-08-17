using Serilog;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using WE_Tool.Json;
using System.Threading.Tasks;
using WE_Tool.Models;
using Windows.ApplicationModel;   // 新增：用于判断是否 Packaged
using Windows.Storage;

namespace WE_Tool.Service
{
    public interface IConfigService
    {
        Task<AppSettings> LoadAsync();
        Task SaveAsync(AppSettings settings);
    }

    public class ConfigService : IConfigService
    {
        private const string FileName = "config.json";

        /// <summary>当前配置结构版本号（配置发生破坏性变更时递增，并在 Migrate 中补充对应迁移步骤）</summary>
        private const int CurrentVersion = 2;

        // 静态路径，一次计算，终身使用
        private static readonly string ConfigPath = GetConfigFilePath();

        private static string GetConfigFilePath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appFolder = Path.Combine(localAppData, "WE_Tool");
            Directory.CreateDirectory(appFolder);
            return Path.Combine(appFolder, FileName);
        }
        public async Task<AppSettings> LoadAsync()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    Log.Information("未找到 config.json，已创建默认配置。路径：{Path}", ConfigPath);
                    var defaultSettings = new AppSettings();
                    await SaveAsync(defaultSettings);   // 复用 SaveAsync 创建文件
                    return defaultSettings;
                }

                string text = await File.ReadAllTextAsync(ConfigPath);
                var settings = JsonSerializer.Deserialize(text, JsonContext.Default.AppSettings) ?? new AppSettings();

                if (settings.Version < CurrentVersion)
                {
                    // 旧版本配置：执行迁移并写回，保证升级后设置不丢失
                    var migrated = Migrate(text, settings);
                    await SaveAsync(migrated);
                    return migrated;
                }

                return settings;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "读取 config.json 失败（路径：{Path}），使用默认设置", ConfigPath);
                return new AppSettings();
            }
        }

        public async Task SaveAsync(AppSettings settings)
        {
            try
            {
                // 确保目录存在（Unpackaged 模式下保险）
                string? dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                string text = JsonSerializer.Serialize(settings, JsonContext.Default.AppSettings);
                await File.WriteAllTextAsync(ConfigPath, text);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存 config.json 失败（路径：{Path}）", ConfigPath);
            }
        }

        /// <summary>
        /// 按原始 JSON 执行配置迁移（不经过模型反序列化，避免旧字段被丢弃）。
        /// 每升一版配置结构，在这里追加一步迁移。
        /// </summary>
        private static AppSettings Migrate(string rawJson, AppSettings settings)
        {
            var node = JsonNode.Parse(rawJson) as JsonObject;
            if (node == null) return settings;

            if (settings.Version < 2)
            {
                // v1 → v2：Papers 相关字段从顶层移入 Papers 对象（v0.2.0 结构重构）
                var papers = node["Papers"] as JsonObject ?? new JsonObject();
                string[] movedFields =
                [
                    "Expander", "IsBottomBarOpen", "AutoPlayGif", "IsWallpaperEnterAnimationEnabled",
                    "WallpaperTagDisplayIndex", "WallpaperViewIndex",
                    "WallpaperListMinWidth", "LeftSplitViewPaneOpen", "RightSplitViewPaneOpen",
                    "SortOrder", "IsSortAscending", "DetailSelectionEnabled", "FilterResultResponseDelay"
                ];
                foreach (var field in movedFields)
                {
                    if (node[field] != null && papers[field] == null)
                    {
                        papers[field] = node[field]!.DeepClone();
                        node.Remove(field);
                    }
                }
                node["Papers"] = papers;
                node["Version"] = 2;
            }

            // 未来的 v2 → v3 迁移在此追加……

            return JsonSerializer.Deserialize(
                node.ToJsonString(), JsonContext.Default.AppSettings) ?? settings;
        }
    }
}
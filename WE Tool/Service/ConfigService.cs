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

        // 进程内缓存:同一进程多次 LoadAsync 只真读一次盘,SaveAsync 同步更新。
        // 静态:App/MainWindow/ViewModel 各自 new ConfigService 实例,必须共享同一份缓存。
        private static AppSettings? _cached;
        private static readonly object CacheLock = new();

        private static string GetConfigFilePath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appFolder = Path.Combine(localAppData, "WE_Tool");
            Directory.CreateDirectory(appFolder);
            return Path.Combine(appFolder, FileName);
        }
        public async Task<AppSettings> LoadAsync()
        {
            // 进程内缓存命中:直接返回(启动路径上多处调用,只真读一次盘)
            lock (CacheLock)
            {
                if (_cached != null) return _cached;
            }

            try
            {
                if (!File.Exists(ConfigPath))
                {
                    Log.Information("未找到 config.json，已创建默认配置。路径：{Path}", ConfigPath);
                    var defaultSettings = new AppSettings();
                    await SaveAsync(defaultSettings).ConfigureAwait(false);   // 复用 SaveAsync 创建文件(内部会填充缓存)
                    return defaultSettings;
                }

                string text = await File.ReadAllTextAsync(ConfigPath).ConfigureAwait(false);
                var settings = JsonSerializer.Deserialize(text, JsonContext.Default.AppSettings) ?? new AppSettings();

                if (settings.Version < CurrentVersion)
                {
                    // 旧版本配置：执行迁移并写回，保证升级后设置不丢失
                    var migrated = Migrate(text, settings);
                    await SaveAsync(migrated).ConfigureAwait(false);
                    return migrated;
                }

                lock (CacheLock)
                {
                    _cached = settings;
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
                await File.WriteAllTextAsync(ConfigPath, text).ConfigureAwait(false);
                // 写盘成功后同步内存缓存,保证后续 LoadAsync 读到的就是刚落盘的值
                lock (CacheLock)
                {
                    _cached = settings;
                }
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
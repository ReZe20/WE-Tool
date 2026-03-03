using Serilog;
using System;
using System.IO;
using System.Text.Json;
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
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                return JsonSerializer.Deserialize<AppSettings>(text, opts) ?? new AppSettings();
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

                string text = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(ConfigPath, text);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存 config.json 失败（路径：{Path}）", ConfigPath);
            }
        }
    }
}
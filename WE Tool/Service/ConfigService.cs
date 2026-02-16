using Serilog;
using System;
using System.Text.Json;
using System.Threading.Tasks;
using WE_Tool.Models;
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

        public async Task<AppSettings> LoadAsync()
        {
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.GetFileAsync(FileName);
                var text = await FileIO.ReadTextAsync(file);
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                //这一行调完就删
                System.Diagnostics.Debug.WriteLine(ApplicationData.Current.LocalFolder.Path);

                return JsonSerializer.Deserialize<AppSettings>(text, opts) ?? new AppSettings();
            }
            catch
            {
                Log.Information("未找到config.json,以默认值重新创建并初始化。");
                return new AppSettings();
            }
        }

        public async Task SaveAsync(AppSettings settings)
        {
            await Task.Run(async () =>
            {
                try
                {
                    var folder = ApplicationData.Current.LocalFolder;
                    var file = await folder.CreateFileAsync(FileName, CreationCollisionOption.ReplaceExisting);
                    var text = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                    await FileIO.WriteTextAsync(file, text);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex,"保存config.json文件时发生错误。");
                }
            });
        }
    }
}

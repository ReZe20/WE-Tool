using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Tool_for_WallpaperEngine.Models;

namespace Tool_for_WallpaperEngine.Helper
{
    internal class WallpaperScanner
    {
        public static async Task<List<WallpaperItem>> ScanWallpapers(string rootPath)
        {
            var results = new List<WallpaperItem>();

            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
                return results;

            await Task.Run(async () =>
            {
                var stack = new Stack<string>();
                stack.Push(rootPath);

                while (stack.Count > 0)
                {
                    string current = stack.Pop();

                    try
                    {
                        foreach (var dir in Directory.GetDirectories(current))
                            stack.Push(dir);

                        string jsonPath = Path.Combine(current, "project.json");
                        if (!File.Exists(jsonPath)) continue;

                        string jsonText = await File.ReadAllTextAsync(jsonPath);
                        JObject json = JObject.Parse(jsonText);

                        // --- 修改点：移除了 "type" == "scene" 的判断 ---
                        // 只要有 project.json 且有 preview 图片就加载

                        string previewFile = json?["preview"]?.Value<string>();
                        if (string.IsNullOrEmpty(previewFile)) continue;

                        string previewFullPath = Path.Combine(current, previewFile);
                        if (!File.Exists(previewFullPath)) continue;

                        string title = json?["title"]?.Value<string>() ?? "无标题";
                        string rating = json?["contentrating"]?.Value<string>() ?? "Everyone";

                        var tagsList = new List<string>();
                        var tagsToken = json?["tags"];
                        if (tagsToken != null)
                        {
                            foreach (var t in tagsToken) tagsList.Add(t.ToString());
                        }

                        results.Add(new WallpaperItem
                        {
                            FolderPath = current,
                            Title = title, 
                            Preview = previewFullPath,
                            ContentRating = rating,
                            Tags = tagsList
                        });
                    }
                    catch (Exception ex)
                    {
                        // 忽略错误，继续扫描
                        System.Diagnostics.Debug.WriteLine($"扫描错误: {ex.Message}");
                    }
                }
            });

            return results;
        }
    }
}

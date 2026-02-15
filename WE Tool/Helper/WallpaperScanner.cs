using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WE_Tool.Models; 

namespace WE_Tool.Helper
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

                        string previewFile = json?["preview"]?.Value<string>();
                        if (string.IsNullOrEmpty(previewFile)) continue;

                        string previewFullPath = Path.Combine(current, previewFile);
                        if (!File.Exists(previewFullPath)) continue;

                        string title = json?["title"]?.Value<string>() ?? "无标题";
                        string rating = json?["contentrating"]?.Value<string>() ?? "Everyone";

                        string declaredType = json?["type"]?.Value<string>();
                        string finalType;
                        if (!string.IsNullOrEmpty(declaredType))
                        {
                            finalType = declaredType;
                        }
                        else
                        {
                            string directoriesPath = Path.Combine(current, "directories");
                            if (Directory.Exists(directoriesPath))
                            {
                                finalType = "web";
                            }
                            else
                            {
                                finalType = "unknown";
                            }
                        }

                        long filesize = 0;
                        try
                        {
                            filesize = new DirectoryInfo(current).EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
                        }
                        catch (Exception)
                        {
                            filesize = 0;
                        }
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
                            FileSize = filesize,
                            CreationTime = Directory.GetCreationTime(current),
                            UpdateTime = Directory.GetLastWriteTime(current),
                            Preview = previewFullPath,
                            ContentRating = rating,
                            Tags = tagsList,
                            Type = finalType
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"扫描错误: {ex.Message}");
                    }
                }
            });

            return results;
        }
    }
}

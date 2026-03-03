using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WE_Tool.Models;
using WE_Tool.ViewModels;

namespace WE_Tool.Helper
{
    internal class WallpaperScanner
    {
        private static HashSet<string> GetInstalledWorkshopIDs(string acfPath)
        {
            var ids = new HashSet<string>();
            if (!File.Exists(acfPath)) return ids;

            try
            {
                string content = File.ReadAllText(acfPath);
                var matches = System.Text.RegularExpressions.Regex.Matches(content, @"\""(\d+)\""\s+\{");
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    ids.Add(match.Groups[1].Value);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "解析.acf文件出现异常。");
            }
            return ids;
        }
        public static async Task<List<WallpaperItem>> ScanWallpapers(string rootPath, string source, string acfPath )
        {
            var results = new List<WallpaperItem>();

            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
                return results;

            var installedIDs = GetInstalledWorkshopIDs(acfPath);

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

                        string folderName = Path.GetFileName(current);
                        string matchedID = installedIDs.Contains(folderName) ? folderName : "";

                        string jsonPath = Path.Combine(current, "project.json");
                        if (!File.Exists(jsonPath)) continue;

                        string jsonText = await File.ReadAllTextAsync(jsonPath);
                        JObject json = JObject.Parse(jsonText);

                        string dependency = string.Empty;
                        string declaredType = json?["type"]?.Value<string>() ?? string.Empty;
                        string finalType;
                        if (!string.IsNullOrEmpty(declaredType))
                        {
                            finalType = declaredType;
                        }
                        else if (json?["preset"] != null)
                        {
                            finalType = "preset";
                            dependency = json?["dependency"]?.Value<string>() ?? string.Empty;
                        }
                        else if (source == "official" && json?["file"]?.Value<string>().Contains(".exe") == true)
                        {
                            finalType = "application";
                        }
                        else if (source == "official" && json?["file"]?.Value<string>().Contains(".json") == true)
                        {
                            finalType = "scene";
                        }
                        else if (json?["category"]?.Value<string>() == "Asset")
                        {
                            continue;
                        }
                        else
                        {
                            finalType = "unknown";
                        }

                        string previewFullPath;
                        string previewFile = json?["preview"]?.Value<string>();
                        if (!string.IsNullOrEmpty(previewFile))
                        {
                            previewFullPath = Path.Combine(current, previewFile);
                            previewFullPath = File.Exists(previewFullPath) ? previewFullPath : "ms-appx:///Assets/noPreview.png";
                        }
                        else
                        {
                            previewFullPath = "ms-appx:///Assets/NoPreview.png";
                        }

                        string title = json?["title"]?.Value<string>() ?? "无标题";
                        string rating = json?["contentrating"]?.Value<string>() ?? "Everyone";

                        string rawDescription = json?["description"]?.Value<string>();
                        string description = string.IsNullOrWhiteSpace(rawDescription) ? LanguageHelper.GetResource("Nodescription") : rawDescription;

                        long filesize = 0;
                        try
                        {
                            filesize = new DirectoryInfo(current).EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex,$"获取壁纸大小时异常。{title}");
                            filesize = 0;
                        }
                        var tagsList = new List<string>();
                        var tagsToken = json?["tags"];
                        if (tagsToken != null)
                        {
                            foreach (var t in tagsToken)
                            {
                                tagsList.Add(t.ToString());
                            }
                        }
                        else
                        {
                            tagsList.Add("Unspecified");
                        }

                        results.Add(new WallpaperItem
                        {
                            WorkshopID = matchedID,
                            FolderPath = current,
                            Title = title,
                            Description = description,
                            FileSize = filesize,
                            CreationTime = Directory.GetCreationTime(current),
                            UpdateTime = Directory.GetLastWriteTime(current),
                            Preview = previewFullPath,
                            ContentRating = rating,
                            Tags = tagsList,
                            Type = finalType,
                            Source = source,
                            Dependency = dependency,
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "扫描壁纸时出现错误。");
                    }
                }
            });

            return results;
        }
    }
}

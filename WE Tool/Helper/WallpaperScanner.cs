using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using WE_Tool.Models;

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
            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
                return new List<WallpaperItem>();

            var installedIDs = GetInstalledWorkshopIDs(acfPath);
            var resultsBag = new ConcurrentBag<WallpaperItem>();

            const int batchSize = 200;
            var sw = Stopwatch.StartNew();

            await Task.Run(async () =>
            {
                var stack = new Stack<string>();
                stack.Push(rootPath);

                var batch = new List<string>(batchSize);
                var tasks = new List<Task>();

                async Task ProcessBatchAsync(string[] dirs)
                {
                    foreach (var current in dirs)
                    {
                        try
                        {
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
                            else if (json?["category"]?.Value<string>() == "Asset")
                            {
                                continue;
                            }
                            else if (json?["preset"] != null)
                            {
                                finalType = "preset";
                                dependency = json?["dependency"]?.Value<string>() ?? string.Empty;
                            }
                            else if (source == "official" && json?["file"]?.Value<string>()?.Contains(".exe") == true)
                            {
                                finalType = "application";
                            }
                            else if (source == "official" && json?["file"]?.Value<string>()?.Contains(".json") == true)
                            {
                                finalType = "scene";
                            }
                            else
                            {
                                finalType = "unknown";
                            }

                            string previewFullPath;
                            string previewFile = json?["preview"]?.Value<string>() ?? "";
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

                            string Description = json?["description"]?.Value<string>() ?? "";

                            long filesize = 0;
                            try
                            {
                                filesize = new DirectoryInfo(current).EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, $"获取壁纸大小时异常。{title}");
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

                            resultsBag.Add(new WallpaperItem
                            {
                                WorkshopID = matchedID,
                                FolderPath = current,
                                Title = title,
                                Description = Description,
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
                }
                while (stack.Count > 0)
                {
                    string current = stack.Pop();

                    try
                    {
                        foreach (var dir in Directory.GetDirectories(current))
                            stack.Push(dir);

                        batch.Add(current);

                        if (batch.Count >= batchSize)
                        {
                            var batchCopy = batch.ToArray();
                            batch.Clear();

                            // 为每个批次启动一个 LongRunning 任务
                            var t = Task.Factory
                                .StartNew(() => ProcessBatchAsync(batchCopy), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                                .Unwrap();
                            tasks.Add(t);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "扫描壁纸时出现错误。");
                    }
                }
                if (batch.Count > 0)
                {
                    var batchCopy = batch.ToArray();
                    var t = Task.Factory
                    .StartNew(() => ProcessBatchAsync(batchCopy), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                    .Unwrap();
                    tasks.Add(t);
                }

                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"等待 {source} 扫描任务完成时出现错误。");
                }
            }).ConfigureAwait(false);

            sw.Stop();
            try
            {
                Log.Information($"扫描源 {source} 完成，耗时 {sw.Elapsed.TotalMilliseconds} ms, 结果数量 {resultsBag.Count}");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "记录扫描时间时出现错误。");
            }

            return resultsBag.ToList();
        }
    }
}

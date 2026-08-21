using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WE_Tool.Models;
using WE_Tool.Json;

namespace WE_Tool.Helper;

internal class WallpaperScanner
{
    /// <summary>最近一次扫描收集到的组件列表（仅 workshop 源）。</summary>
    public static List<ComponentInfo>? LastComponents { get; private set; }
    private static readonly Regex WorkshopIdRegex = new(
        @"\""(\d+)\""\s+\{", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex AcfTimeUpdatedRegex = new(
        @"""(\d+)""\s*\{[^}]*""timeupdated""\s*""(\d+)""[^}]*\}",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex AcfSizeRegex = new(
        @"""(\d+)""\s*\{[^}]*""size""\s*""(\d+)""[^}]*\}",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// 匹配 VDF 订阅文件中每个条目的 publishedfileid 和 disabled_locally 值。
    /// 匹配格式: "0" { ... "publishedfileid" "12345" ... "disabled_locally" "0" ... }
    /// 捕获组: [1]=publishedfileid, [2]=disabled_locally
    /// </summary>
    private static readonly Regex VdfEntryRegex = new(
        @"""publishedfileid""\s+""(\d+)""[^}]*""disabled_locally""\s+""(\d+)""",
        RegexOptions.Compiled);

    // ====================== 扫描缓存(JSON 单文件,原子写) ======================
    // 旧实现用 Microsoft.Data.Sqlite 存 SQLite 数据库;实际用法只有"整表读进内存 + 整批重写",
    // 没用到 SQLite 的查询/索引/并发能力,却背上原生库依赖(e_sqlite3.dll)与 AOT 裁剪风险。
    // 自实现:单个 JSON 文件(源生成器序列化,零反射、AOT 安全),写时先落临时文件再原子替换,
    // 读失败/损坏一律回退全量扫描。
    // 注:CacheFile/CachedEntry 须为 internal(源生成器要求类型可达,private 嵌套类无法注册)。
    internal sealed class CacheFile
    {
        public DateTime SavedAtUtc { get; set; }
        public List<CachedEntry> Entries { get; set; } = [];
    }

    internal sealed class CachedEntry
    {
        public WallpaperItem Item { get; set; } = null!;
        public DateTime UpdateTime { get; set; }
        public DateTime CachedAt { get; set; }
    }

    private static readonly object CacheWriteLock = new();

    private static string GetDefaultCachePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WE_Tool", "wallpaper_cache.json");

    /// <summary>读取缓存文件(损坏/缺失 → 空字典,由调用方回退全量扫描)。</summary>
    private static Dictionary<string, CachedEntry> LoadCacheDictionary(string cachePath)
    {
        if (!File.Exists(cachePath)) return [];

        try
        {
            var json = File.ReadAllText(cachePath);
            var cacheFile = JsonSerializer.Deserialize(json, JsonContext.Default.CacheFile);
            if (cacheFile?.Entries == null) return [];

            var dict = new Dictionary<string, CachedEntry>(StringComparer.Ordinal);
            foreach (var entry in cacheFile.Entries)
            {
                if (entry.Item.FolderPath != null)
                    dict[entry.Item.FolderPath] = entry;
            }
            return dict;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载扫描缓存失败(文件可能损坏)，将进行全量扫描。");
            return [];
        }
    }

    /// <summary>原子写缓存:先写临时文件再替换,避免写一半崩溃留下半截文件。</summary>
    private static void SaveItemsToCache(string cachePath, IEnumerable<WallpaperItem> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;

        // 三个扫描源(workshop/official/mine)并行,共用同一缓存文件,写必须串行化
        lock (CacheWriteLock)
        {
            try
            {
                var cacheFile = new CacheFile
                {
                    SavedAtUtc = DateTime.UtcNow,
                    Entries = list
                        .Where(i => i.FolderPath != null)
                        .Select(i => new CachedEntry
                        {
                            Item = i,
                            UpdateTime = i.UpdateTime,
                            CachedAt = DateTime.UtcNow
                        })
                        .ToList()
                };

                var json = JsonSerializer.Serialize(cacheFile, JsonContext.Default.CacheFile);
                var dir = Path.GetDirectoryName(cachePath);
                if (dir != null) Directory.CreateDirectory(dir);

                var tempPath = cachePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, cachePath, overwrite: true);
                Log.Information($"已缓存 {cacheFile.Entries.Count} 个壁纸到 JSON 缓存");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "保存扫描缓存失败（不影响扫描结果）");
                try { if (File.Exists(cachePath + ".tmp")) File.Delete(cachePath + ".tmp"); }
                catch { /* 忽略清理失败 */ }
            }
        }
    }

    public static async Task<List<WallpaperItem>> ScanWallpapers(
        string rootPath,
        string source,
        string acfPath,
        IProgress<int>? progress = null,
        string? cacheDbPath = null,
        string? vdfPath = null,
        bool useCache = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
            return [];

        var installedIDs = GetInstalledWorkshopIDs(acfPath);
        var acfUpdateTimes = GetAcfUpdateTimes(acfPath);
        var acfSizes = GetAcfSizes(acfPath);
        // 只对 workshop 源解析 VDF；VDF 不存在时返回 null（不进行校验）
        var activeSubscribedIDs = source == "workshop" ? GetActiveSubscribedIDs(vdfPath ?? "") : null;
        var resultsBag = new ConcurrentBag<WallpaperItem>();
        var parsedItems = new ConcurrentBag<WallpaperItem>();
        var sw = Stopwatch.StartNew();

        Log.Information($"开始扫描源 {source}... 根目录: {rootPath}");

        try
        {
            var effectiveCachePath = useCache
                ? (string.IsNullOrEmpty(cacheDbPath) ? GetDefaultCachePath() : cacheDbPath)
                : null;

            var enumOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            };

            var wallpaperDirs = Directory.EnumerateDirectories(rootPath, "*", enumOptions)
                .Where(dir => File.Exists(Path.Combine(dir, "project.json"))
                           && !dir.Contains(Path.DirectorySeparatorChar + ".we_backup")) // 排除备份目录
                .ToList();

            List<string> toParse;

            if (useCache)
            {
                var cacheDict = LoadCacheDictionary(effectiveCachePath!);

                // 缓存命中判断（基于文件修改时间）
                toParse = [];
                foreach (var current in wallpaperDirs)
                {
                    var currentUpdateTime = Directory.GetLastWriteTime(current);

                    if (cacheDict.TryGetValue(current, out var entry) &&
                        entry.UpdateTime == currentUpdateTime)
                    {
                        resultsBag.Add(entry.Item);
                    }
                    else
                    {
                        toParse.Add(current);
                    }
                }

                Log.Information($"SQLite 缓存命中 {resultsBag.Count} 个壁纸，需解析 {toParse.Count} 个新/更新壁纸");
            }
            else
            {
                // 缓存关闭：全部重新解析
                toParse = wallpaperDirs;
                Log.Information($"缓存已关闭，将解析全部 {toParse.Count} 个壁纸");
            }

            // 并行解析新增/修改的壁纸
            if (toParse.Count > 0)
            {
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = ct
                };

                await Parallel.ForEachAsync(toParse, parallelOptions, async (current, token) =>
                {
                    var item = await ParseWallpaperAsync(current, installedIDs, source, acfUpdateTimes, acfSizes, activeSubscribedIDs, token);
                    if (item is not null)
                    {
                        resultsBag.Add(item);
                        parsedItems.Add(item);
                    }
                });
            }

            // === 保存新增/修改的壁纸到缓存 ===
            if (useCache && parsedItems.Count > 0)
                SaveItemsToCache(effectiveCachePath!, parsedItems);

            // === 对 workshop 源：统一校准所有壁纸（含缓存命中）的 ShouldNotExist ===
            // 订阅异常 = 不在有效订阅名单（取消订阅/本地停用）或已被下架（project.json visibility == "private"）
            if (source == "workshop" && activeSubscribedIDs != null)
            {
                foreach (var item in resultsBag)
                {
                    item.ShouldNotExist = string.IsNullOrEmpty(item.WorkshopID)
                        || !activeSubscribedIDs.Contains(item.WorkshopID)
                        || item.IsDelisted;
                }
            }

            // === 扫描组件（仅 workshop 源） ===
            // 组件和壁纸都在 workshop 根目录下同级，各有独立的 project.json
            if (source == "workshop" && resultsBag.Count > 0)
            {
                var componentBag = new ConcurrentBag<ComponentInfo>();
                await Parallel.ForEachAsync(wallpaperDirs, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = ct
                }, async (dir, token) =>
                {
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        var comp = await ParseComponentAsync(dir, "", acfUpdateTimes, token);
                        if (comp != null) componentBag.Add(comp);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "组件解析失败,已跳过: {Dir}", dir);
                    }
                });

                LastComponents = [.. componentBag];
                Log.Information("组件扫描完成，共 {Count} 个", componentBag.Count);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information($"扫描源 {source} 已被取消。耗时 {sw.Elapsed.TotalMilliseconds:F0} ms");
            return [];
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"扫描源 {source} 出现严重错误。");
        }
        finally
        {
            sw.Stop();
            Log.Information($"扫描源 {source} 完成，耗时 {sw.Elapsed.TotalMilliseconds:F0} ms，结果数量 {resultsBag.Count}");
        }

        return [.. resultsBag];
    }

    private static FrozenSet<string> GetInstalledWorkshopIDs(string acfPath)
    {
        if (!File.Exists(acfPath)) return FrozenSet<string>.Empty;

        try
        {
            var content = File.ReadAllText(acfPath);
            var matches = WorkshopIdRegex.Matches(content);
            return matches.Select(m => m.Groups[1].Value).ToFrozenSet();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "解析 .acf 文件出现异常。");
            return FrozenSet<string>.Empty;
        }
    }

    /// <summary>
    /// 从 .acf 文件中解析每个工坊壁纸的更新时间 (timeupdated, Unix 秒 → DateTime)
    /// </summary>
    private static Dictionary<string, DateTime> GetAcfUpdateTimes(string acfPath)
    {
        var result = new Dictionary<string, DateTime>();
        if (!File.Exists(acfPath)) return result;

        try
        {
            var content = File.ReadAllText(acfPath);

            // 只扫描 WorkshopItemsInstalled 段内的条目
            var sectionMatch = Regex.Match(content,
                @"""WorkshopItemsInstalled""\s*\{(?<body>.+?)\}\s*""WorkshopItemDetails""",
                RegexOptions.Singleline);
            if (!sectionMatch.Success) return result;

            var body = sectionMatch.Groups["body"].Value;
            var matches = AcfTimeUpdatedRegex.Matches(body);

            foreach (Match match in matches)
            {
                var id = match.Groups[1].Value;
                if (long.TryParse(match.Groups[2].Value, out var unixTs))
                {
                    result[id] = DateTimeOffset.FromUnixTimeSeconds(unixTs).DateTime;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "解析 .acf 更新时间出现异常。");
        }

        return result;
    }

    /// <summary>
    /// 从 .acf 文件中解析每个工坊壁纸的 Steam 报告大小 (size, 字节)
    /// </summary>
    private static Dictionary<string, long> GetAcfSizes(string acfPath)
    {
        var result = new Dictionary<string, long>();
        if (!File.Exists(acfPath)) return result;

        try
        {
            var content = File.ReadAllText(acfPath);

            var sectionMatch = Regex.Match(content,
                @"""WorkshopItemsInstalled""\s*\{(?<body>.+?)\}\s*""WorkshopItemDetails""",
                RegexOptions.Singleline);
            if (!sectionMatch.Success) return result;

            var body = sectionMatch.Groups["body"].Value;
            var matches = AcfSizeRegex.Matches(body);

            foreach (Match match in matches)
            {
                var id = match.Groups[1].Value;
                if (long.TryParse(match.Groups[2].Value, out var size))
                {
                    result[id] = size;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "解析 .acf 文件大小时出现异常。");
        }

        return result;
    }

    /// <summary>
    /// 从 .vdf 文件中解析有效订阅的工坊壁纸 ID 集合。
    /// 返回在 VDF 中存在且 disabled_locally != "1" 的 publishedfileid。
    /// 当 VDF 文件不存在或解析失败时返回 null。
    /// </summary>
    private static FrozenSet<string>? GetActiveSubscribedIDs(string vdfPath)
    {
        if (string.IsNullOrEmpty(vdfPath) || !File.Exists(vdfPath))
            return null;

        try
        {
            var content = File.ReadAllText(vdfPath);
            var matches = VdfEntryRegex.Matches(content);
            var result = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match match in matches)
            {
                var id = match.Groups[1].Value;
                var disabled = match.Groups[2].Value;
                if (disabled != "1")
                    result.Add(id);
            }

            Log.Information($"VDF 解析完成: 有效订阅 {result.Count} 个工坊壁纸");
            return result.ToFrozenSet();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "解析 .vdf 文件出现异常，将不对订阅状态进行校验。");
            return null;
        }
    }

    /// <summary>
    /// 源生成器不支持 AllowTrailingCommas/ReadCommentHandling,project.json 若含
    /// 尾逗号或注释会导致解析失败——按字符状态机轻量清洗(跳过字符串内的逗号/注释形态)。
    /// </summary>
    private static string SanitizeProjectJson(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        bool inString = false, inLineComment = false, inBlockComment = false;
        char prev = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (inLineComment)
            {
                if (c == '\n') { inLineComment = false; sb.Append(c); }
                continue;
            }
            if (inBlockComment)
            {
                if (c == '*' && next == '/') { inBlockComment = false; i++; }
                continue;
            }
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && next != '\0') { sb.Append(next); i++; }
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; sb.Append(c); continue; }
            if (c == '/' && next == '/') { inLineComment = true; i++; continue; }
            if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
            // 尾逗号:逗号后(跳过空白)紧跟 } 或 ],删除该逗号
            if (c == ',')
            {
                int j = i + 1;
                while (j < text.Length && (text[j] == ' ' || text[j] == '\t' || text[j] == '\r' || text[j] == '\n')) j++;
                if (j < text.Length && (text[j] == '}' || text[j] == ']')) { prev = c; continue; }
            }
            sb.Append(c);
            prev = c;
        }
        return sb.ToString();
    }

    private static async Task<WallpaperItem?> ParseWallpaperAsync(
        string current,
        FrozenSet<string> installedIDs,
        string source,
        Dictionary<string, DateTime> acfUpdateTimes,
        Dictionary<string, long> acfSizes,
        FrozenSet<string>? activeSubscribedIDs,
        CancellationToken ct)
    {
        try
        {
            var folderName = Path.GetFileName(current);
            var matchedID = installedIDs.Contains(folderName) ? folderName : "";

            var jsonPath = Path.Combine(current, "project.json");
            var jsonText = SanitizeProjectJson(await File.ReadAllTextAsync(jsonPath, ct));
            var metadata = JsonSerializer.Deserialize(jsonText, JsonContext.Default.ProjectMetadata)
                           ?? throw new InvalidOperationException("JSON 反序列化失败");

            // 类型推断
            string finalType = metadata.Type ?? string.Empty;
            string dependency = string.Empty;

            if (string.IsNullOrEmpty(finalType))
            {
                if (metadata.Category == "Asset") return null;

                if (metadata.Preset.HasValue)
                {
                    finalType = "preset";
                    dependency = metadata.Dependency ?? string.Empty;
                }
                else if (source == "official" && metadata.File?.Contains(".exe") == true)
                    finalType = "application";
                else if (source == "official" && metadata.File?.Contains(".json") == true)
                    finalType = "scene";
                else
                    finalType = "unknown";
            }

            // Preview
            var previewFile = metadata.Preview ?? "";
            var previewFullPath = string.IsNullOrEmpty(previewFile)
                ? "ms-appx:///Assets/NoPreview.png"
                : Path.Combine(current, previewFile);

            if (!File.Exists(previewFullPath))
                previewFullPath = "ms-appx:///Assets/NoPreview.png";

            // Tags
            string tagsString = "Unspecified";
            if (metadata.Tags.HasValue)
            {
                var tok = metadata.Tags.Value;
                if (tok.ValueKind == JsonValueKind.String)
                {
                    tagsString = tok.GetString() ?? "Unspecified";
                }
                else if (tok.ValueKind == JsonValueKind.Array)
                {
                    var first = tok.EnumerateArray().Select(e => e.GetString()).FirstOrDefault(s => !string.IsNullOrEmpty(s));
                    tagsString = first ?? "Unspecified";
                }
                else
                {
                    try
                    {
                        tagsString = tok.GetRawText();
                        if (string.IsNullOrEmpty(tagsString)) tagsString = "Unspecified";
                    }
                    catch { tagsString = "Unspecified"; }
                }
            }

            // 文件夹大小
            long filesize = 0;
            try
            {
                filesize = new DirectoryInfo(current)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(fi => fi.Length);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"获取壁纸大小时异常。{metadata.Title}");
            }

            bool isDelisted = string.Equals(metadata.Visibility, "private", StringComparison.OrdinalIgnoreCase);

            return new WallpaperItem
            {
                WorkshopID = matchedID,
                FolderPath = current,
                Title = metadata.Title ?? "无标题",
                Description = metadata.Description ?? "",
                FileSize = filesize,
                CreationTime = Directory.GetCreationTime(current),
                UpdateTime = Directory.GetLastWriteTime(current),
                AcfUpdateTime = acfUpdateTimes.TryGetValue(folderName, out var acfTime)
                    ? acfTime
                    : null,
                AcfSize = acfSizes.TryGetValue(folderName, out var acfSize)
                    ? acfSize
                    : null,
                Preview = previewFullPath,
                ContentRating = metadata.Contentrating ?? "Everyone",
                Tags = tagsString,
                Type = finalType,
                Source = source,
                Dependency = dependency,
                IsDelisted = isDelisted,
                ShouldNotExist = source == "workshop" && activeSubscribedIDs != null
                    ? (string.IsNullOrEmpty(matchedID) || !activeSubscribedIDs.Contains(matchedID) || isDelisted)
                    : false
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, $"解析壁纸文件夹失败: {current}");
            return null;
        }
    }

    /// <summary>
    /// 扫描创意工坊壁纸目录下的所有组件（category == "Asset"）。
    /// </summary>
    public static async Task<List<ComponentInfo>> ScanComponentsAsync(
        string workshopPath,
        string acfPath = "",
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(workshopPath) || !Directory.Exists(workshopPath))
            return [];

        var results = new List<ComponentInfo>();
        var acfUpdateTimes = GetAcfUpdateTimes(acfPath);

        try
        {
            var wallpaperDirs = Directory.EnumerateDirectories(workshopPath, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true
            }).Where(dir => !Path.GetFileName(dir).Equals(".we_backup", StringComparison.OrdinalIgnoreCase));

            foreach (var wallpaperDir in wallpaperDirs)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var component = await ParseComponentAsync(wallpaperDir, "", acfUpdateTimes, ct);
                    if (component != null)
                        results.Add(component);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warning(ex, "扫描组件失败: {Path}", wallpaperDir);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("组件扫描被取消。已扫描 {Count} 个组件", results.Count);
            return results;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "扫描组件时出现严重错误");
        }

        Log.Information("组件扫描完成，共 {Count} 个组件", results.Count);
        return results;
    }

    private static async Task<ComponentInfo?> ParseComponentAsync(
        string componentDir,
        string parentWallpaperDir,
        Dictionary<string, DateTime> acfUpdateTimes,
        CancellationToken ct)
    {
        var jsonPath = Path.Combine(componentDir, "project.json");
        if (!File.Exists(jsonPath)) return null;

        var jsonText = SanitizeProjectJson(await File.ReadAllTextAsync(jsonPath, ct));
        var metadata = JsonSerializer.Deserialize(jsonText, JsonContext.Default.ProjectMetadata);

        if (metadata == null || metadata.Category != "Asset")
            return null;

        var componentType = DetermineComponentType(metadata.File ?? "");

        var previewFile = metadata.Preview ?? "";
        var previewFullPath = string.IsNullOrEmpty(previewFile)
            ? "ms-appx:///Assets/NoPreview.png"
            : Path.Combine(componentDir, previewFile);

        if (!File.Exists(previewFullPath))
            previewFullPath = "ms-appx:///Assets/NoPreview.png";

        string tagsString = "Unspecified";
        if (metadata.Tags.HasValue)
        {
            var tok = metadata.Tags.Value;
            if (tok.ValueKind == JsonValueKind.String)
                tagsString = tok.GetString() ?? "Unspecified";
            else if (tok.ValueKind == JsonValueKind.Array)
            {
                var tags = tok.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
                tagsString = tags.Count > 0 ? string.Join(", ", tags) : "Unspecified";
            }
        }

        long fileSize = 0;
        try
        {
            fileSize = new DirectoryInfo(componentDir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(fi => fi.Length);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "统计组件目录大小失败,按 0 处理: {Dir}", componentDir);
        }

        return new ComponentInfo
        {
            Title = metadata.Title ?? "无标题",
            FilePath = metadata.File ?? "",
            ComponentType = componentType,
            FolderPath = componentDir,
            ParentWallpaperPath = parentWallpaperDir,
            WorkshopID = metadata.Workshopid ?? "",
            Preview = previewFullPath,
            FileSize = fileSize,
            InstallDate = Directory.GetLastWriteTime(componentDir),
            CreationTime = Directory.GetCreationTime(componentDir),
            AcfUpdateTime = acfUpdateTimes.TryGetValue(Path.GetFileName(componentDir), out var acfTime)
                ? acfTime
                : null,
            ContentRating = metadata.Contentrating ?? "Everyone",
            Tags = tagsString,
            Description = metadata.Description ?? ""
        };
    }

    /// <summary>
    /// 根据 project.json 的 "file" 属性判断组件类型。
    /// 规则：script.json → Script, effect.json → Effect, assets.json → Layer
    /// </summary>
    public static ComponentType DetermineComponentType(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return ComponentType.Unknown;

        var fileName = Path.GetFileName(filePath.Replace('\\', '/').ToLowerInvariant());

        return fileName switch
        {
            "script.json" => ComponentType.Script,
            "effect.json" => ComponentType.Effect,
            "assets.json" => ComponentType.Layer,
            _ => ComponentType.Unknown
        };
    }
}
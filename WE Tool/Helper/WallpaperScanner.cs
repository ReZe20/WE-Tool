using Microsoft.Data.Sqlite;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Data;
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

namespace WE_Tool.Helper;

internal class WallpaperScanner
{
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    // ====================== SQLite 缓存相关 ======================
    private record CachedEntry(WallpaperItem Item, DateTime UpdateTime, DateTime CachedAt);

    private static string GetDefaultCachePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WE_Tool", "wallpaper_cache.db");

    private static void EnsureDatabaseInitialized(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (dir != null) Directory.CreateDirectory(dir);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS WallpaperCache (
                FolderPath     TEXT PRIMARY KEY,
                Source         TEXT NOT NULL,
                WorkshopID     TEXT,
                Title          TEXT,
                Description    TEXT,
                FileSize       INTEGER,
                CreationTime   TEXT,
                UpdateTime     TEXT,
                AcfUpdateTime  TEXT,
                Preview        TEXT,
                ContentRating  TEXT,
                Tags           TEXT,
                Type           TEXT,
                Dependency     TEXT,
                AcfSize        INTEGER,
                CachedAt       TEXT
            );
            """;
        cmd.ExecuteNonQuery();

        // 迁移：如果旧表缺少 AcfSize 列则添加
        try
        {
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE WallpaperCache ADD COLUMN AcfSize INTEGER;";
            alterCmd.ExecuteNonQuery();
        }
        catch { /* 列已存在，忽略 */ }
    }

    private static Dictionary<string, CachedEntry> LoadCacheDictionary(string dbPath)
    {
        var dict = new Dictionary<string, CachedEntry>(StringComparer.Ordinal);
        if (!File.Exists(dbPath)) return dict;

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM WallpaperCache";
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                var entry = LoadCachedEntry(reader);
                dict[entry.Item.FolderPath] = entry;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载 SQLite 缓存失败，将进行全量扫描。");
        }
        return dict;
    }

    private static CachedEntry LoadCachedEntry(SqliteDataReader reader)
    {
        var tagsString = reader.IsDBNull(reader.GetOrdinal("Tags")) ? "Unspecified" : reader.GetString("Tags");

        var item = new WallpaperItem
        {
            WorkshopID = reader.IsDBNull("WorkshopID") ? "" : reader.GetString("WorkshopID"),
            FolderPath = reader.GetString("FolderPath"),
            Title = reader.IsDBNull("Title") ? "无标题" : reader.GetString("Title"),
            Description = reader.IsDBNull("Description") ? "" : reader.GetString("Description"),
            FileSize = reader.IsDBNull("FileSize") ? 0L : reader.GetInt64("FileSize"),
            CreationTime = DateTime.ParseExact(reader.GetString("CreationTime"), "o", CultureInfo.InvariantCulture),
            UpdateTime = DateTime.ParseExact(reader.GetString("UpdateTime"), "o", CultureInfo.InvariantCulture),
            AcfUpdateTime = reader.IsDBNull("AcfUpdateTime")
                ? null
                : DateTime.ParseExact(reader.GetString("AcfUpdateTime"), "o", CultureInfo.InvariantCulture),
            Preview = reader.GetString("Preview"),
            ContentRating = reader.IsDBNull("ContentRating") ? "Everyone" : reader.GetString("ContentRating"),
            Tags = tagsString,
            Type = reader.GetString("Type"),
            Source = reader.GetString("Source"),
            Dependency = reader.IsDBNull("Dependency") ? "" : reader.GetString("Dependency"),
            AcfSize = reader.IsDBNull("AcfSize") ? null : reader.GetInt64("AcfSize")
        };

        var cachedAt = DateTime.ParseExact(reader.GetString("CachedAt"), "o", CultureInfo.InvariantCulture);
        return new CachedEntry(item, item.UpdateTime, cachedAt);
    }

    private static void SaveItemsToCache(string dbPath, IEnumerable<WallpaperItem> items)
    {
        if (!items.Any()) return;

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO WallpaperCache 
                (FolderPath, Source, WorkshopID, Title, Description, FileSize,
                 CreationTime, UpdateTime, AcfUpdateTime, Preview, ContentRating, Tags,
                 Type, Dependency, AcfSize, CachedAt)
                VALUES (@FolderPath, @Source, @WorkshopID, @Title, @Description, @FileSize,
                        @CreationTime, @UpdateTime, @AcfUpdateTime, @Preview, @ContentRating, @Tags,
                        @Type, @Dependency, @AcfSize, @CachedAt)
                """;

            foreach (var item in items)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@FolderPath", item.FolderPath);
                cmd.Parameters.AddWithValue("@Source", item.Source);
                cmd.Parameters.AddWithValue("@WorkshopID", item.WorkshopID ?? "");
                cmd.Parameters.AddWithValue("@Title", item.Title);
                cmd.Parameters.AddWithValue("@Description", item.Description ?? "");
                cmd.Parameters.AddWithValue("@FileSize", item.FileSize);
                cmd.Parameters.AddWithValue("@CreationTime", item.CreationTime.ToString("o"));
                cmd.Parameters.AddWithValue("@UpdateTime", item.UpdateTime.ToString("o"));
                cmd.Parameters.AddWithValue("@AcfUpdateTime",
                    item.AcfUpdateTime.HasValue
                        ? (object)item.AcfUpdateTime.Value.ToString("o")
                        : DBNull.Value);
                cmd.Parameters.AddWithValue("@Preview", item.Preview);
                cmd.Parameters.AddWithValue("@ContentRating", item.ContentRating);
                cmd.Parameters.AddWithValue("@Tags", item.Tags ?? "Unspecified");
                cmd.Parameters.AddWithValue("@Type", item.Type);
                cmd.Parameters.AddWithValue("@Dependency", item.Dependency ?? "");
                cmd.Parameters.AddWithValue("@CachedAt", DateTime.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("@AcfSize",
                    item.AcfSize.HasValue
                        ? (object)item.AcfSize.Value
                        : DBNull.Value);

                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            Log.Information($"已缓存 {items.Count()} 个壁纸到 SQLite");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "保存 SQLite 缓存失败（不影响扫描结果）");
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
                .Where(dir => File.Exists(Path.Combine(dir, "project.json")))
                .ToList();

            List<string> toParse;

            if (useCache)
            {
                EnsureDatabaseInitialized(effectiveCachePath!);
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
            if (source == "workshop" && activeSubscribedIDs != null)
            {
                foreach (var item in resultsBag)
                {
                    item.ShouldNotExist = string.IsNullOrEmpty(item.WorkshopID)
                        || !activeSubscribedIDs.Contains(item.WorkshopID);
                }
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

    private record ProjectMetadata
    {
        public string? Type { get; init; }
        public string? Category { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? Preset { get; init; }
        public string? Dependency { get; init; }
        public string? File { get; init; }
        public string? Preview { get; init; }
        public string? Title { get; init; }
        public string? Contentrating { get; init; }
        public string? Description { get; init; }
        public JsonElement? Tags { get; init; }
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
            await using var fileStream = File.OpenRead(jsonPath);
            var metadata = await JsonSerializer.DeserializeAsync<ProjectMetadata>(fileStream, JsonOptions, ct)
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
                ShouldNotExist = source == "workshop" && activeSubscribedIDs != null
                    ? (string.IsNullOrEmpty(matchedID) || !activeSubscribedIDs.Contains(matchedID))
                    : false
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, $"解析壁纸文件夹失败: {current}");
            return null;
        }
    }
}
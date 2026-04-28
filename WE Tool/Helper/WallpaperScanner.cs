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
    private record AcfInfo(long Size, long SubTime, long UpdateTime);
    private static readonly Regex AcfIdRegex = new(@"^\""(\d+)\""$", RegexOptions.Compiled);
    private static readonly Regex AcfKeyValueRegex = new(@"\""(?<key>[^\""]+)\""\s+\""(?<value>[^\""]+)\""", RegexOptions.Compiled);

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
                FileSizeInACF  INTEGER,
                CreationTime   TEXT,
                SubTime        TEXT,
                ModifiedTime   TEXT,
                UpdateTime     TEXT,
                Preview        TEXT,
                ContentRating  TEXT,
                Tags           TEXT,
                Type           TEXT,
                Dependency     TEXT,
                CachedAt       TEXT
            );
            """;
        cmd.ExecuteNonQuery();
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
            FileSizeInACF = reader.IsDBNull("FileSizeInACF") ? 0L : reader.GetInt64("FileSizeInACF"),
            CreationTime = DateTime.ParseExact(reader.GetString("CreationTime"), "o", CultureInfo.InvariantCulture),
            UpdateTime = DateTime.ParseExact(reader.GetString("UpdateTime"), "o", CultureInfo.InvariantCulture),
            ModifiedTime = DateTime.ParseExact(reader.GetString("ModifiedTime"), "o", CultureInfo.InvariantCulture),
            SubTime = DateTime.ParseExact(reader.GetString("UpdateTime"), "o", CultureInfo.InvariantCulture),
            Preview = reader.GetString("Preview"),
            ContentRating = reader.IsDBNull("ContentRating") ? "Everyone" : reader.GetString("ContentRating"),
            Tags = tagsString,
            Type = reader.GetString("Type"),
            Source = reader.GetString("Source"),
            Dependency = reader.IsDBNull("Dependency") ? "" : reader.GetString("Dependency")
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
                (FolderPath, Source, WorkshopID, Title, Description, FileSize, FileSizeInACF,
                    CreationTime, SubTime, ModifiedTime, UpdateTime, Preview, ContentRating, Tags,
                    Type, Dependency, CachedAt)
                VALUES (@FolderPath, @Source, @WorkshopID, @Title, @Description, @FileSize, @FileSizeInACF,
                    @CreationTime, @SubTime, @ModifiedTime, @UpdateTime, @Preview, @ContentRating, @Tags,
                    @Type, @Dependency, @CachedAt)
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
                cmd.Parameters.AddWithValue("@FileSizeInACF", item.FileSizeInACF);
                cmd.Parameters.AddWithValue("@SubTime", item.SubTime.ToString("o"));
                cmd.Parameters.AddWithValue("@ModifiedTime", item.ModifiedTime.ToString("o"));
                cmd.Parameters.AddWithValue("@UpdateTime", item.UpdateTime.ToString("o"));
                cmd.Parameters.AddWithValue("@CreationTime", item.CreationTime.ToString("o"));
                cmd.Parameters.AddWithValue("@Preview", item.Preview);
                cmd.Parameters.AddWithValue("@ContentRating", item.ContentRating);
                cmd.Parameters.AddWithValue("@Tags", item.Tags ?? "Unspecified");
                cmd.Parameters.AddWithValue("@Type", item.Type);
                cmd.Parameters.AddWithValue("@Dependency", item.Dependency ?? "");
                cmd.Parameters.AddWithValue("@CachedAt", DateTime.UtcNow.ToString("o"));

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
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
            return [];

        var effectiveCachePath = string.IsNullOrEmpty(cacheDbPath)
            ? GetDefaultCachePath()
            : cacheDbPath;

        var installedIDs = GetInstalledWorkshopIDs(acfPath);
        var resultsBag = new ConcurrentBag<WallpaperItem>();
        var parsedItems = new ConcurrentBag<WallpaperItem>();
        var sw = Stopwatch.StartNew();

        Log.Information($"开始扫描源 {source}... 根目录: {rootPath}");

        try
        {
            EnsureDatabaseInitialized(effectiveCachePath);
            var cacheDict = LoadCacheDictionary(effectiveCachePath);

            var enumOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            };

            var wallpaperDirs = Directory.EnumerateDirectories(rootPath, "*", enumOptions)
                .Where(dir => File.Exists(Path.Combine(dir, "project.json")))
                .ToList();

            // 缓存命中判断
            var toParse = new List<string>();
            foreach (var current in wallpaperDirs)
            {
                var currentUpdateTime = Directory.GetLastWriteTime(current);
                if (cacheDict.TryGetValue(current, out var entry) &&
                    entry.Item.ModifiedTime == currentUpdateTime)
                {
                    resultsBag.Add(entry.Item);
                }
                else
                {
                    toParse.Add(current);
                }
            }

            Log.Information($"SQLite 缓存命中 {resultsBag.Count} 个壁纸，需解析 {toParse.Count} 个新/更新壁纸");

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
                    var item = await ParseWallpaperAsync(current, installedIDs, source, token);
                    if (item is not null)
                    {
                        resultsBag.Add(item);
                        parsedItems.Add(item);
                    }
                });
            }

            // === 保存新增/修改的壁纸到缓存 ===
            if (parsedItems.Count > 0)
                SaveItemsToCache(effectiveCachePath, parsedItems);
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

    private static FrozenDictionary<string, AcfInfo> GetInstalledWorkshopIDs(string acfPath)
    {
        if (!File.Exists(acfPath)) return FrozenDictionary<string, AcfInfo>.Empty;

        try
        {
            var dict = new Dictionary<string, AcfInfo>();
            var lines = File.ReadAllLines(acfPath);
            string currentId = "";
            bool inDetails = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed == "\"WorkshopItemDetails\"") inDetails = true;

                var idMatch = AcfIdRegex.Match(trimmed);
                if (idMatch.Success) currentId = idMatch.Groups[1].Value;

                var kvMatch = AcfKeyValueRegex.Match(trimmed);
                if (kvMatch.Success && !string.IsNullOrEmpty(currentId))
                {
                    var key = kvMatch.Groups["key"].Value;
                    var val = kvMatch.Groups["value"].Value;

                    if (!dict.TryGetValue(currentId, out var info)) info = new AcfInfo(0, 0, 0);

                    if (!inDetails)
                    {
                        if (key == "size" && long.TryParse(val, out var s)) info = info with { Size = s };
                        if (key == "timeupdated" && long.TryParse(val, out var u)) info = info with { UpdateTime = u, SubTime = u };
                    }
                    else
                    {
                        if (key == "timeupdated" && long.TryParse(val, out var u)) info = info with { UpdateTime = u };
                        if (key == "timetouched" && long.TryParse(val, out var t)) info = info with { SubTime = t };
                    }
                    dict[currentId] = info;
                }
            }
            return dict.ToFrozenDictionary();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "解析 .acf 文件出现异常。");
            return FrozenDictionary<string, AcfInfo>.Empty;
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
        FrozenDictionary<string, AcfInfo> installedIDs,
        string source,
        CancellationToken ct)
    {
        try
        {
            var folderName = Path.GetFileName(current);
            var isInstalled = installedIDs.TryGetValue(folderName, out var acfInfo);
            var matchedID = isInstalled ? folderName : "";

            var currentCreateTime = Directory.GetCreationTime(current);
            var currentModTime = Directory.GetLastWriteTime(current);

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
                FileSizeInACF = isInstalled ? acfInfo.Size : 0L,
                CreationTime = currentCreateTime,
                ModifiedTime = currentModTime,
                SubTime = (isInstalled && acfInfo?.SubTime > 0) ? DateTimeOffset.FromUnixTimeSeconds(acfInfo.SubTime).ToLocalTime().DateTime : currentCreateTime,
                UpdateTime = (isInstalled && acfInfo?.UpdateTime > 0) ? DateTimeOffset.FromUnixTimeSeconds(acfInfo.UpdateTime).ToLocalTime().DateTime : currentModTime,
                Preview = previewFullPath,
                ContentRating = metadata.Contentrating ?? "Everyone",
                Tags = tagsString,
                Type = finalType,
                Source = source,
                Dependency = dependency
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, $"解析壁纸文件夹失败: {current}");
            return null;
        }
    }
}
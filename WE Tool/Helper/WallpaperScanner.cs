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
    /// <summary>
    /// 全局扫描并发闸:三个源(workshop/official/mine)同时扫描时,限制总并行解析数。
    /// 解析工作是 IO 密集(读 project.json + 递归数文件),核数级并行对磁盘无益、反而抖动;
    /// 2×核数让单源吃满、多源共存时不至于 3×核数线程同时抢盘。
    /// </summary>
    private static readonly SemaphoreSlim GlobalScanThrottle = new(2 * Environment.ProcessorCount);

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
    /// 抓取 VDF 订阅文件中的叶子块(工坊条目块内无嵌套大括号)。
    /// 两级匹配去字段顺序依赖:先抓块,再在块内独立找各字段;
    /// disabled_locally 缺失或非 "1" 均视为未停用(旧实现要求两字段同现且有序,字段一变即静默漏判)。
    /// </summary>
    private static readonly Regex VdfLeafBlockRegex = new(
        @"""(\d+)""\s*\{([^{}]*)\}",
        RegexOptions.Compiled);

    private static readonly Regex VdfPublishedFileIdRegex = new(
        @"""publishedfileid""\s+""(\d+)""",
        RegexOptions.Compiled);

    private static readonly Regex VdfDisabledLocallyRegex = new(
        @"""disabled_locally""\s+""(\d+)""",
        RegexOptions.Compiled);

    // ====================== 扫描缓存(JSON 单文件,原子写) ======================
    // 旧实现用 Microsoft.Data.Sqlite 存 SQLite 数据库;实际用法只有"整表读进内存 + 整批重写",
    // 没用到 SQLite 的查询/索引/并发能力,却背上原生库依赖(e_sqlite3.dll)与 AOT 裁剪风险。
    // 自实现:单个 JSON 文件(源生成器序列化,零反射、AOT 安全),写时先落临时文件再原子替换,
    // 读失败/损坏一律回退全量扫描。
    // 注:CacheFile/CachedEntry 须为 internal(源生成器要求类型可达,private 嵌套类无法注册)。
    internal sealed class CacheFile
    {
        /// <summary>缓存结构版本。版本不符 → 整体作废回退全量扫描(不做迁移)。当前:2。</summary>
        public int Version { get; set; }
        public DateTime SavedAtUtc { get; set; }
        public List<CachedEntry> Entries { get; set; } = [];
        public List<CachedComponent> Components { get; set; } = [];
    }

    internal sealed class CachedEntry
    {
        public WallpaperItem Item { get; set; } = null!;
        public DateTime UpdateTime { get; set; }
        /// <summary>缓存键之一:project.json 的 LastWriteTimeUtc(解析时的文件元数据快照)。</summary>
        public DateTime JsonUpdateTime { get; set; }
        /// <summary>缓存键之二:project.json 的字节长度。</summary>
        public long JsonLength { get; set; }
        public DateTime CachedAt { get; set; }
    }

    internal sealed class CachedComponent
    {
        public ComponentInfo Component { get; set; } = null!;
        /// <summary>缓存键:project.json 的 LastWriteTimeUtc + Length。</summary>
        public DateTime JsonUpdateTime { get; set; }
        public long JsonLength { get; set; }
    }

    private static readonly object CacheWriteLock = new();

    private static string GetDefaultCachePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WE_Tool", "wallpaper_cache.json");

    /// <summary>当前缓存结构版本,与 CacheFile.Version 对应;不符则整体作废。</summary>
    private const int CacheSchemaVersion = 2;

    /// <summary>读取缓存文件(损坏/缺失/版本不符 → 空字典,由调用方回退全量扫描)。
    /// 调用方若已持有 CacheWriteLock,用 LoadCacheDictionaryLocked 避免重复加锁。</summary>
    private static Dictionary<string, CachedEntry> LoadCacheDictionary(string cachePath)
    {
        lock (CacheWriteLock)
        {
            return LoadCacheDictionaryLocked(cachePath);
        }
    }

    /// <summary>锁内版本:不做自己的加锁,供 SaveItemsToCache 在持锁状态下复用。</summary>
    private static Dictionary<string, CachedEntry> LoadCacheDictionaryLocked(string cachePath)
    {
        if (!File.Exists(cachePath)) return [];

        try
        {
            var json = File.ReadAllText(cachePath);
            var cacheFile = JsonSerializer.Deserialize(json, JsonContext.Default.CacheFile);
            if (cacheFile?.Entries == null || cacheFile.Version != CacheSchemaVersion) return [];

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

    /// <summary>原子写缓存(读-修剪-合并-写,全程持锁):
    /// 1. 读入现有缓存(其它源的条目必须保留——三源并行共用同一文件);
    /// 2. 按本源修剪:survivingKeys 之外、位于本源根目录下的条目视为"目录已消失",删除;
    /// 3. upsert 本次新解析条目;
    /// 4. 整体序列化,先落临时文件再原子替换。
    /// </summary>
    private static void SaveItemsToCache(
        string cachePath,
        string rootPath,
        IReadOnlyDictionary<string, byte> survivingKeys,
        IEnumerable<WallpaperItem> parsedItems,
        IReadOnlyDictionary<string, (DateTime JsonTime, long JsonLen)> jsonMeta)
    {
        // 三源并行 + 与 LoadCacheDictionary 互斥,读写全程串行化
        lock (CacheWriteLock)
        {
            try
            {
                var dict = LoadCacheDictionaryLocked(cachePath);

                // 按源修剪:只动本源根目录下的键,其它源的条目一律不碰
                var removed = 0;
                foreach (var key in dict.Keys.ToList())
                {
                    if (!survivingKeys.ContainsKey(key) && IsPathUnderRoot(key, rootPath))
                    {
                        dict.Remove(key);
                        removed++;
                    }
                }

                foreach (var item in parsedItems)
                {
                    if (item.FolderPath == null) continue;
                    var (jsonTime, jsonLen) = jsonMeta.TryGetValue(item.FolderPath, out var meta)
                        ? meta
                        : default;
                    dict[item.FolderPath] = new CachedEntry
                    {
                        Item = item,
                        UpdateTime = item.UpdateTime,
                        JsonUpdateTime = jsonTime,
                        JsonLength = jsonLen,
                        CachedAt = DateTime.UtcNow
                    };
                }

                var cacheFile = new CacheFile
                {
                    Version = CacheSchemaVersion,
                    SavedAtUtc = DateTime.UtcNow,
                    Entries = [.. dict.Values]
                };

                var json = JsonSerializer.Serialize(cacheFile, JsonContext.Default.CacheFile);
                var dir = Path.GetDirectoryName(cachePath);
                if (dir != null) Directory.CreateDirectory(dir);

                var tempPath = cachePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, cachePath, overwrite: true);
                Log.Information("已缓存 {Count} 个壁纸到 JSON 缓存(修剪 {Removed} 个失效条目)",
                    cacheFile.Entries.Count, removed);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "保存扫描缓存失败（不影响扫描结果）");
                try { if (File.Exists(cachePath + ".tmp")) File.Delete(cachePath + ".tmp"); }
                catch { /* 忽略清理失败 */ }
            }
        }
    }

    /// <summary>判断 path 是否位于 rootPath 目录之下(含子目录);两者都按完整路径比较。</summary>
    private static bool IsPathUnderRoot(string path, string rootPath)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(rootPath)) return false;
        var root = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>读取组件缓存字典(key = FolderPath)。损坏/缺失/版本不符 → 空字典。
    /// 与写入方共用 CacheWriteLock,避免读到原子替换中途的文件。</summary>
    private static Dictionary<string, CachedComponent> LoadComponents(string cachePath)
    {
        lock (CacheWriteLock)
        {
            return LoadComponentsLocked(cachePath);
        }
    }

    private static Dictionary<string, CachedComponent> LoadComponentsLocked(string cachePath)
    {
        if (!File.Exists(cachePath)) return [];
        try
        {
            var cacheFile = JsonSerializer.Deserialize(File.ReadAllText(cachePath), JsonContext.Default.CacheFile);
            if (cacheFile?.Components == null || cacheFile.Version != CacheSchemaVersion) return [];
            var dict = new Dictionary<string, CachedComponent>(StringComparer.Ordinal);
            foreach (var c in cacheFile.Components)
            {
                if (c.Component?.FolderPath != null)
                    dict[c.Component.FolderPath] = c;
            }
            return dict;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载组件缓存失败，将全量解析组件。");
            return [];
        }
    }

    /// <summary>锁内整体替换组件缓存段(组件只有 workshop 一个写方,无需按源修剪)。
    /// 与壁纸条目共用一个文件:读-改-写全程持 CacheWriteLock。</summary>
    private static void SaveComponentsToCache(string cachePath, List<ComponentInfo> components)
    {
        lock (CacheWriteLock)
        {
            try
            {
                // 读入现有文件,保留壁纸条目
                CacheFile? cacheFile = null;
                if (File.Exists(cachePath))
                {
                    try
                    {
                        cacheFile = JsonSerializer.Deserialize(File.ReadAllText(cachePath), JsonContext.Default.CacheFile);
                    }
                    catch { /* 损坏则从空重建 */ }
                }
                cacheFile ??= new CacheFile();
                cacheFile.Version = CacheSchemaVersion;
                cacheFile.SavedAtUtc = DateTime.UtcNow;

                var existing = cacheFile.Components ?? [];
                var byFolder = new Dictionary<string, CachedComponent>(StringComparer.Ordinal);
                foreach (var c in existing)
                {
                    if (c.Component?.FolderPath != null) byFolder[c.Component.FolderPath] = c;
                }

                // 只更新本次实际解析过的组件;未解析的(缓存命中复用)保留原键值
                foreach (var comp in components)
                {
                    if (comp.FolderPath == null) continue;
                    var fi = new FileInfo(Path.Combine(comp.FolderPath, "project.json"));
                    byFolder[comp.FolderPath] = new CachedComponent
                    {
                        Component = comp,
                        JsonUpdateTime = fi.LastWriteTimeUtc,
                        JsonLength = fi.Length
                    };
                }

                cacheFile.Components = [.. byFolder.Values];

                var json = JsonSerializer.Serialize(cacheFile, JsonContext.Default.CacheFile);
                var dir = Path.GetDirectoryName(cachePath);
                if (dir != null) Directory.CreateDirectory(dir);
                var tempPath = cachePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, cachePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "保存组件缓存失败（不影响扫描结果）");
                try { if (File.Exists(cachePath + ".tmp")) File.Delete(cachePath + ".tmp"); }
                catch { /* 忽略清理失败 */ }
            }
        }
    }

    public static async Task<List<WallpaperItem>> ScanWallpapers(
        string rootPath,
        string source,
        string acfPath,
        string? cacheDbPath = null,
        string? vdfPath = null,
        bool useCache = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
        {
            // 路径无效 = 库不存在:workshop 源的组件列表也要诚实置空,不能残留上次扫描的数据
            if (source == "workshop")
                LastComponents = [];
            return [];
        }

        // ACF 一次读取,三份数据(安装ID/更新时间/大小)一次解析;official/mine 无 acfPath → 空快照
        var acf = ParseAcfSnapshot(acfPath);
        var installedIDs = acf.InstalledIDs;
        var acfUpdateTimes = acf.UpdateTimes;
        var acfSizes = acf.Sizes;
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

            // 本源"存活"目录全集(缓存命中 ∪ 解析成功),保存时用于按源修剪失效条目。
            // 并行解析阶段多线程写入,必须用并发容器 —— 普通 HashSet 被并发 Add 会损坏内部数组
            // (实测症状:AddIfNotPresent 抛 IndexOutOfRangeException)
            var survivingKeys = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            // 各目录 project.json 元数据快照(缓存键),命中时来自枚举、解析后重新取值
            var jsonMeta = new ConcurrentDictionary<string, (DateTime JsonTime, long JsonLen)>(StringComparer.Ordinal);

            if (useCache)
            {
                var cacheDict = LoadCacheDictionary(effectiveCachePath!);

                // 缓存命中判断:键 = project.json 的 LastWriteTimeUtc + Length。
                // 原地改写 project.json(属性面板保存等)不改父目录 mtime,
                // 旧方案(目录 mtime)会漏判;文件元数据恰好盖住元数据编辑场景。
                toParse = [];
                foreach (var current in wallpaperDirs)
                {
                    var fi = new FileInfo(Path.Combine(current, "project.json"));
                    var currentJsonTime = fi.LastWriteTimeUtc;
                    var currentJsonLength = fi.Length;

                    if (cacheDict.TryGetValue(current, out var entry) &&
                        entry.JsonUpdateTime == currentJsonTime &&
                        entry.JsonLength == currentJsonLength)
                    {
                        resultsBag.Add(entry.Item);
                        survivingKeys[current] = 0;
                        jsonMeta[current] = (currentJsonTime, currentJsonLength);
                    }
                    else
                    {
                        toParse.Add(current);
                    }
                }

                Log.Information("JSON 缓存命中 {Hit} 个壁纸，需解析 {Parse} 个新/更新壁纸",
                    resultsBag.Count, toParse.Count);
            }
            else
            {
                // 缓存关闭：全部重新解析
                toParse = wallpaperDirs;
                Log.Information($"缓存已关闭，将解析全部 {toParse.Count} 个壁纸");
            }

            // === 合并并行:壁纸解析 + 组件扫描一次遍历 ===
            // 旧结构是两个串行 ForEachAsync(先解析壁纸、再扫组件)遍历同一批目录两次;
            // 合并后一次遍历同时产出壁纸与组件。全部目录都要看(组件不看壁纸缓存、只认组件缓存),
            // 壁纸部分仅在缓存未命中的目录上真正解析。
            // 空库边界:componentBag 为空时仍要诚实置空 LastComponents,不留上次残留。
            if (source == "workshop" && wallpaperDirs.Count == 0)
                LastComponents = [];
            if (wallpaperDirs.Count > 0)
            {
                var toParseSet = toParse.ToHashSet(StringComparer.Ordinal);
                // 组件缓存仅 workshop 源需要;缓存关闭(useCache=false)时为空字典 → 全量解析
                var componentCache = source == "workshop" && useCache
                    ? LoadComponents(effectiveCachePath!)
                    : [];
                var componentBag = new ConcurrentBag<ComponentInfo>();
                var parsedComponentCount = 0;
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = ct
                };
                await Parallel.ForEachAsync(wallpaperDirs, parallelOptions, async (current, token) =>
                {
                    // ---- 壁纸部分:仅缓存未命中的目录需要解析 ----
                    if (toParseSet.Contains(current))
                    {
                        // 全局闸:三个源并行时限制总解析并发(IO 密集,核数级并行对磁盘无益)
                        await GlobalScanThrottle.WaitAsync(token);
                        try
                        {
                            var item = await ParseWallpaperAsync(current, installedIDs, source, acfUpdateTimes, acfSizes, activeSubscribedIDs, token);
                            if (item is not null)
                            {
                                resultsBag.Add(item);
                                parsedItems.Add(item);
                                survivingKeys[current] = 0;
                                // 解析后重取元数据:解析读的就是这个文件,以最新状态为缓存键
                                var pj = new FileInfo(Path.Combine(current, "project.json"));
                                jsonMeta[current] = (pj.LastWriteTimeUtc, pj.Length);
                            }
                        }
                        finally
                        {
                            GlobalScanThrottle.Release();
                        }
                    }
                    // ---- 组件部分:仅 workshop 源;组件缓存命中则直接复用 ----
                    if (source == "workshop")
                    {
                        if (token.IsCancellationRequested) return;
                        try
                        {
                            if (componentCache.TryGetValue(current, out var cached))
                            {
                                var cj = new FileInfo(Path.Combine(current, "project.json"));
                                if (cached.JsonUpdateTime == cj.LastWriteTimeUtc && cached.JsonLength == cj.Length)
                                {
                                    componentBag.Add(cached.Component);
                                    return;
                                }
                            }
                            await GlobalScanThrottle.WaitAsync(token);
                            try
                            {
                                var comp = await ParseComponentAsync(current, "", acfUpdateTimes, token);
                                if (comp is not null)
                                {
                                    componentBag.Add(comp);
                                    Interlocked.Increment(ref parsedComponentCount);
                                }
                            }
                            finally
                            {
                                GlobalScanThrottle.Release();
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "组件解析失败,已跳过: {Dir}", current);
                        }
                    }
                });
                // 组件收尾:结果与"最近一次成功完成的 workshop 扫描"严格一致(空 → 空列表)
                LastComponents = [.. componentBag];
                Log.Information("组件扫描完成，共 {Count} 个(缓存命中 {Hit},解析 {Parsed})",
                    componentBag.Count, componentBag.Count - parsedComponentCount, parsedComponentCount);
                if (source == "workshop" && useCache && parsedComponentCount > 0)
                    SaveComponentsToCache(effectiveCachePath!, [.. componentBag]);
            }
            // === 保存新增/修改的壁纸到缓存(锁内读旧缓存合并,保留其它源条目) ===

            if (useCache && parsedItems.Count > 0)

                SaveItemsToCache(effectiveCachePath!, rootPath, survivingKeys, parsedItems, jsonMeta);


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

    /// <summary>ACF 一次读取解析出的全部快照数据(安装 ID 集 / 更新时间 / Steam 报告大小)。</summary>
    private sealed record AcfSnapshot(
        FrozenSet<string> InstalledIDs,
        Dictionary<string, DateTime> UpdateTimes,
        Dictionary<string, long> Sizes);

    private static readonly AcfSnapshot EmptyAcfSnapshot = new(
        FrozenSet<string>.Empty, [], []);

    /// <summary>读取并一次性解析 .acf 文件(旧实现三个函数各读一遍同一文件)。
    /// 文件缺失/解析异常时返回空快照,调用方按无 ACF 处理。</summary>
    private static AcfSnapshot ParseAcfSnapshot(string acfPath)
    {
        if (string.IsNullOrEmpty(acfPath) || !File.Exists(acfPath))
            return EmptyAcfSnapshot;

        try
        {
            var content = File.ReadAllText(acfPath);

            var ids = WorkshopIdRegex.Matches(content)
                .Select(m => m.Groups[1].Value)
                .ToFrozenSet();

            // 只扫描 WorkshopItemsInstalled 段内的条目
            var updateTimes = new Dictionary<string, DateTime>();
            var sizes = new Dictionary<string, long>();
            var sectionMatch = Regex.Match(content,
                @"""WorkshopItemsInstalled""\s*\{(?<body>.+?)\}\s*""WorkshopItemDetails""",
                RegexOptions.Singleline);
            if (sectionMatch.Success)
            {
                var body = sectionMatch.Groups["body"].Value;

                foreach (Match match in AcfTimeUpdatedRegex.Matches(body))
                {
                    if (long.TryParse(match.Groups[2].Value, out var unixTs))
                        updateTimes[match.Groups[1].Value] =
                            DateTimeOffset.FromUnixTimeSeconds(unixTs).DateTime;
                }

                foreach (Match match in AcfSizeRegex.Matches(body))
                {
                    if (long.TryParse(match.Groups[2].Value, out var size))
                        sizes[match.Groups[1].Value] = size;
                }
            }

            return new AcfSnapshot(ids, updateTimes, sizes);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "解析 .acf 文件出现异常。");
            return EmptyAcfSnapshot;
        }
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
            var result = new HashSet<string>(StringComparer.Ordinal);

            // 两级匹配:叶子块内独立判断,消除对字段顺序/同现的依赖
            foreach (Match block in VdfLeafBlockRegex.Matches(content))
            {
                var body = block.Groups[2].Value;
                var pid = VdfPublishedFileIdRegex.Match(body);
                if (!pid.Success) continue;

                var dis = VdfDisabledLocallyRegex.Match(body);
                if (!dis.Success || dis.Groups[1].Value != "1")
                    result.Add(pid.Groups[1].Value);
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
                if (j < text.Length && (text[j] == '}' || text[j] == ']')) continue;
            }
            sb.Append(c);
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
            string? matchedID = installedIDs.Contains(folderName) ? folderName : null;

            var jsonPath = Path.Combine(current, "project.json");
            var jsonText = SanitizeProjectJson(await File.ReadAllTextAsync(jsonPath, ct));
            var metadata = JsonSerializer.Deserialize(jsonText, JsonContext.Default.ProjectMetadata)
                           ?? throw new InvalidOperationException("JSON 反序列化失败");

            // 文件夹名不在 ACF 安装表时兜底 project.json 的 workshopid
            // (手动改过名的工坊文件夹不再被误判为"应不存在")
            matchedID ??= metadata.Workshopid;

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
                // 与壁纸侧同一语义:每个项目只有一个标签(project.json 里是列表形态,取第一个非空项)
                tagsString = tags.Count > 0 ? tags[0]! : "Unspecified";
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
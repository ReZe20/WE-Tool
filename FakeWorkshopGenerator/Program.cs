using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FakeWorkshopGenerator;

/// <summary>
/// 假创意工坊壁纸库生成器(Papers 页性能测试专用)。
///
/// 生成一个与真实创意工坊库同构的假库:
///   - 每个壁纸目录含 project.json(标题/类型/标签/可见性等真实字段)
///   - 预览图 = 从"真实创意工坊库"抽取的 50 张真实壁纸 preview(池化,同卷硬链接,零额外磁盘)
///   - 配套 appworkshop_431960.acf + 431960_subscriptions.vdf 订阅表
///   - 混入少量异构样本(损坏 JSON / private 下架 / preset),触发扫描器容错路径
///
/// 真实库路径来源(优先级):
///   1. --source 参数
///   2. %LOCALAPPDATA%\WE_Tool\config.json 的 Path.WorkshopPath(用户跑过 WE Tool 即自动可用)
///
/// 用法:
///   FakeWorkshopGenerator --count 10000 --output <目录> [--source <真实库>] [--preview-pool 50]
///
/// 配合 FakeWorkshopLauncher 使用:
///   FakeWorkshopGenerator --count 10000 --output TestData/FakeWorkshop
///   FakeWorkshopLauncher --data TestData/FakeWorkshop   # 以假库调试模式启动 WE Tool
/// </summary>
internal static class Program
{
    private const string SteamAppId = "431960";

    // 类型分布(参考真实库摸底:视频 ~61%, 原生场景 ~36%, HTML 少量)
    private static readonly (string Type, double Weight)[] TypeWeights =
    {
        ("video", 0.61),
        ("scene", 0.36),
        ("html", 0.03),
    };

    private static readonly string[] ContentRatings = { "Everyone", "Mature", "Everyone", "Everyone" };
    private static readonly string[] TagPool =
    {
        "Anime", "Game", "Landscape", "Sci-Fi", "Minimalist", "Abstract",
        "Music", "Cyberpunk", "Nature", "Space", "Fantasy", "Retro",
    };
    private static readonly string[] TitleAdjectives =
        { "Neon", "Starry", "Cyber", "Golden", "Silent", "Ocean", "Crimson", "Lunar", "Misty", "Electric" };
    private static readonly string[] TitleNouns =
        { "City", "Horizon", "Dream", "River", "Sky", "Forest", "Nebula", "Storm", "Garden", "Void" };

    // Steam 工坊 17 位数字 ID 起始
    private const long IdStart = 1_000_000_000_000_000_000L;

    private static readonly Random Rng = new();

    // 无 BOM UTF8:真实 project.json / Steam acf / vdf 均无 BOM,保持一致
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private sealed class Options
    {
        public int Count = 10000;
        public string OutputDir = "";
        public string? SourceDir;
        public int PreviewPoolSize = 50;
        public long IdStart;
        public bool NoHardlink;
        public bool Force;
    }

    private static int Main(string[] args)
    {
        // 控制台输出 UTF-8(默认 GBK 会让中文乱码)
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 忽略 */ }
        try
        {
            var opts = ParseArgs(args);
            if (opts.Count <= 0) throw new ArgumentException("--count 必须为正整数");
            if (string.IsNullOrEmpty(opts.OutputDir)) throw new ArgumentException("--output 必填");

            var outputRoot = Path.GetFullPath(opts.OutputDir);
            var workshopDir = Path.Combine(outputRoot, "workshop");
            if (Directory.Exists(workshopDir))
            {
                if (!opts.Force)
                    throw new InvalidOperationException($"输出目录已存在: {workshopDir}\n请先删除或换目录(拒绝覆盖,防误删真实数据);或加 --force 先删后建");
                // --force:先删整个输出根再生成(只删用户显式指定的输出目录,安全边界明确)
                Console.WriteLine($"[FakeWorkshopGenerator] --force: 删除已存在的输出目录 {outputRoot}");
                Directory.Delete(outputRoot, recursive: true);
            }

            // 真实库路径解析
            var sourceDir = ResolveSourceDir(opts.SourceDir);
            Console.WriteLine($"[FakeWorkshopGenerator] 源库: {sourceDir}");

            // 收集预览图池
            var previewPool = CollectPreviewPool(sourceDir, opts.PreviewPoolSize);
            if (previewPool.Count == 0)
                throw new InvalidOperationException($"源库中没有找到 preview 图片: {sourceDir}");
            Console.WriteLine($"[FakeWorkshopGenerator] 预览池: {previewPool.Count} 张真实预览图");

            // 把池子复制到输出目录 _preview_pool/(跨卷只此一次),之后壁纸目录从池内硬链接 ——
            // 源库与输出目录跨卷时无法直接硬链接,池化后保证后续链接全部同卷零拷贝。
            // 池文件统一改名 pool_{idx}.{ext}:源库目录的预览都叫 preview.jpg/gif,
            // 直接复制会大量重名;统一序号名后壁纸目录文件名即池名,project.json 写它,
            // 无重名冲突、文件与字段永远一致
            var poolDir = Path.Combine(outputRoot, "_preview_pool");
            Directory.CreateDirectory(poolDir);
            var localPool = new List<string>();
            for (int pi = 0; pi < previewPool.Count; pi++)
            {
                var srcPreview = previewPool[pi];
                var ext = Path.GetExtension(srcPreview);
                var dest = Path.Combine(poolDir, $"pool_{pi}{ext}");
                File.Copy(srcPreview, dest);
                localPool.Add(dest);
            }
            Console.WriteLine($"[FakeWorkshopGenerator] 预览池已复制到 {poolDir}");

            Directory.CreateDirectory(workshopDir);
            var sw = Stopwatch.StartNew();
            Console.WriteLine($"生成 {opts.Count} 个壁纸目录到 {workshopDir} ...");

            int brokenCount = 0;
            for (int i = 0; i < opts.Count; i++)
            {
                var wid = (opts.IdStart + i).ToString();
                var wpDir = Path.Combine(workshopDir, wid);
                Directory.CreateDirectory(wpDir);

                // 类型:按权重抽取
                var typeRoll = Rng.NextDouble();
                double acc = 0;
                var wpType = TypeWeights[^1].Type;
                foreach (var (tname, weight) in TypeWeights)
                {
                    acc += weight;
                    if (typeRoll < acc) { wpType = tname; break; }
                }

                var title = $"{TitleAdjectives[Rng.Next(TitleAdjectives.Length)]} {TitleNouns[Rng.Next(TitleNouns.Length)]} {i}";
                // 20% 长描述
                var descLen = Rng.NextDouble() < 0.2
                    ? (new[] { 120, 300, 800 })[Rng.Next(3)]
                    : Rng.Next(20, 90);

                // 预览图:从本地池(同卷)轮询取一张硬链接;NoHardlink 或失败回退复制
                var srcPreview = localPool[i % localPool.Count];
                var previewName = Path.GetFileName(srcPreview);
                var destPreview = Path.Combine(wpDir, previewName);
                if (opts.NoHardlink || !TryHardlink(srcPreview, destPreview))
                    File.Copy(srcPreview, destPreview);

                // project.json(preview 字段与文件名一致)
                var doc = new JsonObject
                {
                    ["title"] = title,
                    ["description"] = new string('x', descLen),
                    ["type"] = wpType,
                    ["file"] = wpType switch
                    {
                        "video" => "wallpaper.mp4",
                        "html" => "index.html",
                        _ => "scene.pkg",
                    },
                    ["preview"] = previewName,
                    ["tags"] = new JsonArray(TagPool.OrderBy(_ => Rng.Next()).Take(Rng.Next(1, 4))
                        .Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
                    ["workshopid"] = wid,
                    ["contentrating"] = ContentRatings[Rng.Next(ContentRatings.Length)],
                    ["visibility"] = "public",
                };
                var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(wpDir, "project.json"), json, Utf8NoBom);

                // 异构样本(每 300 条撒一个)
                if (i % 300 == 299)
                {
                    var kind = (i / 300) % 4;
                    var pjPath = Path.Combine(wpDir, "project.json");
                    switch (kind)
                    {
                        case 0: // 损坏 JSON
                            File.WriteAllText(pjPath, "{\"title\": \"broken\", \"type\": \"scene\", ", Utf8NoBom);
                            brokenCount++;
                            break;
                        case 1: // private 下架
                            doc["visibility"] = "private";
                            File.WriteAllText(pjPath, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);
                            break;
                        case 2: // preset(带依赖)
                            doc["type"] = "preset";
                            doc["dependency"] = "Neon City 1";
                            File.WriteAllText(pjPath, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);
                            break;
                    }
                }

                if ((i + 1) % 1000 == 0)
                    Console.WriteLine($"  ... {i + 1}/{opts.Count} ({sw.Elapsed.TotalSeconds:F1}s)");
            }

            // ACF / VDF
            var acfPath = WriteAcf(outputRoot, opts.Count, opts.IdStart);
            var vdfPath = WriteVdf(outputRoot, opts.Count, opts.IdStart);

            Console.WriteLine($"\n完成: {opts.Count} 个壁纸, {sw.Elapsed.TotalSeconds:F1}s");
            Console.WriteLine($"  创意工坊目录: {workshopDir}");
            Console.WriteLine($"  ACF 文件:     {acfPath}");
            Console.WriteLine($"  VDF 文件:     {vdfPath}");
            if (brokenCount > 0) Console.WriteLine($"  (含 {brokenCount} 个损坏 project.json 容错样本)");
            Console.WriteLine("\n使用方法:");
            Console.WriteLine($"  FakeWorkshopLauncher --data \"{outputRoot}\"");
            Console.WriteLine("  然后 WE Tool 以假库调试模式打开,在 Papers 页测试滚动/筛选/多选性能。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FakeWorkshopGenerator] 错误: {ex.Message}");
            return 1;
        }
    }

    private static Options ParseArgs(string[] args)
    {
        var opts = new Options { IdStart = IdStart };
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--count" when i + 1 < args.Length: opts.Count = int.Parse(args[++i]); break;
                case "--output" when i + 1 < args.Length: opts.OutputDir = args[++i]; break;
                case "--source" when i + 1 < args.Length: opts.SourceDir = args[++i]; break;
                case "--preview-pool" when i + 1 < args.Length: opts.PreviewPoolSize = int.Parse(args[++i]); break;
                case "--id-start" when i + 1 < args.Length: opts.IdStart = long.Parse(args[++i]); break;
                case "--no-hardlink": opts.NoHardlink = true; break;
                case "--force": opts.Force = true; break;
                default: Console.Error.WriteLine($"[警告] 未知参数: {args[i]}"); break;
            }
        }
        return opts;
    }

    /// <summary>解析真实创意工坊库路径:--source > config.json 的 Path.WorkshopPath。</summary>
    private static string ResolveSourceDir(string? sourceOverride)
    {
        if (!string.IsNullOrEmpty(sourceOverride))
        {
            if (!Directory.Exists(sourceOverride))
                throw new DirectoryNotFoundException($"--source 目录不存在: {sourceOverride}");
            return Path.GetFullPath(sourceOverride);
        }

        // 读 WE Tool config.json
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WE_Tool");
        var configPath = Path.Combine(appData, "config.json");
        if (File.Exists(configPath))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
                var ws = root?["Path"]?["WorkshopPath"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrEmpty(ws) && Directory.Exists(ws))
                    return Path.GetFullPath(ws);
            }
            catch { /* 落到报错 */ }
        }
        throw new DirectoryNotFoundException(
            $"未找到真实创意工坊库。请用 --source 指定,或先运行一次 WE Tool 让 config.json 写入真实路径。\n" +
            $"查找位置: {configPath}");
    }

    /// <summary>
    /// 从源库收集 preview 图片池(取 N 张,只收 contentrating == Everyone 的壁纸,
    /// 避免把成人内容预览带进开发调试环境)。
    /// preview 文件名以各目录 project.json 的 "preview" 字段为准(真实库多为 preview.jpg/gif),
    /// 字段缺失/指向不存在时兜底枚举目录内 preview.* 文件。
    /// </summary>
    private static List<string> CollectPreviewPool(string sourceDir, int poolSize)
    {
        var candidates = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(sourceDir))
            {
                // 读 project.json:分级判定 + preview 字段一次解析;非 Everyone 一律跳过
                var pjPath = Path.Combine(dir, "project.json");
                if (!File.Exists(pjPath)) continue;
                JsonObject? pjRoot = null;
                try
                {
                    pjRoot = JsonNode.Parse(File.ReadAllText(pjPath)) as JsonObject;
                }
                catch
                {
                    continue; // project.json 损坏 → 跳过(无法确认分级,宁可不要)
                }
                var rating = pjRoot?["contentrating"]?.GetValue<string>() ?? "";
                if (!string.Equals(rating.Trim(), "Everyone", StringComparison.OrdinalIgnoreCase))
                    continue; // 只收全年龄,防开发尴尬

                // preview 字段优先,缺失则兜底枚举 preview.*
                string? preview = null;
                var fieldPreview = pjRoot?["preview"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrEmpty(fieldPreview))
                {
                    var fp = Path.Combine(dir, fieldPreview);
                    if (File.Exists(fp)) preview = fp;
                }

                preview ??= new[] { ".jpg", ".jpeg", ".png", ".gif" }
                    .Select(ext => Path.Combine(dir, "preview" + ext))
                    .FirstOrDefault(File.Exists);

                if (preview != null)
                {
                    candidates.Add(preview);
                    if (candidates.Count >= poolSize * 4) break; // 够了,防巨库枚举太久
                }
            }
        }
        catch { /* 部分目录不可读则跳过 */ }

        // 打乱后取池
        return candidates.OrderBy(_ => Rng.Next()).Take(poolSize).ToList();
    }

    /// <summary>尝试硬链接(同卷零拷贝);失败返回 false(调用方回退复制)。</summary>
    private static bool TryHardlink(string src, string dest)
    {
        // File.CreateHardLink 是 .NET 6+ 通用 API;此处用 P/Invoke 最稳(跨 .NET 版本一致)
        return CreateHardLinkW(dest, src, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <summary>写 appworkshop_431960.acf(Steam 安装表形态,匹配 WallpaperScanner 正则)。</summary>
    private static string WriteAcf(string outputRoot, int count, long idStart)
    {
        var path = Path.Combine(outputRoot, $"appworkshop_{SteamAppId}.acf");
        var sb = new StringBuilder();
        sb.AppendLine("\"AppID\"\t\t\"431960\"");
        sb.AppendLine("\"WorkshopItemsInstalled\"\t\t{");
        for (int i = 0; i < count; i++)
        {
            var wid = (idStart + i).ToString();
            var ts = 1_600_000_000L + i * 3600L;
            var size = Rng.Next(20_000_000, 900_000_000);
            sb.AppendLine($"\t\t\"{wid}\"\t\t{{");
            sb.AppendLine($"\t\t\t\"timeupdated\"\t\t\"{ts}\"");
            sb.AppendLine($"\t\t\t\"size\"\t\t\"{size}\"");
            sb.AppendLine("\t\t}");
        }
        sb.AppendLine("\t}");
        File.WriteAllText(path, sb.ToString(), Utf8NoBom);
        return path;
    }

    /// <summary>写 431960_subscriptions.vdf(订阅表,匹配 WallpaperScanner 正则)。</summary>
    private static string WriteVdf(string outputRoot, int count, long idStart)
    {
        var path = Path.Combine(outputRoot, $"{SteamAppId}_subscriptions.vdf");
        var sb = new StringBuilder();
        sb.AppendLine("\"Subscriptions\"");
        sb.AppendLine("{");
        for (int i = 0; i < count; i++)
        {
            var wid = (idStart + i).ToString();
            sb.AppendLine($"\t\t\"{wid}\"");
            sb.AppendLine("\t\t{");
            sb.AppendLine($"\t\t\t\"publishedfileid\"\t\t\"{wid}\"");
            sb.AppendLine($"\t\t\t\"time_added\"\t\t\"{1_600_000_000L + i}\"");
            sb.AppendLine("\t\t}");
        }
        sb.AppendLine("}");
        File.WriteAllText(path, sb.ToString(), Utf8NoBom);
        return path;
    }
}

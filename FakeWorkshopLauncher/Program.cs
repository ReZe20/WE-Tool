using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FakeWorkshopLauncher;

/// <summary>
/// 假创意工坊调试启动器。
///
/// 作用:让 WE Tool 以"假库调试模式"打开——把真实 config.json 备份,把创意工坊相关路径
/// (Path.WorkshopPath / AcfPath / VdfPath)临时指向 FakeWorkshopGenerator 生成的假库
/// (默认 TestData/FakeWorkshop),然后启动 WE Tool(或仅切配置供 VS 调试)。
///
/// 用途:Papers 页性能测试(一万条假壁纸的滚动/筛选/多选/缓存)。
/// 安全:不改 WE Tool 一行代码;不碰真实 Steam 库;退出必还原,配置零丢失。
///
/// 用法:
///   FakeWorkshopLauncher [模式] [--data <假库根目录>] [--exe <WE_Tool.exe 路径>]
///
/// 模式(缺省 = launch):
///   launch    备份真实配置 → 写入假配置 → 启动 WE Tool → 等退出后还原(默认)
///   prepare   仅备份 + 写入假配置,不启动 WE Tool —— 供 VS 里 F5 调试 WE Tool 用:
///             先在外部跑一次 prepare,再把 VS 启动项设为 WE Tool(Unpackaged) 打断点调试,
///             调试完跑 restore 还原
///   restore   仅还原真实配置(备份存在则覆盖回;无备份且当前指向假库则删除假配置)
///   status    显示当前配置指向(真实库 / 假库)
///
/// 参数:
///   --data   假库根目录(含 workshop/ 与两个表文件),默认 …/WE Tool/TestData/FakeWorkshop
///   --exe    要启动的 WE_Tool.exe,默认 Debug 构建输出(自动探测;仅 launch 模式用)
/// </summary>
internal static class Program
{
    // 数据目录默认 = 本仓库 TestData/FakeWorkshop(向上找 .slnx / .git 锚点)
    private const string FakeWorkshopDirName = "TestData/FakeWorkshop";

    // WE Tool 配置根:%LOCALAPPDATA%\WE_Tool(开发 Debug 跑的是非便携模式,无 portable.ini)
    private static string AppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WE_Tool");

    private static string RealConfigPath => Path.Combine(AppDataRoot, "config.json");
    private static string BackupConfigPath => Path.Combine(AppDataRoot, "config.json.debugbak");

    private static int Main(string[] args)
    {
        // 控制台输出 UTF-8(默认 GBK 会让中文乱码;错误输出同理)
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 忽略 */ }
        try
        {
            var opts = ParseArgs(args);
            if (string.IsNullOrEmpty(opts.DataDir))
                opts.DataDir = DefaultDataDir();

            switch (opts.Mode)
            {
                case "status":
                    return RunStatus();
                case "prepare":
                    return RunPrepare(opts.DataDir);
                case "restore":
                    return RunRestore();
                default:
                    return RunLaunch(opts);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FakeWorkshopLauncher] 错误: {ex.Message}");
            // 出错时也尽量还原,避免残留假配置污染真实库。
            // ⚠ 只在"已成功备份"后才还原——备份尚未创建时 RestoreRealConfig 会把
            //   真实 config 误当"假配置"删除(实测事故:--exe 无效时真 config 被删)。
            if (File.Exists(BackupConfigPath))
            {
                try { RestoreRealConfig(); } catch { /* 忽略 */ }
            }
            return 1;
        }
    }

    /// <summary>status:显示当前配置指向真实库还是假库。</summary>
    private static int RunStatus()
    {
        if (!File.Exists(RealConfigPath))
        {
            Console.WriteLine("[FakeWorkshopLauncher] 无 config.json(WE Tool 首次运行将创建默认)。");
            return 0;
        }
        bool fake = ConfigPointsToFakeWorkshop();
        bool backupExists = File.Exists(BackupConfigPath);
        Console.WriteLine(fake
            ? "[FakeWorkshopLauncher] 当前配置指向【假库】(调试模式)。" + (backupExists ? " 备份存在,可 restore。" : " 无备份(异常态)。")
            : "[FakeWorkshopLauncher] 当前配置指向【真实库】。");
        return 0;
    }

    /// <summary>prepare:仅备份真实配置 + 写入假配置,不启动 WE Tool(供 VS F5 调试)。</summary>
    private static int RunPrepare(string dataDir)
    {
        if (!Directory.Exists(Path.Combine(dataDir, "workshop")))
        {
            Console.Error.WriteLine($"[FakeWorkshopLauncher] 假库目录不存在: {dataDir}");
            Console.Error.WriteLine($"  先用 FakeWorkshopGenerator 生成,例如:");
            Console.Error.WriteLine($"    dotnet run --project FakeWorkshopGenerator -- --count 10000 --output \"{dataDir}\"");
            return 2;
        }
        EnsureBackup();
        WriteFakeConfig(dataDir);
        Console.WriteLine("[FakeWorkshopLauncher] 配置已切换为假库(未启动 WE Tool)。");
        Console.WriteLine("  现在可在 VS 里把启动项设为 WE Tool(Unpackaged) 并 F5 调试;");
        Console.WriteLine("  调试结束后运行: FakeWorkshopLauncher restore");
        return 0;
    }

    /// <summary>restore:还原真实配置。</summary>
    private static int RunRestore()
    {
        RestoreRealConfig();
        return 0;
    }

    /// <summary>launch(默认):备份 → 写假配置 → 启动 WE Tool → 等退出后还原。</summary>
    private static int RunLaunch(Options opts)
    {
        if (!Directory.Exists(Path.Combine(opts.DataDir, "workshop")))
        {
            Console.Error.WriteLine($"[FakeWorkshopLauncher] 假库目录不存在: {opts.DataDir}");
            Console.Error.WriteLine($"  先用 FakeWorkshopGenerator 生成,例如:");
            Console.Error.WriteLine($"    dotnet run --project FakeWorkshopGenerator -- --count 10000 --output \"{opts.DataDir}\"");
            return 2;
        }

        // 探测 WE Tool exe
        if (string.IsNullOrEmpty(opts.ExePath))
            opts.ExePath = ProbeWeToolExe() ?? throw new FileNotFoundException("未找到 WE_Tool.exe,请用 --exe 指定");
        if (!File.Exists(opts.ExePath))
            throw new FileNotFoundException($"WE_Tool.exe 不存在: {opts.ExePath}");

        EnsureBackup();
        WriteFakeConfig(opts.DataDir);

        Console.WriteLine($"[FakeWorkshopLauncher] 启动 WE Tool (调试模式:假库)");
        Console.WriteLine($"  假库目录: {Path.GetFullPath(opts.DataDir)}");
        Console.WriteLine($"  配置文件: {RealConfigPath}(已备份到 {BackupConfigPath})");
        Console.WriteLine($"  WE Tool : {opts.ExePath}");
        Console.WriteLine($"  退出 WE Tool 后自动还原真实配置。");

        var psi = new ProcessStartInfo
        {
            FileName = opts.ExePath,
            WorkingDirectory = Path.GetDirectoryName(opts.ExePath) ?? ".",
            UseShellExecute = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) throw new InvalidOperationException("启动 WE Tool 失败");

        proc.WaitForExit();
        RestoreRealConfig();
        Console.WriteLine("[FakeWorkshopLauncher] WE Tool 已退出,真实配置已还原。");
        return 0;
    }

    private static Options ParseArgs(string[] args)
    {
        var opts = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "launch":
                case "prepare":
                case "restore":
                case "status":
                    opts.Mode = args[i];
                    break;
                case "--data" when i + 1 < args.Length: opts.DataDir = args[++i]; break;
                case "--exe" when i + 1 < args.Length: opts.ExePath = args[++i]; break;
                default:
                    Console.Error.WriteLine($"[警告] 未知参数: {args[i]}");
                    break;
            }
        }
        return opts;
    }

    private sealed class Options
    {
        public string Mode { get; set; } = "launch";
        public string DataDir { get; set; } = "";
        public string? ExePath { get; set; }
    }

    /// <summary>向上找仓库根(含 .git 或 .slnx),拼出默认假库目录。</summary>
    private static string DefaultDataDir()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, "WE Tool.slnx")))
                return Path.Combine(dir.FullName, FakeWorkshopDirName);
            dir = dir.Parent;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), FakeWorkshopDirName);
    }

    /// <summary>探测 WE Tool 构建输出(Debug x64 下的多个 TFM 目录取最新)。</summary>
    private static string? ProbeWeToolExe()
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot == null) return null;

        var candidates = Directory.Exists(Path.Combine(repoRoot, "WE Tool", "bin"))
            ? Directory.GetFiles(Path.Combine(repoRoot, "WE Tool", "bin"), "WE_Tool.exe", SearchOption.AllDirectories)
            : Array.Empty<string>();
        return candidates
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WE Tool.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>备份真实配置:先删旧备份(上次残留),再复制当前真实配置为备份。</summary>
    private static void EnsureBackup()
    {
        if (!Directory.Exists(AppDataRoot)) Directory.CreateDirectory(AppDataRoot);
        if (!File.Exists(RealConfigPath))
        {
            // 无真实配置:无需备份(WE Tool 会新建默认),但留个标记避免误还原
            Console.WriteLine("[FakeWorkshopLauncher] 未找到真实 config.json,首次运行将生成默认配置。");
            return;
        }
        if (File.Exists(BackupConfigPath))
        {
            Console.WriteLine($"[FakeWorkshopLauncher] 发现上次残留备份,已清理: {BackupConfigPath}");
            File.Delete(BackupConfigPath);
        }
        File.Copy(RealConfigPath, BackupConfigPath, overwrite: true);
        Console.WriteLine($"[FakeWorkshopLauncher] 真实配置已备份: {BackupConfigPath}");
    }

    /// <summary>
    /// 写入假配置:读真实配置 JSON(JsonNode 保留全部字段),仅改 Path 下三项,
    /// 序列化写回 config.json。其余所有字段(窗口位置/主题/提取设置…)原样保留。
    /// </summary>
    private static void WriteFakeConfig(string dataDir)
    {
        var workshopDir = Path.GetFullPath(Path.Combine(dataDir, "workshop"));
        var acfPath = Path.GetFullPath(Path.Combine(dataDir, "appworkshop_431960.acf"));
        var vdfPath = Path.GetFullPath(Path.Combine(dataDir, "431960_subscriptions.vdf"));

        JsonObject? root = null;
        if (File.Exists(RealConfigPath))
        {
            var text = File.ReadAllText(RealConfigPath);
            root = JsonNode.Parse(text) as JsonObject;
        }
        root ??= new JsonObject();

        var path = (root["Path"] as JsonObject) ?? new JsonObject();
        path["WorkshopPath"] = workshopDir;
        path["AcfPath"] = acfPath;
        path["VdfPath"] = vdfPath;
        root["Path"] = path;

        // 保留原始缩进风格(WriteIndented)
        var json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(RealConfigPath, json);
        Console.WriteLine($"[FakeWorkshopLauncher] 假配置已写入: {RealConfigPath}");
    }

    /// <summary>还原真实配置:备份存在则覆盖回 config.json 并删除备份;无备份则删除假配置。</summary>
    private static void RestoreRealConfig()
    {
        if (!Directory.Exists(AppDataRoot)) return;

        if (File.Exists(BackupConfigPath))
        {
            File.Copy(BackupConfigPath, RealConfigPath, overwrite: true);
            File.Delete(BackupConfigPath);
            Console.WriteLine("[FakeWorkshopLauncher] 真实配置已还原。");
            return;
        }

        // ⚠ 无备份时**绝不**删除 config.json——那可能是用户真实配置(本工具尚未成功备份过)。
        //   只删"假配置":判断依据 = 当前 config 的 WorkshopPath 指向假库目录形态(TestData\FakeWorkshop)。
        //   判断失败宁可不删,留给用户手动处理,绝不再误删真实配置。
        if (File.Exists(RealConfigPath) && ConfigPointsToFakeWorkshop())
        {
            File.Delete(RealConfigPath);
            Console.WriteLine("[FakeWorkshopLauncher] 无备份,已删除指向假库的调试配置。");
        }
        else
        {
            Console.WriteLine("[FakeWorkshopLauncher] 无备份且当前配置不指向假库,未改动 config.json(安全跳过)。");
        }
    }

    /// <summary>判断当前 config.json 的创意工坊路径是否指向假库(以路径含 FakeWorkshop 为特征)。</summary>
    private static bool ConfigPointsToFakeWorkshop()
    {
        try
        {
            if (!File.Exists(RealConfigPath)) return false;
            var root = JsonNode.Parse(File.ReadAllText(RealConfigPath)) as JsonObject;
            var workshopPath = root?["Path"]?["WorkshopPath"]?.GetValue<string>() ?? "";
            return workshopPath.Contains("FakeWorkshop", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

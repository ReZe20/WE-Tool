namespace AutoBackupService;

/// <summary>
/// AutoBackupService 入口。
///   --run    常驻:启动补齐 + FileSystemWatcher 监控 VDF + 轮询备份(schtasks 调用)
///   --once   单次:补齐后退出(调试/验证/手动用)
///   --verify 校验:输出配置与状态(WE Tool 调用判断服务可用性),exit 0=可用/1=不可用
///   (无参数)  等价 --verify
///   --data-dir &lt;path&gt;  数据根目录覆盖(便携模式):config.json 与日志都落在该目录;
///                       不传则默认 %LOCALAPPDATA%/WE_Tool(安装版/独立运行)。
/// </summary>
public static class Program
{
    /// <summary>进程内数据根目录(由 --data-dir 或默认 AppData 解析,只算一次)。</summary>
    public static string DataRoot { get; private set; } = "";

    public static int Main(string[] args)
    {
        // 解析 --data-dir(便携模式由 WE Tool 主程序传入);其余参数原样保留给模式分发
        DataRoot = ParseDataDir(args) ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WE_Tool");

        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "--verify";

        var config = ServiceConfig.Load();
        if (config == null)
        {
            if (mode != "--run") return 1; // --run 常驻无配置时也打印后退出
            Log.Write("配置加载失败,服务退出");
            return 1;
        }

        switch (mode)
        {
            case "--run":
                return Run(config);
            case "--once":
                return Once(config);
            case "--verify":
                return Verify(config);
            default:
                Console.WriteLine("未知参数。用法: AutoBackupService [--run|--once|--verify] [--data-dir <path>]");
                return Verify(config);
        }
    }

    /// <summary>从参数中提取 --data-dir 的值;未提供返回 null。</summary>
    private static string? ParseDataDir(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--data-dir", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static int Run(ServiceConfig config)
    {
        if (!config.IsAutoBackupActive())
        {
            Log.Write("自动备份未启用(Enabled/ServiceEnabled/路径任一缺失),服务退出");
            return 1;
        }

        using var runner = new BackupRunner(config);
        runner.BackupAllMissing();
        runner.StartWatch();

        Log.Write("AutoBackupService 常驻运行中,按 Ctrl+C 退出");
        var exitEvent = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; exitEvent.Set(); };
        exitEvent.Wait();

        Log.Write("服务停止");
        return 0;
    }

    private static int Once(ServiceConfig config)
    {
        if (!config.IsAutoBackupActive())
        {
            Log.Write("自动备份未启用(Enabled/ServiceEnabled/路径任一缺失)");
            return 1;
        }
        using var runner = new BackupRunner(config);
        int n = runner.BackupAllMissing();
        Log.Write($"补齐完成,新增备份 {n} 个");
        return 0;
    }

    private static int Verify(ServiceConfig config)
    {
        bool active = config.IsAutoBackupActive();
        var auto = config.AutoBackup;

        Console.WriteLine($"Enabled={auto?.Enabled ?? false}");
        Console.WriteLine($"ServiceEnabled={auto?.ServiceEnabled ?? false}");
        Console.WriteLine($"VdfPath={config.Path?.VdfPath ?? "(空)"}");
        Console.WriteLine($"WorkshopPath={config.Path?.WorkshopPath ?? "(空)"}");
        Console.WriteLine($"筛选: Scene={auto?.TypeScene} Video={auto?.TypeVideo} Web={auto?.TypeWeb} " +
                          $"App={auto?.TypeApplication} Preset={auto?.TypePreset} Unknown={auto?.TypeUnknown}");
        Console.WriteLine($"      G={auto?.RatingG} Pg={auto?.RatingPg} R={auto?.RatingR}");
        Console.WriteLine(active ? "STATUS=ACTIVE" : "STATUS=INACTIVE");

        return active ? 0 : 1;
    }
}

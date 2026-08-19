namespace AutoBackupService;

/// <summary>
/// AutoBackupService 入口。
///   --run    常驻:启动补齐 + FileSystemWatcher 监控 VDF + 轮询备份(schtasks 调用)
///   --once   单次:补齐后退出(调试/验证/手动用)
///   --verify 校验:输出配置与状态(WE Tool 调用判断服务可用性),exit 0=可用/1=不可用
///   (无参数)  等价 --verify
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
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
                Console.WriteLine("未知参数。用法: AutoBackupService [--run|--once|--verify]");
                return Verify(config);
        }
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

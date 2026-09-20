using System.Text;

namespace TestBackupContract;

/// <summary>
/// 契约差分器。把 AutoBackupService 的进程边界行为(退出码 / stdout / 日志文本 / 磁盘结果)
/// 归一化成文本基线存进 goldens/,再用同一套场景打另一份实现。
///
///   --record [exe]   用该 exe 生成基线(当前基线来自 C# NativeAOT 产物)
///   --check  [exe]   用该 exe 跑同一套场景,与基线逐字节比
///   --exe     exe    显式指定被测 exe(相对路径先按当前目录解析,再按仓库根解析)
///   --only   A01,B04 名字前缀过滤    --keep  保留临时场景目录    --wait 结束后按键(F5 用)
///   --list           只列场景名
///
/// exe 省略时自动取 AutoBackupService/bin/{Release,Debug}/AutoBackupService.exe(存在者优先 Release)。
/// 不带任何参数等价于 --check --wait,所以在 VS 里按 F5 就能跑。
/// 每个场景一个独立临时根目录;record 与 check 的根目录不同,所以归一化把根路径抹成 {ROOT}。
/// </summary>
internal static class Program
{
    private const string TempPrefix = "WEToolBackupContract-";

    private static int Main(string[] args)
    {
        // 本工具自己的诊断信息含中文:不设的话重定向时按系统 ANSI(简中=GBK)落盘,没法直接读。
        // 用不带 BOM 的 UTF8,免得重定向时先吐一个 FE FF 干扰比对/日志。
        try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch { /* 无控制台 */ }

        string? exe = null;
        string mode = args.Length == 0 ? "--check" : "";
        bool keep = false, wait = args.Length == 0;
        var only = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--record": case "--check":
                    mode = args[i];
                    exe = Next(args, ref i);   // 允许省略,后面按默认路径找
                    break;
                case "--exe": exe = Next(args, ref i); break;
                case "--only": only.AddRange(Next(args, ref i)?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? []); break;
                case "--keep": keep = true; break;
                case "--wait": wait = true; break;
                case "--list":
                    foreach (var s in Scenario.All) Console.WriteLine(s.Name);
                    return 0;
                default:
                    Console.Error.WriteLine($"未知参数: {args[i]}");
                    return Usage();
            }
        }

        if (mode.Length == 0) return Usage();
        string? resolved = ResolveExe(exe);
        if (resolved is null)
        {
            Console.Error.WriteLine(exe is null
                ? "没找到被测 exe:AutoBackupService/bin/Release|Debug/AutoBackupService.exe 都不存在。"
                : $"exe 不存在: {exe}");
            Console.Error.WriteLine("构建服务任选其一:");
            Console.Error.WriteLine("  dotnet build \"WE Tool/WE Tool.csproj\" -c Release -p:Platform=x64");
            Console.Error.WriteLine("    (CopyAutoBackupService 目标会用 VS 的 MSBuild 编 C++ 工程)");
            Console.Error.WriteLine(@"  MSBuild.exe AutoBackupService\AutoBackupService.vcxproj -p:Configuration=Release -p:Platform=x64");
            return Wait(2, wait);
        }
        exe = resolved;
        Console.WriteLine($"被测 exe: {exe} ({new FileInfo(exe).Length} 字节)");
        Console.WriteLine($"模式: {(mode == "--record" ? "录制基线" : "比对基线")}{(only.Count > 0 ? "  仅 " + string.Join(",", only) : "")}");
        Console.WriteLine();

        var goldenDir = Path.Combine(FindRepoRoot(), "TestBackupContract", "goldens");
        Directory.CreateDirectory(goldenDir);
        bool record = mode == "--record";
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        int pass = 0, bad = 0, noBase = 0;
        var failures = new List<string>();

        foreach (var s in Scenario.All)
        {
            if (only.Count > 0 && !only.Any(o => s.Name.StartsWith(o, StringComparison.OrdinalIgnoreCase))) continue;

            string root = Path.Combine(Path.GetTempPath(), TempPrefix + s.Name, record ? "rec" : "chk");
            TryDelete(root);
            var fx = new Fx(root);
            s.Build(fx);

            Observe.Result? last = null;
            var exits = new List<int>();
            if (s.Resident)
            {
                last = Observe.RunResident(s, exe, root, fx);
                exits.Add(last.ExitCode);
            }
            else
            {
                for (int r = 0; r < s.Runs; r++)
                {
                    last = Observe.Run(exe, s.Args, root);
                    exits.Add(last.ExitCode);
                }
            }
            string actual = NormEol(Observe.Render(s, root, last!, exits));

            string golden = Path.Combine(goldenDir, s.Name + ".txt");
            if (record)
            {
                File.WriteAllText(golden, actual);
                Console.WriteLine($"[REC] {s.Name}");
                pass++;
            }
            else if (!File.Exists(golden))
            {
                Console.WriteLine($"[---] {s.Name}  缺基线(先 --record)");
                noBase++;
            }
            else
            {
                // 仓库是 core.autocrlf=true + * text=auto,签出时 .txt 的 LF 会变 CRLF;
                // 基线文本本身混用 AppendLine(CRLF) 与手拼 \n,所以比对前两边都归一成 LF,
                // 否则换台机器/重新签出就会全线假 DIFF。
                string expected = NormEol(File.ReadAllText(golden));
                if (expected == actual)
                {
                    Console.WriteLine($"[OK ] {s.Name}");
                    pass++;
                }
                else
                {
                    Console.WriteLine($"[DIFF] {s.Name}");
                    Console.Write(DescribeDiff(expected, actual));
                    failures.Add(s.Name);
                    bad++;
                }
            }

            if (!keep) TryDelete(root);
        }

        Console.WriteLine();
        Console.WriteLine(record
            ? $"基线已写入 {goldenDir}: {pass} 条"
            : $"一致 {pass} / 不一致 {bad} / 缺基线 {noBase}");
        if (failures.Count > 0) Console.WriteLine("不一致: " + string.Join(", ", failures));
        return Wait(record ? (pass > 0 ? 0 : 1) : (bad == 0 && noBase == 0 ? 0 : 1), wait);
    }

    /// <summary>
    /// 解析被测 exe:显式给出的先按当前目录、再按仓库根解析(相对路径在 VS 里 cwd=项目目录,
    /// 在 shell 里 cwd 可能是仓库根,两种都要能用);省略时按 Release、Debug 顺序找现成产物。
    /// </summary>
    private static string? ResolveExe(string? given)
    {
        if (given is not null)
        {
            string trimmed = given.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(trimmed)) return File.Exists(trimmed) ? trimmed : null;
            string local = Path.GetFullPath(trimmed);
            if (File.Exists(local)) return local;
            string fromRoot = Path.Combine(FindRepoRoot(), trimmed);
            return File.Exists(fromRoot) ? fromRoot : null;
        }
        string root = FindRepoRoot();
        foreach (string rel in new[] { "AutoBackupService/bin/Release/AutoBackupService.exe", "AutoBackupService/bin/Debug/AutoBackupService.exe" })
        {
            string p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>--wait:VS 里 F5 时控制台会在进程退出瞬间关掉,这里挡一下;重定向输入时不挡,免得卡脚本。</summary>
    private static int Wait(int code, bool wait)
    {
        if (wait && !Console.IsInputRedirected)
        {
            Console.WriteLine();
            Console.WriteLine("按任意键退出…");
            try { Console.ReadKey(true); } catch (InvalidOperationException) { }
        }
        return code;
    }

    /// <summary>取选项值;下一个记号本身是选项(以 -- 开头)时不当作值吞掉。</summary>
    private static string? Next(string[] args, ref int i)
        => i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;

    private static string NormEol(string s) => s.Replace("\r\n", "\n");

    private static int Usage()
    {
        Console.Error.WriteLine("用法: TestBackupContract [--record|--check] [AutoBackupService.exe] [--only A01,B04] [--keep] [--wait]");
        Console.Error.WriteLine("      exe 省略时自动取 AutoBackupService/bin/{Release,Debug}/AutoBackupService.exe;不带参数等价 --check --wait");
        return 2;
    }

    /// <summary>
    /// 找仓库根(以 *.slnx + AutoBackupService/ 为标记),goldens 必须落在源码树里。
    /// 先从当前目录上溯,再试自身 dll 所在目录 —— 后者覆盖"从仓库外 dotnet 某个 dll"和
    /// "VS 把 workingDirectory 设成别处"两种情况,否则会抛一句没人看得懂的异常。
    /// </summary>
    private static string FindRepoRoot()
    {
        foreach (string seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(seed); dir != null; dir = dir.Parent)
            {
                if (dir.EnumerateFiles("*.slnx").Any() && Directory.Exists(Path.Combine(dir.FullName, "AutoBackupService")))
                    return dir.FullName;
            }
        }
        throw new InvalidOperationException("找不到仓库根(需包含 WE Tool.slnx 与 AutoBackupService/)，请在仓库内运行。");
    }

    private static void TryDelete(string root)
    {
        // 常驻场景刚被杀掉时,内核可能还挂着目录句柄;重试几次比留一堆垃圾目录好
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                Directory.Delete(@"\\?\" + root, recursive: true);
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 7) { Console.Error.WriteLine($"[清理失败] {root}: {ex.Message}"); return; }
                Thread.Sleep(250);
            }
        }
    }

    /// <summary>只报第一处不同的上下文,足够定位;完整文本去 goldens 和 --keep 的目录里看。</summary>
    private static string DescribeDiff(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var sb = new StringBuilder();
        int i = 0;
        while (i < e.Length && i < a.Length && e[i] == a[i]) i++;
        sb.Append("       基线   : ").AppendLine(i < e.Length ? e[i].TrimEnd() : "<文件结束>").AppendLine();
        sb.Append("       实际   : ").AppendLine(i < a.Length ? a[i].TrimEnd() : "<文件结束>").AppendLine();
        sb.Append($"       首个差异在第 {i + 1} 行 (基线 {e.Length} 行 / 实际 {a.Length} 行)");
        if (i > 0)
            sb.Append("\n       上一行一致: ").Append(e[i - 1].TrimEnd());
        sb.AppendLine();
        return sb.ToString();
    }
}

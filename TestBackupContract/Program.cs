using System.Text;

namespace TestBackupContract;

/// <summary>
/// 契约差分器。把 AutoBackupService 的进程边界行为(退出码 / stdout / 日志文本 / 磁盘结果)
/// 归一化成文本基线存进 goldens/,再用同一套场景打另一份实现。
///
///   --record &lt;exe&gt;   用该 exe 生成基线(当前基线来自 C# NativeAOT 产物)
///   --check  &lt;exe&gt;   用该 exe 跑同一套场景,与基线逐字节比
///   --only   A01,B04 名字前缀过滤    --keep  保留临时场景目录
///
/// 每个场景一个独立临时根目录;record 与 check 的根目录不同,所以归一化把根路径抹成 {ROOT}。
/// </summary>
internal static class Program
{
    private const string TempPrefix = "WEToolBackupContract-";

    private static int Main(string[] args)
    {
        string? exe = null;
        string mode = "";
        bool keep = false;
        var only = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--record": case "--check": mode = args[i]; exe = Next(args, ref i); break;
                case "--exe": exe = Next(args, ref i); break;
                case "--only": only.AddRange(Next(args, ref i)?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? []); break;
                case "--keep": keep = true; break;
                case "--list":
                    foreach (var s in Scenario.All) Console.WriteLine(s.Name);
                    return 0;
                default:
                    Console.Error.WriteLine($"未知参数: {args[i]}");
                    return Usage();
            }
        }

        if (mode.Length == 0 || exe is null) return Usage();
        exe = Path.GetFullPath(exe);
        if (!File.Exists(exe)) { Console.Error.WriteLine($"exe 不存在: {exe}"); return 2; }

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

            Observe.Result last = default!;
            var exits = new List<int>();
            for (int r = 0; r < s.Runs; r++)
            {
                last = Observe.Run(exe, s.Args, root);
                exits.Add(last.ExitCode);
            }
            string actual = NormEol(Observe.Render(s.Name, s.Args, last, root, exits));

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
        return record ? (pass > 0 ? 0 : 1) : (bad == 0 && noBase == 0 ? 0 : 1);
    }

    /// <summary>取选项值;下一个记号本身是选项(以 -- 开头)时不当作值吞掉。</summary>
    private static string? Next(string[] args, ref int i)
        => i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;

    private static string NormEol(string s) => s.Replace("\r\n", "\n");

    private static int Usage()
    {
        Console.Error.WriteLine("用法: TestBackupContract --record|--check <AutoBackupService.exe> [--only A01,B04] [--keep]");
        return 2;
    }

    /// <summary>从当前目录上溯找仓库根(以 WE Tool.slnx 为标记),goldens 必须落在源码树里。</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (dir.EnumerateFiles("*.slnx").Any() && Directory.Exists(Path.Combine(dir.FullName, "AutoBackupService")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根(需包含 WE Tool.slnx 与 AutoBackupService/)，请在仓库内运行。");
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(@"\\?\" + root, recursive: true);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[清理失败] {root}: {ex.Message}"); }
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

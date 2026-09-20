using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TestBackupContract;

/// <summary>
/// 观测器:跑一次目标服务,收集「退出码 + stdout + 日志文件 + 磁盘结果快照」,
/// 并归一化成跨运行可比较的文本(去时间戳、去根路径、文件 ID 换成首次出现序)。
/// 磁盘快照里的链接数与文件 ID 分组用于证明「备份确实是同一份物理数据」而非复制。
/// </summary>
internal static class Observe
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr, string Log, string Tree, string Extra = "");

    // stdout 两侧都固定 UTF-8(无 BOM):C++ 直接写字节,C# 绕开 Console 编码层写标准输出流。
    // 因此这里按 UTF-8 解码即可,不再与本机代码页牵扯。

    /// <summary>
    /// 常驻场景:起进程 → 按时刻做 During 动作 → 轮询日志直到 Expect 全部出现 →
    /// 静置 SettleAfterMs → 杀掉。日志是这里唯一的观测面(进程不往 stdout 写东西)。
    /// </summary>
    public static Result RunResident(Scenario s, string exe, string root, Fx fx)
    {
        string args = s.Args.Replace("{ROOT}", root);
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = root,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 " + exe);
        string logPath = Path.Combine(root, "data", "AutoBackupService.log");

        var sw = Stopwatch.StartNew();
        var met = new bool[s.Expect.Length];
        bool duringDone = s.During is null;
        string log = "";
        while (true)
        {
            if (!duringDone && sw.ElapsedMilliseconds >= s.DuringDelayMs)
            {
                s.During!(fx);
                duringDone = true;
            }
            log = Tail(logPath);
            for (int i = 0; i < s.Expect.Length; i++)
                if (!met[i] && log.Contains(s.Expect[i], StringComparison.Ordinal)) met[i] = true;
            bool all = true;
            for (int i = 0; i < met.Length; i++) all &= met[i];
            if (all || sw.ElapsedMilliseconds > s.ExpectTimeoutMs || p.HasExited) break;
            Thread.Sleep(100);
        }
        if (!duringDone) s.During!(fx);

        for (int i = 0; i < met.Length; i++)
            if (!met[i] && log.Contains(s.Expect[i], StringComparison.Ordinal)) met[i] = true;

        Thread.Sleep(s.SettleAfterMs);                     // 静置期:空转/忙轮询只在这里露出来
        log = Tail(logPath);

        bool exited = p.WaitForExit(2000);
        int exit = -1;
        string stdout = "", stderr = "";
        if (exited) exit = p.ExitCode;
        else
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } catch { /* 已退出 */ }
        }
        try { stdout = p.StandardOutput.ReadToEnd(); } catch { /* 管道随进程一起没了 */ }
        try { stderr = p.StandardError.ReadToEnd(); } catch { /* 同上 */ }
        p.Dispose();

        var extra = new StringBuilder();
        extra.Append("expect:\n");
        for (int i = 0; i < s.Expect.Length; i++)
            extra.Append("  ").Append(met[i] ? "MET   " : "UNMET ").AppendLine(s.Expect[i]);
        extra.Append("killed: ").Append(exited ? "no(自己退了)" : "yes").Append('\n');

        return new Result(exit, stdout, stderr, log, Tree(root), extra.ToString());
    }

    private static string Tail(string path)
    {
        try
        {
            // 服务可能正握着写句柄:用 ReadWrite 共享打开,失败就给空(下一轮再读)
            using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
            return sr.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }


    public static Result Run(string exe, string argsWithRootToken, string root)
    {
        string args = argsWithRootToken.Replace("{ROOT}", root);
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = root,
            // 显式钉 UTF-8:不设时 .NET 会跟着父进程的 Console.OutputEncoding 走,
            // 无控制台的场景下恰好是 UTF-8,但换机器/换代码页就会解错中文 stdout
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        var sbOut = new StringBuilder();
        var sbErr = new StringBuilder();
        int exit;
        using (var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 " + exe))
        {
            p.OutputDataReceived += (_, e) => { if (e.Data != null) sbOut.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) sbErr.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(120_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                exit = -1;
                sbErr.AppendLine("<<TIMEOUT 120s>>");
            }
            else
            {
                p.WaitForExit(); // 冲刷异步读取
                exit = p.ExitCode;
            }
        }

        string logPath = Path.Combine(root, "data", "AutoBackupService.log");
        string log = File.Exists(logPath) ? File.ReadAllText(logPath) : "<<NO LOG FILE>>";

        return new Result(exit, sbOut.ToString(), sbErr.ToString(), log, Tree(root));
    }

    /// <summary>渲染为 golden 文件内容。归一化在此集中做,新增场景无需自己处理。</summary>
    public static string Render(Scenario s, string root, Result r, IReadOnlyList<int> exits)
    {
        var sb = new StringBuilder();
        sb.Append("### ").AppendLine(s.Name);
        sb.Append(s.Resident
            ? "mode: resident"
            : $"mode: batch  runs: {exits.Count}  exits: {string.Join(",", exits)}").Append('\n');
        sb.Append("args: ").AppendLine(Norm(s.Args, root));
        if (r.Extra.Length > 0) sb.Append(NormBlock(r.Extra, root));
        sb.Append("stdout:\n").Append(NormBlock(r.StdOut, root));
        // 常驻场景的回调次数受时序影响(FileSystemWatcher 可能一次写触发多行),
        // 折叠连续重复行后仍然保留「有没有出现」与「先后次序」这两个有效信息
        sb.Append("log:\n").Append(NormBlock(s.Resident ? Collapse(r.Log) : r.Log, root));
        sb.Append("tree:\n").Append(NormBlock(r.Tree, root));
        return sb.ToString();
    }

    private static string Collapse(string log)
    {
        var lines = log.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        string prev = "\0";
        foreach (var l in lines)
        {
            if (l == prev && l.Length > 0) continue;
            sb.Append(l).Append('\n');
            prev = l;
        }
        return sb.ToString();
    }

    private static string NormBlock(string text, string root)
    {
        if (string.IsNullOrEmpty(text)) return "  <<EMPTY>>\n";
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            sb.Append("  ").AppendLine(Norm(line, root));
        }
        return sb.ToString();
    }

    private static string Norm(string text, string root)
    {
        // 时间戳统一成 [TS](含 .backup_ok 里的 created= 行);根路径统一成 {ROOT}
        text = Timestamp.Replace(text, "[TS]");
        foreach (var v in new[] { @"\\?\" + root, root, root.Replace('\\', '/') })
            text = text.Replace(v, "{ROOT}", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static readonly System.Text.RegularExpressions.Regex Timestamp = new(
        @"\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(\.\d+)?");

    // ---- 磁盘快照 ----

    private static string Tree(string root)
    {
        var entries = new List<(string Rel, bool IsDir, int Links, long Size, string FileId)>();
        Walk(root, "", entries);
        entries.Sort((a, b) => string.CompareOrdinal(a.Rel, b.Rel));

        // 文件 ID → 首次出现序,消除不同临时目录间的卷序列号差异
        var groupOrder = new Dictionary<string, int>();
        var sb = new StringBuilder();
        foreach (var (rel, isDir, links, size, fileId) in entries)
        {
            if (isDir) { sb.Append(rel).Append("/\n"); continue; }
            int g = 0;
            if (fileId.Length > 0)
            {
                if (!groupOrder.TryGetValue(fileId, out g))
                {
                    g = groupOrder.Count + 1;
                    groupOrder[fileId] = g;
                }
            }
            // links=2 且源/备份同 id=Gn ⇒ 确实共享物理数据;size 让「不覆盖外来文件」可验证
            sb.Append(rel).Append("  size=").Append(size).Append("  links=").Append(links);
            if (g > 0) sb.Append("  id=G").Append(g);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void Walk(string abs, string rel, List<(string, bool, int, long, string)> into)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(Extended(abs)))
        {
            string name = Path.GetFileName(path);
            string childRel = rel.Length == 0 ? name : rel + "/" + name;
            bool isDir = File.GetAttributes(Extended(path)).HasFlag(FileAttributes.Directory);
            var (links, size, id) = isDir ? (0, 0L, "") : HardLinkInfo(path);
            into.Add((childRel, isDir, links, size, id));
            if (isDir) Walk(path, childRel, into);
        }
    }

    /// <summary>\\?\ 前缀:快照必须能走完长路径场景,否则测不到服务真正要处理的深度。</summary>
    private static string Extended(string path)
        => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
         : (Path.IsPathRooted(path) ? @"\\?\" + path : path);

    private static (int Links, long Size, string FileId) HardLinkInfo(string path)
    {
        var h = CreateFileW(path, 0, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h == INVALID) return (0, 0, "");
        try
        {
            if (!ByHandleInfo(h, out var i)) return (0, 0, "");
            long size = ((long)i.SizeHigh << 32) | i.SizeLow;
            return ((int)i.NumberOfLinks, size, $"{i.VolumeSerial}:{i.FileIndexHigh}:{i.FileIndexLow}");
        }
        finally { CloseHandle(h); }
    }

    // ---- Win32 ----

    private const uint FILE_SHARE_READ_WRITE = 0x3;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private static readonly IntPtr INVALID = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint Attributes;
        public FILETIME Creation;
        public FILETIME LastAccess;
        public FILETIME LastWrite;
        public uint VolumeSerial;
        public uint SizeHigh;
        public uint SizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    private static extern bool ByHandleInfo(IntPtr h, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
}

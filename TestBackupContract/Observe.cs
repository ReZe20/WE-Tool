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
    public sealed record Result(int ExitCode, string StdOut, string StdErr, string Log, string Tree);

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
    public static string Render(string name, string args, Result r, string root, IReadOnlyList<int> exits)
    {
        var sb = new StringBuilder();
        sb.Append("### ").AppendLine(name);
        sb.Append("runs: ").Append(exits.Count).Append("  exits: ").AppendLine(string.Join(",", exits));
        sb.Append("args: ").AppendLine(Norm(args, root));
        sb.Append("stdout:\n").Append(NormBlock(r.StdOut, root));
        sb.Append("log:\n").Append(NormBlock(r.Log, root));
        sb.Append("tree:\n").Append(NormBlock(r.Tree, root));
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

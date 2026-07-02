using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Models;

namespace WE_Tool.Service;

public class RepkgCliService
{
    private readonly string _repkgDir;
    private readonly ConcurrentDictionary<int, Process> _runningProcesses = new();

    public RepkgCliService()
    {
        _repkgDir = Path.Combine(AppContext.BaseDirectory, "repkg");
    }

    public void Pause()
    {
        foreach (var kvp in _runningProcesses)
        {
            var process = kvp.Value;
            if (process != null && !process.HasExited)
            {
                try { NtSuspendProcess(process.Handle); }
                catch (Exception ex) { Log.Warning(ex, "[repkg] 暂停进程失败: {Pid}", kvp.Key); }
            }
        }
    }

    public void Resume()
    {
        foreach (var kvp in _runningProcesses)
        {
            var process = kvp.Value;
            if (process != null && !process.HasExited)
            {
                try { NtResumeProcess(process.Handle); }
                catch (Exception ex) { Log.Warning(ex, "[repkg] 恢复进程失败: {Pid}", kvp.Key); }
            }
        }
    }

    public void Stop()
    {
        foreach (var kvp in _runningProcesses)
        {
            var process = kvp.Value;
            if (process != null && !process.HasExited)
            {
                try { process.Kill(); }
                catch (Exception ex) { Log.Warning(ex, "[repkg] 终止进程失败: {Pid}", kvp.Key); }
            }
            process?.Dispose();
        }
        _runningProcesses.Clear();
    }

    public async Task ExtractWallpapersAsync(
        IReadOnlyList<WallpaperItem> wallpapers,
        string outputRoot,
        ExtractSettings settings,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        int total = wallpapers.Count;
        if (total == 0) return;

        int done = 0;

        void ReportProgress(string msg) => onProgress?.Invoke(msg);

        var po = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        try
        {
            await Parallel.ForEachAsync(wallpapers, po, async (wallpaper, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(wallpaper.FolderPath)) { Interlocked.Increment(ref done); return; }

                var dir = new DirectoryInfo(wallpaper.FolderPath);
                if (!dir.Exists) { Interlocked.Increment(ref done); return; }

                var pkgFiles = dir.EnumerateFiles("*.pkg", SearchOption.AllDirectories).ToArray();
                var wallpaperOutput = GetOutputPath(outputRoot, wallpaper, settings);
                var name = wallpaper.Title ?? wallpaper.WorkshopID ?? dir.Name;
                var n = Interlocked.Increment(ref done);

                void ItemProgress(string action, double pct)
                    => ReportProgress($"{name}|{action}|{pct}");

                ItemProgress("开始", 0);

                if (pkgFiles.Length > 0)
                {
                    var args = BuildArgs(wallpaper.FolderPath, wallpaperOutput, settings);
                    await RunRepkgAsync(name, args, pct => ItemProgress("解析PKG", pct), token);
                }
                else
                {
                    Directory.CreateDirectory(wallpaperOutput);
                    CopyAllFiles(dir, wallpaperOutput, settings);
                }

                ItemProgress("完成", 100);
            });
        }
        catch (OperationCanceledException) { }

        if (!ct.IsCancellationRequested)
            onProgress?.Invoke($"提取完成，共 {total} 个壁纸");
    }

    private static string BuildArgs(string input, string output, ExtractSettings settings)
    {
        var sb = new StringBuilder();
        sb.Append("extract \""); sb.Append(input); sb.Append("\" ");
        sb.Append("-o \""); sb.Append(output); sb.Append("\" ");
        if (settings.IgnoreExtension && !string.IsNullOrEmpty(settings.IgnoreExtensionList))
            sb.Append("-i ").Append(settings.IgnoreExtensionList).Append(' ');
        if (settings.OnlyExtension && !string.IsNullOrEmpty(settings.OnlyExtensionList))
            sb.Append("-e ").Append(settings.OnlyExtensionList).Append(' ');
        if (settings.OneFolder) sb.Append("-s ");
        if (settings.OutProjectJSON) sb.Append("-c ");
        if (settings.UseProjectName) sb.Append("-n ");
        if (settings.TexExportMode == 0) sb.Append("--no-tex-convert ");
        if (settings.CoverAllFiles) sb.Append("--overwrite ");
        sb.Append("-r"); // recursive
        return sb.ToString();
    }

    private async Task RunRepkgAsync(string wallpaperName, string args, Action<double> progressCb, CancellationToken ct)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(_repkgDir, "RePKG_Re.exe"),
                Arguments = args,
                WorkingDirectory = _repkgDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data) && e.Data.StartsWith("{"))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.Data);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("pos", out var pos) && root.TryGetProperty("total", out var total))
                        progressCb(Math.Round((double)pos.GetInt32() / total.GetInt32() * 100, 1));
                }
                catch { }
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Log.Warning("[repkg] {Msg}", e.Data);
        };

        process.Start();
        var pid = process.Id;
        _runningProcesses[pid] = process;
        JobObjectManager.AddProcess(process.Handle);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        process.Exited += (_, _) =>
        {
            _runningProcesses.TryRemove(pid, out _);
            process.Dispose();
        };

        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            _runningProcesses.TryRemove(pid, out _);
            if (!process.HasExited) process.Kill();
            process.Dispose();
            throw;
        }

        // Normal exit: Exited event handles cleanup, but if it didn't fire yet:
        if (_runningProcesses.TryRemove(pid, out _))
            process.Dispose();
    }

    private static string GetOutputPath(string outputRoot, WallpaperItem wallpaper, ExtractSettings settings)
    {
        if (settings.OneFolder) return outputRoot;
        if (settings.UseProjectName && !string.IsNullOrEmpty(wallpaper.Title))
            return Path.Combine(outputRoot, GetSafeName(wallpaper.Title));
        var sub = !string.IsNullOrEmpty(wallpaper.WorkshopID)
            ? wallpaper.WorkshopID
            : new DirectoryInfo(wallpaper.FolderPath!).Name;
        return Path.Combine(outputRoot, sub);
    }

    private static string GetSafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new StringBuilder(name);
        foreach (var c in new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' })
            safe.Replace(c, '_');
        for (int i = 0; i < safe.Length; i++)
            if (invalid.Contains(safe[i])) safe[i] = '_';
        return safe.ToString().Trim();
    }

    private static void CopyAllFiles(DirectoryInfo sourceDir, string outputDir, ExtractSettings settings)
    {
        foreach (var file in sourceDir.EnumerateFiles())
        {
            var relativePath = file.FullName.Substring(sourceDir.FullName.Length).TrimStart('\\', '/');
            var destPath = Path.Combine(outputDir, relativePath);
            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
            if (!settings.CoverAllFiles && File.Exists(destPath)) continue;
            try { File.Copy(file.FullName, destPath, true); }
            catch (Exception ex) { Log.Error(ex, "拷贝文件失败: {File}", file.FullName); }
        }
        foreach (var subDir in sourceDir.EnumerateDirectories())
            CopyAllFiles(subDir, Path.Combine(outputDir, subDir.Name), settings);
    }

    #region Win32 Process Suspend/Resume

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtResumeProcess(IntPtr processHandle);

    #endregion
}

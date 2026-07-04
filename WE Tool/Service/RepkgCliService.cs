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

        int maxDop = settings.MaxConcurrentExtractions > 0
            ? settings.MaxConcurrentExtractions
            : Environment.ProcessorCount;

        var po = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = maxDop
        };

        Log.Information("[repkg] 开始提取: {Count} 个壁纸, 并发数={Dop}", total, maxDop);

        try
        {
            await Parallel.ForEachAsync(wallpapers, po, async (wallpaper, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(wallpaper.FolderPath)) { Interlocked.Increment(ref done); return; }

                var dir = new DirectoryInfo(wallpaper.FolderPath);
                if (!dir.Exists) { Interlocked.Increment(ref done); return; }

                var wallpaperOutput = GetOutputPath(outputRoot, wallpaper, settings);
                var name = wallpaper.Title ?? wallpaper.WorkshopID ?? dir.Name;
                var n = Interlocked.Increment(ref done);

                void ItemProgress(string action, double pct)
                    => ReportProgress($"{name}|{action}|{pct}");

                // 跳过已提取（阶段3）
                if (settings.SkipExistingOutput && Directory.Exists(wallpaperOutput))
                {
                    if (Directory.EnumerateFileSystemEntries(wallpaperOutput).Any())
                    {
                        ItemProgress("跳过(已提取)", 100);
                        return;
                    }
                }

                ItemProgress("开始", 0);

                try
                {
                    var pkgFiles = dir.EnumerateFiles("*.pkg", SearchOption.AllDirectories).ToArray();

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

                    // 子文件夹模式 + 打乱目录结构：将所有文件移至壁纸子文件夹根目录
                    if (!settings.OneFolder && settings.KeepSubfolderStructure && Directory.Exists(wallpaperOutput))
                    {
                        foreach (var subDir in Directory.EnumerateDirectories(wallpaperOutput))
                        {
                            foreach (var f in Directory.EnumerateFiles(subDir, "*", SearchOption.AllDirectories))
                            {
                                var dest = Path.Combine(wallpaperOutput, Path.GetFileName(f));
                                int seq = 2;
                                while (File.Exists(dest))
                                    dest = Path.Combine(wallpaperOutput, $"{seq}_{Path.GetFileName(f)}");
                                File.Move(f, dest);
                            }
                        }
                        foreach (var subDir in Directory.EnumerateDirectories(wallpaperOutput))
                            Directory.Delete(subDir, true);
                    }

                    // 平铺模式 + 按壁纸名命名文件：将文件重命名为 壁纸名_原文件名
                    if (settings.OneFolder && settings.FlatFileNamingMode == 1 && !string.IsNullOrEmpty(wallpaper.Title))
                    {
                        var safeTitle = GetSafeName(wallpaper.Title);
                        foreach (var f in Directory.EnumerateFiles(wallpaperOutput))
                        {
                            var fi = new FileInfo(f);
                            var newName = $"{safeTitle}_{fi.Name}";
                            var dest = Path.Combine(wallpaperOutput, newName);
                            int seq = 2;
                            while (File.Exists(dest))
                                dest = Path.Combine(wallpaperOutput, $"{safeTitle}_{seq++}_{fi.Name}");
                            File.Move(f, dest);
                        }
                    }

                    ItemProgress("完成", 100);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[repkg] 提取失败: {Name}", name);
                    ItemProgress("失败", 100);
                }
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
        if (settings.LazyLoad) sb.Append("--lazy ");
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

        long _lastProgressTick = 0;
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data) && e.Data.StartsWith("{"))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.Data);
                    var root = doc.RootElement;

                    // Enhanced progress (Phase 5): wallpaper-level start
                    if (root.TryGetProperty("type", out var typeProp))
                    {
                        var type = typeProp.GetString();
                        if (type == "wallpaper" && root.TryGetProperty("total_entries", out var totalEnt))
                        {
                            Log.Information("[repkg] 开始解析壁纸: {Name}, 共 {Total} 个条目",
                                wallpaperName, totalEnt.GetInt32());
                        }
                        else if (type == "entry" && root.TryGetProperty("entry", out var entryProp))
                        {
                            Log.Information("[repkg] 正在转换: {Entry} ({Pos}/{Total})",
                                entryProp.GetString(),
                                root.TryGetProperty("pos", out var p) ? p.GetInt32() : 0,
                                root.TryGetProperty("total", out var t) ? t.GetInt32() : 0);
                        }
                    }
                    else if (root.TryGetProperty("pos", out var pos) && root.TryGetProperty("total", out var total))
                    {
                        // 节流：最多每 30ms 触发一次 progress 回调
                        var now = Environment.TickCount64;
                        if (now - _lastProgressTick < 30) return;
                        _lastProgressTick = now;

                        progressCb(Math.Round((double)pos.GetInt32() / total.GetInt32() * 100, 1));
                    }
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

        // 设置子进程优先级（阶段1）
        try
        {
            if (_processPriorityLevel >= 0)
                process.PriorityClass = _processPriorityLevel;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[repkg] 设置进程优先级失败: Pid={Pid}", process.Id);
        }

        var pid = process.Id;
        _runningProcesses[pid] = process;
        JobObjectManager.AddProcess(process.Handle);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        process.Exited += (_, _) =>
        {
            _runningProcesses.TryRemove(pid, out _);
        };

        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            _runningProcesses.TryRemove(pid, out _);
            if (!process.HasExited) process.Kill();
            process.Dispose();
            throw;
        }

        // Read exit code before any potential dispose
        int exitCode = process.ExitCode;

        // Cleanup: dispose once, only if Exited event didn't already do it
        _runningProcesses.TryRemove(pid, out _);
        process.Dispose();

        if (exitCode != 0)
        {
            Log.Warning("[repkg] 进程退出码非0: {Code}, Name={Name}", exitCode, wallpaperName);
        }
    }

    private static ProcessPriorityClass _processPriorityLevel = ProcessPriorityClass.Normal;

    /// <summary>
    /// 设置后续子进程的优先级。0=Normal, 1=BelowNormal, 2=Idle
    /// </summary>
    public static void SetProcessPriorityLevel(int priority)
    {
        _processPriorityLevel = priority switch
        {
            1 => ProcessPriorityClass.BelowNormal,
            2 => ProcessPriorityClass.Idle,
            _ => ProcessPriorityClass.Normal
        };
    }

    private static string GetOutputPath(string outputRoot, WallpaperItem wallpaper, ExtractSettings settings)
    {
        // 平铺模式：所有文件直接放到输出根目录，不建子文件夹
        if (settings.OneFolder)
            return outputRoot;

        // 子文件夹模式
        // 按壁纸标题命名子文件夹
        if (settings.UseProjectName && !string.IsNullOrEmpty(wallpaper.Title))
            return Path.Combine(outputRoot, GetSafeName(wallpaper.Title));
        // 降级：使用 WorkshopID 或文件夹名
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
            var destPath = Path.Combine(outputDir, file.Name);
            if (!settings.CoverAllFiles && File.Exists(destPath)) continue;
            try { File.Copy(file.FullName, destPath, true); }
            catch (Exception ex) { Log.Error(ex, "拷贝文件失败: {File}", file.FullName); }
        }
        // 勾选2.2时：打乱目录结构，子目录文件扁平放入同一目录
        if (settings.KeepSubfolderStructure)
        {
            foreach (var subDir in sourceDir.EnumerateDirectories())
                CopyAllFiles(subDir, outputDir, settings);
        }
        // 不勾选2.2时：保持子文件夹的目录结构
        else
        {
            foreach (var subDir in sourceDir.EnumerateDirectories())
                CopyAllFiles(subDir, Path.Combine(outputDir, subDir.Name), settings);
        }
    }

    #region Win32 Process Suspend/Resume

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtResumeProcess(IntPtr processHandle);

    #endregion
}

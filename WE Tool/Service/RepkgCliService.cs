using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Models;
using WE_Tool.ViewModels;

namespace WE_Tool.Service;

public class RepkgCliService
{
    private readonly string _repkgDir;
    private readonly ConcurrentDictionary<int, Process> _runningProcesses = new();

    public RepkgCliService(string? repkgDir = null)
    {
        _repkgDir = repkgDir ?? Path.Combine(AppContext.BaseDirectory, "repkg");
    }

    // ---------- repkg 输出日志(Info 页 RePKG_Re 日志面板轮询 repkg.log) ----------
    private static readonly object RepkgLogLock = new();

    private static string RepkgLogPath => Path.Combine(AppSettingsHelper.LogPath, "repkg.log");

    /// <summary>批处理开始前清空 repkg 日志(面板只显示最近一次提取的记录)</summary>
    private static void ResetRepkgLog()
    {
        try
        {
            lock (RepkgLogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RepkgLogPath)!);
                File.WriteAllText(RepkgLogPath, string.Empty);
            }
        }
        catch { /* 写入失败只影响日志面板,不影响提取 */ }
    }

    /// <summary>追加一行 repkg 进程输出(常规行/错误行)到独立日志</summary>
    private static void AppendRepkgLog(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        try
        {
            lock (RepkgLogLock) File.AppendAllText(RepkgLogPath, line + Environment.NewLine);
        }
        catch { }
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
        // 只杀进程,不 Dispose:Process 对象的所有权在 RunBatchAsync(创建→等待→释放)。
        // 停止路径与取消路径并发访问同一对象,若在这里提前 Dispose,
        // RunBatchAsync 取消 catch 里的 HasExited 会抛 InvalidOperationException
        // ("No process is associated with this object",2026-08 实测)。
        // 杀掉后 WaitForExitAsync 自然结束(或走取消路径),统一在那里释放。
        foreach (var kvp in _runningProcesses)
        {
            var process = kvp.Value;
            if (process != null && !process.HasExited)
            {
                try { process.Kill(); }
                catch (Exception ex) { Log.Warning(ex, "[repkg] 终止进程失败: {Pid}", kvp.Key); }
            }
        }
        _runningProcesses.Clear();
    }

    /// <summary>
    /// 单进程 batch 提取:所有 pkg 壁纸交给一个 RePKG_Re.exe batch 进程(内部多线程),
    /// 非 pkg 壁纸(HTML 等)由本服务直接复制;进程崩溃自动重启(第二击跳壁纸,最多 3 次)。
    /// onProgress 消息格式保持 name|action|pct|entry 不变(UI 无感知)。
    /// </summary>
    public async Task ExtractWallpapersAsync(
        IReadOnlyList<WallpaperItem> wallpapers,
        string outputRoot,
        ExtractSettings settings,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        int total = wallpapers.Count;
        if (total == 0) return;

        void ReportProgress(string msg) => onProgress?.Invoke(msg);

        // 跳过已提取 — 仅子文件夹模式下检查(平铺模式共用输出目录,无法按壁纸判断)
        var pending = new List<WallpaperItem>();
        foreach (var wallpaper in wallpapers)
        {
            if (string.IsNullOrEmpty(wallpaper.FolderPath)) continue;
            var dir = new DirectoryInfo(wallpaper.FolderPath);
            if (!dir.Exists) continue;

            var wallpaperOutput = GetOutputPath(outputRoot, wallpaper, settings);
            var name = NameOf(wallpaper);

            if (settings.OneFolder == 0 && settings.SkipExistingOutput &&
                Directory.Exists(wallpaperOutput) &&
                Directory.EnumerateFileSystemEntries(wallpaperOutput).Any())
            {
                ReportProgress($"{name}|跳过(已提取)|100");
                continue;
            }

            pending.Add(wallpaper);
        }

        if (pending.Count == 0)
        {
            if (!ct.IsCancellationRequested)
                ReportProgress($"提取完成，共 {total} 个壁纸");
            return;
        }

        // pkg 壁纸 → batch;非 pkg(HTML 等)→ CopyAllFiles 直接复制
        var pkgWallpapers = pending.Where(w => HasPkgFiles(w.FolderPath!)).ToList();
        var copyWallpapers = pending.Where(w => !HasPkgFiles(w.FolderPath!)).ToList();

        int maxThreads = settings.MaxConcurrentExtractions > 0
            ? settings.MaxConcurrentExtractions
            : Environment.ProcessorCount;

        var batchTask = pkgWallpapers.Count > 0
            ? RunBatchWithRestartAsync(pkgWallpapers, outputRoot, settings, maxThreads, ReportProgress, ct)
            : Task.FromResult((Crashed: 0, GaveUpRemaining: 0));
        var copyTask = copyWallpapers.Count > 0
            ? CopyAllWallpapersAsync(copyWallpapers, outputRoot, settings, maxThreads, ReportProgress, ct)
            : Task.CompletedTask;

        await Task.WhenAll(batchTask, copyTask);

        if (!ct.IsCancellationRequested)
        {
            var (crashed, gaveUpRemaining) = await batchTask;
            if (gaveUpRemaining > 0)
                ReportProgress($"提取失败:批处理连续崩溃,剩余 {gaveUpRemaining} 个壁纸未提取");
            else if (crashed > 0)
                ReportProgress($"提取完成，共 {total} 个壁纸(因崩溃跳过 {crashed} 个)");
            else
                ReportProgress($"提取完成，共 {total} 个壁纸");
        }
    }

    /// <summary>
    /// batch 提取 + 崩溃重启循环:id 稳定(数组下标);收到 wallpaper done 的壁纸移出 pending;
    /// 进程退出未见 batch done = 崩溃:第一次重启全部 pending,第二次崩在同一壁纸 → 跳过该壁纸,
    /// 连续 3 次崩溃整体放弃。用户取消(ct)不触发重启。
    /// 返回:(Crashed = 因第二击跳过的壁纸数, GaveUpRemaining = 整体放弃时剩余壁纸数,0 = 未放弃)。
    /// </summary>
    private async Task<(int Crashed, int GaveUpRemaining)> RunBatchWithRestartAsync(
        List<WallpaperItem> wallpapers,
        string outputRoot,
        ExtractSettings settings,
        int maxThreads,
        Action<string> reportProgress,
        CancellationToken ct)
    {
        var items = wallpapers.Select((w, i) => new BatchItem { Id = i.ToString(), Wallpaper = w }).ToList();
        var idToItem = items.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var pending = new HashSet<string>(items.Select(x => x.Id), StringComparer.Ordinal);

        // 每次提取开始清空 repkg 日志(Info 页 RePKG_Re 日志面板只显示最近一次提取的记录)
        ResetRepkgLog();

        int restartCount = 0;
        const int maxRestarts = 3;
        string? lastCrashId = null;
        var crashedIds = new List<string>();

        while (pending.Count > 0)
        {
            if (ct.IsCancellationRequested) break;

            var manifestPath = WriteManifest(
                items.Where(x => pending.Contains(x.Id)).ToList(), outputRoot, settings, maxThreads);

            BatchRunResult result;
            try
            {
                result = await RunBatchAsync(manifestPath, idToItem, reportProgress, ct);
            }
            finally
            {
                try { File.Delete(manifestPath); } catch { }
            }

            if (ct.IsCancellationRequested) break;

            // 已完成的壁纸:移出 pending + 后处理(project.json/预览图/平铺重命名)
            foreach (var id in result.DoneIds)
            {
                if (pending.Remove(id) && idToItem.TryGetValue(id, out var doneItem))
                    PostProcessWallpaper(doneItem.Wallpaper, GetOutputPath(outputRoot, doneItem.Wallpaper, settings), settings);
            }

            if (result.CleanDone) break;

            // ---- 崩溃恢复 ----
            restartCount++;
            if (restartCount > maxRestarts)
            {
                int remaining = pending.Count;
                Log.Error("[repkg] 批处理连续崩溃超过 {Max} 次,放弃剩余 {Count} 个壁纸", maxRestarts, remaining);
                foreach (var id in pending)
                {
                    if (idToItem.TryGetValue(id, out var it))
                        reportProgress($"{NameOf(it.Wallpaper)}|失败|100");
                }
                pending.Clear();
                return (crashedIds.Count, remaining);
            }

            var suspect = result.LastActiveId;
            if (suspect != null && suspect == lastCrashId && pending.Contains(suspect) &&
                idToItem.TryGetValue(suspect, out var suspectItem))
            {
                // 第二击:同一壁纸连续两次触发崩溃,确认元凶,跳过并记录
                crashedIds.Add(suspect);
                Log.Warning("[repkg] 壁纸 {Name} 连续两次触发崩溃,已跳过", NameOf(suspectItem.Wallpaper));
                reportProgress($"{NameOf(suspectItem.Wallpaper)}|失败|100");
                pending.Remove(suspect);
                lastCrashId = null;
            }
            else
            {
                lastCrashId = suspect;
            }

            Log.Warning("[repkg] 批处理进程崩溃,重启剩余 {Count} 个壁纸(第 {N} 次),manifest: {Path}",
                pending.Count, restartCount, manifestPath);
        }

        if (crashedIds.Count > 0)
        {
            var names = crashedIds
                .Where(id => idToItem.TryGetValue(id, out _))
                .Select(id => NameOf(idToItem[id].Wallpaper));
            Log.Warning("[repkg] 因崩溃跳过的壁纸: {Names}", string.Join(", ", names));
        }

        return (crashedIds.Count, 0);
    }

    /// <summary>单次 batch 进程调用:解析 JSON 事件,按 id 路由到壁纸,跟踪完成/崩溃信息。</summary>
    private async Task<BatchRunResult> RunBatchAsync(
        string manifestPath,
        Dictionary<string, BatchItem> idToItem,
        Action<string> reportProgress,
        CancellationToken ct)
    {
        var result = new BatchRunResult();
        var lastTickById = new Dictionary<string, long>(StringComparer.Ordinal);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(_repkgDir, "RePKG_Re.exe"),
                Arguments = $"batch --manifest \"{manifestPath}\"",
                WorkingDirectory = _repkgDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // repkg 输出 UTF-8(Console.OutputEncoding=UTF-8);不显式指定时 .NET 按系统
                // ANSI 代码页(GBK)解码,中文文件名会乱码(恶魔→榄旂帇,2026-08 实测)
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            // repkg 常规输出行(非 JSON 事件,如启动信息/警告摘要)记录到独立日志,
            // 不再丢弃——Info 页 RePKG_Re 日志面板可见
            if (!e.Data.StartsWith("{"))
            {
                AppendRepkgLog(e.Data);
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(e.Data);
                var root = doc.RootElement;

                // 批次完成:整批正常结束的唯一判据
                if (root.TryGetProperty("type", out var typeProp))
                {
                    var type = typeProp.GetString();
                    if (type == "batch" && root.TryGetProperty("action", out var ba) &&
                        ba.GetString() == "done")
                    {
                        result.CleanDone = true;
                        return;
                    }

                    if (type == "wallpaper" && root.TryGetProperty("id", out var widProp))
                    {
                        var id = widProp.GetString();
                        if (id == null || !idToItem.TryGetValue(id, out var item)) return;
                        var action = root.TryGetProperty("action", out var wa) ? wa.GetString() : null;
                        if (action == "start")
                            reportProgress($"{NameOf(item.Wallpaper)}|开始|0");
                        else if (action == "done")
                        {
                            result.DoneIds.Add(id);
                            reportProgress($"{NameOf(item.Wallpaper)}|完成|100");
                        }
                        return;
                    }
                }

                if (!root.TryGetProperty("id", out var idProp)) return;
                var entryId = idProp.GetString();
                if (entryId == null || !idToItem.TryGetValue(entryId, out var entryItem)) return;
                var entryType = root.TryGetProperty("type", out var et) ? et.GetString() : null;
                var name = NameOf(entryItem.Wallpaper);

                if (entryType == "entry")
                {
                    // 崩溃定位:转换前发出的事件,最后一条 = 崩溃前正在处理的条目
                    result.LastActiveId = entryId;

                    // 节流:每个壁纸最多每 30ms 触发一次进度回调
                    var now = Environment.TickCount64;
                    if (lastTickById.TryGetValue(entryId, out var last) && now - last < 30) return;
                    lastTickById[entryId] = now;

                    double pct = 0;
                    if (root.TryGetProperty("pos", out var posP) && root.TryGetProperty("total", out var totalP) &&
                        totalP.GetInt32() > 0)
                        pct = Math.Round(posP.GetInt32() * 100.0 / totalP.GetInt32(), 1);
                    var entry = root.TryGetProperty("entry", out var ep) ? ep.GetString() : null;
                    reportProgress($"{name}|解析PKG|{pct}|{entry}");
                }
                else if (entryType == "error")
                {
                    result.ErrorCount++;
                    var entry = root.TryGetProperty("entry", out var ep2) ? ep2.GetString() : null;
                    var msg = root.TryGetProperty("msg", out var mp) ? mp.GetString() : null;
                    Log.Warning("[repkg] 条目错误 {Entry}: {Msg}", entry, msg);
                    AppendRepkgLog($"[ERR] {entry}: {msg}");
                }
            }
            catch { }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                Log.Warning("[repkg] {Msg}", e.Data);
                AppendRepkgLog(e.Data);
            }
        };

        process.Start();

        // 设置子进程优先级
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

        _runningProcesses.TryRemove(pid, out _);
        process.Dispose();
        return result;
    }

    /// <summary>ExtractSettings → batch manifest 文件(临时目录,用完即删)。</summary>
    private static string WriteManifest(
        List<BatchItem> items, string outputRoot, ExtractSettings settings, int threads)
    {
        var manifest = new
        {
            threads,
            wallpapers = items.Select(x => new
            {
                id = x.Id,
                input = x.Wallpaper.FolderPath,
                output = GetOutputPath(outputRoot, x.Wallpaper, settings)
            }).ToList(),
            options = BuildManifestOptions(settings)
        };

        var path = Path.Combine(Path.GetTempPath(),
            $"repkg_batch_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        return path;
    }

    /// <summary>manifest options:与旧 BuildArgs 的分支逻辑 1:1 对应。</summary>
    private static object BuildManifestOptions(ExtractSettings settings)
    {
        var o = new Dictionary<string, object?>
        {
            ["overwrite"] = settings.CoverAllFiles,
            ["keepSubfolderStructure"] = settings.KeepSubfolderStructure == 1,
            ["pathsDepth"] = 0,
            ["filterEffectImages"] = 0,
            ["noTexConvert"] = false,
            ["onlyTexImages"] = false
        };

        // 自定义模式(OutputMode==2):输出层过滤 + 目录过滤
        if (settings.OutputMode == 2)
        {
            if (settings.IgnoreExtension && !string.IsNullOrEmpty(settings.IgnoreExtensionList))
                o["outputIgnoreExts"] = SplitCsv(settings.IgnoreExtensionList);
            if (settings.OnlyExtension && !string.IsNullOrEmpty(settings.OnlyExtensionList))
                o["outputOnlyExts"] = SplitCsv(settings.OnlyExtensionList);
            if (settings.IgnorePaths && !string.IsNullOrEmpty(settings.IgnorePathsList))
                o["ignorepaths"] = SplitCsv(settings.IgnorePathsList);
            if (settings.OnlyPaths && !string.IsNullOrEmpty(settings.OnlyPathsList))
                o["onlypaths"] = SplitCsv(settings.OnlyPathsList);
            if (settings.FilterEffectImagesEnabled && settings.FilterEffectImagesThreshold > 0)
                o["filterEffectImages"] = settings.FilterEffectImagesThreshold;
        }

        // 媒体模式(OutputMode==1):媒体白名单 + materials/sounds 直接子文件
        if (settings.OutputMode == 1)
        {
            o["outputOnlyExts"] = MediaOnlyExtensionsArg.Split(',');
            o["onlypaths"] = new[] { "materials", "sounds" };
            o["pathsDepth"] = 1;
        }
        else if (settings.TexExportMode == 0)
            o["noTexConvert"] = true;
        else if (settings.TexExportMode == 2)
            o["onlyTexImages"] = true;

        return o;
    }

    /// <summary>非 pkg 壁纸(HTML 等):直接复制(与旧流程 CopyAllFiles 分支等价)。</summary>
    private async Task CopyAllWallpapersAsync(
        List<WallpaperItem> wallpapers,
        string outputRoot,
        ExtractSettings settings,
        int maxThreads,
        Action<string> reportProgress,
        CancellationToken ct)
    {
        var po = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, maxThreads)
        };

        try
        {
            await Parallel.ForEachAsync(wallpapers, po, (wallpaper, token) =>
            {
                token.ThrowIfCancellationRequested();
                var dir = new DirectoryInfo(wallpaper.FolderPath!);
                var wallpaperOutput = GetOutputPath(outputRoot, wallpaper, settings);
                var name = NameOf(wallpaper);

                reportProgress($"{name}|开始|0");
                try
                {
                    Directory.CreateDirectory(wallpaperOutput);
                    CopyAllFiles(dir, wallpaperOutput, settings, wallpaper.Type == "scene");
                    PostProcessWallpaper(wallpaper, wallpaperOutput, settings);
                    reportProgress($"{name}|完成|100");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[repkg] 拷贝壁纸失败: {Name}", name);
                    reportProgress($"{name}|失败|100");
                }
                return ValueTask.CompletedTask;
            });
        }
        catch (OperationCanceledException) { }
    }

    private static bool HasPkgFiles(string folderPath)
    {
        try
        {
            var dir = new DirectoryInfo(folderPath);
            return dir.EnumerateFiles("*.pkg", SearchOption.AllDirectories).Any()
                || dir.EnumerateFiles("*.mpkg", SearchOption.AllDirectories).Any();
        }
        catch { return false; }
    }

    /// <summary>壁纸提取完成后的统一后处理:project.json/预览图导出 + 平铺模式重命名。</summary>
    private static void PostProcessWallpaper(WallpaperItem wallpaper, string outputDir, ExtractSettings settings)
    {
        // OutputMode==1(仅输出媒体文件)时不复制 project.json/预览图
        if (settings.OutProjectJSON && settings.OutputMode != 1)
            CopyProjectFiles(new DirectoryInfo(wallpaper.FolderPath!), outputDir, settings);

        // 平铺模式
        if (settings.OneFolder == 1 && settings.FlatFileNamingMode == 1 && !string.IsNullOrEmpty(wallpaper.Title))
        {
            var safeTitle = GetSafeName(wallpaper.Title);
            foreach (var f in Directory.EnumerateFiles(outputDir))
            {
                var fi = new FileInfo(f);
                var newName = $"{safeTitle}_{fi.Name}";
                var dest = Path.Combine(outputDir, newName);
                int seq = 2;
                while (File.Exists(dest))
                    dest = Path.Combine(outputDir, $"{safeTitle}_{seq++}_{fi.Name}");
                File.Move(f, dest);
            }
        }
    }

    private static string GetOutputPath(string outputRoot, WallpaperItem wallpaper, ExtractSettings settings)
    {
        // 平铺模式:所有文件直接放到输出根目录,不建子文件夹
        if (settings.OneFolder == 1)
            return outputRoot;

        // 子文件夹模式
        // 按壁纸标题命名子文件夹
        if (settings.UseProjectName && !string.IsNullOrEmpty(wallpaper.Title))
            return Path.Combine(outputRoot, GetSafeName(wallpaper.Title));
        // 降级:使用 WorkshopID 或文件夹名
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

    private static void CopyAllFiles(DirectoryInfo sourceDir, string outputDir, ExtractSettings settings, bool isScene = false)
    {
        foreach (var file in sourceDir.EnumerateFiles())
        {
            // OutputMode==1(仅输出媒体文件):独立模式,只检查媒体扩展名,不受 IgnoreExtension/OnlyExtension 影响
            if (settings.OutputMode == 1)
            {
                if (!IsMediaExtension(file.Extension)) continue;
            }
            else
            {
                // 自定义模式的扩展名过滤
                if (ShouldSkipExtension(file.Extension, settings)) continue;
            }

            var destPath = Path.Combine(outputDir, file.Name);
            if (!settings.CoverAllFiles && File.Exists(destPath)) continue;
            try { File.Copy(file.FullName, destPath, true); }
            catch (Exception ex) { Log.Error(ex, "拷贝文件失败: {File}", file.FullName); }
        }
        // 子目录处理:非场景壁纸始终保持目录结构,场景壁纸由 KeepSubfolderStructure 控制
        bool flatten = isScene && settings.KeepSubfolderStructure == 1;
        if (flatten)
        {
            foreach (var subDir in sourceDir.EnumerateDirectories())
                CopyAllFiles(subDir, outputDir, settings, isScene);
        }
        else
        {
            foreach (var subDir in sourceDir.EnumerateDirectories())
                CopyAllFiles(subDir, Path.Combine(outputDir, subDir.Name), settings, isScene);
        }
    }

    private static void CopyProjectFiles(DirectoryInfo sourceDir, string outputDir, ExtractSettings settings)
    {
        var projectJsonFile = sourceDir.GetFiles("project.json", SearchOption.TopDirectoryOnly)
                                       .FirstOrDefault();
        if (projectJsonFile == null || !projectJsonFile.Exists) return;

        try
        {
            // 拷贝 project.json
            var destProjectJson = Path.Combine(outputDir, "project.json");
            if (settings.CoverAllFiles || !File.Exists(destProjectJson))
            {
                File.Copy(projectJsonFile.FullName, destProjectJson, true);
                Log.Information("[repkg] 已拷贝 project.json 到 {Dir}", outputDir);
            }

            // 尝试读取 preview 字段并拷贝预览图
            try
            {
                var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(projectJsonFile.FullName));
                if (json.RootElement.TryGetProperty("preview", out var previewProp))
                {
                    var previewFile = Path.Combine(sourceDir.FullName, previewProp.GetString()!);
                    if (File.Exists(previewFile))
                    {
                        var destPreview = Path.Combine(outputDir, Path.GetFileName(previewFile));
                        if (settings.CoverAllFiles || !File.Exists(destPreview))
                        {
                            File.Copy(previewFile, destPreview, true);
                            Log.Information("[repkg] 已拷贝预览图 {File} 到 {Dir}", Path.GetFileName(previewFile), outputDir);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[repkg] 读取 project.json preview 字段失败: {File}", projectJsonFile.FullName);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[repkg] 拷贝 project.json 失败: {File}", projectJsonFile.FullName);
        }
    }

    /// <summary>
    /// 媒体文件扩展名集合(仅输出媒体文件模式使用):图像 + 视频 + 音频。
    /// 与 RePKG 转换输出格式对齐:TEX 纹理→png/gif 等,视频纹理 TEX→mp4。
    /// </summary>
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // 图像
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".tif", ".ico",
        // 视频(含 RePKG 视频纹理 TEX 转换输出的 .mp4)
        ".mp4", ".webm", ".mov",
        // 音频(场景壁纸 sounds/ 目录下的 mp3/ogg/wav 等)
        ".mp3", ".ogg", ".wav", ".flac", ".m4a", ".aac"
    };

    /// <summary>
    /// 仅输出媒体文件模式(OutputMode==1)传给 RePKG 的 -E 白名单(逗号分隔、无前导点)。
    /// 与 MediaExtensions 保持一致:RePKG 输出层过滤会按转换后扩展名保留 TEX 转换图/视频,
    /// 并滤除 raw .tex、.tex-json 及 pkg 内非媒体条目。
    /// </summary>
    private static readonly string MediaOnlyExtensionsArg =
        string.Join(',', MediaExtensions.Select(e => e.TrimStart('.')));

    private static bool IsMediaExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return false;
        var ext = extension.StartsWith('.') ? extension : '.' + extension;
        return MediaExtensions.Contains(ext);
    }

    private static bool ShouldSkipExtension(string extension, ExtractSettings settings)
    {
        if (string.IsNullOrEmpty(extension)) return false;
        // 确保扩展名以 . 开头
        var ext = extension.StartsWith('.') ? extension : '.' + extension;

        if (settings.IgnoreExtension && !string.IsNullOrEmpty(settings.IgnoreExtensionList))
        {
            var ignoreList = settings.IgnoreExtensionList.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var ignored in ignoreList)
            {
                var normalized = ignored.Trim().StartsWith('.') ? ignored.Trim() : '.' + ignored.Trim();
                if (string.Equals(ext, normalized, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        if (settings.OnlyExtension && !string.IsNullOrEmpty(settings.OnlyExtensionList))
        {
            var onlyList = settings.OnlyExtensionList.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var only in onlyList)
            {
                var normalized = only.Trim().StartsWith('.') ? only.Trim() : '.' + only.Trim();
                if (string.Equals(ext, normalized, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true; // 不在白名单中 → 跳过
        }

        return false;
    }

    private static string[] SplitCsv(string csv)
        => csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToArray();

    private static string NameOf(WallpaperItem w)
        => w.Title ?? w.WorkshopID ?? (w.FolderPath != null ? new DirectoryInfo(w.FolderPath).Name : "?");

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

    private sealed class BatchItem
    {
        public string Id { get; init; } = "";
        public WallpaperItem Wallpaper { get; init; } = null!;
    }

    /// <summary>单次 batch 进程的运行结果(崩溃检测与恢复依据)。</summary>
    private sealed class BatchRunResult
    {
        /// <summary>收到 batch done = 整批正常结束</summary>
        public bool CleanDone;

        /// <summary>崩溃前最后活跃的条目所属壁纸 id(崩溃点定位)</summary>
        public string? LastActiveId;

        /// <summary>收到 wallpaper done 的壁纸 id 集合</summary>
        public HashSet<string> DoneIds { get; } = new(StringComparer.Ordinal);

        public int ErrorCount;
    }

    #region Win32 Process Suspend/Resume

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtResumeProcess(IntPtr processHandle);

    #endregion
}

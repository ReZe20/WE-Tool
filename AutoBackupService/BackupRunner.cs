namespace AutoBackupService;

/// <summary>
/// 服务核心:监控 VDF 订阅变化 → 对新增 ID 轮询 content project.json 出现(下载完成) →
/// 命中筛选则硬链接备份;启动时先全量补齐。downloads 目录作为日志参考信号。
/// </summary>
public sealed class BackupRunner : IDisposable
{
    private readonly ServiceConfig _config;
    private readonly string _vdfPath;
    private readonly string _workshopPath;
    private readonly string _downloadsPath;
    private readonly CancellationTokenSource _cts = new();
    private FileSystemWatcher? _vdfWatcher;
    private FileSystemWatcher? _downloadsWatcher;
    private HashSet<string> _knownIds = [];
    /// <summary>待处理队列:只含新增订阅 ID(存量订阅启动时已补齐,不轮询)。</summary>
    private readonly HashSet<string> _pendingIds = [];
    private Task? _loop;

    public BackupRunner(ServiceConfig config)
    {
        _config = config;
        _vdfPath = config.Path!.VdfPath;
        _workshopPath = config.Path!.WorkshopPath;
        // downloads 与 content 同层:steamapps/workshop/downloads/431960
        var workshopRoot = Path.GetDirectoryName(Path.GetDirectoryName(_workshopPath));
        _downloadsPath = Path.Combine(workshopRoot ?? "", "downloads",
            Path.GetFileName(_workshopPath) ?? "431960");
    }

    /// <summary>启动补齐:对所有已下载(有 project.json)、未备份、命中筛选的订阅项目执行备份。</summary>
    public int BackupAllMissing()
    {
        if (!Directory.Exists(_workshopPath))
        {
            Log.Write($"content 目录不存在: {_workshopPath}");
            return 0;
        }
        var subscribed = VdfWatcher.ParseSubscribedIds(_vdfPath);
        int backed = 0;
        foreach (var dir in Directory.EnumerateDirectories(_workshopPath))
        {
            var id = Path.GetFileName(dir);
            if (id == ".we_backup") continue;
            if (subscribed.Count > 0 && !subscribed.Contains(id)) continue; // 仅处理订阅内
            if (HardLinkBackup.IsBackedUp(_workshopPath, id)) continue;
            var projectPath = Path.Combine(dir, "project.json");
            if (!File.Exists(projectPath)) continue;
            if (!AutoBackupFilter.Matches(_config.AutoBackup, VdfWatcher.ReadProjectMeta(projectPath))) continue;
            if (TryBackup(id)) backed++;
        }
        Log.Write($"启动补齐完成: 新增备份 {backed} 个");
        return backed;
    }

    /// <summary>启动常驻监听(补齐后调用)。不阻塞返回,由内部线程循环运行。</summary>
    public void StartWatch()
    {
        _knownIds = VdfWatcher.ParseSubscribedIds(_vdfPath);
        Log.Write($"服务已启动: VDF={_vdfPath}, 当前订阅 {_knownIds.Count} 个");

        _vdfWatcher = new FileSystemWatcher
        {
            Path = Path.GetDirectoryName(_vdfPath)!,
            Filter = Path.GetFileName(_vdfPath),
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
        };
        _vdfWatcher.Changed += OnVdfChanged;
        _vdfWatcher.Created += OnVdfChanged;
        _vdfWatcher.EnableRaisingEvents = true;

        // downloads 目录信号(日志参考,不阻塞)
        if (Directory.Exists(_downloadsPath))
        {
            _downloadsWatcher = new FileSystemWatcher
            {
                Path = _downloadsPath,
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.CreationTime
            };
            _downloadsWatcher.Created += OnDownloadCreated;
            _downloadsWatcher.EnableRaisingEvents = true;
        }

        // 启动时无待处理项(补齐已在 BackupAllMissing 完成);只有新增订阅才进队列
        _loop = Task.Run(() => RunPendingLoop(_cts.Token));
    }

    /// <summary>等待刷新 VDF 变化(防抖 2s),处理后清理已完成 ID。</summary>
    private void OnVdfChanged(object sender, FileSystemEventArgs e)
    {
        Thread.Sleep(2000); // 防抖:等 Steam 写完
        RefreshKnownIds();
    }

    private void OnDownloadCreated(object sender, FileSystemEventArgs e)
    {
        var id = Path.GetFileName(e.FullPath);
        Log.Write($"downloads 缓存出现新目录: {id}(下载中,等待移入 content)");
    }

    /// <summary>重读 VDF,与新快照对比,把新增 ID 加入处理队列(只轮询新增,不轮询存量)。</summary>
    private void RefreshKnownIds()
    {
        var fresh = VdfWatcher.ParseSubscribedIds(_vdfPath);
        lock (this)
        {
            var added = fresh.Where(id => !_knownIds.Contains(id)).ToList();
            _knownIds = fresh;
            if (added.Count > 0)
            {
                Log.Write($"发现新增订阅 {added.Count} 个: {string.Join(",", added)}");
                // 新 ID 加入待处理队列(只处理这些,避免存量幽灵订阅导致持续轮询)
                foreach (var id in added) _pendingIds.Add(id);
                // 唤醒轮询线程处理新订阅
                try { _hasPending.Release(); } catch { /* 已满(1)或已释放 */ }
            }
        }
    }

    /// <summary>后台循环:轮询 content 目录备份新订阅;空队列时暂停,有新订阅才恢复。</summary>
    private async Task RunPendingLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (GetPendingCount() > 0)
                {
                    await Task.Delay(5000, ct); // 给 Steam 下载留时间
                    ProcessPending();
                }
                else
                {
                    // 无待处理项:阻塞到有新订阅或 VDF 变化唤醒,空闲期零 CPU
                    await _hasPending.WaitAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
    }

    private int GetPendingCount()
    {
        lock (this) return _pendingIds.Count;
    }

    private readonly SemaphoreSlim _hasPending = new(0, 1);

    private void ProcessPending()
    {
        List<string> toProcess;
        lock (this) toProcess = _pendingIds.ToList();

        foreach (var id in toProcess)
        {
            if (HardLinkBackup.IsBackedUp(_workshopPath, id))
            {
                lock (this) _pendingIds.Remove(id); // 已备份,出队
                continue;
            }
            var projPath = Path.Combine(_workshopPath, id, "project.json");
            if (!File.Exists(projPath)) continue; // 还在下载,下轮再查(仅新增订阅,量极小)

            // 已下载:命中筛选则备份;无论命中与否都出队(筛选不符永久跳过)
            if (AutoBackupFilter.Matches(_config.AutoBackup, VdfWatcher.ReadProjectMeta(projPath)))
                TryBackup(id);
            lock (this) _pendingIds.Remove(id);
        }
    }

    private bool TryBackup(string id)
    {
        var sourceDir = Path.Combine(_workshopPath, id);
        if (!Directory.Exists(sourceDir))
        {
            Log.Write($"content/{id} 目录不存在,跳过备份");
            return false;
        }
        var result = HardLinkBackup.BackupWallpaperFolder(sourceDir, _workshopPath, id);
        if (result.Error is null)
        {
            Log.Write($"已备份 {id}: 链接 {result.Linked} 个,跳过 {result.Skipped} 个");
            return true;
        }
        Log.Write($"备份 {id} 失败: {result.Error}");
        return false;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _vdfWatcher?.Dispose();
        _downloadsWatcher?.Dispose();
        _cts.Dispose();
    }
}

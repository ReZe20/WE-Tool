using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;

namespace WE_Tool.Service;

/// <summary>共享定时器驱动的 GIF 播放器:所有可见会话按各自帧延迟换帧。
/// 帧数据在原生内存(引用计数管理),换帧 = 拷贝当前帧到会话持有的单个 WriteableBitmap(微秒级)。
/// 停止/回收/筛选 → 原生帧立即 Free → 任务管理器内存当场回落(不再滞留 .NET 大对象堆)。</summary>
public sealed class GifFramePlayer
{
    private sealed class GifPlaybackSession
    {
        public required string Path;
        public required GifFrames Frames;
        public required Image Target;
        public required WriteableBitmap Frame; // 单帧显示位图(每会话仅 1 个,替换内容 + Invalidate)
        public int Index;
        public TimeSpan NextDue;
    }

    private sealed class PendingDecode
    {
        public required string Path;
        public required CancellationTokenSource Cts;
    }

    private const int TickMs = 16;

    /// <summary>解析缓存开关:关=不存储任何解析缓存,帧只活在播放会话里,停止即释放,
    /// 下次播放(滚回/重开)重新解析。实验——测试无缓存下的内存/并发表现。</summary>
    private const bool CacheEnabled = false;

    private readonly GifFrameCache _cache;
    private readonly Dictionary<object, GifPlaybackSession> _sessions = [];
    private readonly Dictionary<object, PendingDecode> _pending = []; // owner → 进行中的解码任务(记录 path,区分同 path 双触发与换绑)
    private readonly SemaphoreSlim _decodeGate = new(3); // 滚动瞬间大量容器进入时限并发,防解码尖峰
    private readonly DispatcherQueueTimer _timer;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew(); // 单调时钟,防系统时间跳变
    private bool _timerRunning;

    /// <summary>[内存诊断] 事件计数:Put/Remove/Register 次数(定位缓存条数与会话数脱钩的元凶)</summary>
    public int DiagPutCount, DiagRemoveCount, DiagRegisterCount;

    /// <summary>[内存诊断] RemoveExcept(对账)移除计数</summary>
    public int DiagExceptRemoveCount => _cache.DiagRemoveCount;

    /// <summary>必须在 UI 线程构造(定时器与 WriteableBitmap 都要求)</summary>
    public GifFramePlayer()
    {
        _cache = new GifFrameCache(IsPathActive);
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(TickMs);
        _timer.Tick += OnTick;
    }

    /// <summary>启动/换绑播放:缓存命中立即注册;未命中异步解码(限流),完成后复查再注册。
    /// 幂等:同一容器已在播同一 GIF 时不重启(ContainerContentChanging 与 ShadowRect_Loaded 双触发防护)。</summary>
    public void Start(object owner, Image target, string path)
    {
        if (_sessions.TryGetValue(owner, out var existing))
        {
            if (existing.Path == path) return; // 同容器同 GIF:不重启,防双触发反复重解码
            Stop(owner); // 换绑到不同壁纸:先清旧会话与待解码任务
        }
        if (_pending.TryGetValue(owner, out var pending))
        {
            if (pending.Path == path) return; // 同容器同 GIF 解码中:等待完成,不重复入队
            pending.Cts.Cancel();             // 换绑到不同 GIF:取消旧解码任务
            _pending.Remove(owner);
        }

        if (CacheEnabled && _cache.TryGet(path) is { } frames)
        {
            Register(owner, path, frames, target);
            return;
        }

        var cts = new CancellationTokenSource();
        _pending[owner] = new PendingDecode { Path = path, Cts = cts };
        _ = DecodeAndRegisterAsync(owner, target, path, cts);
    }

    /// <summary>停止(容器回收/换绑/开关关闭):取消待解码,释放原生帧内存(引用计数归零即 Free),
    /// Image.Source 保持当前帧(静态不闪)。
    /// 缓存=活动会话模型:该路径无其他活动会话时立即移除缓存 → 原生内存当场释放 → 任务管理器内存回落。</summary>
    public void Stop(object owner)
    {
        if (_pending.Remove(owner, out var pd)) pd.Cts.Cancel();
        if (_sessions.Remove(owner, out var session))
        {
            session.Frames.Release(); // 会话引用释放
            if (!IsPathActive(session.Path))
            {
                _cache.Remove(session.Path);
                DiagRemoveCount++;
            }
        }
        if (_sessions.Count == 0) StopTimer();
    }

    /// <summary>全部停止(AutoPlayGif 关闭时)</summary>
    public void StopAll()
    {
        foreach (var pd in _pending.Values) pd.Cts.Cancel();
        _pending.Clear();
        foreach (var s in _sessions.Values) s.Frames.Release();
        _sessions.Clear();
        _cache.Trim();
        StopTimer();
        ScheduleBackgroundGc(); // 清托管残留(BitmapImage/堆段),防开关循环内存抬升
    }

    /// <summary>清空帧缓存(关闭动图时释放全部原生帧内存;重开时重新解码,一次性 CPU 成本)</summary>
    public void ClearCache() => _cache.ClearAll();

    /// <summary>延迟后台强制 GC:释放 BitmapImage/解码临时等托管残留。
    /// 原因:.NET 无内存压力时不主动 GC,native 关联对象(位图)滞留 → 任务管理器内存只涨不降。</summary>
    private void ScheduleBackgroundGc()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500); // 等 UI 切换完成
            try
            {
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            }
            catch { /* 后台 GC 失败无碍 */ }
        });
    }

    /// <summary>释放不在保留集合中的帧缓存(列表变化后调用;仍在列表的帧保留,零解码复用)</summary>
    public void RemoveCacheExcept(HashSet<string> keepPaths) => _cache.RemoveExcept(keepPaths);

    /// <summary>缓存淘汰用:该路径是否有活动播放会话</summary>
    public bool IsPathActive(string path)
    {
        foreach (var s in _sessions.Values)
            if (s.Path == path) return true;
        return false;
    }

    /// <summary>该容器是否正在播放(视口对账幂等判断用)</summary>
    public bool IsPlaying(object owner) => _sessions.ContainsKey(owner);

    /// <summary>[内存诊断] 当前活动会话数</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>[内存诊断] 帧缓存当前占用字节</summary>
    public long CacheBytes => _cache.Bytes;

    /// <summary>[内存诊断] 会话去重后的 GIF 路径数(判断是否同路径多会话)</summary>
    public int DistinctPathCount => _sessions.Values.Select(s => s.Path).Distinct().Count();

    /// <summary>[内存诊断] 帧缓存条数</summary>
    public int CacheCount => _cache.Count;

    /// <summary>[内存诊断] 缓存 path 列表(前 N 个)</summary>
    public IEnumerable<string> CachePaths() => _cache.Paths();

    /// <summary>[内存诊断] 会话 path 列表(前 N 个)</summary>
    public IEnumerable<string> SessionPaths() => _sessions.Values.Select(s => s.Path);

    /// <summary>[内存诊断] 缓存与当前会话 path 的交集数(理想 = 缓存条数 = 会话去重数)</summary>
    public int CacheSessionOverlap()
    {
        var sessionSet = _sessions.Values.Select(s => s.Path).ToHashSet();
        int n = 0;
        foreach (var k in _cache.Paths())
            if (sessionSet.Contains(k)) n++;
        return n;
    }

    private async Task DecodeAndRegisterAsync(object owner, Image target, string path, CancellationTokenSource cts)
    {
        try
        {
            await _decodeGate.WaitAsync(cts.Token);
            try
            {
                var frames = await GifPreviewDecoder.DecodeAsync(path, cts.Token);
                if (frames == null)
                {
                    // 解码失败/取消:清理自己的 pending —— 否则同一容器重新 Start 被残留
                    // pending 幂等挡住("同 path 解码中")→ 永远不再解码 → 永不播放
                    if (_pending.TryGetValue(owner, out var cur) && ReferenceEquals(cur.Cts, cts))
                        _pending.Remove(owner);
                    return;
                }
                if (CacheEnabled)
                {
                    _cache.Put(path, frames); // Put 内部 AddRef(缓存持有)
                    DiagPutCount++;
                }
                else
                {
                    frames.AddRef(); // 缓存关闭:解码任务临时持有(Register/失败时释放)
                }

                // 竞态三复查:解码期间容器可能已回收/换绑/开关已关
                if (cts.IsCancellationRequested
                    || !_pending.TryGetValue(owner, out var current)
                    || !ReferenceEquals(current.Cts, cts)
                    || current.Path != path)
                {
                    // 无主帧立即释放(否则帧滞留,内存与可见数脱钩)
                    if (CacheEnabled)
                    {
                        if (!IsPathActive(path)) _cache.Remove(path);
                    }
                    else
                    {
                        frames.Release(); // 缓存关闭:解码临时引用释放 → 归零即 Free
                    }
                    return;
                }

                Register(owner, path, frames, target);
                if (!CacheEnabled) frames.Release(); // 缓存关闭:会话已持 1 份,释放解码临时引用
            }
            finally
            {
                _decodeGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // 取消是正常路径(容器回收/滚动):同样清理自己的 pending(防同一容器被残留 pending 挡死)
            if (_pending.TryGetValue(owner, out var cur) && ReferenceEquals(cur.Cts, cts))
                _pending.Remove(owner);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GIF 播放启动失败: {Path}", path);
        }
    }

    private void Register(object owner, string path, GifFrames frames, Image target)
    {
        _pending.Remove(owner);
        DiagRegisterCount++;
        frames.AddRef(); // 会话持 1 份引用

        // 单帧显示位图(会话持有;换帧 = 拷贝原生像素到该位图 + Invalidate)
        var frame = new WriteableBitmap(frames.Width, frames.Height);
        var session = new GifPlaybackSession
        {
            Path = path,
            Frames = frames,
            Target = target,
            Frame = frame,
            Index = 0,
            NextDue = _clock.Elapsed + TimeSpan.FromMilliseconds(frames.DelaysMs[0])
        };
        // 替换 Source 前显式清空兜底 BitmapImage:其内部可能已解码 GIF 全部帧(native),
        // 不清则与帧数据双份驻留 —— 内存大头之一
        if (target.Source is BitmapImage fallback) fallback.UriSource = null;
        // 立即填充第一帧(避免解码完成到首次 Tick 之间空白闪烁)
        int frameBytes = frames.FrameBytes;
        var buf = ArrayPool<byte>.Shared.Rent(frameBytes);
        try
        {
            frames.CopyFrameTo(0, buf);
            using var ms = frame.PixelBuffer.AsStream();
            ms.Write(buf, 0, frameBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        target.Source = frame;
        _sessions[owner] = session;
        _cache.Trim(); // 会话已注册后再淘汰:IsPathActive 判断准确,只淘汰真正的非活动残留
        StartTimer();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (_sessions.Count == 0)
        {
            StopTimer();
            return;
        }

        var now = _clock.Elapsed;
        foreach (var session in _sessions.Values.ToList()) // 快照:回调期间可能增删
        {
            if (!session.Target.IsLoaded) continue; // 容器已回收(不可见),等 ContainerContentChanging 统一 Stop
            if (now < session.NextDue) continue;

            var frames = session.Frames;
            int count = frames.Count;
            if (count <= 1) continue; // 单帧 GIF:静态显示,无需换帧
            int steps = 0;
            while (now >= session.NextDue && steps < count) // 追赶多帧(极端卡顿),最多整圈
            {
                session.Index = (session.Index + 1) % count;
                session.NextDue += TimeSpan.FromMilliseconds(frames.DelaysMs[session.Index]);
                steps++;
            }
            // 拷贝原生帧像素 → 会话位图(中间数组池化,不产生 LOH 分配)
            int frameBytes = frames.FrameBytes;
            var buf = ArrayPool<byte>.Shared.Rent(frameBytes);
            try
            {
                frames.CopyFrameTo(session.Index, buf);
                using var ms = session.Frame.PixelBuffer.AsStream();
                ms.Write(buf, 0, frameBytes);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
            session.Frame.Invalidate(); // 位图内容已更新,请求重绘
        }
    }

    private void StartTimer()
    {
        if (_timerRunning) return;
        _timer.Start();
        _timerRunning = true;
    }

    private void StopTimer()
    {
        if (!_timerRunning) return;
        _timer.Stop();
        _timerRunning = false;
    }
}

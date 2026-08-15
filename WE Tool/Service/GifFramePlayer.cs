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

        /// <summary>注册时刻(容器回收后会话挂起;超时未激活自动清理,防池中容器滞留帧)</summary>
        public DateTime RegisteredAt;
    }

    private sealed class PendingDecode
    {
        public required string Path;
        public required CancellationTokenSource Cts;

        /// <summary>启动时刻(MaxSessions 满员时淘汰最旧滞留解码用)</summary>
        public DateTime EnteredAt;

        /// <summary>最新显示目标(解码期间容器模板重建重绑时更新 —— Register 用最新 Image)</summary>
        public Image Target = null!;
    }

    private const int TickMs = 16;

    /// <summary>同时播放/解码的会话上限:防"一次性启动所有可见 GIF"导致的 CPU 峰值与内存峰值。
    /// 满员时新项保持 BitmapImage 静态首帧,槽位释放后由 SessionSlotFreed → 页面对账补启动。
    /// 曾为 10——最大化视口 30~45 张时大量卡片被拒(没动画),调 48 覆盖极限视口。</summary>
    private const int MaxSessions = 48;

    /// <summary>解析缓存开关:开=解码过的 GIF 帧存入缓存(LRU,预算见 GifFrameCache),滚动回看零解码零 GC;
    /// 关=帧只活在会话里,每次滚回重新解析(采样实测:滚动经过 40 张 = 40 次重解码 = 一波 GC 潮)。</summary>
    private const bool CacheEnabled = true;

    private readonly GifFrameCache _cache;
    private readonly Dictionary<object, GifPlaybackSession> _sessions = [];
    private readonly Dictionary<object, PendingDecode> _pending = []; // owner → 进行中的解码任务(记录 path,区分同 path 双触发与换绑)
    private readonly DispatcherQueueTimer _timer;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew(); // 单调时钟,防系统时间跳变
    private bool _timerRunning;

    /// <summary>曾因会话上限拒绝过启动:槽位释放时据此补触发一次页面对账(而非每次 Stop 都刷)</summary>
    private bool _capWasFull;

    /// <summary>槽位释放事件:上限满员后只要有会话/解码退出,页面借此补一次视口对账以填补空位</summary>
    public event Action? SessionSlotFreed;

    /// <summary>[内存诊断] 事件计数:Put/Remove/Register 次数(定位缓存条数与会话数脱钩的元凶)</summary>
    public int DiagPutCount, DiagRemoveCount, DiagRegisterCount;

    /// <summary>[内存诊断] RemoveExcept(对账)移除计数</summary>
    public int DiagExceptRemoveCount => _cache.DiagRemoveCount;

    /// <summary>上一版"节流 GC"的教训:非阻塞后台 Gen2(Optimized, blocking:false)不回收 LOH、
    /// 也不把已提交内存段归还 OS,滚动积累的 LOH 垃圾滞留 → 任务管理器内存只涨不降(600MB 水位);
    /// 阻塞式 Aggressive 每 3 秒则反复冻结 UI。折中方案见 ScheduleBackgroundGc。</summary>

    /// <summary>churn 时间戳:解码完成/会话停止时更新。空闲回收以此为据 —— 停止后 5 秒无新 churn 才考虑回收。</summary>
    private DateTime _lastChurn = DateTime.MinValue;

    /// <summary>上一次真正执行回收的时刻(防 GC 风暴:两次回收至少间隔 20 秒)</summary>
    private DateTime _lastGc = DateTime.MinValue;

    /// <summary>空闲回收任务是否已排队(0/1 防重复排队)</summary>
    private int _gcPassScheduled;

    /// <summary>进行中的解码数(static:两个页面各持一个播放器,低延迟窗口必须跨实例共享)。
    /// >0 期间挂 SustainedLowLatency:抑制阻塞式 Gen2/LOH 收集 —— 启动/滚动时的解码风暴不再反复冻结 UI。</summary>
    private static int _activeDecodes;

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
            if (existing.Path == path)
            {
                existing.Target = target; // 重绑(容器模板重建,Image 是新对象):更新显示目标,会话/帧复用(抖动滚动动画不打断)
                return;
            }
            Stop(owner); // 换绑到不同壁纸:先清旧会话与待解码任务
        }
        if (_pending.TryGetValue(owner, out var pending))
        {
            if (pending.Path == path)
            {
                pending.Target = target; // 重绑(模板重建):更新最新显示目标,解码结果注册到新 Image
                return; // 同容器同 GIF 解码中:等待完成,不重复入队
            }
            pending.Cts.Cancel();             // 换绑到不同 GIF:取消旧解码任务
            _pending.Remove(owner);
        }

        // 会话上限:满员时拒绝新启动(卡片保持静态首帧)。先尝试淘汰最旧的滞留解码腾位
        // (滚动离开容器的解码通常已作废),没有可淘汰的才拒绝;槽位释放由 SessionSlotFreed 通知页面对账补启动
        if (_sessions.Count + _pending.Count >= MaxSessions)
        {
            object? oldestOwner = null;
            DateTime oldest = DateTime.MaxValue;
            foreach (var kv in _pending)
                if (kv.Value.EnteredAt < oldest) { oldest = kv.Value.EnteredAt; oldestOwner = kv.Key; }
            if (oldestOwner == null || ReferenceEquals(oldestOwner, owner))
            {
                _capWasFull = true;
                return;
            }
            _pending.Remove(oldestOwner, out var stale);
            stale.Cts.Cancel(); // 滞留解码取消是正常路径(DecodeAsync 捕获 OCE 返回 null 自清理)
        }
        if (CacheEnabled && _cache.TryGet(path) is { } frames)
        {
            Register(owner, path, frames, target);
            return;
        }

        var cts = new CancellationTokenSource();
        _pending[owner] = new PendingDecode { Path = path, Cts = cts, EnteredAt = DateTime.UtcNow, Target = target };
        _ = DecodeAndRegisterAsync(owner, target, path, cts);
    }

    /// <summary>停止(容器回收/换绑/开关关闭):取消待解码,释放原生帧内存(引用计数归零即 Free),
    /// Image.Source 保持当前帧(静态不闪)。
    /// 缓存=活动会话模型:该路径无其他活动会话时立即移除缓存 → 原生内存当场释放 → 任务管理器内存回落。</summary>
    public void Stop(object owner)
    {
        if (_pending.Remove(owner, out var pd)) pd.Cts.Cancel();
        StopSession(owner);
        NotifySlotFreed(); // 停止后槽位可能已空:若曾满员,通知页面补启动
    }

    /// <summary>仅停会话(容器回收用):释放原生帧,但【不取消解码任务】。
    /// 抖动滚动时容器反复回收/重绑 —— 取消解码会让 200ms 停留门槛永远等不满(动画永不开始);
    /// 保留解码 → 重绑同一 item 时 Start 幂等命中 → 解码完门槛已过 → 立即注册播放。
    /// 解码完成时容器仍未重绑 → 会话注册但 Tick 跳过(IsLoaded false),对账(停止后)清理。</summary>
    public void StopSession(object owner)
    {
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
        ScheduleBackgroundGc(); // 停止 churn:节流清 LOH/托管残留
        NotifySlotFreed(); // 会话移除释放播放槽位:若曾满员,通知页面补启动(对账 200ms 防抖)
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
        NotifySlotFreed(); // 全停后槽位全空:若曾满员,通知页面补启动
    }

    /// <summary>槽位释放通知:仅当曾因会话上限拒绝过启动、且当前确有富余槽位时才触发,
    /// 避免滚动回收时每个 StopSession 都刷一次对账(抖动滚动不打断解码的设计不受影响)。</summary>
    private void NotifySlotFreed()
    {
        if (_capWasFull && _sessions.Count + _pending.Count < MaxSessions)
        {
            _capWasFull = false;
            SessionSlotFreed?.Invoke();
        }
    }

    /// <summary>清空帧缓存(关闭动图时释放全部原生帧内存;重开时重新解码,一次性 CPU 成本)</summary>
    public void ClearCache() => _cache.ClearAll();

    /// <summary>空闲触发的托管堆回收:churn(解码完成/会话停止)停歇 5 秒 + 堆垃圾超 100MB 时才执行一次阻塞式 Gen2。
    /// 阻塞式 Gen2(不压缩)会回收 LOH 并把空段归还 OS —— 任务管理器内存水位回落;选在空闲时执行,UI 冻结不可感知。
    /// 非阻塞后台 Gen2(上一版方案)不回收 LOH 也不归还内存段,故弃用;防风暴:实际回收间隔 ≥ 20 秒。</summary>
    private void ScheduleBackgroundGc()
    {
        _lastChurn = DateTime.UtcNow; // 每次 churn 都更新:空闲计时从最后一次活动起算
        if (Interlocked.Exchange(ref _gcPassScheduled, 1) == 1) return; // 已有回收任务在排队,防 churn 风暴

        _ = Task.Run(async () =>
        {
            await Task.Delay(5000); // 等 churn 停歇
            try
            {
                if ((DateTime.UtcNow - _lastChurn).TotalSeconds < 5) return; // 仍在解码/停止,跳过
                if ((DateTime.UtcNow - _lastGc).TotalSeconds < 20) return; // 防 GC 风暴:两次回收间隔 ≥ 20 秒
                var info = GC.GetGCMemoryInfo();
                long garbage = info.HeapSizeBytes - GC.GetTotalMemory(false);
                if (garbage < 100L * 1024 * 1024) return; // 垃圾不多:不值得暂停
                if (Volatile.Read(ref _activeDecodes) > 0) return; // 仍有解码在进行,不在此时开阻塞 GC
                _lastGc = DateTime.UtcNow;
                GC.Collect(2, GCCollectionMode.Forced, false); // 非阻塞:后台回收,不暂停 UI 线程(像素池化后 LOH 垃圾≈0,无需阻塞式)
                GC.WaitForPendingFinalizers();
            }
            catch { /* 后台 GC 失败无碍 */ }
            finally
            {
                Interlocked.Exchange(ref _gcPassScheduled, 0);
            }
        });
    }

    /// <summary>解码开始:首个解码挂 SustainedLowLatency。解码的每帧 DetachPixelData 都是 LOH 大数组,
    /// 自动 GC 会在加载/滚动瞬间反复触发阻塞式 Gen2(全线程停顿,UI 卡顿);低延迟窗口内 gen2 被抑制,
    /// gen0/1 照常(亚毫秒级)。仅工作站 GC 支持;Server GC 下设置失败被吞,退化回原行为。</summary>
    private static void EnterDecode()
    {
        if (Interlocked.Increment(ref _activeDecodes) == 1)
        {
            try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency; }
            catch { /* 环境不支持低延迟模式,忽略 */ }
        }
    }

    /// <summary>解码结束:全部解码退出后恢复 Interactive,积压垃圾交给空闲触发回收。</summary>
    private static void ExitDecode()
    {
        if (Interlocked.Decrement(ref _activeDecodes) == 0)
        {
            try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive; }
            catch { }
        }
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
        GifFrames? frames = null;
        bool productReleased = false; // 解码产物持有是否已释放(防 catch 误减已转移的会话/缓存引用)
        EnterDecode();
        try
        {
            frames = await GifPreviewDecoder.DecodeAsync(path, cts.Token);
            if (frames == null)
            {
                // 解码失败/取消:清理自己的 pending —— 否则同一容器重新 Start 被残留
                // pending 幂等挡住("同 path 解码中")→ 永远不再解码 → 永不播放
                if (_pending.TryGetValue(owner, out var cur) && ReferenceEquals(cur.Cts, cts))
                    _pending.Remove(owner);
                return;
            }

            // 解码产物统一持有(复查期间帧不丢;缓存开=Put 后释放,缓存关=Register 后释放)
            frames.AddRef();

            if (CacheEnabled)
            {
                _cache.Put(path, frames); // Put 内部 AddRef(缓存持有)
                DiagPutCount++;
                frames.Release(); // 释放解码产物持有 → 缓存持 1 份
                productReleased = true;
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
                    productReleased = true;
                }
                return;
            }

            // 解码期间容器可能已重绑(模板重建,Image 是新对象):用 pending 记录的最新目标
            Register(owner, path, frames, current.Target);
            if (!CacheEnabled)
            {
                frames.Release(); // 缓存关闭:会话已持 1 份,释放解码临时引用
                productReleased = true;
            }
            ScheduleBackgroundGc(); // 解码完成:登记 churn,空闲后回收 LOH(每帧 DetachPixelData 大数组)
        }
        catch (OperationCanceledException)
        {
            // 取消是正常路径(容器回收/滚动):释放解码产物持有(归零即 Free),清理自己的 pending
            // (防同一容器被残留 pending 挡死);门槛等待期间取消时 frames 已 AddRef,必须 Release
            if (!productReleased) frames?.Release();
            if (CacheEnabled && productReleased && !IsPathActive(path))
                _cache.Remove(path); // 取消发生在 Put 之后:缓存条目已无主,补释放(否则滞留到下次 Trim)
            if (_pending.TryGetValue(owner, out var cur) && ReferenceEquals(cur.Cts, cts))
                _pending.Remove(owner);
        }
        catch (Exception ex)
        {
            if (!productReleased) frames?.Release(); // 异常兜底:仅释放未转移的解码产物持有
            if (CacheEnabled && productReleased && !IsPathActive(path))
                _cache.Remove(path); // 异常发生在 Put 之后:同取消,补释放无主缓存条目
            Log.Warning(ex, "GIF 播放启动失败: {Path}", path);
        }
        finally
        {
            ExitDecode();
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
            NextDue = _clock.Elapsed + TimeSpan.FromMilliseconds(frames.DelaysMs[0] + ((owner.GetHashCode() & 0x7F) % 8)), // 相位抖动 0~7ms:避免所有会话同一 tick 集中换帧
            RegisteredAt = DateTime.UtcNow
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
        var nowUtc = DateTime.UtcNow;
        foreach (var session in _sessions.Values.ToList()) // 快照:回调期间可能增删
        {
            if (!session.Target.IsLoaded)
            {
                // 容器已回收(不可见):挂起;注册超 2 秒仍未重绑 → 自动清理(防池中容器滞留帧内存)
                if ((nowUtc - session.RegisteredAt).TotalSeconds > 2)
                {
                    var owner = _sessions.FirstOrDefault(kv => ReferenceEquals(kv.Value, session)).Key;
                    if (owner != null) Stop(owner);
                }
                continue;
            }
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

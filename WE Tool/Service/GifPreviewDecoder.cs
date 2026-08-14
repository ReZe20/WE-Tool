using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WE_Tool.Service;

/// <summary>GIF 全帧缓存模型:帧像素存于页面级原生内存(VirtualAlloc),VirtualFree 立即归还操作系统 ——
/// 解决 .NET 大对象堆(LOH)与进程默认堆(AllocHGlobal)释放后不归还导致的任务管理器内存虚高。
/// 引用计数:缓存与播放会话各持有一份引用,归零时释放原生内存。</summary>
public sealed class GifFrames : IDisposable
{
    private nint _block; // 整张 GIF 全部帧的连续原生块(一次 VirtualAlloc)
    private int _refs;   // 0 起:Put(缓存)/Register(会话)各自 AddRef,归零时 Free 原生内存

    public required int[] DelaysMs { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int FrameBytes { get; init; } // 每帧 w*h*4

    /// <summary>帧数</summary>
    public int Count => _frameCount;

    private int _frameCount;

    /// <summary>内存估算(全部帧)</summary>
    public long MemoryBytes => (long)_frameCount * FrameBytes;

    private const uint MEM_COMMIT_RESERVE = 0x1000 | 0x2000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint MEM_RELEASE = 0x8000;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(nint lpAddress, nuint dwSize, uint dwFreeType);

    public static GifFrames Create(IReadOnlyList<byte[]> managedFrames, int[] delaysMs, int width, int height)
    {
        int frameBytes = width * height * 4;
        int count = managedFrames.Count;
        // 整张 GIF 一个连续块:一次 VirtualAlloc,释放时一次 VirtualFree 立即归还 OS
        nint block = VirtualAlloc(0, (nuint)((long)count * frameBytes), MEM_COMMIT_RESERVE, PAGE_READWRITE);
        if (block == 0)
            throw new OutOfMemoryException("VirtualAlloc 失败");

        for (int i = 0; i < count; i++)
            Marshal.Copy(managedFrames[i], 0, block + (nint)((long)i * frameBytes), frameBytes);

        var gif = new GifFrames
        {
            DelaysMs = delaysMs,
            Width = width,
            Height = height,
            FrameBytes = frameBytes
        };
        gif._block = block;
        gif._frameCount = count;
        return gif;
    }

    /// <summary>先分配后填充(解码器直接写原生块 —— 不再收集托管帧数组,解码过程零 LOH 分配)。
    /// 引用计数从 0 起:解码完成后由调用方 AddRef(Put/会话)持有。</summary>
    public static GifFrames CreateForDecode(int count, int width, int height, int[] delaysMs)
    {
        int frameBytes = width * height * 4;
        nint block = VirtualAlloc(0, (nuint)((long)frameBytes * count), MEM_COMMIT_RESERVE, PAGE_READWRITE);
        if (block == 0) throw new OutOfMemoryException("VirtualAlloc 失败");

        var gif = new GifFrames
        {
            DelaysMs = delaysMs,
            Width = width,
            Height = height,
            FrameBytes = frameBytes
        };
        gif._block = block;
        gif._frameCount = count;
        return gif;
    }

    /// <summary>解码临时缓冲分配(VirtualAlloc 页面级;给 GifPreviewDecoder 用,解码完 FreeNative 归还)</summary>
    internal static nint AllocNative(int bytes)
        => VirtualAlloc(0, (nuint)bytes, MEM_COMMIT_RESERVE, PAGE_READWRITE);

    /// <summary>解码临时缓冲释放(立即归还 OS)</summary>
    internal static void FreeNative(nint ptr)
        => VirtualFree(ptr, 0, MEM_RELEASE);

    /// <summary>解码器写入第 index 帧(源为托管数组,如 WIC 像素输出)</summary>
    public void WriteFrame(int index, byte[] source)
        => Marshal.Copy(source, 0, _block + (nint)((long)index * FrameBytes), FrameBytes);

    /// <summary>解码器写入第 index 帧(源为原生画布指针,memcpy)</summary>
    public unsafe void WriteFrameNative(int index, byte* source)
        => System.Buffer.MemoryCopy(source, (byte*)(_block + (nint)((long)index * FrameBytes)), FrameBytes, FrameBytes);

    /// <summary>拷贝指定帧到目标数组(播放换帧用;中间数组由调用方池化)</summary>
    public void CopyFrameTo(int index, byte[] destination)
        => Marshal.Copy(_block + (nint)((long)index * FrameBytes), destination, 0, FrameBytes);

    public void AddRef() => Interlocked.Increment(ref _refs);

    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0)
        {
            FreeNative();
        }
    }

    private void FreeNative()
    {
        nint block = _block;
        _block = 0;
        _frameCount = 0;
        if (block != 0)
            VirtualFree(block, 0, MEM_RELEASE); // 立即归还操作系统(页面级释放)
    }

    public void Dispose() => Release();
}

/// <summary>GIF 解码:Windows.Graphics.Imaging(WIC 的 WinRT 门面)逐帧解码 + 手动画布合成。
/// GIF 帧是增量帧(局部区域+偏移+disposal),必须按逻辑屏幕尺寸合成完整帧,否则缺失像素/颜色。
/// 后台线程池解码像素,UI 线程(调用方上下文)构建帧模型。</summary>
public static class GifPreviewDecoder
{
    /// <summary>帧延迟钳制下限:0/过小延迟的工具生成 GIF 会疯转,统一 33ms(~30fps 上限)</summary>
    private const int MinFrameDelayMs = 33;

    public static async Task<GifFrames?> DecodeAsync(string path, CancellationToken ct)
    {
        try
        {
            // ① 后台线程池:WinRT 解码 + 原生内存流式合成(零 LOH 分配 —— 画布/快照/帧块全 VirtualAlloc,
            // 解码完成即 VirtualFree 归还;方案 B:无缓存滚动时不再有 LOH 积累)
            var result = await Task.Run<(GifFrames? Frames, int[] Delays, int W, int H)?>(async () =>
            {
                using var stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read).AsTask(ct);
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
                uint count = decoder.FrameCount;
                if (count == 0) return null;

                int logicalW = (int)decoder.PixelWidth;   // 逻辑屏幕尺寸(GIF 画布)
                int logicalH = (int)decoder.PixelHeight;
                if (logicalW <= 0 || logicalH <= 0) return null;

                var meta = ReadGifMeta(path); // 帧延迟/偏移/disposal 手动解析(WinRT 投影无 Duration/偏移)
                var delays = new int[count];
                int frameBytes = logicalW * logicalH * 4;

                // 原生画布与快照(解码完 VirtualFree 立即归还 OS,不产生托管大数组)
                nint canvas = GifFrames.AllocNative(frameBytes);
                if (canvas == 0) throw new OutOfMemoryException("VirtualAlloc 失败");
                nint snapshot = 0;
                GifFrames? gif = null;
                try
                {
                    gif = GifFrames.CreateForDecode((int)count, logicalW, logicalH, delays);
                    int prevD = 0, prevL = 0, prevT = 0, prevW = 0, prevH = 0;

                    for (uint i = 0; i < count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var frame = await decoder.GetFrameAsync(i).AsTask(ct);
                        var provider = await frame.GetPixelDataAsync(
                            BitmapPixelFormat.Bgra8,
                            BitmapAlphaMode.Premultiplied,
                            new BitmapTransform(),
                            ExifOrientationMode.IgnoreExifOrientation,
                            ColorManagementMode.DoNotColorManage).AsTask(ct);
                        var pixels = provider.DetachPixelData(); // 单帧数组(池化复用,不向 LOH 反复申请)

                        int fw = (int)frame.PixelWidth, fh = (int)frame.PixelHeight;
                        int l, t, d;
                        if (meta != null && i < meta.Value.Delays.Length && i < meta.Value.Rects.Length)
                        {
                            delays[i] = (int)Math.Clamp(meta.Value.Delays[i], MinFrameDelayMs, int.MaxValue);
                            (l, t, _, _, d) = meta.Value.Rects[i];
                        }
                        else
                        {
                            delays[i] = MinFrameDelayMs; // 解析失败退化:全屏直拼 + 统一 33ms
                            l = t = d = 0;
                        }

                        unsafe
                        {
                            byte* cp = (byte*)canvas;
                            // 上一帧显示后的处置(渲染本帧前画布已处于"上一帧显示完毕"状态)
                            if (prevD == 2) ClearRectNative(cp, prevL, prevT, prevW, prevH, logicalW, logicalH);
                            else if (prevD == 3 && snapshot != 0)
                                System.Buffer.MemoryCopy((byte*)snapshot, cp, frameBytes, frameBytes);

                            // 本帧 disposal==3:显示前保存快照(用于本帧显示后的恢复)
                            if (d == 3)
                            {
                                if (snapshot == 0)
                                    snapshot = GifFrames.AllocNative(frameBytes);
                                System.Buffer.MemoryCopy(cp, (byte*)snapshot, frameBytes, frameBytes);
                            }

                            // 绘制本帧(透明像素跳过,保留画布内容)
                            fixed (byte* sp = pixels)
                                BlitNative(cp, sp, l, t, fw, fh, logicalW, logicalH);

                            // 输出完整帧 → 原生帧块(零托管大数组)
                            gif.WriteFrameNative((int)i, cp);
                        }

                        prevD = d; prevL = l; prevT = t; prevW = fw; prevH = fh;
                    }

                    return (gif, delays, logicalW, logicalH);
                }
                catch
                {
                    gif?.Release(); // 取消/异常:释放已分配的原生帧块(引用 0 → Free)
                    throw;
                }
                finally
                {
                    if (canvas != 0) GifFrames.FreeNative(canvas);
                    if (snapshot != 0) GifFrames.FreeNative(snapshot);
                }
            }, ct);

            if (result == null || result.Value.Frames == null) return null;
            return result.Value.Frames; // 引用 0 返回,由调用方 Put/AddRef 持有
        }
        catch (OperationCanceledException)
        {
            return null; // 取消是正常路径(容器回收/滚动),不记日志
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GIF 解码失败: {Path}", path);
            return null;
        }
    }

    /// <summary>帧像素拷贝到画布指定偏移;alpha=0 像素跳过(GIF 透明色索引 → WIC 输出 alpha=0)。原生指针版。</summary>
    private static unsafe void BlitNative(byte* dst, byte* src, int left, int top, int srcW, int srcH, int dstW, int dstH)
    {
        for (int y = 0; y < srcH; y++)
        {
            int dy = top + y;
            if (dy < 0 || dy >= dstH) continue;
            for (int x = 0; x < srcW; x++)
            {
                int dx = left + x;
                if (dx < 0 || dx >= dstW) continue;
                int si = (y * srcW + x) * 4;
                if (src[si + 3] == 0) continue;
                int di = (dy * dstW + dx) * 4;
                dst[di] = src[si];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = 255;
            }
        }
    }

    /// <summary>清除画布区域为透明(disposal 2:恢复到背景色)。注意行清除宽度按实际区间计算,防越界。原生指针版。</summary>
    private static unsafe void ClearRectNative(byte* canvas, int left, int top, int w, int h, int logicalW, int logicalH)
    {
        int x0 = Math.Max(0, left);
        int x1 = Math.Min(logicalW, left + w);
        if (x1 <= x0) return;
        int rowBytes = (x1 - x0) * 4;
        for (int y = Math.Max(0, top); y < Math.Min(logicalH, top + h); y++)
        {
            byte* row = canvas + (long)(y * logicalW + x0) * 4;
            NativeMemory.Clear(row, (nuint)rowBytes);
        }
    }

    /// <summary>手动解析 GIF 文件:每帧延迟(GCE,1/100s→ms)、图像描述符偏移/尺寸、disposal。
    /// CsWinRT 投影的 BitmapFrame 没有 Duration/偏移属性,增量帧合成必需这些信息。
    /// 关键坑:图像描述符后可能有局部色表(LCT),不跳过则子块扫描错位 → 延迟/尺寸全错。</summary>
    private static (int[] Delays, (int L, int T, int W, int H, int D)[] Rects)? ReadGifMeta(string path)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 13 || data[0] != (byte)'G' || data[1] != (byte)'I' || data[2] != (byte)'F')
                return null;

            // Logical Screen Descriptor:偏移 10 的 flags 位 7 = 全局色表标志,低 3 位 = 色表大小指数
            bool hasGct = (data[10] & 0x80) != 0;
            int gctBytes = hasGct ? 3 * (1 << ((data[10] & 0x07) + 1)) : 0;
            int i = 13 + gctBytes;

            var delays = new List<int>();
            var rects = new List<(int L, int T, int W, int H, int D)>();
            int pendingDelay = 100, pendingDisposal = 0;
            bool havePendingGce = false;

            while (i + 1 < data.Length)
            {
                byte b = data[i];
                if (b == 0x3B) break; // trailer
                if (b == 0x2C)
                {
                    // 图像描述符:left/top/width/height 各 2 字节小端 + packed 1 字节
                    int left = data[i + 1] | (data[i + 2] << 8);
                    int top = data[i + 3] | (data[i + 4] << 8);
                    int fw = data[i + 5] | (data[i + 6] << 8);
                    int fh = data[i + 7] | (data[i + 8] << 8);
                    byte packed = data[i + 9];
                    i += 10;

                    i += 1; // LZW 最小码长
                    if ((packed & 0x80) != 0)
                        i += 3 * (1 << ((packed & 0x07) + 1)); // 局部色表:2^(N+1) 项 × 3 字节

                    while (i < data.Length) // 图像子块
                    {
                        int sz = data[i++];
                        if (sz == 0) break;
                        i += sz;
                    }

                    delays.Add(havePendingGce ? pendingDelay : 100);
                    rects.Add((left, top, fw, fh, havePendingGce ? pendingDisposal : 0));
                    havePendingGce = false;
                }
                else if (b == 0x21)
                {
                    byte label = data[i + 1];
                    i += 2;
                    while (i < data.Length) // 扩展块子块
                    {
                        int sz = data[i++];
                        if (sz == 0) break;
                        if (label == 0xF9 && sz >= 4 && i + 2 < data.Length)
                        {
                            // GCE:packed(1) + delay(2 小端,1/100s) + 透明色索引(1)
                            byte packed = data[i];
                            pendingDelay = (data[i + 1] | (data[i + 2] << 8)) * 10;
                            pendingDisposal = (packed >> 2) & 0x07;
                            havePendingGce = true;
                        }
                        i += sz;
                    }
                }
                else
                {
                    break; // 异常块,放弃解析
                }
            }
            return delays.Count > 0 ? (delays.ToArray(), rects.ToArray()) : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>GIF 帧缓存:按预览路径缓存 GifFrames(原生内存),引用计数管理。
/// 缓存=活动会话模型:该路径无活动会话时立即移除并释放原生内存 → 内存严格跟随可见容器数。</summary>
public sealed class GifFrameCache
{
    private const long BudgetBytes = 512L * 1024 * 1024; // 兜底预算(活动帧不淘汰,仅防异常残留)

    private readonly Dictionary<string, GifFrames> _map = [];
    private readonly LinkedList<string> _lru = []; // 头 = 最近使用
    private readonly Func<string, bool> _isPathActive;
    private long _bytes;

    public GifFrameCache(Func<string, bool> isPathActive) => _isPathActive = isPathActive;

    /// <summary>命中则移到链表头(最近使用)并返回</summary>
    public GifFrames? TryGet(string path)
    {
        if (_map.TryGetValue(path, out var frames))
        {
            _lru.Remove(path);
            _lru.AddFirst(path);
            return frames;
        }
        return null;
    }

    public void Put(string path, GifFrames frames)
    {
        if (_map.TryGetValue(path, out var old))
        {
            // 同 path 新解码帧:替换旧帧(旧帧释放引用,可能仍被会话持有,由引用计数决定真正释放)
            _bytes -= old.MemoryBytes;
            _map[path] = frames;
            frames.AddRef(); // 缓存持有
            old.Release();   // 缓存释放旧帧
            _bytes += frames.MemoryBytes;
            _lru.Remove(path);
            _lru.AddFirst(path);
            return;
        }
        _map[path] = frames;
        frames.AddRef(); // 缓存持有(创建者已持 1 份,这里再持 1 份 → 缓存与解码上下文解耦)
        _bytes += frames.MemoryBytes;
        _lru.AddFirst(path);
        // 注意:不在 Put 里 Evict —— 此刻帧尚未 Register(会话未注册 = 判为非活动),
        // Evict 会把刚解码的帧当垃圾立即淘汰,新帧永远进不了缓存
    }

    /// <summary>播放会话 Stop 后调用,补淘汰之前因活动跳过而超预算的条目</summary>
    public void Trim() => Evict();

    /// <summary>移除指定路径的缓存帧(该路径无活动会话时调用 → 原生内存立即释放 → 任务管理器内存回落)</summary>
    public void Remove(string path)
    {
        if (_map.Remove(path, out var frames))
        {
            _bytes -= frames.MemoryBytes;
            _lru.Remove(path);
            frames.Release(); // 缓存释放 → 若会话已全部停止,引用归零 → 原生内存 Free
        }
    }

    /// <summary>清空全部缓存(关闭动图时释放帧内存)</summary>
    public void ClearAll()
    {
        foreach (var frames in _map.Values) frames.Release();
        _map.Clear();
        _lru.Clear();
        _bytes = 0;
    }

    /// <summary>移除不在保留集合中的条目(列表变化后释放已离开列表的壁纸帧)</summary>
    public void RemoveExcept(HashSet<string> keep)
    {
        List<string>? toRemove = null;
        foreach (var key in _map.Keys)
            if (!keep.Contains(key))
                (toRemove ??= []).Add(key);
        if (toRemove == null) return;
        foreach (var key in toRemove)
        {
            Remove(key);
            DiagRemoveCount++;
        }
    }

    /// <summary>[内存诊断] RemoveExcept 移除计数(Stop 之外的移除路径)</summary>
    public int DiagRemoveCount;

    /// <summary>[内存诊断] 帧缓存当前占用字节</summary>
    public long Bytes => _bytes;

    /// <summary>[内存诊断] 帧缓存条数</summary>
    public int Count => _map.Count;

    /// <summary>[内存诊断] 缓存 path 列表</summary>
    public IEnumerable<string> Paths() => _map.Keys;

    /// <summary>超预算时从尾部(最久未用)淘汰,播放中的条目跳过</summary>
    private void Evict()
    {
        while (_bytes > BudgetBytes && _lru.Count > 0)
        {
            var node = _lru.Last;
            while (node != null && _isPathActive(node.Value)) node = node.Previous;
            if (node == null) break; // 全部在播,等 Stop 后 Trim 补淘汰
            if (_map.Remove(node.Value, out var frames))
            {
                _bytes -= frames.MemoryBytes;
                _lru.Remove(node);
                frames.Release();
            }
        }
    }
}

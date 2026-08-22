using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WE_Tool.Controls;

/// <summary>Skia 流式 GIF 播放控件:
/// SKCodec(Skia 引擎,与 Flutter 同源)按帧延迟流式解码 + SKXamlCanvas 绘制。
/// 帧不驻留(仅当前帧 SKBitmap ~0.3MB),内存远小于批量解码;解码在共享时钟 Tick 内(UI 线程,小图 ~1ms/帧)。
/// 共享 DispatcherQueueTimer 驱动所有实例(按各自帧延迟推进),避免每卡片一个定时器。</summary>
public sealed partial class SkiaGifView : SKXamlCanvas
{
    private SKCodec? _codec;
    private SKBitmap? _frame;
    private int _frameIndex;
    private long _nextTickMs;

    /// <summary>是否正在播放(幂等 Start 判断用)</summary>
    public bool IsPlaying { get; private set; }
    /// <summary>当前播放的 GIF 路径(同 path 重绑跳过,避免重复打开)</summary>
    public string? CurrentPath { get; private set; }

    private static readonly List<SkiaGifView> _instances = [];
    private static DispatcherQueueTimer? _timer;

    public SkiaGifView()
    {
        PaintSurface += OnPaint;
        SizeChanged += (_, _) => Invalidate();
        Loaded += (_, _) => Invalidate(); // 挂载后强制首次重绘(渲染表面初始化)
        Unloaded += (_, _) => Stop(); // 容器销毁/回收:自停,防共享时钟空转
    }

    /// <summary>打开 GIF 并开始播放(替换 BitmapImage 直播路径);同 path 正在播则忽略</summary>
    public void Start(string path)
    {
        if (IsPlaying && CurrentPath == path) return;
        Stop();
        _codec = SKCodec.Create(path);
        if (_codec == null)
        {
            Log.Warning("[Skia][Start] SKCodec 打开失败: {Path}", path);
            return;
        }
        _frame = new SKBitmap(new SKImageInfo(_codec.Info.Width, _codec.Info.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul));
        _frameIndex = 0;
        _nextTickMs = 0;
        IsPlaying = true;
        CurrentPath = path;
        _instances.Add(this);
        EnsureTimer();
        DecodeFrame();
        Invalidate();
    }

    /// <summary>停止并释放(滚出视口/换绑/软挂起时调用)</summary>
    public void Stop()
    {
        if (_instances.Remove(this) && _instances.Count == 0)
        {
            _timer?.Stop();
            _timer = null;
        }
        _codec?.Dispose();
        _codec = null;
        _frame = null;
        IsPlaying = false;
        CurrentPath = null;
        Invalidate();
    }

    /// <summary>全部停止(页面离开/AutoPlayGif 关闭/软挂起)</summary>
    public static void StopAll()
    {
        foreach (var v in _instances.ToList()) v.Stop();
    }

    private static void EnsureTimer()
    {
        if (_timer != null) return;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) =>
        {
            long now = Environment.TickCount64;
            foreach (var v in _instances.ToList())
                v.Tick(now);
        };
        _timer.Start();
    }

    private void Tick(long now)
    {
        if (_codec == null || _frame == null) return;
        if (now < _nextTickMs) return;
        var info = _codec.FrameInfo;
        if (_frameIndex >= _codec.FrameCount) _frameIndex = 0;
        int duration = _frameIndex < info.Length ? Math.Max(1, info[_frameIndex].Duration) : 100;
        _nextTickMs = now + duration;
        DecodeFrame();
        _frameIndex++;
        Invalidate();
    }

    private void DecodeFrame()
    {
        if (_codec == null || _frame == null) return;
        var opts = new SKCodecOptions { FrameIndex = _frameIndex };
        // GIF 帧间合成依赖:必须指定前一帧(环绕时用最后一帧),否则依赖帧 GetPixels 返回 InvalidParameters
        opts.PriorFrame = _frameIndex == 0 ? _codec.FrameCount - 1 : _frameIndex - 1;
        var result = _codec.GetPixels(_frame.Info, _frame.GetPixels(), opts);
        if (result != SKCodecResult.Success)
            Log.Debug("[Skia][Decode] 失败 帧={Index} 结果={Result}", _frameIndex, result); // 失败诊断(保留)
    }

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear();
        if (_frame == null) return;
        var dest = new SKRect(0, 0, e.Info.Width, e.Info.Height); // 渲染表面像素尺寸(ActualWidth 是 DIP,高 DPI 下画不满)
        // cover 居中裁剪(保持比例,与 BitmapImage UniformToFill 对齐,不变形);线性采样(小图放大不模糊)
        float scale = Math.Max(dest.Width / _frame.Width, dest.Height / _frame.Height);
        float srcW = dest.Width / scale, srcH = dest.Height / scale;
        var src = new SKRect((_frame.Width - srcW) / 2, (_frame.Height - srcH) / 2,
            (_frame.Width + srcW) / 2, (_frame.Height + srcH) / 2);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        canvas.DrawBitmap(_frame, src, dest, sampling);
    }
}

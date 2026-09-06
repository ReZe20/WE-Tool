using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;

namespace WE_Tool.Helper;

/// <summary>
/// [2026-09] 预览内容过滤(高斯模糊)共享服务:Papers 页卡片/详情大图 + 属性窗口 preview 共用。
/// 从 Papers.xaml.cs 提取静态化(Win2D GPU 高斯模糊:加载原图 → 降采样到 480 内 → GaussianBlurEffect
/// → PNG 流 → BitmapImage),模糊位图按预览路径缓存,GPU 生成一次全窗口复用。
/// </summary>
public static class BlurPreviewService
{
    /// <summary>模糊位图缓存(按预览路径)</summary>
    private static readonly Dictionary<string, BitmapImage> BlurCache = [];

    /// <summary>按年龄段开关判定某分级是否需要模糊(rating: everyone/questionable/mature 小写)。</summary>
    public static bool ShouldBlur(string? contentRating, bool blurEveryone, bool blurTeen, bool blurAdult)
    {
        return contentRating?.ToLower() switch
        {
            "everyone" => blurEveryone,
            "questionable" => blurTeen,
            "mature" => blurAdult,
            _ => false
        };
    }

    /// <summary>生成(或取缓存)某预览路径的高斯模糊位图。源文件不存在/生成失败返回 null。</summary>
    public static async Task<BitmapImage?> GetBlurredPreviewAsync(string previewPath)
    {
        try
        {
            if (string.IsNullOrEmpty(previewPath) || !File.Exists(previewPath)) return null;
            if (BlurCache.TryGetValue(previewPath, out var cached)) return cached;

            var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
            using var canvasBitmap = await Microsoft.Graphics.Canvas.CanvasBitmap.LoadAsync(device, previewPath);

            // 先缩放到 480 内再模糊:模糊半径按目标分辨率折算(在原图上模糊 26px 相对 1920 宽几乎不可见)
            var src = canvasBitmap.SizeInPixels;
            float scale = Math.Min(1f, 480f / Math.Max(src.Width, src.Height));
            int w = Math.Max(1, (int)(src.Width * scale));
            int h = Math.Max(1, (int)(src.Height * scale));

            var scaleEffect = new Microsoft.Graphics.Canvas.Effects.Transform2DEffect
            {
                Source = canvasBitmap,
                TransformMatrix = System.Numerics.Matrix3x2.CreateScale(scale)
            };

            // BorderEffect(clamp)让模糊在图像边缘也能采样到延伸像素,避免"中心糊边缘清晰"的不均匀
            var borderEffect = new Microsoft.Graphics.Canvas.Effects.BorderEffect
            {
                Source = scaleEffect,
                ExtendX = Microsoft.Graphics.Canvas.CanvasEdgeBehavior.Clamp,
                ExtendY = Microsoft.Graphics.Canvas.CanvasEdgeBehavior.Clamp
            };
            var blurEffect = new Microsoft.Graphics.Canvas.Effects.GaussianBlurEffect
            {
                BlurAmount = 26f,
                BorderMode = Microsoft.Graphics.Canvas.Effects.EffectBorderMode.Hard,
                Optimization = Microsoft.Graphics.Canvas.Effects.EffectOptimization.Speed,
                Source = borderEffect
            };

            using var target = new Microsoft.Graphics.Canvas.CanvasRenderTarget(device, w, h, 96);
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Microsoft.UI.Colors.Transparent);
                ds.DrawImage(blurEffect);
            }

            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await target.SaveAsync(stream, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png);
            stream.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            BlurCache[previewPath] = bmp;
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "生成预览模糊图失败: {Path}", previewPath);
            return null;
        }
    }
}

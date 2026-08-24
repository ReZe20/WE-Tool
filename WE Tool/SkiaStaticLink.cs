using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace WE_Tool;

/// <summary>
/// NativeAOT 静态链接 Skia 的 P/Invoke 重定向(路线二,2026-08-24 实测通过)。
/// 发布时 libSkiaSharp.dll 不再随包分发:937 个 sk_* 符号通过 .def 导出表直接焊进 exe 本体;
/// 这里把 SkiaSharp 托管层的 "libSkiaSharp" 解析请求重定向到进程自身模块。
/// ModuleInitializer 保证在任何 SkiaSharp P/Invoke 之前完成注册。
/// 注:SkiaSharp.Views.WinUI.Native.dll 保留官方原版(激活服务器不做融入,2026-08-24 用户决定)。
/// (日常 Debug/非 AOT 运行仍走 NuGet 的 dll,本模块只影响解析时机,无副作用)
/// </summary>
internal static class SkiaStaticLink
{
    [ModuleInitializer]
    internal static void Init()
    {
        NativeLibrary.SetDllImportResolver(typeof(SKCodec).Assembly, (name, assembly, path) =>
        {
            if (name is "libSkiaSharp" or "SkiaSharp")
                return GetModuleHandle(null); // 进程自身 = exe 本体,导出表含全部 sk_* 符号
            return IntPtr.Zero;
        });
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
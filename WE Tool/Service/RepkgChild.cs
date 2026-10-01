using System;

namespace WE_Tool.Service;

/// <summary>
/// 提取后端子模式:主程序以 <c>WE_Tool.exe --repkg batch --manifest ...</c> 自我启动一个进程,
/// 在那个进程里跑 RePKG_Re.Cli 的命令树 —— 与 RePKG_Re.exe 逐字节同一份代码,不存在第二套协议。
/// 背景:后端原先是随包的独立 AOT exe(repkg/RePKG_Re.exe,实测 9.5MB,其中约一半是与主程序
/// 重复的运行时原生代码)。2026-09-29 折进主 exe,省掉那第二份运行时;
/// 形态与 <see cref="SteamBridgeChild"/> 同一条通道(分流必须在 Application.Start 之前,
/// 否则子进程要背上整套 XAML 运行时)。
/// 进程边界是刻意保留的,不是没改完:崩溃重启循环(第二击跳壁纸、3 次上限)、Stop() 的 Kill、
/// 壁纸边界的 SetProcessWorkingSetSize 都依附于它 —— 并回 UI 进程的话,前两者失去载体,
/// 第三者会连界面内存一起吐出去。
/// </summary>
internal static class RepkgChild
{
    /// <summary>子模式开关:主程序拉起自己时带的第一个参数,其后的参数原样交给 CLI 解析</summary>
    internal const string ChildArg = "--repkg";

    internal static bool IsChildMode(string[] args) => Array.IndexOf(args, ChildArg) >= 0;

    /// <summary>剥掉开关本身,余下的交给 CLI;返回值即子进程的退出码</summary>
    internal static int Run(string[] args)
    {
        var cliArgs = new string[args.Length - 1];
        var k = 0;
        foreach (var arg in args)
        {
            if (arg == ChildArg) continue;
            cliArgs[k++] = arg;
        }

        return global::RePKG_Re.RepkgCli.Run(cliArgs);
    }
}

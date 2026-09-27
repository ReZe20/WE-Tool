using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Steamworks;
using WE_Tool.Json;

namespace WE_Tool.Service;

/// <summary>
/// Steamworks 桥接子模式:主程序以 <c>WE_Tool.exe --steam-bridge</c> 自我启动一个进程,
/// 在那个进程里注册 Steamworks(AppID 431960)。
/// 背景:Steam 客户端退出时会强制关闭以游戏 AppID 连接的进程(Wallpaper Engine 本体也会被杀),
/// 若主应用直接注册会被 Steam 连带关闭;放到子进程后,被杀的是本桥接进程,主应用存活。
/// 协议(stdin/stdout,行分隔 JSON):父进程发 {"op":"status"|"unsubscribe"|"exit",...},
/// 本进程回 {"op":...} 响应行;stdout 只输出协议,日志写文件。
/// 形态:2026-09-27 从独立工程 SteamworksBridge/(AOT 单 exe 2.89MB)折进主 exe,
/// 省下的就是那第二份运行时。分流点必须在 Application.Start 之前(Program.cs),
/// 否则这个全程常驻的空闲进程要背上整套 XAML 运行时。
/// </summary>
internal static class SteamBridgeChild
{
    /// <summary>子模式开关:主程序拉起自己时带的参数</summary>
    internal const string BridgeArg = "--steam-bridge";

    private const uint AppId = 431960; // Wallpaper Engine

    private static readonly object LogLock = new();

    /// <summary>--log-off:主程序日志级别为"关闭"(Off→Fatal)时由启动参数传入,所有文件日志静默
    /// (桥接是独立进程,读不到主程序的 LoggingLevelSwitch;含未处理异常在内全部静默,与主程序 Off 语义一致)</summary>
    private static bool _logOff;

    private static StreamReader? _stdin;
    private static StreamWriter? _stdout;

    private static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WE_Tool", "logs", "steamworks-bridge.log");

    /// <summary>日志大小上限(字节):超过后从头截断重写,防止日志无限增长</summary>
    private const long MaxLogSize = 5 * 1024 * 1024;

    internal static bool IsBridgeMode(string[] args) => Array.IndexOf(args, BridgeArg) >= 0;

    internal static int Run(string[] args)
    {
        _logOff = Array.IndexOf(args, "--log-off") >= 0;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log($"未处理异常: {e.ExceptionObject}");
            Environment.Exit(1);
        };

        Log($"桥接子模式启动 (PID {Environment.ProcessId})");

        // 主程序以管道拉起才有 stdin/stdout;手动带参数跑同一个 exe 时没有管道,
        // 直接退出,免得留一个读不到命令、又不会自己结束的常驻进程
        if (!Console.IsInputRedirected || !Console.IsOutputRedirected)
        {
            Log("stdin/stdout 未重定向:桥接子模式只能由主程序以管道拉起,本次退出");
            return 1;
        }
        _stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        _stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));

        if (!InitSteamworks())
            return 1;

        try
        {
            // 清掉 InitSteamworks 期间原生层(steam_api64)写入 stdin 的调试残留,
            // 否则父进程第一条命令会与残留拼行被误读(见 DrainStdinResidue 注释)
            DrainStdinResidue();
            RunLoop();
            return 0;
        }
        finally
        {
            try { SteamClient.Shutdown(); } catch { }
            Log("桥接子模式退出");
        }
    }

    private static bool InitSteamworks()
    {
        try
        {
            SteamClient.Init(AppId, asyncCallbacks: true);
            Log($"Steamworks 初始化成功,用户: {SteamClient.Name} (SteamID: {SteamClient.SteamId})");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Steamworks 初始化失败: {ex.Message}");
            return false;
        }
    }

    private static void RunLoop()
    {
        string? line;
        while ((line = _stdin!.ReadLine()) != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                switch (root.GetProperty("op").GetString())
                {
                    case "status":
                        Reply(JsonSerializer.Serialize(new StatusReply(
                            "status", true, SteamClient.Name, SteamClient.SteamId.ToString()),
                            BridgeJsonContext.Default.StatusReply));
                        break;

                    case "unsubscribe":
                        var ok = Unsubscribe(root.GetProperty("workshopId").GetString());
                        Reply(JsonSerializer.Serialize(new UnsubscribeReply("unsubscribe", ok),
                            BridgeJsonContext.Default.UnsubscribeReply));
                        break;

                    case "exit":
                        return;

                    default:
                        // 未知 op:回 error,让父进程明确感知(不静默)
                        Reply(JsonSerializer.Serialize(new ErrorReply("error",
                                $"未知 op: {root.GetProperty("op").GetString()}"),
                            BridgeJsonContext.Default.ErrorReply));
                        break;
                }
            }
            catch (JsonException)
            {
                // 非 JSON 脏行:进程启动时原生层(steam_api64)可能向 stdin 写入调试残留,
                // 或管道缓冲错位。这类行不是任何命令的响应,静默丢弃不 Reply,
                // 否则父进程会把 error 当成第一条命令的响应(曾导致取消订阅误报 KeyNotFound)。
                Log($"忽略非 JSON 输入行: {Truncate(line)}");
            }
            catch (Exception ex)
            {
                Log($"请求处理异常: {ex}");
                Reply(JsonSerializer.Serialize(new ErrorReply("error", ex.Message),
                    BridgeJsonContext.Default.ErrorReply));
            }
        }
    }

    private static string Truncate(string s, int max = 200)
        => s.Length <= max ? s : s.Substring(0, max) + "...";

    /// <summary>
    /// 清空 stdin 中已缓冲的无换行残留。根因:SteamClient.Init 时原生层(steam_api64)
    /// 会向 stdin 写入一段无换行的调试残留(0xE9 开头);若不清掉,父进程发来的第一条命令
    /// 会与残留拼成一行被 ReadLine 一次读走,导致第一条命令丢失/误报 error。
    /// PeekNamedPipe 只读"已就绪"字节,不阻塞等待真实命令。
    /// </summary>
    private static void DrainStdinResidue()
    {
        try
        {
            nint handle = GetStdHandle(-10 /* STD_INPUT_HANDLE */);
            if (handle == nint.Zero || handle == new nint(-1)) return;
            var buf = new byte[8192];
            while (true)
            {
                if (!PeekNamedPipe(handle, null, 0, out _, out uint available, out _))
                    break; // 非管道(如调试器直连控制台)或错误,跳过
                if (available == 0) break;
                uint toRead = Math.Min(available, (uint)buf.Length);
                if (!ReadFile(handle, buf, toRead, out uint read, nint.Zero) || read == 0)
                    break;
                Log($"清空 stdin 残留: {read} 字节");
            }
        }
        catch
        {
            // 清残留失败不影响主循环(残留由 RunLoop 的 Parse 跳过兜底)
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekNamedPipe(
        nint hNamedPipe, byte[]? lpBuffer, uint nBufferSize,
        out uint lpBytesRead, out uint lpTotalBytesAvail, out uint lpBytesLeftThisMessage);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        nint hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, nint lpOverlapped);

    private static bool Unsubscribe(string? workshopId)
    {
        if (!ulong.TryParse(workshopId, out var wid))
            return false;
        try
        {
            var item = new Steamworks.Ugc.Item(wid);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            return item.Unsubscribe().WaitAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Log($"取消订阅超时: WorkshopID={wid}");
            return false;
        }
        catch (Exception ex)
        {
            Log($"取消订阅异常: {ex.Message}");
            return false;
        }
    }

    private static void Reply(string json)
    {
        // 响应行 flush 保证父进程及时读到
        _stdout!.WriteLine(json);
        _stdout.Flush();
    }

    private static void Log(string message)
    {
        if (_logOff) return; // 主程序要求关闭日志:全部静默(见 _logOff 注释)
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                // FileInfo.Length 对不存在的文件抛 FileNotFoundException,而这里是 Log() 里第一处
                // 磁盘访问:不先判存在就必然抛出、被外层 catch 吞掉,导致日志文件永远创建不出来
                var log = new FileInfo(LogPath);
                if (log.Exists && log.Length > MaxLogSize)
                    File.WriteAllText(LogPath, string.Empty);
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{message.Split(' ')[0]}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不影响桥接功能
        }
    }
}

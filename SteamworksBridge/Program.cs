using System.Text.Json;
using Steamworks;
using Steamworks.Ugc;

namespace SteamworksBridge;

/// <summary>
/// Steamworks 桥接子进程:在独立进程里注册 Steamworks(AppID 431960)。
/// 背景:Steam 客户端退出时会强制关闭以游戏 AppID 连接的进程(Wallpaper Engine 本体也会被杀),
/// 若主应用直接注册会被 Steam 连带关闭;放到子进程后,被杀的是本桥接进程,主应用存活。
/// 协议(stdin/stdout,行分隔 JSON):父进程发 {"op":"status"|"unsubscribe"|"exit",...},
/// 本进程回 {"op":...} 响应行;stdout 只输出协议,日志写文件。
/// </summary>
internal static class Program
{
    private const uint AppId = 431960; // Wallpaper Engine

    private static readonly object LogLock = new();

    /// <summary>--log-off:主程序日志级别为"关闭"(Off→Fatal)时由启动参数传入,所有文件日志静默
    /// (桥接是独立进程,读不到主程序的 LoggingLevelSwitch;含未处理异常在内全部静默,与主程序 Off 语义一致)</summary>
    private static bool _logOff;

    private static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WE_Tool", "logs", "steamworks-bridge.log");

    /// <summary>日志大小上限(字节):超过后从头截断重写,防止日志无限增长</summary>
    private const long MaxLogSize = 5 * 1024 * 1024;

    private static int Main(string[] args)
    {
        _logOff = args.Contains("--log-off");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log($"未处理异常: {e.ExceptionObject}");
            Environment.Exit(1);
        };

        Log($"SteamworksBridge 启动 (PID {Environment.ProcessId})");
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
            Log("SteamworksBridge 退出");
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
        while ((line = Console.ReadLine()) != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                switch (root.GetProperty("op").GetString())
                {
                    case "status":
                        Reply(JsonSerializer.Serialize(new
                        {
                            op = "status",
                            ok = true,
                            user = SteamClient.Name,
                            steamId = SteamClient.SteamId.ToString(),
                        }));
                        break;

                    case "unsubscribe":
                        var ok = Unsubscribe(root.GetProperty("workshopId").GetString());
                        Reply(JsonSerializer.Serialize(new { op = "unsubscribe", ok }));
                        break;

                    case "exit":
                        return;

                    default:
                        // 未知 op:回 error,让父进程明确感知(不静默)
                        Reply(JsonSerializer.Serialize(new { op = "error", message = $"未知 op: {root.GetProperty("op").GetString()}" }));
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
                Reply(JsonSerializer.Serialize(new { op = "error", message = ex.Message }));
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
            var item = new Item(wid);
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
        Console.Out.WriteLine(json);
        Console.Out.Flush();
    }

    private static void Log(string message)
    {
        if (_logOff) return; // 主程序要求关闭日志:全部静默(见 _logOff 注释)
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                // 超过上限截断重写,防止日志无限增长
                if (new FileInfo(LogPath).Length > MaxLogSize)
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

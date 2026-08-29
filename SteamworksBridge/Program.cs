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
                }
            }
            catch (Exception ex)
            {
                Log($"请求处理异常: {ex}");
                Reply(JsonSerializer.Serialize(new { op = "error", message = ex.Message }));
            }
        }
    }

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

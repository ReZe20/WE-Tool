using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using WE_Tool.Json;
using System.Threading.Tasks;

namespace WE_Tool.Service;

/// <summary>Steamworks 状态</summary>
public enum SteamworksStatus
{
    /// <summary>未启动(尚未拉起桥接进程)</summary>
    NotStarted,
    /// <summary>桥接进程存活且上次状态查询正常</summary>
    Running,
    /// <summary>启动失败/初始化失败(如 Steam 未运行)</summary>
    Failed,
    /// <summary>曾正常运行但桥接进程已退出(如 Steam 客户端关闭时被杀)</summary>
    Disconnected,
    /// <summary>用户手动关闭(Info 页"关闭 Steamworks"按钮),桥接进程已停止</summary>
    Stopped,
}

/// <summary>
/// Steamworks 桥接管理器:Steamworks 注册在独立子进程 SteamworksBridge.exe 中,
/// 通过 stdin/stdout 行 JSON 协议通信。原因:Steam 客户端退出时会强制关闭以游戏 AppID
/// 连接的进程(连 Wallpaper Engine 本体都会被关),子进程方案让被杀的是桥接进程,主应用存活。
/// </summary>
public partial class SteamWorkshopService : IDisposable
{
    private const string BridgeExeName = "SteamworksBridge.exe";

    private static SteamWorkshopService? _instance;
    private static readonly object Lock = new();

    private readonly object _ioLock = new(); // 同一时刻只有一个请求在途
    private Process? _bridge;
    private TaskCompletionSource<string>? _pendingResponse;
    private bool _disposed;
    private bool _hadGoodStatus; // 桥接进程是否曾成功响应过状态查询
    private bool _startAttempted; // 已尝试过启动(失败后需手动重试,避免每秒自动重启刷屏)
    private bool _userStopped; // 用户手动关闭(Shutdown 置位;Status 优先返回 Stopped)

    /// <summary>状态发生切换时触发(桥接启动成功/失败、桥接退出;UI 据此更新导航徽标等)</summary>
    public static event Action? StatusChanged;

    /// <summary>获取全局单例(首次调用即拉起桥接进程)</summary>
    public static SteamWorkshopService GetInstance()
    {
        if (_instance == null)
        {
            lock (Lock)
            {
                _instance ??= new SteamWorkshopService();
            }
        }
        _instance.StartBridgeIfNeeded();
        return _instance;
    }

    /// <summary>惰性启动桥接进程(仅首次;退出后需通过重试按钮 Reinitialize 重启)</summary>
    private void StartBridgeIfNeeded()
    {
        if (_startAttempted) return;
        lock (Lock)
        {
            if (_startAttempted) return;
            _startAttempted = true;
            StartBridge();
        }
    }

    /// <summary>当前状态</summary>
    public SteamworksStatus Status
    {
        get
        {
            if (_userStopped)
                return SteamworksStatus.Stopped;
            if (_bridge == null || _bridge.HasExited)
                return _hadGoodStatus ? SteamworksStatus.Disconnected : SteamworksStatus.Failed;
            return SteamworksStatus.Running;
        }
    }

    /// <summary>是否已成功初始化(兼容旧调用方)</summary>
    public bool IsAvailable => Status == SteamworksStatus.Running;

    /// <summary>最近一次状态查询返回的 Steam 用户名</summary>
    public string? UserName { get; private set; }

    /// <summary>在后台线程拉起桥接进程(Info 页加载用;进程启动很快,初始化发生在子进程内)</summary>
    public static Task InitializeOnBackground()
        => Task.Run(GetInstance);

    /// <summary>重启桥接进程(Steam 恢复后由 Info 页重试按钮调用)</summary>
    public bool Reinitialize()
    {
        lock (Lock)
        {
            _userStopped = false;
            StopBridge();
            _startAttempted = true;
            return StartBridge();
        }
    }

    /// <summary>用户手动关闭:停止桥接进程,状态置 Stopped(Info 页"关闭 Steamworks"按钮调用)。关闭后需 Reinitialize 恢复。</summary>
    public void Shutdown()
    {
        lock (Lock)
        {
            _userStopped = true;
            StopBridge();
            StatusChanged?.Invoke();
        }
    }

    /// <summary>对指定工坊 ID 执行取消订阅</summary>
    public async Task<bool> UnsubscribeAsync(ulong workshopId)
    {
        try
        {
            var response = await RequestAsync(
                JsonSerializer.Serialize(new BridgeCommand("unsubscribe", workshopId.ToString()), BridgeJsonContext.Default.BridgeCommand),
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(response);
            return doc.RootElement.GetProperty("ok").GetBoolean();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "取消订阅通信异常: WorkshopID={WorkshopId}", workshopId);
            return false;
        }
    }

    /// <summary>刷新状态(Info 页轮询用):ping 桥接进程,更新用户名</summary>
    public async Task RefreshStatusAsync()
    {
        if (_bridge == null || _bridge.HasExited)
        {
            UserName = null;
            return;
        }
        try
        {
            var response = await RequestAsync(
                JsonSerializer.Serialize(new BridgeCommand("status"), BridgeJsonContext.Default.BridgeCommand),
                TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.GetProperty("ok").GetBoolean())
            {
                UserName = doc.RootElement.TryGetProperty("user", out var u) ? u.GetString() : null;
                _hadGoodStatus = true;
            }
        }
        catch (Exception ex)
        {
            UserName = null;
            Log.Warning(ex, "读取 Steam 状态失败,用户名置空");
        }
    }

    /// <summary>App 退出时调用,结束桥接进程</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (Lock)
        {
            StopBridge();
        }
    }

    private bool StartBridge()
    {
        try
        {
            var exePath = Path.Combine(AppContext.BaseDirectory, BridgeExeName);
            if (!File.Exists(exePath))
            {
                Log.Error("SteamworksBridge.exe 缺失于 {Path},取消订阅功能不可用。", exePath);
                _hadGoodStatus = false;
                StatusChanged?.Invoke();
                return false;
            }

            var startInfo = new ProcessStartInfo(exePath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
            };
            // 桥接是独立进程,读不到主程序日志级别;主程序"关闭日志"(Off→Fatal)时以参数同步静默
            if (App.LogLevelSwitch.MinimumLevel == Serilog.Events.LogEventLevel.Fatal)
                startInfo.ArgumentList.Add("--log-off");

            var bridge = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };
            // 自包含发布(无对应 .NET 运行时的机器)时,桥接为框架依赖进程,须指向应用自带的运行时;
            // 框架依赖安装(本机装有对应 .NET)则不用设置
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "hostfxr.dll")))
                bridge.StartInfo.Environment["DOTNET_ROOT"] = AppContext.BaseDirectory;
            bridge.Exited += (_, _) =>
            {
                // 桥接进程退出(Steam 关闭时被 Steam 终止,或自身崩溃)
                Log.Warning("Steamworks 桥接进程已退出 (PID {Pid}, ExitCode {Code})",
                    bridge.Id, bridge.ExitCode);
                lock (_ioLock)
                {
                    _pendingResponse?.TrySetCanceled();
                    _pendingResponse = null;
                }
                // 仅当退出的还是当前桥接进程时通知(重试中被替换的旧进程不触发)
                if (ReferenceEquals(bridge, _bridge))
                    StatusChanged?.Invoke();
            };
            bridge.Start();
            _bridge = bridge;
            _hadGoodStatus = false;
            _ = Task.Run(() => ReadBridgeOutput(bridge));
            Log.Information("Steamworks 桥接进程已启动 (PID {Pid})", bridge.Id);
            StatusChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Steamworks 桥接进程启动失败");
            _hadGoodStatus = false;
            StatusChanged?.Invoke();
            return false;
        }
    }

    private void StopBridge()
    {
        var bridge = _bridge;
        _bridge = null;
        if (bridge == null) return;

        // 先给优雅退出机会,再强杀
        try
        {
            if (!bridge.HasExited)
            {
                bridge.StandardInput.WriteLine(JsonSerializer.Serialize(new BridgeCommand("exit"), BridgeJsonContext.Default.BridgeCommand));
                bridge.StandardInput.Flush();
                if (!bridge.WaitForExit(2000))
                    bridge.Kill();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "停止 Steamworks 桥接进程失败");
            try { bridge.Kill(); } catch { }
        }
        finally
        {
            bridge.Dispose();
            lock (_ioLock)
            {
                _pendingResponse?.TrySetCanceled();
                _pendingResponse = null;
            }
        }
    }

    private async Task ReadBridgeOutput(Process bridge)
    {
        try
        {
            while (bridge.StandardOutput.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                lock (_ioLock)
                {
                    _pendingResponse?.TrySetResult(line);
                    _pendingResponse = null;
                }
            }
        }
        catch
        {
            // 桥接进程退出导致读取中断,属预期
        }
    }

    /// <summary>发送一行请求并等待一行响应(同一时刻仅一个请求在途)</summary>
    private async Task<string> RequestAsync(string json, TimeSpan timeout)
    {
        Task<string> responseTask;
        lock (_ioLock)
        {
            if (_bridge == null || _bridge.HasExited)
                throw new InvalidOperationException("Steamworks 桥接进程未运行");

            _pendingResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            responseTask = _pendingResponse.Task;
            _bridge.StandardInput.WriteLine(json);
            _bridge.StandardInput.Flush();
        }

        using var cts = new CancellationTokenSource(timeout);
        var completed = await Task.WhenAny(responseTask, Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);
        cts.Cancel();
        if (completed != responseTask)
        {
            lock (_ioLock)
            {
                if (_pendingResponse?.Task == responseTask)
                    _pendingResponse = null;
            }
            throw new TimeoutException("Steamworks 桥接进程响应超时");
        }
        return await responseTask.ConfigureAwait(false);
    }
}

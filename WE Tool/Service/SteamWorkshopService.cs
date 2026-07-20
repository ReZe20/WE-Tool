using Serilog;
using Steamworks;
using Steamworks.Ugc;
using System;
using System.Threading.Tasks;

namespace WE_Tool.Service
{
    /// <summary>
    /// Steam 创意工坊服务（全局单例）。
    /// Init 只调用一次，Shutdown 随 App 退出自动执行。
    /// 所有 Steam async API 调用使用 ConfigureAwait(false) 避免与 WinUI SynchronizationContext 冲突。
    /// </summary>
    public class SteamWorkshopService : IDisposable
    {
        private static SteamWorkshopService? _instance;
        private static readonly object _lock = new();

        private bool _initialized;
        private bool _disposed;

        private SteamWorkshopService() { }

        /// <summary>获取全局单例（同时初始化 Steamworks）</summary>
        public static SteamWorkshopService GetInstance()
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new SteamWorkshopService();
                        if (!_instance.Init())
                            Log.Warning("SteamWorkshopService 初始化失败，取消订阅功能不可用。");
                    }
                }
            }
            return _instance;
        }

        /// <summary>是否已成功初始化</summary>
        public bool IsAvailable => _initialized;

        /// <summary>仅内部调用一次的初始化</summary>
        private bool Init()
        {
            try
            {
                // 注意：asyncCallbacks: true 会设置全局 SynchronizationContext。
                // WinUI 已有自己的 DispatcherQueueSynchronizationContext，
                // 因此在调用方使用 ConfigureAwait(false) 让延续在 ThreadPool 上运行。
                SteamClient.Init(431960, asyncCallbacks: true);
                _initialized = true;
                Log.Information($"Steamworks 初始化成功，用户: {SteamClient.Name} (SteamID: {SteamClient.SteamId})");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Steamworks 初始化失败，请确认 Steam 已在运行。");
                return false;
            }
        }

        /// <summary>对指定工坊 ID 执行取消订阅</summary>
        public async Task<bool> UnsubscribeAsync(ulong workshopId)
        {
            if (!_initialized) return false;

            try
            {
                var item = new Item(workshopId);
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
                // ConfigureAwait(false) 防止延续回到 WinUI UI 线程，避免 SynchronizationContext 冲突
                bool success = await item.Unsubscribe().WaitAsync(cts.Token).ConfigureAwait(false);
                Log.Information($"取消订阅 {(success ? "成功" : "失败")}: WorkshopID={workshopId}");
                return success;
            }
            catch (OperationCanceledException)
            {
                Log.Warning($"取消订阅超时: WorkshopID={workshopId}");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"取消订阅异常: WorkshopID={workshopId}");
                return false;
            }
        }

        /// <summary>App 退出时调用，释放 Steamworks 原生资源</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _initialized = false;

            try
            {
                SteamClient.Shutdown();
                Log.Information("Steamworks 已关闭");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Steamworks 关闭时出现异常");
            }

            lock (_lock)
            {
                _instance = null;
            }
            GC.SuppressFinalize(this);
        }
    }
}

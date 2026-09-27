using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Serilog;
using WE_Tool.Helper;
using WE_Tool.Json;
using WE_Tool.ViewModels;

namespace WE_Tool.Service
{
    /// <summary>
    /// 白名单副窗口的母进程侧宿主:同 exe 自我启动一个子进程显示白名单窗口。
    /// 与属性副窗口的关键差别是**写权归属**——原实现把母进程的 HashSet 引用直接交给窗口,
    /// 删条目就地改集合并由窗口落盘;出进程后没有共享引用,改成
    /// "子窗口只报意图(removed),母进程改集合 + 写 cleanup_whitelist.json + 把壁纸退回清理列表"。
    /// 这样文件仍然只有一个写者,不必引入跨进程锁(本仓库原本一个同步原语都没有)。
    /// 反向同理:清理页加入白名单时母进程推 add 给子窗口增量加卡。
    /// </summary>
    public static class WhitelistWindowHost
    {
        /// <summary>子窗口请求移除某 ID(在 UI 线程上触发),页面据此改集合、写文件、把壁纸退回清理列表。
        /// 用赋值而不是 += 事件:多张 Cleanup 页会同时活在 Frame 的导航记录里,广播会让每张页都写一次文件,
        /// 还会把打开过窗口的每张页都钉住不放;赋值=最后一次打开窗口的那张页接管。</summary>
        public static Action<string>? OnItemRemoved { get; set; }

        /// <summary>子进程已退出(在 UI 线程上触发),页面据此整表重扫——对应原先的 Window.Closed。</summary>
        public static Action? OnClosed { get; set; }

        private static readonly object _gate = new();
        private static Process? _process;
        private static PropertyWindowChannel? _channel;
        private static DispatcherQueue? _uiQueue;
        private static SettingsViewModel? _hookedVm;
        private static string _payloadPath = "";

        public static bool IsOpen
        {
            get { lock (_gate) return _process != null; }
        }

        public static async Task OpenAsync(SettingsViewModel viewModel, IEnumerable<string> entries, string workshopPath)
        {
            _uiQueue ??= DispatcherQueue.GetForCurrentThread();
            HookTheme(viewModel);

            lock (_gate)
            {
                if (_process != null)
                {
                    Send(new PropertyWindowMessage { Kind = PropertyWindowLink.KindFocus });
                    return;
                }
            }

            var settings = await new ConfigService().LoadAsync().ConfigureAwait(false);
            var snapshot = new WhitelistWindowSnapshot
            {
                Protocol = PropertyWindowLink.Protocol,
                PipeName = PropertyWindowLink.NewPipeName(),
                Language = Windows.Globalization.ApplicationLanguages.Languages.FirstOrDefault() ?? "",
                LogLevel = settings.LogLevel ?? "",
                Theme = viewModel.AppSettingsVM.Theme ?? "",
                WorkshopPath = workshopPath,
                Entries = entries.ToList(),
            };

            string payloadPath;
            try
            {
                payloadPath = PropertyWindowLink.WritePayload(snapshot);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[白名单副窗] 写启动载荷失败");
                return;
            }

            var process = new Process { EnableRaisingEvents = true };
            try
            {
                string exePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"{PropertyWindowLink.SwitchWhitelist} \"{payloadPath}\"",
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
                };
                lock (_gate)
                {
                    _process = process;
                    _payloadPath = payloadPath;
                }
                process.Exited += (_, _) => OnExited(process.Id);
                if (!process.Start())
                {
                    lock (_gate) _process = null;
                    Log.Error("[白名单副窗] 子进程启动失败");
                    TryDelete(payloadPath);
                    return;
                }
                JobObjectManager.AddProcess(process.Handle);
                Log.Information("[白名单副窗] 子进程已启动: Pid={Pid} Entries={Count}", process.Id, snapshot.Entries.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[白名单副窗] 启动子进程异常");
                lock (_gate) _process = null;
                TryDelete(payloadPath);
                return;
            }

            string pipeName = snapshot.PipeName;
            _ = Task.Run(async () =>
            {
                var channel = await PropertyWindowChannel
                    .ConnectAsClientAsync(pipeName, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                if (channel == null)
                {
                    Log.Warning("[白名单副窗] 与子进程管道未建成,主题/增删同步不可用: Pid={Pid}", process.Id);
                    return;
                }
                // 连上时窗口可能已经关了(子进程秒退):别把通道挂到一个已死的进程上
                lock (_gate)
                {
                    if (_process == null) { channel.Dispose(); return; }
                    _channel = channel;
                }
                channel.MessageReceived += OnMessage;
            });
        }

        /// <summary>清理页把某 ID 加入白名单后通知子窗口增量加卡。</summary>
        public static void NotifyAdded(string id) =>
            Send(new PropertyWindowMessage { Kind = PropertyWindowLink.KindAdd, EntryId = id });

        private static void OnMessage(PropertyWindowMessage message)
        {
            if (message.Kind != PropertyWindowLink.KindRemoved || string.IsNullOrEmpty(message.EntryId)) return;
            string id = message.EntryId!;
            Log.Information("[白名单副窗] 收到移除请求: {Id}", id);
            _uiQueue?.TryEnqueue(() => OnItemRemoved?.Invoke(id));
        }

        private static void OnExited(int pid)
        {
            PropertyWindowChannel? channel;
            lock (_gate)
            {
                if (_process?.Id != pid) return; // 迟到的旧进程退出事件,忽略
                channel = _channel;
                _channel = null;
                _process = null;
                TryDelete(_payloadPath);
                _payloadPath = "";
            }
            channel?.Dispose();
            Log.Information("[白名单副窗] 子进程已退出: Pid={Pid}", pid);
            _uiQueue?.TryEnqueue(() => OnClosed?.Invoke());
        }

        /// <summary>订阅一次主题变化推给子窗口。副窗口收不到就停在打开时的主题。</summary>
        private static void HookTheme(SettingsViewModel viewModel)
        {
            if (_hookedVm != null) return;
            _hookedVm = viewModel;
            viewModel.AppSettingsVM.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AppSettingsViewModel.Theme))
                    Send(new PropertyWindowMessage
                    {
                        Kind = PropertyWindowLink.KindTheme,
                        Theme = viewModel.AppSettingsVM.Theme ?? "",
                    });
            };
        }

        /// <summary>管道写可能在对方不读时阻塞,一律离开 UI 线程。</summary>
        private static void Send(PropertyWindowMessage message)
        {
            PropertyWindowChannel? channel;
            lock (_gate) channel = _channel;
            if (channel == null) return;
            _ = Task.Run(() => channel.Send(message));
        }

        private static void TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { File.Delete(path); } catch { }
        }
    }
}

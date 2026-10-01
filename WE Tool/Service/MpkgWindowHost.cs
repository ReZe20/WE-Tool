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
using WE_Tool.Models;
using WE_Tool.ViewModels;

namespace WE_Tool.Service
{
    /// <summary>
    /// 移动版队列副窗口的母进程侧宿主:同 exe 自我启动一个子进程,把那整块待转面板搬到桌面上。
    /// 与属性/白名单副窗口的实质差别是<b>队列归谁</b>:
    /// detach 之后队列由子进程持有,编辑/探测/开转都在它那边跑,母进程只留一份"存回的副本"。
    /// 所以这里没有双向同步,只有三条单向通道 —— 发新入队的行、收副本、收"贴回"那一次(带 Dock,母进程要重开页面面板)。
    ///
    /// 副本的用场只有一个:子进程被打掉(崩溃、母进程退出被 Job Object 带走)时,这批待转项不至于跟着没。
    /// </summary>
    public static class MpkgWindowHost
    {
        /// <summary>副窗口把整份队列交回来(在 UI 线程上触发)。参数=行、面板状态、是否要顺带把页面面板展开。</summary>
        public static Action<List<MpkgQueueRowDto>, MpkgPanelStateDto, bool>? OnQueueReturned { get; set; }

        /// <summary>副窗口的存在/交回变了(在 UI 线程上触发)。Papers 页据此把页面那份队列与副本对齐。</summary>
        public static Action? OnDetachedChanged { get; set; }

        private static readonly object _gate = new();
        private static Process? _process;
        private static PropertyWindowChannel? _channel;
        private static DispatcherQueue? _uiQueue;
        private static string _payloadPath = "";
        private static SettingsViewModel? _hookedVm;

        // 上一次这个副窗口的尺寸:只活在本次会话里,不写 config.json(母进程是那份配置的唯一写者)
        private static int _lastWidth, _lastHeight;

        // 副进程每次改动存回来的那份副本。DetachRequested 之后、交回之前,母进程自己不留可改的第二份。
        private static List<MpkgQueueRowDto>? _parkedRows;
        private static MpkgPanelStateDto? _parkedPanel;

        // 子进程已经把队列交回来了(进程还活着的那一小段)。缺这一道,页面会在这段窗口期里把新入队的行
        // 投给一个正在退出的子进程 —— 那边收不到,这边也没留,几张壁纸就这么静默没了。
        private static bool _returned;

        /// <summary>最后一次交回带的是哪种意图:true=贴回(页面面板要展开),false=只是收着。</summary>
        private static bool _returnDock;

        /// <summary>队列现在在副窗口里(母进程只有副本)。交回那一次一到就算"回来了",不等进程退出。</summary>
        public static bool IsDetached
        {
            get { lock (_gate) return _process != null && !_returned; }
        }

        /// <summary>副窗口那边正在转换 —— 这段时间母进程不该再开一批提取,两批 repkg 会抢同一批核。
        /// 页面面板正在自己转的时候由 Papers 那边另算(它直接读控件的 IsBusy)。</summary>
        public static bool IsChildConverting { get; private set; }

        /// <summary>把面板 detach 到独立窗口。返回 false = 子进程没起来,母进程原样继续持有队列。</summary>
        public static bool Detach(
            SettingsViewModel viewModel,
            IReadOnlyList<MpkgQueueRowDto> rows,
            MpkgPanelStateDto panel,
            string outputRoot)
        {
            _uiQueue ??= DispatcherQueue.GetForCurrentThread();

            lock (_gate)
            {
                if (_process != null)
                {
                    Send(new PropertyWindowMessage { Kind = PropertyWindowLink.KindFocus });
                    Log.Warning("[移动版副窗] 已有一个副窗口,这次不重复起: {Rows} 行仍由它持有", rows.Count);
                    return true;
                }
            }

            HookSettings(viewModel);
            string language = Windows.Globalization.ApplicationLanguages.Languages.FirstOrDefault() ?? "";
            // 日志级别与主题都来自主 VM:子进程零读 config.json,这两样是它唯一能拿到的值
            string logLevel = viewModel.LogLevel ?? "";
            string theme = viewModel.AppSettingsVM.Theme ?? "";
            int priority = viewModel.ProcessPriority;

            var snapshot = new MpkgWindowSnapshot
            {
                Protocol = PropertyWindowLink.Protocol,
                PipeName = PropertyWindowLink.NewPipeName(),
                Language = language,
                LogLevel = logLevel,
                Theme = theme,
                Width = _lastWidth,
                Height = _lastHeight,
                OutputRoot = outputRoot,
                ProcessPriority = priority,
                // 预览模糊那三档年龄段开关:子进程手上没有主 VM,只能随载荷带过去,之后经 KindTheme 那条消息再推
                BlurEveryone = viewModel.WallpaperDisplayVM.BlurEveryone,
                BlurTeen = viewModel.WallpaperDisplayVM.BlurTeen,
                BlurAdult = viewModel.WallpaperDisplayVM.BlurAdult,
                Panel = panel,
                Rows = rows.ToList(),
            };

            string payloadPath;
            try
            {
                payloadPath = PropertyWindowLink.WritePayload(snapshot);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[移动版副窗] 写启动载荷失败");
                return false;
            }

            var process = new Process { EnableRaisingEvents = true };
            try
            {
                string exePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"{PropertyWindowLink.SwitchMpkgQueue} \"{payloadPath}\"",
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
                };
                lock (_gate)
                {
                    _process = process;
                    _payloadPath = payloadPath;
                    _parkedRows = rows.ToList();
                    _parkedPanel = panel;
                    _returned = false;
                    _returnDock = false;
                    IsChildConverting = false;
                }
                process.Exited += (_, _) => OnExited(process.Id);
                if (!process.Start())
                {
                    lock (_gate) _process = null;
                    Log.Error("[移动版副窗] 子进程启动失败");
                    TryDelete(payloadPath);
                    return false;
                }
                // 母进程退出把这批子进程一起带走(与属性/白名单副窗口同一套):那一次交回可能来不及,
                // 所以队列在 detach 期间的新改动都有节流副本垫着。
                JobObjectManager.AddProcess(process.Handle);
                Log.Information("[移动版副窗] 子进程已启动: Pid={Pid} Rows={Count} 输出={Output}",
                    process.Id, snapshot.Rows.Count, outputRoot);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[移动版副窗] 启动子进程异常");
                lock (_gate) { _process = null; _parkedRows = null; _parkedPanel = null; }
                TryDelete(payloadPath);
                return false;
            }

            _uiQueue?.TryEnqueue(() => OnDetachedChanged?.Invoke());

            string pipeName = snapshot.PipeName;
            _ = Task.Run(async () =>
            {
                var channel = await PropertyWindowChannel
                    .ConnectAsClientAsync(pipeName, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                if (channel == null)
                {
                    Log.Warning("[移动版副窗] 与子进程管道未建成,主题/新入队/队列交回不可用: Pid={Pid}", process.Id);
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

            return true;
        }

        /// <summary>面板还挂着独立窗口时,页面新「转为移动版」的这几张投给子进程(母进程自己那份是空的)。</summary>
        public static void AddRows(IReadOnlyList<MpkgQueueRowDto> rows)
        {
            if (rows.Count == 0) return;
            PropertyWindowChannel? channel;
            lock (_gate) channel = _channel;
            if (channel == null)
            {
                // 冷启动那两秒里管道还没通。这不常见,而且丢了也只是那几张没进队列(界面上会看着"按了没反应"),
                // 所以不做重发队列,只把话说清楚。
                Log.Warning("[移动版副窗] 管道还没通,{Rows} 行没能投进副窗口,请重新入队", rows.Count);
                return;
            }
            Send(new PropertyWindowMessage { Kind = PropertyWindowLink.KindMpkgRows, Rows = rows.ToList() });
            Log.Information("[移动版副窗] 投递 {Rows} 行给副窗口", rows.Count);
        }

        /// <summary>让已开着的副窗口前置(页面又点「转为移动版」而队列还在它那边时)。</summary>
        public static void FocusWindow() => Send(new PropertyWindowMessage { Kind = PropertyWindowLink.KindFocus });

        private static void OnMessage(PropertyWindowMessage message)
        {
            switch (message.Kind)
            {
                case PropertyWindowLink.KindSize:
                    if (message.Width > 0 && message.Height > 0)
                    {
                        _lastWidth = message.Width;
                        _lastHeight = message.Height;
                    }
                    return;
                case PropertyWindowLink.KindMpkgBusy:
                    IsChildConverting = message.Busy;
                    Log.Information("[移动版副窗] 子进程转换状态: {State}", message.Busy ? "在转" : "空了");
                    return;
                case PropertyWindowLink.KindMpkgQueue:
                    lock (_gate)
                    {
                        // 平时那些"改动存一份"只刷新副本,不往页面上灌 —— 灌一次就把他正在编辑的面板重建一遍。
                        // 真要让页面接管的是子进程退出那一次(见 OnExited),那时才按最后一次带的 Dock 意图办。
                        _parkedRows = message.Rows ?? [];
                        _parkedPanel = message.Panel ?? new MpkgPanelStateDto();
                        _returnDock = message.Dock;
                        _returned |= message.Dock;
                    }
                    Log.Information("[移动版副窗] 收到队列: {Rows} 行, Dock={Dock}",
                        message.Rows?.Count ?? 0, message.Dock);
                    return;
            }
        }

        private static void OnExited(int pid)
        {
            PropertyWindowChannel? channel;
            List<MpkgQueueRowDto>? parked;
            MpkgPanelStateDto? panel;
            bool dock;
            lock (_gate)
            {
                if (_process?.Id != pid) return; // 迟到的旧进程退出事件,忽略
                channel = _channel;
                _channel = null;
                _process = null;
                parked = _parkedRows;
                panel = _parkedPanel;
                dock = _returnDock;
                _parkedRows = null;
                _parkedPanel = null;
                _returned = false;
                _returnDock = false;
                TryDelete(_payloadPath);
                _payloadPath = "";
            }
            channel?.Dispose();
            IsChildConverting = false;
            Log.Information("[移动版副窗] 子进程已退出: Pid={Pid} 副本 {Rows} 行, 贴回={Dock}",
                pid, parked?.Count ?? 0, dock ? "要展开页面面板" : "只收下");
            // 页面在这一整段时间里没有队列可改(detach 时就清空了),所以这一句是唯一的归还点:
            // 正常关窗=子进程最后那次交回,被打掉=最后一次节流的副本。两条都只灌一次,不会互相覆盖。
            _uiQueue?.TryEnqueue(() =>
            {
                if (parked != null) OnQueueReturned?.Invoke(parked, panel ?? new MpkgPanelStateDto(), dock);
                OnDetachedChanged?.Invoke();
            });
        }

        /// <summary>订阅主题与预览模糊开关,变化就推给子窗口。副窗口收不到就停在打开时那一份。
        /// 先减后加:Papers 页换 VM 实例时旧的那份订阅会留在树上,一次改主题就发两遍。</summary>
        private static void HookSettings(SettingsViewModel viewModel)
        {
            if (_hookedVm != null && !ReferenceEquals(_hookedVm, viewModel))
            {
                _hookedVm.AppSettingsVM.PropertyChanged -= OnSettingsChanged;
                _hookedVm.WallpaperDisplayVM.PropertyChanged -= OnSettingsChanged;
            }
            _hookedVm = viewModel;
            viewModel.AppSettingsVM.PropertyChanged -= OnSettingsChanged;
            viewModel.AppSettingsVM.PropertyChanged += OnSettingsChanged;
            // 三档模糊开关挂在显示 VM 上,不在 AppSettingsVM 里,所以两个源都要订
            viewModel.WallpaperDisplayVM.PropertyChanged -= OnSettingsChanged;
            viewModel.WallpaperDisplayVM.PropertyChanged += OnSettingsChanged;
        }

        private static void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            var vm = _hookedVm;
            if (vm == null) return;
            // 主题来自 AppSettingsVM,预览模糊那三档来自 WallpaperDisplayVM,共用同一条消息发出去
            if (e.PropertyName is not (nameof(AppSettingsViewModel.Theme)
                    or nameof(WallpaperDisplayViewModel.BlurEveryone)
                    or nameof(WallpaperDisplayViewModel.BlurTeen)
                    or nameof(WallpaperDisplayViewModel.BlurAdult))) return;
            Send(new PropertyWindowMessage
            {
                Kind = PropertyWindowLink.KindTheme,
                Theme = vm.AppSettingsVM.Theme ?? "",
                BlurEveryone = vm.WallpaperDisplayVM.BlurEveryone,
                BlurTeen = vm.WallpaperDisplayVM.BlurTeen,
                BlurAdult = vm.WallpaperDisplayVM.BlurAdult,
            });
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

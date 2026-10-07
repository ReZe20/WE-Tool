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
    /// 场景预览副窗口的母进程侧宿主:同 exe 自我启动一个子进程,把那张壁纸交给它渲染。
    ///
    /// 与属性/白名单/队列三扇窗的实质差别是<b>数据不流动</b>:预览是只读的,子进程手上那块
    /// WebView2 自己去磁盘取包,母进程只需要告诉它「哪一张」,所以这里没有交回、没有副本、
    /// 也没有第二写者问题,只有一条换壁纸的单向消息。
    ///
    /// 全局只开一扇:再点别的壁纸是往已有子进程发 preview 消息。起第二个进程要再付一份
    /// XAML 运行时加一套浏览器进程,而看到的画面并不会因此多一张。
    /// </summary>
    public static class ScenePreviewWindowHost
    {
        private static readonly object _gate = new();
        private static Process? _process;
        private static PropertyWindowChannel? _channel;
        private static DispatcherQueue? _uiQueue;
        private static string _payloadPath = "";
        private static SettingsViewModel? _hookedVm;

        // 上一次这扇窗的尺寸:只活在本次会话里,不写 config.json(母进程是那份配置的唯一写者)。
        // 按模式分开记:比例模式的尺寸是算出来的,把它当"上次尺寸"记下来会在下次开窗时把比例挤歪。
        private static readonly Dictionary<string, (int W, int H)> _sizes = new();

        // 子进程当前在哪一模式:尺寸消息里不带模式,只能由这边记着发出去的是什么
        private static string _currentMode = ScenePreviewModes.Properties;

        // 冷启动那一两秒里管道还没通,这时按「预览」的那一张先记下,连通后补发。
        // 不补的话症状是"点了没反应",而子进程其实已经在起了。
        private static string? _pendingFolder;
        private static string? _pendingTitle;
        private static string? _pendingMode;

        /// <summary>预览副窗口现在开着(母进程留着那一侧的管道)。</summary>
        public static bool IsOpen { get { lock (_gate) return _process != null; } }

        /// <summary>
        /// 打开预览或让已有的那扇换到这张壁纸/这一模式。返回 false = 子进程没起来
        /// (调用方不必兜底:预览是可选功能,失败信息已经写进日志)。
        /// </summary>
        public static bool OpenOrSwitch(SettingsViewModel viewModel, WallpaperItem item, string mode)
        {
            var folder = item.FolderPath;
            if (string.IsNullOrWhiteSpace(folder))
            {
                Log.Warning("[预览副窗] 该条目没有目录,不起子进程: {Title}", item.Title);
                return false;
            }

            _uiQueue ??= DispatcherQueue.GetForCurrentThread();

            lock (_gate)
            {
                if (_process != null)
                {
                    SendPreview(folder, item.Title ?? item.WorkshopID ?? "", mode);
                    Log.Information("[预览副窗] 已有副窗口,改发换壁纸: {Folder} 模式={Mode}", folder, mode);
                    return true;
                }
            }

            HookSettings(viewModel);
            _currentMode = mode;
            // 子进程零读 config.json:主题/日志级别/尺寸只能是母进程算好带过去的值
            var snapshot = new ScenePreviewWindowSnapshot
            {
                Protocol = PropertyWindowLink.Protocol,
                PipeName = PropertyWindowLink.NewPipeName(),
                Language = Windows.Globalization.ApplicationLanguages.Languages.FirstOrDefault() ?? "",
                LogLevel = viewModel.LogLevel ?? "",
                Theme = viewModel.AppSettingsVM.Theme ?? "",
                Mode = mode,
                Folder = folder,
                Title = item.Title ?? item.WorkshopID ?? ""
            };
            // 只有可控制属性模式用得上会话里记的上次尺寸;比例模式的尺寸由窗口自己按比例算
            if (mode == ScenePreviewModes.Properties && _sizes.TryGetValue(mode, out var remembered))
            {
                snapshot.Width = remembered.W;
                snapshot.Height = remembered.H;
            }

            string payloadPath;
            try
            {
                payloadPath = PropertyWindowLink.WritePayload(snapshot);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[预览副窗] 写启动载荷失败");
                return false;
            }

            var process = new Process { EnableRaisingEvents = true };
            try
            {
                string exePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"{PropertyWindowLink.SwitchScenePreview} \"{payloadPath}\"",
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
                    Log.Error("[预览副窗] 子进程启动失败");
                    TryDelete(payloadPath);
                    return false;
                }
                // 母进程退出把这扇窗一起带走(与另三扇窗同一套):预览没有任何东西需要交回,
                // 所以这里不需要节流的副本
                JobObjectManager.AddProcess(process.Handle);
                Log.Information("[预览副窗] 子进程已启动: Pid={Pid} Folder={Folder}", process.Id, folder);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[预览副窗] 启动子进程异常");
                lock (_gate) _process = null;
                TryDelete(payloadPath);
                return false;
            }

            string pipeName = snapshot.PipeName;
            _ = Task.Run(async () =>
            {
                var channel = await PropertyWindowChannel
                    .ConnectAsClientAsync(pipeName, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                if (channel == null)
                {
                    Log.Warning("[预览副窗] 与子进程管道未建成,换壁纸与主题跟随不可用: Pid={Pid}", process.Id);
                    return;
                }
                // 连上时窗口可能已经关了(子进程秒退):别把通道挂到一个已死的进程上
                lock (_gate)
                {
                    if (_process == null) { channel.Dispose(); return; }
                    _channel = channel;
                }
                channel.MessageReceived += OnMessage;
                FlushPendingPreview();
            });

            return true;
        }

        /// <summary>发「换到这张/这一模式」:管道还没通就先存着,连通后补发(见 _pendingFolder)。</summary>
        private static void SendPreview(string folder, string title, string mode)
        {
            lock (_gate)
            {
                _currentMode = mode;
                if (_channel is null)
                {
                    _pendingFolder = folder;
                    _pendingTitle = title;
                    _pendingMode = mode;
                    Log.Warning("[预览副窗] 管道还没通,这张先记下等连通后补发: {Folder}", folder);
                    return;
                }
            }
            Send(new PropertyWindowMessage
            {
                Kind = PropertyWindowLink.KindPreview,
                Folder = folder,
                PreviewTitle = title,
                Mode = mode
            });
        }

        private static void FlushPendingPreview()
        {
            string? folder, title, mode;
            lock (_gate)
            {
                folder = _pendingFolder;
                title = _pendingTitle;
                mode = _pendingMode;
                _pendingFolder = null;
                _pendingTitle = null;
                _pendingMode = null;
            }
            if (string.IsNullOrEmpty(folder)) return;
            Send(new PropertyWindowMessage
            {
                Kind = PropertyWindowLink.KindPreview,
                Folder = folder,
                PreviewTitle = title ?? "",
                Mode = mode ?? ""
            });
            Log.Information("[预览副窗] 补发了等待中的那一张: {Folder} 模式={Mode}", folder, mode);
        }

        private static void OnMessage(PropertyWindowMessage message)
        {
            if (message.Kind != PropertyWindowLink.KindSize) return;
            if (message.Width > 0 && message.Height > 0)
            {
                // 记在"这边最后一次发过去的模式"名下:尺寸消息是子进程主动报的,不带模式
                lock (_gate) _sizes[_currentMode] = (message.Width, message.Height);
            }
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
                _pendingFolder = null;
                _pendingTitle = null;
                _pendingMode = null;
                TryDelete(_payloadPath);
                _payloadPath = "";
            }
            channel?.Dispose();
            Log.Information("[预览副窗] 子进程已退出: Pid={Pid}", pid);
        }

        /// <summary>订阅主题,变化就推给子窗口;副窗口收不到就停在打开时那一份。
        /// 先减后加:Papers 页换 VM 实例时旧的那份订阅会留在树上,一次改主题就发两遍。</summary>
        private static void HookSettings(SettingsViewModel viewModel)
        {
            if (_hookedVm != null && !ReferenceEquals(_hookedVm, viewModel))
                _hookedVm.AppSettingsVM.PropertyChanged -= OnSettingsChanged;
            _hookedVm = viewModel;
            viewModel.AppSettingsVM.PropertyChanged -= OnSettingsChanged;
            viewModel.AppSettingsVM.PropertyChanged += OnSettingsChanged;
        }

        private static void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            var vm = _hookedVm;
            if (vm == null || e.PropertyName != nameof(AppSettingsViewModel.Theme)) return;
            Send(new PropertyWindowMessage { Kind = PropertyWindowLink.KindTheme, Theme = vm.AppSettingsVM.Theme ?? "" });
        }

        /// <summary>管道写可能在对方不读时阻塞,一律离开 UI 线程。</summary>
        private static void Send(PropertyWindowMessage message)
        {
            PropertyWindowChannel? channel;
            lock (_gate) channel = _channel;
            if (channel == null)
            {
                // 冷启动那一两秒里管道还没通:这条丢掉的只是"换到这张",重按一次预览就补上
                Log.Warning("[预览副窗] 管道还没通,这次换壁纸没送到副窗口");
                return;
            }
            _ = Task.Run(() => channel.Send(message));
        }

        private static void TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { File.Delete(path); } catch { }
        }
    }
}

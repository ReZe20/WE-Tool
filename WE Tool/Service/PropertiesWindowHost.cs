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
    /// 属性副窗口的母进程侧宿主:每个属性窗口 = 同 exe 自我启动的一个子进程。
    /// 之所以出进程——属性窗口的 XAML 布局/文件树/模糊预览都在主窗口的 UI 线程上跑,
    /// 而 WinUI 3 的 Window 无法挪到另一个线程(整个进程的 XAML 运行时绑一个 DispatcherQueue)。
    /// 母进程只做三件事:发快照、把主题/模糊变化推过去、收子进程的尺寸与保存通知。
    /// 跨进程 owner 关系不做:那会把两边输入队列绑起来,正好抵消解耦的收益。
    /// </summary>
    public static class PropertiesWindowHost
    {
        /// <summary>与旧"上限 5 个属性窗口"一致的软上限(现在一个窗口=一个进程)。</summary>
        public const int WindowLimit = 5;

        private sealed class Child
        {
            public Process Process = null!;
            public string FolderPath = "";
            public string PayloadPath = "";
            public PropertyWindowChannel? Channel;
        }

        private static readonly List<Child> _children = new();
        private static readonly object _gate = new();
        private static DispatcherQueue? _uiQueue;
        private static SettingsViewModel? _hookedVm;
        // 尺寸写盘的合并窗:最近一次报来的尺寸 + 计时器(只在 UI 线程上读写,每来一条新的重新计时)
        private static DispatcherQueueTimer? _sizeWriteTimer;
        private static int _sizeW;
        private static int _sizeH;

        /// <summary>当前存活的属性窗口子进程数(原 PropertiesWindow.OpenWindowCount)。</summary>
        public static int OpenWindowCount
        {
            get { lock (_gate) return _children.Count; }
        }

        public static async Task OpenAsync(SettingsViewModel viewModel, WallpaperItem wallpaper, bool showPropsPage = true)
        {
            if (string.IsNullOrEmpty(wallpaper.FolderPath)) return;

            HookSettings(viewModel);
            _uiQueue ??= DispatcherQueue.GetForCurrentThread();

            lock (_gate)
            {
                var existing = _children.FirstOrDefault(c => c.FolderPath == wallpaper.FolderPath);
                if (existing != null)
                {
                    // 同一壁纸不重复开:要求已存在的子窗口自己前置(母进程无法跨进程 Activate)
                    Send(existing, new PropertyWindowMessage { Kind = PropertyWindowLink.KindFocus });
                    return;
                }
            }

            var snapshot = await BuildSnapshotAsync(viewModel, wallpaper, showPropsPage).ConfigureAwait(false);
            string payloadPath;
            try
            {
                payloadPath = PropertyWindowLink.WritePayload(snapshot);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[属性副窗] 写启动载荷失败");
                return;
            }

            var child = new Child { FolderPath = wallpaper.FolderPath!, PayloadPath = payloadPath };
            try
            {
                string exePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    // 载荷走临时文件而不是命令行:Description/Tags/Dependency 长度不可控,命令行有 32K 上限
                    Arguments = $"{PropertyWindowLink.SwitchProperties} \"{payloadPath}\"",
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
                };
                child.Process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                child.Process.Exited += (_, _) => OnChildExited(child);

                lock (_gate) _children.Add(child);

                if (!child.Process.Start())
                {
                    lock (_gate) _children.Remove(child);
                    Log.Error("[属性副窗] 子进程启动失败");
                    return;
                }
                // 主程序退出即带走子进程(现件 Job Object,KILL_ON_JOB_CLOSE)
                JobObjectManager.AddProcess(child.Process.Handle);
                Log.Information("[属性副窗] 子进程已启动: Pid={Pid} Folder={Folder}", child.Process.Id, wallpaper.FolderPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[属性副窗] 启动子进程异常");
                lock (_gate) _children.Remove(child);
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
                    Log.Warning("[属性副窗] 与子进程管道未建成,主题/尺寸同步不可用: Pid={Pid}", child.Process.Id);
                    return;
                }
                child.Channel = channel;
                channel.MessageReceived += message => OnChildMessage(child, message);
                channel.Broken += () => child.Channel = null;
            });
        }

        private static async Task<PropertyWindowSnapshot> BuildSnapshotAsync(
            SettingsViewModel viewModel, WallpaperItem wallpaper, bool showPropsPage)
        {
            var settings = await new ConfigService().LoadAsync().ConfigureAwait(false);
            bool restoreSize = viewModel.AppSettingsVM.RestorePropertiesWindowSize;

            var snapshot = new PropertyWindowSnapshot
            {
                Protocol = PropertyWindowLink.Protocol,
                PipeName = PropertyWindowLink.NewPipeName(),
                Language = Windows.Globalization.ApplicationLanguages.Languages.FirstOrDefault() ?? "",
                LogLevel = settings.LogLevel ?? "",
                Theme = viewModel.AppSettingsVM.Theme ?? "",
                BlurEveryone = viewModel.WallpaperDisplayVM.BlurEveryone,
                BlurTeen = viewModel.WallpaperDisplayVM.BlurTeen,
                BlurAdult = viewModel.WallpaperDisplayVM.BlurAdult,
                ShowPropsPage = showPropsPage,
                Width = restoreSize ? settings.PropertiesWindowWidth : 0,
                Height = restoreSize ? settings.PropertiesWindowHeight : 0,

                WorkshopID = wallpaper.WorkshopID,
                Title = wallpaper.Title,
                FolderPath = wallpaper.FolderPath,
                Preview = wallpaper.Preview,
                ContentRating = wallpaper.ContentRating,
                Type = wallpaper.Type,
                Description = wallpaper.Description,
                Tags = wallpaper.Tags,
                Source = wallpaper.Source,
                Dependency = wallpaper.Dependency,
                CreationTime = wallpaper.CreationTime,
                UpdateTime = wallpaper.UpdateTime,
                AcfUpdateTime = wallpaper.AcfUpdateTime,
                FileSize = wallpaper.FileSize,
                AcfSize = wallpaper.AcfSize,
            };
            return snapshot;
        }

        /// <summary>订阅一次主 VM 的主题/模糊开关,变化时推给所有子进程。
        /// 副窗口拿不到这些事件就会停在打开时的主题——这是出进程唯一会掉的功能,靠本通道补回。</summary>
        private static void HookSettings(SettingsViewModel viewModel)
        {
            if (_hookedVm != null) return; // 只认第一个(整个应用共用同一个单例 VM)

            _hookedVm = viewModel;
            viewModel.AppSettingsVM.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AppSettingsViewModel.Theme)) BroadcastSettings();
            };
            viewModel.WallpaperDisplayVM.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(WallpaperDisplayViewModel.BlurEveryone)
                    or nameof(WallpaperDisplayViewModel.BlurTeen)
                    or nameof(WallpaperDisplayViewModel.BlurAdult)) BroadcastSettings();
            };
        }

        private static void BroadcastSettings()
        {
            var vm = _hookedVm;
            if (vm == null) return;
            var message = new PropertyWindowMessage
            {
                Kind = PropertyWindowLink.KindTheme,
                Theme = vm.AppSettingsVM.Theme ?? "",
                BlurEveryone = vm.WallpaperDisplayVM.BlurEveryone,
                BlurTeen = vm.WallpaperDisplayVM.BlurTeen,
                BlurAdult = vm.WallpaperDisplayVM.BlurAdult,
            };
            List<Child> snapshot;
            lock (_gate) snapshot = _children.ToList();
            foreach (var child in snapshot) Send(child, message);
        }

        /// <summary>管道写可能在对方不读时阻塞,一律离开 UI 线程。</summary>
        private static void Send(Child child, PropertyWindowMessage message)
        {
            var channel = child.Channel;
            if (channel == null) return;
            _ = Task.Run(() => channel.Send(message));
        }

        /// <summary>某个属性副窗口写完了它那张壁纸的 project.json(子进程报回 saved 消息),参数是文件夹。
        /// Papers 的属性面板订阅它:面板若正显示同一张,手里的模型已经过期,须重读。</summary>
        public static event Action<string>? PropertySavedByChild;

        /// <summary>反向:母进程(Papers 属性面板)刚写了某张壁纸的 project.json,请它的属性副窗口重读。
        /// 不做这件事的话,那个进程手里还是它打开时的快照,下一次它保存会把面板的修改覆盖回去。</summary>
        public static void NotifyPropertySaved(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;
            Child[] targets;
            lock (_gate) targets = _children.Where(c => c.FolderPath == folderPath).ToArray();
            if (targets.Length == 0) return;
            var message = new PropertyWindowMessage { Kind = PropertyWindowLink.KindReload };
            foreach (var child in targets) Send(child, message);
        }

        private static void OnChildMessage(Child child, PropertyWindowMessage message)
        {
            switch (message.Kind)
            {
                case PropertyWindowLink.KindSize:
                    // 尺寸变化当场就记,不等子进程关闭或退出:它随时可能被 Job Object 带走,
                    // 而"上次多大"这件事只有母进程这边写得了 config.json。
                    if (message.Width <= 0 || message.Height <= 0) return;
                    ScheduleSizeWrite(message.Width, message.Height);
                    break;
                case PropertyWindowLink.KindSaved:
                    // 子进程写了 project.json。母进程的 WallpaperPropertyParser 按文件修改时间缓存,
                    // 下次解析自然重读;列表字段(标题/类型/分级)不来自 general.properties,无需刷新。
                    Log.Information("[属性副窗] 子进程已保存属性: {Folder}", child.FolderPath);
                    // 但 Papers 属性面板若正显示这一张,它手里的模型也过期了(再保存会把子窗口的修改覆盖回去)
                    PropertySavedByChild?.Invoke(child.FolderPath);
                    break;
            }
        }

        /// <summary>尺寸消息一到就攒下,400ms 内不再有新的才真写盘。
        /// 合并是必要的:一次拖拽会产生几十到几百条尺寸消息,逐条全量覆写 config.json 既没必要,
        /// 多个写盘任务叠在同一份文件上还可能写出半截 JSON。
        /// 计时器挂在这一侧的 UI 线程上,所以子进程什么时候退都不影响最后一次落盘。
        /// 在管道的后台读线程上被调用,尺寸对(宽+高)须在同一个 UI 线程赋值,避免取到两条消息各一半。</summary>
        private static void ScheduleSizeWrite(int width, int height)
        {
            var queue = _uiQueue;
            if (queue == null) return;
            queue.TryEnqueue(() =>
            {
                _sizeW = width;
                _sizeH = height;
                if (_sizeWriteTimer == null)
                {
                    _sizeWriteTimer = queue.CreateTimer();
                    _sizeWriteTimer.Interval = TimeSpan.FromMilliseconds(400);
                    _sizeWriteTimer.Tick += (_, _) =>
                    {
                        _sizeWriteTimer!.Stop();
                        PersistSize(_sizeW, _sizeH);
                    };
                }
                _sizeWriteTimer.Stop();
                _sizeWriteTimer.Start();
            });
        }

        /// <summary>把尺寸写进共享 config.json 的 PropertiesWindowWidth/Height(属性窗口下次打开时用它)。
        /// 开关(RestorePropertiesWindowSize)只管读不问:关掉仍在记尺寸,再打开就接着用。</summary>
        private static void PersistSize(int width, int height)
        {
            _uiQueue?.TryEnqueue(async () =>
            {
                try
                {
                    var settings = await new ConfigService().LoadAsync();
                    if (settings.PropertiesWindowWidth == width && settings.PropertiesWindowHeight == height) return;
                    settings.PropertiesWindowWidth = width;
                    settings.PropertiesWindowHeight = height;
                    await new ConfigService().SaveAsync(settings);
                    Log.Information("[属性副窗] 已记录属性窗口尺寸 {Width}x{Height}", width, height);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[属性副窗] 回灌窗口尺寸失败");
                }
            });
        }

        private static void OnChildExited(Child child)
        {
            bool removed;
            lock (_gate) removed = _children.Remove(child);
            // 不在这里 Dispose 管道:子进程一没,还压在管道缓冲里的最后几条尺寸消息就读不到了。
            // 管道由它自己的读循环在 EOF 后释放(见 PropertyWindowChannel.PumpAsync)。
            TryDelete(child.PayloadPath);
            if (!removed) return;

            Log.Information("[属性副窗] 子进程已退出: Pid={Pid}", child.Process.Id);
        }

        private static void TryDelete(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { File.Delete(path); } catch { }
        }
    }
}

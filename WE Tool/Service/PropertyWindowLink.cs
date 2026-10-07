using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using WE_Tool.Json;

namespace WE_Tool.Service
{
    /// <summary>
    /// 副窗口(属性 / 白名单 / 移动版队列 / 场景预览)的进程间链路:启动载荷文件 + 命名管道双向行帧。
    /// 子进程是 server(它先从载荷里拿到管名并等待连接),母进程是 client 并重试——
    /// WinUI 冷启动比管道重试窗口慢不了多少,所以不需要额外的"就绪"信令。
    /// 只用一条管道,不引入 WndProc 子类化/共享内存:NativeAOT 下没有函数指针生命周期要管。
    /// </summary>
    internal static class PropertyWindowLink
    {
        /// <summary>协议代号:子进程据此拒绝旧母进程(或反过来)的载荷,不做静默兼容。
        /// 名字里的 property 是历史原因,它现在同时管属性、白名单、移动版队列与场景预览四种副窗口。</summary>
        public const string Protocol = "we-property-1";

        /// <summary>副窗口模式的启动开关(生成的 Main 丢弃 args,只能在 App 构造里读命令行)。</summary>
        public const string SwitchProperties = "--properties-window";
        public const string SwitchWhitelist = "--whitelist-window";
        public const string SwitchMpkgQueue = "--mpkg-window";
        public const string SwitchScenePreview = "--scene-preview-window";

        public const string KindTheme = "theme";
        public const string KindBlur = "blur";
        public const string KindFocus = "focus";
        public const string KindSize = "size";
        public const string KindSaved = "saved";
        /// <summary>母→子:Papers 属性面板刚写了同一张壁纸的 project.json,子窗口重读。
        /// 与 KindSaved 是一对反向通知——两侧都可能是写者,谁写完谁喊对方重读。</summary>
        public const string KindReload = "reload";
        /// <summary>母→子:清理页刚把某 ID 加入白名单,子窗口增量加卡。</summary>
        public const string KindAdd = "add";
        /// <summary>子→母:子窗口请求把某 ID 移出白名单。落盘由母进程做——保持单写者。</summary>
        public const string KindRemoved = "removed";

        /// <summary>母→子:页面又「转为移动版」了几张,子窗口把行追加进自己那份队列。
        /// 队列此时只有一个持有者(子进程),母进程自己那份是空的,所以这条是单向投递不是同步。</summary>
        public const string KindMpkgRows = "mpkg-rows";
        /// <summary>子→母:整份队列(行 + 面板状态)交回。带 Dock=true 的是点「贴回」,母进程要顺带把页面面板展开;
        /// 不带的是每次改动后的存一份和退出前那一次 —— 母进程收下但不弹面板。</summary>
        public const string KindMpkgQueue = "mpkg-queue";
        /// <summary>子→母:副窗口正在转换 / 转完了。母进程据此挡住同一段时间里再开一批提取,
        /// 免得两个 repkg 批次抢同一批核(两侧原本共用 IsExtracting 这道闸,出进程后只剩这一条消息能顶它)。</summary>
        public const string KindMpkgBusy = "mpkg-busy";

        /// <summary>母→子:预览副窗口换一张壁纸(Folder=条目目录,PreviewTitle=标题上的名字)。
        /// 预览只开一扇窗:再点别的壁纸是发这一条,而不是再起一个进程去付一份 XAML 运行时。</summary>
        public const string KindPreview = "preview";

        public enum WindowKind { None, Properties, Whitelist, MpkgQueue, ScenePreview }

        public static string NewPipeName() => $"we-tool-child-{Guid.NewGuid():N}";

        public static string WritePayload(PropertyWindowSnapshot snapshot) =>
            WriteTemp($"we-tool-property-{Guid.NewGuid():N}.json", JsonSerializer.Serialize(
                snapshot, PropertyWindowJsonContext.Default.PropertyWindowSnapshot));

        public static string WritePayload(WhitelistWindowSnapshot snapshot) =>
            WriteTemp($"we-tool-whitelist-{Guid.NewGuid():N}.json", JsonSerializer.Serialize(
                snapshot, PropertyWindowJsonContext.Default.WhitelistWindowSnapshot));

        public static string WritePayload(MpkgWindowSnapshot snapshot) =>
            WriteTemp($"we-tool-mpkg-{Guid.NewGuid():N}.json", JsonSerializer.Serialize(
                snapshot, PropertyWindowJsonContext.Default.MpkgWindowSnapshot));

        public static string WritePayload(ScenePreviewWindowSnapshot snapshot) =>
            WriteTemp($"we-tool-preview-{Guid.NewGuid():N}.json", JsonSerializer.Serialize(
                snapshot, PropertyWindowJsonContext.Default.ScenePreviewWindowSnapshot));

        private static string WriteTemp(string fileName, string text)
        {
            string path = Path.Combine(Path.GetTempPath(), fileName);
            File.WriteAllText(path, text);
            return path;
        }

        /// <summary>本进程该以哪种副窗口启动;同时给出载荷文件路径(载荷由调用方按类型读取)。</summary>
        public static WindowKind ReadLaunchKind(out string payloadPath)
        {
            payloadPath = "";
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], SwitchProperties, StringComparison.Ordinal))
                {
                    payloadPath = args[i + 1];
                    return WindowKind.Properties;
                }
                if (string.Equals(args[i], SwitchWhitelist, StringComparison.Ordinal))
                {
                    payloadPath = args[i + 1];
                    return WindowKind.Whitelist;
                }
                if (string.Equals(args[i], SwitchMpkgQueue, StringComparison.Ordinal))
                {
                    payloadPath = args[i + 1];
                    return WindowKind.MpkgQueue;
                }
                if (string.Equals(args[i], SwitchScenePreview, StringComparison.Ordinal))
                {
                    payloadPath = args[i + 1];
                    return WindowKind.ScenePreview;
                }
            }
            return WindowKind.None;
        }

        public static PropertyWindowSnapshot? ReadPropertiesPayload(string path)
        {
            var snapshot = ReadAndDelete(path, text => JsonSerializer.Deserialize(
                text, PropertyWindowJsonContext.Default.PropertyWindowSnapshot));
            if (snapshot != null && !Protocol.Equals(snapshot.Protocol, StringComparison.Ordinal))
            {
                Log.Error("[属性副窗] 协议不匹配: 期望 {Want},实际 {Got}", Protocol, snapshot.Protocol);
                return null;
            }
            return snapshot;
        }

        public static WhitelistWindowSnapshot? ReadWhitelistPayload(string path)
        {
            var snapshot = ReadAndDelete(path, text => JsonSerializer.Deserialize(
                text, PropertyWindowJsonContext.Default.WhitelistWindowSnapshot));
            if (snapshot != null && !Protocol.Equals(snapshot.Protocol, StringComparison.Ordinal))
            {
                Log.Error("[白名单副窗] 协议不匹配: 期望 {Want},实际 {Got}", Protocol, snapshot.Protocol);
                return null;
            }
            return snapshot;
        }

        public static MpkgWindowSnapshot? ReadMpkgPayload(string path)
        {
            var snapshot = ReadAndDelete(path, text => JsonSerializer.Deserialize(
                text, PropertyWindowJsonContext.Default.MpkgWindowSnapshot));
            if (snapshot != null && !Protocol.Equals(snapshot.Protocol, StringComparison.Ordinal))
            {
                Log.Error("[移动版副窗] 协议不匹配: 期望 {Want},实际 {Got}", Protocol, snapshot.Protocol);
                return null;
            }
            return snapshot;
        }

        public static ScenePreviewWindowSnapshot? ReadScenePayload(string path)
        {
            var snapshot = ReadAndDelete(path, text => JsonSerializer.Deserialize(
                text, PropertyWindowJsonContext.Default.ScenePreviewWindowSnapshot));
            if (snapshot != null && !Protocol.Equals(snapshot.Protocol, StringComparison.Ordinal))
            {
                Log.Error("[预览副窗] 协议不匹配: 期望 {Want},实际 {Got}", Protocol, snapshot.Protocol);
                return null;
            }
            return snapshot;
        }

        /// <summary>读载荷并立刻删除:副窗口崩溃也要留下干净的 %TEMP%。</summary>
        private static T? ReadAndDelete<T>(string path, Func<string, T?> deserialize) where T : class
        {
            try
            {
                return deserialize(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[副窗口] 读取启动载荷失败: {Path}", path);
                return null;
            }
            finally
            {
                try { File.Delete(path); } catch { /* 删不掉不阻塞启动 */ }
            }
        }
    }

    /// <summary>一条已连通的属性副窗口管道:双向、一行一个 JSON。
    /// 必须 partial:实现了 IDisposable(WinRT 可见接口),CsWinRT 要求标 partial 才能正确裁剪。</summary>
    internal sealed partial class PropertyWindowChannel : IDisposable
    {
        private readonly PipeStream _stream;
        private readonly StreamWriter _writer;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _writeGate = new();

        /// <summary>收到对端消息(在管道后台线程上触发,UI 相关处理须自行回队)。</summary>
        public event Action<PropertyWindowMessage>? MessageReceived;

        /// <summary>对端断开或读失败。</summary>
        public event Action? Broken;

        private PropertyWindowChannel(PipeStream stream)
        {
            _stream = stream;
            _writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };
        }

        /// <summary>母进程侧:连接到子进程的管道,期间重试直到超时。</summary>
        public static async Task<PropertyWindowChannel?> ConnectAsClientAsync(string pipeName, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(deadline - DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    if (pipe.IsConnected) return Start(pipe);
                    pipe.Dispose();
                }
                catch (Exception)
                {
                    pipe.Dispose();
                    await Task.Delay(150).ConfigureAwait(false);
                }
            }
            Log.Warning("[属性副窗] 管道连接超时: {Pipe}", pipeName);
            return null;
        }

        /// <summary>子进程侧:建 server 并等待母进程接进来。</summary>
        public static async Task<PropertyWindowChannel?> AcceptAsServerAsync(string pipeName, TimeSpan timeout)
        {
            var pipe = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await pipe.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);
                return Start(pipe);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[属性副窗] 等待管道连接失败: {Pipe}", pipeName);
                pipe.Dispose();
                return null;
            }
        }

        private static PropertyWindowChannel Start(PipeStream stream)
        {
            var channel = new PropertyWindowChannel(stream);
            _ = Task.Run(channel.PumpAsync);
            return channel;
        }

        /// <summary>发一条消息。管道写会在对端不读时阻塞,所以调用方不放 UI 线程上同步等。</summary>
        public void Send(PropertyWindowMessage message)
        {
            string line = JsonSerializer.Serialize(
                message, PropertyWindowJsonContext.Default.PropertyWindowMessage);
            lock (_writeGate)
            {
                try
                {
                    _writer.WriteLine(line);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[属性副窗] 管道写入失败(对端可能已退出)");
                }
            }
        }

        private async Task PumpAsync()
        {
            var reader = new StreamReader(_stream);
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    var message = JsonSerializer.Deserialize(
                        line, PropertyWindowJsonContext.Default.PropertyWindowMessage);
                    if (message != null) MessageReceived?.Invoke(message);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Debug(ex, "[属性副窗] 管道读循环结束");
            }
            // 由读循环来收尾释放:它只在 EOF/读失败之后才走到这里,也就是对端关闭前写进管道的
            // 最后几条消息已经全部交付了。反过来(对端进程一没就 Dispose)会把缓冲里的消息掐掉。
            Dispose();
            Broken?.Invoke();
        }

        public void Dispose()
        {
            _cts.Cancel();
            lock (_writeGate)
            {
                try { _writer.Dispose(); } catch { }
            }
            try { _stream.Dispose(); } catch { }
        }
    }
}

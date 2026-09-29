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
    /// 副窗口(属性 / 白名单)的进程间链路:启动载荷文件 + 命名管道双向行帧。
    /// 子进程是 server(它先从载荷里拿到管名并等待连接),母进程是 client 并重试——
    /// WinUI 冷启动比管道重试窗口慢不了多少,所以不需要额外的"就绪"信令。
    /// 只用一条管道,不引入 WndProc 子类化/共享内存:NativeAOT 下没有函数指针生命周期要管。
    /// </summary>
    internal static class PropertyWindowLink
    {
        /// <summary>协议代号:子进程据此拒绝旧母进程(或反过来)的载荷,不做静默兼容。
        /// 名字里的 property 是历史原因,它现在同时管属性与白名单两种副窗口。</summary>
        public const string Protocol = "we-property-1";

        /// <summary>副窗口模式的启动开关(生成的 Main 丢弃 args,只能在 App 构造里读命令行)。</summary>
        public const string SwitchProperties = "--properties-window";
        public const string SwitchWhitelist = "--whitelist-window";

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

        public enum WindowKind { None, Properties, Whitelist }

        public static string NewPipeName() => $"we-tool-child-{Guid.NewGuid():N}";

        public static string WritePayload(PropertyWindowSnapshot snapshot) =>
            WriteTemp($"we-tool-property-{Guid.NewGuid():N}.json", JsonSerializer.Serialize(
                snapshot, PropertyWindowJsonContext.Default.PropertyWindowSnapshot));

        public static string WritePayload(WhitelistWindowSnapshot snapshot) =>
            WriteTemp($"we-tool-whitelist-{Guid.NewGuid():N}.json", JsonSerializer.Serialize(
                snapshot, PropertyWindowJsonContext.Default.WhitelistWindowSnapshot));

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

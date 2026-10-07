using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Serilog;
using WE_Tool.Json;

namespace WE_Tool.Service
{
    /// <summary>
    /// 场景壁纸实时预览宿主:把 WebWallGL(vendored 在随包的 WebWallGL\ 子目录)装进 WinUI 的 WebView2,
    /// 由库在 WebGL2 里实时还原选中的场景包。库里做的是 WE 场景引擎的第三方复刻,所以画面与官方
    /// WallpaperEngine 可能有差异——这是"预览",不是"渲染基准"。
    ///
    /// 浏览器本体不随包:用系统预装的 Edge WebView2 Evergreen 运行时。没预装时这里只做提醒并返回 false,
    /// 既不下载也不带 212MB 的独立安装包(见 RuntimeAvailable)。
    ///
    /// 资源不出网卡:两个虚域名由 Chromium 在进程内解析成目录读取,不是真的 DNS 名。
    /// ItemHost 用 Allow 而不是 Deny,是因为宿主页跑在 AssetHost 上,取壁纸包属跨源 fetch,
    /// 默认会被 CORS 拦掉(虚域名的 Allow 只放开 WebView2 内部的跨源读,不对外暴露任何东西)。
    /// </summary>
    public static class ScenePreviewHost
    {
        /// <summary>随包宿主资产目录名(与 csproj 里 Content 的 Link 同形,改名要两头一起改)。</summary>
        public const string AssetFolderName = "WebWallGL";

        public const string AssetHost = "we-tool.assets.example";
        public const string ItemHost = "we-tool.items.example";

        // WebView2 Evergreen 运行时在注册表里以这个 CLSID 登记
        private const string EdgeWebViewClientPath =
            @"Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

        private const string NotInstalledPv = "0.0.0.0";

        private static WebView2? _view;
        private static Grid? _host;
        private static bool _coreReady;
        private static bool _pageListening;
        private static string? _itemsRoot;
        private static string? _loadedFolder;
        private static string? _libVersion;
        private static string? _browserVersion;
        private static bool _instanceReady;

        /// <summary>这张壁纸当前生效的属性值(键 → JSON 字面量)。留着是因为页面可能在值到达之前还没出首帧,
        /// 也可能是换壁纸后重建了实例——两种情况都要能把这批值补发一遍,而不是把用户改过的值丢掉。</summary>
        private static readonly Dictionary<string, string> _props = new();

        private static TaskCompletionSource<bool>? _navigationTcs;
        private static TaskCompletionSource<bool>? _helloTcs;

        public static string StatusLine { get; private set; } = "";

        /// <summary>状态读数变化(页面把它显示在预览下方,初始化失败时是唯一线索)。</summary>
        public static event Action? StatusChanged;

        /// <summary>
        /// 运行时是否可用。一次进程探一次盘:运行中途装好不算数,重开程序才生效
        /// (与属性副窗"快照随进程"同一取舍,预览是可选功能,不值得为它做热重探)。
        /// </summary>
        public static bool RuntimeAvailable => RuntimeVersion != null;

        public static string? RuntimeVersion { get; } = ProbeRuntimeVersion();

        private static string AssetsRoot => Path.Combine(AppContext.BaseDirectory, AssetFolderName);

        /// <summary>
        /// 让预览面显示这张壁纸。返回 false = 没显示(运行时缺失 / 壁纸目录不存在 / 宿主初始化失败),
        /// 调用方保留静态预览图即可,这里不自己兜底。首帧与失败都走 StatusChanged,不在返回值里。
        /// </summary>
        /// <param name="fillHost">true=铺满宿主(预览副窗口那种整窗画面);false=按详情页那块 280×280 摆</param>
        /// <param name="fps">帧率上限:详情页小图 30 足够,整窗预览给 60</param>
        public static async Task<bool> TryShowAsync(Grid host, string? wallpaperFolder, bool fillHost = false, int fps = 30)
        {
            if (!RuntimeAvailable)
            {
                SetStatus("WebView2 运行时未安装");
                return false;
            }

            var folder = ResolveFolder(wallpaperFolder);
            if (folder is null)
            {
                SetStatus("壁纸目录不存在");
                return false;
            }

            EnsureView(host, fillHost);
            // 壁纸域名要在首帧导航之前就挂好:留给"页面已经开跑才补映射"这个窗口,第一次取包就可能撞上空域名
            if (!_coreReady && !await EnsureCoreAsync(folder)) return false;

            // 首帧之后换到别的库根(多 Steam 库)时靠这句重新挂映射,同根时它自己短路
            MapItemsRoot(folder);
            var baseUrl = $"https://{ItemHost}/{Uri.EscapeDataString(Path.GetFileName(folder))}";

            // 同一张壁纸重复选中(切面板/回本页)不重新装载:库重新解析一包要几百 ms 到数秒
            if (_loadedFolder == folder && _pageListening)
            {
                Post("{\"cmd\":\"resume\"}");
                _view!.Visibility = Visibility.Visible;
                return true;
            }

            // base 走源生成的 JsonContext.Default.String(反射式 Serialize 在 AOT 下是 IL2026/IL3050);
            // fps 是我们自己算出来的整数,直接拼进 JSON 不需要转义
            _instanceReady = false;
            _props.Clear();      // 换的是另一张壁纸:上一张的属性值对新实例无意义(键名逐壁纸不同)
            Post($"{{\"cmd\":\"load\",\"base\":{JsonSerializer.Serialize(baseUrl, JsonContext.Default.String)},\"fps\":{fps}}}");
            _loadedFolder = folder;
            _view!.Visibility = Visibility.Visible;
            SetStatus($"已请求装载 {Path.GetFileName(folder)}");
            Log.Information("[场景预览] 装载请求已发: Folder={Folder} Base={Base}", folder, baseUrl);
            return true;
        }

        /// <summary>
        /// 改一批用户属性:页面已出首帧就当场生效,还没出就只进台账等首帧补发。
        /// 值由调用方给成 JSON 字面量(布尔/数字/字符串三种),这里只负责拼消息——
        /// 属性类型是 project.json 的事,宿主不该认识 WallpaperProperty。
        /// 一次发一批而不是逐条发:开栏时要把整份当前值同步过去,逐条就是几百次 setProperties,
        /// 而每一次都会让场景里所有声明了 applyUserProperties 的脚本各跑一遍。
        /// </summary>
        /// <param name="literals">属性名 → 该值的 JSON 字面量,如 true / 0.5 / "1 0 0"</param>
        public static void ApplyProperties(IReadOnlyDictionary<string, string> literals)
        {
            if (literals.Count == 0) return;
            foreach (var (key, value) in literals) _props[key] = value;
            if (!_coreReady || !_pageListening || !_instanceReady) return;
            PostProps(literals);
        }

        private static void PostProps(IReadOnlyDictionary<string, string> literals)
        {
            var sb = new StringBuilder("{\"cmd\":\"props\",\"values\":{");
            bool first = true;
            foreach (var (key, value) in literals)
            {
                if (!first) sb.Append(',');
                first = false;
                // 键走源生成的 String 转义(属性名是作者自定的,什么字符都可能);值已经是 JSON 字面量,原样嵌
                sb.Append(JsonSerializer.Serialize(key, JsonContext.Default.String)).Append(':').Append(value);
            }
            sb.Append("}}");
            Post(sb.ToString());
        }

        /// <summary>收起预览面并叫页面暂停渲染——只 Visibility.Collapsed 不保证 WebView2 停帧。</summary>
        public static void Hide()
        {
            if (_view is null) return;
            _view.Visibility = Visibility.Collapsed;
            if (_coreReady && _pageListening) Post("{\"cmd\":\"pause\"}");
        }

        /// <summary>
        /// 路径既可能是壁纸目录,也可能直接指到工程文件(与 RepkgCliService 收到的 FolderPath 同形态),
        /// 所以是文件就退回它所在目录——松散工程与打包壁纸都能被 WebWallGL 按 project.json 判形态。
        /// </summary>
        private static string? ResolveFolder(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (File.Exists(trimmed)) trimmed = Path.GetDirectoryName(trimmed) ?? trimmed;
            return Directory.Exists(trimmed) ? trimmed : null;
        }

        /// <summary>
        /// 首次调用时建出预览面并挂到 host 上。fillHost 只在建那一刻生效:
        /// 一个进程只服务一种宿主(母进程只有详情页小图,预览副窗口只有整窗),不会中途换档。
        /// </summary>
        private static void EnsureView(Grid host, bool fillHost)
        {
            if (_view is not null && _host == host) return;

            if (_view is null)
            {
                _view = new WebView2 { Visibility = Visibility.Collapsed };
                if (fillHost)
                {
                    _view.HorizontalAlignment = HorizontalAlignment.Stretch;
                    _view.VerticalAlignment = VerticalAlignment.Stretch;
                }
                else
                {
                    // 与详情页那块 SinglePreviewBorder 对齐:280×280、顶上留 10
                    _view.Width = 280;
                    _view.Height = 280;
                    _view.Margin = new Thickness(0, 10, 0, 0);
                    _view.HorizontalAlignment = HorizontalAlignment.Center;
                    _view.VerticalAlignment = VerticalAlignment.Center;
                }
                // WinUI 的 WebView2 事件是 TypedEventHandler<WebView2, …>,不是 CoreWebView2 那一路的委托
                _view.WebMessageReceived += OnWebMessageReceived;
            }

            _host?.Children.Remove(_view);
            host.Children.Add(_view);
            _host = host;
        }

        private static async Task<bool> EnsureCoreAsync(string firstFolder)
        {
            var view = _view!;
            try
            {
                // 不自己指定用户数据目录:非打包程序的默认值落在 %TEMP% 下,不进包目录就够了
                // (预览面没有需要长期保留的东西,缓存被磁盘清理收掉也不影响功能)
                await view.EnsureCoreWebView2Async();

                var core = view.CoreWebView2;
                _browserVersion = core.Environment.BrowserVersionString;
                core.Settings.AreDefaultContextMenusEnabled = false;   // 预览面不要"后退/检查元素"
                core.SetVirtualHostNameToFolderMapping(
                    AssetHost, AssetsRoot, CoreWebView2HostResourceAccessKind.Allow);
                // 两张域名都在首次导航前挂好:页面一旦开跑就有取包请求,那时才补映射就撞上"域名还没有映射"
                MapItemsRoot(firstFolder);

                _navigationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _helloTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                view.NavigationCompleted += OnNavigationCompleted;
                core.Navigate($"https://{AssetHost}/preview.html");

                var finished = await Task.WhenAny(_navigationTcs.Task, Task.Delay(15000));
                if (finished != _navigationTcs.Task)
                {
                    SetStatus("宿主页面 15s 内未加载完");
                    return false;
                }

                // 模块脚本是延迟执行的,NavigationCompleted 可能比监听器装好更早——等页面报一声
                var hello = await Task.WhenAny(_helloTcs.Task, Task.Delay(3000));
                if (hello != _helloTcs.Task)
                {
                    SetStatus("宿主页面未回报就绪");
                    return false;
                }
                // 内核有没有 WebGL2 不在这里探:自建探针要多占一个 GL 上下文,而 Chromium 同进程只给 8~16 个,
                // 被挤掉的最旧那个往往就是壁纸。缺 WebGL2 由页面报 error,那一路把预览面收回去。
                _coreReady = true;
                SetStatus($"WebView2 {_browserVersion} · WebWallGL {_libVersion}");
                Log.Information("[场景预览] 宿主就绪: Browser={Browser} Lib={Lib} Assets={Assets}",
                    _browserVersion, _libVersion, AssetsRoot);
                return true;
            }
            catch (Exception ex)
            {
                // 边界在浏览器进程的启动上:运行时装了又被卸载、组策略禁了 WebView2、GPU 驱动起不来
                // 都会从这里抛出,不吞掉——StatusLine 是唯一能看出死在哪一步的地方
                SetStatus($"初始化失败: {ex.GetType().Name} {ex.Message}");
                Log.Error(ex, "[场景预览] WebView2 初始化失败");
                return false;
            }
        }

        private static void MapItemsRoot(string wallpaperFolder)
        {
            var root = Path.GetDirectoryName(wallpaperFolder) ?? wallpaperFolder;
            if (root == _itemsRoot) return;
            _view!.CoreWebView2.SetVirtualHostNameToFolderMapping(
                ItemHost, root, CoreWebView2HostResourceAccessKind.Allow);
            _itemsRoot = root;
            Log.Information("[场景预览] 条目虚域名换根: {Root}", root);
        }

        private static void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (!args.IsSuccess) SetStatus($"页面导航失败: {args.WebErrorStatus}");
            _navigationTcs?.TrySetResult(args.IsSuccess);
        }

        private static void Post(string json)
        {
            try { _view?.CoreWebView2.PostWebMessageAsJson(json); }
            catch (Exception ex) { Log.Warning(ex, "[场景预览] 向页面发命令失败: {Json}", json); }
        }

        private static void OnWebMessageReceived(WebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(args.WebMessageAsJson); }
            catch (JsonException) { return; }   // 页面只能发对象;发不出对象的就是被改坏的脚本,不值得记日志

            // JsonDocument 是 DOM:零反射、AOT 安全(与 RepkgCliService 手写 manifest 同一口径)
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;

                switch (GetString(root, "t"))
                {
                    case "hello":
                        _libVersion = GetString(root, "lib");
                        break;
                    case "listening":
                        _pageListening = true;
                        _helloTcs?.TrySetResult(true);
                        break;
                    case "ready":
                        _instanceReady = true;
                        var info = root.TryGetProperty("info", out var i) ? i : default;
                        SetStatus($"首帧 {GetInteger(root, "ms")}ms · " +
                                  $"{GetInteger(info, "width")}×{GetInteger(info, "height")} · " +
                                  $"图层 {GetInteger(info, "layerCount")}");
                        Log.Information("[场景预览] 首帧: Ms={Ms} Info={Info}", GetInteger(root, "ms"), info.ToString());
                        // 首帧之前攒下的属性值现在才有着落:库的 setProperties 要实例活着才生效,
                        // 而右栏的行在装载请求发出的同时就在建了,两者谁先到都可能
                        if (_props.Count > 0)
                        {
                            PostProps(_props);
                            Log.Information("[场景预览] 首帧后补发属性 {Count} 项", _props.Count);
                        }
                        break;
                    case "diag":
                        var lvl = GetString(root, "lvl");
                        if (lvl is "warn" or "error") SetStatus($"[{lvl}] {GetString(root, "msg")}");
                        Log.Information("[场景预览] 诊断 {Level}: {Message}", lvl, GetString(root, "msg"));
                        break;
                    case "error":
                        SetStatus($"渲染失败: {GetString(root, "msg")}");
                        Log.Warning("[场景预览] 渲染失败: {Message}", GetString(root, "msg"));
                        break;
                    case "probe":
                        // 页面自己报回来的取包通道分岔:跨域被拦 / 虚域名没接住 / 路径 404,三者修法不同
                        SetStatus($"取包探测:{GetString(root, "crossCors")}");
                        Log.Warning("[场景预览] 取包通道探测: {Probe}", root.ToString());
                        break;
                }
            }
        }

        private static void SetStatus(string text)
        {
            StatusLine = text;
            StatusChanged?.Invoke();
        }

        private static string? ProbeRuntimeVersion()
        {
            // HKCU 与 HKLM 两个视图都要查:Evergreen 可以装在当前用户下,也可以整机部署
            string? pv = Read(Registry.CurrentUser)
                ?? Read(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                ?? Read(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32));
            return string.IsNullOrEmpty(pv) || pv == NotInstalledPv ? null : pv;

            static string? Read(RegistryKey baseKey)
            {
                using var key = baseKey.OpenSubKey(EdgeWebViewClientPath);
                return key?.GetValue("pv") as string;
            }
        }

        private static string GetString(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object &&
               e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";

        private static int GetInteger(JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object &&
               e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
                ? n : 0;
    }
}

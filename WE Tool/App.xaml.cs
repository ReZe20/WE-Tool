using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Models;
using WE_Tool.Service;
using WE_Tool.ViewModels;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WE_Tool
{
    public partial class App : Application
    {
        private Window? _window;

        public SettingsViewModel ViewModel { get; }
        private readonly IConfigService _configService = new ConfigService();
        public static List<WallpaperItem> GlobalAllWallpapers { get; private set; } = [];
        public static Task ScanTask { get; private set; } = Task.CompletedTask;
        /// <summary>日志级别运行时开关(设置页修改即时生效,无需重启;默认关闭)</summary>
        public static LoggingLevelSwitch LogLevelSwitch { get; } = new(LogEventLevel.Fatal);
        /// <summary>启动时的完整扫描链路（读配置 → StartBackgroundScan），页面可等待它确保扫描已开始</summary>
        public static Task? InitialScanTask { get; private set; }
        public static event EventHandler? ScanCompleted;
        // 扫描防重入:代号(新扫描递增,旧代结果按代号判旧丢弃)+ 当前代的取消令牌
        private static int _scanGeneration;
        private static CancellationTokenSource? _scanCts;
        public static Window? MainWindowInstance { get; private set; }
        // 捕获启动时的系统首选 UI 语言（如 "zh-CN"/"en-US"），跟随系统时用作 PrimaryLanguageOverride
        public static readonly string SystemLanguage = System.Globalization.CultureInfo.CurrentUICulture.Name;
        private static Json.PropertyWindowSnapshot? _launchSnapshot;
        private static Json.WhitelistWindowSnapshot? _launchWhitelistSnapshot;
        private static Json.MpkgWindowSnapshot? _launchMpkgSnapshot;
        /// <summary>当前进程是属性副窗口子进程(母进程带 --properties-window 自我启动)</summary>
        public static bool IsPropertiesWindowChild => _launchSnapshot != null;
        public static Json.PropertyWindowSnapshot? PropertiesWindowLaunch => _launchSnapshot;
        /// <summary>当前进程是白名单副窗口子进程(母进程带 --whitelist-window 自我启动)</summary>
        public static Json.WhitelistWindowSnapshot? WhitelistWindowLaunch => _launchWhitelistSnapshot;
        /// <summary>当前进程是移动版队列副窗口子进程(母进程带 --mpkg-window 自我启动,队列已整份交给它)</summary>
        public static Json.MpkgWindowSnapshot? MpkgWindowLaunch => _launchMpkgSnapshot;
        /// <summary>用户数据根,InitLogging 里定值(日志/配置/缓存同根)</summary>
        public static string AppDataRoot { get; private set; } = "";

        public App()
        {
            // 副窗口子进程要在建主 VM 之前分流:主窗口那条启动链会读配置(文件缺失/旧版还会写回盘)、
            // 拉起 SteamworksBridge、全盘扫描并写 wallpaper_cache.json、注册 HKCU 通知——
            // 子进程一样都不需要,且与母进程并发全量覆写同一批文件必丢更新。
            var launchKind = Service.PropertyWindowLink.ReadLaunchKind(out string payloadPath);
            switch (launchKind)
            {
                case Service.PropertyWindowLink.WindowKind.Properties:
                    _launchSnapshot = Service.PropertyWindowLink.ReadPropertiesPayload(payloadPath);
                    break;
                case Service.PropertyWindowLink.WindowKind.Whitelist:
                    _launchWhitelistSnapshot = Service.PropertyWindowLink.ReadWhitelistPayload(payloadPath);
                    break;
                case Service.PropertyWindowLink.WindowKind.MpkgQueue:
                    _launchMpkgSnapshot = Service.PropertyWindowLink.ReadMpkgPayload(payloadPath);
                    break;
            }

            if (_launchSnapshot != null || _launchWhitelistSnapshot != null || _launchMpkgSnapshot != null)
            {
                // 三种副窗口的差别只剩语言 / 日志级别 / 日志文件名 / 横幅上的名字。
                // 载荷读失败(协议不匹配、json 坏了)时三份全为 null,那时当普通进程往下走,不建一个空壳副窗口。
                string childLanguage, childLevel, childLogFile, childLabel;
                if (_launchMpkgSnapshot != null)
                {
                    childLanguage = _launchMpkgSnapshot.Language;
                    childLevel = _launchMpkgSnapshot.LogLevel;
                    childLogFile = "mpkg.txt";
                    childLabel = "移动版队列副窗口";
                }
                else if (_launchWhitelistSnapshot != null)
                {
                    childLanguage = _launchWhitelistSnapshot.Language;
                    childLevel = _launchWhitelistSnapshot.LogLevel;
                    childLogFile = "whitelist.txt";
                    childLabel = "白名单副窗口";
                }
                else
                {
                    childLanguage = _launchSnapshot!.Language;
                    childLevel = _launchSnapshot.LogLevel;
                    childLogFile = "properties.txt";
                    childLabel = "属性副窗口";
                }
                ViewModel = null!; // 副模式不构建主 VM:三类副窗口都只吃快照,不再引用它
                ApplyLanguage(childLanguage);
                this.InitializeComponent();
                InitLogging(childProcess: true, forcedLevel: childLevel, childLogFile: childLogFile);
                HookGlobalExceptionHandlers();
                Log.Information("===={Child}子进程已启动。Pid={Pid}====", childLabel, Environment.ProcessId);
                return;
            }

            ViewModel = new SettingsViewModel(new ConfigService(), new PickerService());
            LoadInitialLanguage();
            this.InitializeComponent();
            InitLogging(childProcess: false);
            HookGlobalExceptionHandlers();

            Log.Information($"====应用程序已启动。路径：{AppDataRoot}====", AppDataRoot);

            // 进程退出时释放 Steamworks 原生资源
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                Service.SteamWorkshopService.GetInstance().Dispose();
            };
        }

        /// <summary>日志初始化。子进程必须写独立文件:Serilog 的 File sink 默认独占写句柄,
        /// 第二个进程开同一个 log.txt 会在 sink 构造期抛 IOException,副窗口进程根本起不来。
        /// 两类子进程之间也要分文件——属性与白名单窗口可以同时开着。</summary>
        private static void InitLogging(bool childProcess, string forcedLevel = "", string childLogFile = "properties.txt")
        {
            AppDataRoot = GetAppDataRoot();
            string logPath = System.IO.Path.Combine(AppDataRoot, "logs",
                childProcess ? childLogFile : "log.txt");

            // 日志文件规范化:固定单文件,不做滚动(滚动会把活跃文件改成 log_001.txt,
            // 导致 Info 页日志面板读不到)。超 5MB 在启动时截断重写,防止无限增长。
            try
            {
                var logDir = System.IO.Path.GetDirectoryName(logPath) ?? AppDataRoot;
                Directory.CreateDirectory(logDir);
                // 清理历史遗留的滚动序号文件(旧版本 rollOnFileSizeLimit 产生),保持目录只有活跃文件
                foreach (var f in Directory.GetFiles(logDir, "log_*.txt"))
                    try { File.Delete(f); } catch { }
                if (new FileInfo(logPath).Length > 5 * 1024 * 1024)
                    File.WriteAllText(logPath, string.Empty);
            }
            catch { /* 日志初始化失败不阻塞启动 */ }

            // 级别来源:主进程从配置预读;子进程用快照带过来的值,不回头读 config.json
            string level = childProcess ? forcedLevel : ReadConfiguredLogLevel();
            if (level == "Off")
                LogLevelSwitch.MinimumLevel = LogEventLevel.Fatal;
            else if (Enum.TryParse<LogEventLevel>(level, true, out var parsed))
                LogLevelSwitch.MinimumLevel = parsed;

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(LogLevelSwitch) // 级别由设置页控制,运行时即时生效
                .WriteTo.File(logPath, fileSizeLimitBytes: 5 * 1024 * 1024,
                    rollOnFileSizeLimit: false)
                .CreateLogger();
        }

        /// <summary>日志级别预读:此处时机早于 Log.Logger 赋值,LoadAsync 自身的日志打给
        /// Serilog 默认静默器,不会落盘;文件缺失时 LoadAsync 会创建默认配置(与旧行为等价)。</summary>
        private static string ReadConfiguredLogLevel()
        {
            try
            {
                return new ConfigService().LoadAsync().GetAwaiter().GetResult()?.LogLevel ?? "";
            }
            catch { return ""; }
        }

        /// <summary>全局异常日志:任何线程的未处理异常都落盘;
        /// Steamworks 回调循环的异常(关闭 Steam 时管道断开)不杀死应用。</summary>
        private void HookGlobalExceptionHandlers()
        {
            UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            if (_launchSnapshot != null)
            {
                // 子进程:属性窗口就是本进程的全部。MainWindowInstance 指向它,App.GetPopupTheme()
                // 才能从 Content.RequestedTheme 取到当前主题,否则弹层掉回 Default。
                _window = new PropertiesWindow(_launchSnapshot);
                MainWindowInstance = _window;
                _window.Activate();
                return;
            }

            if (_launchWhitelistSnapshot != null)
            {
                _window = new Views.WhitelistWindow(_launchWhitelistSnapshot);
                MainWindowInstance = _window;
                _window.Activate();
                return;
            }

            if (_launchMpkgSnapshot != null)
            {
                // 移动版队列副窗口:本进程的全部就是这块面板,转换也由它自己起 repkg 子进程跑。
                _window = new Views.MpkgQueueWindow(_launchMpkgSnapshot);
                MainWindowInstance = _window;
                _window.Activate();
                return;
            }

            _window = new MainWindow();
            MainWindowInstance = _window;

            await ViewModel.InitializeAsync();

            // 保存完整扫描链路任务，页面可等待它确保扫描已开始
            InitialScanTask = ScanWallpaperWhenStart();

            // 系统通知(AppNotification):非打包应用注册 AUMID + 通知平台(失败仅记日志)
            NotificationService.Initialize();

            _window.Activate();
            LoadTheme();
            LoadNavigationMode();
        }
        /// <summary>UI 线程未处理异常:记录日志;Steamworks 相关的标记为已处理,避免关闭 Steam 时应用崩溃</summary>
        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            Log.Error(e.Exception, "未处理的 UI 线程异常");
            if (e.Exception.StackTrace?.Contains("Steamworks", StringComparison.Ordinal) == true)
                e.Handled = true;
        }

        /// <summary>非 UI 线程未处理异常:只记录(无法阻止进程终止,但能留下证据)</summary>
        private void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
                Log.Error(ex, "未处理的 AppDomain 异常");
            else
                Log.Error("未处理的 AppDomain 异常: {ExceptionObject}", e.ExceptionObject);
        }

        /// <summary>
        /// 数据根目录:所有用户数据(配置 config.json / 日志 logs / 缓存 wallpaper_cache.json / 清理白名单)
        /// 的统一根。便携模式(exe 同目录存在 portable.ini)→ 包内 Data\ 目录(真正随身带);
        /// 否则 → %LOCALAPPDATA%\WE_Tool(安装版/默认)。
        /// </summary>
        public static string GetAppDataRoot()
        {
            // 便携标记(exe 同目录,或上一级目录):portable 布局 = launcher + portable.ini 在包根,
            // 主程序在 app\ 子目录(Environment.ProcessPath 指向 app\WE_Tool.exe),需向上找一级。
            string exeDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            string? portableRoot = null;
            if (File.Exists(System.IO.Path.Combine(exeDir, "portable.ini")))
                portableRoot = exeDir;
            else if (System.IO.Path.GetDirectoryName(exeDir) is { } parent
                     && File.Exists(System.IO.Path.Combine(parent, "portable.ini")))
                portableRoot = parent;

            if (portableRoot != null)
            {
                string portableData = System.IO.Path.Combine(portableRoot, "Data");
                Directory.CreateDirectory(portableData);
                return portableData;
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appFolder = System.IO.Path.Combine(localAppData, "WE_Tool");
            System.IO.Directory.CreateDirectory(appFolder);
            return appFolder;
        }
        public void LoadTheme()
        {
            try
            {
                string theme = ViewModel.AppSettingsVM.Theme ?? "";

                ElementTheme elementTheme = theme switch
                {
                    "Dark" => ElementTheme.Dark,
                    "Light" => ElementTheme.Light,
                    _ => ElementTheme.Default
                };

                if (MainWindowInstance?.Content is FrameworkElement rootElement)
                {
                    rootElement.RequestedTheme = elementTheme;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "应用主题时发生异常。");
            }
        }

        /// <summary>应用主窗口导航栏模式(设置页即时切换与启动时共用)。</summary>
        public void LoadNavigationMode()
        {
            try
            {
                string mode = ViewModel.AppSettingsVM.NavigationMode ?? "Left";
                if (MainWindowInstance is MainWindow mainWindow)
                    mainWindow.ApplyNavigationMode(mode);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "应用导航模式时发生异常。");
            }
        }

        /// <summary>
        /// 当前生效的弹层主题:用户显式选了 Dark/Light 就返回它;Default(跟随系统)返回 Default 不干预。
        /// 弹层(ContentDialog/Flyout/MenuFlyout)挂在独立弹层,不自动继承主窗口根元素运行时设置的 RequestedTheme。
        /// </summary>
        public static ElementTheme GetPopupTheme()
            => MainWindowInstance?.Content is FrameworkElement root
                ? root.RequestedTheme
                : ElementTheme.Default;

        /// <summary>对弹层根元素显式应用当前主题(Default 时不写,保持跟随系统)。</summary>
        public static void ApplyPopupTheme(FrameworkElement popupRoot)
        {
            ElementTheme theme = GetPopupTheme();
            if (theme != ElementTheme.Default)
                popupRoot.RequestedTheme = theme;
        }

        /// <summary>
        /// Flyout/MenuFlyout/CommandBarFlyout Opened 事件通用处理:对弹层显式应用当前主题。
        /// XAML 里挂 Opened="FlyoutThemeRefresh_Opened"(code-behind 一行转发到本方法)。
        /// </summary>
        public static void ApplyFlyoutTheme(object? sender, object e)
        {
            switch (sender)
            {
                case Flyout flyout:
                    if (flyout.Content is FrameworkElement content)
                        ApplyThemeToFlyoutRoot(content);
                    break;
                case MenuFlyout menu:
                    FrameworkElement? firstItem = menu.Items.OfType<FrameworkElement>().FirstOrDefault();
                    if (firstItem != null)
                        ApplyThemeToFlyoutRoot(firstItem);
                    break;
                case CommandBarFlyout commandBar:
                    FrameworkElement? firstCommand = commandBar.PrimaryCommands.OfType<FrameworkElement>().FirstOrDefault()
                        ?? commandBar.SecondaryCommands.OfType<FrameworkElement>().FirstOrDefault();
                    if (firstCommand != null)
                        ApplyThemeToFlyoutRoot(firstCommand);
                    break;
                case TeachingTip tip:
                    ApplyPopupTheme(tip);
                    break;
            }
        }

        /// <summary>
        /// 从弹层内容元素沿可视树向上找弹层根(FlyoutPresenter/CommandBar)应用主题——
        /// 弹层底色由容器决定,只设内容元素会出现"容器浅色底 + 内容深色"混合;找不到时兜底设内容本身。
        /// </summary>
        private static void ApplyThemeToFlyoutRoot(FrameworkElement start)
        {
            DependencyObject current = start;
            for (int depth = 0; depth < 10 && current != null; depth++)
            {
                if (current is FlyoutPresenter or MenuFlyoutPresenter or CommandBar)
                {
                    ApplyPopupTheme((FrameworkElement)current);
                    return;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            ApplyPopupTheme(start);
        }

        private async Task ScanWallpaperWhenStart()
        {
            try
            {
                var settings = await _configService.LoadAsync();
                if (settings != null)
                {
                    // 应用日志级别配置(Off=关闭→Fatal 等效零输出;非法值回落默认级别)
                    if (settings.LogLevel == "Off")
                    {
                        LogLevelSwitch.MinimumLevel = LogEventLevel.Fatal;
                    }
                    else if (Enum.TryParse<LogEventLevel>(settings.LogLevel, true, out var configuredLevel))
                    {
                        LogLevelSwitch.MinimumLevel = configuredLevel;
                    }

                    StartBackgroundScan(
                        settings.Path.WorkshopPath,
                        settings.Path.OfficialPath,
                        settings.Path.ProjectPath,
                        settings.Path.AcfPath,
                        settings.Path.VdfPath,
                        settings.ScanCacheEnabled == "1"
                        );
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "初始化失败。");
            }
        }
        public static void StartBackgroundScan(string workShopPath, string officialPath, string projectPath, string acfPath, string? vdfPath = null, bool useCache = true)
        {
            // 防重入:每代扫描一个代号 + 取消令牌。新调用使上一代作废——
            // 上一代被取消、其晚到的结果按代号判旧直接丢弃,绝不覆盖新一代数据。
            // 注:CTS 无计时器、调用频度低,不做 Dispose(与在途 token 的竞态不值得冒)。
            int gen = Interlocked.Increment(ref _scanGeneration);
            try { _scanCts?.Cancel(); } catch (ObjectDisposedException) { /* 并发窗口,忽略 */ }
            var cts = new CancellationTokenSource();
            var ct = cts.Token;
            _scanCts = cts;

            ScanTask = Task.Run(async () =>
            {
                try
                {
                    var workShopListTask = WallpaperScanner.ScanWallpapers(workShopPath ?? "", "workshop", acfPath, vdfPath: vdfPath, useCache: useCache, ct: ct);
                    var officialListTask = WallpaperScanner.ScanWallpapers(officialPath ?? "", "official", "", useCache: useCache, ct: ct);
                    var projectListTask = WallpaperScanner.ScanWallpapers(projectPath ?? "", "mine", "", useCache: useCache, ct: ct);

                    var workShopList = await workShopListTask;
                    var officialList = await officialListTask;
                    var projectList = await projectListTask;

                    // 代号已过期 = 期间又发起了新扫描 → 本代结果作废,不覆盖
                    if (gen != Volatile.Read(ref _scanGeneration))
                    {
                        Log.Information("后台扫描(gen {Gen})已过期,丢弃结果", gen);
                        return;
                    }
                    ct.ThrowIfCancellationRequested();

                    GlobalAllWallpapers = workShopList.Concat(officialList).Concat(projectList).ToList();

                    ScanCompleted?.Invoke(null, EventArgs.Empty);
                }
                catch (OperationCanceledException)
                {
                    Log.Information("后台全局扫描(gen {Gen})已被新扫描取代,静默退出。", gen);
                    // 不清空 GlobalAllWallpapers、不发 ScanCompleted —— 那是新一代的事
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "后台全局扫描壁纸失败。");
                    if (gen == Volatile.Read(ref _scanGeneration))
                    {
                        // 真意外的异常:保留旧列表(好过整页空白),仍通知完成让页面刷新到保留数据
                        ScanCompleted?.Invoke(null, EventArgs.Empty);
                    }
                }
            });
        }
        public static void ApplyLanguage(string lang)
        {
            // 跟随系统时将系统 UI 语言码传给 WinRT 覆盖设置
            string targetLang = (string.IsNullOrEmpty(lang) || lang == "default")
                ? SystemLanguage
                : lang;

            // 重复检测：如果和当前设置相同，无需更新
            string currentLang = Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride;
            if (string.Equals(currentLang, targetLang, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = targetLang;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "设置语言覆盖时出现异常（WinRT限制，已忽略）");
            }

            // 热更新：重建当前 Page
            if (MainWindowInstance is MainWindow mainWindow)
                mainWindow.RefreshUILanguage();

            Log.Information("语言热切换完成: {Language}", targetLang);
        }

        private static void LoadInitialLanguage()
        {
            try
            {
                // 统一走 ConfigService 缓存:构造期首次调用填缓存(真读一次盘),
                // 之后 OnLaunched 的 InitializeAsync 及各处 LoadAsync 全部命中内存缓存。
                // 文件缺失时 LoadAsync 会创建默认配置并返回默认值,与旧行为等价
                // (旧实现文件缺失返回"跟随系统",而 InitializeAsync 本来也会创建文件)。
                var settings = new WE_Tool.Service.ConfigService().LoadAsync().GetAwaiter().GetResult();
                ApplyLanguage(settings?.AppLanguage ?? "default");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "加载语言失败，将使用系统默认语言");
            }
        }
    }
}
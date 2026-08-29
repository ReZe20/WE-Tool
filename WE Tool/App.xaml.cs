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

        public App()
        {
            ViewModel = new SettingsViewModel(new ConfigService(), new PickerService());
            LoadInitialLanguage();
            this.InitializeComponent();
            string appDataRoot = GetAppDataRoot();
            string logPath = System.IO.Path.Combine(appDataRoot, "logs", "log.txt");

            // 日志文件规范化:固定单文件 logs/log.txt,不做滚动(滚动会把活跃文件改成 log_001.txt,
            // 导致 Info 页日志面板读不到)。超 5MB 在启动时截断重写,防止无限增长。
            try
            {
                var logDir = System.IO.Path.GetDirectoryName(logPath) ?? appDataRoot;
                Directory.CreateDirectory(logDir);
                // 清理历史遗留的滚动序号文件(旧版本 rollOnFileSizeLimit 产生),保持目录只有 log.txt
                foreach (var f in Directory.GetFiles(logDir, "log_*.txt"))
                    try { File.Delete(f); } catch { }
                if (new FileInfo(logPath).Length > 5 * 1024 * 1024)
                    File.WriteAllText(logPath, string.Empty);
            }
            catch { /* 日志初始化失败不阻塞启动 */ }

            // 日志级别从配置预读:让启动横幅(下方第一条日志)起就受设置约束(含"关闭")。
            // 正式加载在 ScanWallpaperWhenStart(ConfigService 进程内缓存,只真读一次盘);
            // 此处时机早于 Log.Logger 赋值,LoadAsync 自身的日志打给 Serilog 默认静默器,不会落盘。
            try
            {
                var bootSettings = new ConfigService().LoadAsync().GetAwaiter().GetResult();
                if (bootSettings?.LogLevel == "Off")
                    LogLevelSwitch.MinimumLevel = LogEventLevel.Fatal;
                else if (Enum.TryParse<LogEventLevel>(bootSettings?.LogLevel, true, out var bootLevel))
                    LogLevelSwitch.MinimumLevel = bootLevel;
            }
            catch { /* 预读失败不阻塞启动,保持默认级别 */ }

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(LogLevelSwitch) // 级别由设置页控制,运行时即时生效
                .WriteTo.File(logPath, fileSizeLimitBytes: 5 * 1024 * 1024,
                    rollOnFileSizeLimit: false)
                .CreateLogger();

            // 全局异常日志:任何线程的未处理异常都记录到 log.txt;
            // Steamworks 回调循环的异常(关闭 Steam 时管道断开)不杀死应用
            UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

            Log.Information($"====应用程序已启动。路径：{appDataRoot}====", appDataRoot);

            // 进程退出时释放 Steamworks 原生资源
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                Service.SteamWorkshopService.GetInstance().Dispose();
            };
        }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            MainWindowInstance = _window;

            await ViewModel.InitializeAsync();

            // 保存完整扫描链路任务，页面可等待它确保扫描已开始
            InitialScanTask = ScanWallpaperWhenStart();

            // 系统通知(AppNotification):非打包应用注册 AUMID + 通知平台(失败仅记日志)
            NotificationService.Initialize();

            _window.Activate();
            LoadTheme();
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
        public static void ApplyFlyoutTheme(object sender, object e)
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
                string lang = settings?.AppLanguage ?? "default";

                // 跟随系统（空字符串或"default"）→ 不设置 PrimaryLanguageOverride
                // 文档：空字符串不是合法的 BCP-47 标签，set 会抛 COMException
                // 正确做法：完全不调用 setter，让系统默认生效
                if (string.IsNullOrEmpty(lang) || lang == "default")
                {
                    Log.Information("语言加载完成: 跟随系统默认");
                }
                else
                {
                    Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = lang;
                    Log.Information("语言加载完成: {Language}", lang);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "加载语言失败，将使用系统默认语言");
            }
        }
    }
}
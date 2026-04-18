using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
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
        public static event EventHandler? ScanCompleted;
        // 全局扫描进度事件（0-100）
        public static event EventHandler<int>? ScanProgressChanged;
        public static Window? MainWindowInstance { get; private set; }

        public App()
        {
            ViewModel = new SettingsViewModel(new ConfigService(), new PickerService());
            LoadInitialLanguage();
            this.InitializeComponent();
            string appDataRoot = GetAppDataRoot();
            string logPath = System.IO.Path.Combine(appDataRoot, "logs", "log.txt");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                .CreateLogger();

            Log.Information($"====应用程序已启动。路径：{appDataRoot}====", appDataRoot);

            LoadInitialLanguage();
        }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            MainWindowInstance = _window;

            await ViewModel.InitializeAsync();

            _window.Activate();
            ScanWallpaperWhenStart();
            LoadTheme();
        }
        public static string GetAppDataRoot()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appFolder = System.IO.Path.Combine(localAppData, "WE_Tool");
            System.IO.Directory.CreateDirectory(appFolder);
            return appFolder;
        }
        public void LoadTheme()
        {
            try
            {
                string theme = ViewModel.Theme ?? "";

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
        private async void ScanWallpaperWhenStart()
        {
            try
            {
                var settings = await _configService.LoadAsync();
                if (settings != null)
                {
                    StartBackgroundScan(
                        settings.Path.WorkshopPath,
                        settings.Path.OfficialPath,
                        settings.Path.ProjectPath,
                        settings.Path.AcfPath
                        );
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "初始化失败。");
            }
        }
        public static void StartBackgroundScan(string workShopPath, string officialPath, string projectPath, string acfPath)
        {
            ScanProgressChanged?.Invoke(null, 0);

            ScanTask = Task.Run(async () =>
            {
                try
                {
                    int sources = 3;
                    EventHandler<int>? handler = ScanProgressChanged;
                    IProgress<int> makeProgress(int index)
                    {
                        int baseOffset = (int)Math.Round(index * (100.0 / sources));
                        return new Progress<int>(val =>
                        {
                            double slot = 100.0 / sources;
                            int overall = Math.Min(100, (int)Math.Round(baseOffset + (val / 100.0) * slot));
                            handler?.Invoke(null, overall);
                        });
                    }

                    var workShopListTask = WallpaperScanner.ScanWallpapers(workShopPath ?? "", "workshop", acfPath, makeProgress(0));
                    var officialListTask = WallpaperScanner.ScanWallpapers(officialPath ?? "", "official", "", makeProgress(1));
                    var projectListTask = WallpaperScanner.ScanWallpapers(projectPath ?? "", "mine", "", makeProgress(2));

                    var workShopList = await workShopListTask;
                    var officialList = await officialListTask;
                    var projectList = await projectListTask;

                    GlobalAllWallpapers = workShopList.Concat(officialList).Concat(projectList).ToList();

                    ScanProgressChanged?.Invoke(null, 100);
                    ScanCompleted?.Invoke(null, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "后台全局扫描壁纸失败。");
                    GlobalAllWallpapers = [];
                    ScanProgressChanged?.Invoke(null, 100);
                }
            });
        }
        private static void LoadInitialLanguage()
        {
            try
            {
                string folderPath = GetAppDataRoot();
                var configPath = System.IO.Path.Combine(folderPath, "config.json");

                string json = File.ReadAllText(configPath);
                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                string lang = obj["AppLanguage"]?.ToString() ?? "default";

                if (!System.IO.File.Exists(configPath))
                {
                    Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "";
                }
                else if (lang == "default")
                {
                    Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "";
                }
                else
                {
                    Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = lang;
                }

                Log.Information("语言加载完成: {Language}", lang);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "加载语言失败，将使用系统默认语言");
            }
        }
    }
}
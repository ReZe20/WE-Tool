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
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public SettingsViewModel ViewModel { get; }
        private readonly IConfigService _configService = new ConfigService();
        public static List<WallpaperItem> GlobalAllWallpapers { get; private set; } = [];
        public static Task ScanTask { get; private set; } = Task.CompletedTask;
        public static event EventHandler? ScanCompleted;
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

            Log.Information($"应用程序已启动。路径：{appDataRoot}", appDataRoot);

            LoadInitialLanguage();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            MainWindowInstance = _window;
            _window.Activate();
            ScanWallpaperWhenStart();
        }
        public static string GetAppDataRoot()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appFolder = System.IO.Path.Combine(localAppData, "WE_Tool");
            System.IO.Directory.CreateDirectory(appFolder);
            return appFolder;
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
            ScanTask = Task.Run(async () =>
            {
                try
                {
                    var workShopList = await WallpaperScanner.ScanWallpapers(workShopPath ?? "", "workshop", acfPath);
                    var officialList = await WallpaperScanner.ScanWallpapers(officialPath ?? "", "official", "");
                    var projectList = await WallpaperScanner.ScanWallpapers(projectPath ?? "", "mine", "");

                    GlobalAllWallpapers = workShopList.Concat(officialList).Concat(projectList).ToList();
                    ScanCompleted?.Invoke(null, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "后台全局扫描壁纸失败。");
                    GlobalAllWallpapers = [];
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

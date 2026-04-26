#nullable enable
using ABI.Microsoft.UI.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Win32;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using WE_Tool.Helper;
using WE_Tool.Models;
using WE_Tool.Service;
using WE_Tool.ViewModels.Settings;
using static System.Formats.Asn1.AsnWriter;

namespace WE_Tool.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private ObservableCollection<WallpaperItem>? _previousSelectedWallpapers;
        public bool _isBatchUpdating = false;
        public int _wallpaperViewIndex;
        public bool _isAnnotatedScrollBarEnabled;

        private readonly IConfigService _configService;
        private readonly IPickerService _pickerService;
        private AppSettings _settings = new();
        private CancellationTokenSource? _saveCts;
        private readonly System.TimeSpan _saveDelay = System.TimeSpan.FromMilliseconds(500);
        private readonly SemaphoreSlim _saveSemaphore = new(1, 1);

        public IRelayCommand<string> ChangeSortCommand { get; }
        public IAsyncRelayCommand SaveCommand { get; }
        public IAsyncRelayCommand<object> BrowseFolderCommand { get; }
        public IAsyncRelayCommand<object> BrowseFileCommand { get; }
        public IAsyncRelayCommand<object> OpenFolderCommand { get; }
        public IAsyncRelayCommand<string> AutoDetectWorkshopPathCommand { get; }
        public IAsyncRelayCommand AutoDetectDownloadPathCommand { get; }

        public AppearanceViewModel Appearance { get; private set; } = null!;

        public PapersViewModel Papers { get; private set; } = null!;
        public Controls.Papers.PapersControlViewModel PapersControl { get; private set; } = null!;
        public Controls.Papers.TagViewModel Tags { get; private set; } = null!;
        public Controls.Papers.RatingViewModel Rating { get; private set; } = null!;
        public Controls.Papers.TypesViewModel Types { get; private set; } = null!;
        public Controls.Papers.SourceViewModel Source { get; private set; } = null!;

        [ObservableProperty]
        public partial string DownloadPath { get; set; } = null!;

        [ObservableProperty]
        public partial string WorkshopPath { get; set; } = null!;

        [ObservableProperty]
        public partial string ProjectPath { get; set; } = null!;

        [ObservableProperty]
        public partial string AcfPath { get; set; } = null!;

        [ObservableProperty]
        public partial string OfficialPath { get; set; } = null!;

        [ObservableProperty]
        public partial bool IgnoreExtension { get; set; }

        [ObservableProperty]
        public partial string IgnoreExtensionList { get; set; } = null!;

        [ObservableProperty]
        public partial bool OnlyExtension { get; set; }

        [ObservableProperty]
        public partial string OnlyExtensionList { get; set; } = null!;

        [ObservableProperty]
        public partial bool ConvertTEX { get; set; }

        [ObservableProperty]
        public partial bool OneFolder { get; set; }

        [ObservableProperty]
        public partial bool OutProjectJSON { get; set; }

        [ObservableProperty]
        public partial bool UseProjectName { get; set; }

        [ObservableProperty]
        public partial bool DontConvertTEX { get; set; }

        [ObservableProperty]
        public partial bool CoverAllFiles { get; set; }

        public SettingsViewModel(IConfigService configService, IPickerService pickerService)
        {
            _configService = configService;
            _pickerService = pickerService;

            Appearance = new AppearanceViewModel();

            PapersControl = new Controls.Papers.PapersControlViewModel(); 
            Tags = new Controls.Papers.TagViewModel();
            Source = new Controls.Papers.SourceViewModel();
            Types = new Controls.Papers.TypesViewModel();
            Rating = new Controls.Papers.RatingViewModel();

            PapersControl.PropertyChanged += OnPropertyChanged;
            Source.PropertyChanged += OnPropertyChanged;
            Appearance.PropertyChanged += OnPropertyChanged;
            Types.PropertyChanged += OnPropertyChanged;
            Tags.PropertyChanged += OnPropertyChanged;

            SaveCommand = new AsyncRelayCommand(SaveAsync);
            BrowseFolderCommand = new AsyncRelayCommand<object>(BrowseFolderAsync);
            BrowseFileCommand = new AsyncRelayCommand<object>(BrowseFileAsync);
            OpenFolderCommand = new AsyncRelayCommand<object>(OpenFolderAsync);
            AutoDetectWorkshopPathCommand = new AsyncRelayCommand<string>(AutoDetectWorkshopPathAsync);
            AutoDetectDownloadPathCommand = new AsyncRelayCommand(AutoDetectDownloadPathAsync);
        }
        private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_isBatchUpdating) return;
            _ = SaveAsync();
        }
        

#pragma warning disable CA1822 // ConfigPath在之后需要实例访问，不标记static
        public string ConfigPath
        {
            get
            {
                return System.IO.Path.Combine(App.GetAppDataRoot());
            }
            set { }
        }

#pragma warning disable CA1822 // LogPath在之后需要实例访问，不标记static
        public string LogPath
        {
            get
            {
                return System.IO.Path.Combine(App.GetAppDataRoot(), "logs");
            }
            set { }
        }

        public async Task InitializeAsync()
        {
            _isBatchUpdating = true;

            var loadedSettings = await _configService.LoadAsync();
            bool isNewConfig = loadedSettings == null;
            _settings = loadedSettings ?? new AppSettings();

            _settings = await _configService.LoadAsync() ?? new AppSettings();

            Appearance.LoadFromSettings(_settings);

            PapersControl.LoadFromSettings(_settings.Papers);
            Tags.LoadFromSettings(_settings.Expander);
            Source.LoadFromSettings(_settings.Expander);
            Types.LoadFromSettings(_settings.Expander);
            Rating.LoadFromSettings(_settings.Expander);

            DownloadPath = _settings.Path.DownloadPath;
            if (string.IsNullOrEmpty(DownloadPath))
                await AutoDetectDownloadPathAsync();

            string mode = "0000";
            WorkshopPath = _settings.Path.WorkshopPath;
            ProjectPath = _settings.Path.ProjectPath;
            AcfPath = _settings.Path.AcfPath;
            OfficialPath = _settings.Path.OfficialPath;

            if (string.IsNullOrEmpty(WorkshopPath))
                mode = mode.Remove(0, 1).Insert(0, "1");
            if (string.IsNullOrEmpty(ProjectPath))
                mode = mode.Remove(1, 1).Insert(1, "1");
            if (string.IsNullOrEmpty(AcfPath))
                mode = mode.Remove(2, 1).Insert(2, "1");
            if (string.IsNullOrEmpty(OfficialPath))
                mode = mode.Remove(3, 1).Insert(3, "1");

            if (mode.Contains('1'))
                await AutoDetectWorkshopPathAsync(mode);

            IgnoreExtension = _settings.Extract.IgnoreExtension;
            IgnoreExtensionList = _settings.Extract.IgnoreExtensionList;
            OnlyExtension = _settings.Extract.OnlyExtension;
            OnlyExtensionList = _settings.Extract.OnlyExtensionList;
            ConvertTEX = _settings.Extract.ConvertTEX;
            OneFolder = _settings.Extract.OneFolder;
            OutProjectJSON = _settings.Extract.OutProjectJSON;
            UseProjectName = _settings.Extract.UseProjectName;
            DontConvertTEX = _settings.Extract.DontConvertTEX;
            CoverAllFiles = _settings.Extract.CoverAllFiles;

            if (isNewConfig || mode.Contains('1') || string.IsNullOrEmpty(_settings.Path.DownloadPath))
            {
                SyncPathsToSettings();
                await _configService.SaveAsync(_settings);
            }

            _isBatchUpdating = false;
            await SaveAsync();
            OnPropertyChanged(string.Empty);
        }

        private void SyncPathsToSettings()
        {
            _settings.Path.DownloadPath = DownloadPath;
            _settings.Path.WorkshopPath = WorkshopPath;
            _settings.Path.ProjectPath = ProjectPath;
            _settings.Path.AcfPath = AcfPath;
            _settings.Path.OfficialPath = OfficialPath;
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            if (_isBatchUpdating) return;

            if (e.PropertyName == nameof(PapersControl.SelectedWallpaper) ||
                string.IsNullOrEmpty(e.PropertyName))
            {
                return;
            }

            _ = SaveAsync();
        }
        private async Task SaveAsync()
        {
            if (_isBatchUpdating) return;

            _saveCts?.Cancel();
            _saveCts = new CancellationTokenSource();

            await _saveSemaphore.WaitAsync();
            try
            {
                _settings.AppLanguage = Appearance.AppLanguage ?? "";
                _settings.StartPageTag = Appearance.StartPageTag;
                _settings.Theme = Appearance.Theme;

                _settings.Papers.IsBottomBarOpen = PapersControl.IsBottomBarOpen;
                _settings.Papers.WallpaperViewIndex = PapersControl.WallpaperViewIndex;
                _settings.Papers.AutoPlayGif = PapersControl.AutoPlayGif;
                _settings.Papers.IsWallpaperEnterAnimationEnabled = PapersControl.IsWallpaperEnterAnimationEnabled;
                _settings.Papers.WallpaperTagDisplayIndex = PapersControl.WallpaperTagDisplayIndex;
                _settings.Papers.IsAnnotatedScrollBarEnabled = PapersControl.IsAnnotatedScrollBarEnabled;
                _settings.Papers.WallpaperListMinWidth = PapersControl.WallpaperListMinWidth;
                _settings.Papers.LeftSplitViewPaneOpen = PapersControl.LeftSplitViewPaneOpen;
                _settings.Papers.RightSplitViewPaneOpen = PapersControl.RightSplitViewPaneOpen;

                _settings.Papers.IsSortAscending = PapersControl.IsSortAscending;
                _settings.Papers.SortOrder = PapersControl.SortOrder;
                _settings.Papers.DetailSelectionEnabled = PapersControl.DetailSelectionEnabled;
                _settings.Papers.FilterResultResponseDelay = PapersControl.FilterResultResponseDelay;

                _settings.Expander.TypeExpander = Types.TypeExpander;
                _settings.Expander.Scene = Types.Scene;
                _settings.Expander.Video = Types.Video;
                _settings.Expander.Web = Types.Web;
                _settings.Expander.Application = Types.Application;
                _settings.Expander.Preset = Types.Preset;
                _settings.Expander.Unknown = Types.Unknown;

                _settings.Expander.RatingExpander = Rating.RatingExpander;
                _settings.Expander.G = Rating.G;
                _settings.Expander.Pg = Rating.Pg;
                _settings.Expander.R = Rating.R;

                _settings.Expander.SourceExpander = Source.SourceExpander;
                _settings.Expander.Official = Source.Official;
                _settings.Expander.Workshop = Source.Workshop;
                _settings.Expander.Mine = Source.Mine;

                _settings.Expander.TagsExpander = Tags.TagsExpander;
                _settings.Expander.Abstract = Tags.Abstract;
                _settings.Expander.Animal = Tags.Animal;
                _settings.Expander.Anime = Tags.Anime;
                _settings.Expander.Cartoon = Tags.Cartoon;
                _settings.Expander.Cgi = Tags.Cgi;
                _settings.Expander.Cyberpunk = Tags.Cyberpunk;
                _settings.Expander.Fantasy = Tags.Fantasy;
                _settings.Expander.Game = Tags.Game;
                _settings.Expander.Girls = Tags.Girls;
                _settings.Expander.Guys = Tags.Guys;
                _settings.Expander.Landscape = Tags.Landscape;
                _settings.Expander.Medieval = Tags.Medieval;
                _settings.Expander.Memes = Tags.Memes;
                _settings.Expander.Mmd = Tags.Mmd;
                _settings.Expander.Music = Tags.Music;
                _settings.Expander.Nature = Tags.Nature;
                _settings.Expander.Pixelart = Tags.Pixelart;
                _settings.Expander.Relaxing = Tags.Relaxing;
                _settings.Expander.Retro = Tags.Retro;
                _settings.Expander.SciFi = Tags.SciFi;
                _settings.Expander.Sports = Tags.Sports;
                _settings.Expander.Technology = Tags.Technology;
                _settings.Expander.Television = Tags.Television;
                _settings.Expander.Vehicle = Tags.Vehicle;
                _settings.Expander.Unspecified = Tags.Unspecified;

                _settings.Path.DownloadPath = DownloadPath;
                _settings.Path.WorkshopPath = WorkshopPath;
                _settings.Path.ProjectPath = ProjectPath;
                _settings.Path.OfficialPath = OfficialPath;
                _settings.Path.AcfPath = AcfPath;

                _settings.Extract.IgnoreExtension = IgnoreExtension;
                _settings.Extract.IgnoreExtensionList = IgnoreExtensionList;
                _settings.Extract.OnlyExtension = OnlyExtension;
                _settings.Extract.OnlyExtensionList = OnlyExtensionList;
                _settings.Extract.ConvertTEX = ConvertTEX;
                _settings.Extract.OneFolder = OneFolder;
                _settings.Extract.OutProjectJSON = OutProjectJSON;
                _settings.Extract.UseProjectName = UseProjectName;
                _settings.Extract.DontConvertTEX = DontConvertTEX;
                _settings.Extract.CoverAllFiles = CoverAllFiles;

                await _configService.SaveAsync(_settings);
            }
            finally
            {
                _saveSemaphore.Release();
            }
        }

        private async Task BrowseFileAsync(object? parameter)
        {
            var filePath = await _pickerService.PickFileAsync();

            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath) && parameter != null)
            {
                AcfPath = filePath;
                await SaveAsync();
            }
        }

        private async Task BrowseFolderAsync(object? parameter)
        {
            var path = await _pickerService.PickFolderAsync();
            if (!string.IsNullOrEmpty(path))
            {
                var key = (parameter as string) ?? "WorkshopPath";
                switch (key)
                {
                    case "WorkshopPath":
                        WorkshopPath = path;
                        break;
                    case "ProjectPath":
                        ProjectPath = path;
                        break;
                    case "AcfPath":
                        AcfPath = path;
                        break;
                    case "OfficialPath":
                        OfficialPath = path;
                        break;
                    default:
                        DownloadPath = path;
                        break;
                }
            }
        }

        private async Task OpenFolderAsync(object? parameter)
        {
            var key = (parameter as string) ?? "DownloadPath";

            if (key == "OpenSelectedWallpapers")
            {
                var itemToOpen = PapersControl.SelectedWallpapers.Count > 0
                    ? PapersControl.SelectedWallpapers.ToList()
                    : PapersControl.SelectedWallpaper is { FolderPath: not null }
                    ? [PapersControl.SelectedWallpaper]
                    : [];

                await ParallelOpenFoldersAsync(itemToOpen);
                return;
            }

            string? targetPath = key switch
            {
                "WorkshopPath" => WorkshopPath,
                "ProjectPath" => ProjectPath,
                "AcfPath" => AcfPath,
                "OfficialPath" => OfficialPath,
                "ConfigPath" => ConfigPath,
                "LogPath" => LogPath,
                "DownloadPath" => DownloadPath,
                _ => key
            };

            if (!string.IsNullOrEmpty(targetPath) && !Directory.Exists(targetPath))
            {
                try
                {
                    Directory.CreateDirectory(targetPath);
                }
                catch (Exception ex)
                {
                    await DialogHelper.ShowMessageAsync("错误", $"打开目录不存在，程序在创建目录时失败: {ex.Message}");
                    Log.Error(ex, "创建目录时出现异常。");
                    return;
                }
            }

            if (string.IsNullOrEmpty(targetPath))
            {
                await DialogHelper.ShowMessageAsync("错误", "打开目录为空，请先选择目录。");
                Log.Warning("用户尝试打开空目录。");
                return;
            }

            await _pickerService.OpenFolderAsync(targetPath);
        }

        public async Task OpenSelectedWallpapersFoldersAsync()
        {
            await OpenFolderAsync("OpenSelectedWallpapers");
        }

        public async Task RemoveWorkshopKeyFromAcfAsync(string workshopID, string acfPath)
        {
            if (string.IsNullOrEmpty(workshopID) || !File.Exists(acfPath)) return;

            await Task.Run(() =>
            {
                try
                {
                    string content = File.ReadAllText(acfPath);
                    string pattern = $@"\s*""{workshopID}""\s*\{{[\s\S]*?\}}";

                    if (System.Text.RegularExpressions.Regex.IsMatch(content, pattern))
                    {
                        string newContent = System.Text.RegularExpressions.Regex.Replace(content, pattern, string.Empty);
                        File.WriteAllText(acfPath, newContent);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"[ViewModel] 清理 ACF 键值失败: {workshopID}");
                }
            });
        }

        private async Task ParallelOpenFoldersAsync(List<WallpaperItem> items)
        {
            if (items.Count > 5)
            {
                bool isConfirmed = await DialogHelper.ShowConfirmDialogAsync(
                    "确认打开",
                    $"这将打开 {items.Count} 个文件资源管理器，是否继续打开？",
                    "确定",
                    "取消");
                if (!isConfirmed) return;
            }

            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.FolderPath)) continue;
                await OpenSingleFolderAsync(item.FolderPath);
            }
        }

        private async Task OpenSingleFolderAsync(string path)
        {
            if (!Directory.Exists(path))
            {
                try
                {
                    Directory.CreateDirectory(path);
                }
                catch (Exception ex)
                {
                    await DialogHelper.ShowMessageAsync("错误", $"尝试打开目录时发现目录不存在，尝试创建目录失败：{ex.Message}");
                }
            }

            await _pickerService.OpenFolderAsync(path);
        }

        public async Task AutoDetectWorkshopPathAsync(string? mode)
        {
            if (string.IsNullOrEmpty(mode))
            {
                return;
            }

            if (mode == "0000") return;

            var result = await Task.Run(() =>
            {
                string? foundBaseDir = null;
                try
                {
                    using RegistryKey rootKey = Registry.CurrentUser;

                    string[] possibleSubKeys = [@"Software\WallpaperEngine", @"Software\Wallpaper Engine"];
                    foreach (var subKey in possibleSubKeys)
                    {
                        using RegistryKey? weKey = rootKey.OpenSubKey(subKey);

                        if (weKey == null) continue;
                        object? installPath = weKey.GetValue("installPath");
                        if (installPath is string pathStr)
                        {
                            string targetSuffix = @"\common\wallpaper_engine";
                            int index = pathStr.ToLower().LastIndexOf(targetSuffix);
                            if (index != -1)
                            {
                                foundBaseDir = pathStr[..index];
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "读取WallpaperEngine注册表出现异常。");
                }
                return foundBaseDir;
            });

            if (!string.IsNullOrEmpty(result))
            {
                if (mode[0] == '1')
                    WorkshopPath = result + @"\workshop\content\431960";
                if (mode[1] == '1')
                    ProjectPath = result + @"\common\wallpaper_engine\projects\myprojects";
                if (mode[2] == '1')
                    AcfPath = result + @"\workshop\appworkshop_431960.acf";
                if (mode[3] == '1')
                    OfficialPath = result + @"\common\wallpaper_engine\projects\defaultprojects";
            }
        }

        public async Task AutoDetectDownloadPathAsync()
        {
            try
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (!string.IsNullOrEmpty(desktopPath))
                {
                    DownloadPath = desktopPath + "\\WE_OutPut";
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "设置桌面路径为保存路径时出现异常。");
            }
        }

        private static async Task ShowRestartDialog()
        {
            var xamlRoot = App.MainWindowInstance?.Content?.XamlRoot;
            if (xamlRoot == null)
            {
                Log.Warning("无法显示重启对话框：XamlRoot 为空。");
                return;
            }
            ContentDialog dialog = new()
            {
                Title = "需要重启",
                Content = "更改语言设置后需要重启应用程序才能完全生效。是否现在重启？",
                PrimaryButtonText = "立即重启",
                CloseButtonText = "稍后重启",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
            }
        }
    }
}
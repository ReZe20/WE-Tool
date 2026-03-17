#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Win32;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.ComponentModel;
using WE_Tool.Helper;
using WE_Tool.Models;
using WE_Tool.Service;
using static System.Formats.Asn1.AsnWriter;

namespace WE_Tool.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        public bool _isBatchUpdating = false;

        public ObservableCollection<WallpaperItem> SelectedWallpapers { get; set; } = [];

        private readonly IConfigService _configService;
        private readonly IPickerService _pickerService;
        private AppSettings _settings = new();
        private CancellationTokenSource? _saveCts;
        private readonly TimeSpan _saveDelay = TimeSpan.FromMilliseconds(500);
        private readonly SemaphoreSlim _saveSemaphore = new(1, 1);

        public IRelayCommand<string> ChangeSortCommand { get; }
        public IAsyncRelayCommand SaveCommand { get; }
        public IAsyncRelayCommand<object> BrowseFolderCommand { get; }
        public IAsyncRelayCommand<object> BrowseFileCommand { get; }
        public IAsyncRelayCommand<object> OpenFolderCommand { get; }
        public IAsyncRelayCommand<string> AutoDetectWorkshopPathCommand { get; }
        public IAsyncRelayCommand AutoDetectDownloadPathCommand { get; }

        [ObservableProperty]
        public partial WallpaperItem? SelectedWallpaper { get; set; }

        [ObservableProperty]
        public partial string AppLanguage { get; set; } = null!;

        [ObservableProperty]
        public partial string StartPageTag { get; set; } = null!;

        [ObservableProperty]
        public partial int BottomBarHeight { get; set; }

        [ObservableProperty]
        public partial bool IsBottomBarOpen { get; set; }

        [ObservableProperty]
        public partial bool AutoPlayGif { get; set; }

        [ObservableProperty]
        public partial int WallpaperListMinWidth { get; set; }

        [ObservableProperty]
        public partial bool LeftSplitViewPaneOpen { get; set; }

        [ObservableProperty]
        public partial bool RightSplitViewPaneOpen { get; set; }

        [ObservableProperty]
        public partial bool DetailSelectionEnabled { get; set; }

        [ObservableProperty]
        public partial int FilterResultResponseDelay { get; set; }

        [ObservableProperty]
        public partial int SortOrder { get; set; }

        [ObservableProperty]
        public partial string SortGlyph { get; set; } = "\uE8D2";

        [ObservableProperty]
        public partial string SortText { get; set; } = LanguageHelper.GetResource("SortByName.Text");

        [ObservableProperty]
        public partial bool IsSortAscending { get; set; }

        [ObservableProperty]
        public partial bool TypeExpander { get; set; }

        [ObservableProperty]
        public partial bool Scene { get; set; }

        [ObservableProperty]
        public partial bool Video { get; set; }

        [ObservableProperty]
        public partial bool Web { get; set; }

        [ObservableProperty]
        public partial bool Application { get; set; }

        [ObservableProperty]
        public partial bool Preset { get; set; }

        [ObservableProperty]
        public partial bool Unknown { get; set; }

        [ObservableProperty]
        public partial bool RatingExpander { get; set; }

        [ObservableProperty]
        public partial bool G { get; set; }

        [ObservableProperty]
        public partial bool Pg { get; set; }

        [ObservableProperty]
        public partial bool R { get; set; }

        [ObservableProperty]
        public partial bool SourceExpander { get; set; }

        [ObservableProperty]
        public partial bool Official { get; set; }

        [ObservableProperty]
        public partial bool Workshop { get; set; }

        [ObservableProperty]
        public partial bool Mine { get; set; }

        [ObservableProperty]
        public partial bool TagsExpander { get; set; }

        [ObservableProperty]
        public partial bool Abstract { get; set; }

        [ObservableProperty]
        public partial bool Animal { get; set; }

        [ObservableProperty]
        public partial bool Anime { get; set; }

        [ObservableProperty]
        public partial bool Cartoon { get; set; }

        [ObservableProperty]
        public partial bool Cgi { get; set; }

        [ObservableProperty]
        public partial bool Cyberpunk { get; set; }

        [ObservableProperty]
        public partial bool Fantasy { get; set; }

        [ObservableProperty]
        public partial bool Game { get; set; }

        [ObservableProperty]
        public partial bool Girls { get; set; }

        [ObservableProperty]
        public partial bool Guys { get; set; }

        [ObservableProperty]
        public partial bool Landscape { get; set; }

        [ObservableProperty]
        public partial bool Medieval { get; set; }

        [ObservableProperty]
        public partial bool Memes { get; set; }

        [ObservableProperty]
        public partial bool Mmd { get; set; }

        [ObservableProperty]
        public partial bool Music { get; set; }

        [ObservableProperty]
        public partial bool Nature { get; set; }

        [ObservableProperty]
        public partial bool Pixelart { get; set; }

        [ObservableProperty]
        public partial bool Relaxing { get; set; }

        [ObservableProperty]
        public partial bool Retro { get; set; }

        [ObservableProperty]
        public partial bool SciFi { get; set; }

        [ObservableProperty]
        public partial bool Sports { get; set; }

        [ObservableProperty]
        public partial bool Technology { get; set; }

        [ObservableProperty]
        public partial bool Television { get; set; }

        [ObservableProperty]
        public partial bool Vehicle { get; set; }

        [ObservableProperty]
        public partial bool Unspecified { get; set; }

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

            ChangeSortCommand = new RelayCommand<string>(ExecuteChangeSort);
            SaveCommand = new AsyncRelayCommand(SaveAsync);
            BrowseFolderCommand = new AsyncRelayCommand<object>(BrowseFolderAsync);
            BrowseFileCommand = new AsyncRelayCommand<object>(BrowseFileAsync);
            OpenFolderCommand = new AsyncRelayCommand<object>(OpenFolderAsync);
            AutoDetectWorkshopPathCommand = new AsyncRelayCommand<string>(AutoDetectWorkshopPathAsync);
            AutoDetectDownloadPathCommand = new AsyncRelayCommand(AutoDetectDownloadPathAsync);
        }

        public void ExecuteChangeSort(string? parameter)
        {
            if (int.TryParse(parameter, out int newOrder))
            {
                SortOrder = newOrder;
            }
        }

        private void UpdateSortUI()
        {
            switch (SortOrder)
            {
                case 0:
                    SortGlyph = "\uE8D2";
                    SortText = LanguageHelper.GetResource("SortByName.Text");
                    break;
                case 1:
                    SortGlyph = "\uED0E";
                    SortText = LanguageHelper.GetResource("SortBySubTime.Text");
                    break;
                case 2:
                    SortGlyph = "\uF738";
                    SortText = LanguageHelper.GetResource("SortByLastTime.Text");
                    break;
                case 3:
                    SortGlyph = "\uEDA2";
                    SortText = LanguageHelper.GetResource("SortByFileSize.Text");
                    break;
            }
        }


        partial void OnAppLanguageChanged(string value)
        {
            if (_isBatchUpdating) return;

            _settings.AppLanguage = value ?? "default";
            _ = ShowRestartDialog();
        }

        partial void OnSortOrderChanged(int value)
        {
            UpdateSortUI();
        }

        partial void OnIsSortAscendingChanged(bool value)
        {
            OnPropertyChanged(nameof(SortDirectionGlyph));
        }

        partial void OnIsBottomBarOpenChanged(bool value)
        {
            BottomBarHeight = value ? 50 : 0;
        }

        public string SortDirectionGlyph => IsSortAscending ? "\uE70D" : "\uE70E";

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

            AppLanguage = _settings.AppLanguage ?? "default";
            StartPageTag = string.IsNullOrEmpty(_settings.StartPageTag) ? "Papers" : _settings.StartPageTag;

            BottomBarHeight = _settings.Papers.BottomBarHeight;
            IsBottomBarOpen = _settings.Papers.IsBottomBarOpen;
            AutoPlayGif = _settings.Papers.AutoPlayGif;
            WallpaperListMinWidth = _settings.Papers.WallpaperListMinWidth;
            LeftSplitViewPaneOpen = _settings.Papers.LeftSplitViewPaneOpen;
            RightSplitViewPaneOpen = _settings.Papers.RightSplitViewPaneOpen;
            DetailSelectionEnabled = _settings.Papers.DetailSelectionEnabled;
            FilterResultResponseDelay = _settings.Papers.FilterResultResponseDelay;

            IsSortAscending = _settings.Papers.IsSortAscending;
            SortOrder = _settings.Papers.SortOrder;

            TypeExpander = _settings.Expander.TypeExpander;
            Scene = _settings.Expander.Scene;
            Video = _settings.Expander.Video;
            Web = _settings.Expander.Web;
            Application = _settings.Expander.Application;
            Preset = _settings.Expander.Preset;
            Unknown = _settings.Expander.Unknown;

            RatingExpander = _settings.Expander.RatingExpander;
            G = _settings.Expander.G;
            Pg = _settings.Expander.Pg;
            R = _settings.Expander.R;

            SourceExpander = _settings.Expander.SourceExpander;
            Official = _settings.Expander.Official;
            Workshop = _settings.Expander.Workshop;
            Mine = _settings.Expander.Mine;

            TagsExpander = _settings.Expander.TagsExpander;
            Abstract = _settings.Expander.Abstract;
            Animal = _settings.Expander.Animal;
            Anime = _settings.Expander.Anime;
            Cartoon = _settings.Expander.Cartoon;
            Cgi = _settings.Expander.Cgi;
            Cyberpunk = _settings.Expander.Cyberpunk;
            Fantasy = _settings.Expander.Fantasy;
            Game = _settings.Expander.Game;
            Girls = _settings.Expander.Girls;
            Guys = _settings.Expander.Guys;
            Landscape = _settings.Expander.Landscape;
            Medieval = _settings.Expander.Medieval;
            Memes = _settings.Expander.Memes;
            Mmd = _settings.Expander.Mmd;
            Music = _settings.Expander.Music;
            Nature = _settings.Expander.Nature;
            Pixelart = _settings.Expander.Pixelart;
            Relaxing = _settings.Expander.Relaxing;
            Retro = _settings.Expander.Retro;
            SciFi = _settings.Expander.SciFi;
            Sports = _settings.Expander.Sports;
            Technology = _settings.Expander.Technology;
            Television = _settings.Expander.Television;
            Vehicle = _settings.Expander.Vehicle;
            Unspecified = _settings.Expander.Unspecified;

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

        public async Task ResetFiltersAsync(int mode, bool selectmode)
        {
            if (_isBatchUpdating) return;

            _isBatchUpdating = true;

            try
            {
                if (mode == 1)
                {
                    var actions = new List<Action>
                    {
                        () => Scene = selectmode,
                        () => Video = selectmode,
                        () => Web = selectmode,
                        () => Application = selectmode,
                        () => Unknown = selectmode,
                        () => G = selectmode,
                        () => Pg = selectmode,
                        () => R = selectmode,
                        () => Official = selectmode,
                        () => Workshop = selectmode,
                        () => Mine = selectmode,
                    };
                    foreach (var action in actions)
                    {
                        action();
                    }
                }
                if (mode == 1 || mode == 2)
                {
                    var tags = new List<Action>
                    {
                        () => Abstract = selectmode,
                        () => Animal = selectmode,
                        () => Anime = selectmode,
                        () => Cartoon = selectmode,
                        () => Cgi = selectmode,
                        () => Cyberpunk = selectmode,
                        () => Fantasy = selectmode,
                        () => Game = selectmode,
                        () => Girls = selectmode,
                        () => Guys = selectmode,
                        () => Landscape = selectmode,
                        () => Medieval = selectmode,
                        () => Memes = selectmode,
                        () => Mmd = selectmode,
                        () => Music = selectmode,
                        () => Nature = selectmode,
                        () => Pixelart = selectmode,
                        () => Relaxing = selectmode,
                        () => Retro = selectmode,
                        () => SciFi = selectmode,
                        () => Sports = selectmode,
                        () => Technology = selectmode,
                        () => Television = selectmode,
                        () => Vehicle = selectmode,
                        () => Unspecified = selectmode
                    };
                    foreach (var action in tags)
                    {
                        action();
                    }
                }
            }
            finally
            {
                _isBatchUpdating = false;
                OnPropertyChanged(nameof(Abstract));
                await SaveAsync();
            }
        }
        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            if (_isBatchUpdating) return;

            if (e.PropertyName == nameof(SelectedWallpaper) ||
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
                _settings.AppLanguage = AppLanguage ?? "";

                _settings.StartPageTag = StartPageTag;

                _settings.Papers.BottomBarHeight = BottomBarHeight;
                _settings.Papers.IsBottomBarOpen = IsBottomBarOpen;
                _settings.Papers.AutoPlayGif = AutoPlayGif;
                _settings.Papers.WallpaperListMinWidth = WallpaperListMinWidth;
                _settings.Papers.LeftSplitViewPaneOpen = LeftSplitViewPaneOpen;
                _settings.Papers.RightSplitViewPaneOpen = RightSplitViewPaneOpen;

                _settings.Papers.IsSortAscending = IsSortAscending;
                _settings.Papers.SortOrder = SortOrder;
                _settings.Papers.DetailSelectionEnabled = DetailSelectionEnabled;
                _settings.Papers.FilterResultResponseDelay = FilterResultResponseDelay;

                _settings.Expander.TypeExpander = TypeExpander;
                _settings.Expander.Scene = Scene;
                _settings.Expander.Video = Video;
                _settings.Expander.Web = Web;
                _settings.Expander.Application = Application;
                _settings.Expander.Preset = Preset;
                _settings.Expander.Unknown = Unknown;

                _settings.Expander.RatingExpander = RatingExpander;
                _settings.Expander.G = G;
                _settings.Expander.Pg = Pg;
                _settings.Expander.R = R;

                _settings.Expander.SourceExpander = SourceExpander;
                _settings.Expander.Official = Official;
                _settings.Expander.Workshop = Workshop;
                _settings.Expander.Mine = Mine;

                _settings.Expander.TagsExpander = TagsExpander;
                _settings.Expander.Abstract = Abstract;
                _settings.Expander.Animal = Animal;
                _settings.Expander.Anime = Anime;
                _settings.Expander.Cartoon = Cartoon;
                _settings.Expander.Cgi = Cgi;
                _settings.Expander.Cyberpunk = Cyberpunk;
                _settings.Expander.Fantasy = Fantasy;
                _settings.Expander.Game = Game;
                _settings.Expander.Girls = Girls;
                _settings.Expander.Guys = Guys;
                _settings.Expander.Landscape = Landscape;
                _settings.Expander.Medieval = Medieval;
                _settings.Expander.Memes = Memes;
                _settings.Expander.Mmd = Mmd;
                _settings.Expander.Music = Music;
                _settings.Expander.Nature = Nature;
                _settings.Expander.Pixelart = Pixelart;
                _settings.Expander.Relaxing = Relaxing;
                _settings.Expander.Retro = Retro;
                _settings.Expander.SciFi = SciFi;
                _settings.Expander.Sports = Sports;
                _settings.Expander.Technology = Technology;
                _settings.Expander.Television = Television;
                _settings.Expander.Vehicle = Vehicle;
                _settings.Expander.Unspecified = Unspecified;

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
                var itemToOpen = SelectedWallpapers.Count > 0
                    ? SelectedWallpapers.ToList()
                    : SelectedWallpaper is { FolderPath: not null }
                    ? [SelectedWallpaper]
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
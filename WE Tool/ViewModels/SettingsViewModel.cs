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
        private readonly TimeSpan _saveDelay = TimeSpan.FromMilliseconds(500);
        private readonly SemaphoreSlim _saveSemaphore = new(1, 1);

        public IAsyncRelayCommand SaveCommand { get; }

        public FilterExpanderViewModel FilterExpanderVM { get; } = new();
        public AppSettingsViewModel AppSettingsVM { get; } = new();
        public WallpaperDisplayViewModel WallpaperDisplayVM { get; } = new();
        public PathManagementViewModel PathManagementVM { get; }

        [ObservableProperty]
        public partial ObservableCollection<WallpaperItem> SelectedWallpapers { get; set; } = [];

        [ObservableProperty]
        public partial WallpaperItem? SelectedWallpaper { get; set; }

        public bool IsButtonInGridColumnEnabled
        {
            get => SelectedWallpapers.Count != 0 || SelectedWallpaper != null;
        }

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

            SaveCommand = new AsyncRelayCommand(SaveAsync);
            PathManagementVM = new PathManagementViewModel(_pickerService)
            {
                GetSelectedWallpapersToOpen = () =>
                {
                    var selected = SelectedWallpapers;
                    if (selected.Count > 0)
                        return [.. selected];
                    if (SelectedWallpaper is { FolderPath: not null })
                        return [SelectedWallpaper];
                    return [];
                }
            };
            PathManagementVM.SaveRequested += () => _ = SaveAsync();

            // 任意子 VM 的属性变化都触发保存
            AppSettingsVM.PropertyChanged += OnSubViewModelPropertyChanged;
            FilterExpanderVM.PropertyChanged += OnSubViewModelPropertyChanged;
            WallpaperDisplayVM.PropertyChanged += OnSubViewModelPropertyChanged;
            PathManagementVM.PropertyChanged += OnSubViewModelPropertyChanged;

            AppSettingsVM.PropertyChanged += (s, e) =>
            {
                if (_isBatchUpdating) return;

                switch (e.PropertyName)
                {
                    case nameof(AppSettingsViewModel.AppLanguage):
                        var appLang = AppSettingsVM.AppLanguage ?? "";
                        _settings.AppLanguage = appLang;
                        App.ApplyLanguage(appLang);
                        break;
                    case nameof(AppSettingsViewModel.Theme):
                        _settings.Theme = AppSettingsVM.Theme ?? "";
                        try
                        {
                            var app = Microsoft.UI.Xaml.Application.Current as App;
                            app?.LoadTheme();
                        }
                        catch (Exception ex)
                        {
                            Serilog.Log.Error(ex, "尝试应用主题时失败。");
                        }
                        break;
                }
            };
        }

        private void OnSubViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isBatchUpdating) return;
            _ = SaveAsync();
        }

        public void SuspendSelectedWallpapersCollectionChanged()
        {
            _previousSelectedWallpapers?.CollectionChanged -= OnSelectedWallpapersCollectionChanged;
        }
        public void ResumeSelectedWallpapersCollectionChanged()
        {
            _previousSelectedWallpapers?.CollectionChanged += OnSelectedWallpapersCollectionChanged;
            OnSelectedWallpaperChanged(SelectedWallpaper);
        }
        partial void OnSelectedWallpaperChanged(WallpaperItem value)
        {
            OnPropertyChanged(nameof(IsButtonInGridColumnEnabled));
        }
        public void OnSelectedWallpapersCollectionChanged(object? sender,System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(IsButtonInGridColumnEnabled));
        }
        partial void OnSelectedWallpapersChanged(ObservableCollection<WallpaperItem> value)
        {
            if (_previousSelectedWallpapers != null)
            {
                _previousSelectedWallpapers.CollectionChanged -= OnSelectedWallpapersCollectionChanged;
            }

            _previousSelectedWallpapers = value;
            if (value != null)
            {
                value.CollectionChanged += OnSelectedWallpapersCollectionChanged;
            }

            OnPropertyChanged(nameof(IsButtonInGridColumnEnabled));
        }


        public async Task InitializeAsync()
        {
            _isBatchUpdating = true;

            var loadedSettings = await _configService.LoadAsync();
            bool isNewConfig = loadedSettings == null;
            _settings = loadedSettings ?? new AppSettings();

            _settings = await _configService.LoadAsync() ?? new AppSettings();

            AppSettingsVM.AppLanguage = _settings.AppLanguage ?? "default";

            AppSettingsVM.StartPageTag = string.IsNullOrEmpty(_settings.StartPageTag) ? "Papers" : _settings.StartPageTag;
            AppSettingsVM.Theme = _settings.Theme;

            WallpaperDisplayVM.IsBottomBarOpen = _settings.Papers.IsBottomBarOpen;
            WallpaperDisplayVM.AutoPlayGif = _settings.Papers.AutoPlayGif;
            WallpaperDisplayVM.IsWallpaperEnterAnimationEnabled = _settings.Papers.IsWallpaperEnterAnimationEnabled;
            WallpaperDisplayVM.IsAnnotatedScrollBarEnabled = _settings.Papers.IsAnnotatedScrollBarEnabled;
            WallpaperDisplayVM.WallpaperTagDisplayIndex = _settings.Papers.WallpaperTagDisplayIndex;
            WallpaperDisplayVM.WallpaperViewIndex = _settings.Papers.WallpaperViewIndex;
            WallpaperDisplayVM.WallpaperListMinWidth = _settings.Papers.WallpaperListMinWidth;
            WallpaperDisplayVM.LeftSplitViewPaneOpen = _settings.Papers.LeftSplitViewPaneOpen;
            WallpaperDisplayVM.RightSplitViewPaneOpen = _settings.Papers.RightSplitViewPaneOpen;
            WallpaperDisplayVM.DetailSelectionEnabled = _settings.Papers.DetailSelectionEnabled;
            WallpaperDisplayVM.FilterResultResponseDelay = _settings.Papers.FilterResultResponseDelay;

            WallpaperDisplayVM.IsSortAscending = _settings.Papers.IsSortAscending;
            WallpaperDisplayVM.SortOrder = _settings.Papers.SortOrder;

            FilterExpanderVM.TypeExpander = _settings.Expander.TypeExpander;
            FilterExpanderVM.Scene = _settings.Expander.Scene;
            FilterExpanderVM.Video = _settings.Expander.Video;
            FilterExpanderVM.Web = _settings.Expander.Web;
            FilterExpanderVM.Application = _settings.Expander.Application;
            FilterExpanderVM.Preset = _settings.Expander.Preset;
            FilterExpanderVM.Unknown = _settings.Expander.Unknown;

            FilterExpanderVM.RatingExpander = _settings.Expander.RatingExpander;
            FilterExpanderVM.G = _settings.Expander.G;
            FilterExpanderVM.Pg = _settings.Expander.Pg;
            FilterExpanderVM.R = _settings.Expander.R;

            FilterExpanderVM.SourceExpander = _settings.Expander.SourceExpander;
            FilterExpanderVM.Official = _settings.Expander.Official;
            FilterExpanderVM.Workshop = _settings.Expander.Workshop;
            FilterExpanderVM.Mine = _settings.Expander.Mine;

            FilterExpanderVM.TagsExpander = _settings.Expander.TagsExpander;
            FilterExpanderVM.Abstract = _settings.Expander.Abstract;
            FilterExpanderVM.Animal = _settings.Expander.Animal;
            FilterExpanderVM.Anime = _settings.Expander.Anime;
            FilterExpanderVM.Cartoon = _settings.Expander.Cartoon;
            FilterExpanderVM.Cgi = _settings.Expander.Cgi;
            FilterExpanderVM.Cyberpunk = _settings.Expander.Cyberpunk;
            FilterExpanderVM.Fantasy = _settings.Expander.Fantasy;
            FilterExpanderVM.Game = _settings.Expander.Game;
            FilterExpanderVM.Girls = _settings.Expander.Girls;
            FilterExpanderVM.Guys = _settings.Expander.Guys;
            FilterExpanderVM.Landscape = _settings.Expander.Landscape;
            FilterExpanderVM.Medieval = _settings.Expander.Medieval;
            FilterExpanderVM.Memes = _settings.Expander.Memes;
            FilterExpanderVM.Mmd = _settings.Expander.Mmd;
            FilterExpanderVM.Music = _settings.Expander.Music;
            FilterExpanderVM.Nature = _settings.Expander.Nature;
            FilterExpanderVM.Pixelart = _settings.Expander.Pixelart;
            FilterExpanderVM.Relaxing = _settings.Expander.Relaxing;
            FilterExpanderVM.Retro = _settings.Expander.Retro;
            FilterExpanderVM.SciFi = _settings.Expander.SciFi;
            FilterExpanderVM.Sports = _settings.Expander.Sports;
            FilterExpanderVM.Technology = _settings.Expander.Technology;
            FilterExpanderVM.Television = _settings.Expander.Television;
            FilterExpanderVM.Vehicle = _settings.Expander.Vehicle;
            FilterExpanderVM.Unspecified = _settings.Expander.Unspecified;

            PathManagementVM.LoadFromSettings(_settings);
            if (string.IsNullOrEmpty(PathManagementVM.DownloadPath))
                await PathManagementVM.AutoDetectDownloadPathAsync();

            string mode = "0000";
            if (string.IsNullOrEmpty(PathManagementVM.WorkshopPath))
                mode = mode.Remove(0, 1).Insert(0, "1");
            if (string.IsNullOrEmpty(PathManagementVM.ProjectPath))
                mode = mode.Remove(1, 1).Insert(1, "1");
            if (string.IsNullOrEmpty(PathManagementVM.AcfPath))
                mode = mode.Remove(2, 1).Insert(2, "1");
            if (string.IsNullOrEmpty(PathManagementVM.OfficialPath))
                mode = mode.Remove(3, 1).Insert(3, "1");

            if (mode.Contains('1'))
                await PathManagementVM.AutoDetectWorkshopPathAsync(mode);

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
                PathManagementVM.SyncToSettings(_settings);
                await _configService.SaveAsync(_settings);
            }

            _isBatchUpdating = false;
            await SaveAsync();
            OnPropertyChanged(string.Empty);
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
                        () => FilterExpanderVM.Scene = selectmode,
                        () => FilterExpanderVM.Video = selectmode,
                        () => FilterExpanderVM.Web = selectmode,
                        () => FilterExpanderVM.Application = selectmode,
                        () => FilterExpanderVM.Unknown = selectmode,
                        () => FilterExpanderVM.G = selectmode,
                        () => FilterExpanderVM.Pg = selectmode,
                        () => FilterExpanderVM.R = selectmode,
                        () => FilterExpanderVM.Official = selectmode,
                        () => FilterExpanderVM.Workshop = selectmode,
                        () => FilterExpanderVM.Mine = selectmode,
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
                        () => FilterExpanderVM.Abstract = selectmode,
                        () => FilterExpanderVM.Animal = selectmode,
                        () => FilterExpanderVM.Anime = selectmode,
                        () => FilterExpanderVM.Cartoon = selectmode,
                        () => FilterExpanderVM.Cgi = selectmode,
                        () => FilterExpanderVM.Cyberpunk = selectmode,
                        () => FilterExpanderVM.Fantasy = selectmode,
                        () => FilterExpanderVM.Game = selectmode,
                        () => FilterExpanderVM.Girls = selectmode,
                        () => FilterExpanderVM.Guys = selectmode,
                        () => FilterExpanderVM.Landscape = selectmode,
                        () => FilterExpanderVM.Medieval = selectmode,
                        () => FilterExpanderVM.Memes = selectmode,
                        () => FilterExpanderVM.Mmd = selectmode,
                        () => FilterExpanderVM.Music = selectmode,
                        () => FilterExpanderVM.Nature = selectmode,
                        () => FilterExpanderVM.Pixelart = selectmode,
                        () => FilterExpanderVM.Relaxing = selectmode,
                        () => FilterExpanderVM.Retro = selectmode,
                        () => FilterExpanderVM.SciFi = selectmode,
                        () => FilterExpanderVM.Sports = selectmode,
                        () => FilterExpanderVM.Technology = selectmode,
                        () => FilterExpanderVM.Television = selectmode,
                        () => FilterExpanderVM.Vehicle = selectmode,
                        () => FilterExpanderVM.Unspecified = selectmode
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
                _settings.AppLanguage = AppSettingsVM.AppLanguage ?? "";

                _settings.StartPageTag = AppSettingsVM.StartPageTag;
                _settings.Theme = AppSettingsVM.Theme;

                _settings.Papers.IsBottomBarOpen = WallpaperDisplayVM.IsBottomBarOpen;
                _settings.Papers.WallpaperViewIndex = WallpaperDisplayVM.WallpaperViewIndex;
                _settings.Papers.AutoPlayGif = WallpaperDisplayVM.AutoPlayGif;
                _settings.Papers.IsWallpaperEnterAnimationEnabled = WallpaperDisplayVM.IsWallpaperEnterAnimationEnabled;
                _settings.Papers.WallpaperTagDisplayIndex = WallpaperDisplayVM.WallpaperTagDisplayIndex;
                _settings.Papers.IsAnnotatedScrollBarEnabled = WallpaperDisplayVM.IsAnnotatedScrollBarEnabled;
                _settings.Papers.WallpaperListMinWidth = WallpaperDisplayVM.WallpaperListMinWidth;
                _settings.Papers.LeftSplitViewPaneOpen = WallpaperDisplayVM.LeftSplitViewPaneOpen;
                _settings.Papers.RightSplitViewPaneOpen = WallpaperDisplayVM.RightSplitViewPaneOpen;

                _settings.Papers.IsSortAscending = WallpaperDisplayVM.IsSortAscending;
                _settings.Papers.SortOrder = WallpaperDisplayVM.SortOrder;
                _settings.Papers.DetailSelectionEnabled = WallpaperDisplayVM.DetailSelectionEnabled;
                _settings.Papers.FilterResultResponseDelay = WallpaperDisplayVM.FilterResultResponseDelay;

                _settings.Expander.TypeExpander = FilterExpanderVM.TypeExpander;
                _settings.Expander.Scene = FilterExpanderVM.Scene;
                _settings.Expander.Video = FilterExpanderVM.Video;
                _settings.Expander.Web = FilterExpanderVM.Web;
                _settings.Expander.Application = FilterExpanderVM.Application;
                _settings.Expander.Preset = FilterExpanderVM.Preset;
                _settings.Expander.Unknown = FilterExpanderVM.Unknown;

                _settings.Expander.RatingExpander = FilterExpanderVM.RatingExpander;
                _settings.Expander.G = FilterExpanderVM.G;
                _settings.Expander.Pg = FilterExpanderVM.Pg;
                _settings.Expander.R = FilterExpanderVM.R;

                _settings.Expander.SourceExpander = FilterExpanderVM.SourceExpander;
                _settings.Expander.Official = FilterExpanderVM.Official;
                _settings.Expander.Workshop = FilterExpanderVM.Workshop;
                _settings.Expander.Mine = FilterExpanderVM.Mine;

                _settings.Expander.TagsExpander = FilterExpanderVM.TagsExpander;
                _settings.Expander.Abstract = FilterExpanderVM.Abstract;
                _settings.Expander.Animal = FilterExpanderVM.Animal;
                _settings.Expander.Anime = FilterExpanderVM.Anime;
                _settings.Expander.Cartoon = FilterExpanderVM.Cartoon;
                _settings.Expander.Cgi = FilterExpanderVM.Cgi;
                _settings.Expander.Cyberpunk = FilterExpanderVM.Cyberpunk;
                _settings.Expander.Fantasy = FilterExpanderVM.Fantasy;
                _settings.Expander.Game = FilterExpanderVM.Game;
                _settings.Expander.Girls = FilterExpanderVM.Girls;
                _settings.Expander.Guys = FilterExpanderVM.Guys;
                _settings.Expander.Landscape = FilterExpanderVM.Landscape;
                _settings.Expander.Medieval = FilterExpanderVM.Medieval;
                _settings.Expander.Memes = FilterExpanderVM.Memes;
                _settings.Expander.Mmd = FilterExpanderVM.Mmd;
                _settings.Expander.Music = FilterExpanderVM.Music;
                _settings.Expander.Nature = FilterExpanderVM.Nature;
                _settings.Expander.Pixelart = FilterExpanderVM.Pixelart;
                _settings.Expander.Relaxing = FilterExpanderVM.Relaxing;
                _settings.Expander.Retro = FilterExpanderVM.Retro;
                _settings.Expander.SciFi = FilterExpanderVM.SciFi;
                _settings.Expander.Sports = FilterExpanderVM.Sports;
                _settings.Expander.Technology = FilterExpanderVM.Technology;
                _settings.Expander.Television = FilterExpanderVM.Television;
                _settings.Expander.Vehicle = FilterExpanderVM.Vehicle;
                _settings.Expander.Unspecified = FilterExpanderVM.Unspecified;

                _settings.Path.DownloadPath = PathManagementVM.DownloadPath;
                _settings.Path.WorkshopPath = PathManagementVM.WorkshopPath;
                _settings.Path.ProjectPath = PathManagementVM.ProjectPath;
                _settings.Path.OfficialPath = PathManagementVM.OfficialPath;
                _settings.Path.AcfPath = PathManagementVM.AcfPath;

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
    }
}
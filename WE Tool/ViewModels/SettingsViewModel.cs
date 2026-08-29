#nullable enable
using ABI.Microsoft.UI.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
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

        private readonly IConfigService _configService;
        private readonly IPickerService _pickerService;
        private AppSettings _settings = new();
        private CancellationTokenSource? _saveCts;
        private readonly TimeSpan _saveDelay = TimeSpan.FromMilliseconds(500);
        private readonly SemaphoreSlim _saveSemaphore = new(1, 1);
        private int _saveGuard;

        public IAsyncRelayCommand SaveCommand { get; }

        public FilterExpanderViewModel FilterExpanderVM { get; } = new();
        public ComponentsFilterViewModel ComponentsFilterVM { get; } = new();
        public AppSettingsViewModel AppSettingsVM { get; } = new();
        public WallpaperDisplayViewModel WallpaperDisplayVM { get; } = new();
        public ComponentsDisplayViewModel ComponentsDisplayVM { get; } = new();
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

        // === 目录过滤（自定义模式） ===

        [ObservableProperty]
        public partial bool OnlyPaths { get; set; }

        [ObservableProperty]
        public partial string OnlyPathsList { get; set; } = null!;

        [ObservableProperty]
        public partial bool IgnorePaths { get; set; }

        [ObservableProperty]
        public partial string IgnorePathsList { get; set; } = null!;

        /// <summary>OnlyPaths 勾选时，TextBox 才可编辑（父 CheckBox 禁用时自动级联）</summary>
        public bool IsOnlyPathsTextBoxEnabled => OnlyPaths;

        /// <summary>IgnorePaths 勾选时，TextBox 才可编辑（父 CheckBox 禁用时自动级联）</summary>
        public bool IsIgnorePathsTextBoxEnabled => IgnorePaths;

        partial void OnOnlyPathsChanged(bool value)
        {
            OnPropertyChanged(nameof(IsOnlyPathsTextBoxEnabled));
        }

        partial void OnIgnorePathsChanged(bool value)
        {
            OnPropertyChanged(nameof(IsIgnorePathsTextBoxEnabled));
        }

        [ObservableProperty]
        public partial int OneFolder { get; set; }

        [ObservableProperty]
        public partial bool OutProjectJSON { get; set; }

        [ObservableProperty]
        public partial bool UseProjectName { get; set; }

        /// <summary>平铺输出时的文件命名模式：0=保持原文件名, 1=按壁纸名命名（重复加序号）</summary>
        [ObservableProperty]
        public partial int FlatFileNamingMode { get; set; }

        /// <summary>子文件夹模式下保持源目录结构：0=保持, 1=打平</summary>
        [ObservableProperty]
        public partial int KeepSubfolderStructure { get; set; }

        [ObservableProperty]
        public partial bool CoverAllFiles { get; set; }

        /// <summary>0=覆盖已存在的文件, 1=跳过已提取的壁纸</summary>
        [ObservableProperty]
        public partial int OverwriteMode { get; set; }

        partial void OnOverwriteModeChanged(int value)
        {
            CoverAllFiles = value == 0;
            SkipExistingOutput = value == 1;
        }

        // === 输出设置的 IsEnabled 计算属性 ===
        /// <summary>子文件夹模式 (OneFolder==0) 时，冲突处理等才可操作</summary>
        public bool IsSubfolderModeContentEnabled => OneFolder == 0;
        /// <summary>全量输出(OutputMode==0)且不使用子文件夹(OneFolder==1)同时选中时，输出目录可能混乱，显示警告图标</summary>
        public bool IsConflictMode => OutputMode == 0 && OneFolder == 1;
        /// <summary>平铺模式 (OneFolder==1) 时，平铺相关控件可操作</summary>
        public bool IsFlatModeContentEnabled => OneFolder == 1;
        /// <summary>IgnoreExtension 勾选时，TextBox 才可编辑（父 CheckBox 禁用时自动级联）</summary>
        public bool IsIgnoreExtensionTextBoxEnabled => IgnoreExtension;
        /// <summary>OnlyExtension 勾选时，TextBox 才可编辑（父 CheckBox 禁用时自动级联）</summary>
        public bool IsOnlyExtensionTextBoxEnabled => OnlyExtension;

        partial void OnOneFolderChanged(int value)
        {
            OnPropertyChanged(nameof(IsSubfolderModeContentEnabled));
            OnPropertyChanged(nameof(IsConflictMode));
            OnPropertyChanged(nameof(IsFlatModeContentEnabled));
        }

        partial void OnIgnoreExtensionChanged(bool value)
        {
            OnPropertyChanged(nameof(IsIgnoreExtensionTextBoxEnabled));
        }

        partial void OnOnlyExtensionChanged(bool value)
        {
            OnPropertyChanged(nameof(IsOnlyExtensionTextBoxEnabled));
        }

        /// <summary>输出类型：0=全量输出, 1=仅输出媒体文件, 2=自定义</summary>
        [ObservableProperty]
        public partial int OutputMode { get; set; }

        partial void OnOutputModeChanged(int value)
        {
            OnPropertyChanged(nameof(IsConflictMode));
        }

        /// <summary>0=导出原始文件, 1=导出并转换TEX, 2=只导出TEX图片</summary>
        [ObservableProperty]
        public partial int TexExportMode { get; set; }

        /// <summary>效果图剔除阈值(%):0=关闭;1-100=透明或黑色占比达到该值的转换图整条目跳过</summary>
        [ObservableProperty]
        public partial int FilterEffectImagesThreshold { get; set; }

        /// <summary>效果图剔除开关(自定义模式):NumberBox 的 IsEnabled 单向绑定此值(即父 CheckBox 的 IsChecked)</summary>
        [ObservableProperty]
        public partial bool FilterEffectImagesEnabled { get; set; }

        // === 性能参数（阶段1） ===

        /// <summary>最大并发提取数，1 到 MaxConcurrentMax</summary>
        [ObservableProperty]
        public partial int MaxConcurrentExtractions { get; set; } = Environment.ProcessorCount;

        /// <summary>0=Normal, 1=BelowNormal, 2=Idle</summary>
        [ObservableProperty]
        public partial int ProcessPriority { get; set; }

        /// <summary>当前 CPU 逻辑核心数，用作 NumberBox 上限</summary>
        public int MaxConcurrentMax => Environment.ProcessorCount;

        // === 文件过滤（阶段3） ===

        [ObservableProperty]
        public partial bool SkipExistingOutput { get; set; }

        /// <summary>分块解析，逐条读取减少内存占用</summary>
        [ObservableProperty]
        public partial bool LazyLoad { get; set; } = true;
        /// <summary>日志记录级别(Off=关闭/Verbose/Debug/Information/Warning/Error/Fatal),修改即时生效。默认关闭。</summary>
        [ObservableProperty]
        public partial string LogLevel { get; set; } = "Off";

        partial void OnLogLevelChanged(string value)
        {
            // 运行时切换 Serilog 最小级别,无需重启。
            // Off(关闭):Serilog 无 Off 级别(LogEventLevel 仅 Verbose→Fatal 六档),
            // 最小级别提到 Fatal 即等效零输出——项目代码无 Log.Fatal 调用,已反射实证。
            if (value == "Off")
            {
                App.LogLevelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Fatal;
            }
            else if (Enum.TryParse<Serilog.Events.LogEventLevel>(value, true, out var level))
            {
                App.LogLevelSwitch.MinimumLevel = level;
            }
        }

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
            ComponentsFilterVM.PropertyChanged += OnSubViewModelPropertyChanged;
            WallpaperDisplayVM.PropertyChanged += OnSubViewModelPropertyChanged;
            ComponentsDisplayVM.PropertyChanged += OnSubViewModelPropertyChanged;
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
        partial void OnSelectedWallpaperChanged(WallpaperItem? value)
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


        private bool _initialized;

        public async Task InitializeAsync()
        {
            // 幂等守卫:OnLaunched 与 Papers 首载都会调用本方法,第二次直接跳过。
            // 每次完整执行都伴随整盘读 + 整盘写(LoadAsync + SaveAsync),重复执行纯属浪费。
            if (_initialized) return;
            _initialized = true;

            _isBatchUpdating = true;

            _settings = await _configService.LoadAsync() ?? new AppSettings();


            await ApplySettingsToViewModelsAsync();


            _isBatchUpdating = false;
            await SaveAsync();
            OnPropertyChanged(string.Empty);
        }

        /// <summary>
        /// 将 _settings 的当前值填充到所有子 VM 与页面属性（InitializeAsync 与 ResetAllSettingsAsync 共用）。
        /// 路径为空时触发自动检测，并将检测结果同步回 _settings。
        /// </summary>
        private async Task ApplySettingsToViewModelsAsync()
        {
            AppSettingsVM.AppLanguage = _settings.AppLanguage ?? "default";

            AppSettingsVM.StartPageTag = string.IsNullOrEmpty(_settings.StartPageTag) ? "Papers" : _settings.StartPageTag;
            AppSettingsVM.Theme = _settings.Theme;
            AppSettingsVM.ScanCacheEnabled = _settings.ScanCacheEnabled;
            AppSettingsVM.RestoreWindowGeometry = _settings.RestoreWindowGeometry;
            AppSettingsVM.RestorePropertiesWindowSize = _settings.RestorePropertiesWindowSize;

            WallpaperDisplayVM.IsBottomBarOpen = _settings.Papers.IsBottomBarOpen;
            WallpaperDisplayVM.AutoPlayGif = _settings.Papers.AutoPlayGif;
            WallpaperDisplayVM.IsWallpaperEnterAnimationEnabled = _settings.Papers.IsWallpaperEnterAnimationEnabled;
            WallpaperDisplayVM.WallpaperTagDisplayIndex = _settings.Papers.WallpaperTagDisplayIndex;
            WallpaperDisplayVM.WallpaperViewIndex = _settings.Papers.WallpaperViewIndex;
            WallpaperDisplayVM.BlurEveryone = _settings.Papers.BlurEveryone;
            WallpaperDisplayVM.BlurTeen = _settings.Papers.BlurTeen;
            WallpaperDisplayVM.BlurAdult = _settings.Papers.BlurAdult;
            WallpaperDisplayVM.WallpaperDisplayMode = _settings.Papers.WallpaperDisplayMode;
            WallpaperDisplayVM.WallpaperListMinWidth = _settings.Papers.WallpaperListMinWidth;
            WallpaperDisplayVM.LeftSplitViewPaneOpen = _settings.Papers.LeftSplitViewPaneOpen;
            WallpaperDisplayVM.RightSplitViewPaneOpen = _settings.Papers.RightSplitViewPaneOpen;
            WallpaperDisplayVM.RightPanelIndex = _settings.Papers.RightPanelIndex;
            WallpaperDisplayVM.DetailSelectionEnabled = _settings.Papers.DetailSelectionEnabled;
            WallpaperDisplayVM.FilterResultResponseDelay = _settings.Papers.FilterResultResponseDelay;
            WallpaperDisplayVM.PaginationMode = _settings.Papers.PaginationMode;

            WallpaperDisplayVM.IsSortAscending = _settings.Papers.IsSortAscending;
            WallpaperDisplayVM.SortOrder = _settings.Papers.SortOrder;

            FilterExpanderVM.TypeExpander = _settings.Papers.Expander.TypeExpander;
            FilterExpanderVM.Scene = _settings.Papers.Expander.Scene;
            FilterExpanderVM.Video = _settings.Papers.Expander.Video;
            FilterExpanderVM.Web = _settings.Papers.Expander.Web;
            FilterExpanderVM.Application = _settings.Papers.Expander.Application;
            FilterExpanderVM.Preset = _settings.Papers.Expander.Preset;
            FilterExpanderVM.Unknown = _settings.Papers.Expander.Unknown;

            FilterExpanderVM.RatingExpander = _settings.Papers.Expander.RatingExpander;
            FilterExpanderVM.G = _settings.Papers.Expander.G;
            FilterExpanderVM.Pg = _settings.Papers.Expander.Pg;
            FilterExpanderVM.R = _settings.Papers.Expander.R;

            FilterExpanderVM.SourceExpander = _settings.Papers.Expander.SourceExpander;
            FilterExpanderVM.Official = _settings.Papers.Expander.Official;
            FilterExpanderVM.Workshop = _settings.Papers.Expander.Workshop;
            FilterExpanderVM.Mine = _settings.Papers.Expander.Mine;

            FilterExpanderVM.SubscriptionExpander = _settings.Papers.Expander.SubscriptionExpander;
            FilterExpanderVM.Subscribed = _settings.Papers.Expander.Subscribed;
            FilterExpanderVM.Unsubscribed = _settings.Papers.Expander.Unsubscribed;

            FilterExpanderVM.TagsExpander = _settings.Papers.Expander.TagsExpander;
            FilterExpanderVM.Abstract = _settings.Papers.Expander.Abstract;
            FilterExpanderVM.Animal = _settings.Papers.Expander.Animal;
            FilterExpanderVM.Anime = _settings.Papers.Expander.Anime;
            FilterExpanderVM.Cartoon = _settings.Papers.Expander.Cartoon;
            FilterExpanderVM.Cgi = _settings.Papers.Expander.Cgi;
            FilterExpanderVM.Cyberpunk = _settings.Papers.Expander.Cyberpunk;
            FilterExpanderVM.Fantasy = _settings.Papers.Expander.Fantasy;
            FilterExpanderVM.Game = _settings.Papers.Expander.Game;
            FilterExpanderVM.Girls = _settings.Papers.Expander.Girls;
            FilterExpanderVM.Guys = _settings.Papers.Expander.Guys;
            FilterExpanderVM.Landscape = _settings.Papers.Expander.Landscape;
            FilterExpanderVM.Medieval = _settings.Papers.Expander.Medieval;
            FilterExpanderVM.Memes = _settings.Papers.Expander.Memes;
            FilterExpanderVM.Mmd = _settings.Papers.Expander.Mmd;
            FilterExpanderVM.Music = _settings.Papers.Expander.Music;
            FilterExpanderVM.Nature = _settings.Papers.Expander.Nature;
            FilterExpanderVM.Pixelart = _settings.Papers.Expander.Pixelart;
            FilterExpanderVM.Relaxing = _settings.Papers.Expander.Relaxing;
            FilterExpanderVM.Retro = _settings.Papers.Expander.Retro;
            FilterExpanderVM.SciFi = _settings.Papers.Expander.SciFi;
            FilterExpanderVM.Sports = _settings.Papers.Expander.Sports;
            FilterExpanderVM.Technology = _settings.Papers.Expander.Technology;
            FilterExpanderVM.Television = _settings.Papers.Expander.Television;
            FilterExpanderVM.Vehicle = _settings.Papers.Expander.Vehicle;
            FilterExpanderVM.Unspecified = _settings.Papers.Expander.Unspecified;

            // === Components 加载 ===
            ComponentsFilterVM.TypeExpander = _settings.Components.Expander.TypeExpander;
            ComponentsFilterVM.RatingExpander = _settings.Components.Expander.RatingExpander;
            ComponentsFilterVM.TagsExpander = _settings.Components.Expander.TagsExpander;
            ComponentsFilterVM.Layers = _settings.Components.Expander.Layers;
            ComponentsFilterVM.Scripts = _settings.Components.Expander.Scripts;
            ComponentsFilterVM.Effects = _settings.Components.Expander.Effects;
            ComponentsFilterVM.Everyone = _settings.Components.Expander.Everyone;
            ComponentsFilterVM.Questionable = _settings.Components.Expander.Questionable;
            ComponentsFilterVM.Mature = _settings.Components.Expander.Mature;
            ComponentsFilterVM.UnspecifiedGenre = _settings.Components.Expander.UnspecifiedGenre;
            ComponentsFilterVM.Abstract = _settings.Components.Expander.Abstract;
            ComponentsFilterVM.Anime = _settings.Components.Expander.Anime;
            ComponentsFilterVM.AudioVisualizer = _settings.Components.Expander.AudioVisualizer;
            ComponentsFilterVM.Background = _settings.Components.Expander.Background;
            ComponentsFilterVM.Cgi = _settings.Components.Expander.Cgi;
            ComponentsFilterVM.Character = _settings.Components.Expander.Character;
            ComponentsFilterVM.Clock = _settings.Components.Expander.Clock;
            ComponentsFilterVM.Fire = _settings.Components.Expander.Fire;
            ComponentsFilterVM.Interactive = _settings.Components.Expander.Interactive;
            ComponentsFilterVM.Magic = _settings.Components.Expander.Magic;
            ComponentsFilterVM.Memes = _settings.Components.Expander.Memes;
            ComponentsFilterVM.Nature = _settings.Components.Expander.Nature;
            ComponentsFilterVM.PostProcessing = _settings.Components.Expander.PostProcessing;
            ComponentsFilterVM.Smoke = _settings.Components.Expander.Smoke;
            ComponentsFilterVM.Space = _settings.Components.Expander.Space;
            ComponentsFilterVM.Sports = _settings.Components.Expander.Sports;
            ComponentsFilterVM.Technology = _settings.Components.Expander.Technology;
            ComponentsFilterVM.Vehicle = _settings.Components.Expander.Vehicle;

            ComponentsDisplayVM.ViewModeIndex = _settings.Components.ComponentViewIndex;
            ComponentsDisplayVM.ComponentTagDisplayIndex = _settings.Components.ComponentTagDisplayIndex;
            ComponentsDisplayVM.ComponentListMinWidth = _settings.Components.ComponentListMinWidth;
            ComponentsDisplayVM.AutoPlayGif = _settings.Components.AutoPlayGif;
            ComponentsDisplayVM.IsComponentEnterAnimationEnabled = _settings.Components.IsComponentEnterAnimationEnabled;
            ComponentsDisplayVM.IsBottomBarOpen = _settings.Components.IsBottomBarOpen;
            ComponentsDisplayVM.DetailSelectionEnabled = _settings.Components.DetailSelectionEnabled;
            ComponentsDisplayVM.FilterResultResponseDelay = _settings.Components.FilterResultResponseDelay;
            ComponentsDisplayVM.LeftSplitViewPaneOpen = _settings.Components.LeftSplitViewPaneOpen;
            ComponentsDisplayVM.RightSplitViewPaneOpen = _settings.Components.RightSplitViewPaneOpen;
            ComponentsDisplayVM.SortOrder = _settings.Components.SortOrder;
            ComponentsDisplayVM.IsSortAscending = _settings.Components.IsSortAscending;
            ComponentsDisplayVM.PaginationMode = _settings.Components.PaginationMode;

            PathManagementVM.LoadFromSettings(_settings);
            if (string.IsNullOrEmpty(PathManagementVM.DownloadPath))
                await PathManagementVM.AutoDetectDownloadPathAsync();

            // 缺哪个路径就把对应位标记为 1
            string mode = string.Concat(
                string.IsNullOrEmpty(PathManagementVM.WorkshopPath) ? '1' : '0',
                string.IsNullOrEmpty(PathManagementVM.ProjectPath) ? '1' : '0',
                string.IsNullOrEmpty(PathManagementVM.AcfPath) ? '1' : '0',
                string.IsNullOrEmpty(PathManagementVM.OfficialPath) ? '1' : '0',
                string.IsNullOrEmpty(PathManagementVM.VdfPath) ? '1' : '0');

            if (mode.Contains('1'))
                await PathManagementVM.AutoDetectWorkshopPathAsync(mode);

            IgnoreExtension = _settings.Extract.IgnoreExtension;
            IgnoreExtensionList = _settings.Extract.IgnoreExtensionList;
            OnlyExtension = _settings.Extract.OnlyExtension;
            OnlyExtensionList = _settings.Extract.OnlyExtensionList;
            OnlyPaths = _settings.Extract.OnlyPaths;
            OnlyPathsList = _settings.Extract.OnlyPathsList;
            IgnorePaths = _settings.Extract.IgnorePaths;
            IgnorePathsList = _settings.Extract.IgnorePathsList;
            OneFolder = _settings.Extract.OneFolder;
            OutProjectJSON = _settings.Extract.OutProjectJSON;
            UseProjectName = _settings.Extract.UseProjectName;
            FlatFileNamingMode = _settings.Extract.FlatFileNamingMode;
            KeepSubfolderStructure = _settings.Extract.KeepSubfolderStructure;
            OutputMode = _settings.Extract.OutputMode;
            CoverAllFiles = _settings.Extract.CoverAllFiles;
            OverwriteMode = _settings.Extract.CoverAllFiles ? 0 : (_settings.Extract.SkipExistingOutput ? 1 : 0);
            TexExportMode = _settings.Extract.TexExportMode;
            FilterEffectImagesThreshold = _settings.Extract.FilterEffectImagesThreshold;
            FilterEffectImagesEnabled = _settings.Extract.FilterEffectImagesEnabled;

            MaxConcurrentExtractions = _settings.Extract.MaxConcurrentExtractions;
            ProcessPriority = _settings.Extract.ProcessPriority;
            SkipExistingOutput = _settings.Extract.SkipExistingOutput;
            LazyLoad = _settings.Extract.LazyLoad;
            LogLevel = _settings.LogLevel;

            if (mode.Contains('1') || string.IsNullOrEmpty(_settings.Path.DownloadPath))
            {
                PathManagementVM.SyncToSettings(_settings);
                await _configService.SaveAsync(_settings);
            }
        }

        /// <summary>
        /// 将所有设置恢复为默认值：新建默认 AppSettings 并重新填充所有 VM（UI 即时刷新），
        /// 随后保存到配置文件。语言/主题等变化由 AppSettingsVM 的 PropertyChanged 钩子即时应用。
        /// </summary>
        public async Task ResetAllSettingsAsync()
        {
            _isBatchUpdating = true;

            _settings = new AppSettings();

            await ApplySettingsToViewModelsAsync();

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
                        () => FilterExpanderVM.Subscribed = selectmode,
                        () => FilterExpanderVM.Unsubscribed = selectmode,
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
                // 手动触发一次筛选更新（批处理期间跳过的所有 FilterExpanderVM 事件在此一次性触发）
                OnPropertyChanged(nameof(FilterExpanderVM));
                await SaveAsync();
            }
        }

        public void ResetComponentsFilters()
        {
            if (_isBatchUpdating) return;
            _isBatchUpdating = true;

            try
            {
                var f = ComponentsFilterVM;
                // 类型全选、年龄全选、标签全选
                f.Layers = true;
                f.Scripts = true;
                f.Effects = true;
                f.Everyone = true;
                f.Questionable = true;
                f.Mature = true;
                f.UnspecifiedGenre = true;
                f.Abstract = true;
                f.Anime = true;
                f.AudioVisualizer = true;
                f.Background = true;
                f.Cgi = true;
                f.Character = true;
                f.Clock = true;
                f.Fire = true;
                f.Interactive = true;
                f.Magic = true;
                f.Memes = true;
                f.Nature = true;
                f.PostProcessing = true;
                f.Smoke = true;
                f.Space = true;
                f.Sports = true;
                f.Technology = true;
                f.Vehicle = true;
            }
            finally
            {
                _isBatchUpdating = false;
                OnPropertyChanged(nameof(ComponentsFilterVM));
            }
        }

        public void SetAllComponentTags(bool select)
        {
            if (_isBatchUpdating) return;
            _isBatchUpdating = true;

            try
            {
                var f = ComponentsFilterVM;
                f.UnspecifiedGenre = select;
                f.Abstract = select;
                f.Anime = select;
                f.AudioVisualizer = select;
                f.Background = select;
                f.Cgi = select;
                f.Character = select;
                f.Clock = select;
                f.Fire = select;
                f.Interactive = select;
                f.Magic = select;
                f.Memes = select;
                f.Nature = select;
                f.PostProcessing = select;
                f.Smoke = select;
                f.Space = select;
                f.Sports = select;
                f.Technology = select;
                f.Vehicle = select;
            }
            finally
            {
                _isBatchUpdating = false;
                OnPropertyChanged(nameof(ComponentsFilterVM));
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

            // 守卫：同一时刻只允许一个 SaveAsync 进入执行体，后续冗余调用静默丢弃
            if (Interlocked.Exchange(ref _saveGuard, 1) == 1)
                return;

            try
            {
                // 防抖：取消上一次待处理的保存，等 _saveDelay (500ms) 无新变化再执行写盘
                _saveCts?.Cancel();
                _saveCts = new CancellationTokenSource();
                var token = _saveCts.Token;

                try
                {
                    await Task.Delay(_saveDelay, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await _saveSemaphore.WaitAsync();
                try
                {
                    _settings.AppLanguage = AppSettingsVM.AppLanguage ?? "";

                    _settings.StartPageTag = AppSettingsVM.StartPageTag;
                    _settings.Theme = AppSettingsVM.Theme;
                    _settings.ScanCacheEnabled = AppSettingsVM.ScanCacheEnabled;
                    _settings.RestoreWindowGeometry = AppSettingsVM.RestoreWindowGeometry;
                    _settings.RestorePropertiesWindowSize = AppSettingsVM.RestorePropertiesWindowSize;

                    _settings.Papers.IsBottomBarOpen = WallpaperDisplayVM.IsBottomBarOpen;
                    _settings.Papers.WallpaperViewIndex = WallpaperDisplayVM.WallpaperViewIndex;
                    _settings.Papers.BlurEveryone = WallpaperDisplayVM.BlurEveryone;
                    _settings.Papers.BlurTeen = WallpaperDisplayVM.BlurTeen;
                    _settings.Papers.BlurAdult = WallpaperDisplayVM.BlurAdult;
                    _settings.Papers.WallpaperDisplayMode = WallpaperDisplayVM.WallpaperDisplayMode;
                    _settings.Papers.AutoPlayGif = WallpaperDisplayVM.AutoPlayGif;
                    _settings.Papers.IsWallpaperEnterAnimationEnabled = WallpaperDisplayVM.IsWallpaperEnterAnimationEnabled;
                    _settings.Papers.WallpaperTagDisplayIndex = WallpaperDisplayVM.WallpaperTagDisplayIndex;
                    _settings.Papers.WallpaperListMinWidth = WallpaperDisplayVM.WallpaperListMinWidth;
                    _settings.Papers.LeftSplitViewPaneOpen = WallpaperDisplayVM.LeftSplitViewPaneOpen;
                    _settings.Papers.RightSplitViewPaneOpen = WallpaperDisplayVM.RightSplitViewPaneOpen;
                    _settings.Papers.RightPanelIndex = WallpaperDisplayVM.RightPanelIndex;

                    _settings.Papers.IsSortAscending = WallpaperDisplayVM.IsSortAscending;
                    _settings.Papers.SortOrder = WallpaperDisplayVM.SortOrder;
                    _settings.Papers.DetailSelectionEnabled = WallpaperDisplayVM.DetailSelectionEnabled;
                    _settings.Papers.FilterResultResponseDelay = WallpaperDisplayVM.FilterResultResponseDelay;
                    _settings.Papers.PaginationMode = WallpaperDisplayVM.PaginationMode;

                    _settings.Papers.Expander.TypeExpander = FilterExpanderVM.TypeExpander;
                    _settings.Papers.Expander.Scene = FilterExpanderVM.Scene;
                    _settings.Papers.Expander.Video = FilterExpanderVM.Video;
                    _settings.Papers.Expander.Web = FilterExpanderVM.Web;
                    _settings.Papers.Expander.Application = FilterExpanderVM.Application;
                    _settings.Papers.Expander.Preset = FilterExpanderVM.Preset;
                    _settings.Papers.Expander.Unknown = FilterExpanderVM.Unknown;

                    _settings.Papers.Expander.RatingExpander = FilterExpanderVM.RatingExpander;
                    _settings.Papers.Expander.G = FilterExpanderVM.G;
                    _settings.Papers.Expander.Pg = FilterExpanderVM.Pg;
                    _settings.Papers.Expander.R = FilterExpanderVM.R;

                    _settings.Papers.Expander.SourceExpander = FilterExpanderVM.SourceExpander;
                    _settings.Papers.Expander.Official = FilterExpanderVM.Official;
                    _settings.Papers.Expander.Workshop = FilterExpanderVM.Workshop;
                    _settings.Papers.Expander.Mine = FilterExpanderVM.Mine;

                    _settings.Papers.Expander.SubscriptionExpander = FilterExpanderVM.SubscriptionExpander;
                    _settings.Papers.Expander.Subscribed = FilterExpanderVM.Subscribed;
                    _settings.Papers.Expander.Unsubscribed = FilterExpanderVM.Unsubscribed;

                    _settings.Papers.Expander.TagsExpander = FilterExpanderVM.TagsExpander;
                    _settings.Papers.Expander.Abstract = FilterExpanderVM.Abstract;
                    _settings.Papers.Expander.Animal = FilterExpanderVM.Animal;
                    _settings.Papers.Expander.Anime = FilterExpanderVM.Anime;
                    _settings.Papers.Expander.Cartoon = FilterExpanderVM.Cartoon;
                    _settings.Papers.Expander.Cgi = FilterExpanderVM.Cgi;
                    _settings.Papers.Expander.Cyberpunk = FilterExpanderVM.Cyberpunk;
                    _settings.Papers.Expander.Fantasy = FilterExpanderVM.Fantasy;
                    _settings.Papers.Expander.Game = FilterExpanderVM.Game;
                    _settings.Papers.Expander.Girls = FilterExpanderVM.Girls;
                    _settings.Papers.Expander.Guys = FilterExpanderVM.Guys;
                    _settings.Papers.Expander.Landscape = FilterExpanderVM.Landscape;
                    _settings.Papers.Expander.Medieval = FilterExpanderVM.Medieval;
                    _settings.Papers.Expander.Memes = FilterExpanderVM.Memes;
                    _settings.Papers.Expander.Mmd = FilterExpanderVM.Mmd;
                    _settings.Papers.Expander.Music = FilterExpanderVM.Music;
                    _settings.Papers.Expander.Nature = FilterExpanderVM.Nature;
                    _settings.Papers.Expander.Pixelart = FilterExpanderVM.Pixelart;
                    _settings.Papers.Expander.Relaxing = FilterExpanderVM.Relaxing;
                    _settings.Papers.Expander.Retro = FilterExpanderVM.Retro;
                    _settings.Papers.Expander.SciFi = FilterExpanderVM.SciFi;
                    _settings.Papers.Expander.Sports = FilterExpanderVM.Sports;
                    _settings.Papers.Expander.Technology = FilterExpanderVM.Technology;
                    _settings.Papers.Expander.Television = FilterExpanderVM.Television;
                    _settings.Papers.Expander.Vehicle = FilterExpanderVM.Vehicle;
                    _settings.Papers.Expander.Unspecified = FilterExpanderVM.Unspecified;

                    // === Components 保存 ===
                    _settings.Components.Expander.TypeExpander = ComponentsFilterVM.TypeExpander;
                    _settings.Components.Expander.RatingExpander = ComponentsFilterVM.RatingExpander;
                    _settings.Components.Expander.TagsExpander = ComponentsFilterVM.TagsExpander;
                    _settings.Components.Expander.Layers = ComponentsFilterVM.Layers;
                    _settings.Components.Expander.Scripts = ComponentsFilterVM.Scripts;
                    _settings.Components.Expander.Effects = ComponentsFilterVM.Effects;
                    _settings.Components.Expander.Everyone = ComponentsFilterVM.Everyone;
                    _settings.Components.Expander.Questionable = ComponentsFilterVM.Questionable;
                    _settings.Components.Expander.Mature = ComponentsFilterVM.Mature;
                    _settings.Components.Expander.UnspecifiedGenre = ComponentsFilterVM.UnspecifiedGenre;
                    _settings.Components.Expander.Abstract = ComponentsFilterVM.Abstract;
                    _settings.Components.Expander.Anime = ComponentsFilterVM.Anime;
                    _settings.Components.Expander.AudioVisualizer = ComponentsFilterVM.AudioVisualizer;
                    _settings.Components.Expander.Background = ComponentsFilterVM.Background;
                    _settings.Components.Expander.Cgi = ComponentsFilterVM.Cgi;
                    _settings.Components.Expander.Character = ComponentsFilterVM.Character;
                    _settings.Components.Expander.Clock = ComponentsFilterVM.Clock;
                    _settings.Components.Expander.Fire = ComponentsFilterVM.Fire;
                    _settings.Components.Expander.Interactive = ComponentsFilterVM.Interactive;
                    _settings.Components.Expander.Magic = ComponentsFilterVM.Magic;
                    _settings.Components.Expander.Memes = ComponentsFilterVM.Memes;
                    _settings.Components.Expander.Nature = ComponentsFilterVM.Nature;
                    _settings.Components.Expander.PostProcessing = ComponentsFilterVM.PostProcessing;
                    _settings.Components.Expander.Smoke = ComponentsFilterVM.Smoke;
                    _settings.Components.Expander.Space = ComponentsFilterVM.Space;
                    _settings.Components.Expander.Sports = ComponentsFilterVM.Sports;
                    _settings.Components.Expander.Technology = ComponentsFilterVM.Technology;
                    _settings.Components.Expander.Vehicle = ComponentsFilterVM.Vehicle;

                    _settings.Components.ComponentViewIndex = ComponentsDisplayVM.ViewModeIndex;
                    _settings.Components.ComponentTagDisplayIndex = ComponentsDisplayVM.ComponentTagDisplayIndex;
                    _settings.Components.ComponentListMinWidth = ComponentsDisplayVM.ComponentListMinWidth;
                    _settings.Components.AutoPlayGif = ComponentsDisplayVM.AutoPlayGif;
                    _settings.Components.IsComponentEnterAnimationEnabled = ComponentsDisplayVM.IsComponentEnterAnimationEnabled;
                    _settings.Components.IsBottomBarOpen = ComponentsDisplayVM.IsBottomBarOpen;
                    _settings.Components.DetailSelectionEnabled = ComponentsDisplayVM.DetailSelectionEnabled;
                    _settings.Components.FilterResultResponseDelay = ComponentsDisplayVM.FilterResultResponseDelay;
                    _settings.Components.LeftSplitViewPaneOpen = ComponentsDisplayVM.LeftSplitViewPaneOpen;
                    _settings.Components.RightSplitViewPaneOpen = ComponentsDisplayVM.RightSplitViewPaneOpen;
                    _settings.Components.SortOrder = ComponentsDisplayVM.SortOrder;
                    _settings.Components.IsSortAscending = ComponentsDisplayVM.IsSortAscending;
                    _settings.Components.PaginationMode = ComponentsDisplayVM.PaginationMode;

                    _settings.Path.DownloadPath = PathManagementVM.DownloadPath;
                    _settings.Path.WorkshopPath = PathManagementVM.WorkshopPath;
                    _settings.Path.ProjectPath = PathManagementVM.ProjectPath;
                    _settings.Path.OfficialPath = PathManagementVM.OfficialPath;
                    _settings.Path.AcfPath = PathManagementVM.AcfPath;

                    _settings.Extract.IgnoreExtension = IgnoreExtension;
                    _settings.Extract.IgnoreExtensionList = IgnoreExtensionList;
                    _settings.Extract.OnlyExtension = OnlyExtension;
                    _settings.Extract.OnlyExtensionList = OnlyExtensionList;
                    _settings.Extract.OnlyPaths = OnlyPaths;
                    _settings.Extract.OnlyPathsList = OnlyPathsList;
                    _settings.Extract.IgnorePaths = IgnorePaths;
                    _settings.Extract.IgnorePathsList = IgnorePathsList;
                    _settings.Extract.OneFolder = OneFolder;
                    _settings.Extract.OutProjectJSON = OutProjectJSON;
                    _settings.Extract.UseProjectName = UseProjectName;
                    _settings.Extract.FlatFileNamingMode = FlatFileNamingMode;
                    _settings.Extract.KeepSubfolderStructure = KeepSubfolderStructure;
                    _settings.Extract.CoverAllFiles = OverwriteMode == 0;
                    _settings.Extract.TexExportMode = TexExportMode;
                    _settings.Extract.OutputMode = OutputMode;
                    _settings.Extract.FilterEffectImagesThreshold = FilterEffectImagesThreshold;
                    _settings.Extract.FilterEffectImagesEnabled = FilterEffectImagesEnabled;

                    _settings.Extract.MaxConcurrentExtractions = MaxConcurrentExtractions;
                    _settings.Extract.ProcessPriority = ProcessPriority;
                    _settings.Extract.SkipExistingOutput = OverwriteMode == 1;
                    _settings.Extract.LazyLoad = LazyLoad;
                    _settings.LogLevel = LogLevel;

                    await _configService.SaveAsync(_settings);
                }
                finally
                {
                    _saveSemaphore.Release();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _saveGuard, 0);
            }
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Tool_for_WallpaperEngine.Models;
using Tool_for_WallpaperEngine.Service;

namespace Tool_for_WallpaperEngine.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private bool _isBatchUpdating = false;

        private readonly IConfigService _configService;
        private readonly IPickerService _pickerService;
        private AppSettings _settings = new AppSettings();

        private string _startPageTag = "Papers";

        public bool _leftSplitViewPaneOpen;
        public bool _rightSplitViewPaneOpen;

        public bool _typeExpander;
        public bool _scene;
        public bool _video;
        public bool _web;
        public bool _application;
        public bool _regular;
        public bool _preset;

        public bool _ratingExpander;
        public bool _g;
        public bool _pg;
        public bool _r;

        public bool _sourceExpander;
        public bool _official;
        public bool _workshop;
        public bool _mine;

        public bool _tagsExpander;
        public bool _abstract;
        public bool _animal;
        public bool _anime;
        public bool _cartoon;
        public bool _cgi;
        public bool _cyberpunk;
        public bool _fantasy;
        public bool _game;
        public bool _girls;
        public bool _guys;
        public bool _landscape;
        public bool _medieval;
        public bool _memes;
        public bool _mmd;
        public bool _music;
        public bool _nature;
        public bool _pixelart;
        public bool _relaxing;
        public bool _retro;
        public bool _sciFi;
        public bool _sports;
        public bool _technology;
        public bool _television;
        public bool _vehicle;
        public bool _unspecified;

        private string _downloadPath;
        private string _workshopPath;
        private string _projectPath;
        private string _acfPath;
        private bool _ignoreExtension;
        private string _ignoreExtensionList;
        private bool _onlyExtension;
        private string _onlyExtensionList;
        private bool _convertTEX;
        private bool _oneFolder;
        private bool _outProjectJSON;
        private bool _useProjectName;
        private bool _dontConvertTEX;
        private bool _coverAllFiles;

        // debounce / save control
        private CancellationTokenSource? _saveCts;
        private readonly TimeSpan _saveDelay = TimeSpan.FromMilliseconds(500);
        private readonly SemaphoreSlim _saveSemaphore = new SemaphoreSlim(1, 1);

        public IAsyncRelayCommand SaveCommand { get; }
        public IAsyncRelayCommand<object> BrowseFolderCommand { get; }

        public SettingsViewModel(IConfigService configService, IPickerService pickerService)
        {
            _configService = configService;
            _pickerService = pickerService;

            SaveCommand = new AsyncRelayCommand(SaveAsync);
            BrowseFolderCommand = new AsyncRelayCommand<object>(BrowseFolderAsync);
        }

        public async Task InitializeAsync()
        {
            _settings = await _configService.LoadAsync() ?? new AppSettings();

            _startPageTag = string.IsNullOrEmpty(_settings.StartPageTag) ? "Papers" : _settings.StartPageTag;

            LeftSplitViewPaneOpen = _settings.Papers.LeftSplitViewPaneOpen;
            RightSplitViewPaneOpen = _settings.Papers.RightSplitViewPaneOpen;

            TypeExpander = _settings.Expander.TypeExpander;
            Scene = _settings.Expander.Scene;
            Video = _settings.Expander.Video;
            Web = _settings.Expander.Web;
            Application = _settings.Expander.Application;
            Regular = _settings.Expander.Regular;
            Preset = _settings.Expander.Preset;

            RatingExpander = _settings.Expander.RatingExpander;
            G = _settings.Expander.G;
            PG = _settings.Expander.PG;
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
            CGI = _settings.Expander.CGI;
            Cyberpunk = _settings.Expander.Cyberpunk;
            Fantasy = _settings.Expander.Fantasy;
            Game = _settings.Expander.Game;
            Girls = _settings.Expander.Girls;
            Guys = _settings.Expander.Guys;
            Landscape = _settings.Expander.Landscape;
            Medieval = _settings.Expander.Medieval;
            Memes = _settings.Expander.Memes;
            MMD = _settings.Expander.MMD;
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
            WorkshopPath = _settings.Path.WorkshopPath;
            ProjectPath = _settings.Path.ProjectPath;
            AcfPath = _settings.Path.AcfPath;

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

            OnPropertyChanged(nameof(StartPageTag));

            OnPropertyChanged(nameof(LeftSplitViewPaneOpen));
            OnPropertyChanged(nameof(RightSplitViewPaneOpen));

            OnPropertyChanged(nameof(TypeExpander));
            OnPropertyChanged(nameof(Scene));
            OnPropertyChanged(nameof(Video));
            OnPropertyChanged(nameof(Web));
            OnPropertyChanged(nameof(Application));
            OnPropertyChanged(nameof(Regular));
            OnPropertyChanged(nameof(Preset));

            OnPropertyChanged(nameof(RatingExpander));
            OnPropertyChanged(nameof(G));
            OnPropertyChanged(nameof(PG));
            OnPropertyChanged(nameof(R));

            OnPropertyChanged(nameof(SourceExpander));
            OnPropertyChanged(nameof(Official));
            OnPropertyChanged(nameof(Workshop));
            OnPropertyChanged(nameof(Mine));

            OnPropertyChanged(nameof(TagsExpander));
            OnPropertyChanged(nameof(Abstract));
            OnPropertyChanged(nameof(Animal));
            OnPropertyChanged(nameof(Anime));
            OnPropertyChanged(nameof(Cartoon));
            OnPropertyChanged(nameof(CGI));
            OnPropertyChanged(nameof(Cyberpunk));
            OnPropertyChanged(nameof(Fantasy));
            OnPropertyChanged(nameof(Game));
            OnPropertyChanged(nameof(Girls));
            OnPropertyChanged(nameof(Guys));
            OnPropertyChanged(nameof(Landscape));
            OnPropertyChanged(nameof(Medieval));
            OnPropertyChanged(nameof(Memes));
            OnPropertyChanged(nameof(MMD));
            OnPropertyChanged(nameof(Music));
            OnPropertyChanged(nameof(Nature));
            OnPropertyChanged(nameof(Pixelart));
            OnPropertyChanged(nameof(Relaxing));
            OnPropertyChanged(nameof(Retro));
            OnPropertyChanged(nameof(SciFi));
            OnPropertyChanged(nameof(Sports));
            OnPropertyChanged(nameof(Technology));
            OnPropertyChanged(nameof(Television));
            OnPropertyChanged(nameof(Vehicle));
            OnPropertyChanged(nameof(Unspecified));

            OnPropertyChanged(nameof(DownloadPath));
            OnPropertyChanged(nameof(WorkshopPath));
            OnPropertyChanged(nameof(ProjectPath));
            OnPropertyChanged(nameof(AcfPath));

            OnPropertyChanged(nameof(IgnoreExtension));
            OnPropertyChanged(nameof(IgnoreExtensionList));
            OnPropertyChanged(nameof(OnlyExtension));
            OnPropertyChanged(nameof(OnlyExtensionList));
            OnPropertyChanged(nameof(ConvertTEX));
            OnPropertyChanged(nameof(OneFolder));
            OnPropertyChanged(nameof(OutProjectJSON));
            OnPropertyChanged(nameof(UseProjectName));
            OnPropertyChanged(nameof(DontConvertTEX));
            OnPropertyChanged(nameof(CoverAllFiles));
        }

        public string StartPageTag
        {
            get => _startPageTag;
            set
            {
                if (SetProperty(ref _startPageTag, value))
                    DebounceSave();
            }
        }

        public bool LeftSplitViewPaneOpen
        {
            get => _leftSplitViewPaneOpen;
            set
            {
                if (SetProperty(ref _leftSplitViewPaneOpen, value))
                    DebounceSave();
            }
        }

        public bool RightSplitViewPaneOpen
        {
            get => _rightSplitViewPaneOpen;
            set
            {
                if (SetProperty(ref _rightSplitViewPaneOpen, value))
                    DebounceSave();
            }
        }

        public bool TypeExpander
        {
            get => _typeExpander;
            set
            {
                if (SetProperty(ref _typeExpander, value))
                    DebounceSave();
            }
        }
        public bool Scene
        {
            get => _scene;
            set
            {
                if (SetProperty(ref _scene, value))
                    DebounceSave();
            }
        }
        public bool Video
        {
            get => _video;
            set
            {
                if (SetProperty(ref _video, value))
                    DebounceSave();
            }
        }
        public bool Web
        {
            get => _web;
            set
            {
                if (SetProperty(ref _web, value))
                    DebounceSave();
            }
        }
        public bool Application
        {
            get => _application;
            set
            {
                if (SetProperty(ref _application, value))
                    DebounceSave();
            }
        }
        public bool Regular
        {
            get => _regular;
            set
            {
                if (SetProperty(ref _regular, value))
                    DebounceSave();
            }
        }
        public bool Preset
        {
            get => _preset;
            set
            {
                if (SetProperty(ref _preset, value))
                    DebounceSave();
            }
        }

        // Rating 相关属性
        public bool RatingExpander
        {
            get => _ratingExpander;
            set
            {
                if (SetProperty(ref _ratingExpander, value))
                    DebounceSave();
            }
        }
        public bool G
        {
            get => _g;
            set
            {
                if (SetProperty(ref _g, value))
                    DebounceSave();
            }
        }
        public bool PG
        {
            get => _pg;
            set
            {
                if (SetProperty(ref _pg, value))
                    DebounceSave();
            }
        }
        public bool R
        {
            get => _r;
            set
            {
                if (SetProperty(ref _r, value))
                    DebounceSave();
            }
        }

        // Source 相关属性
        public bool SourceExpander
        {
            get => _sourceExpander;
            set
            {
                if (SetProperty(ref _sourceExpander, value))
                    DebounceSave();
            }
        }
        public bool Official
        {
            get => _official;
            set
            {
                if (SetProperty(ref _official, value))
                    DebounceSave();
            }
        }
        public bool Workshop
        {
            get => _workshop;
            set
            {
                if (SetProperty(ref _workshop, value))
                    DebounceSave();
            }
        }
        public bool Mine
        {
            get => _mine;
            set
            {
                if (SetProperty(ref _mine, value))
                    DebounceSave();
            }
        }

        // Tags 相关属性
        public bool TagsExpander
        {
            get => _tagsExpander;
            set
            {
                if (SetProperty(ref _tagsExpander, value))
                    DebounceSave();
            }
        }
        public bool Abstract
        {
            get => _abstract;
            set
            {
                if (SetProperty(ref _abstract, value))
                    DebounceSave();
            }
        }
        public bool Animal
        {
            get => _animal;
            set
            {
                if (SetProperty(ref _animal, value))
                    DebounceSave();
            }
        }
        public bool Anime
        {
            get => _anime;
            set
            {
                if (SetProperty(ref _anime, value))
                    DebounceSave();
            }
        }
        public bool Cartoon
        {
            get => _cartoon;
            set
            {
                if (SetProperty(ref _cartoon, value))
                    DebounceSave();
            }
        }
        public bool CGI
        {
            get => _cgi;
            set
            {
                if (SetProperty(ref _cgi, value))
                    DebounceSave();
            }
        }
        public bool Cyberpunk
        {
            get => _cyberpunk;
            set
            {
                if (SetProperty(ref _cyberpunk, value))
                    DebounceSave();
            }
        }
        public bool Fantasy
        {
            get => _fantasy;
            set
            {
                if (SetProperty(ref _fantasy, value))
                    DebounceSave();
            }
        }
        public bool Game
        {
            get => _game;
            set
            {
                if (SetProperty(ref _game, value))
                    DebounceSave();
            }
        }
        public bool Girls
        {
            get => _girls;
            set
            {
                if (SetProperty(ref _girls, value))
                    DebounceSave();
            }
        }
        public bool Guys
        {
            get => _guys;
            set
            {
                if (SetProperty(ref _guys, value))
                    DebounceSave();
            }
        }
        public bool Landscape
        {
            get => _landscape;
            set
            {
                if (SetProperty(ref _landscape, value))
                    DebounceSave();
            }
        }
        public bool Medieval
        {
            get => _medieval;
            set
            {
                if (SetProperty(ref _medieval, value))
                    DebounceSave();
            }
        }
        public bool Memes
        {
            get => _memes;
            set
            {
                if (SetProperty(ref _memes, value))
                    DebounceSave();
            }
        }
        public bool MMD
        {
            get => _mmd;
            set
            {
                if (SetProperty(ref _mmd, value))
                    DebounceSave();
            }
        }
        public bool Music
        {
            get => _music;
            set
            {
                if (SetProperty(ref _music, value))
                    DebounceSave();
            }
        }
        public bool Nature
        {
            get => _nature;
            set
            {
                if (SetProperty(ref _nature, value))
                    DebounceSave();
            }
        }
        public bool Pixelart
        {
            get => _pixelart;
            set
            {
                if (SetProperty(ref _pixelart, value))
                    DebounceSave();
            }
        }
        public bool Relaxing
        {
            get => _relaxing;
            set
            {
                if (SetProperty(ref _relaxing, value))
                    DebounceSave();
            }
        }
        public bool Retro
        {
            get => _retro;
            set
            {
                if (SetProperty(ref _retro, value))
                    DebounceSave();
            }
        }
        public bool SciFi
        {
            get => _sciFi;
            set
            {
                if (SetProperty(ref _sciFi, value))
                    DebounceSave();
            }
        }
        public bool Sports
        {
            get => _sports;
            set
            {
                if (SetProperty(ref _sports, value))
                    DebounceSave();
            }
        }
        public bool Technology
        {
            get => _technology;
            set
            {
                if (SetProperty(ref _technology, value))
                    DebounceSave();
            }
        }
        public bool Television
        {
            get => _television;
            set
            {
                if (SetProperty(ref _television, value))
                    DebounceSave();
            }
        }
        public bool Vehicle
        {
            get => _vehicle;
            set
            {
                if (SetProperty(ref _vehicle, value))
                    DebounceSave();
            }
        }
        public bool Unspecified
        {
            get => _unspecified;
            set
            {
                if (SetProperty(ref _unspecified, value))
                    DebounceSave();
            }
        }

        public string DownloadPath
        {
            get => _downloadPath;
            set
            {
                if (SetProperty(ref _downloadPath, value))
                    DebounceSave();
            }
        }

        public string WorkshopPath
        {
            get => _workshopPath;
            set
            {
                if (SetProperty(ref _workshopPath, value))
                    DebounceSave();
            }
        }

        public string ProjectPath
        {
            get => _projectPath;
            set
            {
                if (SetProperty(ref _projectPath, value))
                    DebounceSave();
            }
        }

        public string AcfPath
        {
            get => _acfPath;
            set
            {
                if (SetProperty(ref _acfPath, value))
                    DebounceSave();
            }
        }

        public bool IgnoreExtension
        {
            get => _ignoreExtension;
            set
            {
                if (SetProperty(ref _ignoreExtension, value))
                    DebounceSave();
            }
        }

        public string IgnoreExtensionList
        {
            get => _ignoreExtensionList;
            set
            {
                if (SetProperty(ref _ignoreExtensionList, value))
                    DebounceSave();
            }
        }

        public bool OnlyExtension
        {
            get => _onlyExtension;
            set
            {
                if (SetProperty(ref _onlyExtension, value))
                    DebounceSave();
            }
        }

        public string OnlyExtensionList
        {
            get => _onlyExtensionList;
            set
            {
                if (SetProperty(ref _onlyExtensionList, value))
                    DebounceSave();
            }
        }

        public bool ConvertTEX
        {
            get => _convertTEX;
            set
            {
                if (SetProperty(ref _convertTEX, value))
                    DebounceSave();
            }
        }

        public bool OneFolder
        {
            get => _oneFolder;
            set
            {
                if (SetProperty(ref _oneFolder, value))
                    DebounceSave();
            }
        }

        public bool OutProjectJSON
        {
            get => _outProjectJSON;
            set
            {
                if (SetProperty(ref _outProjectJSON, value))
                    DebounceSave();
            }
        }

        public bool UseProjectName
        {
            get => _useProjectName;
            set
            {
                if (SetProperty(ref _useProjectName, value))
                    DebounceSave();
            }
        }

        public bool DontConvertTEX
        {
            get => _dontConvertTEX;
            set
            {
                if (SetProperty(ref _dontConvertTEX, value))
                    DebounceSave();
            }
        }

        public bool CoverAllFiles
        {
            get => _coverAllFiles;
            set
            {
                if (SetProperty(ref _coverAllFiles, value))
                    DebounceSave();
            }
        }

        public async Task ResetFiltersAsync(int mode)
        {
            if (_isBatchUpdating) return;

            _isBatchUpdating = true;

            try
            {
                var actions = new List<Action>
                {
                    () => Scene = true,
                    () => Video = true,
                    () => Web = true,
                    () => Application = true,
                    () => Regular = true,
                    () => Preset = true,

                    () => G = true,
                    () => PG = true,
                    () => R = true,

                    () => Official = true,
                    () => Workshop = true,
                    () => Mine = true,
                };
                var tags = new List<Action>
                {
                    () => Abstract = true,
                    () => Animal = true,
                    () => Anime = true,
                    () => Cartoon = true,
                    () => CGI = true,
                    () => Cyberpunk = true,
                    () => Fantasy = true,
                    () => Game = true,
                    () => Girls = true,
                    () => Guys = true,
                    () => Landscape = true,
                    () => Medieval = true,
                    () => Memes = true,
                    () => MMD = true,
                    () => Music = true,
                    () => Nature = true,
                    () => Pixelart = true,
                    () => Relaxing = true,
                    () => Retro = true,
                    () => SciFi = true,
                    () => Sports = true,
                    () => Technology = true,
                    () => Television = true,
                    () => Vehicle = true,
                    () => Unspecified = true
                };
                if (mode == 1)
                {
                    foreach (var action in actions)
                    {
                        action();
                        await Task.Delay(1);
                    }
                }
                if (mode == 2 || mode == 1)
                {

                    foreach (var action in tags)
                    {
                        action();
                        await Task.Delay(1);
                    }
                }
            }
            finally
            {
                _isBatchUpdating = false;

                await SaveAsync();
            }
        }

        public async Task DeselctAllAsync(int mode)
        {
            if (_isBatchUpdating) return;

            _isBatchUpdating = true;

            try
            {

                var tags = new List<Action>
                {
                    () => Abstract = false,
                    () => Animal = false,
                    () => Anime = false,
                    () => Cartoon = false,
                    () => CGI = false,
                    () => Cyberpunk = false,
                    () => Fantasy = false,
                    () => Game = false,
                    () => Girls = false,
                    () => Guys = false,
                    () => Landscape = false,
                    () => Medieval = false,
                    () => Memes = false,
                    () => MMD = false,
                    () => Music = false,
                    () => Nature = false,
                    () => Pixelart = false,
                    () => Relaxing = false,
                    () => Retro = false,
                    () => SciFi = false,
                    () => Sports = false,
                    () => Technology = false,
                    () => Television = false,
                    () => Vehicle = false,
                    () => Unspecified = false
                };
                if (mode == 1)
                {
                    foreach (var action in tags)
                    {
                        action();
                        await Task.Delay(1);
                    }
                }
            }
            finally
            {
                _isBatchUpdating = false;

                await SaveAsync();
            }
        }

        private void DebounceSave()
        {
            _saveCts?.Cancel();
            _saveCts = new CancellationTokenSource();
            var ct = _saveCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_saveDelay, ct);
                    if (!ct.IsCancellationRequested)
                        await SaveAsync();
                }
                catch (TaskCanceledException) { }
            }, ct);
        }

        private async Task SaveAsync()
        {
            if (_isBatchUpdating) return;

            await _saveSemaphore.WaitAsync();
            try
            {
                _settings.StartPageTag = StartPageTag;

                _settings.Papers.LeftSplitViewPaneOpen = LeftSplitViewPaneOpen;
                _settings.Papers.RightSplitViewPaneOpen = RightSplitViewPaneOpen;

                // 类型相关
                _settings.Expander.TypeExpander = TypeExpander;
                _settings.Expander.Scene = Scene;
                _settings.Expander.Video = Video;
                _settings.Expander.Web = Web;
                _settings.Expander.Application = Application;
                _settings.Expander.Regular = Regular;
                _settings.Expander.Preset = Preset;

                // 分级相关
                _settings.Expander.RatingExpander = RatingExpander;
                _settings.Expander.G = G;
                _settings.Expander.PG = PG;
                _settings.Expander.R = R;

                // 来源相关
                _settings.Expander.SourceExpander = SourceExpander;
                _settings.Expander.Official = Official;
                _settings.Expander.Workshop = Workshop;
                _settings.Expander.Mine = Mine;

                // 标签相关
                _settings.Expander.TagsExpander = TagsExpander;
                _settings.Expander.Abstract = Abstract;
                _settings.Expander.Animal = Animal;
                _settings.Expander.Anime = Anime;
                _settings.Expander.Cartoon = Cartoon;
                _settings.Expander.CGI = CGI;
                _settings.Expander.Cyberpunk = Cyberpunk;
                _settings.Expander.Fantasy = Fantasy;
                _settings.Expander.Game = Game;
                _settings.Expander.Girls = Girls;
                _settings.Expander.Guys = Guys;
                _settings.Expander.Landscape = Landscape;
                _settings.Expander.Medieval = Medieval;
                _settings.Expander.Memes = Memes;
                _settings.Expander.MMD = MMD;
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

        private async Task BrowseFolderAsync(object? parameter)
        {
            var path = await _pickerService.PickFileAsync();
            if (!string.IsNullOrEmpty(path))
            {
                var key = (parameter as string) ?? "DownloadPath";
                switch (key)
                {
                    case "DownloadPath":
                        DownloadPath = path;
                        break;
                    case "WorkshopPath":
                        WorkshopPath = path;
                        break;
                    case "ProjectPath":
                        ProjectPath = path;
                        break;
                    case "AcfPath":
                        AcfPath = path;
                        break;
                    default:
                        DownloadPath = path;
                        break;
                }
            }
        }
    }
}

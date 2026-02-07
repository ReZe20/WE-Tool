using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Tool_for_WallpaperEngine.Models;
using Tool_for_WallpaperEngine.Service;

namespace Tool_for_WallpaperEngine.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly IConfigService _configService;
        private readonly IPickerService _pickerService;
        private AppSettings _settings = new AppSettings();

        private string _startPageTag = "Papers";
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
            DownloadPath = _settings.DownloadPath;
            WorkshopPath = _settings.WorkshopPath;
            ProjectPath = _settings.ProjectPath;
            AcfPath = _settings.AcfPath;

            IgnoreExtension = _settings.IgnoreExtension;
            IgnoreExtensionList = _settings.IgnoreExtensionList;
            OnlyExtension = _settings.OnlyExtension;
            OnlyExtensionList = _settings.OnlyExtensionList;
            ConvertTEX = _settings.ConvertTEX;
            OneFolder = _settings.OneFolder;
            OutProjectJSON = _settings.OutProjectJSON;
            UseProjectName = _settings.UseProjectName;
            DontConvertTEX = _settings.DontConvertTEX;
            CoverAllFiles = _settings.CoverAllFiles;

            OnPropertyChanged(nameof(StartPageTag));

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

        private void DebounceSave()
        {
            // 取消上一次计划
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
            await _saveSemaphore.WaitAsync();
            try
            {
                _settings.StartPageTag = StartPageTag;
                _settings.DownloadPath = DownloadPath;
                _settings.WorkshopPath = WorkshopPath;
                _settings.ProjectPath = ProjectPath;
                _settings.AcfPath = AcfPath;

                _settings.IgnoreExtension = IgnoreExtension;
                _settings.IgnoreExtensionList = IgnoreExtensionList;
                _settings.OnlyExtension = OnlyExtension;
                _settings.OnlyExtensionList = OnlyExtensionList;
                _settings.ConvertTEX = ConvertTEX;
                _settings.OneFolder = OneFolder;
                _settings.OutProjectJSON = OutProjectJSON;
                _settings.UseProjectName = UseProjectName;
                _settings.DontConvertTEX = DontConvertTEX;
                _settings.CoverAllFiles = CoverAllFiles;

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

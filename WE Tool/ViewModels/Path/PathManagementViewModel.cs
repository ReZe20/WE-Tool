using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Models;
using WE_Tool.Service;
using Microsoft.Win32;

namespace WE_Tool.ViewModels
{
    public partial class PathManagementViewModel : ObservableObject
    {
        private readonly IPickerService _pickerService;

        public event Action? SaveRequested;

        /// <summary>
        /// Callback to get wallpaper items when user clicks "Open selected wallpapers".
        /// </summary>
        public Func<List<WallpaperItem>>? GetSelectedWallpapersToOpen { get; set; }

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

        public IAsyncRelayCommand<object> BrowseFolderCommand { get; }
        public IAsyncRelayCommand<object> BrowseFileCommand { get; }
        public IAsyncRelayCommand<object> OpenFolderCommand { get; }
        public IAsyncRelayCommand<string> AutoDetectWorkshopPathCommand { get; }
        public IAsyncRelayCommand AutoDetectDownloadPathCommand { get; }

        public PathManagementViewModel(IPickerService pickerService)
        {
            _pickerService = pickerService;

            BrowseFolderCommand = new AsyncRelayCommand<object>(BrowseFolderAsync);
            BrowseFileCommand = new AsyncRelayCommand<object>(BrowseFileAsync);
            OpenFolderCommand = new AsyncRelayCommand<object>(OpenFolderAsync);
            AutoDetectWorkshopPathCommand = new AsyncRelayCommand<string>(AutoDetectWorkshopPathAsync);
            AutoDetectDownloadPathCommand = new AsyncRelayCommand(AutoDetectDownloadPathAsync);
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
                SaveRequested?.Invoke();
            }
        }

        private async Task BrowseFileAsync(object? parameter)
        {
            var filePath = await _pickerService.PickFileAsync();

            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath) && parameter != null)
            {
                AcfPath = filePath;
                SaveRequested?.Invoke();
            }
        }

        public async Task OpenSelectedWallpapersFoldersAsync()
        {
            await OpenFolderAsync("OpenSelectedWallpapers");
        }

        private async Task OpenFolderAsync(object? parameter)
        {
            var key = (parameter as string) ?? "DownloadPath";

            if (key == "OpenSelectedWallpapers")
            {
                var items = GetSelectedWallpapersToOpen?.Invoke() ?? [];
                await ParallelOpenFoldersAsync(items);
                return;
            }

            string? targetPath = key switch
            {
                "WorkshopPath" => WorkshopPath,
                "ProjectPath" => ProjectPath,
                "AcfPath" => AcfPath,
                "OfficialPath" => OfficialPath,
                "ConfigPath" => AppSettingsHelper.ConfigPath,
                "LogPath" => AppSettingsHelper.LogPath,
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

                SaveRequested?.Invoke();
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
                    SaveRequested?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "设置桌面路径为保存路径时出现异常。");
            }
        }

        public void SyncToSettings(AppSettings settings)
        {
            settings.Path.DownloadPath = DownloadPath;
            settings.Path.WorkshopPath = WorkshopPath;
            settings.Path.ProjectPath = ProjectPath;
            settings.Path.AcfPath = AcfPath;
            settings.Path.OfficialPath = OfficialPath;
        }

        public void LoadFromSettings(AppSettings settings)
        {
            DownloadPath = settings.Path.DownloadPath;
            WorkshopPath = settings.Path.WorkshopPath;
            ProjectPath = settings.Path.ProjectPath;
            AcfPath = settings.Path.AcfPath;
            OfficialPath = settings.Path.OfficialPath;
        }
    }
}

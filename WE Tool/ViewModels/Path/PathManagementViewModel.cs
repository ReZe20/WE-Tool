using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Models;
using WE_Tool.Service;
using Microsoft.Win32;

namespace WE_Tool.ViewModels
{
    public partial class PathManagementViewModel : ObservableObject
    {
        private const ulong SteamId64Base = 76561197960265728;

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
        public partial string VdfPath { get; set; } = null!;

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
                    case "VdfPath":
                        VdfPath = path;
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
                string key = parameter as string ?? "";
                switch (key)
                {
                    case "VdfPath":
                        VdfPath = filePath;
                        break;
                    default:
                        AcfPath = filePath;
                        break;
                }
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
                "VdfPath" => VdfPath,
                "OfficialPath" => OfficialPath,
                "ConfigPath" => AppSettingsHelper.ConfigPath,
                "LogPath" => AppSettingsHelper.LogPath,
                "CachePath" => AppSettingsHelper.CachePath,
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

            if (mode == "00000") return;

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

            // VDF 路径走独立逻辑：从 Steam 安装目录 + 当前账号 ID 拼接
            if (mode[4] == '1')
            {
                string? steamPath = GetSteamInstallPath();
                uint? accountId = GetCurrentAccountId();
                if (steamPath != null && accountId.HasValue)
                {
                    VdfPath = Path.Combine(steamPath, "userdata", accountId.Value.ToString(), "ugc", "431960_subscriptions.vdf");
                    SaveRequested?.Invoke();
                }
                else
                {
                    Log.Warning("无法自动检测 VDF 路径：未能获取 Steam 安装路径或当前账号 ID");
                }
            }
        }

        /// <summary>
        /// 从注册表读取 Steam 安装路径
        /// </summary>
        private static string? GetSteamInstallPath()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                if (key?.GetValue("InstallPath") is string path && Directory.Exists(path))
                    return path;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取 Steam 注册表路径失败");
            }
            return null;
        }

        /// <summary>
        /// 从 loginusers.vdf 获取当前登录账号的 SteamID32 (AccountID)
        /// </summary>
        private static uint? GetCurrentAccountId()
        {
            string? steamPath = GetSteamInstallPath();
            if (steamPath == null) return null;

            string loginUsersPath = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(loginUsersPath)) return null;

            try
            {
                string content = File.ReadAllText(loginUsersPath);

                // 找到 "MostRecent" "1" 所在的块，提取其 SteamID64
                var match = Regex.Match(content, @"""(\d{17})""\s*\{[^}]*""MostRecent""\s*""1""[^}]*\}");
                if (!match.Success) return null;

                string steamId64Str = match.Groups[1].Value;
                if (!ulong.TryParse(steamId64Str, out var steamId64)) return null;

                // SteamID32 = SteamID64 - 基数
                ulong accountId = steamId64 - SteamId64Base;
                return (uint)accountId;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "解析 loginusers.vdf 失败");
                return null;
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
            settings.Path.VdfPath = VdfPath;
            settings.Path.OfficialPath = OfficialPath;
        }

        public void LoadFromSettings(AppSettings settings)
        {
            DownloadPath = settings.Path.DownloadPath;
            WorkshopPath = settings.Path.WorkshopPath;
            ProjectPath = settings.Path.ProjectPath;
            AcfPath = settings.Path.AcfPath;
            VdfPath = settings.Path.VdfPath;
            OfficialPath = settings.Path.OfficialPath;
        }
    }
}

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;
using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Service;
using WE_Tool.ViewModels;
using WE_Tool.Views;
using Windows.Graphics;
using Windows.UI;
using WinUIEx;

// To learndata:image/svg+xml;base64,PD94bWwgdmVyc2lvbj0iMS4wIiBzdGFuZGFsb25lPSJubyI/PjwhRE9DVFlQRSBzdmcgUFVCTElDICItLy9XM0MvL0RURCBTVkcgMS4xLy9FTiIgImh0dHA6Ly93d3cudzMub3JnL0dyYXBoaWNzL1NWRy8xLjEvRFREL3N2ZzExLmR0ZCI+PHN2ZyB0PSIxNTgxNDkxOTQyMjQzIiBjbGFzcz0iaWNvbiIgdmlld0JveD0iMCAwIDEwMjQgMTAyNCIgdmVyc2lvbj0iMS4xIiB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHAtaWQ9IjQ1NzUiIHhtbG5zOnhsaW5rPSJodHRwOi8vd3d3LnczLm9yZy8xOTk5L3hsaW5rIiB3aWR0aD0iMzIiIGhlaWdodD0iMzIiPjxkZWZzPjxzdHlsZSB0eXBlPSJ0ZXh0L2NzcyI+PC9zdHlsZT48L2RlZnM+PHBhdGggZD0iTTU4My4xNjggNTIzLjc3Nkw5NTguNDY0IDE0OC40OGMxOC45NDQtMTguOTQ0IDE4Ljk0NC01MC4xNzYgMC02OS4xMmwtMi4wNDgtMi4wNDhjLTE4Ljk0NC0xOC45NDQtNTAuMTc2LTE4Ljk0NC02OS4xMiAwTDUxMiA0NTMuMTIgMTM2LjcwNCA3Ny4zMTJjLTE4Ljk0NC0xOC45NDQtNTAuMTc2LTE4Ljk0NC02OS4xMiAwbC0yLjA0OCAyLjA0OGMtMTkuNDU2IDE4Ljk0NC0xOS40NTYgNTAuMTc2IDAgNjkuMTJsMzc1LjI5NiAzNzUuMjk2TDY1LjUzNiA4OTkuMDcyYy0xOC45NDQgMTguOTQ0LTE4Ljk0NCA1MC4xNzYgMCA2OS4xMmwyLjA0OCAyLjA0OGMxOC45NDQgMTguOTQ0IDUwLjE3NiAxOC45NDQgNjkuMTIgMEw1MTIgNTk0Ljk0NCA4ODcuMjk2IDk3MC4yNGMxOC45NDQgMTguOTQ0IDUwLjE3NiAxOC45NDQgNjkuMTIgMGwyLjA0OC0yLjA0OGMxOC45NDQtMTguOTQ0IDE4Ljk0NC01MC4xNzYgMC02OS4xMkw1ODMuMTY4IDUyMy43NzZ6IiBwLWlkPSI0NTc2IiBmaWxsPSIjZmZmZmZmIj48L3BhdGg+PC9zdmc+ more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WE_Tool
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : WindowEx
    {
        public SettingsViewModel ViewModel { get; }
        private readonly IConfigService _configService = new ConfigService();
        public MainWindow()
        {
            var app = Application.Current as App;
            ViewModel = app?.ViewModel ?? new SettingsViewModel(new ConfigService(), new PickerService());
            InitializeComponent();
            this.ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            this.Activated += MainWindow_Activated;
            // 焦点跟踪:提取等后台事件仅在主窗口无焦点时弹系统通知(常驻,区别于一次性启动导航的 MainWindow_Activated)
            this.Activated += MainWindow_FocusChanged;
            this.AppWindow.Changed += OnAppWindowChanged;
            // 导航栏 Info 项徽标实时反映 Steamworks 状态(桥接进程事件驱动,不依赖 Info 页轮询)
            SteamWorkshopService.StatusChanged += OnSteamworksStatusChanged;
            UpdateSteamStatusBadge();
        }

        /// <summary>桥接状态事件可能来自任意线程,统一编组到 UI 线程更新徽标</summary>
        private void OnSteamworksStatusChanged()
        {
            DispatcherQueue.TryEnqueue(UpdateSteamStatusBadge);
        }

        /// <summary>焦点跟踪:提取等后台事件仅在主窗口无焦点时弹系统通知(Deactivated = 失去焦点)</summary>
        private void MainWindow_FocusChanged(object? sender, WindowActivatedEventArgs e)
        {
            NotificationService.IsWindowFocused = e.WindowActivationState != WindowActivationState.Deactivated;
        }

        /// <summary>导航栏 Info 项徽标:全绿才绿(Steamworks 在线 且 RePKG_Re 版本匹配),其余一律红</summary>
        private void UpdateSteamStatusBadge()
        {
            bool allOk = SteamWorkshopService.GetInstance().Status == SteamworksStatus.Running
                         && Info.IsRepkgStatusOk();
            if (allOk)
            {
                SteamStatusBadge.Visibility = Visibility.Visible;
                SteamStatusBadge.Background = new SolidColorBrush(Color.FromArgb(255, 16, 124, 16));
            }
            else
            {
                SteamStatusBadge.Visibility = Visibility.Visible;
                SteamStatusBadge.Background = new SolidColorBrush(Color.FromArgb(255, 196, 43, 28));
            }
        }

        private CancellationTokenSource? _positionSaveCts;
        private async void MainWindow_Activated(object? sender, WindowActivatedEventArgs e)
        {
            this.Activated -= MainWindow_Activated;
            try
            {
                var settings = await _configService.LoadAsync();
                var tag = settings?.StartPageTag ?? "Papers";

                var item = FindNavItemByTag(nvSample.MenuItems, tag) ?? FindNavItemByTag(nvSample.FooterMenuItems, tag);

                if (item is not null)
                {
                    item.IsSelected = true;
                    nvSample.SelectedItem = item;

                    if (MapTagToPageType(tag) is { } pageType)
                        contentFrame.Navigate(pageType);
                }

                // 恢复窗口位置和大小（在导航之后执行，确保窗口布局已完成）
                bool hasPosition = settings is { RestoreWindowGeometry: true, WindowX: >= 0, WindowY: >= 0 };
                bool hasSize = settings is { RestoreWindowGeometry: true, WindowWidth: > 0, WindowHeight: > 0 };

                if (hasPosition || hasSize)
                {
                    try
                    {
                        int x = hasPosition ? settings!.WindowX : this.AppWindow.Position.X;
                        int y = hasPosition ? settings!.WindowY : this.AppWindow.Position.Y;
                        int w = hasSize ? settings!.WindowWidth : this.AppWindow.Size.Width;
                        int h = hasSize ? settings!.WindowHeight : this.AppWindow.Size.Height;

                        var rect = new RectInt32(x, y, w, h);
                        // 检查位置是否在有效显示器范围内（防止外接显示器被移除后窗口跑出屏幕可见区域）
                        var area = DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.Nearest);
                        if (area != null)
                        {
                            this.AppWindow.MoveAndResize(rect);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "恢复窗口位置/大小时异常，将使用默认值。");
                    }
                }

                // 恢复最大化状态（放在位置/大小之后，确保 restore bounds 先设置好）
                if (settings is { RestoreWindowGeometry: true, WindowMaximized: true })
                {
                    try
                    {
                        if (this.AppWindow.Presenter is OverlappedPresenter op)
                            op.Maximize();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "恢复窗口最大化状态失败。");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"初始化失败。Tag: {ViewModel?.AppSettingsVM.StartPageTag}");
            }
        }
        private static NavigationViewItem? FindNavItemByTag(IEnumerable items, string tag)
        {
            foreach (var obj in items)
            {
                if (obj is NavigationViewItem nvi)
                {
                    if ((nvi.Tag?.ToString() ?? "") == tag)
                        return nvi;

                    if (nvi.MenuItems?.Count > 0)
                    {
                        var found = FindNavItemByTag(nvi.MenuItems, tag);
                        if (found != null) return found;
                    }
                }
            }
            return null;
        }

        private static Type? MapTagToPageType(string tag) =>
            tag switch
            {
                "Papers" => typeof(Papers),
                "LoadPapers" => typeof(LoadPapers),
                "InstalledComponents" => typeof(InstalledComponents),
                "Cleanup" => typeof(Cleanup),
                "WallpaperBackup" => typeof(WallpaperBackup),
                "Info" => typeof(Info),
                "Settings" => typeof(Settings),
                _ => typeof(Papers)
            };

        private void NvSample_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.InvokedItemContainer == null)
                return;

            string? tag = args.InvokedItemContainer.Tag.ToString();

            _ = tag switch
            {
                "Papers" => contentFrame.Navigate(typeof(Papers), null),
                "LoadPapers" => contentFrame.Navigate(typeof(LoadPapers), null),
                "InstalledComponents" => contentFrame.Navigate(typeof(InstalledComponents), null),
                "Cleanup" => contentFrame.Navigate(typeof(Cleanup), null),
                "WallpaperBackup" => contentFrame.Navigate(typeof(WallpaperBackup), null),
                "Info" => contentFrame.Navigate(typeof(Info), null),
                "Settings" => contentFrame.Navigate(typeof(Settings), null),
                _ => contentFrame.Navigate(typeof(Papers), null)
            };
        }

        public string CurrentPageTag =>
            (nvSample.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "Papers";

        internal void NavigateToPage(string tag)
        {
            var pageType = MapTagToPageType(tag);
            contentFrame.Navigate(pageType, null);
        }

        internal void RefreshUILanguage()
        {
            // 重新加载语言资源，使 SortText 等更新
            LanguageHelper.ReloadResources();

            // 重建当前 Page（x:Uid 重新从 .resw 加载）
            var pageType = MapTagToPageType(CurrentPageTag);
            contentFrame.BackStack.Clear();
            int originalCacheSize = contentFrame.CacheSize;
            contentFrame.CacheSize = 0;
            contentFrame.Navigate(pageType, null);
            contentFrame.CacheSize = originalCacheSize;

        }

        private async void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (!args.DidPositionChange && !args.DidSizeChange && !args.DidPresenterChange) return;

            // 防抖：用户拖拽过程中会连续触发，只取最后一次停止后 500ms 写入
            _positionSaveCts?.Cancel();
            _positionSaveCts = new CancellationTokenSource();
            var token = _positionSaveCts.Token;

            try
            {
                await Task.Delay(500, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var settings = await _configService.LoadAsync();
                if (args.DidPositionChange)
                {
                    settings.WindowX = sender.Position.X;
                    settings.WindowY = sender.Position.Y;
                }
                if (args.DidSizeChange)
                {
                    settings.WindowWidth = sender.Size.Width;
                    settings.WindowHeight = sender.Size.Height;
                }
                // 无论触发原因，始终记录当前窗口最大化状态
                settings.WindowMaximized =
                    sender.Presenter is OverlappedPresenter op &&
                    op.State == OverlappedPresenterState.Maximized;
                await _configService.SaveAsync(settings);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存窗口位置/大小/状态失败");
            }
        }
    }
}

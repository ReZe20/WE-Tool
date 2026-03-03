using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Models;
using WE_Tool.Service;
using WE_Tool.ViewModels;
using Windows.Foundation;
using Windows.Foundation.Collections;
using WinUIEx;

// To learn more about WinUI, the WinUI project structure,
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
        public static List<WallpaperItem> GlobalAllWallpapers { get; private set; } = new List<WallpaperItem>();
        public static Task ScanTask { get; private set; }
        public static event EventHandler? ScanCompleted;
        public MainWindow()
        {
            ViewModel = new SettingsViewModel(new ConfigService(), new PickerService());
            InitializeComponent();
            this.ExtendsContentIntoTitleBar = true;
            this.Activated += MainWindow_Activated;
        }
        private async void MainWindow_Activated(object? sender, WindowActivatedEventArgs e)
        {
            this.Activated -= MainWindow_Activated;
            try
            {
                var settings = await _configService.LoadAsync();
                var tag = settings?.StartPageTag ?? "Papers";

                // 找到对应的 NavigationViewItem（在 MenuItems 或 FooterMenuItems）
                var item = FindNavItemByTag(nvSample.MenuItems, tag) ?? FindNavItemByTag(nvSample.FooterMenuItems, tag);

                if (item != null)
                {
                    // 选择该项（通常会触发你已实现的导航逻辑）
                    item.IsSelected = true;
                    nvSample.SelectedItem = item;

                    // 如果你需要直接导航到页面（保证页面类型存在），也可用下面方式：
                    var pageType = MapTagToPageType(tag);
                    if (pageType != null)
                        contentFrame.Navigate(pageType);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "初始化失败。");
            }
        }
        private NavigationViewItem? FindNavItemByTag(IEnumerable items, string tag)
        {
            foreach (var obj in items)
            {
                if (obj is NavigationViewItem nvi)
                {
                    if ((nvi.Tag?.ToString() ?? "") == tag)
                        return nvi;

                    // 递归检查子项（如果存在）
                    if (nvi.MenuItems?.Count > 0)
                    {
                        var found = FindNavItemByTag(nvi.MenuItems, tag);
                        if (found != null) return found;
                    }
                }
            }
            return null;
        }

        private Type? MapTagToPageType(string tag) =>
            tag switch
            {
                "Papers" => typeof(Papers),
                "LoadPapers" => typeof(LoadPapers),
                "Info" => typeof(Info),
                "Settings" => typeof(Settings),
                _ => typeof(Papers)
            };

        private void nvSample_ItemInvoked(NavigationView sneder, NavigationViewItemInvokedEventArgs args)
        {
            string tag = args.InvokedItemContainer.Tag.ToString();

            switch (tag)
            {
                case "Papers":
                    contentFrame.Navigate(typeof(Papers), null);
                    break;
                case "LoadPapers":
                    contentFrame.Navigate(typeof(LoadPapers), null);
                    break;
                case "Info":
                    contentFrame.Navigate(typeof(Info), null);
                    break;
                case "Settings":
                    contentFrame.Navigate(typeof(Settings), null);
                    break;
            }
        }

    }
}
 
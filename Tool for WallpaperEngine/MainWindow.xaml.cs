using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Tool_for_WallpaperEngine.Service;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Tool_for_WallpaperEngine
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private readonly IConfigService _configService = new ConfigService();
        public MainWindow()
        {
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
            catch
            {
                // 忽略加载失败，保留默认行为
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
 
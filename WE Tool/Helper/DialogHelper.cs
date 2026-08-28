using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WE_Tool.Helper
{
    class DialogHelper
    {
        public static async Task ShowMessageAsync(string title, string content)
        {
            var xamlRoot = App.MainWindowInstance?.Content?.XamlRoot;

            if (xamlRoot == null) return;

            ContentDialog dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "确定",
                XamlRoot = xamlRoot,
                // 弹层不自动继承主窗口运行时主题,显式应用(见 App.ApplyPopupTheme)
                RequestedTheme = App.GetPopupTheme()
            };
            await dialog.ShowAsync();
        }

        public static async Task<bool> ShowConfirmDialogAsync(string title, string content, string primaryText = "确定", string closeText = "取消")
        {
            var xamlRoot = App.MainWindowInstance?.Content?.XamlRoot;

            if (xamlRoot == null) return false;

            ContentDialog dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = primaryText,
                CloseButtonText = closeText,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
                // 弹层不自动继承主窗口运行时主题,显式应用(见 App.ApplyPopupTheme)
                RequestedTheme = App.GetPopupTheme()
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
    }
}

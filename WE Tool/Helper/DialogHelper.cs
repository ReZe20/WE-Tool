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
                XamlRoot = xamlRoot
            };
            await dialog.ShowAsync();
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Serilog;
using System;
using System.Threading.Tasks;
using Microsoft.Windows.AppLifecycle;
using WE_Tool.Models;

namespace WE_Tool.ViewModels.Settings
{
    public partial class AppearanceViewModel : ObservableObject
    {
        private bool _isBatchUpdating = false;

        [ObservableProperty]
        public partial string AppLanguage { get; set; } = null!;

        [ObservableProperty]
        public partial string StartPageTag { get; set; } = null!;

        [ObservableProperty]
        public partial string Theme { get; set; } = null!;

        public void LoadFromSettings(AppSettings settings)
        {
            _isBatchUpdating = true;

            AppLanguage = settings.AppLanguage ?? "default";
            StartPageTag = string.IsNullOrEmpty(settings.StartPageTag) ? "Papers" : settings.StartPageTag;
            Theme = settings.Theme;

            _isBatchUpdating = false;
        }

        partial void OnAppLanguageChanged(string value)
        {
            if (_isBatchUpdating) return;
            _ = ShowRestartDialog();
        }

        partial void OnThemeChanged(string value)
        {
            if (_isBatchUpdating) return;

            try
            {
                var app = Microsoft.UI.Xaml.Application.Current as App;
                app?.LoadTheme();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "尝试应用主题时失败。");
            }
        }
        private static async Task ShowRestartDialog()
        {
            var xamlRoot = App.MainWindowInstance?.Content?.XamlRoot;
            if (xamlRoot == null)
            {
                Log.Warning("无法显示重启对话框：XamlRoot 为空。");
                return;
            }

            ContentDialog dialog = new()
            {
                Title = "需要重启",
                Content = "更改语言设置后需要重启应用程序才能完全生效。是否现在重启？",
                PrimaryButtonText = "立即重启",
                CloseButtonText = "稍后重启",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                AppInstance.Restart("");
            }
        }
    }
}

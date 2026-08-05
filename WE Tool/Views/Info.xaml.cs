using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Models;
using WE_Tool.Service;
using WE_Tool.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WE_Tool;

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class Info : Page
{
    public SettingsViewModel ViewModel { get; }
    public string VersionText { get; } = GetVersionText();
    public string ConfigPathText => ViewModel.AppSettingsVM.ConfigPath;
    public string LogPathText => ViewModel.AppSettingsVM.LogPath;
    public string CachePathText => ViewModel.AppSettingsVM.CachePath;
    public ObservableCollection<Contributor> Contributors { get; } = new();
    public ObservableCollection<Contributor> RepkgContributors { get; } = new();

    /// <summary>RePKG_Re 后端版本:读取随包 exe 的文件版本(0.4.2.0 → 0.4.2),自动跟随后端发布</summary>
    public string RepkgVersionText
    {
        get
        {
            try
            {
                var exePath = Path.Combine(AppContext.BaseDirectory, "repkg", "RePKG_Re.exe");
                if (!File.Exists(exePath)) return string.Empty;
                var version = FileVersionInfo.GetVersionInfo(exePath).FileVersion;
                return string.IsNullOrEmpty(version) ? string.Empty : version.TrimEnd('0', '.');
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public Info()
    {
        var app = Application.Current as App;
        ViewModel = app?.ViewModel ?? new SettingsViewModel(new ConfigService(), new PickerService());
        InitializeComponent();
        _ = LoadContributorsAsync(Contributors, Path.Combine(AppContext.BaseDirectory, "Assets", "Contributors.csv"));
        _ = LoadContributorsAsync(RepkgContributors, Path.Combine(AppContext.BaseDirectory, "Assets", "ContributorsRepkg.csv"));
    }

    private static string GetVersionText()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return version == null ? string.Empty : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>从 CSV 加载贡献者(照抄 BetterLyrics 的 CSV 解析)</summary>
    private async Task LoadContributorsAsync(ObservableCollection<Contributor> target, string path)
    {
        try
        {
            if (!File.Exists(path)) return;

            var lines = await File.ReadAllLinesAsync(path);

            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = Regex.Split(line, ",(?=(?:[^\"]*\"[^\"]*\")*[^\"]*$)");

                if (parts.Length >= 4)
                    target.Add(new Contributor
                    {
                        Header = parts[0].Trim('"', ' '),
                        AvatarSource = parts[1].Trim('"', ' '),
                        Badges = parts[2].Trim('"', ' '),
                        Description = parts[3].Trim('"', ' ')
                    });
            }
        }
        catch
        {
            // 贡献者加载失败不影响页面
        }
    }

    private async void LicenseButton_Click(object sender, RoutedEventArgs e)
        => await ShowTextFileDialogAsync(
            LanguageHelper.GetResource("Info_License.Header"),
            Path.Combine(AppContext.BaseDirectory, "LICENSE"),
            "https://github.com/ReZe20/WE-Tool/blob/master/LICENSE");

    private async void ThirdPartyButton_Click(object sender, RoutedEventArgs e)
        => await ShowTextFileDialogAsync(
            LanguageHelper.GetResource("Info_ThirdPartyButton.Content"),
            Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt"),
            "https://github.com/ReZe20/WE-Tool/blob/master/THIRD-PARTY-NOTICES.txt");

    private async void RepkgLicenseButton_Click(object sender, RoutedEventArgs e)
        => await ShowTextFileDialogAsync(
            LanguageHelper.GetResource("Info_License.Header"),
            Path.Combine(AppContext.BaseDirectory, "repkg", "LICENSE"),
            "https://github.com/ReZe20/repkg-Re/blob/master/LICENSE");

    private async void RepkgThirdPartyButton_Click(object sender, RoutedEventArgs e)
        => await ShowTextFileDialogAsync(
            LanguageHelper.GetResource("Info_RepkgThirdPartyButton.Content"),
            Path.Combine(AppContext.BaseDirectory, "repkg", "THIRD-PARTY-NOTICES.txt"),
            "https://github.com/ReZe20/repkg-Re/blob/master/THIRD-PARTY-NOTICES.txt");

    /// <summary>在应用内对话框显示许可证/第三方组件全文(可选中、可滚动);viewUrl 非空时在"关闭"左边加"在浏览器中查看"按钮</summary>
    private async Task ShowTextFileDialogAsync(string title, string filePath, string? viewUrl = null)
    {
        var xamlRoot = App.MainWindowInstance?.Content?.XamlRoot;
        if (xamlRoot == null) return;

        string text;
        try
        {
            text = File.Exists(filePath) ? await File.ReadAllTextAsync(filePath) : LanguageHelper.GetResource("Info_LicenseFileMissing.Text");
        }
        catch (Exception ex)
        {
            text = $"{LanguageHelper.GetResource("Info_LicenseFileMissing.Text")}\n{ex.Message}";
        }

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new ScrollViewer
        {
            Content = new TextBlock
            {
                Text = text,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
            },
            MaxHeight = 400,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        // 底部按钮行(右对齐):[在浏览器中查看](HyperlinkButton,左) [关闭](右)
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        if (viewUrl != null)
        {
            buttonRow.Children.Add(new HyperlinkButton
            {
                Content = LanguageHelper.GetResource("Info_ViewInBrowser.Text"),
                NavigateUri = new Uri(viewUrl),
                // 样式在 App.xaml(应用级):页面 Resources 索引器不参与资源链,必须从 Application.Current.Resources 取
                Style = Application.Current.Resources["ExternalLinkButtonStyle"] as Style,
            });
        }

        var closeButton = new Button
        {
            Content = LanguageHelper.GetResource("Info_DialogClose.Content"),
        };
        buttonRow.Children.Add(closeButton);
        content.Children.Add(buttonRow);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            XamlRoot = xamlRoot,
        };
        closeButton.Click += (_, _) => dialog.Hide();

        await dialog.ShowAsync();
    }

    private void CopyConfigPath_Click(object sender, RoutedEventArgs e) => CopyToClipboard(ConfigPathText);

    private void CopyLogPath_Click(object sender, RoutedEventArgs e) => CopyToClipboard(LogPathText);

    private void CopyCachePath_Click(object sender, RoutedEventArgs e) => CopyToClipboard(CachePathText);

    private void OpenLogPath_Click(object sender, RoutedEventArgs e)
    {
        // 打开日志目录(不存在则先创建),方便用户直接查看日志
        var logDir = LogPathText;
        if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
        Process.Start(new ProcessStartInfo(logDir) { UseShellExecute = true });
    }

    private static void CopyToClipboard(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }
}

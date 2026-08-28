using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Models;
using WE_Tool.Service;
using WE_Tool.ViewModels;
using Windows.UI;
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

    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _lastSteamState = -1; // -1=未检查 0=正常 1=初始化失败 2=中途断开
    private Task? _steamInitTask;
    /// <summary>RePKG_Re 后端版本:读取随包 exe 的文件版本(0.5.0.0 → 0.5.0),自动跟随后端发布</summary>
    public string RepkgVersionText
    {
        get
        {
            try
            {
                var exePath = Path.Combine(AppContext.BaseDirectory, "repkg", "RePKG_Re.exe");
                if (!File.Exists(exePath)) return string.Empty;
                var version = FileVersionInfo.GetVersionInfo(exePath).FileVersion;
                return string.IsNullOrEmpty(version) ? string.Empty : TrimFileVersion(version);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
    /// <summary>随包 RePKG_Re 是否为目标版本(静态信息,运行时不变;Info 页 InfoBar 与导航徽标共用)</summary>
    public static bool IsRepkgStatusOk()
    {
        try
        {
            var required = RepkgVersionInfo.Required;
            if (string.IsNullOrEmpty(required)) return false; // 构建时未注入(external 缺失)
            var exePath = Path.Combine(AppContext.BaseDirectory, "repkg", "RePKG_Re.exe");
            if (!File.Exists(exePath)) return false;
            var version = FileVersionInfo.GetVersionInfo(exePath).FileVersion;
            var current = string.IsNullOrEmpty(version) ? string.Empty : TrimFileVersion(version);
            return current == required;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 文件版本 "0.5.0.0" → "0.5.0":只裁掉 FileVersion 自动补的第四段(.0),保留 Major.Minor.Build。
    /// 不能用 TrimEnd('0', '.')——它会把 0.5.0.0 误剪成 0.5(第三段为 0 时),与注入的 Required("0.5.0")比对失败。
    /// </summary>
    private static string TrimFileVersion(string fileVersion)
    {
        var parts = fileVersion.Split('.');
        if (parts.Length == 4 && parts[3] == "0")
            return $"{parts[0]}.{parts[1]}.{parts[2]}";
        return fileVersion;
    }
    /// <summary>校验随包 RePKG_Re 是否为目标版本(目标版本构建时从 external/repkg_Re 仓库 csproj 注入)</summary>
    private void UpdateRepkgStatus()
    {
        try
        {
            // 目标版本:构建时由 InjectRepkgVersion 目标生成 RepkgVersion.g.cs 常量,
            // 单源来自 external/repkg_Re/RePKG_Re/RePKG_Re.csproj
            var required = RepkgVersionInfo.Required;
            if (string.IsNullOrEmpty(required)) return; // 构建时未注入(如 external 缺失),不做校验

            var exePath = Path.Combine(AppContext.BaseDirectory, "repkg", "RePKG_Re.exe");
            if (!File.Exists(exePath))
            {
                RepkgStatusBar.Severity = InfoBarSeverity.Error;
                RepkgStatusBar.Title = LanguageHelper.GetResource("Info_RepkgVersionMissing.Title.Text");
                RepkgStatusBar.Message = LanguageHelper.GetResource("Info_RepkgVersionMissing.Message.Text");
            }
            else if (IsRepkgStatusOk())
            {
                RepkgStatusBar.Severity = InfoBarSeverity.Success;
                RepkgStatusBar.Title = LanguageHelper.GetResource("Info_RepkgVersionOk.Title.Text");
                RepkgStatusBar.Message = string.Format(
                    LanguageHelper.GetResource("Info_RepkgVersionOk.Message.Text"), RepkgVersionText);
            }
            else
            {
                RepkgStatusBar.Severity = InfoBarSeverity.Error;
                RepkgStatusBar.Title = LanguageHelper.GetResource("Info_RepkgVersionMismatch.Title.Text");
                RepkgStatusBar.Message = string.Format(
                    LanguageHelper.GetResource("Info_RepkgVersionMismatch.Message.Text"),
                    string.IsNullOrEmpty(RepkgVersionText) ? "?" : RepkgVersionText, required);
            }
            RepkgStatusBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "检查 RePKG_Re 版本失败");
        }
    }

    public ObservableCollection<TranslationStatusItem> TranslationStatus { get; } = new();
    public Info()
    {
        var app = Application.Current as App;
        ViewModel = app?.ViewModel ?? new SettingsViewModel(new ConfigService(), new PickerService());
        InitializeComponent();
        // 贡献者数据硬编码(ContributorsData.cs),发布包不再携带 CSV 文件
        LoadContributors(Contributors, ContributorsData.Main);
        LoadContributors(RepkgContributors, ContributorsData.Repkg);
        _logTimer.Tick += OnLogTimerTick;
        // Steamworks 首次初始化放后台线程,避免页面加载卡顿;完成后立即反映状态
        _steamInitTask = SteamWorkshopService.InitializeOnBackground();
        _ = RefreshSteamStatusAsync();
        UpdateRepkgStatus(); // RePKG_Re 后端版本校验(静态信息,加载时查一次)

        // 翻译完成度(构建时统计,加载时填充一次)
        foreach (var item in TranslationStatusInfo.Items)
            TranslationStatus.Add(item);
    }
    private static string GetVersionText()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return version == null ? string.Empty : $"{version.Major}.{version.Minor}.{version.Build}";
    }
    /// <summary>InfoBar 动作按钮:正常态=关闭 Steamworks,异常/已关闭态=重试。按当前状态分发。</summary>
    private async void SteamActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSteamState == 0)
        {
            // 正常态:关闭 Steamworks
            SteamWorkshopService.GetInstance().Shutdown();
            _lastSteamState = -1; // 强制刷新:立即反映"已关闭"状态
            UpdateSteamStatus();
            return;
        }

        // 异常/已关闭态:重试
        SteamActionButton.Visibility = Visibility.Collapsed;
        RetryProgressBar.Visibility = Visibility.Visible;
        SteamStatusBar.Severity = InfoBarSeverity.Warning;
        SteamStatusBar.Title = LanguageHelper.GetResource("Info_SteamRetrying.Title.Text");
        SteamStatusBar.Message = LanguageHelper.GetResource("Info_SteamRetrying.Message.Text");
        SteamStatusBar.IsOpen = true;

        await Task.Run(() => SteamWorkshopService.GetInstance().Reinitialize());
        // 给子进程一点时间完成 Steamworks 初始化(成功则响应状态,失败则退出)
        await Task.Delay(800);

        RetryProgressBar.Visibility = Visibility.Collapsed;
        _lastSteamState = -1; // 强制刷新:成功翻绿,失败应用红条 + 恢复重试按钮
        await SteamWorkshopService.GetInstance().RefreshStatusAsync();
        UpdateSteamStatus();
    }
    /// <summary>等待后台初始化完成后刷新状态(初始化期间 UI 不阻塞)</summary>
    private async Task RefreshSteamStatusAsync()
    {
        if (_steamInitTask is { } init)
        {
            _steamInitTask = null;
            await init;
        }
        await SteamWorkshopService.GetInstance().RefreshStatusAsync();
        UpdateSteamStatus();
    }
    /// <summary>反映 Steamworks 工作状况(经桥接子进程):正常=绿、初始化失败=红、断开(Steam 关闭桥接被杀)=黄;
    /// 状态变化才更新 UI;随日志轮询每秒复查</summary>
    private void UpdateSteamStatus()
    {
        try
        {
            var service = SteamWorkshopService.GetInstance();
            int state = service.Status switch
            {
                SteamworksStatus.Running => 0,
                SteamworksStatus.Disconnected => 2,
                SteamworksStatus.Stopped => 3,
                _ => 1,
            };
            if (state == _lastSteamState) return;
            _lastSteamState = state;

            // 正常态显示"关闭 Steamworks"按钮,异常/已关闭态显示"重试"按钮(单按钮双角色)
            if (state == 0)
            {
                SteamActionButton.Content = LanguageHelper.GetResource("Info_SteamShutdown.Content");
                SteamActionButton.Visibility = Visibility.Visible;
            }
            else
            {
                SteamActionButton.Content = LanguageHelper.GetResource("Info_SteamRetry.Content");
                SteamActionButton.Visibility = Visibility.Visible;
            }
            switch (state)
            {
                case 0:
                    SteamStatusBar.Severity = InfoBarSeverity.Success;
                    SteamStatusBar.Title = LanguageHelper.GetResource("Info_SteamStatusOk.Title.Text");
                    SteamStatusBar.Message = LanguageHelper.GetResource("Info_SteamStatusOk.Message.Text");
                    break;
                case 1:
                    SteamStatusBar.Severity = InfoBarSeverity.Error;
                    SteamStatusBar.Title = LanguageHelper.GetResource("Info_SteamStatusFail.Title.Text");
                    SteamStatusBar.Message = LanguageHelper.GetResource("Info_SteamStatusFail.Message.Text");
                    break;
                case 2:
                    SteamStatusBar.Severity = InfoBarSeverity.Warning;
                    SteamStatusBar.Title = LanguageHelper.GetResource("Info_SteamStatusLost.Title.Text");
                    SteamStatusBar.Message = LanguageHelper.GetResource("Info_SteamStatusLost.Message.Text");
                    break;
                case 3:
                    SteamStatusBar.Severity = InfoBarSeverity.Warning;
                    SteamStatusBar.Title = LanguageHelper.GetResource("Info_SteamStatusStopped.Title.Text");
                    SteamStatusBar.Message = LanguageHelper.GetResource("Info_SteamStatusStopped.Message.Text");
                    break;
            }
            SteamStatusBar.IsOpen = true;
        }
        catch
        {
            // Steamworks 未就绪(库缺失等)时保持隐藏
        }
    }

    /// <summary>每秒复查 Steamworks 状态(仅页面可见时运行;日志轮询已迁至独立"日志"页)。</summary>
    private void OnLogTimerTick(object? sender, object e)
    {
        _ = RefreshSteamStatusAsync();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // 页面可见才轮询(离开即停;页面缓存复用不重建实例)
        _ = RefreshSteamStatusAsync();
        _logTimer.Start();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logTimer.Stop();
    }
    /// <summary>从 CSV 加载贡献者(照抄 BetterLyrics 的 CSV 解析)</summary>
    private void LoadContributors(ObservableCollection<Contributor> target, IEnumerable<Contributor> source)
    {
        try
        {
            foreach (var c in source)
                target.Add(new Contributor
                {
                    Header = c.Header,
                    AvatarSource = c.AvatarSource,
                    Badges = c.Badges,
                    Description = c.Description
                });
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
            // 显式跟随主程序主题:弹窗默认可能落回系统主题,与主窗口设置的主题不一致(统一走 App.GetPopupTheme)
            RequestedTheme = App.GetPopupTheme(),
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
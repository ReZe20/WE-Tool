using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using WE_Tool.Service;
using WE_Tool.ViewModels;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WE_Tool;

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class Settings : Page
{
    public SettingsViewModel ViewModel { get; }
    private CancellationTokenSource? _pathChangedCts;
    static bool firstClickSettingsPage = true;

    public Settings()
    {
        InitializeComponent();

        ViewModel = new SettingsViewModel(new ConfigService(), new PickerService());

        DataContext = this;

        this.Loaded += async (s, e) => await ViewModel.InitializeAsync();
    }

    private async void WallpapersPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (firstClickSettingsPage == true)
        {
            firstClickSettingsPage = false;
            return;
        }
        _pathChangedCts?.Cancel();
        _pathChangedCts = new CancellationTokenSource();

        try
        {
            await Task.Delay(1000, _pathChangedCts.Token);

            TriggerGlobalScan();
        }
        catch (TaskCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex,"壁纸路径变化后启动扫描异常。");
        }
    }
    private void TriggerGlobalScan()
    {
        App.StartBackgroundScan(ViewModel.WorkshopPath, ViewModel.OfficialPath, ViewModel.ProjectPath, ViewModel.AcfPath);
    }
}

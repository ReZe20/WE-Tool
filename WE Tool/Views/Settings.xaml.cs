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
    static int firstClickSettingsPage = 0;

    public Settings()
    {
        var app = Application.Current as App;
        ViewModel = app?.ViewModel ?? new SettingsViewModel(new ConfigService(), new PickerService());
        InitializeComponent();
        DataContext = this;
    }

    private async void WallpapersPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (firstClickSettingsPage < 3)
        {
            firstClickSettingsPage += 1;
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
        App.StartBackgroundScan(ViewModel.PathManagementVM.WorkshopPath, ViewModel.PathManagementVM.OfficialPath, ViewModel.PathManagementVM.ProjectPath, ViewModel.PathManagementVM.AcfPath);
    }

    private void ComboBoxItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {

    }

    private void OutputTypeHelpButton_Click(object sender, RoutedEventArgs e)
    {
        OutputTypeTeachingTip.Target = OutputTypeFirstRadio;
        OutputTypeTeachingTip.IsOpen = true;
    }

    private void FolderStructureHelpButton_Click(object sender, RoutedEventArgs e)
    {
        FolderStructureTeachingTip.Target = FolderStructureFirstRadio;
        FolderStructureTeachingTip.IsOpen = true;
    }

    private void SceneWallpaperHelpButton_Click(object sender, RoutedEventArgs e)
    {
        SceneWallpaperTeachingTip.Target = SceneWallpaperFirstRadio;
        SceneWallpaperTeachingTip.IsOpen = true;
    }

    private void TeachingTip_ActionButtonClick(TeachingTip sender, object args)
    {
        if (sender == OutputTypeTeachingTip)
        {
            if (OutputTypeTeachingTip.Target == OutputTypeFirstRadio)
            {
                OutputTypeTeachingTip.Target = OutputTypeImageOnlyRadio;
            }
            else if (OutputTypeTeachingTip.Target == OutputTypeImageOnlyRadio)
            {
                OutputTypeTeachingTip.Target = OutputTypeCustomRadio;
            }
            else
            {
                OutputTypeTeachingTip.IsOpen = false;
                return;
            }
        }
        else if (sender == FolderStructureTeachingTip)
        {
            if (FolderStructureTeachingTip.Target == FolderStructureFirstRadio)
            {
                FolderStructureTeachingTip.Target = FolderStructureFlatRadio;
            }
            else
            {
                FolderStructureTeachingTip.IsOpen = false;
                return;
            }
        }
        else if (sender == SceneWallpaperTeachingTip)
        {
            if (SceneWallpaperTeachingTip.Target == SceneWallpaperFirstRadio)
            {
                SceneWallpaperTeachingTip.Target = SceneWallpaperFlattenRadio;
            }
            else
            {
                SceneWallpaperTeachingTip.IsOpen = false;
                return;
            }
        }
    }

    private void TeachingTip_CloseButtonClick(TeachingTip sender, object args)
    {
        sender.IsOpen = false;
    }
}

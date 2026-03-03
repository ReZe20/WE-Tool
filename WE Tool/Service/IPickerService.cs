using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Models;

namespace WE_Tool.Service
{
    public interface IPickerService
    {
        Task<string?> PickFolderAsync();
        Task<string?> PickFileAsync();
        Task OpenFolderAsync(string folderPath);
        Task<bool> DeleteFolderAsync(string folderPath);               
    }

    public class PickerService : IPickerService 
    {
        public async Task<string?> PickFolderAsync()
        {
            var folderPicker = new Windows.Storage.Pickers.FolderPicker();

            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

            folderPicker.FileTypeFilter.Add("*");
            var folder = await folderPicker.PickSingleFolderAsync();
            return folder?.Path;
        }
        public async Task<string?> PickFileAsync()
        {
            var filePicker = new Windows.Storage.Pickers.FileOpenPicker();

            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
            WinRT.Interop.InitializeWithWindow.Initialize(filePicker, hwnd);

            filePicker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
            filePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;

            filePicker.FileTypeFilter.Add(".acf");

            var file = await filePicker.PickSingleFileAsync();
            return file?.Path;
        }
        public async Task OpenFolderAsync(string folderPath)
        {
            await Task.Run(() => {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
                {
                    FileName = folderPath,
                    UseShellExecute = true
                });
            });
        }
        public async Task<bool> DeleteFolderAsync(string folderPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (Directory.Exists(folderPath))
                    {
                        Directory.Delete(folderPath, true);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"[PickerService] 物理删除文件夹失败：{folderPath}");
                    return false;
                }
            });
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WE_Tool.Helper;

namespace WE_Tool.Service
{
    public interface IPickerService
    {
        Task<string?> PickFolderAsync();
        Task<string?> PickFileAsync();
        Task OpenFolderAsync(string folderPath);
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
    }
}

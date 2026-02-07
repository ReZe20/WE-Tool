using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tool_for_WallpaperEngine.Service
{
    public interface IPickerService
    {
        Task<string?> PickFileAsync();
    }

    public class PickerService : IPickerService 
    {
        public async Task<string?> PickFileAsync()
        {
            var folderPicker = new Windows.Storage.Pickers.FolderPicker();

            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

            folderPicker.FileTypeFilter.Add("*");
            var folder = await folderPicker.PickSingleFolderAsync();
            return folder?.Path;
        }
    }
}

using System.IO;

namespace WE_Tool.ViewModels
{
    internal static class AppSettingsHelper
    {
        public static string ConfigPath
        {
            get
            {
                return System.IO.Path.Combine(App.GetAppDataRoot());
            }
        }

        public static string LogPath
        {
            get
            {
                return System.IO.Path.Combine(App.GetAppDataRoot(), "logs");
            }
        }
    }
}

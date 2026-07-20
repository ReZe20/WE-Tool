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

        /// <summary>缓存文件所在目录（wallpaper_cache.db）</summary>
        public static string CachePath
        {
            get
            {
                return System.IO.Path.Combine(App.GetAppDataRoot());
            }
        }
    }
}

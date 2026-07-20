using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace WE_Tool.ViewModels
{
    public partial class AppSettingsViewModel : ObservableObject
    {
        [ObservableProperty]
        public partial string AppLanguage { get; set; } = null!;

        [ObservableProperty]
        public partial string StartPageTag { get; set; } = null!;

        [ObservableProperty]
        public partial string Theme { get; set; } = null!;

        /// <summary>扫描缓存开关："0"=关闭, "1"=启用</summary>
        [ObservableProperty]
        public partial string ScanCacheEnabled { get; set; } = "1";

#pragma warning disable CA1822 // ConfigPath在之后需要实例访问，不标记static
        public string ConfigPath
        {
            get
            {
                return System.IO.Path.Combine(App.GetAppDataRoot());
            }
            set { }
        }

#pragma warning disable CA1822 // LogPath在之后需要实例访问，不标记static
        public string LogPath
        {
            get
            {
                return System.IO.Path.Combine(App.GetAppDataRoot(), "logs");
            }
            set { }
        }

        public string CachePath
        {
            get
            {
                return AppSettingsHelper.CachePath;
            }
            set { }
        }
    }
}

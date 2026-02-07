using System;

namespace Tool_for_WallpaperEngine.Models
{
    public class AppSettings
    {
        public int Version { get; set; } = 1;

        public string StartPageTag { get; set; } = "Papers";

        public string DownloadPath { get; set; } = "";
        public string WorkshopPath { get; set; } = "";
        public string ProjectPath { get; set; } = "";
        public string AcfPath { get; set; } = "";
        public bool IgnoreExtension { get; set; } = false;
        public string IgnoreExtensionList { get; set; } = "";

        public bool OnlyExtension { get; set; } = false;
        public string OnlyExtensionList { get; set; } = "";

        public bool ConvertTEX { get; set; } = false;
        public bool OneFolder { get; set; } = false;
        public bool OutProjectJSON { get; set; } = false;
        public bool UseProjectName { get; set; } = false;
        public bool DontConvertTEX { get; set; } = false;
        public bool CoverAllFiles { get; set; } = false;
    }
}

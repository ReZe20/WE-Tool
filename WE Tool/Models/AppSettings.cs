using System;
using System.IO;
using WE_Tool.Helper;

namespace WE_Tool.Models
{
    public class AppSettings
    {
        public int Version { get; set; } = 1;
        public string AppLanguage { get; set; } = "zh-CN";
        public string StartPageTag { get; set; } = "Papers";
        public PapersConfig Papers { get; set; } = new PapersConfig();
        public PapersConfig.Expander Expander { get; set; } = new PapersConfig.Expander();
        public PathConfig Path { get; set; } = new PathConfig();
        public ExtractSettings Extract { get; set; } = new ExtractSettings();
    }

    public class PapersConfig
    {
        public int BottomBarHeight { get; set; } = 50;
        public bool IsBottomBarOpen { get; set; } = true;
        public bool AutoPlayGif { get; set; } = true;
        public int WallpaperViewIndex { get; set; } = 0;
        public int WallpaperListMinWidth { get; set; } = 180;
        public bool LeftSplitViewPaneOpen { get; set; } = true;
        public bool RightSplitViewPaneOpen { get; set; } = true;
        public int SortOrder { get; set; } = 0;
        public bool IsSortAscending { get; set; } = true;
        public bool DetailSelectionEnabled { get; set; } = true;
        public int FilterResultResponseDelay { get; set; } = 1000;
        public class Expander
        {
            public bool TypeExpander { get; set; } = true;
            public bool Scene { get; set; } = true;
            public bool Video { get; set; } = true;
            public bool Web { get; set; } = true;
            public bool Application { get; set; } = true;
            public bool Preset { get; set; } = true;
            public bool Unknown { get; set; } = true;

            public bool RatingExpander { get; set; } = true;
            public bool G { get; set; } = true;
            public bool Pg { get; set; } = false;
            public bool R { get; set; } = false;

            public bool SourceExpander { get; set; } = true;
            public bool Official { get; set; } = true;
            public bool Workshop { get; set; } = true;
            public bool Mine { get; set; } = true;

            public bool TagsExpander { get; set; } = true;
            public bool Abstract { get; set; } = true;
            public bool Animal { get; set; } = true;
            public bool Anime { get; set; } = true;
            public bool Cartoon { get; set; } = true;
            public bool Cgi { get; set; } = true;
            public bool Cyberpunk { get; set; } = true;
            public bool Fantasy { get; set; } = true;
            public bool Game { get; set; } = true;
            public bool Girls { get; set; } = true;
            public bool Guys { get; set; } = true;
            public bool Landscape { get; set; } = true;
            public bool Medieval { get; set; } = true;
            public bool Memes { get; set; } = true;
            public bool Mmd { get; set; } = true;
            public bool Music { get; set; } = true;
            public bool Nature { get; set; } = true;
            public bool Pixelart { get; set; } = true;
            public bool Relaxing { get; set; } = true;
            public bool Retro { get; set; } = true;
            public bool SciFi { get; set; } = true;
            public bool Sports { get; set; } = true;
            public bool Technology { get; set; } = true;
            public bool Television { get; set; } = true;
            public bool Vehicle { get; set; } = true;
            public bool Unspecified { get; set; } = true;
        }
    }

    public class PathConfig
    {
        public string DownloadPath { get; set; } = "";
        public string WorkshopPath { get; set; } = "";
        public string ProjectPath { get; set; } = "";
        public string OfficialPath { get; set; } = "";
        public string AcfPath { get; set; } = "";
    }

    public class ExtractSettings
    {
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

using System;
using System.IO;

namespace Tool_for_WallpaperEngine.Models
{
    public class AppSettings
    {
        public int Version { get; set; } = 1;

        public PapersConfig Papers { get; set; } = new PapersConfig();
        public PapersConfig.Expander Expander { get; set; } = new PapersConfig.Expander();
        public string StartPageTag { get; set; } = "Papers";

        public PathConfig Path { get; set; } = new PathConfig();
        public ExtractSettings Extract { get; set; } = new ExtractSettings();
    }

    public class PapersConfig
    {
        public bool LeftSplitViewPaneOpen { get; set; } = true;
        public bool RightSplitViewPaneOpen { get; set; } = true;

        public class Expander
        {
            public bool TypeExpander { get; set; } = true;
            public bool Scene { get; set; } = true;
            public bool Video { get; set; } = true;
            public bool Web { get; set; } = true;
            public bool Application { get; set; } = true;
            public bool Regular { get; set; } = true;
            public bool Preset { get; set; } = true;

            public bool RatingExpander { get; set; } = true;
            public bool G { get; set; } = true;
            public bool PG { get; set; } = true;
            public bool R { get; set; } = true;

            public bool SourceExpander { get; set; } = true;
            public bool Official { get; set; } = true;
            public bool Workshop { get; set; } = true;
            public bool Mine { get; set; } = true;

            public bool TagsExpander { get; set; } = true;
            public bool Abstract { get; set; } = true;
            public bool Animal { get; set; } = true;
            public bool Anime { get; set; } = true;
            public bool Cartoon { get; set; } = true;
            public bool CGI { get; set; } = true;
            public bool Cyberpunk { get; set; } = true;
            public bool Fantasy { get; set; } = true;
            public bool Game { get; set; } = true;
            public bool Girls { get; set; } = true;
            public bool Guys { get; set; } = true;
            public bool Landscape { get; set; } = true;
            public bool Medieval { get; set; } = true;
            public bool Memes { get; set; } = true;
            public bool MMD { get; set; } = true;
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

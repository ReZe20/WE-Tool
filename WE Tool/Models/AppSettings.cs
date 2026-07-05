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
        public string Theme { get; set; } = "Default";
        public PapersConfig Papers { get; set; } = new PapersConfig();
        public PapersConfig.Expander Expander { get; set; } = new PapersConfig.Expander();
        public PathConfig Path { get; set; } = new PathConfig();
        public ExtractSettings Extract { get; set; } = new ExtractSettings();
    }

    public class PapersConfig
    {
        public bool IsBottomBarOpen { get; set; } = true;
        public bool AutoPlayGif { get; set; } = true;
        public bool IsWallpaperEnterAnimationEnabled { get; set; } = false;
        public bool IsAnnotatedScrollBarEnabled { get; set; } = false;
        public int WallpaperTagDisplayIndex { get; set; } = 0;
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
        // 通用设置
        public bool UseProjectName { get; set; } = false;
        public int OneFolder { get; set; } = 0;
        public bool CoverAllFiles { get; set; } = false;
        /// <summary>平铺输出时的文件命名模式：0=保持原文件名, 1=按壁纸名命名（重复加序号）</summary>
        public int FlatFileNamingMode { get; set; } = 0;
        /// <summary>子文件夹模式下保持源目录结构：0=保持, 1=打平</summary>
        public int KeepSubfolderStructure { get; set; } = 1;

        // 文件过滤（通用：对 PKG 解析和直接拷贝均生效）
        public bool IgnoreExtension { get; set; } = false;
        public string IgnoreExtensionList { get; set; } = "";
        public bool OnlyExtension { get; set; } = false;
        public string OnlyExtensionList { get; set; } = "";

        // PKG 专用
        public bool OutProjectJSON { get; set; } = false;
        /// <summary>0=导出原始文件(TEX不转换), 1=导出并转换TEX为图片, 2=只导出TEX转换后的图片</summary>
        public int TexExportMode { get; set; } = 1;
        /// <summary>输出类型：0=自定义, 1=全量输出, 2=仅输出图像</summary>
        public int OutputMode { get; set; } = 0;

        // 性能参数（阶段1）
        /// <summary>最大并发提取数，默认 CPU 逻辑核心数</summary>
        public int MaxConcurrentExtractions { get; set; } = Environment.ProcessorCount;
        /// <summary>0=Normal, 1=BelowNormal, 2=Idle</summary>
        public int ProcessPriority { get; set; } = 0;

        /// <summary>如果输出目录已存在且非空，跳过该壁纸</summary>
        public bool SkipExistingOutput { get; set; } = false;
        /// <summary>分块解析模式，逐条读取减少内存占用</summary>
        public bool LazyLoad { get; set; } = true;

    }
}

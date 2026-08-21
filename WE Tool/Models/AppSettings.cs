using System;
using System.IO;
using WE_Tool.Helper;

namespace WE_Tool.Models
{
    public class AppSettings
    {
        public int Version { get; set; } = 2;
        public string AppLanguage { get; set; } = "default";
        public string StartPageTag { get; set; } = "Papers";
        public string Theme { get; set; } = "Default";
        public string LogLevel { get; set; } = "Information";
        public int WindowX { get; set; } = -1;
        public int WindowY { get; set; } = -1;
        public int WindowWidth { get; set; } = -1;
        public int WindowHeight { get; set; } = -1;
        public PapersConfig Papers { get; set; } = new PapersConfig();
        public ComponentsConfig Components { get; set; } = new ComponentsConfig();
        public PathConfig Path { get; set; } = new PathConfig();
        public ExtractSettings Extract { get; set; } = new ExtractSettings();
        public AutoBackupConfig AutoBackup { get; set; } = new AutoBackupConfig();
        public string ScanCacheEnabled { get; set; } = "1";
        public bool RestoreWindowGeometry { get; set; } = true;
        public bool WindowMaximized { get; set; } = false;
    }

    public class PapersConfig
    {
        public bool IsBottomBarOpen { get; set; } = true;
        public bool AutoPlayGif { get; set; } = true;
        public bool IsWallpaperEnterAnimationEnabled { get; set; } = false;
        public int WallpaperTagDisplayIndex { get; set; } = 0;
        public int WallpaperViewIndex { get; set; } = 0;
        /// <summary>预览模糊(按年龄段):勾选哪个年龄段,该年龄段壁纸的预览图就高斯模糊</summary>
        public bool BlurEveryone { get; set; } = false;
        public bool BlurTeen { get; set; } = false;
        public bool BlurAdult { get; set; } = false;
        public int WallpaperDisplayMode { get; set; } = 0;
        public int WallpaperListMinWidth { get; set; } = 180;
        public bool LeftSplitViewPaneOpen { get; set; } = true;
        public bool RightSplitViewPaneOpen { get; set; } = true;
        /// <summary>右侧面板模式：0=详情面板, 1=属性面板</summary>
        public int RightPanelIndex { get; set; } = 0;
        public int SortOrder { get; set; } = 0;
        public bool IsSortAscending { get; set; } = true;
        public bool DetailSelectionEnabled { get; set; } = true;
        public int FilterResultResponseDelay { get; set; } = 1000;
        /// <summary>0=不分页, 1=每页30, 2=每页50, 3=每页70, 4=每页90</summary>
        public int PaginationMode { get; set; } = 0;
        public ExpanderConfig Expander { get; set; } = new ExpanderConfig();
        public class ExpanderConfig
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

            public bool SubscriptionExpander { get; set; } = true;
            public bool Subscribed { get; set; } = true;
            public bool Unsubscribed { get; set; } = true;

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
        public string VdfPath { get; set; } = "";
        /// <summary>导入解包页的导出目录(留空则回退 ProjectPath)。</summary>
        public string ImportExportPath { get; set; } = "";
    }

    public class ExtractSettings
    {
        // 通用设置
        public bool UseProjectName { get; set; } = true;
        public int OneFolder { get; set; } = 0;
        public bool CoverAllFiles { get; set; } = true;
        /// <summary>平铺输出时的文件命名模式：0=保持原文件名, 1=按壁纸名命名（重复加序号）</summary>
        public int FlatFileNamingMode { get; set; } = 0;
        /// <summary>子文件夹模式下保持源目录结构：0=保持, 1=打平</summary>
        public int KeepSubfolderStructure { get; set; } = 1;

        // 文件过滤（通用：对 PKG 解析和直接拷贝均生效）
        public bool IgnoreExtension { get; set; } = false;
        public string IgnoreExtensionList { get; set; } = "";
        public bool OnlyExtension { get; set; } = false;
        public string OnlyExtensionList { get; set; } = "";
        // 目录过滤（自定义模式，仅对 PKG 解析生效）
        public bool OnlyPaths { get; set; } = false;
        public string OnlyPathsList { get; set; } = "";
        public bool IgnorePaths { get; set; } = false;
        public string IgnorePathsList { get; set; } = "";

        // PKG 专用
        public bool OutProjectJSON { get; set; } = false;
        /// <summary>0=导出原始文件(TEX不转换), 1=导出并转换TEX为图片, 2=只导出TEX转换后的图片</summary>
        public int TexExportMode { get; set; } = 1;
        /// <summary>输出类型：0=全量输出, 1=仅输出媒体文件, 2=自定义</summary>
        public int OutputMode { get; set; } = 1;
        /// <summary>效果图剔除阈值(%):转换图透明或黑色占比 ≥ 该值则整条目跳过;0=关闭</summary>
        public int FilterEffectImagesThreshold { get; set; } = 0;
        /// <summary>效果图剔除开关(自定义模式):勾选后按阈值剔除效果图</summary>
        public bool FilterEffectImagesEnabled { get; set; } = false;

        // 性能参数(阶段1)
        /// <summary>batch 最大线程数(单进程 batch 模式,v0.5.0 起语义从"进程数"变为"线程数"),默认 CPU 逻辑核心数</summary>
        public int MaxConcurrentExtractions { get; set; } = Environment.ProcessorCount;
        /// <summary>0=Normal, 1=BelowNormal, 2=Idle</summary>
        public int ProcessPriority { get; set; } = 0;

        /// <summary>如果输出目录已存在且非空，跳过该壁纸</summary>
        public bool SkipExistingOutput { get; set; } = false;
        /// <summary>分块解析模式，逐条读取减少内存占用</summary>
        public bool LazyLoad { get; set; } = true;

    }

    public class ComponentsConfig
    {
        public int ComponentViewIndex { get; set; } = 0;
        public int ComponentTagDisplayIndex { get; set; } = 0;
        public int ComponentListMinWidth { get; set; } = 180;
        public bool AutoPlayGif { get; set; } = true;
        public bool IsComponentEnterAnimationEnabled { get; set; } = false;
        public bool IsBottomBarOpen { get; set; } = true;
        public bool DetailSelectionEnabled { get; set; } = true;
        public int FilterResultResponseDelay { get; set; } = 1000;
        public bool LeftSplitViewPaneOpen { get; set; } = true;
        public bool RightSplitViewPaneOpen { get; set; } = true;
        public int SortOrder { get; set; } = 0;
        public bool IsSortAscending { get; set; } = true;
        /// <summary>0=不分页, 1=每页30, 2=每页50, 3=每页70, 4=每页90</summary>
        public int PaginationMode { get; set; } = 0;
        public ComponentsExpanderConfig Expander { get; set; } = new();
    }

    public class ComponentsExpanderConfig
    {
        // --- Expander 展开状态 ---
        public bool TypeExpander { get; set; } = true;
        public bool RatingExpander { get; set; } = true;
        public bool TagsExpander { get; set; } = true;

        // --- 类型 ---
        public bool Layers { get; set; } = true;
        public bool Scripts { get; set; } = true;
        public bool Effects { get; set; } = true;

        // --- 年龄 ---
        public bool Everyone { get; set; } = true;
        public bool Questionable { get; set; } = false;
        public bool Mature { get; set; } = false;

        // --- 标签 ---
        public bool UnspecifiedGenre { get; set; } = true;
        public bool Abstract { get; set; } = true;
        public bool Anime { get; set; } = true;
        public bool AudioVisualizer { get; set; } = true;
        public bool Background { get; set; } = true;
        public bool Cgi { get; set; } = true;
        public bool Character { get; set; } = true;
        public bool Clock { get; set; } = true;
        public bool Fire { get; set; } = true;
        public bool Interactive { get; set; } = true;
        public bool Magic { get; set; } = true;
        public bool Memes { get; set; } = true;
        public bool Nature { get; set; } = true;
        public bool PostProcessing { get; set; } = true;
        public bool Smoke { get; set; } = true;
        public bool Space { get; set; } = true;
        public bool Sports { get; set; } = true;
        public bool Technology { get; set; } = true;
        public bool Vehicle { get; set; } = true;
    }

    /// <summary>自动备份配置(壁纸备份页设置区,服务 AutoBackupService.exe 读取同一份 config.json)。</summary>
    public class AutoBackupConfig
    {
        /// <summary>自动备份总开关。</summary>
        public bool Enabled { get; set; } = false;
        /// <summary>服务已安装/启用标记(与服务 schtasks 状态同步)。</summary>
        public bool ServiceEnabled { get; set; } = false;
        /// <summary>筛选:类型(与 Papers 的 Type* 语义一致)。</summary>
        public bool TypeScene { get; set; } = true;
        public bool TypeVideo { get; set; } = true;
        public bool TypeWeb { get; set; } = true;
        public bool TypeApplication { get; set; } = true;
        public bool TypePreset { get; set; } = true;
        public bool TypeUnknown { get; set; } = true;
        /// <summary>筛选:分级。</summary>
        public bool RatingG { get; set; } = true;
        public bool RatingPg { get; set; } = true;
        public bool RatingR { get; set; } = true;
    }
}

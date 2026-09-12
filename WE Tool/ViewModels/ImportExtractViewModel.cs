#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using WE_Tool.Models;

namespace WE_Tool.ViewModels
{
    /// <summary>
    /// [导入解包输出设置 2026-09] 导入解包页的导出配方,与「已安装壁纸页面输出设置」(SettingsViewModel 上那批
    /// Extract 属性)同构但**完全独立生效**:只承载设置页该分区的 UI 状态,读写落在 AppSettings.ImportExtract。
    /// 默认值 = 该页原先写死的"编辑器同款"配方(见 AppSettings.ImportExtract 的初始化)。
    /// </summary>
    public partial class ImportExtractViewModel : ObservableObject
    {
        // ==================== 输出类型 ====================

        /// <summary>输出类型：0=全量输出, 1=仅输出媒体文件, 2=自定义</summary>
        [ObservableProperty]
        public partial int OutputMode { get; set; }

        /// <summary>0=导出原始文件(TEX不转换), 1=导出并转换TEX为图片, 2=只导出TEX转换后的图片</summary>
        [ObservableProperty]
        public partial int TexExportMode { get; set; }

        /// <summary>全量输出(OutputMode==0)且平铺(OneFolder==1)同时选中时输出目录可能混乱,显示警告图标</summary>
        public bool IsConflictMode => OutputMode == 0 && OneFolder == 1;

        partial void OnOutputModeChanged(int value)
        {
            OnPropertyChanged(nameof(IsConflictMode));
        }

        // ==================== 仅输出媒体文件(OutputMode==1) ====================

        [ObservableProperty]
        public partial bool MediaExportImages { get; set; } = true;

        [ObservableProperty]
        public partial bool MediaExportVideos { get; set; } = true;

        [ObservableProperty]
        public partial bool MediaExportAudios { get; set; } = true;

        [ObservableProperty]
        public partial bool MediaFilterTransparentImages { get; set; } = true;

        /// <summary>媒体模式下"过滤透明图片"是否可勾选:须先勾选图片/动图</summary>
        public bool IsMediaFilterTransparentEnabled => MediaExportImages;

        partial void OnMediaExportImagesChanged(bool value)
        {
            OnPropertyChanged(nameof(IsMediaFilterTransparentEnabled));
            // 图片被取消勾选时,透明过滤随之关闭(它只作用于图片)
            if (!value) MediaFilterTransparentImages = false;
        }

        // ==================== 自定义模式(OutputMode==2)的文件/目录过滤 ====================

        [ObservableProperty]
        public partial bool IgnoreExtension { get; set; }

        [ObservableProperty]
        public partial string IgnoreExtensionList { get; set; } = null!;

        [ObservableProperty]
        public partial bool OnlyExtension { get; set; }

        [ObservableProperty]
        public partial string OnlyExtensionList { get; set; } = null!;

        [ObservableProperty]
        public partial bool OnlyPaths { get; set; }

        [ObservableProperty]
        public partial string OnlyPathsList { get; set; } = null!;

        [ObservableProperty]
        public partial bool IgnorePaths { get; set; }

        [ObservableProperty]
        public partial string IgnorePathsList { get; set; } = null!;

        [ObservableProperty]
        public partial bool FilterEffectImagesEnabled { get; set; }

        /// <summary>效果图剔除阈值(%):0=关闭;1-100=透明或黑色占比达到该值的转换图整条目跳过</summary>
        [ObservableProperty]
        public partial int FilterEffectImagesThreshold { get; set; }

        public bool IsIgnoreExtensionTextBoxEnabled => IgnoreExtension;
        public bool IsOnlyExtensionTextBoxEnabled => OnlyExtension;
        public bool IsOnlyPathsTextBoxEnabled => OnlyPaths;
        public bool IsIgnorePathsTextBoxEnabled => IgnorePaths;

        partial void OnIgnoreExtensionChanged(bool value)
        {
            OnPropertyChanged(nameof(IsIgnoreExtensionTextBoxEnabled));
        }

        partial void OnOnlyExtensionChanged(bool value)
        {
            OnPropertyChanged(nameof(IsOnlyExtensionTextBoxEnabled));
        }

        partial void OnOnlyPathsChanged(bool value)
        {
            OnPropertyChanged(nameof(IsOnlyPathsTextBoxEnabled));
        }

        partial void OnIgnorePathsChanged(bool value)
        {
            OnPropertyChanged(nameof(IsIgnorePathsTextBoxEnabled));
        }

        // ==================== 文件夹结构 ====================

        /// <summary>0=每个壁纸一个子文件夹, 1=平铺输出</summary>
        [ObservableProperty]
        public partial int OneFolder { get; set; }

        /// <summary>0=覆盖已存在的文件, 1=跳过已提取的壁纸(仅子文件夹模式生效)</summary>
        [ObservableProperty]
        public partial int OverwriteMode { get; set; }

        [ObservableProperty]
        public partial bool UseProjectName { get; set; } = true;

        /// <summary>平铺输出时的文件命名模式：0=保持原文件名, 1=按壁纸名命名(重复加序号)</summary>
        [ObservableProperty]
        public partial int FlatFileNamingMode { get; set; }

        /// <summary>场景壁纸的子目录结构：0=保持源目录结构, 1=打平</summary>
        [ObservableProperty]
        public partial int KeepSubfolderStructure { get; set; }

        /// <summary>子文件夹模式(OneFolder==0)时,冲突处理等才可操作</summary>
        public bool IsSubfolderModeContentEnabled => OneFolder == 0;

        /// <summary>平铺模式(OneFolder==1)时,平铺相关控件才可操作</summary>
        public bool IsFlatModeContentEnabled => OneFolder == 1;

        partial void OnOneFolderChanged(int value)
        {
            OnPropertyChanged(nameof(IsSubfolderModeContentEnabled));
            OnPropertyChanged(nameof(IsConflictMode));
            OnPropertyChanged(nameof(IsFlatModeContentEnabled));
        }

        // ==================== 读写 ====================

        /// <summary>从配置填充(设置页加载设置/重置设置时调用)。</summary>
        public void LoadFrom(ExtractSettings s)
        {
            OutputMode = s.OutputMode;
            TexExportMode = s.TexExportMode;
            MediaExportImages = s.MediaExportImages;
            MediaExportVideos = s.MediaExportVideos;
            MediaExportAudios = s.MediaExportAudios;
            MediaFilterTransparentImages = s.MediaFilterTransparentImages;
            IgnoreExtension = s.IgnoreExtension;
            IgnoreExtensionList = s.IgnoreExtensionList;
            OnlyExtension = s.OnlyExtension;
            OnlyExtensionList = s.OnlyExtensionList;
            OnlyPaths = s.OnlyPaths;
            OnlyPathsList = s.OnlyPathsList;
            IgnorePaths = s.IgnorePaths;
            IgnorePathsList = s.IgnorePathsList;
            FilterEffectImagesEnabled = s.FilterEffectImagesEnabled;
            FilterEffectImagesThreshold = s.FilterEffectImagesThreshold;
            OneFolder = s.OneFolder;
            OverwriteMode = s.CoverAllFiles ? 0 : (s.SkipExistingOutput ? 1 : 0);
            UseProjectName = s.UseProjectName;
            FlatFileNamingMode = s.FlatFileNamingMode;
            KeepSubfolderStructure = s.KeepSubfolderStructure;
        }

        /// <summary>写回配置(设置页任意改动触发保存时调用)。</summary>
        public void SaveTo(ExtractSettings s)
        {
            s.OutputMode = OutputMode;
            s.TexExportMode = TexExportMode;
            s.MediaExportImages = MediaExportImages;
            s.MediaExportVideos = MediaExportVideos;
            s.MediaExportAudios = MediaExportAudios;
            s.MediaFilterTransparentImages = MediaFilterTransparentImages;
            s.IgnoreExtension = IgnoreExtension;
            s.IgnoreExtensionList = IgnoreExtensionList;
            s.OnlyExtension = OnlyExtension;
            s.OnlyExtensionList = OnlyExtensionList;
            s.OnlyPaths = OnlyPaths;
            s.OnlyPathsList = OnlyPathsList;
            s.IgnorePaths = IgnorePaths;
            s.IgnorePathsList = IgnorePathsList;
            s.FilterEffectImagesEnabled = FilterEffectImagesEnabled;
            s.FilterEffectImagesThreshold = FilterEffectImagesThreshold;
            s.OneFolder = OneFolder;
            s.CoverAllFiles = OverwriteMode == 0;
            s.SkipExistingOutput = OverwriteMode == 1;
            s.UseProjectName = UseProjectName;
            s.FlatFileNamingMode = FlatFileNamingMode;
            s.KeepSubfolderStructure = KeepSubfolderStructure;
        }
    }
}

using System;

namespace WE_Tool.Json
{
    /// <summary>
    /// 属性副窗口子进程的启动载荷:母进程写出 %TEMP% json,子进程读入即删。
    /// 子进程据此完全不读 config.json——主题/模糊/尺寸/语言全部由母进程算好后带过来,
    /// 避免两个进程同时全量覆写同一份配置(ConfigService.SaveAsync 是无锁全量覆盖)。
    /// </summary>
    public sealed class PropertyWindowSnapshot
    {
        public string Protocol { get; set; } = "";
        public string PipeName { get; set; } = "";
        /// <summary>母进程当时生效的首选 UI 语言(已解析,非"跟随系统"这种未定值)</summary>
        public string Language { get; set; } = "";
        /// <summary>母进程当时的日志级别设置:子进程据此开自己的日志,不再回头读 config.json</summary>
        public string LogLevel { get; set; } = "";
        public string Theme { get; set; } = "";
        public bool BlurEveryone { get; set; }
        public bool BlurTeen { get; set; }
        public bool BlurAdult { get; set; }
        /// <summary>是否显示"壁纸属性"页(组件等无 project.json 可配置属性的条目为 false)</summary>
        public bool ShowPropsPage { get; set; } = true;
        /// <summary>母进程按 RestorePropertiesWindowSize 开关决定是否填;0 表示用窗口默认尺寸</summary>
        public int Width { get; set; }
        public int Height { get; set; }

        // ===== 条目字段,镜像 Models/WallpaperItem(列表里已是内存值,无需子进程重扫) =====
        public string? WorkshopID { get; set; }
        public string? Title { get; set; }
        public string? FolderPath { get; set; }
        public string? Preview { get; set; }
        public string? ContentRating { get; set; }
        public string? Type { get; set; }
        public string? Description { get; set; }
        public string? Tags { get; set; }
        public string? Source { get; set; }
        public string? Dependency { get; set; }
        public DateTime CreationTime { get; set; }
        public DateTime UpdateTime { get; set; }
        public DateTime? AcfUpdateTime { get; set; }
        public long FileSize { get; set; }
        public long? AcfSize { get; set; }
    }

    /// <summary>副窗口管道上的一条消息(一行一个 JSON),属性 / 白名单 / 移动版队列三种副窗口共用。</summary>
    public sealed class PropertyWindowMessage
    {
        /// <summary>theme / blur / focus(母进程要求子窗口前置)/ add(母→子:白名单新增)/
        /// reload(母→子:面板刚写了同一张壁纸的 project.json,子窗口重读)/
        /// removed(子→母:白名单移除请求)| size(子→母:最新尺寸)| saved(子→母:已写 project.json)|
        /// mpkg-rows(母→子:页面又入了几行)| mpkg-queue(子→母:整份队列交回,见 Dock)|
        /// mpkg-busy(子→母:副窗口正在转换,母进程这段时间别开提取)</summary>
        public string Kind { get; set; } = "";
        public string? Theme { get; set; }
        public bool BlurEveryone { get; set; }
        public bool BlurTeen { get; set; }
        public bool BlurAdult { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        /// <summary>add/removed 用的工坊 ID。</summary>
        public string? EntryId { get; set; }

        /// <summary>mpkg-rows/mpkg-queue 带的行。null = 这条消息与移动版队列无关。</summary>
        public System.Collections.Generic.List<MpkgQueueRowDto>? Rows { get; set; }

        /// <summary>mpkg-queue 带的面板状态(总控档位、重命名档、自定义模式)。</summary>
        public MpkgPanelStateDto? Panel { get; set; }

        /// <summary>mpkg-queue:除了把队列交回,还要母进程把页面面板展开(点「贴回」);
        /// false = 只是把这份状态存下来,面板保持收起(关窗、退出前的那一次同步)。</summary>
        public bool Dock { get; set; }

        /// <summary>mpkg-busy:子进程这一批还在转。</summary>
        public bool Busy { get; set; }
    }
}

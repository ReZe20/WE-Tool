using System.Collections.Generic;

namespace WE_Tool.Json
{
    /// <summary>
    /// 「转为移动版」队列副窗口子进程的启动载荷。与属性/白名单副窗口同一条链路(启动载荷文件 + 一条双向管道),
    /// 差别是这一种<b>把队列的所有权一起交出去</b>:detach 之后母进程自己不再留一份,
    /// 编辑、探测、开转全在子进程里跑,队列只在改动时和退出时交回母进程 —— 只有一个持有者,才没有双向同步。
    ///
    /// 子进程因此要能自己开 repkg:输出根与进程优先级这两样原本读主 VM 的值得随载荷带过来,
    /// config.json / wallpaper_cache.json 一律不碰(与另两种副窗口同一条理由)。
    /// </summary>
    public sealed class MpkgWindowSnapshot
    {
        public string Protocol { get; set; } = "";
        public string PipeName { get; set; } = "";
        public string Language { get; set; } = "";
        public string LogLevel { get; set; } = "";
        public string Theme { get; set; } = "";
        /// <summary>上一次这个副窗口的大小(0 = 用默认);母进程只在本次会话里记,不写 config.json。</summary>
        public int Width { get; set; }
        public int Height { get; set; }

        /// <summary>产物根:母进程的下载目录,平铺在其下,一张壁纸一个 .mpkg。</summary>
        public string OutputRoot { get; set; } = "";

        /// <summary>repkg 子进程的优先级档位,与设置页「性能」那一格同一个值。</summary>
        public int ProcessPriority { get; set; }

        /// <summary>预览模糊的三档年龄段开关。子进程不读 config.json,这几格只能由母进程随载荷给;
        /// 之后母进程改动会经 KindTheme 那条消息再推一次(与属性副窗口同一个口径)。</summary>
        public bool BlurEveryone { get; set; }
        public bool BlurTeen { get; set; }
        public bool BlurAdult { get; set; }

        // ===== 面板状态:detach 时页面面板是什么样,窗口打开就是什么样 =====
        public MpkgPanelStateDto Panel { get; set; } = new();
        public List<MpkgQueueRowDto> Rows { get; set; } = [];
    }

    /// <summary>面板那三样"不属于某一行"的状态。跟着队列一起走,否则贴回来会发现总控被重置。</summary>
    public sealed class MpkgPanelStateDto
    {
        /// <summary>总控 slider 的档位序号(0/1/2)。它是命令不是状态,但用户拖过之后位置该留住。</summary>
        public int MasterTier { get; set; }

        /// <summary>重命名总控(0=标题 1=创意工坊 ID),同时是 MpkgPackingDefaults.NameMode 的新值。</summary>
        public int NameMode { get; set; }

        public bool CustomMode { get; set; }
    }

    /// <summary>
    /// 队列一行的跨进程形状:定位一张壁纸要的三个字段 + 这一行自己的逐行覆盖(null = 跟随全局) + 展开状态。
    /// 与 <see cref="WE_Tool.Models.MpkgQueueItem"/> 的私有字段一一对应,映射写在行模型上
    /// (<c>ToRowDto</c>/<c>FromRowDto</c>),因为那些覆盖字段只有它自己碰得到。
    /// </summary>
    public sealed class MpkgQueueRowDto
    {
        public string? WorkshopID { get; set; }
        public string? Title { get; set; }
        public string? FolderPath { get; set; }
        public string? Preview { get; set; }
        public string? Type { get; set; }

        /// <summary>分级(everyone/questionable/mature)。子进程手上没有主 VM,该不该糊只能靠这一格自己带。</summary>
        public string? ContentRating { get; set; }

        /// <summary>存下的档位意图(不是生效档位):照搬开着时生效值恒为 0,把 0 写回去就抹掉了他选的倍数。</summary>
        public int Tier { get; set; }

        public bool? KeepAudio { get; set; }
        public bool? UseLz4 { get; set; }
        public bool? ShaderCompat { get; set; }
        public bool? EncodeEtc2 { get; set; }
        public bool? CopyTextures { get; set; }
        public bool? ShrinkDx { get; set; }
        public int? NameMode { get; set; }
        public bool IsExpanded { get; set; }
    }
}

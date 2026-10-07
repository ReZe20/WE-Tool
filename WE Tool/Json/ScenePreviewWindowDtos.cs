namespace WE_Tool.Json
{
    /// <summary>
    /// 预览的三种模式。字符串而不是枚举:它要进快照 json、也要在换模式的管道消息里原样走,
    /// 加枚举还得为它单独注册源生成的转换器。
    /// </summary>
    public static class ScenePreviewModes
    {
        /// <summary>可控制属性预览:左画面右属性表,改一行当场热更。尺寸记进母进程的会话记忆。</summary>
        public const string Properties = "props";

        /// <summary>电脑比例预览:纯净一块面,窗口按本机显示器的实际像素比例。</summary>
        public const string DesktopRatio = "desktop";

        /// <summary>手机比例预览:纯净一块面,窗口按手机的竖屏比例。</summary>
        public const string PhoneRatio = "phone";

        /// <summary>手机竖屏比例。仓库里没有"这张壁纸的手机分辨率"这种数据(project.json 的
        /// generalproperties 是空的,acf 也没这字段),所以只能取一个当季主流值:1080×2340。</summary>
        public const double PhoneRatioW = 1080;
        public const double PhoneRatioH = 2340;
    }

    /// <summary>
    /// 场景预览副窗口子进程的启动载荷(母进程写 %TEMP% json,子进程读入即删)。
    /// 与前三种副窗口同一条链路,差别只在数据形状:这里只带「要预览哪张壁纸」,
    /// 包里的内容一律由子进程自己的 WebView2 现取现画,母进程不解析场景文件。
    ///
    /// 写权归属:预览是只读操作,子进程不写任何用户数据文件,所以没有第二写者问题。
    /// </summary>
    public sealed class ScenePreviewWindowSnapshot
    {
        public string Protocol { get; set; } = "";
        public string PipeName { get; set; } = "";
        public string Language { get; set; } = "";
        public string LogLevel { get; set; } = "";
        public string Theme { get; set; } = "";
        /// <summary>0 = 用窗口默认尺寸(母进程只在本次会话里记上一次的值,不写 config.json)。
        /// 只有可控制属性模式用它:另两种的尺寸由比例算出来,记下来反而会把比例带歪。</summary>
        public int Width { get; set; }
        public int Height { get; set; }
        /// <summary>壁纸所在目录(= 条目根,里面有 project.json 与 scene.pkg)</summary>
        public string Folder { get; set; } = "";
        /// <summary>标题上要显示的名字</summary>
        public string Title { get; set; } = "";
        /// <summary>见 <see cref="ScenePreviewModes"/>。空 = 按可控制属性模式处理(旧载荷的默认形态)。</summary>
        public string Mode { get; set; } = "";
    }
}

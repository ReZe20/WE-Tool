using System.Collections.Generic;

namespace WE_Tool.Json
{
    /// <summary>
    /// 白名单副窗口子进程的启动载荷。与属性副窗口同一条链路,差别只在数据形状:
    /// 条目是一组工坊 ID,卡片内容由子进程自己扫 workshop 目录得到(纯磁盘读,不需要母进程内存态)。
    ///
    /// 写权归属:cleanup_whitelist.json 只由母进程写。子窗口删条目时只发意图(removed),
    /// 由母进程改自己的集合、落盘并把该壁纸退回清理列表——避免出现第二个全量覆写的写者。
    /// </summary>
    public sealed class WhitelistWindowSnapshot
    {
        public string Protocol { get; set; } = "";
        public string PipeName { get; set; } = "";
        public string Language { get; set; } = "";
        public string LogLevel { get; set; } = "";
        public string Theme { get; set; } = "";
        public string WorkshopPath { get; set; } = "";
        public List<string> Entries { get; set; } = [];
    }
}

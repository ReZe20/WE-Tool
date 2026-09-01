using System;

namespace WE_Tool.Service
{
    /// <summary>导航徽标状态(对应 InfoBar Severity 的语义:绿=正常进行,黄=暂停/警告,红=失败)。</summary>
    public enum NavBadgeState
    {
        /// <summary>隐藏(无任务)。</summary>
        None,
        /// <summary>进行中(绿)。</summary>
        Running,
        /// <summary>暂停/警告(黄)。</summary>
        Paused,
        /// <summary>失败(红)。</summary>
        Error,
    }

    /// <summary>
    /// 导航栏 InfoBadge 静态服务:页面在提取开始/进度/完成时调用,更新左侧导航项上的数量徽标。
    /// MainWindow 注册回调(UI 线程),页面无需感知 MainWindow 实例。
    /// count 为 null 或 0 时隐藏徽标;state 决定徽标颜色(绿/黄/红)。
    /// </summary>
    public static class NavBadgeService
    {
        /// <summary>导航项 Tag → 更新回调(由 MainWindow 在 UI 线程注册)。</summary>
        public static event Action<string, int?, NavBadgeState>? BadgeChanged;

        /// <summary>更新某导航项徽标:显示剩余数量 + 状态颜色;count 为 null 或 0 时隐藏。</summary>
        public static void SetBadge(string pageTag, int? count, NavBadgeState state = NavBadgeState.Running)
        {
            BadgeChanged?.Invoke(pageTag, count, state);
        }
    }
}

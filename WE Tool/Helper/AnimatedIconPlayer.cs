using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace WE_Tool.Helper
{
    /// <summary>
    /// 删除类图标(Lottie 动画)的统一播放器:点击删除按钮时,把该按钮里的 AnimatedIcon 整段播一遍
    /// (第 0→20 帧),播完自动归位 Normal。全工程共用一份,页面里只需一行 AnimatedIconPlayer.PlayOnce(sender, "卸载")。
    /// [为什么播完要归位] AnimatedIcon 只在"状态真正变化"时才播动画,播完切回 Normal,
    /// 下一次点击才是真实的状态切换(否则会出现"点两次才动一次",全选那边踩过)。
    /// [2026-09-18 起的例外] 普通 Button 宿主(详情面板那类)由按钮模板按"按下 0→10 / 松开 10→20"驱动,
    /// 这里直接跳过 —— 否则两段之后又整段重播一遍(会看到动画演两次)。
    /// </summary>
    internal static class AnimatedIconPlayer
    {
        /// <summary>false = 所有删除类图标回到"静止图标"(不播动画);排查问题时一键关掉</summary>
        public static bool Enabled = true;

        // 每个图标实例各自的归位令牌(连点时取消上一次);弱表挂住,图标被回收后不残留
        private static readonly ConditionalWeakTable<AnimatedIcon, CancellationTokenSource> _resets = new();

        /// <summary>
        /// 在 sender(按钮/菜单项,或它内部的内容)里找到 AnimatedIcon,整段播一遍再归位。
        /// tag 只用于日志(如"卸载"/"删除全部")。
        /// </summary>
        public static void PlayOnce(object? sender, string tag)
        {
            if (!Enabled) return;
            // 普通 Button 宿主(详情面板那类):按钮模板已按“按下 0→10 / 松开 10→20”驱动了整段,
            // 这里再整段播一遍会看到动画演两次 —— 直接跳过。
            if (sender is Button and not AppBarButton) return;
            var icon = FindAnimatedIcon(sender);
            if (icon is null) return;   // 图标被收进溢出菜单/尚未实化时安静跳过

            if (_resets.TryGetValue(icon, out var prev))
            {
                prev.Cancel();
                _resets.Remove(icon);
            }
            var cts = new CancellationTokenSource();
            _resets.Add(icon, cts);

            Log.Information("[动画] {Tag}图标状态切换 → Playing(整段:第 0→20 帧)", tag);
            AnimatedIcon.SetState(icon, "Playing");
            _ = ResetAsync(icon, cts, tag);
        }

        private static async Task ResetAsync(AnimatedIcon icon, CancellationTokenSource cts, string tag)
        {
            try
            {
                await Task.Delay(340, cts.Token);   // 素材整段约 0.33 秒(20 帧 @60fps);等短一点,连点也跟得上
            }
            catch (TaskCanceledException)
            {
                return;   // 期间又点了一次,交给新的一次接管
            }
            if (cts.IsCancellationRequested) return;
            Log.Information("[动画] {Tag}图标状态归位 → Normal", tag);
            AnimatedIcon.SetState(icon, "Normal");
        }

        /// <summary>深度优先在 sender 内部找第一个 AnimatedIcon(AppBarButton.Icon 优先)。</summary>
        public static AnimatedIcon? FindAnimatedIcon(object? sender)
        {
            if (sender is AnimatedIcon direct) return direct;
            if (sender is AppBarButton abb && abb.Icon is AnimatedIcon fromIcon) return fromIcon;
            if (sender is MenuFlyoutItem mfi && mfi.Icon is AnimatedIcon fromMenu) return fromMenu;
            return sender is DependencyObject root ? FindInTree(root, 0) : null;
        }

        private static AnimatedIcon? FindInTree(DependencyObject node, int depth)
        {
            if (depth > 8) return null;   // 够深了,避免在异常树上兜圈
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is AnimatedIcon found) return found;
                var deeper = FindInTree(child, depth + 1);
                if (deeper is not null) return deeper;
            }
            return null;
        }
    }
}

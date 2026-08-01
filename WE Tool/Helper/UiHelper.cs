using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WE_Tool.Helper
{
    internal static class UiHelper
    {
        /// <summary>
        /// 重新加载可视树中所有 Image 的位图源，使 AutoPlay 设置变化对已在播放的动图立即生效
        /// （BitmapImage.AutoPlay 只影响尚未开始的动画，已在播放的需重置 UriSource 才会响应）
        /// </summary>
        public static void ReloadGifImages(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Image img && img.Source is BitmapImage bmp && bmp.UriSource != null)
                {
                    var uri = bmp.UriSource;
                    bmp.UriSource = null;
                    bmp.UriSource = uri;
                }
                ReloadGifImages(child);
            }
        }
    }
}

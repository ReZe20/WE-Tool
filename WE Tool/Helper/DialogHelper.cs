using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WE_Tool.Helper
{
    class DialogHelper
    {
        // WinUI 3 ContentDialog 默认没有入场动画(已知 regression,见 microsoft-ui-xaml#8476)。
        // 在 Popup 挂载时对对话框根元素做 Composition Opacity 淡入。
        private static void AttachFadeIn(ContentDialog dialog)
        {
            dialog.Loading += (s, _) =>
            {
                if ((s as ContentDialog)?.Parent is not Popup popup) return;
                if (popup.Child is not UIElement child) return;

                var visual = ElementCompositionPreview.GetElementVisual(child);
                visual.Opacity = 0f;
                var anim = visual.Compositor.CreateScalarKeyFrameAnimation();
                anim.Target = "Opacity";
                anim.InsertKeyFrame(0f, 0f);
                anim.InsertKeyFrame(1f, 1f,
                    visual.Compositor.CreateCubicBezierEasingFunction(
                        new System.Numerics.Vector2(0.17f, 0.67f), new System.Numerics.Vector2(0.83f, 0.67f)));
                anim.Duration = TimeSpan.FromMilliseconds(120);
                visual.StartAnimation("Opacity", anim);
            };
        }

        public static async Task ShowMessageAsync(string title, string content)
        {
            var xamlRoot = App.MainWindowInstance?.Content?.XamlRoot;

            if (xamlRoot == null) return;

            ContentDialog dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "确定",
                XamlRoot = xamlRoot,
                // 弹层不自动继承主窗口运行时主题,显式应用(见 App.ApplyPopupTheme)
                RequestedTheme = App.GetPopupTheme()
            };
            AttachFadeIn(dialog);
            await dialog.ShowAsync();
        }

        public static async Task<bool> ShowConfirmDialogAsync(string title, string content, string primaryText = "确定", string closeText = "取消")
        {
            var xamlRoot = App.MainWindowInstance?.Content?.XamlRoot;

            if (xamlRoot == null) return false;

            ContentDialog dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = primaryText,
                CloseButtonText = closeText,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
                // 弹层不自动继承主窗口运行时主题,显式应用(见 App.ApplyPopupTheme)
                RequestedTheme = App.GetPopupTheme()
            };
            AttachFadeIn(dialog);

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
    }
}

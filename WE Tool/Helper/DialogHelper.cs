using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WE_Tool.Helper
{
    class DialogHelper
    {
        private static string Text(string key) => LanguageHelper.GetResource(key);

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

            if (xamlRoot == null)
            {
                Log.Error("[弹窗] 拿不到主窗口的 XamlRoot,这句没显示出来: {Title} — {Content}", title, content);
                return;
            }

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

        // ===== 贴在按钮上的小卡(Flyout):代替居中的模态框 =====
        // 属性面板与属性窗口的按钮都在自己那一屏的边缘,模态框会把焦点从按钮拽到窗口正中,
        // 一眼要重新找刚才在点什么;小卡直接开在按钮下面,焦点几乎不挪。骨架照 Papers 详情面板那枚
        // 「卸载确认」卡:Wrap 文案 + 右下角按钮,主按钮用 AccentButtonStyle。
        // 点别处/按 Esc 由 Flyout 的 light dismiss 收掉,一律按"没同意"算。

        /// <summary>一句话 + 确定(结果提示与报错走这条)</summary>
        public static Task<bool> ShowFlyoutMessageAsync(FrameworkElement anchor, string text, string okText)
            => ShowFlyoutCardAsync(anchor, Hint(text), okText, null, null);

        /// <summary>一句问话 + 确定/取消。确定 → true,取消与 light dismiss(点别处/Esc)→ false。</summary>
        public static Task<bool> ShowFlyoutConfirmAsync(
            FrameworkElement anchor, string text, string okText, string cancelText)
            => ShowFlyoutCardAsync(anchor, Hint(text), okText, cancelText, null);

        /// <summary>单行输入 + 确定/取消。确定返回非空文本,取消与 light dismiss 返回 null。
        /// validate 在每次按键后调用,返回一句提示就显示在输入框下面(返回 null 不显示)——
        /// 重名这类"按下之前就该知道"的事走这条,而不是等保存完再补一层确认。
        /// 弹层一开就把焦点送进输入框,回车等同按确定 —— 少一次点击,也少一次视线转移。</summary>
        public static async Task<string?> ShowFlyoutInputAsync(
            FrameworkElement anchor, string text, string placeholder, string okText, string cancelText,
            Func<string, string?>? validate = null)
        {
            var box = new TextBox { PlaceholderText = placeholder };
            var note = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Visibility = Visibility.Collapsed
            };
            if (Application.Current.Resources["TextFillColorSecondaryBrush"] is Microsoft.UI.Xaml.Media.Brush secondary)
                note.Foreground = secondary;

            if (validate != null)
                box.TextChanged += (s, e) =>
                {
                    string message = validate(box.Text) ?? "";
                    note.Text = message;
                    note.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                };

            var card = new StackPanel { Spacing = 8 };
            card.Children.Add(Hint(text));
            card.Children.Add(box);
            card.Children.Add(note);

            if (!await ShowFlyoutCardAsync(anchor, card, okText, cancelText, box)) return null;
            string value = box.Text.Trim();
            return value.Length == 0 ? null : value;
        }

        private static TextBlock Hint(string text) => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };

        /// <summary>小卡的共用构建:content 在上、按钮一排右下;focus 是要吃焦点的控件,
        /// 它是 TextBox 时把回车接成"按确定"。</summary>
        private static Task<bool> ShowFlyoutCardAsync(
            FrameworkElement anchor, UIElement content, string okText, string? cancelText, UIElement? focus)
        {
            var tcs = new TaskCompletionSource<bool>();
            bool accepted = false;

            var flyout = new Flyout();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var ok = new Button
            {
                Content = okText,
                Style = Application.Current.Resources["AccentButtonStyle"] as Style
            };
            ok.Click += (s, e) =>
            {
                accepted = true;
                flyout.Hide();
            };
            buttons.Children.Add(ok);
            if (!string.IsNullOrEmpty(cancelText))
            {
                var cancel = new Button { Content = cancelText };
                cancel.Click += (s, e) => flyout.Hide();
                buttons.Children.Add(cancel);
            }

            var card = new StackPanel { Width = 300, Spacing = 10 };
            card.Children.Add(content);
            card.Children.Add(buttons);
            flyout.Content = card;

            // 弹层在独立顶层里,不继承页面主题(App.ApplyFlyoutTheme 同时支持 Flyout 与 MenuFlyout)
            flyout.Opened += App.ApplyFlyoutTheme;
            if (focus != null)
            {
                // Opened 时弹层内容未必已就位,延一帧再给焦点(直接在回调里 Focus 有时不生效)
                var target = focus;
                var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                flyout.Opened += (s, e) => queue?.TryEnqueue(() => target.Focus(FocusState.Programmatic));
            }
            if (focus is TextBox box)
                box.KeyDown += (s, e) =>
                {
                    if (e.Key != Windows.System.VirtualKey.Enter) return;
                    accepted = true;
                    flyout.Hide();
                };
            flyout.Closed += (s, e) => tcs.TrySetResult(accepted);

            flyout.ShowAt(anchor);
            return tcs.Task;
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

        /// <summary>多行 JSON 编辑框(分享 JSON):框内两个小按钮做复制/粘贴,确定返回框里的文本(可被改过),取消返回 null。
        /// 剪贴板两侧的口径都由调用方给(<c>toClipboard</c>/<c>fromClipboard</c>):WE 的分享框复制的是 Base64、
        /// 贴进来时又会把 Base64 就地还原成 JSON,这个 helper 只负责照做,不认得那是 wallpaper 的格式。
        /// 弹层挂在唤起它的那枚按钮所属的窗口上(anchor.XamlRoot),取不到时报日志而不是静默返回 ——
        /// 静默返回就是"按钮按下去没动静",现场看不出任何差别。</summary>
        public static async Task<string?> ShowJsonAsync(
            FrameworkElement anchor, string title, string hint, string json,
            string copyText, string pasteText, string applyText,
            Func<string, string> toClipboard, Func<string, string> fromClipboard)
        {
            var xamlRoot = anchor.XamlRoot ?? App.MainWindowInstance?.Content?.XamlRoot;
            if (xamlRoot == null)
            {
                Log.Error("[分享 JSON] 拿不到 XamlRoot(anchor={Anchor},主窗口={MainWindow}),弹窗没有出现",
                             anchor.GetType().Name, App.MainWindowInstance == null ? "null" : "已有");
                return null;
            }

            var box = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                Height = 260,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
            };
            // Text 必须等 AcceptsReturn 落定之后再赋:对象初始化器按书写顺序赋值,而单行 TextBox 遇到
            // 带换行的赋值是**截断**(第一个换行符之后整段丢掉),不是"把换行符去掉"——
            // 于是那份以 "{\r\n" 开头的 JSON 在框里只剩一个 "{",后面再开 AcceptsReturn 也找不回来。
            box.Text = json;
            ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);

            var status = new TextBlock { Text = "", FontSize = 12 };
            if (Application.Current.Resources["TextFillColorSecondaryBrush"] is Microsoft.UI.Xaml.Media.Brush secondary)
                status.Foreground = secondary;
            var copy = new Button { Content = copyText };
            var paste = new Button { Content = pasteText };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(copy);
            buttons.Children.Add(paste);

            copy.Click += (s, e) =>
            {
                string text = toClipboard(box.Text);
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                status.Text = string.Format(Text("PropertyPreset_CopiedFmt.Text"), text.Length);
            };
            paste.Click += async (s, e) =>
            {
                try
                {
                    var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                    if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return;
                    string text = await content.GetTextAsync();
                    if (!string.IsNullOrEmpty(text)) box.Text = text;
                }
                catch (Exception ex)
                {
                    status.Text = ex.Message;
                }
            };

            // 直接 Ctrl+V 贴进框里也走同一条:内容一旦能还原成 JSON 就换掉它(和 WE 分享框的 $watch 一致)。
            // 换内容会再触发一次 TextChanged,所以用标志位挡住自己引起的递归。
            bool normalizing = false;
            box.TextChanged += (s, e) =>
            {
                if (normalizing) return;
                string before = box.Text;
                string normalized = fromClipboard(before);
                if (normalized == before) return;
                normalizing = true;
                box.Text = normalized;
                normalizing = false;
                box.SelectionStart = normalized.Length;
                status.Text = string.Format(Text("PropertyPreset_DecodedFmt.Text"), normalized.Length);
                Log.Information("[分享 JSON] 框内内容还原: {From} → {To} 字符", before.Length, normalized.Length);
            };

            var panel = new StackPanel { Spacing = 8, MinWidth = 460 };
            if (hint.Length > 0)
            {
                var hintText = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap };
                if (Application.Current.Resources["TextFillColorSecondaryBrush"] is Microsoft.UI.Xaml.Media.Brush hintBrush)
                    hintText.Foreground = hintBrush;
                panel.Children.Add(hintText);
            }
            panel.Children.Add(box);
            panel.Children.Add(buttons);
            panel.Children.Add(status);

            ContentDialog dialog = new ContentDialog
            {
                Title = title,
                Content = panel,
                PrimaryButtonText = applyText,
                CloseButtonText = "取消",
                // 默认按钮给「应用」:这条路是按下去就是要套值,取消只是不想要了。
                // 焦点在框里时 Enter 仍是换行(那是 AcceptsReturn 的多行框),这条只在焦点落在按钮上时接管。
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
                RequestedTheme = App.GetPopupTheme()
            };
            AttachFadeIn(dialog);

            Log.Information("[分享 JSON] 弹窗打开: 初值 {Length} 字符", box.Text.Length);
            var result = await dialog.ShowAsync();
            // 关闭时把内容整串留下(它最多几百字节):"框里剩几个字符"这种事,看一眼文本比数长度确定得多
            Log.Information("[分享 JSON] 弹窗关闭: {Result},框内 {Length} 字符: {Text}",
                result, box.Text.Length, box.Text.Length > 400 ? box.Text[..400] + "…" : box.Text);
            return result == ContentDialogResult.Primary ? box.Text : null;
        }
    }
}

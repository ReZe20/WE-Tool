using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Linq;
using WE_Tool.Models;
using Windows.UI;

namespace WE_Tool.Helper
{
    /// <summary>
    /// 壁纸属性行的公共渲染:属性窗口(子进程)与 Papers 右侧「属性面板」共用同一份实现 ——
    /// 标签文字(链接/HTTP 图片/文字色/标题字号)、分组标题、分组容器、编辑控件都在这里,两处不再各写一份。
    /// 编辑控件直接改 <see cref="WallpaperProperty"/> 上的值,写盘由调用方收齐后交给
    /// <see cref="WallpaperPropertyWriter"/>。全部为 UI 线程调用:DependencyObject 不能离开创建它的
    /// XAML 运行时,包括 SolidColorBrush、Application.Current.Resources 取主题资源这些也要在 UI 线程。
    /// </summary>
    internal static class WallpaperPropertyRowBuilder
    {
        /// <summary>只读值列的最大宽度:详情面板整宽约 310px,限宽让长值（路径、文本框内容）换行，
        /// 而不是把左列标签挤到没有宽度。</summary>
        private const double ReadOnlyValueMaxWidth = 140;

        /// <summary>构建文字内容:纯文本 → TextBlock;含链接 → StackPanel + HyperlinkButton(可点击跳转);
        /// 含 &lt;font color&gt; 应用文字色;含 &lt;img&gt; 渲染 HTTP 图片(外层 &lt;a href&gt; 时整图可点击)。
        /// 图片段不渲染其文本;加载失败隐藏。</summary>
        public static FrameworkElement BuildTextContent(WallpaperProperty prop)
        {
            bool hasLink = prop.LinkSegments.Any(s => s.Url != null);
            bool hasImage = prop.ImageSegments.Count > 0;
            Brush? textBrush = prop.TextColor is Color c ? new SolidColorBrush(c) : null;

            // 无链接无图片:单 TextBlock(带颜色)
            if (!hasLink && !hasImage)
            {
                var tb = new TextBlock
                {
                    Text = prop.DisplayText,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = prop.TextFontSize,
                    FontWeight = prop.TextFontWeight,
                    TextAlignment = prop.TextAlignmentValue
                };
                if (textBrush != null) tb.Foreground = textBrush;
                return tb;
            }

            var sp = new StackPanel { Spacing = 2 };

            // 文字段(链接 → HyperlinkButton;普通 → TextBlock;均应用文字色)
            foreach (var (text, url) in prop.LinkSegments)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (url == null)
                {
                    var tb = new TextBlock
                    {
                        Text = text,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = prop.TextFontSize,
                        FontWeight = prop.TextFontWeight
                    };
                    if (textBrush != null) tb.Foreground = textBrush;
                    sp.Children.Add(tb);
                }
                else
                {
                    try
                    {
                        var hb = new HyperlinkButton
                        {
                            Content = text,
                            NavigateUri = new Uri(url),
                            Style = (Style)Application.Current.Resources["ExternalLinkButtonStyle"]
                        };
                        if (textBrush != null) hb.Foreground = textBrush;
                        sp.Children.Add(hb);
                    }
                    catch
                    {
                        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
                        if (textBrush != null) tb.Foreground = textBrush;
                        sp.Children.Add(tb);
                    }
                }
            }

            // 图片段(<img src>,HTTP 加载;外层 <a href> 时整图可点击;加载失败隐藏)
            foreach (var (src, link, width, height) in prop.ImageSegments)
            {
                try
                {
                    var image = new Microsoft.UI.Xaml.Controls.Image
                    {
                        Source = new BitmapImage(new Uri(src)),
                        Stretch = Stretch.Uniform,
                        MaxWidth = 200,
                        MaxHeight = 200,
                        Margin = new Thickness(0, 4, 0, 4)
                    };
                    if (width.HasValue) image.Width = Math.Min(width.Value, 200);
                    if (height.HasValue) image.Height = Math.Min(height.Value, 200);
                    image.ImageFailed += (s, e) => image.Visibility = Visibility.Collapsed;

                    if (link != null)
                    {
                        try
                        {
                            var hb = new HyperlinkButton
                            {
                                NavigateUri = new Uri(link),
                                Style = (Style)Application.Current.Resources["ExternalLinkButtonStyle"],
                                Content = image
                            };
                            sp.Children.Add(hb);
                        }
                        catch { sp.Children.Add(image); }
                    }
                    else
                    {
                        sp.Children.Add(image);
                    }
                }
                catch { /* 无效图片 URL → 跳过 */ }
            }
            return sp;
        }

        /// <summary>构建分组标题(分隔线 + 粗体文字)。分组标题是纯文本组件(无链接),直接 TextBlock。
        /// 标题为空 = 纯分隔线(WE 内置块与作者属性之间那条):不建那行空文字(空 TextBlock 照样占一行高),
        /// 上下留对称的 6,否则线底下会多出一段没人解释的空白。</summary>
        public static FrameworkElement BuildGroupHeader(WallpaperProperty prop)
        {
            bool bare = string.IsNullOrEmpty(prop.DisplayText);
            var sp = new StackPanel();
            sp.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Height = 1,
                Fill = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                Margin = new Thickness(0, 6, 0, bare ? 6 : 10)
            });
            if (bare) return sp;
            sp.Children.Add(new TextBlock
            {
                Text = prop.DisplayText,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = prop.TextAlignmentValue
            });
            return sp;
        }

        /// <summary>只读值文本(右列/值列),取解析器已格式化好的 DisplayValue;空值返回 null(行只剩标签)。
        /// 没有编辑控件的类型(如 usershortcut)靠它显示值 —— 原 XAML 的 ReadOnly 分支在代码构建时漏了。</summary>
        public static FrameworkElement? BuildReadOnlyValue(WallpaperProperty prop)
        {
            string value = prop.DisplayValue;
            if (string.IsNullOrEmpty(value)) return null;
            return new TextBlock
            {
                Text = value,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Right,
                MaxWidth = ReadOnlyValueMaxWidth,
                FontSize = prop.TextFontSize,
                VerticalAlignment = VerticalAlignment.Top
            };
        }

        /// <summary>构建单行:按类型分发(分组标题/分组 Expander/可编辑行),并按条件把行藏起来</summary>
        public static FrameworkElement BuildPropertyRow(WallpaperProperty prop)
        {
            var row = prop.IsGroupHeader ? BuildGroupHeader(prop)
                    : prop.IsGroup ? BuildGroupExpander(prop)
                    : BuildEditableRow(prop);
            // 条件行:控制它的那条 bool 还没勾上就不占位(每轮读取新建实例,一行只属于一个面板)
            prop.RowElement = row;
            if (prop.Gate != null) row.Visibility = IsOn(prop.Gate) ? Visibility.Visible : Visibility.Collapsed;
            return row;
        }

        /// <summary>bool 行算不算「开着」:未定态(WE 没记录、又不知默认值)按关处理</summary>
        private static bool IsOn(WallpaperProperty prop) => !prop.IsUnset && prop.BoolValue;

        /// <summary>开关变化后重刷受控行的显隐</summary>
        private static void ApplyGate(WallpaperProperty gate)
        {
            bool on = IsOn(gate);
            foreach (var dependent in gate.Gated)
                if (dependent.RowElement != null)
                    dependent.RowElement.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>构建单个属性行(Grid 两列:左标签 + 右控件)</summary>
        private static FrameworkElement BuildEditableRow(WallpaperProperty prop)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4), ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = BuildTextContent(prop);
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            // 右列:编辑控件;只读类型没有控件,改挂只读值
            var editor = BuildEditor(prop);
            if (editor != null)
            {
                Grid.SetColumn(editor, 1);
                grid.Children.Add(editor);
            }
            else
            {
                var value = BuildReadOnlyValue(prop);
                if (value != null)
                {
                    Grid.SetColumn(value, 1);
                    grid.Children.Add(value);
                }
            }
            return grid;
        }

        /// <summary>按类型构建右列编辑控件;只读类型返回 null(无控件)。控件直接改 prop 上的值,
        /// 不通知任何人 —— 写盘由调用方收齐 IsEditable 的属性后统一做。</summary>
        private static FrameworkElement? BuildEditor(WallpaperProperty prop)
        {
            switch (prop.Type)
            {
                case "bool":
                {
                    var cb = new CheckBox
                    {
                        // WE 内置属性没记录又不知默认值时显示未定态(三态外观,单击即成显式值)
                        IsChecked = prop.IsUnset ? null : prop.BoolValue,
                        MinWidth = 0,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    cb.Checked += (s, e) => { prop.BoolValue = true; ApplyGate(prop); };
                    cb.Unchecked += (s, e) => { prop.BoolValue = false; ApplyGate(prop); };
                    return cb;
                }
                case "slider":
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                    var slider = new Slider
                    {
                        Value = prop.SliderValue,
                        Minimum = prop.SliderMin,
                        Maximum = prop.SliderMax,
                        StepFrequency = prop.SliderStep,
                        Width = 100,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var valueText = new TextBlock
                    {
                        Text = prop.SliderValueText,
                        MinWidth = 40,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    slider.ValueChanged += (s, e) =>
                    {
                        prop.SliderValue = slider.Value;
                        valueText.Text = prop.SliderValueText;
                    };
                    sp.Children.Add(slider);
                    sp.Children.Add(valueText);
                    return sp;
                }
                case "combo":
                {
                    var button = new DropDownButton
                    {
                        MinWidth = 140,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var display = new TextBlock { Text = prop.ComboDisplayText };
                    button.Content = display;
                    button.Click += (s, e) => ShowComboMenu(button, prop, display);
                    return button;
                }
                case "color":
                {
                    var button = new Button { Padding = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Center };
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    var swatch = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 14, Height = 14, Fill = prop.ColorBrush, VerticalAlignment = VerticalAlignment.Center };
                    var hex = new TextBlock { Text = ColorText(prop), VerticalAlignment = VerticalAlignment.Center };
                    sp.Children.Add(swatch);
                    sp.Children.Add(hex);
                    button.Content = sp;
                    var picker = new ColorPicker
                    {
                        Color = prop.ColorValue,
                        IsAlphaEnabled = false,
                        IsColorPreviewVisible = false,
                        IsColorSpectrumVisible = true,
                        IsColorSliderVisible = true,
                        IsHexInputVisible = true
                    };
                    var flyout = new Flyout { Content = picker };
                    flyout.Opened += App.ApplyFlyoutTheme;
                    picker.ColorChanged += (s, e) =>
                    {
                        prop.ColorValue = picker.Color;
                        swatch.Fill = prop.ColorBrush;
                        hex.Text = ColorText(prop);
                    };
                    button.Flyout = flyout;
                    return button;
                }
                case "textinput":
                {
                    var tb = new TextBox
                    {
                        Text = prop.TextValue,
                        TextWrapping = TextWrapping.Wrap,
                        Width = 180,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    tb.TextChanged += (s, e) => prop.TextValue = tb.Text;
                    return tb;
                }
                case "scenetexture":
                {
                    // PickFileCommand 内部走 PickerService:它拿 App.MainWindowInstance 当 owner,
                    // 所以主进程(Papers)与副窗口里点都能弹出选择器
                    var button = new Button
                    {
                        Command = prop.PickFileCommand,
                        Content = prop.FilePathDisplay,
                        MaxWidth = 180,
                        Padding = new Thickness(10, 4, 10, 4),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    return button;
                }
                default:
                    return null; // 只读类型:无编辑控件,值由 BuildEditableRow 回退到只读值
            }
        }

        /// <summary>颜色行的值文字:未定态(WE 内置属性没记录)显示「默认」占位,否则显示色值</summary>
        private static string ColorText(WallpaperProperty prop)
            => prop.IsUnset && prop.UnsetText.Length > 0 ? prop.UnsetText : prop.ColorHexText;

        /// <summary>combo 下拉:DropDownButton + 动态 MenuFlyout(弹层主题显式应用)</summary>
        private static void ShowComboMenu(DropDownButton button, WallpaperProperty prop, TextBlock display)
        {
            var flyout = new MenuFlyout();
            string group = $"Combo_{prop.Key}";
            for (int i = 0; i < prop.Options.Count; i++)
            {
                int index = i; // 闭包捕获
                var item = new RadioMenuFlyoutItem
                {
                    Text = prop.Options[i].Label,
                    IsChecked = i == prop.ComboIndex,
                    GroupName = group
                };
                item.Click += (s, e2) =>
                {
                    prop.ComboIndex = index;
                    display.Text = prop.ComboDisplayText;
                };
                flyout.Items.Add(item);
            }
            flyout.Opened += App.ApplyFlyoutTheme;
            button.Flyout = flyout;
            flyout.ShowAt(button);
        }

        /// <summary>构建分组 Expander(header = 文字/链接;内容 = 子属性行递归构建)</summary>
        private static FrameworkElement BuildGroupExpander(WallpaperProperty prop)
        {
            var expander = new Expander
            {
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 4, 0, 4),
                Header = BuildTextContent(prop)
            };
            var items = new ItemsControl { IsTabStop = false };
            foreach (var child in prop.Children)
                items.Items.Add(BuildPropertyRow(child));
            expander.Content = items;
            return expander;
        }
    }
}

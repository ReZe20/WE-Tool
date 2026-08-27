using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;
using WE_Tool.Service;
using Windows.UI;
using Windows.UI.Text;

namespace WE_Tool.Models
{
    /// <summary>
    /// 壁纸属性面板的单行数据：数据源为壁纸目录 project.json 的 general.properties。
    /// 可编辑类型（bool/slider/combo/color/textinput）的值在对应控件上双向绑定，
    /// 保存时一次性写回 project.json（文本级定点替换，只动 value token）。
    /// 纯文本组件（text/group）可能带超链接与样式；group 类型渲染为可折叠 Expander 并吞并后续属性；
    /// condition 字段控制行可见性（由 ViewModel 建立属性间监听）。
    /// </summary>
    public partial class WallpaperProperty : INotifyPropertyChanged
    {
        // === 元数据（解析时固定） ===
        public string Key { get; set; } = "";

        /// <summary>控件类型：bool/slider/combo/color/textinput/scenetexture/text/group（未知类型原样保留）</summary>
        public string Type { get; set; } = "";

        public string Text { get; set; } = "";

        /// <summary>剥除 HTML 后的显示标签；分组标题的 text 以 &lt;hr&gt; 开头</summary>
        public string DisplayText { get; set; } = "";

        /// <summary>只读类型的显示值（scenetexture 文件名/未知类型原文）</summary>
        public string DisplayValue { get; set; } = "";

        /// <summary>是否为分组标题（分隔线 + 粗体，无编辑控件；仅纯文本组件判定）</summary>
        public bool IsGroupHeader { get; set; }

        /// <summary>纯文本组件的标题样式（text 含 &lt;h1&gt;~&lt;h6&gt; 或 &lt;big&gt;）</summary>
        public bool IsTitle { get; set; }

        /// <summary>纯文本组件粗体（text 含 &lt;b&gt;/&lt;strong&gt;）</summary>
        public bool IsBold { get; set; }

        /// <summary>纯文本组件居中（text 含 &lt;center&gt;）</summary>
        public bool IsCentered { get; set; }

        /// <summary>纯文本组件字号：标题 16，普通 13</summary>
        public double TextFontSize => IsTitle ? 16 : 13;

        /// <summary>纯文本组件字重：标题/粗体 SemiBold，普通 Normal</summary>
        public FontWeight TextFontWeight => IsTitle || IsBold ? FontWeights.SemiBold : FontWeights.Normal;

        /// <summary>纯文本组件对齐：含 &lt;center&gt; 时居中</summary>
        public TextAlignment TextAlignmentValue => IsCentered ? TextAlignment.Center : TextAlignment.Left;

        // === group 类型（Border 包裹） ===
        /// <summary>是否为分组（type=group 且有子属性）；解析时归组后判定</summary>
        public bool IsGroup { get; set; }

        /// <summary>组内属性（归组时按 order 顺序吞并，直到下一个 group）</summary>
        public List<WallpaperProperty> Children { get; } = [];

        public Visibility GroupBorderVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;

        // === 编辑配置（解析时固定，仅解析器写入） ===
        public double SliderMin { get; set; }
        public double SliderMax { get; set; } = 100;
        public double SliderStep { get; set; } = 1;

        /// <summary>slider 小数位数；-1 = 未定义（写回时保留原精度）</summary>
        public int Precision { get; set; } = -1;

        public IReadOnlyList<ComboOption> Options { get; set; } = [];

        // === 可编辑值（x:Bind TwoWay） ===
        private bool _boolValue;
        public bool BoolValue
        {
            get => _boolValue;
            set
            {
                if (_boolValue != value)
                {
                    _boolValue = value;
                    OnPropertyChanged();
                }
            }
        }

        private double _sliderValue;
        public double SliderValue
        {
            get => _sliderValue;
            set
            {
                if (_sliderValue != value)
                {
                    _sliderValue = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SliderValueText));
                }
            }
        }

        /// <summary>slider 当前值显示文本：有精度定义按精度保留小数，否则最多 3 位（去尾零）</summary>
        public string SliderValueText
            => Precision >= 0 ? SliderValue.ToString($"F{Precision}") : SliderValue.ToString("0.###");

        private int _comboIndex = -1;
        public int ComboIndex
        {
            get => _comboIndex;
            set
            {
                if (_comboIndex != value)
                {
                    _comboIndex = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ComboValue));
                    OnPropertyChanged(nameof(ComboDisplayText));
                }
            }
        }

        /// <summary>当前下拉选项的 value（写回用）；未匹配到选项时为空</summary>
        public string ComboValue
            => ComboIndex >= 0 && ComboIndex < Options.Count ? Options[ComboIndex].Value : "";

        /// <summary>下拉按钮显示文本：选中项 label；匹配失败显示原值（解析器已格式化到 DisplayValue）</summary>
        public string ComboDisplayText
        {
            get
            {
                if (ComboIndex >= 0 && ComboIndex < Options.Count)
                    return Options[ComboIndex].Label;
                return string.IsNullOrEmpty(DisplayValue) ? ComboValue : DisplayValue;
            }
        }

        private Color _colorValue = Color.FromArgb(255, 255, 255, 255);
        public Color ColorValue
        {
            get => _colorValue;
            set
            {
                if (_colorValue != value)
                {
                    _colorValue = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ColorBrush));
                    OnPropertyChanged(nameof(ColorHexText));
                }
            }
        }

        private string _textValue = "";
        public string TextValue
        {
            get => _textValue;
            set
            {
                if (_textValue != value)
                {
                    _textValue = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FilePathDisplay));
                }
            }
        }

        // === 文件路径选择（scenetexture：点击按钮选图片，写回路径字符串） ===
        /// <summary>按钮显示文本：当前文件名，未选择时提示</summary>
        public string FilePathDisplay
        {
            get
            {
                if (string.IsNullOrEmpty(TextValue)) return "选择图片…";
                string name = Path.GetFileName(TextValue.TrimEnd('\\', '/'));
                return name.Length > 26 ? name[..26] + "…" : name;
            }
        }

        private IAsyncRelayCommand? _pickFileCommand;
        /// <summary>打开文件选择器并写回所选路径</summary>
        public IAsyncRelayCommand PickFileCommand
            => _pickFileCommand ??= new AsyncRelayCommand(PickFileAsync);

        private async Task PickFileAsync()
        {
            string? path = await new PickerService().PickImageAsync();
            if (!string.IsNullOrEmpty(path))
                TextValue = path;
        }

        // === 计算属性 ===
        /// <summary>可写回的类型（bool/slider/combo/color/textinput/scenetexture）；未知类型仅只读</summary>
        public bool IsEditable
            => !IsGroupHeader && !IsGroup && (Type is "bool" or "slider" or "combo" or "color" or "textinput" or "scenetexture");

        public Visibility GroupHeaderVisibility
            => IsGroupHeader ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>属性行可见性：分组标题/Border 分组由各自分支显示，普通行始终显示</summary>
        public Visibility RowVisibility
            => !IsGroupHeader && !IsGroup ? Visibility.Visible : Visibility.Collapsed;

        public Visibility BoolVisibility => Type == "bool" ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SliderVisibility => Type == "slider" ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ComboVisibility => Type == "combo" ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ColorVisibility => Type == "color" ? Visibility.Visible : Visibility.Collapsed;
        public Visibility TextInputVisibility => Type == "textinput" ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FilePathVisibility => Type == "scenetexture" ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ReadOnlyVisibility
            => IsGroupHeader || IsGroup || IsEditable ? Visibility.Collapsed : Visibility.Visible;

        public SolidColorBrush ColorBrush => new(ColorValue);
        public string ColorHexText => $"#{ColorValue.R:X2}{ColorValue.G:X2}{ColorValue.B:X2}";

        // === 链接感知分段(纯文本组件:<a href> 与裸 URL 拆成可点击段,懒构建) ===
        private static readonly Regex LinkAnchorRegex = new(
            @"<a\s+[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex LinkUrlRegex = new(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase);
        private static readonly Regex LinkTagRegex = new(@"<[^>]+>");
        private static readonly Regex LinkBrRegex = new(@"<br\s*/?>", RegexOptions.IgnoreCase);
        private static readonly Regex AnchorOpenRegex = new(@"<a\b[^>]*>", RegexOptions.IgnoreCase);
        private static readonly Regex AnchorCloseRegex = new(@"</a\s*>", RegexOptions.IgnoreCase);

        /// <summary>补全未闭合的 &lt;a&gt; 标签:部分壁纸作者漏写 &lt;/a&gt;,导致锚点正则不匹配、
        /// 链接被拆成"裸URL+纯文本"错乱渲染。在文本末尾补足缺失数量的闭合标签后再解析。</summary>
        private string NormalizeAnchorTags()
        {
            int open = AnchorOpenRegex.Matches(Text).Count;
            int close = AnchorCloseRegex.Matches(Text).Count;
            if (open <= close) return Text;
            var sb = new System.Text.StringBuilder(Text);
            for (int i = close; i < open; i++) sb.Append("</a>");
            return sb.ToString();
        }

        private IReadOnlyList<(string Text, string? Url)>? _linkSegments;

        /// <summary>链接感知分段:Text=显示文本,Url=null 为普通文本(有链接才需要接管 Inlines 渲染)</summary>
        public IReadOnlyList<(string Text, string? Url)> LinkSegments
            => _linkSegments ??= BuildLinkSegments();

        private IReadOnlyList<(string Text, string? Url)> BuildLinkSegments()
        {
            string html = NormalizeAnchorTags();
            var result = new List<(string, string?)>();
            int pos = 0;
            foreach (Match m in LinkAnchorRegex.Matches(html))
            {
                AddPlainSegments(result, html.Substring(pos, m.Index - pos));
                string inner = StripLinkHtml(m.Groups[2].Value);
                string url = m.Groups[1].Value;
                if (inner.Length > 0)
                    result.Add((inner, url));
                pos = m.Index + m.Length;
            }
            AddPlainSegments(result, html.Substring(pos));
            return result;
        }

        private void AddPlainSegments(List<(string, string?)> result, string text)
        {
            // 先剥掉 img 标签:其 src URL 是图片地址,不作为链接文本显示(图片由 ImageSegments 单独渲染)
            text = ImageTagRegex.Replace(text, "");
            int pos = 0;
            foreach (Match m in LinkUrlRegex.Matches(text))
            {
                if (m.Index > pos)
                {
                    string plain = StripLinkHtml(text.Substring(pos, m.Index - pos));
                    if (plain.Length > 0) result.Add((plain, null));
                }
                result.Add((m.Value, m.Value));
                pos = m.Index + m.Length;
            }
            string rest = StripLinkHtml(text.Substring(pos));
            if (rest.Length > 0) result.Add((rest, null));
        }

        private static string StripLinkHtml(string text)
        {
            // <br/> → \n:与解析器 StripHtml 同规;后续按 \n 拆行渲染
            string t = LinkBrRegex.Replace(text, "\n");
            t = LinkTagRegex.Replace(t, "");
            t = t.Replace("&amp;", "&")
                 .Replace("&lt;", "<")
                 .Replace("&gt;", ">")
                 .Replace("&quot;", "\"")
                 .Replace("&#39;", "'")
                 .Replace("&nbsp;", " ");
            // 只去首尾空白、保留段首/段尾的换行符——<br/> 位于锚点或文本边缘时,
            // 换行承担着"与下一段分行"的职责,Trim() 会把它剥掉导致两段挤回一行
            return t.Trim(' ', '\t');
        }

        // === 图片段(纯文本组件中的 <img src>,HTTP 加载显示) ===
        private static readonly Regex ImageTagRegex = new(
            @"<img\s+[^>]*src\s*=\s*(?:[""']([^""']+)[""']|([^\s>""']+))[^>]*>",
            RegexOptions.IgnoreCase);
        private static readonly Regex ImageSizeRegex = new(
            @"\b(width|height)\s*=\s*[""']?(\d+)", RegexOptions.IgnoreCase);

        private IReadOnlyList<(string Src, string? Link, int? Width, int? Height)>? _imageSegments;

        /// <summary>图片段:Src=HTTP 图片地址,Link=外层 &lt;a href&gt;(整图可点击),Width/Height=标签尺寸</summary>
        public IReadOnlyList<(string Src, string? Link, int? Width, int? Height)> ImageSegments
            => _imageSegments ??= BuildImageSegments();

        private IReadOnlyList<(string Src, string? Link, int? Width, int? Height)> BuildImageSegments()
        {
            string html = NormalizeAnchorTags();
            var result = new List<(string, string?, int?, int?)>();
            var anchors = LinkAnchorRegex.Matches(html);
            foreach (Match im in ImageTagRegex.Matches(html))
            {
                string src = im.Groups[1].Value.Length > 0 ? im.Groups[1].Value : im.Groups[2].Value;
                if (src.Length == 0) continue;

                // img 是否被 <a href> 包裹 → 整图可点击跳转
                string? link = null;
                foreach (Match am in anchors)
                {
                    if (im.Index > am.Index && im.Index < am.Index + am.Length)
                    {
                        link = am.Groups[1].Value;
                        break;
                    }
                }

                int? width = null, height = null;
                foreach (Match sm in ImageSizeRegex.Matches(im.Value))
                {
                    int val = int.Parse(sm.Groups[2].Value);
                    if (sm.Groups[1].Value == "width") width = val;
                    else height = val;
                }

                result.Add((src, link, width, height));
            }
            return result;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

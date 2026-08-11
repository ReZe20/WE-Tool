using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using WE_Tool.Models;
using Windows.UI;

namespace WE_Tool.Helper
{
    /// <summary>
    /// 解析壁纸目录 project.json 的 general.properties 为属性面板数据。
    /// 选中壁纸时懒解析 + 按文件夹路径缓存（project.json 修改时间变化时重新解析）。
    /// 仅 Scene 类壁纸有 general.properties；视频/网页/未发布项目通常没有（返回空列表）。
    /// </summary>
    internal static class WallpaperPropertyParser
    {
        private static readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, List<WallpaperProperty> Properties)> Cache = new();

        /// <summary>解析壁纸文件夹下的 project.json；文件缺失/无属性/解析失败一律返回空列表（调用方显示占位提示）</summary>
        public static List<WallpaperProperty> Parse(string folderPath)
        {
            try
            {
                string path = Path.Combine(folderPath, "project.json");
                if (!File.Exists(path)) return [];

                var lastWrite = File.GetLastWriteTimeUtc(path);
                if (Cache.TryGetValue(folderPath, out var cached) && cached.LastWriteUtc == lastWrite)
                    return cached.Properties;

                var props = ParseInternal(path);
                Cache[folderPath] = (lastWrite, props);
                return props;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "解析壁纸属性失败: {Folder}", folderPath);
                return [];
            }
        }

        private static List<WallpaperProperty> ParseInternal(string projectJsonPath)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(projectJsonPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return [];
            if (!root.TryGetProperty("general", out var general) || general.ValueKind != JsonValueKind.Object) return [];
            if (!general.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) return [];

            var rows = new List<(int Order, int Index, WallpaperProperty Prop)>();
            foreach (var kv in properties.EnumerateObject())
            {
                if (kv.Value.ValueKind != JsonValueKind.Object) continue;
                var obj = kv.Value;

                string type = GetString(obj, "type") ?? "";
                string text = GetString(obj, "text") ?? kv.Name;

                // text/group = 纯文本组件:剥标签后无文字(如纯 <img> 行)直接跳过,不占面板空间;
                // 例外:含 <img> 的保留(标签区渲染 HTTP 图片)
                bool isTextLike = type == "text" || type == "group";
                bool hasImage = text.Contains("<img", StringComparison.OrdinalIgnoreCase);
                // 分组标题仅对纯文本组件判定；可编辑类型（如 bool）text 里的 <hr> 只是装饰
                bool isGroupHeader = isTextLike && text.StartsWith("<hr", StringComparison.OrdinalIgnoreCase);
                string displayText = ResolveLabel(text);
                if (isTextLike && !isGroupHeader && string.IsNullOrWhiteSpace(displayText) && !hasImage)
                    continue;

                var prop = new WallpaperProperty
                {
                    Key = kv.Name,
                    Type = type,
                    Text = text,
                    DisplayText = displayText,
                    DisplayValue = isGroupHeader ? "" : FormatValue(type, obj),
                    IsGroupHeader = isGroupHeader,
                    // 纯文本组件可能带 HTML 样式标签：<h1>~<h6>/<big> 标题字号、<b> 粗体、<center> 居中、<font color> 文字色
                    IsTitle = text.Contains("<h1", StringComparison.OrdinalIgnoreCase)
                           || text.Contains("<h2", StringComparison.OrdinalIgnoreCase)
                           || text.Contains("<h3", StringComparison.OrdinalIgnoreCase)
                           || text.Contains("<h4", StringComparison.OrdinalIgnoreCase)
                           || text.Contains("<h5", StringComparison.OrdinalIgnoreCase)
                           || text.Contains("<h6", StringComparison.OrdinalIgnoreCase)
                           || text.Contains("<big", StringComparison.OrdinalIgnoreCase),
                    IsBold = text.Contains("<b>", StringComparison.OrdinalIgnoreCase)
                          || text.Contains("<b ", StringComparison.OrdinalIgnoreCase)
                          || text.Contains("<strong", StringComparison.OrdinalIgnoreCase),
                    IsCentered = text.Contains("<center", StringComparison.OrdinalIgnoreCase),
                    IsGroup = type == "group" && !isGroupHeader
                };
                LoadEditableValue(prop, obj);

                rows.Add((
                    GetInt(obj, "order"),
                    GetInt(obj, "index"),
                    prop));
            }

            // WE 按 order 排序（100 起），无 order 时按 index 兜底，再按原始 JSON 顺序；
            // 排序后归组：group 类型的属性吞并其后（直到下一个 group）的所有属性作为 Children
            var sorted = rows
                .Select((r, i) => (r.Order, Index: r.Index == int.MaxValue ? i : r.Index, r.Prop))
                .OrderBy(r => r.Order)
                .ThenBy(r => r.Index)
                .Select(r => r.Prop)
                .ToList();

            var result = new List<WallpaperProperty>();
            WallpaperProperty? currentGroup = null;
            foreach (var p in sorted)
            {
                if (p.IsGroup)
                {
                    currentGroup = p;
                    result.Add(p);
                }
                else if (currentGroup is not null)
                {
                    currentGroup.Children.Add(p);
                }
                else
                {
                    result.Add(p);
                }
            }

            // group 无子属性 → 降级为普通文本行（空 Expander 无意义）
            foreach (var g in result.Where(g => g.IsGroup && g.Children.Count == 0))
                g.IsGroup = false;

            return result;
        }

        /// <summary>显示标签：剥 HTML 标签（&lt;br&gt;→换行），ui_ 本地化 key 先查已知映射，其余显示原文</summary>
        private static string ResolveLabel(string text)
        {
            if (text.StartsWith("ui_", StringComparison.Ordinal))
            {
                return KnownUiKeys.TryGetValue(text, out var mapped) ? mapped : text;
            }
            return StripHtml(text);
        }

        private static readonly Regex BrRegex = new(@"<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);

        private static string StripHtml(string text)
        {
            string t = BrRegex.Replace(text, "\n");
            t = HtmlTagRegex.Replace(t, "");
            t = t.Replace("&amp;", "&")
                 .Replace("&lt;", "<")
                 .Replace("&gt;", ">")
                 .Replace("&quot;", "\"")
                 .Replace("&#39;", "'")
                 .Replace("&nbsp;", " ");
            return t.Trim();
        }

        /// <summary>
        /// 当前值格式化：bool→true/false；slider→原数字文本；combo→选项 label（找不到用值本身）；
        /// color→"r g b" 浮点串转 #RRGGBB；scenetexture→文件名（空值→"—"）；未知类型→字符串原样/JSON 原文。
        /// </summary>
        private static string FormatValue(string type, JsonElement obj)
        {
            if (!obj.TryGetProperty("value", out var value)) return "";
            if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined) return "";

            switch (type)
            {
                case "bool":
                    return value.ValueKind == JsonValueKind.True ? "true" : "false";
                case "slider":
                    return value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.ToString();
                case "combo":
                    {
                        string current = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
                        if (obj.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var opt in options.EnumerateArray())
                            {
                                if (opt.TryGetProperty("value", out var optVal)
                                    && optVal.ValueKind == JsonValueKind.String
                                    && optVal.GetString() == current
                                    && opt.TryGetProperty("label", out var optLabel))
                                {
                                    return optLabel.GetString() ?? current;
                                }
                            }
                        }
                        return current;
                    }
                case "color":
                    {
                        string raw = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
                        string[] parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 3
                            && parts.All(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                        {
                            int ToByte(string s) => Math.Clamp(
                                (int)Math.Round(float.Parse(s, CultureInfo.InvariantCulture) * 255f), 0, 255);
                            return $"#{ToByte(parts[0]):X2}{ToByte(parts[1]):X2}{ToByte(parts[2]):X2}";
                        }
                        return raw;
                    }
                case "scenetexture":
                    {
                        string raw = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
                        return string.IsNullOrEmpty(raw) ? "—" : Path.GetFileName(raw);
                    }
                default:
                    return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
            }
        }

        private static string? GetString(JsonElement obj, string name)
        {
            return obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }

        private static int GetInt(JsonElement obj, string name)
        {
            if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number)
                return el.TryGetInt32(out int v) ? v : int.MaxValue;
            return int.MaxValue;
        }

        private static double GetDouble(JsonElement obj, string name, double fallback)
        {
            if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number)
                return el.TryGetDouble(out double v) ? v : fallback;
            return fallback;
        }

        /// <summary>按类型把 project.json 的 value 载入可编辑值（未定义/解析失败保持默认）</summary>
        private static void LoadEditableValue(WallpaperProperty prop, JsonElement obj)
        {
            if (prop.IsGroupHeader) return;

            switch (prop.Type)
            {
                case "bool":
                    prop.BoolValue = obj.TryGetProperty("value", out var bv) && bv.ValueKind == JsonValueKind.True;
                    break;
                case "slider":
                    prop.SliderMin = GetDouble(obj, "min", 0);
                    prop.SliderMax = GetDouble(obj, "max", 100);
                    prop.SliderStep = GetDouble(obj, "step", 1);
                    if (prop.SliderStep <= 0) prop.SliderStep = 1;   // StepFrequency=0 会抛异常
                    if (prop.SliderMax < prop.SliderMin)
                        (prop.SliderMin, prop.SliderMax) = (prop.SliderMax, prop.SliderMin);   // 个别壁纸 min/max 写反，交换防 Slider 崩溃
                    if (obj.TryGetProperty("precision", out var pv) && pv.ValueKind == JsonValueKind.Number
                        && pv.TryGetInt32(out int precision))
                        prop.Precision = precision;
                    if (obj.TryGetProperty("value", out var sv) && sv.ValueKind == JsonValueKind.Number
                        && sv.TryGetDouble(out double sliderValue))
                        prop.SliderValue = sliderValue;
                    break;
                case "combo":
                    {
                        var options = new List<ComboOption>();
                        if (obj.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var opt in opts.EnumerateArray())
                            {
                                if (opt.ValueKind != JsonValueKind.Object) continue;
                                string label = GetString(opt, "label") ?? "";
                                string value = GetString(opt, "value") ?? "";
                                if (label.Length == 0 && value.Length == 0) continue;
                                options.Add(new ComboOption { Label = label, Value = value });
                            }
                        }
                        prop.Options = options;
                        if (obj.TryGetProperty("value", out var cv) && cv.ValueKind == JsonValueKind.String)
                        {
                            string current = cv.GetString() ?? "";
                            prop.ComboIndex = options.FindIndex(o => o.Value == current);
                        }
                        break;
                    }
                case "color":
                    if (obj.TryGetProperty("value", out var colv) && colv.ValueKind == JsonValueKind.String)
                    {
                        string raw = colv.GetString() ?? "";
                        string[] parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 3
                            && parts.All(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                        {
                            byte ToByte(string s) => (byte)Math.Clamp(
                                (int)Math.Round(float.Parse(s, CultureInfo.InvariantCulture) * 255f), 0, 255);
                            prop.ColorValue = Windows.UI.Color.FromArgb(255, ToByte(parts[0]), ToByte(parts[1]), ToByte(parts[2]));
                        }
                    }
                    break;
                case "textinput":
                case "scenetexture":
                    if (obj.TryGetProperty("value", out var tv) && tv.ValueKind == JsonValueKind.String)
                        prop.TextValue = tv.GetString() ?? "";
                    break;
            }
        }

        /// <summary>已知的 ui_ 本地化 key 映射（WE 编辑器内置文案，作者可能直接用 key 当标签）</summary>
        private static readonly Dictionary<string, string> KnownUiKeys = new()
        {
            ["ui_browse_properties_scheme_color"] = "主色调",
        };
    }
}

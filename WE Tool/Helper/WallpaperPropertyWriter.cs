using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Serilog;
using System.Text.Json;
using WE_Tool.Json;
using WE_Tool.Models;

namespace WE_Tool.Helper
{
    /// <summary>
    /// 把属性面板的编辑值写回壁纸目录 project.json。
    /// 定案方案：文本级定点替换——只改 general.properties.&lt;key&gt;.value 这一个 token，
    /// 其余字节一律不动（模型重序列化会丢未知字段/改键序/改写 HTML 转义，整体 JsonDocument 重写会产生巨量 diff）。
    /// 安全措施：写前备份 project.json.bak、临时文件 + File.Replace 原子写、写后 JsonDocument 重解析校验。
    /// </summary>
    internal static class WallpaperPropertyWriter
    {
        /// <summary>写回全部可编辑属性；任一键定位失败则整体不写盘。返回 (成功, 错误信息)。</summary>
        public static (bool Ok, string? Error) Save(string folderPath, IReadOnlyList<WallpaperProperty> properties)
        {
            string path = Path.Combine(folderPath, "project.json");
            if (!File.Exists(path))
                return (false, $"project.json 不存在: {path}");

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取 project.json 失败: {Path}", path);
                return (false, $"读取 project.json 失败: {ex.Message}");
            }

            var editable = properties.Where(p => p.IsEditable && !(p.Type == "combo" && p.ComboIndex < 0)).ToList();
            if (editable.Count == 0)
                return (false, "没有可写回的属性");

            string modified = json;
            var failedKeys = new List<string>();
            foreach (var prop in editable)
            {
                string token = EncodeValue(prop);
                if (TryReplaceValueToken(modified, prop.Key, token, out string next))
                {
                    modified = next;
                }
                else
                {
                    failedKeys.Add(prop.Key);
                }
            }

            if (failedKeys.Count > 0)
                return (false, $"以下属性在 project.json 中未找到，已取消保存: {string.Join(", ", failedKeys)}");

            // 写后校验：重新解析失败则放弃（不写盘，原文件不受影响）
            try
            {
                JsonDocument.Parse(modified);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "写回结果 JSON 校验失败,已取消保存: {Path}", path);
                return (false, $"写回结果 JSON 校验失败，已取消保存: {ex.Message}");
            }

            // 临时文件 + File.Replace 原子写，同时生成备份
            string tempPath = path + ".tmp";
            try
            {
                File.WriteAllText(tempPath, modified);
                File.Replace(tempPath, path, path + ".bak", ignoreMetadataErrors: true);
                return (true, null);
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                Log.Warning(ex, "写入 project.json 失败: {Path}", path);
                return (false, $"写入 project.json 失败: {ex.Message}");
            }
        }

        /// <summary>值编码：bool→true/false；slider→按 precision 四舍五入的数字；color→"r g b"；combo/textinput→JSON 转义字符串</summary>
        private static string EncodeValue(WallpaperProperty prop) => prop.Type switch
        {
            "bool" => prop.BoolValue ? "true" : "false",
            "slider" => FormatSlider(prop.SliderValue, prop.Precision),
            "combo" => JsonSerializer.Serialize(prop.ComboValue, JsonContext.Default.String),
            "color" => FormatColor(prop.ColorValue),
            "textinput" => JsonSerializer.Serialize(prop.TextValue, JsonContext.Default.String),
            "scenetexture" => JsonSerializer.Serialize(prop.TextValue, JsonContext.Default.String),
            _ => JsonSerializer.Serialize(prop.DisplayValue, JsonContext.Default.String)   // IsEditable 过滤后理论不可达
        };

        private static string FormatSlider(double value, int precision)
        {
            double rounded = precision >= 0
                ? Math.Round(value, precision, MidpointRounding.AwayFromZero)
                : value;
            return rounded.ToString("0.###############", CultureInfo.InvariantCulture);
        }

        /// <summary>0-255 字节 → "r g b" 浮点串（保留 4 位小数，WE 编辑器同款格式）</summary>
        private static string FormatColor(Windows.UI.Color c)
        {
            string ToFloat(byte b) => (b / 255f).ToString("0.####", CultureInfo.InvariantCulture);
            return $"\"{ToFloat(c.R)} {ToFloat(c.G)} {ToFloat(c.B)}\"";
        }

        /// <summary>
        /// 在 JSON 文本中定位 "key" : { 块（括号深度跟踪，跳过字符串），
        /// 在块内找第一个属于该对象的 "value" 键（depth==1，避免误替换 options 里的 value），
        /// 用 newToken 替换其值 token。返回替换后的完整文本。
        /// </summary>
        private static bool TryReplaceValueToken(string json, string key, string newToken, out string result)
        {
            string keyToken = "\"" + key.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            int searchFrom = 0;

            while (true)
            {
                int keyIdx = json.IndexOf(keyToken, searchFrom, StringComparison.Ordinal);
                if (keyIdx < 0)
                {
                    result = json;
                    return false;
                }
                searchFrom = keyIdx + keyToken.Length;

                int colon = SkipWhitespace(json, searchFrom);
                if (colon >= json.Length || json[colon] != ':') continue;
                int brace = SkipWhitespace(json, colon + 1);
                if (brace >= json.Length || json[brace] != '{') continue;

                // 块内扫描：depth==1 时遇到的 "value" 键即属性对象的值键
                int depth = 1;
                int i = brace + 1;
                while (i < json.Length)
                {
                    char c = json[i];
                    if (c == '"')
                    {
                        string tokenText = ReadJsonString(json, ref i);
                        int after = SkipWhitespace(json, i);
                        if (depth == 1 && tokenText == "value"
                            && after < json.Length && json[after] == ':')
                        {
                            int valStart = SkipWhitespace(json, after + 1);
                            int valEnd = ReadValueTokenEnd(json, valStart);
                            result = json[..valStart] + newToken + json[valEnd..];
                            return true;
                        }
                        continue;
                    }
                    if (c == '{') depth++;
                    else if (c == '}') { depth--; if (depth == 0) break; }
                    i++;
                }
                // 该位置不是属性对象（如 text 内容恰好形似），继续找下一处
            }
        }

        /// <summary>跳过空白，返回第一个非空白字符下标</summary>
        private static int SkipWhitespace(string json, int i)
        {
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            return i;
        }

        /// <summary>json[i] 为 '"'，读取字符串（跳过转义），i 更新到闭合引号之后；返回原始内容（不含引号）</summary>
        private static string ReadJsonString(string json, ref int i)
        {
            int start = i + 1;
            i++;
            while (i < json.Length)
            {
                if (json[i] == '\\') i += 2;
                else if (json[i] == '"') { i++; break; }
                else i++;
            }
            return json[start..(i - 1)];
        }

        /// <summary>返回从 start 开始的值 token 的结束下标（字符串含引号跳转义；字面量到 , } 或空白止）</summary>
        private static int ReadValueTokenEnd(string json, int start)
        {
            if (start >= json.Length) return start;
            if (json[start] == '"')
            {
                int i = start + 1;
                while (i < json.Length)
                {
                    if (json[i] == '\\') i += 2;
                    else if (json[i] == '"') return i + 1;
                    else i++;
                }
                return json.Length;
            }
            int j = start;
            while (j < json.Length && json[j] != ',' && json[j] != '}' && !char.IsWhiteSpace(json[j])) j++;
            return j;
        }
    }
}

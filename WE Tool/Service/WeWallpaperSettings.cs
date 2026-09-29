using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Serilog;
using WE_Tool.Helper;
using WE_Tool.Json;
using WE_Tool.Models;
using Windows.UI;

namespace WE_Tool.Service
{
    /// <summary>
    /// WE 给每张壁纸另存一层「WE 自带的属性」(主题配色/对齐方式/位置/翻转/鼠标视差/播放速度/
    /// 图片筛选器/颜色校正)。它们不在 project.json 里:WE 只把用户改过的键写进安装目录 config.json 的
    /// &lt;Windows账户&gt;.wproperties[壁纸主文件路径][MonitorN],没记录的键走 WE 内部默认值 ——
    /// 本类内置的那几个默认值取自 WE 自己的 UI 代码(getSharedDefaultProperties:对齐=覆盖、位置=50、
    /// 播放速度=100、鼠标视差=开);颜色校正/滤镜强度的默认值 WE 没暴露,面板显示「默认」占位。
    ///
    /// 读出的每一行都是 <see cref="WallpaperProperty"/>,与 project.json 属性共用同一套渲染
    /// (<see cref="WallpaperPropertyRowBuilder"/>),靠 IsWeBuiltin 区分写回目标。
    /// 写回与 <see cref="WallpaperPropertyWriter"/> 同法:文本级定点替换 + 备份 + 临时文件 File.Replace +
    /// 写后 JSON 校验 —— WE 的配置文件结构未知且它自己会整份重写,模型重序列化会产生巨量 diff。
    /// 注意:WE 运行中时它按内存里的配置整份覆写该文件,写完要重启 WE 才生效(由调用方提示)。
    /// </summary>
    internal static class WeWallpaperSettings
    {
        // ==================== 内置属性目录 ====================

        /// <summary>一个内置键:WE 语言文件里的标签键、控件类型、范围/默认值。
        /// DefaultNumber/DefaultBool 为 null = WE 没暴露该键的默认值 ⇒ 无记录时显示「默认」占位。
        /// GateKey 非空 = 这一行只在指定那条 bool 打开时才显示(照搬 WE 对话框的 condition)。</summary>
        private sealed record BuiltinDef(
            string Key,
            string LocaleKey,
            string Type,
            double Min = 0,
            double Max = 100,
            double? DefaultNumber = null,
            bool? DefaultBool = null,
            bool IsAlignment = false,
            bool IsLutFilter = false,
            string GateKey = "");

        /// <summary>顺序即面板里的行序(WE 自己的属性对话框也是主题配色/对齐在前)。</summary>
        private static readonly BuiltinDef[] Defs =
        [
            new("schemecolor", "ui_browse_properties_scheme_color", "color"),
            new("alignment", "ui_browse_properties_alignment", "combo", IsAlignment: true),
            new("alignmentposition", "ui_browse_properties_alignment_position", "slider", 0, 100, DefaultNumber: 50),
            new("alignmentfliph", "ui_browse_properties_alignment_flip_horizontally", "bool", DefaultBool: false),
            new("cameraparallax", "ui_browse_properties_mouse_parallax", "bool", DefaultBool: true),
            new("rate", "ui_browse_properties_playback_rate", "slider", 10, 200, DefaultNumber: 100),
            new("wcc_v", "ui_browse_properties_image_filter", "combo", IsLutFilter: true),
            // 这五条 WE 没在 UI 侧暴露默认值,getSharedDefaultProperties 里查不到,之前只能显示「默认」占位。
            // 实测取证自 WE 自己写出的预设表(config.json 的 wpresets[].properties):WE 每次分享/保存都把这 15 个键
            // 整份写全,没动过的就是它的原生默认 —— wcc_amt=100(滤镜强度满档)、wec_*=50(中性)、wec_e=false。
            // 补齐之后它们不再是未定态,分享 JSON 也才和 WE 那串一样是满的(见 BuildShareJson)。
            new("wcc_amt", "ui_browse_properties_filter_strength", "slider", DefaultNumber: 100),
            new("wec_e", "ui_browse_properties_show_color_options", "bool", DefaultBool: false),
            new("wec_brs", "ui_browse_properties_brightness", "slider", DefaultNumber: 50, GateKey: "wec_e"),
            new("wec_con", "ui_browse_properties_contrast", "slider", DefaultNumber: 50, GateKey: "wec_e"),
            new("wec_hue", "ui_browse_properties_hue_shift", "slider", DefaultNumber: 50, GateKey: "wec_e"),
            new("wec_sa", "ui_browse_properties_saturation", "slider", DefaultNumber: 50, GateKey: "wec_e"),
        ];

        /// <summary>对齐方式的枚举:WE 存序号,0..4 的顺序按 WE 语言文件的排列推定。</summary>
        private static readonly string[] AlignmentLocaleKeys =
        [
            "ui_browse_properties_alignment_cover",
            "ui_browse_properties_alignment_center",
            "ui_browse_properties_alignment_stretch",
            "ui_browse_properties_alignment_fill",
            "ui_browse_properties_alignment_free",
        ];

        // ==================== 读取 ====================

        /// <summary>属性行 = WE 内置属性(在最上面) + 一条分隔线 + project.json 的作者属性。
        /// 分隔线挂在作者属性头顶而不是内置块尾巴:壁纸没有作者属性时,尾巴那条线就成了没人分隔的孤线。
        /// 主题配色归内置块(WE 里改的就是它的覆盖值),同项不再在作者属性里重复;
        /// 内置块不可用(没装 WE / 读不了配置)时原样返回作者属性,主题配色也就留在作者属性里。</summary>
        public static List<WallpaperProperty> ReadRows(string folderPath)
        {
            // 解析器每次给的都是新建的一批行(它缓存的是 project.json 那段原文),这份列表可以直接动
            var author = WallpaperPropertyParser.Parse(folderPath);
            var block = Read(folderPath, author.FirstOrDefault(p => p.Key == "schemecolor")?.ColorValue);
            if (block == null) return author;

            author.RemoveAll(p => p.Key == "schemecolor");
            var rows = new List<WallpaperProperty>(block.Count + author.Count + 1);
            rows.AddRange(block);
            if (author.Count > 0)
            {
                rows.Add(DividerRow());
                rows.AddRange(author);
            }
            return rows;
        }

        /// <summary>内置块与作者属性之间的分隔线(BuildGroupHeader 的空标题 = 纯一条线,没有文字)。</summary>
        private static WallpaperProperty DividerRow() => new()
        {
            Key = "we-settings-divider",
            Type = "text",
            IsGroupHeader = true,
            DisplayText = "",
            Text = "",
        };

        /// <summary>读一张壁纸的 WE 内置属性:13 行平铺(不加分组标题、不加分隔线,分开的手段由 ReadRows 决定位置)。
        /// 「亮度/对比度/色调偏移/饱和度」四行挂在「显示颜色选项」下面,没打开时由行构建器收起(值仍在,照存)。
        /// WE 装不上/读不了时返回 null,调用方照旧只显示 project.json 的属性。</summary>
        /// <param name="authorSchemeColor">壁纸 project.json 里的主题配色 —— WE 对该项的默认值就是它</param>
        private static List<WallpaperProperty>? Read(string folderPath, Color? authorSchemeColor)
        {
            try
            {
                string? weDir = FindWeDir();
                if (weDir == null) return null;
                var locale = LoadLocale(weDir);
                if (locale == null) return null;
                var root = LoadConfig(Path.Combine(weDir, "config.json"));
                if (root == null) return null;
                var profile = FindProfile(root.Value);
                if (profile == null) return null;

                var entry = ReadEntry(profile.Value, folderPath, out string? entryKey);
                string? mainFile = entryKey ?? DeriveMainFileKey(folderPath);
                string monitor = MonitorFromConfigs(profile.Value, mainFile) ?? FirstMonitorOf(entry) ?? "Monitor0";
                JsonElement overrides = default;
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty(monitor, out var mon) && mon.ValueKind == JsonValueKind.Object)
                    overrides = mon;
                Log.Information("[WE 属性] {Folder} @ {Monitor},覆盖 {Count} 项",
                    folderPath, monitor, overrides.ValueKind == JsonValueKind.Object ? overrides.EnumerateObject().Count() : 0);

                // 不加分组标题:这 13 项直接平铺在最上面(与作者属性之间的分隔线由 ReadRows 放)
                var rows = new List<WallpaperProperty>(Defs.Length);
                foreach (var def in Defs)
                    rows.Add(BuildRow(def, overrides, locale, authorSchemeColor));

                // 条件显隐接线:内置行按 Defs 顺序从 rows[0] 开始
                for (int i = 0; i < Defs.Length; i++)
                {
                    if (Defs[i].GateKey.Length == 0) continue;
                    var gate = rows.Find(r => r.Key == Defs[i].GateKey);
                    if (gate == null) continue;
                    rows[i].Gate = gate;
                    gate.Gated.Add(rows[i]);
                }

                return rows;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取 Wallpaper Engine 内置属性失败: {Folder}", folderPath);
                return null;
            }
        }

        private static WallpaperProperty BuildRow(
            BuiltinDef def, JsonElement overrides, Dictionary<string, string> locale, Color? authorSchemeColor)
        {
            string label = locale.TryGetValue(def.LocaleKey, out var l) && l.Length > 0 ? l : def.LocaleKey;
            var prop = new WallpaperProperty
            {
                Key = def.Key,
                Type = def.Type,
                Text = label,
                DisplayText = label,
                IsWeBuiltin = true,
                UnsetText = LanguageHelper.GetResource("WeSettings_Default.Text"),
            };

            JsonElement v = default;
            bool has = overrides.ValueKind == JsonValueKind.Object
                       && overrides.TryGetProperty(def.Key, out v)
                       && v.ValueKind != JsonValueKind.Null;

            switch (def.Type)
            {
                case "bool":
                    prop.BoolValue = has
                        ? v.ValueKind == JsonValueKind.True
                          || (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double b) && b != 0)
                        : def.DefaultBool == true;
                    prop.IsUnset = !has && def.DefaultBool == null;
                    break;

                case "slider":
                    prop.SliderMin = def.Min;
                    prop.SliderMax = def.Max;
                    prop.SliderStep = 1;
                    prop.Precision = 0;
                    double number = def.DefaultNumber ?? (def.Min + def.Max) / 2;
                    if (has && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double dv)) number = dv;
                    prop.SliderValue = Math.Clamp(number, def.Min, def.Max);
                    prop.IsUnset = !has && def.DefaultNumber == null;
                    break;

                case "combo":
                    prop.Options = def.IsAlignment ? BuildAlignmentOptions(locale) : BuildLutOptions(locale);
                    string current = has && v.ValueKind == JsonValueKind.String ? v.GetString() ?? ""
                                   : has && v.ValueKind == JsonValueKind.Number ? v.GetRawText()
                                   : def.IsAlignment ? "0" : "";   // 对齐默认 0=覆盖;滤镜默认空=无
                    prop.ComboIndex = prop.Options.ToList().FindIndex(o => o.Value == current);
                    prop.DisplayValue = prop.ComboDisplayText;
                    prop.IsUnset = false;   // 两个的默认值都已知(覆盖 / 无)
                    break;

                case "color":
                    if (has && v.ValueKind == JsonValueKind.String && TryParseColorTriple(v.GetString(), out var c))
                        prop.ColorValue = c;
                    else if (authorSchemeColor is Color authored)
                        prop.ColorValue = authored;
                    prop.IsUnset = !has && authorSchemeColor == null;
                    break;
            }

            // 读入不算改动:上面的赋值会经过 setter 的「用户改过值」判定,这里统一抹平
            prop.IsModified = false;
            return prop;
        }

        private static List<ComboOption> BuildAlignmentOptions(Dictionary<string, string> locale)
        {
            var options = new List<ComboOption>();
            for (int i = 0; i < AlignmentLocaleKeys.Length; i++)
            {
                string key = AlignmentLocaleKeys[i];
                options.Add(new ComboOption
                {
                    Value = i.ToString(CultureInfo.InvariantCulture),
                    Label = locale.TryGetValue(key, out var l) && l.Length > 0 ? l : key,
                });
            }
            return options;
        }

        /// <summary>滤镜列表:WE 语言文件里的 ui_browse_lut_filter_* 就是全集(名字即 config 里存的值)。</summary>
        private static List<ComboOption> BuildLutOptions(Dictionary<string, string> locale)
        {
            var options = new List<ComboOption>
            {
                new() { Value = "", Label = locale.TryGetValue("ui_browse_properties_image_filter_none", out var none) ? none : "—" }
            };
            const string prefix = "ui_browse_lut_filter_";
            foreach (var kv in locale)
            {
                if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal) || kv.Value.Length == 0) continue;
                options.Add(new ComboOption { Value = kv.Key[prefix.Length..], Label = kv.Value });
            }
            return options;
        }

        private static bool TryParseColorTriple(string? raw, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string[] parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) return false;
            try
            {
                byte ToByte(string s) => (byte)Math.Clamp(
                    (int)Math.Round(float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture) * 255f), 0, 255);
                color = Color.FromArgb(255, ToByte(parts[0]), ToByte(parts[1]), ToByte(parts[2]));
                return true;
            }
            catch { return false; }
        }

        // ==================== 写回 ====================

        /// <summary>把两层编辑分头写回:WE 内置属性 → WE 的 config.json,作者属性 → project.json。
        /// 任一层失败即整体报错(两层各自都是原子写,不会写坏)。</summary>
        public static (bool Ok, string? Error, bool WroteProject, int WeWritten) SaveRows(
            string folderPath, IReadOnlyList<WallpaperProperty> rows)
        {
            var flat = rows
                .SelectMany(p => p.Children.Prepend(p))
                .Where(p => p.IsEditable && !(p.Type == "combo" && p.ComboIndex < 0))
                .ToList();

            var (weOk, weError, weWritten) = Save(folderPath, flat.Where(p => p.IsWeBuiltin).ToList());
            if (!weOk) return (false, weError, false, 0);

            bool wroteProject = false;
            var project = flat.Where(p => !p.IsWeBuiltin).ToList();
            if (project.Count > 0)
            {
                var (ok, error) = WallpaperPropertyWriter.Save(folderPath, project);
                if (!ok) return (false, error, false, weWritten);
                wroteProject = true;
            }

            // 改动已落盘 → 抹掉脏标记:宿主的「应用更改/撤销更改」就靠这个标记决定可用态,不抹会一直亮着
            foreach (var p in rows.SelectMany(p => p.Children.Prepend(p))) p.IsModified = false;
            return (true, null, wroteProject, weWritten);
        }

        /// <summary>保存结果提示(两层都可能写;WE 那层的值要等它重启才生效)。</summary>
        public static string SaveResultText(bool wroteProject, int weWritten)
        {
            if (weWritten > 0 && wroteProject)
                return "属性已保存到 project.json，Wallpaper Engine 的内置属性也已写入；重启 Wallpaper Engine 生效。";
            if (weWritten > 0)
                return "已写入 Wallpaper Engine 的内置属性；重启 Wallpaper Engine 生效。";
            return "属性已保存到 project.json。";
        }

        /// <summary>把内置行里改过的写回 WE 的 config.json。没有改动时直接成功(不碰文件)。</summary>
        private static (bool Ok, string? Error, int Written) Save(
            string folderPath, IReadOnlyList<WallpaperProperty> rows)
        {
            var changed = rows.Where(r => r.IsWeBuiltin && r.IsModified).ToList();
            if (changed.Count == 0) return (true, null, 0);

            string? weDir = FindWeDir();
            if (weDir == null)
                return (false, "找不到 Wallpaper Engine 安装目录（注册表 Software\\WallpaperEngine 的 installPath）", 0);
            string path = Path.Combine(weDir, "config.json");

            string json;
            try { json = File.ReadAllText(path); }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取 WE 配置失败: {Path}", path);
                return (false, $"读取 Wallpaper Engine 配置失败：{ex.Message}", 0);
            }

            try
            {
                // 先按与读取时同一套逻辑定好「哪个壁纸段、哪台显示器」,文本侧只负责把值写进去
                var root = LoadConfig(path);
                var profileElement = root == null ? null : FindProfile(root.Value);
                if (profileElement == null) return (false, "Wallpaper Engine 配置里找不到当前账户段", 0);
                var entryElement = ReadEntry(profileElement.Value, folderPath, out string? entryKey);
                string? targetKey = entryKey ?? DeriveMainFileKey(folderPath);
                if (targetKey == null)
                    return (false, "无法确定该壁纸在 Wallpaper Engine 里的主文件，改动没有写出去", 0);
                string monitor = MonitorFromConfigs(profileElement.Value, targetKey)
                                 ?? FirstMonitorOf(entryElement) ?? "Monitor0";

                int rootOpen = json.IndexOf('{', StringComparison.Ordinal);
                if (rootOpen < 0) return (false, "Wallpaper Engine 配置内容无法识别", 0);
                int rootClose = FindClose(json, rootOpen);
                var profile = FindProfileMember(json, rootOpen, rootClose);
                if (profile == null) return (false, "Wallpaper Engine 配置里找不到当前账户段", 0);

                string modified = json;
                int wpOpen = SetOrFindMember(ref modified, profile.Value.ObjOpen, profile.Value.ObjClose, "wproperties");
                if (wpOpen < 0) return (false, "Wallpaper Engine 配置里无法定位 wproperties", 0);

                int wpClose = FindClose(modified, wpOpen);
                int entryOpen = SetOrFindMember(ref modified, wpOpen, wpClose, targetKey);
                if (entryOpen < 0) return (false, "定位壁纸段失败", 0);

                int entryClose = FindClose(modified, entryOpen);
                int monOpen = SetOrFindMember(ref modified, entryOpen, entryClose, monitor);
                if (monOpen < 0) return (false, "定位显示器段失败", 0);

                int monClose = FindClose(modified, monOpen);
                int written = 0;
                foreach (var row in changed)
                {
                    string token = EncodeToken(row);
                    if (token.Length == 0) continue;
                    SetMemberValue(ref modified, monOpen, monClose, row.Key, token);
                    monClose = FindClose(modified, monOpen);   // 写入会挪动闭合位置
                    written++;
                }
                if (written == 0) return (true, null, 0);

                try { JsonDocument.Parse(modified); }
                catch (Exception ex)
                {
                    Log.Warning(ex, "WE 配置写回结果校验失败,已取消保存: {Path}", path);
                    return (false, $"写回结果不是合法 JSON，已取消保存：{ex.Message}", 0);
                }

                string temp = path + ".tmp";
                try
                {
                    File.WriteAllText(temp, modified);
                    File.Replace(temp, path, path + ".wetool.bak", ignoreMetadataErrors: true);
                    Log.Information("[WE 属性] 已写入 {Count} 项: {Folder} @ {Monitor}", written, folderPath, monitor);
                    return (true, null, written);
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    Log.Warning(ex, "写入 WE 配置失败: {Path}", path);
                    return (false, $"写入 Wallpaper Engine 配置失败：{ex.Message}", 0);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "写回 WE 内置属性失败: {Folder}", folderPath);
                return (false, $"写回 Wallpaper Engine 配置失败：{ex.Message}", 0);
            }
        }

        // ==================== 预设(WE 属性对话框里的「您的预设」) ====================
        // 位置:config.json 的 <账户>.general.wpresets[主文件].presets[],与 wproperties 平级。
        // 只覆盖内置那层(作者属性归 project.json,不进预设——WE 里那层值我们没接)。

        /// <summary>一条本地预设:名字 + 属性值(key → 规范化字符串)。</summary>
        public sealed record WePreset(string Name, Dictionary<string, string> Values);

        /// <summary>读这张壁纸的本地预设(顺序即 WE 里的顺序);没有或读不了返回空表。</summary>
        public static List<WePreset> ReadPresets(string folderPath)
        {
            var list = new List<WePreset>();
            try
            {
                if (!TryContext(folderPath, out _, out var profile, out string? mainFile, out _, out _) || mainFile == null)
                    return list;
                if (!TryGetSection(profile, "wpresets", out var presets) || presets.ValueKind != JsonValueKind.Object)
                    return list;

                foreach (var kv in presets.EnumerateObject())
                {
                    if (!string.Equals(NormalizePath(kv.Name), NormalizePath(mainFile), StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (kv.Value.ValueKind != JsonValueKind.Object
                        || !kv.Value.TryGetProperty("presets", out var arr) || arr.ValueKind != JsonValueKind.Array)
                        return list;
                    foreach (var p in arr.EnumerateArray())
                    {
                        if (p.ValueKind != JsonValueKind.Object) continue;
                        string name = p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                            ? n.GetString() ?? "" : "";
                        if (name.Length == 0) continue;
                        var values = new Dictionary<string, string>(StringComparer.Ordinal);
                        if (p.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                            foreach (var v in props.EnumerateObject())
                            {
                                string? s = NormalizeValue(v.Value);
                                if (s != null) values[v.Name] = s;
                            }
                        list.Add(new WePreset(name, values));
                    }
                    break;
                }
            }
            catch (Exception ex) { Log.Warning(ex, "读取 WE 预设失败: {Folder}", folderPath); }
            return list;
        }

        /// <summary>把当前内置行的值存成预设(WE 的「保存」):重名就并进已有那条 —— 只改我们这 13 个键,
        /// WE 自己多存的(alignmentx/y/z 之类)留着;没有同名就追加一条。</summary>
        public static (bool Ok, string? Error, bool Merged) SavePreset(
            string folderPath, string name, IReadOnlyList<WallpaperProperty> rows)
        {
            name = name.Trim();
            if (name.Length == 0) return (false, "预设名不能为空", false);

            var values = new List<(string Key, string Token)>();
            foreach (var row in rows)
            {
                if (!row.IsWeBuiltin) continue;
                string token = EncodeToken(row);
                if (token.Length > 0) values.Add((row.Key, token));
            }
            if (values.Count == 0) return (false, "没有可保存的属性", false);

            if (!TryContext(folderPath, out string path, out var profile, out string? mainFile, out _, out string? ctxError))
                return (false, ctxError, false);
            if (mainFile == null)
                return (false, "无法确定该壁纸在 Wallpaper Engine 里的主文件，改动没有写出去", false);

            string json;
            try { json = File.ReadAllText(path); }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取 WE 配置失败: {Path}", path);
                return (false, $"读取 Wallpaper Engine 配置失败：{ex.Message}", false);
            }

            try
            {
                string modified = json;
                int rootOpen = modified.IndexOf('{', StringComparison.Ordinal);
                if (rootOpen < 0) return (false, "Wallpaper Engine 配置内容无法识别", false);
                var profileRange = FindProfileMember(modified, rootOpen, FindClose(modified, rootOpen));
                if (profileRange == null) return (false, "Wallpaper Engine 配置里找不到当前账户段", false);

                int generalOpen = SetOrFindMember(ref modified, profileRange.Value.ObjOpen, profileRange.Value.ObjClose, "general");
                if (generalOpen < 0) return (false, "Wallpaper Engine 配置里无法定位 general", false);
                int generalClose = FindClose(modified, generalOpen);

                int presetsRootOpen = SetOrFindMember(ref modified, generalOpen, generalClose, "wpresets");
                if (presetsRootOpen < 0) return (false, "Wallpaper Engine 配置里无法定位 wpresets", false);
                int presetsRootClose = FindClose(modified, presetsRootOpen);

                // 键照 WE 已写的那个用(大小写/斜杠样式随它),没有才按推导出来的建
                string fileKey = FindMemberName(modified, presetsRootOpen, presetsRootClose, mainFile) ?? mainFile;
                int fileOpen = SetOrFindMember(ref modified, presetsRootOpen, presetsRootClose, fileKey);
                if (fileOpen < 0) return (false, "定位壁纸预设段失败", false);
                int fileClose = FindClose(modified, fileOpen);

                int arrOpen = SetOrFindArrayMember(ref modified, fileOpen, fileClose, "presets");
                if (arrOpen < 0) return (false, "定位预设列表失败", false);
                int arrClose = FindClose(modified, arrOpen);

                int elemOpen = FindPresetElement(modified, arrOpen, arrClose, name);
                bool merged = elemOpen >= 0;
                if (merged)
                {
                    int elemClose = FindClose(modified, elemOpen);
                    int propsOpen = SetOrFindMember(ref modified, elemOpen, elemClose, "properties");
                    if (propsOpen < 0) return (false, "定位预设的属性段失败", false);
                    int propsClose = FindClose(modified, propsOpen);
                    foreach (var (key, token) in values)
                    {
                        SetMemberValue(ref modified, propsOpen, propsClose, key, token);
                        propsClose = FindClose(modified, propsOpen);   // 写入会挪动闭合位置
                    }
                }
                else
                {
                    string element = BuildPresetElement(name, values, Newline(modified), ChildIndent(modified, arrClose));
                    AppendArrayElement(ref modified, arrOpen, arrClose, element);
                }

                var (ok, error) = WriteConfig(path, modified, $"预设「{name}」");
                return (ok, error, merged);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "保存 WE 预设失败: {Folder}", folderPath);
                return (false, $"保存 Wallpaper Engine 预设失败：{ex.Message}", false);
            }
        }

        /// <summary>这张壁纸已有的预设名(给"名字重了没"的事前提示用)。
        /// 比较口径照写入那边来:<see cref="FindPresetElement"/> 用的是区分大小写的相等,
        /// 所以这里也按 Ordinal —— 提示说要并入,保存就必须真的并入那条。</summary>
        public static HashSet<string> PresetNames(string folderPath)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in ReadPresets(folderPath)) names.Add(p.Name);
            return names;
        }

        /// <summary>重置(WE 的「重置」):删掉这张壁纸在本机 WE 配置里的覆盖记录,让它回到壁纸默认。
        /// 预设列表不动(WE 也是分开的);本来就没有记录时直接成功。</summary>
        public static (bool Ok, string? Error) ResetLocalOverrides(string folderPath)
        {
            if (!TryContext(folderPath, out string path, out var profile, out _, out string? entryKey, out string? ctxError))
                return (false, ctxError);
            if (entryKey == null) return (true, null);   // 本来就没有覆盖记录

            string json;
            try { json = File.ReadAllText(path); }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取 WE 配置失败: {Path}", path);
                return (false, $"读取 Wallpaper Engine 配置失败：{ex.Message}");
            }

            try
            {
                string modified = json;
                int rootOpen = modified.IndexOf('{', StringComparison.Ordinal);
                if (rootOpen < 0) return (false, "Wallpaper Engine 配置内容无法识别");
                var profileRange = FindProfileMember(modified, rootOpen, FindClose(modified, rootOpen));
                if (profileRange == null) return (false, "Wallpaper Engine 配置里找不到当前账户段");

                int wpOpen = SetOrFindMember(ref modified, profileRange.Value.ObjOpen, profileRange.Value.ObjClose, "wproperties");
                if (wpOpen < 0) return (false, "Wallpaper Engine 配置里无法定位 wproperties");
                int wpClose = FindClose(modified, wpOpen);
                if (!RemoveMember(ref modified, wpOpen, wpClose, entryKey)) return (true, null);

                var (ok, error) = WriteConfig(path, modified, $"重置 {Path.GetFileName(folderPath)}");
                return (ok, error);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "重置 WE 覆盖记录失败: {Folder}", folderPath);
                return (false, $"重置 Wallpaper Engine 设置失败：{ex.Message}");
            }
        }

        /// <summary>把预设/分享 JSON 的值套到行上(WE 的「加载」与「分享 JSON」共用):认不出或不认识的键跳过,
        /// 套上的值算用户改动(IsModified),要点保存才写回。返回套上的项数。</summary>
        public static int ApplyValues(IReadOnlyList<WallpaperProperty> rows, IReadOnlyDictionary<string, string> values)
        {
            int applied = 0;
            foreach (var row in rows)
            {
                if (!row.IsWeBuiltin || !values.TryGetValue(row.Key, out string? raw)) continue;
                switch (row.Type)
                {
                    case "bool":
                        row.BoolValue = raw is "true" or "1";
                        applied++;
                        break;
                    case "slider":
                        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                        {
                            row.SliderValue = Math.Clamp(d, row.SliderMin, row.SliderMax);
                            applied++;
                        }
                        break;
                    case "combo":
                        int index = row.Options.ToList().FindIndex(o => o.Value == raw);
                        if (index >= 0) { row.ComboIndex = index; applied++; }
                        break;
                    case "color":
                        if (TryParseColorTriple(raw, out Color c)) { row.ColorValue = c; applied++; }
                        break;
                }
            }
            return applied;
        }

        /// <summary>当前内置行 → 分享 JSON(WE 的「分享 JSON」同一形状:key → 值,全量、可粘贴回去)。</summary>
        public static string BuildShareJson(IReadOnlyList<WallpaperProperty> rows)
        {
            var sb = new StringBuilder("{\r\n");
            bool first = true;
            foreach (var row in rows)
            {
                if (!row.IsWeBuiltin) continue;
                string token = EncodeToken(row);
                if (token.Length == 0) continue;
                if (!first) sb.Append(",\r\n");
                sb.Append("\t\"").Append(row.Key).Append("\" : ").Append(token);
                first = false;
            }
            sb.Append(first ? "}" : "\r\n}");
            return sb.ToString();
        }

        /// <summary>解析分享 JSON:认 key→值 的对象,也认带 name/properties 的预设对象(取 properties)。
        /// 文本先按原文解析,不像 JSON 再试 Base64(WE 的复制按钮给的是 Base64)。</summary>
        public static (bool Ok, string? Error, Dictionary<string, string> Values) ParseShareJson(string text)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            string trimmed = text.Trim();
            if (trimmed.Length == 0) return (false, "内容为空", values);

            JsonDocument? doc = TryParse(trimmed);
            if (doc == null)
            {
                try
                {
                    string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(trimmed));
                    doc = TryParse(decoded.Trim());
                }
                catch { /* 不是 Base64 */ }
            }
            if (doc == null) return (false, "不是合法的 JSON（也不像 Base64 编码的 JSON）", values);

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return (false, "JSON 顶层要是一个对象", values);
                if (!root.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
                    props = root;

                foreach (var v in props.EnumerateObject())
                {
                    string? s = NormalizeValue(v.Value);
                    if (s != null) values[v.Name] = s;
                }
            }
            if (values.Count == 0) return (false, "没有认得出属性值", values);
            return (true, null, values);

            static JsonDocument? TryParse(string s)
            {
                try { return JsonDocument.Parse(s); }
                catch { return null; }
            }
        }

        /// <summary>预设/分享 JSON 里的值 → 面板能用的字符串:数字原样、布尔 true/false、字符串去引号。</summary>
        private static string? NormalizeValue(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };

        /// <summary>分享文本 → 剪贴板内容:WE 分享框的「复制」按的是 <c>btoa(jsonText)</c>(ui/dist/scripts/scripts.js
        /// 的 BrowseWallpaperSharePresetModalCtrl.copyPreset),照同一口径,我们复制出去的也该是 Base64。
        /// 两边不是完全等价:WE 的 <c>btoa</c> 只吃单字节,而它自己那表的值全是 ASCII(数字/布尔/滤镜名/"r g b"),
        /// 我们按 UTF-8 编 —— 一旦分享对象里出现中文(比如作者属性那层将来接进来),WE 的 <c>atob</c> 解出来是乱码、
        /// 它那步 fromJson 会失败,届时得改它读得懂的编码。</summary>
        public static string EncodeShareForClipboard(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        /// <summary>贴进分享框的文本 → 框里该显示的样子。WE 在同一个控制器里挂了 <c>$watch("data.preset")</c>:
        /// 内容能 atob 解成 JSON 就把它就地换成解码后的文本,所以 WE 那边贴进来永远是可读的。
        /// 我们没做这一步时,从 WE 复制的那串 Base64 就一直躺在框里 —— 那就是「文本框内没有有效信息」。
        /// 已经是 JSON 的原样留着;两者都不是也原样留着,让「应用」那步去说它不合法(别在这里吞掉用户贴的东西)。</summary>
        public static string NormalizeShareText(string text)
        {
            string trimmed = text.Trim();
            if (trimmed.Length == 0 || IsJson(trimmed)) return text;
            try
            {
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(trimmed));
                if (IsJson(decoded)) return decoded;
            }
            catch { /* 不是 Base64 */ }
            return text;

            static bool IsJson(string s)
            {
                try { JsonDocument.Parse(s).Dispose(); return true; }
                catch { return false; }
            }
        }

        /// <summary>写前置:定位 WE 配置与当前账户段(找不到安装目录/账户段时给出提示文案)。
        /// mainFile = 主文件键(优先用配置里已有的那个);entryKey = 覆盖记录已存在时的那个键。</summary>
        private static bool TryContext(
            string folderPath, out string path, out JsonElement profile, out string? mainFile,
            out string? entryKey, out string? error)
        {
            path = "";
            profile = default;
            mainFile = null;
            entryKey = null;
            error = null;

            string? weDir = FindWeDir();
            if (weDir == null)
            {
                error = "找不到 Wallpaper Engine 安装目录（注册表 Software\\WallpaperEngine 的 installPath）";
                return false;
            }
            path = Path.Combine(weDir, "config.json");
            var root = LoadConfig(path);
            var found = root == null ? null : FindProfile(root.Value);
            if (found == null)
            {
                error = "Wallpaper Engine 配置里找不到当前账户段";
                return false;
            }
            profile = found.Value;
            var entry = ReadEntry(profile, folderPath, out string? key);
            entryKey = key;
            mainFile = key ?? DeriveMainFileKey(folderPath);
            return true;
        }

        /// <summary>对象里名为 name 的成员:返回配置里实际写的那个名字(大小写/正反斜杠随它),没有返回 null。</summary>
        private static string? FindMemberName(string s, int objOpen, int objClose, string name)
        {
            foreach (var m in ReadMembers(s, objOpen, objClose))
                if (string.Equals(NormalizePath(m.Name), NormalizePath(name), StringComparison.OrdinalIgnoreCase))
                    return m.Name;
            return null;
        }

        /// <summary>预设数组里名字相同的元素(返回它的 '{' 下标;没有返回 -1)。</summary>
        private static int FindPresetElement(string s, int arrOpen, int arrClose, string name)
        {
            int i = arrOpen + 1;
            while (i < arrClose)
            {
                i = SkipWhitespace(s, i);
                if (i >= arrClose || s[i] == ']') break;
                if (s[i] == '{')
                {
                    int objClose = FindClose(s, i);
                    if (objClose < 0) break;
                    foreach (var m in ReadMembers(s, i, objClose))
                        if (m.Name == "name" && s[m.ValueStart] == '"')
                        {
                            int end = EndOfString(s, m.ValueStart);
                            string value = Unescape(s.Substring(m.ValueStart + 1, Math.Max(0, end - m.ValueStart - 2)));
                            if (value == name) return i;
                        }
                    i = objClose + 1;
                }
                else
                {
                    i = EndOfValue(s, i);
                }
                i = SkipWhitespace(s, i);
                if (i < s.Length && s[i] == ',') i++;
            }
            return -1;
        }

        /// <summary>一条预设的 JSON 文本(name + properties),按 WE 自己的排版(键与括号同缩进、成员再进一级)。
        /// 以换行开头 —— 追加进数组时是接在“前一条的 },”之后。</summary>
        private static string BuildPresetElement(
            string name, IReadOnlyList<(string Key, string Token)> values, string nl, string elemIndent)
        {
            string inner = elemIndent + "\t";
            string deep = inner + "\t";
            var sb = new StringBuilder();
            sb.Append(nl).Append(elemIndent).Append('{').Append(nl);
            sb.Append(inner).Append("\"name\" : ")
              .Append(JsonSerializer.Serialize(name, JsonContext.Default.String)).Append(',').Append(nl);
            sb.Append(inner).Append("\"properties\" : ").Append(nl);
            sb.Append(inner).Append('{').Append(nl);
            for (int i = 0; i < values.Count; i++)
            {
                sb.Append(deep).Append('"').Append(values[i].Key).Append("\" : ").Append(values[i].Token);
                sb.Append(i == values.Count - 1 ? nl : "," + nl);
            }
            sb.Append(inner).Append('}').Append(nl);
            sb.Append(elemIndent).Append('}');
            return sb.ToString();
        }

        /// <summary>往数组尾部追加一个元素(空数组直接放,非空先补逗号)。</summary>
        private static void AppendArrayElement(ref string s, int arrOpen, int arrClose, string element)
        {
            int last = LastNonWhitespaceBefore(s, arrOpen, arrClose);
            if (last == arrOpen)
                s = string.Concat(s.AsSpan(0, arrOpen + 1), element, s.AsSpan(arrOpen + 1));
            else
                s = string.Concat(s.AsSpan(0, last + 1), ",", element, s.AsSpan(last + 1));
        }

        /// <summary>删掉对象里的一个成员(连它那侧的逗号一起)。返回是否删到了。</summary>
        private static bool RemoveMember(ref string s, int objOpen, int objClose, string name)
        {
            var members = ReadMembers(s, objOpen, objClose);
            int index = members.FindIndex(m => m.Name == name);
            if (index < 0) return false;

            int start, end;
            if (index + 1 < members.Count)
            {
                start = members[index].NameStart;
                end = members[index + 1].NameStart;
            }
            else
            {
                end = members[index].ValueEnd;
                int before = members[index].NameStart - 1;
                while (before > objOpen && char.IsWhiteSpace(s[before])) before--;
                start = before > objOpen && s[before] == ',' ? before : members[index].NameStart;
            }
            s = string.Concat(s.AsSpan(0, start), s.AsSpan(end));
            return true;
        }

        /// <summary>子数组;不存在时补一个空数组(键与括号同缩进,同 SetOrFindMember 的排版)。</summary>
        private static int SetOrFindArrayMember(ref string s, int objOpen, int objClose, string name)
        {
            foreach (var m in ReadMembers(s, objOpen, objClose))
            {
                if (m.Name != name) continue;
                return m.ValueStart < s.Length && s[m.ValueStart] == '[' ? m.ValueStart : -1;
            }
            string indent = ChildIndent(s, objClose);
            SetMemberValue(ref s, objOpen, objClose, name, $"{Newline(s)}{indent}[{Newline(s)}{indent}]");

            int newClose = FindClose(s, objOpen);
            foreach (var m in ReadMembers(s, objOpen, newClose))
                if (m.Name == name) return m.ValueStart;
            return -1;
        }

        /// <summary>校验 + 落盘的公共尾巴(与写 wproperties 同一套:合法 JSON 校验 + .wetool.bak + File.Replace)。</summary>
        private static (bool Ok, string? Error) WriteConfig(string path, string modified, string what)
        {
            try { JsonDocument.Parse(modified); }
            catch (Exception ex)
            {
                Log.Warning(ex, "WE 配置写回结果校验失败,已取消保存: {Path}", path);
                return (false, $"写回结果不是合法 JSON，已取消保存：{ex.Message}");
            }

            string temp = path + ".tmp";
            try
            {
                File.WriteAllText(temp, modified);
                File.Replace(temp, path, path + ".wetool.bak", ignoreMetadataErrors: true);
                Log.Information("[WE 预设] 已写入 {What}: {Path}", what, path);
                return (true, null);
            }
            catch (Exception ex)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                Log.Warning(ex, "写入 WE 配置失败: {Path}", path);
                return (false, $"写入 Wallpaper Engine 配置失败：{ex.Message}");
            }
        }

        /// <summary>值 token:对齐是数字,滤镜/配色是字符串,其余是数字/布尔。</summary>
        private static string EncodeToken(WallpaperProperty prop)
        {
            if (prop.IsUnset) return "";   // 没值(不该走到这里,防御)
            return prop.Key switch
            {
                "schemecolor" => JsonSerializer.Serialize(FormatColorTriple(prop.ColorValue), JsonContext.Default.String),
                "alignment" => int.TryParse(prop.ComboValue, out int a) ? a.ToString(CultureInfo.InvariantCulture) : "",
                "wcc_v" => JsonSerializer.Serialize(prop.ComboValue, JsonContext.Default.String),
                _ => prop.Type switch
                {
                    "bool" => prop.BoolValue ? "true" : "false",
                    "slider" => Math.Round(prop.SliderValue, MidpointRounding.AwayFromZero)
                        .ToString("0.###############", CultureInfo.InvariantCulture),
                    _ => "",
                },
            };
        }

        /// <summary>0-255 字节 → "r g b" 浮点串(与 project.json 的属性写回同款格式,WE 也这么存)</summary>
        private static string FormatColorTriple(Color c)
        {
            string ToFloat(byte b) => (b / 255f).ToString("0.####", CultureInfo.InvariantCulture);
            return $"{ToFloat(c.R)} {ToFloat(c.G)} {ToFloat(c.B)}";
        }

        // ==================== 定位 WE 安装目录 / 配置文件 ====================

        private static string? _weDir;
        private static bool _weDirProbed;

        /// <summary>WE 安装目录(含 config.json);注册表 installPath 指向它的 exe。</summary>
        private static string? FindWeDir()
        {
            if (_weDirProbed) return _weDir;
            _weDirProbed = true;
            try
            {
                foreach (string keyName in new[] { @"Software\WallpaperEngine", @"Software\Wallpaper Engine" })
                {
                    using var key = Registry.CurrentUser.OpenSubKey(keyName);
                    if (key?.GetValue("installPath") is not string exePath || exePath.Length == 0) continue;
                    string? dir = Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "config.json")))
                    {
                        _weDir = dir;
                        return _weDir;
                    }
                }
            }
            catch (Exception ex) { Log.Warning(ex, "读注册表定位 Wallpaper Engine 失败"); }

            // 回退:Steam 安装目录下的默认位置
            try
            {
                using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (steam?.GetValue("SteamPath") is string steamPath && steamPath.Length > 0)
                {
                    string dir = Path.Combine(steamPath, "steamapps", "common", "wallpaper_engine");
                    if (File.Exists(Path.Combine(dir, "config.json")))
                    {
                        _weDir = dir;
                        return _weDir;
                    }
                }
            }
            catch (Exception ex) { Log.Warning(ex, "读注册表定位 Steam 失败"); }

            Log.Information("[WE 属性] 未找到 Wallpaper Engine 安装目录,内置属性不可用");
            return null;
        }

        private static readonly ConcurrentDictionary<string, (DateTime WriteUtc, JsonDocument Doc)> ConfigCache = new();

        private static JsonElement? LoadConfig(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var write = File.GetLastWriteTimeUtc(path);
                if (ConfigCache.TryGetValue(path, out var cached) && cached.WriteUtc == write)
                    return cached.Doc.RootElement;
                var doc = JsonDocument.Parse(File.ReadAllText(path));
                ConfigCache[path] = (write, doc);
                return doc.RootElement;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "解析 WE 配置失败: {Path}", path);
                return null;
            }
        }

        private static readonly ConcurrentDictionary<string, (DateTime WriteUtc, Dictionary<string, string> Map)> LocaleCache = new();

        /// <summary>WE 的语言文件(ui_&lt;lang&gt;.json):内置属性的标签、对齐选项名与滤镜名都在里面。</summary>
        private static Dictionary<string, string>? LoadLocale(string weDir)
        {
            foreach (string lang in new[] { PickLocaleName(), "en-us" })
            {
                string path = Path.Combine(weDir, "locale", $"ui_{lang}.json");
                try
                {
                    if (!File.Exists(path)) continue;
                    var write = File.GetLastWriteTimeUtc(path);
                    if (LocaleCache.TryGetValue(path, out var cached) && cached.WriteUtc == write) return cached.Map;
                    var map = new Dictionary<string, string>(StringComparer.Ordinal);
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    foreach (var kv in doc.RootElement.EnumerateObject())
                        if (kv.Value.ValueKind == JsonValueKind.String) map[kv.Name] = kv.Value.GetString() ?? "";
                    LocaleCache[path] = (write, map);
                    return map;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "读取 WE 语言文件失败: {Path}", path);
                }
            }
            return null;
        }

        /// <summary>按界面语言挑 WE 的语言文件名:zh → zh-chs/zh-cht,其余取「语言-地区」小写(如 en-us)。</summary>
        private static string PickLocaleName()
        {
            string tag = Windows.Globalization.ApplicationLanguages.Languages.FirstOrDefault() ?? "en-US";
            try
            {
                var culture = new CultureInfo(tag);
                string lang = culture.TwoLetterISOLanguageName.ToLowerInvariant();
                if (lang == "zh")
                {
                    bool hant = tag.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                             || tag.Contains("-TW", StringComparison.OrdinalIgnoreCase)
                             || tag.Contains("-HK", StringComparison.OrdinalIgnoreCase)
                             || tag.Contains("-MO", StringComparison.OrdinalIgnoreCase);
                    return hant ? "zh-cht" : "zh-chs";
                }
                string[] parts = culture.Name.Split('-');
                return parts.Length >= 2 ? $"{lang}-{parts[^1].ToLowerInvariant()}" : lang;
            }
            catch { return "en-us"; }
        }

        // ==================== 配置里的段 ====================

        /// <summary>config.json 根按 Windows 账户分段(如 "lijun"),取当前账户那一段;
        /// 名字对不上时退而取有 wproperties 的段、再退而取有 general 的段。</summary>
        private static JsonElement? FindProfile(JsonElement root)
        {
            JsonElement? named = null, hasWproperties = null, hasGeneral = null;
            foreach (var kv in root.EnumerateObject())
            {
                if (kv.Value.ValueKind != JsonValueKind.Object) continue;
                if (named == null && string.Equals(kv.Name, Environment.UserName, StringComparison.OrdinalIgnoreCase))
                    named = kv.Value;
                if (hasWproperties == null && kv.Value.TryGetProperty("wproperties", out _)) hasWproperties = kv.Value;
                if (hasGeneral == null && kv.Value.TryGetProperty("general", out _)) hasGeneral = kv.Value;
            }
            return named ?? hasWproperties ?? hasGeneral;
        }

        private static (int ObjOpen, int ObjClose)? FindProfileMember(string json, int rootOpen, int rootClose)
        {
            (int, int)? named = null, hasWproperties = null, hasGeneral = null;
            foreach (var m in ReadMembers(json, rootOpen, rootClose))
            {
                if (m.ValueStart >= json.Length || json[m.ValueStart] != '{') continue;
                int objClose = FindClose(json, m.ValueStart);
                if (objClose < 0) continue;
                if (named == null && m.Name == Environment.UserName) named = (m.ValueStart, objClose);
                if (hasWproperties == null && HasMember(json, m.ValueStart, objClose, "wproperties"))
                    hasWproperties = (m.ValueStart, objClose);
                if (hasGeneral == null && HasMember(json, m.ValueStart, objClose, "general"))
                    hasGeneral = (m.ValueStart, objClose);
            }
            // 名字优先:根下还有 WsiAccount 这类段也有 general,按「有 general 就算」会写错段
            return named ?? hasWproperties ?? hasGeneral;
        }

        /// <summary>取该壁纸在 wproperties 里的记录(键 = 主文件完整路径,按目录匹配),顺带给出键。</summary>
        private static JsonElement ReadEntry(JsonElement profile, string folderPath, out string? entryKey)
        {
            entryKey = null;
            if (!profile.TryGetProperty("wproperties", out var wp) || wp.ValueKind != JsonValueKind.Object)
                return default;
            foreach (var kv in wp.EnumerateObject())
            {
                if (!SameFolder(kv.Name, folderPath)) continue;
                entryKey = kv.Name;
                return kv.Value;
            }
            return default;
        }

        private static bool SameFolder(string configKey, string folderPath)
        {
            string dir;
            try { dir = Path.GetDirectoryName(configKey.Replace('/', '\\')) ?? ""; }
            catch { return false; }
            if (dir.Length == 0) return false;
            return string.Equals(dir.TrimEnd('\\'), folderPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>该壁纸在 WE 里的主文件路径(新建记录时用):场景包优先,其余用 project.json 的 file。</summary>
        private static string? DeriveMainFileKey(string folderPath)
        {
            try
            {
                var pkgs = Directory.EnumerateFiles(folderPath, "*.pkg", SearchOption.TopDirectoryOnly).ToList();
                if (pkgs.Count > 0)
                {
                    string? scene = pkgs.FirstOrDefault(p => string.Equals(
                        Path.GetFileName(p), "scene.pkg", StringComparison.OrdinalIgnoreCase));
                    return Normalize(scene ?? pkgs[0]);
                }

                string projectJson = Path.Combine(folderPath, "project.json");
                if (File.Exists(projectJson))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(projectJson));
                    if (doc.RootElement.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String)
                    {
                        string rel = f.GetString() ?? "";
                        if (rel.Length > 0)
                        {
                            string full = Path.GetFullPath(Path.Combine(folderPath, rel));
                            if (File.Exists(full)) return Normalize(full);
                        }
                    }
                }

                string[] exts = [".mp4", ".webm", ".html", ".exe", ".gif"];
                var media = Directory.EnumerateFiles(folderPath)
                    .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .ToList();
                if (media.Count == 1) return Normalize(media[0]);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "推断壁纸主文件失败: {Folder}", folderPath);
            }
            return null;

            static string Normalize(string path) => path.Replace('\\', '/');
        }

        private static string? FirstMonitorOf(JsonElement entry)
        {
            if (entry.ValueKind != JsonValueKind.Object) return null;
            foreach (var kv in entry.EnumerateObject())
                if (kv.Value.ValueKind == JsonValueKind.Object) return kv.Name;
            return null;
        }

        /// <summary>扫当前配置 / 屏保配置 / 最近配置,找出这张壁纸挂在哪台显示器(读取与写回共用)。
        /// 这几段挂在 profile.general 下(本机实测),个别版本也可能直接挂在 profile 上,两处都找。</summary>
        private static string? MonitorFromConfigs(JsonElement profile, string? mainFile)
        {
            if (string.IsNullOrEmpty(mainFile)) return null;

            foreach (string section in new[] { "wallpaperconfig", "wallpaperconfigscreensaver" })
                if (TryGetSection(profile, section, out var el))
                {
                    var m = MonitorInSection(el, mainFile);
                    if (m != null) return m;
                }

            if (TryGetSection(profile, "wallpaperconfigrecent", out var recent) && recent.ValueKind == JsonValueKind.Array)
            {
                var items = recent.EnumerateArray().ToList();
                for (int i = items.Count - 1; i >= 0; i--)   // 数组按时间追加,末尾最新
                {
                    var m = MonitorInSection(items[i], mainFile);
                    if (m != null) return m;
                }
            }
            return null;
        }

        private static bool TryGetSection(JsonElement profile, string name, out JsonElement section)
        {
            if (profile.TryGetProperty("general", out var general)
                && general.ValueKind == JsonValueKind.Object
                && general.TryGetProperty(name, out section)
                && section.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                return true;
            if (profile.TryGetProperty(name, out section)
                && section.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                return true;
            section = default;
            return false;
        }

        private static string? MonitorInSection(JsonElement section, string mainFile)
        {
            if (section.ValueKind != JsonValueKind.Object) return null;
            if (!section.TryGetProperty("selectedwallpapers", out var selected)
                && !(section.TryGetProperty("config", out var config)
                     && config.ValueKind == JsonValueKind.Object
                     && config.TryGetProperty("selectedwallpapers", out selected)))
                return null;
            if (selected.ValueKind != JsonValueKind.Object) return null;
            foreach (var kv in selected.EnumerateObject())
            {
                if (kv.Value.ValueKind != JsonValueKind.Object) continue;
                if (!kv.Value.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.String) continue;
                string? path = file.GetString();
                if (path != null && string.Equals(NormalizePath(path), NormalizePath(mainFile), StringComparison.OrdinalIgnoreCase))
                    return kv.Name;
            }
            return null;
        }

        private static string NormalizePath(string p) => p.Replace('\\', '/').Trim();

        // ==================== 文本级 JSON 编辑(思路同 WallpaperPropertyWriter) ====================

        private static int SkipWhitespace(string s, int i)
        {
            while (i >= 0 && i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        /// <summary>s[i] 为 '"',返回闭合引号之后的下标。</summary>
        private static int EndOfString(string s, int i)
        {
            i++;
            while (i < s.Length)
            {
                if (s[i] == '\\') i += 2;
                else if (s[i] == '"') return i + 1;
                else i++;
            }
            return s.Length;
        }

        /// <summary>s[i] 为 '{' 或 '[',返回配对的闭合括号下标。</summary>
        private static int FindClose(string s, int i)
        {
            if (i < 0 || i >= s.Length) return -1;
            char open = s[i];
            char close = open == '{' ? '}' : ']';
            int depth = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"') { i = EndOfString(s, i); continue; }
                if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return i;
                }
                i++;
            }
            return -1;
        }

        /// <summary>值 token 的结束下标(字符串跳转义,对象/数组找闭合括号,字面量到分隔符)。</summary>
        private static int EndOfValue(string s, int i)
        {
            if (i >= s.Length) return i;
            char c = s[i];
            if (c == '"') return EndOfString(s, i);
            if (c == '{' || c == '[')
            {
                int close = FindClose(s, i);
                return close < 0 ? s.Length : close + 1;
            }
            int j = i;
            while (j < s.Length && s[j] != ',' && s[j] != '}' && s[j] != ']' && !char.IsWhiteSpace(s[j])) j++;
            return j;
        }

        /// <summary>对象(不含括号)的直接成员:名字(含它在本文件里的起点) + 值区间。</summary>
        private static List<(string Name, int NameStart, int ValueStart, int ValueEnd)> ReadMembers(string s, int objOpen, int objClose)
        {
            var list = new List<(string, int, int, int)>();
            if (objOpen < 0 || objClose <= objOpen) return list;
            int i = objOpen + 1;
            while (i < objClose)
            {
                i = SkipWhitespace(s, i);
                if (i >= objClose || s[i] == '}') break;
                if (s[i] != '"') break;
                int nameStart = i;
                int nameEnd = EndOfString(s, i);
                string name = Unescape(s.Substring(i + 1, Math.Max(0, nameEnd - i - 2)));
                i = SkipWhitespace(s, nameEnd);
                if (i >= s.Length || s[i] != ':') break;
                i = SkipWhitespace(s, i + 1);
                int valueEnd = EndOfValue(s, i);
                list.Add((name, nameStart, i, valueEnd));
                i = SkipWhitespace(s, valueEnd);
                if (i < s.Length && s[i] == ',') i++;
            }
            return list;
        }

        private static bool HasMember(string s, int objOpen, int objClose, string name)
        {
            foreach (var m in ReadMembers(s, objOpen, objClose))
                if (m.Name == name) return true;
            return false;
        }

        /// <summary>改成员的值;成员不存在就在对象末尾补一个(接在最后一个成员之后,或空对象时接在 '{' 之后)。</summary>
        private static void SetMemberValue(ref string s, int objOpen, int objClose, string name, string token)
        {
            foreach (var m in ReadMembers(s, objOpen, objClose))
            {
                if (m.Name != name) continue;
                s = string.Concat(s.AsSpan(0, m.ValueStart), token, s.AsSpan(m.ValueEnd));
                return;
            }
            string line = $"{Newline(s)}{ChildIndent(s, objClose)}\"{name}\" : {token}";
            int last = LastNonWhitespaceBefore(s, objOpen, objClose);
            if (last == objOpen)   // 空对象:直接接在 '{' 后面
                s = string.Concat(s.AsSpan(0, objOpen + 1), line, s.AsSpan(objOpen + 1));
            else                   // 已有成员:补一个逗号后接上去(闭合括号那行的空白保持原样)
                s = string.Concat(s.AsSpan(0, last + 1), ",", line, s.AsSpan(last + 1));
        }

        /// <summary>取子对象继续编辑;不存在时补一个空对象。空对象写成键、{、} 三行(缩进照 WE 的排版:
        /// 键与它的括号同缩进、成员再进一级),随后往里插成员时缩进才对得上。</summary>
        private static int SetOrFindMember(ref string s, int objOpen, int objClose, string name)
        {
            foreach (var m in ReadMembers(s, objOpen, objClose))
            {
                if (m.Name != name) continue;
                return m.ValueStart < s.Length && s[m.ValueStart] == '{' ? m.ValueStart : -1;
            }
            string indent = ChildIndent(s, objClose);
            SetMemberValue(ref s, objOpen, objClose, name, $"{Newline(s)}{indent}{{{Newline(s)}{indent}}}");

            int newClose = FindClose(s, objOpen);
            foreach (var m in ReadMembers(s, objOpen, newClose))
                if (m.Name == name) return m.ValueStart;
            return -1;
        }

        private static string Newline(string s) => s.Contains("\r\n") ? "\r\n" : "\n";

        /// <summary>最后一个非空白字符(空对象返回 '{' 的下标)——决定新成员该接在哪儿。</summary>
        private static int LastNonWhitespaceBefore(string s, int objOpen, int objClose)
        {
            int i = objClose - 1;
            while (i > objOpen && char.IsWhiteSpace(s[i])) i--;
            return i;
        }

        /// <summary>插到这个对象里时该用的缩进:取闭合括号那一行的空白再进一级;那一行不是纯空白就退回空串。</summary>
        private static string ChildIndent(string s, int objClose)
        {
            int lineStart = s.LastIndexOf('\n', Math.Max(0, objClose - 1));
            if (lineStart < 0) return "";
            string closingIndent = s.Substring(lineStart + 1, Math.Max(0, objClose - lineStart - 1));
            return closingIndent.All(c => c == ' ' || c == '\t') ? closingIndent + "\t" : "";
        }

        private static string Unescape(string s)
            => s.IndexOf('\\') < 0 ? s : s.Replace("\\\"", "\"").Replace("\\\\", "\\");
    }
}

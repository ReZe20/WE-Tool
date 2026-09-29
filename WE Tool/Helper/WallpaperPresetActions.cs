using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WE_Tool.Models;
using WE_Tool.Service;

namespace WE_Tool.Helper
{
    /// <summary>
    /// 预设操作的四件事:加载 / 保存 / 分享 JSON / 重置(WE 属性对话框里那组,「应用到所有壁纸」不做 ——
    /// 它要跨壁纸改 config.json,还会动默认值)。按钮在 XAML 上(两处宿主各一排,静态控件不必代码建),
    /// 行为共用这一份:传进来的 rows 是宿主当前面板上的行(与行上控件共享同一批实例)。
    /// 与 WE 的差别只在一处:WE 里改属性立刻生效,我们这边值落在行上、要按「应用更改」才写盘,
    /// 所以加载/套用之后都提示一句"点应用更改生效"。
    /// 弹层除分享 JSON 那一处(它要一个大 JSON 编辑框,按他的意思保持模态)外一律是贴在按钮上的小卡
    /// (DialogHelper.ShowFlyout*),焦点不离开面板;所以这几件事都要唤起它的那枚按钮当锚点。
    /// </summary>
    internal static class WallpaperPresetActions
    {
        private static string Text(string key) => LanguageHelper.GetResource(key);
        private static string Ok => Text("Common_OK.Text");
        private static string Cancel => Text("Common_Cancel.Text");

        /// <summary>加载:在按钮处弹出该壁纸的预设名单(读的是已缓存的内存里的配置,同步建完再弹)。
        /// 选一条就把它的值套到行上,仍要点「应用更改」才写回。</summary>
        public static void ShowLoadFlyout(
            FrameworkElement anchor, string folderPath, Func<IReadOnlyList<WallpaperProperty>> rows)
        {
            List<WeWallpaperSettings.WePreset> presets;
            try
            {
                presets = WeWallpaperSettings.ReadPresets(folderPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取预设失败: {Folder}", folderPath);
                presets = [];
            }

            var flyout = new MenuFlyout();
            string? note = null;
            if (presets.Count == 0)
            {
                flyout.Items.Add(new MenuFlyoutItem { Text = Text("PropertyPreset_Empty.Text"), IsEnabled = false });
            }
            else
            {
                foreach (var preset in presets)
                {
                    var item = new MenuFlyoutItem { Text = preset.Name };
                    var chosen = preset;
                    item.Click += (s, e) =>
                    {
                        int applied = WeWallpaperSettings.ApplyValues(rows(), chosen.Values);
                        note = string.Format(Text("PropertyPreset_LoadedFmt.Text"), chosen.Name, applied);
                    };
                    flyout.Items.Add(item);
                }
            }
            // 套用结果在名单收掉之后才贴出来:两个弹层同时开在一个锚点上会互相顶掉
            flyout.Closed += (s, e) =>
            {
                if (note != null) _ = DialogHelper.ShowFlyoutMessageAsync(anchor, note, Ok);
            };
            flyout.Opened += App.ApplyFlyoutTheme;
            flyout.ShowAt(anchor);
        }

        /// <summary>保存:在按钮处问个名字,把当前内置行整份存成预设。
        /// 名字撞上已有那条,WE 那边是把这次的值并进那条(不是整条替换),我们照同一口径,
        /// 并在输入时就把"已有同名预设"提示出来 —— 不用等保存完再补一层确认。</summary>
        public static async Task SaveAsync(
            FrameworkElement anchor, string folderPath, Func<IReadOnlyList<WallpaperProperty>> rows)
        {
            // 名单在开卡之前一次读齐(与「加载预设」同一条同步读);每次按键再去摸一遍配置就是白白读文件
            var names = WeWallpaperSettings.PresetNames(folderPath);

            string? name = await DialogHelper.ShowFlyoutInputAsync(
                anchor,
                Text("PropertyPreset_SaveHint.Text"),
                Text("PropertyPreset_NameLabel.Text"),
                Text("PropertyPreset_Save.Content"),
                Cancel,
                typed => names.Contains(typed.Trim()) ? Text("PropertyPreset_DupName.Text") : null);
            if (name == null) return;

            var (ok, error, merged) = await Task.Run(() => WeWallpaperSettings.SavePreset(folderPath, name, rows()));
            await DialogHelper.ShowFlyoutMessageAsync(anchor,
                ok ? string.Format(Text(merged ? "PropertyPreset_MergedFmt.Text" : "PropertyPreset_SavedFmt.Text"), name)
                   : error ?? "", Ok);
        }

        /// <summary>分享 JSON:把当前值摆出来可复制可改,点应用就把改过的值套回行上(不写盘,仍要点「应用更改」)。
        /// 它要装一个多行 JSON 编辑框,整条留模态(小卡装不下);剪贴板两向的口径照 WE 的分享框来:
        /// 复制出去是 Base64,贴进来只要还原得出 JSON 就当场换成可读文本(见 WeWallpaperSettings 那两个方法)。</summary>
        public static async Task ShareAsync(FrameworkElement anchor, Func<IReadOnlyList<WallpaperProperty>> rows)
        {
            var current = rows();
            int builtin = current.Count(r => r.IsWeBuiltin);
            string json = WeWallpaperSettings.BuildShareJson(current);
            // 读数:内置行数 / JSON 里的项数 / 字节数 —— "框里没东西"分三种(没内置行、值全未定态、生成端没事而弹窗端出事)
            Log.Information("[WE 预设] 分享 JSON: 行共 {Rows} 条(内置 {Builtin}),JSON {Items} 项 {Length} 字节",
                current.Count, builtin, json.Split('\n').Length - 2, json.Length);
            if (builtin > 0 && json.Length <= 4)
                Log.Warning("[WE 预设] 分享 JSON: 内置行有 {Builtin} 条,但一个键都没生成出来(值都是未定态?)", builtin);

            if (builtin == 0)
            {
                await DialogHelper.ShowMessageAsync(Text("PropertyPreset_ShareTitle.Text"),
                    Text("PropertyPreset_ShareEmpty.Text"));
                return;
            }

            string? edited = await DialogHelper.ShowJsonAsync(
                anchor,
                Text("PropertyPreset_ShareTitle.Text"),
                Text("PropertyPreset_ShareHint.Text"),
                json,
                Text("PropertyPreset_Copy.Text"),
                Text("PropertyPreset_Paste.Text"),
                Text("PropertyPreset_Apply.Text"),
                WeWallpaperSettings.EncodeShareForClipboard,
                WeWallpaperSettings.NormalizeShareText);
            if (edited == null) return;

            var (ok, error, values) = WeWallpaperSettings.ParseShareJson(edited);
            if (!ok)
            {
                Log.Warning("[WE 预设] 分享 JSON 没有套用: {Error}", error);
                await DialogHelper.ShowMessageAsync(Text("PropertyPreset_ShareTitle.Text"), error ?? "");
                return;
            }

            int applied = WeWallpaperSettings.ApplyValues(rows(), values);
            // 认不出的键跳掉不算错(WE 的表里多 alignmentx/y/z 这些我们没实现的),但要看得见跳了哪些,
            // 否则"套上了 0 项"这种就无从判断是键名不对还是值不对
            var known = new HashSet<string>(rows().Where(r => r.IsWeBuiltin).Select(r => r.Key), StringComparer.Ordinal);
            var skipped = values.Keys.Where(k => !known.Contains(k)).ToList();
            Log.Information("[WE 预设] 分享 JSON 套用: 读到 {Read} 项,套上 {Applied} 项,不认识的键 {Skipped}",
                values.Count, applied, string.Join(",", skipped));

            await DialogHelper.ShowMessageAsync(Text("PropertyPreset_Header.Content"),
                string.Format(Text("PropertyPreset_AppliedFmt.Text"), applied));
        }

        /// <summary>重置:先在按钮处确认一句,再清掉这张壁纸在 WE 配置里的覆盖记录,面板重读回默认值(预设不动)。</summary>
        public static async Task ResetAsync(FrameworkElement anchor, string folderPath, Func<Task> reload)
        {
            bool go = await DialogHelper.ShowFlyoutConfirmAsync(
                anchor, Text("PropertyPreset_ResetConfirm.Text"), Text("PropertyPreset_Reset.Content"), Cancel);
            if (!go) return;

            var (ok, error) = await Task.Run(() => WeWallpaperSettings.ResetLocalOverrides(folderPath));
            if (!ok)
            {
                await DialogHelper.ShowFlyoutMessageAsync(anchor, error ?? "", Ok);
                return;
            }

            await reload();
            await DialogHelper.ShowFlyoutMessageAsync(anchor, Text("PropertyPreset_ResetDone.Text"), Ok);
        }
    }
}

using System.Runtime.InteropServices;
using System.Text;

namespace TestBackupContract;

/// <summary>
/// 场景 fixture 构造器:在独立临时根目录下摆出「config.json + VDF + 工坊 content 目录」三件套。
/// 占位符 {ROOT}/{DATA}/{VDF}/{WS} 在写入前替换为该场景的实际路径。
/// 所有写盘均为无 BOM UTF-8(与 .NET File.WriteAllText 及真实 Steam 产物一致),
/// 只有显式要 BOM 的变体场景才带 BOM。
/// </summary>
internal sealed class Fx
{
    public string Root { get; }
    public string DataDir => Path.Combine(Root, "data");
    public string Workshop => Path.Combine(Root, "steam", "workshop", "content", "431960");
    public string VdfPath => Path.Combine(Root, "steam", "userdata", "123", "ugc", "431960_subscriptions.vdf");
    public string ConfigPath => Path.Combine(DataDir, "config.json");

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly UTF8Encoding Utf8Bom = new(true);

    public Fx(string root)
    {
        Root = root;
        Directory.CreateDirectory(DataDir);
    }

    public string Expand(string text) => text
        .Replace("{ROOT}", Root)
        .Replace("{DATA}", DataDir)
        .Replace("{VDF}", VdfPath)
        .Replace("{WS}", Workshop);

    /// <summary>路径要嵌进 JSON 字符串字面量,反斜杠必须转义,否则 System.Text.Json 直接判非法转义并拒读。</summary>
    public string ExpandJson(string text) => text
        .Replace("{ROOT}", Esc(Root))
        .Replace("{DATA}", Esc(DataDir))
        .Replace("{VDF}", Esc(VdfPath))
        .Replace("{WS}", Esc(Workshop));

    private static string Esc(string s) => s.Replace("\\", "\\\\");

    public Fx Write(string absPath, string content, bool bom = false)
    {
        var dir = Path.GetDirectoryName(absPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(absPath, Expand(content), bom ? Utf8Bom : Utf8NoBom);
        return this;
    }

    /// <summary>真实 config.json 同形(含服务不读的字段,验证多余键被忽略)。</summary>
    public const string ConfigStandardText = """
        {
          "Version": 2,
          "Path": {
            "DownloadPath": "{ROOT}\\downloads",
            "WorkshopPath": "{WS}",
            "ProjectPath": "{ROOT}\\myprojects",
            "OfficialPath": "{ROOT}\\defaultprojects",
            "AcfPath": "{ROOT}\\steam\\workshop\\appworkshop_431960.acf",
            "VdfPath": "{VDF}",
            "ImportExportPath": "{ROOT}\\export"
          },
          "AutoBackup": {
            "Enabled": true,
            "ServiceEnabled": true,
            "TypeScene": true,
            "TypeVideo": true,
            "TypeWeb": true,
            "TypeApplication": true,
            "TypePreset": true,
            "TypeUnknown": true,
            "RatingG": true,
            "RatingPg": false,
            "RatingR": false
          }
        }
        """;

    public Fx Config(string json, bool bom = false)
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(ConfigPath, ExpandJson(json), bom ? Utf8Bom : Utf8NoBom);
        return this;
    }
    public Fx ConfigStandard(bool bom = false) => Config(ConfigStandardText, bom);

    /// <summary>工坊项目:project.json(小写键,与真实 WE 一致)+ preview.jpg + 若干附加文件。</summary>
    public Fx Item(string id, string type, string rating, params string[] extraFiles)
        => ItemRaw(id, $$"""
            {
              "title": "Item {{id}}",
              "type": "{{type}}",
              "file": "wallpaper.mp4",
              "preview": "preview.jpg",
              "contentrating": "{{rating}}",
              "visibility": "public"
            }
            """, extraFiles);

    public Fx ItemRaw(string id, string projectJson, params string[] extraFiles)
    {
        Write(Path.Combine(Workshop, id, "project.json"), projectJson);
        FileItem(Path.Combine(Workshop, id, "preview.jpg"), $"preview-bytes-{id}");
        foreach (var rel in extraFiles)
            FileItem(Path.Combine(Workshop, id, rel.Replace('/', Path.DirectorySeparatorChar)), $"extra-{id}-{rel}");
        return this;
    }

    public Fx ItemNoProjectJson(string id)
    {
        FileItem(Path.Combine(Workshop, id, "preview.jpg"), $"preview-bytes-{id}");
        return this;
    }

    /// <summary>截断的 project.json(真实库里存在这种脏数据)。</summary>
    public Fx ItemBrokenJson(string id)
        => ItemRaw(id, "{\"title\": \"broken\", \"type\": \"scene\", ");

    public Fx FileItem(string absPath, string content)
    {
        var dir = Path.GetDirectoryName(absPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(absPath, Expand(content), Utf8NoBom);
        return this;
    }

    /// <summary>
    /// 订阅表。disabled=null 表示该条目完全没有 disabled_locally 键(真实 Steam 会写这个键,
    /// 仓库里的 FakeWorkshopGenerator 不写——两者行为差异正是契约要钉住的地方)。
    /// </summary>
    public Fx Vdf(params (string Id, string? Disabled)[] entries)
        => VdfBlocks(entries, disabledBeforeId: false);

    public Fx VdfDisabledFirst(params (string Id, string? Disabled)[] entries)
        => VdfBlocks(entries, disabledBeforeId: true);

    private Fx VdfBlocks((string Id, string? Disabled)[] entries, bool disabledBeforeId)
    {
        var sb = new StringBuilder();
        sb.Append("\"subscribedfiles\"\n{\n");
        sb.Append("\t\"appid\"\t\t\"431960\"\n");
        sb.Append("\t\"time_last_updated\"\t\t\"1789795832\"\n");
        for (int i = 0; i < entries.Length; i++)
        {
            var (id, disabled) = entries[i];
            sb.Append($"\t\"{i}\"\n\t{{\n");
            void Pid() => sb.Append($"\t\t\"publishedfileid\"\t\t\"{id}\"\n");
            void Dis()
            {
                if (disabled != null)
                    sb.Append($"\t\t\"disabled_locally\"\t\t\"{disabled}\"\n");
            }
            if (disabledBeforeId) { Dis(); Pid(); } else { Pid(); Dis(); }
            sb.Append($"\t\t\"time_subscribed\"\t\t\"{1_600_000_000L + i}\"\n");
            sb.Append("\t}\n");
        }
        sb.Append("}\n");
        return Write(VdfPath, sb.ToString());
    }

    /// <summary>在备份目录里预先放一个「外来」文件(内容与源不同),验证服务不覆盖。</summary>
    public Fx PreExistingBackupFile(string id, string rel, string content)
        => FileItem(Path.Combine(Workshop, ".we_backup", id, rel.Replace('/', Path.DirectorySeparatorChar)), content);

    /// <summary>
    /// 模拟「上一次运行被中断」:把某个源文件预先硬链接进备份目录,但不写 .backup_ok。
    /// 必须真是硬链接——否则会被判成外来文件而跳过,测不到补链路径。
    /// </summary>
    public Fx HardlinkedIntoBackup(string id, string relFromItem)
    {
        string src = Path.Combine(Workshop, id, relFromItem.Replace('/', Path.DirectorySeparatorChar));
        string dst = Path.Combine(Workshop, ".we_backup", id, relFromItem.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (!CreateHardLinkW(@"\\?\" + dst, @"\\?\" + src, IntPtr.Zero))
            throw new InvalidOperationException(
                $"预置硬链接失败: {src} → {dst} (0x{Marshal.GetLastWin32Error():X8})");
        return this;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr sa);
}

namespace TestBackupContract;

/// <summary>
/// 契约场景表。每条都是「服务当前实际怎么做」的快照,不是「服务应该怎么做」——
/// 已知可疑行为(B08/B09/A08)照抄钉死,以便 C++ 版被逐字节验证为等价。
/// Args 里的 {ROOT} 由 Program 替换为本次场景根目录。
/// </summary>
internal sealed record Scenario(string Name, Action<Fx> Build, string Args, int Runs = 1)
{
    private const string Once = "--once --data-dir \"{ROOT}\\data\"";
    private const string Verify = "--verify --data-dir \"{ROOT}\\data\"";

    /// <summary>只指定 AutoBackup 段(原文嵌入),Path 段指向本场景根目录。</summary>
    private static Action<Fx> Cfg(string autoBackupJson) => fx =>
    {
        fx.Config($$"""
            {
              "Version": 2,
              "Path": { "WorkshopPath": "{WS}", "VdfPath": "{VDF}" },
              "AutoBackup": {{autoBackupJson}}
            }
            """);
        fx.Vdf();
    };

    public static readonly Scenario[] All =
    [
        // ---- A 组:配置与命令行语义(observable = stdout + 退出码) ----
        new("A01-verify-active",
            fx => { fx.ConfigStandard(); fx.Vdf(("1001", "0")); fx.Item("1001", "video", "Everyone"); }, Verify),

        new("A02-verify-service-disabled",
            Cfg("""{ "Enabled": true, "ServiceEnabled": false }"""), Verify),

        new("A03-verify-workshop-path-empty",
            fx =>
            {
                fx.Config("""{ "Version": 2, "Path": { "WorkshopPath": "", "VdfPath": "{VDF}" }, "AutoBackup": { "Enabled": true, "ServiceEnabled": true } }""");
                fx.Vdf();
            }, Verify),

        new("A04-verify-no-config-file",
            _ => { /* 既不写 config.json 也不写 VDF */ }, Verify),

        new("A05-verify-unknown-mode",
            fx => { fx.ConfigStandard(); fx.Vdf(); }, "--bogus --data-dir \"{ROOT}\\data\""),

        // 缺省值必须与 C# 属性初始化器一致:类型/分级开关缺失时是 true,Enabled 缺失时是 false
        new("A06-config-defaults-missing-toggles",
            Cfg("""{ "Enabled": true, "ServiceEnabled": true }"""), Verify),

        // 大小写不敏感 + 注释 + 尾逗号 + BOM(源生成器选项里那四条读盘语义)
        new("A07-config-camel-comments-trailing-bom",
            fx => fx.Config("""
                {
                  // 行注释也要能吃下
                  "version": 2,
                  "path": { "workshopPath": "{WS}", "vdfPath": "{VDF}", },
                  "autobackup": { "enabled": true, "serviceEnabled": true, "ratingG": true, },
                }
                """, bom: true), Verify),

        // --data-dir 只有在 mode 之后才生效:args[0] 被当成 mode,这里落到 default 分支
        new("A08-argv-data-dir-first",
            fx => { fx.ConfigStandard(); fx.Vdf(); }, "--data-dir \"{ROOT}\\data\" --once"),

        // ---- B 组:补齐(--once)行为 ----
        new("B01-once-inactive",
            Cfg("""{ "Enabled": true, "ServiceEnabled": false }"""), Once),

        new("B02-once-empty-workshop",
            fx => { fx.ConfigStandard(); fx.Vdf(); Directory.CreateDirectory(fx.Workshop); }, Once),

        new("B03-once-workshop-dir-missing",
            fx => { fx.ConfigStandard(); fx.Vdf(); /* 故意不创建 content 目录 */ }, Once),

        new("B04-once-one-video-backup",
            fx => { fx.ConfigStandard(); fx.Vdf(("1001", "0")); fx.Item("1001", "video", "Everyone"); }, Once),

        new("B05-once-filter-by-rating",
            fx =>
            {
                fx.ConfigStandard();
                fx.Vdf(("1001", "0"), ("1002", "0"), ("1003", "0"));
                fx.Item("1001", "video", "Everyone");
                fx.Item("1002", "video", "Teen");
                fx.Item("1003", "video", "Mature");
            }, Once),

        new("B06-once-filter-by-type",
            fx =>
            {
                fx.Config("""
                    { "Version": 2, "Path": { "WorkshopPath": "{WS}", "VdfPath": "{VDF}" },
                      "AutoBackup": { "Enabled": true, "ServiceEnabled": true,
                        "TypeScene": true, "TypeVideo": true, "TypeWeb": false,
                        "TypeApplication": false, "TypePreset": false, "TypeUnknown": false,
                        "RatingG": true, "RatingPg": false, "RatingR": false } }
                    """);
                fx.Vdf(("1001", "0"), ("1002", "0"), ("1003", "0"), ("1004", "0"), ("1005", "0"), ("1006", "0"));
                fx.Item("1001", "scene", "Everyone");
                fx.Item("1002", "video", "Everyone");
                fx.Item("1003", "web", "Everyone");
                fx.Item("1004", "application", "Everyone");
                fx.Item("1005", "preset", "Everyone");
                fx.Item("1006", "html", "Everyone");
            }, Once),

        // 订阅集非空时:不在集里的 content 目录一律不备份
        new("B07-once-not-subscribed-skipped",
            fx =>
            {
                fx.ConfigStandard();
                fx.Vdf(("1001", "0"));
                fx.Item("1001", "video", "Everyone");
                fx.Item("1002", "video", "Everyone");
            }, Once),

        // 可疑行为钉死:条目缺 disabled_locally 时服务的合并正则整条不匹配 → 订阅集为空
        // → BackupAllMissing 把「空集」当成「不过滤」。主程序的两级匹配则视其为有效订阅。
        new("B08-once-vdf-missing-disabled-key",
            fx =>
            {
                fx.ConfigStandard();
                fx.Vdf(("1001", null), ("1002", null));
                fx.Item("1001", "video", "Everyone");
                fx.Item("1002", "video", "Everyone");
            }, Once),

        // 同上,但只是键序颠倒(主程序不受键序影响,服务受影响)
        new("B09-once-vdf-disabled-key-first",
            fx =>
            {
                fx.ConfigStandard();
                fx.VdfDisabledFirst(("1001", "0"), ("1002", "0"));
                fx.Item("1001", "video", "Everyone");
                fx.Item("1002", "video", "Everyone");
            }, Once),

        new("B10-once-vdf-disabled-1-excluded",
            fx =>
            {
                fx.ConfigStandard();
                fx.Vdf(("1001", "0"), ("1002", "1"));
                fx.Item("1001", "video", "Everyone");
                fx.Item("1002", "video", "Everyone");
            }, Once),

        // 第二次运行必须完全幂等(标记文件已在,连枚举都不进)
        new("B11-once-idempotent-second-run",
            fx => { fx.ConfigStandard(); fx.Vdf(("1001", "0")); fx.Item("1001", "video", "Everyone", "sub/data.bin"); },
            Once, Runs: 2),

        // 备份目录里已有同名但内容不同的文件 → 跳过且不覆盖
        new("B12-once-foreign-target-not-overwritten",
            fx =>
            {
                fx.ConfigStandard();
                fx.Vdf(("1001", "0"));
                fx.Item("1001", "video", "Everyone");
                fx.PreExistingBackupFile("1001", "preview.jpg", "foreign!");
            }, Once),

        new("B13-once-nested-subdirs",
            fx =>
            {
                fx.ConfigStandard();
                fx.Vdf(("1001", "0"));
                fx.Item("1001", "scene", "Everyone", "a/b/c/deep.bin", "a/side.bin", "top.bin");
            }, Once),

        new("B14-once-long-path",
            fx =>
            {
                fx.ConfigStandard();
                fx.Vdf(("1001", "0"));
                string nest = string.Join('/', Enumerable.Repeat("LEVEL_1234567890_ABCDEFGHIJKLMNOPQRSTUVWXYZ_0123456789", 6));
                fx.Item("1001", "scene", "Everyone", nest + "/bottom.bin");
            }, Once),

        new("B15-once-non-ascii-names",
            fx =>
            {
                fx.ConfigStandard();
                fx.Vdf(("1001", "0"));
                fx.Item("1001", "scene", "Everyone", "壁纸 目录/预览 说明.txt", "日本語/データ.bin");
            }, Once),

        new("B16-once-broken-project-json",
            fx => { fx.ConfigStandard(); fx.Vdf(("1001", "0")); fx.ItemBrokenJson("1001"); }, Once),

        new("B17-once-missing-project-json",
            fx => { fx.ConfigStandard(); fx.Vdf(("1001", "0")); fx.ItemNoProjectJson("1001"); }, Once),

        // 类型/分级归一化:大小写不敏感 + Trim;分级缺失兜底为 g
        new("B18-once-case-and-space-normalization",
            fx =>
            {
                fx.Config("""
                    { "Version": 2, "Path": { "WorkshopPath": "{WS}", "VdfPath": "{VDF}" },
                      "AutoBackup": { "Enabled": true, "ServiceEnabled": true,
                        "TypeScene": true, "TypeVideo": true, "TypeWeb": false,
                        "TypeApplication": false, "TypePreset": false, "TypeUnknown": false,
                        "RatingG": false, "RatingPg": true, "RatingR": false } }
                    """);
                fx.Vdf(("1001", "0"), ("1002", "0"), ("1003", "0"));
                fx.ItemRaw("1001", """{ "type": "  ViDeO  ", "contentrating": "TEEN" }""");
                fx.ItemRaw("1002", """{ "type": "Web", "contentrating": "Everyone" }""");
                fx.ItemRaw("1003", """{ "type": "scene" }"""); // 无 contentrating → g,而 RatingG=false → 不备份
            }, Once),

        // 上次运行被中断(备份目录里已有硬链接文件,但没有 .backup_ok)→ 本次必须补链并写标记。
        // 钉住「不完整不写标记」这条语义的另一半:标记缺失时真的会重试。
        new("B19-once-partial-backup-retries",
            fx =>
            {
                fx.ConfigStandard();
                fx.Vdf(("1001", "0"));
                fx.Item("1001", "video", "Everyone", "sub/data.bin");
                fx.HardlinkedIntoBackup("1001", "project.json");
            }, Once),

        // ---- C 组:护栏 ----
        // .we_backup 自己绝不能被当成项目备份进去
        new("C01-once-never-backs-up-backup-root",
            fx =>
            {
                fx.ConfigStandard();
                fx.Vdf(("1001", "0"));
                fx.Item("1001", "video", "Everyone");
                fx.PreExistingBackupFile("9999", "junk.bin", "leftover-from-previous-run");
            }, Once),

        // 非工坊 ID 形态的目录不参与(订阅集非空,它们也不在集里)
        new("C02-once-junk-dir-names",
            fx =>
            {
                fx.ConfigStandard();
                fx.Vdf(("1001", "0"));
                fx.Item("1001", "video", "Everyone");
                fx.ItemRaw("temp", """{ "type": "video", "contentrating": "Everyone" }""");
                fx.ItemRaw("1001.bak", """{ "type": "video", "contentrating": "Everyone" }""");
            }, Once),
    ];
}

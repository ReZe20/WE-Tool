namespace TestBackupContract;

/// <summary>
/// 契约场景表。每条都是「服务当前实际怎么做」的快照,不是「服务应该怎么做」——
/// 已知可疑行为(B08/B09/A08)照抄钉死,以便 C++ 版被逐字节验证为等价。
/// Args 里的 {ROOT} 由 Program 替换为本次场景根目录。
/// </summary>
internal sealed record Scenario(string Name, Action<Fx> Build, string Args, int Runs = 1)
{
    /// <summary>常驻场景:日志里必须出现这些子串,超时即判失败。</summary>
    public string[] Expect { get; init; } = [];
    public int ExpectTimeoutMs { get; init; } = 30_000;
    /// <summary>进程跑起来之后对文件系统动的手(模拟 Steam 订阅/下载落盘)。</summary>
    public Action<Fx>? During { get; init; }
    public int DuringDelayMs { get; init; } = 1500;
    /// <summary>Expect 全部命中后再静置这么久,然后才杀进程 —— 空转/忙轮询只有在这段里才看得见。</summary>
    public int SettleAfterMs { get; init; } = 1500;
    public bool Resident => Expect.Length > 0;

    private const string Run = "--run --data-dir \"{ROOT}\\data\"";
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
        // 探测 .NET 递归枚举的 AttributesToSkip(Hidden|System)到底作用在哪一层:
        // 隐藏文件 / 隐藏目录内的文件 / 正常文件各一,C++ 必须按基线结果复刻同一套取舍
        new("B20-once-hidden-entries",
            fx =>
            {
                fx.ConfigStandard();
                fx.Vdf(("1001", "0"));
                fx.Item("1001", "video", "Everyone", "plain.bin", "hd/inside.bin", "sub/vis.bin");
                fx.SetHiddenInItem("1001", "plain.bin");
                fx.SetHiddenInItem("1001", "hd");
                fx.SetHiddenInItem("1001", "sub/vis.bin");
            }, Once),
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

        // ---- R 组:常驻(--run)。时序噪声无法完全消除,所以渲染时折叠连续重复行;
        //      Expect 不出现判失败,多出来的行同样让基线不一致 —— 忙轮询会当场现形。
        new("R01-run-subscribe-triggers-backup",
            fx =>
            {
                fx.ConfigStandard();
                fx.Vdf(("1001", "0"));
                fx.Item("1001", "video", "Everyone");
                fx.Item("1002", "video", "Everyone");   // 启动时未订阅 → 不该被备份
            },
            Run)
        {
            During = fx => fx.Vdf(("1001", "0"), ("1002", "0")),
            DuringDelayMs = 2000,
            Expect =
            [
                "服务已启动: VDF=",
                "发现新增订阅 1 个: 1002",
                "已备份 1002: 链接 2 个,跳过 0 个",
            ],
        },

        new("R02-run-downloads-signal",
            fx =>
            {
                fx.ConfigStandard();
                fx.Vdf(("1001", "0"));
                fx.Item("1001", "video", "Everyone");
                fx.MakeDownloadsDir();
            },
            Run)
        {
            During = fx => fx.DownloadArrived("2002"),
            DuringDelayMs = 2000,
            Expect =
            [
                "服务已启动: VDF=",
                "downloads 缓存出现新目录: 2002(下载中,等待移入 content)",
            ],
        },

        // 静置 8 秒(> 5 秒轮询间隔)且不动任何文件:日志必须停在「常驻运行中」不再多一行
        new("R03-run-idle-silent",
            fx =>
            {
                fx.ConfigStandard();
                fx.Vdf(("1001", "0"));
                fx.Item("1001", "video", "Everyone");
                fx.MakeDownloadsDir();
            },
            Run)
        {
            During = _ => { },
            SettleAfterMs = 8000,
            Expect = ["AutoBackupService 常驻运行中,按 Ctrl+C 退出"],
        },

        // VDF 的父目录不存在(Steam 尚未创建 userdata\<sid>\ugc 的真实形态)。
        // C# 修之前这里直接 0xC0000409 failfast;基线钉的是「不崩、留一行日志、继续常驻」
        new("R04-run-missing-vdf-dir",
            fx =>
            {
                fx.Config("""
                    {
                      "Version": 2,
                      "Path": { "WorkshopPath": "{WS}", "VdfPath": "{ROOT}\\nodir\\431960_subscriptions.vdf" },
                      "AutoBackup": { "Enabled": true, "ServiceEnabled": true }
                    }
                    """);
                fx.Item("1001", "video", "Everyone");
            },
            Run)
        {
            During = _ => { },
            SettleAfterMs = 3000,
            Expect = ["监听目录不存在,跳过 VDF 监听:", "AutoBackupService 常驻运行中,按 Ctrl+C 退出"],
        },
    ];
}

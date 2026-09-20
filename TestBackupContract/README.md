# TestBackupContract

`AutoBackupService` 的黑盒回归测试。不停留在调用内部函数,而是真的把 `AutoBackupService.exe` 起成子进程,把它在进程边界上表现出来的一切(退出码 / stdout / 服务日志文本 / 备份目录结果)归一化成文本存进 `goldens/`,以后每次改动逐字节比对。C# → C++ 换语言时的验收依据就是它。

对 AutoBackupService 无编译期引用,不在主应用的构建/发布链路上;在解决方案里只为能在 VS 里 F5。

## 用法

```bash
# VS:把本项目设为启动项目,工具栏下拉选 profile(检查 / 指定 Release·Debug / 只跑一条 / 保留现场 / 重录 / 列场景)

dotnet run --project TestBackupContract                                  # 跑全部 34 条,等价 --check --wait
dotnet run --project TestBackupContract -- --check --only B14            # 只跑一条(前缀匹配,可 A01,B04 逗号分隔)
dotnet run --project TestBackupContract -- --check <exe路径>             # 打指定 exe,相对仓库根或绝对路径均可
dotnet run --project TestBackupContract -- --record                      # 行为确实变了且是故意的 → 重写 goldens/
```

`--check`/`--record` 的 exe 可省略,默认取 `AutoBackupService/bin/Release/AutoBackupService.exe`,其次 Debug;都没有则报错并打印构建命令(构建主应用的 `CopyAutoBackupService` 目标会顺带把服务编出来)。退出码:全一致 0,有不一致或缺基线 1。

## 一次运行的流程

1. **造环境**(`Fx.cs`):在 `%TEMP%\WEToolBackupContract-<场景名>\` 搭一套假 Steam —— `config.json`、workshop 下的假创意工坊项目(`project.json` + 视频文件 + 隐藏项 + 超长路径 + 非 ASCII 名 + 写坏的 json)、`librarycache/*.vdf` 订阅清单,以及"备份目录里已存在同名文件"这类干扰项。不读写真实 Steam 库。
2. **起进程**(`Observe.cs`):按场景传 `--verify` / `--once` / `--run`,抓退出码、stdout、stderr 与服务自己写的日志;常驻场景在运行中往假环境里加订阅或往下载目录落文件,断言日志出现/不出现指定行,超时未退出则杀掉。
3. **拍文件系统**:遍历结果目录,每个文件输出 `size=… links=… id=Gn`。硬链接是否真共享(同 id 组 + `links=2`)、不该动的文件有没有被动过,都靠这行证明。
4. **归一化后比对**:时间戳→`[TS]`,临时根路径→`{ROOT}`,文件 id→组序号,换行统一为 LF(仓库 `* text=auto`,不归一则换台机器全线假差异),连续重复日志行折叠;与 `goldens/<场景名>.txt` 逐字节比。

## 场景

```
A01–A08   配置与命令行:服务未启用 / workshop 路径空 / 无配置文件 / 未知 mode / 注释·尾逗号·BOM / 数据目录参数排最前
B01–B20   单次备份:评分与类型过滤、未订阅跳过、disabled_locally 三种写法、二次运行幂等、不覆盖外来同名目标、
          嵌套子目录、长路径、非 ASCII 名、project.json 损坏、半截备份必须重试、隐藏条目也要备份
C01–C02   不许备份的路径
R01–R04   常驻监听:订阅变化触发备份、下载落盘触发、空闲时必须一句话都不刷、VDF 目录缺失不许崩
```

## 出现 DIFF 时

- 差异是时间戳/路径/文件 id → 归一化没做到位,改 `Observe.Render`/`Norm`,不要动基线。
- 行为是故意改的 → 先说清理由再 `--record`,提交里要能看出改了哪几条、为什么。
- 说不清 → 当 bug 查。这套基线抓到过 5 个真问题:日志首行被截走、长路径下静默漏文件、只备份了一半却写 `.backup_ok`、`--run` 在 VDF 目录缺失时崩、stdout 跟随机器代码页导致基线换机即废。

## 加场景

`Scenarios.cs` 里加一条 `new Scenario("B21-…", Build: fx => …, Args: "…")`,`--record --only B21` 录基线,再 `--check --only B21` 复跑确认稳定。多次运行的场景传 `Runs`;带 `Expect` 的即常驻场景(`Resident` 由它派生),运行中改文件系统用 `During`,等待与静置时长是 `ExpectTimeoutMs`/`SettleAfterMs`。

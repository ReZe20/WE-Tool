# WE Tool

一款 Wallpaper Engine 壁纸管理工具:扫描并浏览 Steam 官方壁纸、创意工坊订阅与本地壁纸,将 `.pkg` 包完整解包为原始文件(纹理自动转换为图片/视频),并支持已安装组件(图层/脚本/特效)的批量管理。

界面基于 WinUI 3,提取后端为 [RePKG_Re](https://github.com/ReZe20/repkg-Re)(原 [repkg](https://github.com/notscuffed/repkg) 的增强分支)。

## 功能特性

- **三源扫描**:官方壁纸、创意工坊订阅、本地项目(我的壁纸),并行解析,秒级二次扫描(SQLite 缓存)
- **订阅状态校验**:自动识别「已取消订阅但仍占用磁盘」的壁纸(通过 Steam 订阅清单比对)
- **浏览与筛选**:类型(场景/视频/网页/应用/预设)、年龄分级、来源、标签多维筛选,支持排序、分页、动图预览、标注滚动条
- **一键提取**:解包 `.pkg` + TEX 纹理自动转换,支持全量输出 / 仅媒体文件 / 自定义扩展名过滤三种模式,提取过程可暂停、继续、停止,实时进度
- **组件管理**:已安装组件(图层/脚本/特效)独立页签,多选批量操作,支持快捷键(Ctrl+A 全选 / Ctrl+I 反选 / Ctrl+C 复制 / Delete 删除 / Alt+Enter 属性)
- **Steamworks 集成**:壁纸页与组件页均可一键取消创意工坊订阅;Steam 会话由独立桥接子进程持有,Steam 客户端退出不会连带关闭本应用,恢复后可一键重连
- **残留清理**:扫描并清理 Steam 取消订阅/下架后删除不彻底的遗留壁纸文件;支持白名单管理、批量清理,可设置为启动页
- **信息面板**:实时日志(分级着色)、Steamworks 状态、提取后端版本自动校验、11 语言翻译进度一览
- **多语言**:简体中文 / 繁体中文 / English / Deutsch / Español / Français / Italiano / 日本語 / 한국어 / Português (Brasil) / Русский
- **设置**:主题、启动页、路径自动检测、扫描缓存开关,配置自动迁移,支持一键重置为默认值

## 系统要求

- Windows 10 版本 2004(20H1)及以上,或 Windows 11
- 已安装 **Wallpaper Engine**(壁纸数据来源)
- 「取消订阅」功能需要 Steam 客户端登录运行;浏览、扫描、提取不需要
- 自包含版无需安装任何运行时;框架依赖版需要 [.NET 8 运行时](https://dotnet.microsoft.com/download/dotnet/8.0)

## 下载与安装

1. 前往 [Releases](https://github.com/ReZe20/WE-Tool/releases) 下载最新版本,两种产物任选:
   - **自包含版**(推荐):体积较大,自带运行库,任何满足系统要求的机器解压即用
   - **框架依赖版**:体积小,需先安装 .NET 8 运行时
2. 解压到任意目录(建议非系统盘、路径不含中文)
3. 运行目录下的 `WE_Tool.exe`(启动器,会自动拉起 `app` 目录内的主程序)

绿色软件,无需安装、不写注册表;卸载 = 删除目录。

## 快速上手

1. **首次启动**:程序会自动检测 Wallpaper Engine 相关路径(官方壁纸目录、创意工坊目录、本地项目目录),可在「设置 → 路径」中查看与修改
2. **扫描壁纸**:进入「壁纸」页,选择来源(官方 / 工坊 / 我的),点击扫描;之后再次扫描秒级完成(仅增量解析)
3. **筛选与选择**:左侧筛选面板按类型 / 分级 / 来源 / 标签缩小范围,搜索框按标题或 ID 过滤;勾选目标壁纸,支持全选、反选
4. **提取**:在「提取设置」中选择输出模式,确认输出目录后开始提取;批量提取支持并发,可随时暂停 / 继续 / 停止
5. **查看结果**:提取完成后在输出目录中查看;「信息」页可查看实时日志与 Steamworks 状态

> 提示:壁纸列表中的「已取消订阅」标记表示该壁纸已不在你的订阅清单中,但文件仍占用磁盘——可以放心提取或手动清理。

## 提取说明

### 输出模式

| 模式 | 说明 |
| --- | --- |
| 全量输出 | 解包全部条目(纹理保持 TEX 原始格式) |
| 仅输出媒体文件 | 只保留图片与视频(纹理转换为 png,场景视频提取为 mp4),适合快速备份素材 |
| 自定义 | 按扩展名白名单 / 黑名单过滤输出(如只保留 `.png` 与 `.json`) |

## 反馈与支持

- 问题与建议请提交至 [GitHub Issues](https://github.com/ReZe20/WE-Tool/issues)(应用「信息」页也内置了反馈入口)
- 提交问题时请附上版本号与 `%LOCALAPPDATA%\WE_Tool\logs\log.txt`
- 后续规划(如导入本地 pkg)以 Issues 讨论为准,欢迎参与

## 开发与构建

### 环境要求

- Visual Studio 2022(安装「使用 C++ 的桌面开发」与「Windows 应用 SDK」工作负载)
- .NET 8 SDK

### 构建

```bash
# 还原并构建主应用(自动构建 repkg 后端与 Steamworks 桥接)
dotnet build "WE Tool/WE Tool.csproj" -c Release
```

发布由 GitHub Actions 自动完成:推送 tag 即触发,生成自包含 / 框架依赖两种产物,并附带启动器、许可证与第三方组件声明。

### 项目结构

```
WE Tool/
├── WE Tool.slnx
├── README.md
├── CHANGELOG.md
├── LICENSE                     # GPL-3.0
├── THIRD-PARTY-NOTICES.txt     # 第三方组件声明(随发布产物)
├── WE Tool/                    # WinUI 3 主应用
│   ├── App.xaml(.cs)           # 应用入口(全局样式:ExternalLinkButtonStyle/CardGridStyle)
│   ├── MainWindow.xaml(.cs)    # 主窗口
│   ├── WE Tool.csproj
│   ├── Helper/
│   │   ├── WallpaperScanner.cs # Steam 壁纸扫描
│   │   ├── DialogHelper.cs     # 对话框工具
│   │   ├── JobObjectManager.cs # 子进程生命周期管理
│   │   ├── LanguageHelper.cs   # 本地化辅助
│   │   ├── MemoryHelper.cs     # 内存工具
│   │   └── UiHelper.cs         # UI 工具
│   ├── Models/
│   │   ├── AppSettings.cs      # 应用配置模型
│   │   ├── WallpaperItem.cs    # 壁纸项
│   │   ├── FileTreeItem.cs     # 文件树节点
│   │   ├── ExtractProgressItem.cs # 提取进度项
│   │   ├── Contributor.cs      # 贡献者(Info 页 CSV 加载)
│   │   ├── ComponentInfo.cs    # 组件信息
│   │   └── ComponentType.cs    # 组件类型
│   ├── Service/
│   │   ├── ConfigService.cs    # 配置读写
│   │   ├── IPickerService.cs   # 文件选取接口
│   │   ├── RepkgCliService.cs  # 子进程调用 RePKG_Re.exe + 暂停/继续/停止
│   │   └── SteamWorkshopService.cs # Steamworks 桥接管理(取消订阅/状态)
│   ├── ViewModels/
│   │   ├── SettingsViewModel.cs
│   │   ├── App/                # 子 VM
│   │   ├── Display/            # 子 VM
│   │   ├── Filter/             # 子 VM
│   │   └── Path/               # 路径/命令 VM
│   ├── Views/
│   │   ├── Papers.xaml(.cs)    # 壁纸列表 & 提取面板
│   │   ├── Settings.xaml(.cs)  # 设置页
│   │   ├── Info.xaml(.cs)      # 关于页(WE Tool / RePKG_Re 双 expander)
│   │   ├── InstalledComponents.xaml(.cs) # 组件管理
│   │   ├── LoadPapers.xaml(.cs) # 导入解包
│   │   ├── Cleanup.xaml(.cs)    # 残留清理
│   │   └── WhitelistWindow.xaml(.cs) # 白名单管理窗口
│   ├── Controls/
│   │   └── ContributorCard.xaml(.cs) # 贡献者卡片(头像/名字/链接)
│   ├── Converters/
│   ├── Strings/                # 本地化资源(11 语言)
│   │   ├── zh-CN/ en-US/ de-DE/ es-ES/ fr-FR/ it-IT/ ja-JP/ ko-KR/ pt-BR/ ru-RU/ zh-TW/
│   └── Assets/                 # 应用图标 + Contributors.csv / ContributorsRepkg.csv
├── WE_Tool.Launcher/           # 启动器(.NET Framework 4.7.2 控制台,自动拉起 app\WE_Tool.exe)
├── SteamworksBridge/           # Steamworks 桥接子进程(Steam 退出时会关闭游戏 AppID 进程,被杀的是它,主应用存活)
└── external/
    └── repkg_Re/               # 独立 git 仓库:RePKG_Re(ReZe20 分支)
        ├── RePKG_Re.sln
        ├── RePKG_Re/           # 控制台 exe(输出 RePKG_Re.exe)
        ├── RePKG_Re.Core/      # .pkg 包解析核心
        ├── RePKG_Re.Application/ # 纹理解码 & 图片转换
        ├── RePKG_Re.Tests/
        ├── RePKG/              # 原版 RePKG(仅参考)
        ├── RePKG.Core/
        ├── RePKG.Application/
        ├── RePKG.Tests/
        └── publish/            # 发布输出(单文件 exe + THIRD-PARTY-NOTICES.txt)
```

## License

[GPL-3.0](LICENSE) © 2026 ReZe20

本工具依赖的第三方组件(如 repkg_Re、CommunityToolkit、Facepunch.Steamworks 等)按各自协议保留版权与许可声明,详见发布产物中的 THIRD-PARTY-NOTICES.txt。

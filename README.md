# WE Tool

## 项目结构

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
│   │   └── SteamWorkshopService.cs # Steam 创意工坊
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
│   │   └── LoadPapers.xaml(.cs)
│   ├── Controls/
│   │   └── ContributorCard.xaml(.cs) # 贡献者卡片(头像/名字/链接)
│   ├── Converters/
│   ├── Strings/                # 本地化资源(11 语言)
│   │   ├── zh-CN/ en-US/ de-DE/ es-ES/ fr-FR/ it-IT/ ja-JP/ ko-KR/ pt-BR/ ru-RU/ zh-TW/
│   └── Assets/                 # 应用图标 + Contributors.csv / ContributorsRepkg.csv
├── WE_Tool.Launcher/           # 启动器(.NET Framework 4.7.2 控制台)
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

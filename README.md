# WE Tool

## 项目结构

```
WE Tool/
├── WE Tool.slnx
├── README.md
├── WE Tool/                    # WinUI 3 主应用
│   ├── App.xaml(.cs)           # 应用入口
│   ├── MainWindow.xaml(.cs)    # 主窗口
│   ├── WE Tool.csproj
│   ├── Helper/
│   │   ├── WallpaperScanner.cs # Steam 壁纸扫描
│   │   ├── DialogHelper.cs     # 对话框工具
│   │   ├── JobObjectManager.cs # 子进程生命周期管理
│   │   └── LanguageHelper.cs   # 本地化辅助
│   ├── Models/
│   │   ├── AppSettings.cs      # 应用配置模型
│   │   ├── WallpaperItem.cs    # 壁纸项
│   │   ├── FileTreeItem.cs     # 文件树节点
│   │   └── ExtractProgressItem.cs # 提取进度项
│   ├── Service/
│   │   ├── ConfigService.cs    # 配置读写
│   │   ├── IPickerService.cs   # 文件选取接口
│   │   └── RepkgCliService.cs  # 子进程调用 RePKG_Re.exe + 暂停/继续/停止
│   ├── ViewModels/
│   │   ├── SettingsViewModel.cs
│   │   ├── App/
│   │   ├── Display/
│   │   ├── Filter/
│   │   └── Path/
│   ├── Views/
│   │   ├── Papers.xaml(.cs)    # 壁纸列表 & 提取面板
│   │   ├── Settings.xaml(.cs)  # 设置页
│   │   ├── Info.xaml(.cs)      # 关于页
│   │   └── LoadPapers.xaml(.cs)
│   ├── Converters/
│   ├── Strings/                # 本地化资源
│   │   ├── en-US/
│   │   └── zh-CN/
│   └── Assets/
└── external/
    └── repkg_Re/               # 独立 git 仓库：RePKG_Re（ReZe20 分支）
        ├── RePKG_Re.sln
        ├── RePKG_Re/           # 控制台 exe（输出 RePKG_Re.exe）
        ├── RePKG_Re.Core/      # .pkg 包解析核心
        ├── RePKG_Re.Application/ # 纹理解码 & 图片转换
        ├── RePKG_Re.Tests/
        ├── RePKG/              # 原版 RePKG（仅参考）
        ├── RePKG.Core/
        └── RePKG.Application/
```

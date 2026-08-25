; WE Tool 安装包脚本(Inno Setup 6)
; 源 = bin/Package 下已定稿的未压缩发布文件夹(zip 的内容,不再二次加工)
; 产物 = 单个 WE-Tool-<版本>-win-x64-setup.exe(固实 LZMA2 最高压缩)
; 版本号由 csproj 的 PackageInstaller 目标经 /D 参数注入,本地直接编译时回退 0.0.0

#define AppName "WE Tool"
#define AppExeName "WE_Tool.exe"
#ifndef AppVersion
#define AppVersion "0.0.0"
#endif

[Setup]
AppId={{5D5467BA-2504-43EE-B07E-96BF5427FCFD}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=ReZe20
AppPublisherURL=https://github.com/ReZe20/WE-Tool
; 许可证页:展示仓库根目录的 GPLv3 全文(路径相对本脚本所在目录)
LicenseFile=..\LICENSE
DefaultDirName={autopf}\WE Tool
; 用户级安装:不弹 UAC,默认装到 %LOCALAPPDATA%\Programs\WE Tool
PrivilegesRequired=lowest
; 用户级安装时这两页默认被隐藏,显式打开:可选安装位置 + 可选开始菜单文件夹
DisableDirPage=no
DisableProgramGroupPage=no
DefaultGroupName={#AppName}
; 开始菜单文件夹页附"不创建开始菜单文件夹"勾选框(选了则只建程序组外的必要项)
AllowNoIcons=yes
; 程序自带卸载入口,开始菜单文件夹用完即删
Uninstallable=yes
CloseApplications=no
RestartApplications=no
; 固实 LZMA2 最高压缩:安装是一次性动作,时间换体积
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
LZMADictionarySize=65536
LZMANumBlockThreads=1
ArchitecturesInstallIn64BitMode=x64compatible
; setup.exe 外观暂用默认图标(仓库无 .ico 资源)
WizardStyle=modern
OutputDir=bin
OutputBaseFilename=WE-Tool-{#AppVersion}-win-x64-setup

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Messages]
; 随仓库的中文翻译文件较老,缺开始菜单页"不创建文件夹"勾选框的官方消息文案,在此覆盖补充
NoIcons=不创建开始菜单文件夹(&W)

[Files]
; 整目录递归打包(含 app\ 子目录与 repkg),忽略仅存在于本地的残留文件
Source: "{#SourceRoot}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
; 开始菜单项由 AllowNoIcons 的"不创建开始菜单文件夹"勾选框控制:勾选则全部跳过
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
; 可选桌面快捷方式(默认勾选)
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Tasks]
; 可选桌面快捷方式(默认勾选)
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Run]
; 安装完成页可选直接启动
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时清掉运行期可能生成的空目录残留
Type: filesandordirs; Name: "{app}\repkg"

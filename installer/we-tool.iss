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
; 整目录递归打包(含 repkg 等子目录),忽略仅存在于本地的残留文件
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

[Code]
{ 卸载器:可选删除用户数据与配置。
  安装版数据落在 %LOCALAPPDATA%\WE_Tool(主程序 GetAppDataRoot 非便携分支)。
  卸载进度窗体上提供勾选框,勾选则卸载完成后一并删除该目录(含 config.json / logs / 缓存 / 白名单)。
  仅删除确切路径,不做通配,且确认目标名=WE_Tool 才删(防误删)。

  注意:不可在 InitializeUninstall 里访问 WizardForm/UninstallProgressForm(此时未创建,会空指针崩)。
  勾选框在 InitializeUninstallProgressForm(卸载进度窗体创建后)里挂到 UninstallProgressForm。 }

var
  UninstallDeleteDataCheck: TNewCheckBox;

function InitializeUninstall(): Boolean;
begin
  Result := True;   { 继续卸载 }
end;

procedure InitializeUninstallProgressForm();
begin
  { 在卸载进度窗体上追加勾选框(默认不勾,用户主动选才删数据)。
    进度窗体创建后触发,此时 UninstallProgressForm 可用。
    位置:取消按钮上方,避免遮挡按钮/进度条 }
  UninstallDeleteDataCheck := TNewCheckBox.Create(UninstallProgressForm);
  UninstallDeleteDataCheck.Parent := UninstallProgressForm;
  UninstallDeleteDataCheck.Top := UninstallProgressForm.CancelButton.Top - ScaleY(32);
  UninstallDeleteDataCheck.Left := ScaleX(8);
  UninstallDeleteDataCheck.Width := UninstallProgressForm.ClientWidth - ScaleX(16);
  UninstallDeleteDataCheck.Height := ScaleY(24);
  UninstallDeleteDataCheck.Caption := '同时删除用户数据与配置(日志、缓存、白名单等,保存在 %LOCALAPPDATA%\WE_Tool)';
  UninstallDeleteDataCheck.Checked := False;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if UninstallDeleteDataCheck <> nil then
    begin
      if UninstallDeleteDataCheck.Checked then
      begin
        { 精确计算数据目录(与主程序 GetAppDataRoot 非便携分支一致:%LOCALAPPDATA%\WE_Tool) }
        DataDir := ExpandConstant('{localappdata}\WE_Tool');
        { 防护:目标目录名必须确实是 WE_Tool 才删,避免路径解析意外(如 localappdata 为空) }
        if (DataDir <> '') and (ExtractFileName(DataDir) = 'WE_Tool') then
        begin
          if DelTree(DataDir, True, True, True) then
            MsgBox('已删除用户数据与配置。', mbInformation, MB_OK)
          else
            MsgBox('用户数据删除失败(部分文件可能被占用)。', mbError, MB_OK);
        end;
      end;
    end;
  end;
end;

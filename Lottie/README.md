# Lottie 动画图标素材（AnimatedVisuals 的生成源）

这里是 `WE Tool/AnimatedVisuals/*.cs` 的**原始素材**（After Effects + Bodymovin 导出的 json）。
`json → .cs` 是单向转换：`.cs` 里只剩翻译后的 composition 常量，反推不出 json（图层分组、标记名、缓动曲线原值都会丢），
所以素材必须随代码一起保存。

放在仓库根的 `Lottie/`（与 `installer/`、`native/` 同级），**不要**放进 `WE Tool/Assets/`：
SDK 会把项目下 `Assets\**` 自动作为内容拷进输出目录，素材就会被带进发布包。这些 json 只是手工生成时的输入，运行时不需要。

## 重新生成

对某个 json 执行（与仓库内 `.cs` 头部 `Command:` 记录一致）：

```
set DOTNET_ROLL_FORWARD=LatestMajor
LottieGen -Language CSharp -Namespace WE_Tool.AnimatedVisuals -Public -WinUIVersion 3.0 -InputFile <路径>\<名字>.json -OutputFolder "<仓库>\WE Tool\AnimatedVisuals"
```

- 工具：全局 dotnet 工具 `lottiegen`（当前 8.2.250604，NuGet 上最高版本；它要 .NET 9，本机只有 8/10，故必须 `DOTNET_ROLL_FORWARD=LatestMajor`）。
- **json 文件名必须等于类名**：LottieGen 拿文件名当生成的类名，`selectall.json` 会生成类 `selectall`。所以本目录统一 PascalCase 且与 `AnimatedVisuals/<类名>.cs` 同名，这不是风格偏好而是硬约束。
- 素材规则（漏了会静默丢动画）：状态标记只能在 AE 里用 `Shift+数字` 打合成标记；空心图形必须画成"中心线路径 + 描边"，带关键帧的组不允许"填充 + 挖孔"（LT0005）。

## 生成后必做：补 `partial`

LottieGen 的 C# 模板把类声明写死成 `sealed class`（上游 `CSharpInstantiatorGenerator.cs:263/688`，无任何开关）。
实现 WinRT 接口的类若不为 `partial`，CsWinRT 不会生成 CCW 互操作桩，**NativeAOT 发布包会在窗口加载时崩溃**
（`0xc000027b`，WER 指向 `Microsoft.UI.Xaml.dll`；Debug 不是 AOT 所以毫无症状）。生成后立即在仓库根执行：

```
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem 'WE Tool\AnimatedVisuals\*.cs' | ForEach-Object { $t=[IO.File]::ReadAllText($_.FullName); $n=$t -creplace '\bpublic sealed class\b','public sealed partial class' -creplace '(?m)^(\s+)sealed class\b','$1sealed partial class'; if($n -ne $t){[IO.File]::WriteAllText($_.FullName,$n,(New-Object Text.UTF8Encoding($true)))} }"
```

验收：`dotnet build "WE Tool/WE Tool.csproj" -c Debug -p:Platform=x64` 里 `CsWinRT1028` 必须 0 条。

## 已入库素材（11，均验证过"重生成 == 仓库内 .cs"逐字节一致）

`CopyIcon` `DeleteIcon` `DetailPanelToggleIcon` `ExtractIcon` `ImportToEditorIcon` `InvertSelection`
`OpenDirectoryIcon` `PropertiesIcon` `RefreshIcon` `SelectAllIcon` `ViewIcon`

## 源已丢失的图标（9，本机找不到同名同尺寸文件）

下表是每个 `.cs` 头部记录的输入文件名/字节数/生成时刻，日后若在任何备份里看到同尺寸同名文件即可认领：

| 类名 | 记录输入 | 字节 | 生成时刻 |
|---|---|---|---|
| InfoIcon | InfoIcon.json | 6504 | 2026-09-16 21:26 |
| InstalledComponentsIcon | InstalledComponentsIcon.json | 10521 | 2026-09-16 10:58 |
| LoadPapersIcon | LoadPapersIcon.json | 6228 | 2026-09-16 11:28 |
| LogsIcon | LogsIcon.json | 5676 | 2026-09-16 17:47 |
| PapersIcon | PapersIcon.json | 8718 | 2026-09-15 11:09 |
| SortDirectionAscIcon | SortDirectionAscIcon.json | 1781 | 2026-09-17 13:01 |
| SortDirectionDescIcon | SortDirectionDescIcon.json | 1784 | 2026-09-17 13:01 |
| SortIcon | SortIcon.json | 7060 | 2026-09-16 23:08 |
| WallpaperBackupIcon | WallpaperBackupIcon.json | 9358 | 2026-09-16 13:19 |

## 未入库的稿子

`C:\Users\lijun\Documents\JSON`（含 `papers\`）里的 14 份 json 与上表逐字节比对**全部不命中**，多为补标记之前的早期稿子
（例：`selectall.json` 15,867B @09-18 22:58，实际入包的是 `SelectAllIcon.json` 58,190B @09-18 23:08）。
入库会误导"这就是源"，故不放进来。

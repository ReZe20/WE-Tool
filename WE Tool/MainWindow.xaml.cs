using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Serilog;
using System;
using System.Collections;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Json;
using WE_Tool.Service;
using WE_Tool.ViewModels;
using WE_Tool.Views;
using Windows.Graphics;
using Windows.UI;
using WinUIEx;

// To learndata:image/svg+xml;base64,PD94bWwgdmVyc2lvbj0iMS4wIiBzdGFuZGFsb25lPSJubyI/PjwhRE9DVFlQRSBzdmcgUFVCTElDICItLy9XM0MvL0RURCBTVkcgMS4xLy9FTiIgImh0dHA6Ly93d3cudzMub3JnL0dyYXBoaWNzL1NWRy8xLjEvRFREL3N2ZzExLmR0ZCI+PHN2ZyB0PSIxNTgxNDkxOTQyMjQzIiBjbGFzcz0iaWNvbiIgdmlld0JveD0iMCAwIDEwMjQgMTAyNCIgdmVyc2lvbj0iMS4xIiB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHAtaWQ9IjQ1NzUiIHhtbG5zOnhsaW5rPSJodHRwOi8vd3d3LnczLm9yZy8xOTk5L3hsaW5rIiB3aWR0aD0iMzIiIGhlaWdodD0iMzIiPjxkZWZzPjxzdHlsZSB0eXBlPSJ0ZXh0L2NzcyI+PC9zdHlsZT48L2RlZnM+PHBhdGggZD0iTTU4My4xNjggNTIzLjc3Nkw5NTguNDY0IDE0OC40OGMxOC45NDQtMTguOTQ0IDE4Ljk0NC01MC4xNzYgMC02OS4xMmwtMi4wNDgtMi4wNDhjLTE4Ljk0NC0xOC45NDQtNTAuMTc2LTE4Ljk0NC02OS4xMiAwTDUxMiA0NTMuMTIgMTM2LjcwNCA3Ny4zMTJjLTE4Ljk0NC0xOC45NDQtNTAuMTc2LTE4Ljk0NC02OS4xMiAwbC0yLjA0OCAyLjA0OGMtMTkuNDU2IDE4Ljk0NC0xOS40NTYgNTAuMTc2IDAgNjkuMTJsMzc1LjI5NiAzNzUuMjk2TDY1LjUzNiA4OTkuMDcyYy0xOC45NDQgMTguOTQ0LTE4Ljk0NCA1MC4xNzYgMCA2OS4xMmwyLjA0OCAyLjA0OGMxOC45NDQgMTguOTQ0IDUwLjE3NiAxOC45NDQgNjkuMTIgMEw1MTIgNTk0Ljk0NCA4ODcuMjk2IDk3MC4yNGMxOC45NDQgMTguOTQ0IDUwLjE3NiAxOC45NDQgNjkuMTIgMGwyLjA0OC0yLjA0OGMxOC45NDQtMTguOTQ0IDE4Ljk0NC01MC4xNzYgMC02OS4xMkw1ODMuMTY4IDUyMy43NzZ6IiBwLWlkPSI0NTc2IiBmaWxsPSIjZmZmZmZmIj48L3BhdGg+PC9zdmc+ more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WE_Tool
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : WindowEx
    {
        public SettingsViewModel ViewModel { get; }
        private readonly IConfigService _configService = new ConfigService();
        public MainWindow()
        {
            var app = Application.Current as App;
            ViewModel = app?.ViewModel ?? new SettingsViewModel(new ConfigService(), new PickerService());
            InitializeComponent();
            this.ExtendsContentIntoTitleBar = true;
            // [2026-09] Tall 标题栏:按钮与内容条同高。跨 DPI 拖动窗口时系统按钮高度(物理px)与
            // XAML 内容条(DIP)可能差 1-2px——见 SyncTitleBarRowToCaptionButtons(DPI 变化时校准)。
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            // DPI 变化(跨屏拖动/系统缩放变更)时校准标题栏内容行高度——注意 XamlRoot 构造时未就绪,
            // Changed 订阅放到 MainWindow_Activated(首次激活后 XamlRoot 可用)里
            this.Activated += MainWindow_Activated;
            // 焦点跟踪:提取等后台事件仅在主窗口无焦点时弹系统通知(常驻,区别于一次性启动导航的 MainWindow_Activated)
            this.Activated += MainWindow_FocusChanged;
            this.AppWindow.Changed += OnAppWindowChanged;
            // 导航栏 Info 项徽标实时反映 Steamworks 状态(桥接进程事件驱动,不依赖 Info 页轮询)
            SteamWorkshopService.StatusChanged += OnSteamworksStatusChanged;
            UpdateSteamStatusBadge();

            // 导航项数量徽标:页面提取开始/进度/完成时经 NavBadgeService 更新
            NavBadgeService.BadgeChanged += OnNavBadgeChanged;
            PapersNavIcon_WirePointer();   // [导航项图标动画 2026-09] 见下方 PapersNavItem_PointerPressed/Released
            InstalledComponentsNavIcon_WirePointer();   // [导航项图标动画 2026-09] 见下方 InstalledComponentsNavItem_PointerPressed/Released
            LoadPapersNavIcon_WirePointer();   // [导航项图标动画 2026-09] 见下方 LoadPapersNavItem_PointerPressed/Released
            WallpaperBackupNavIcon_WirePointer();   // [导航项图标动画 2026-09] 见下方 WallpaperBackupNavItem_PointerPressed/Released
            LogsNavIcon_WirePointer();   // [导航项图标动画 2026-09] 见下方 LogsNavItem_PointerPressed/Released
            CleanupNavIcon_WirePointer();   // [导航项图标动画 2026-09] 见下方 CleanupNavItem_PointerPressed/Released
            InfoNavIcon_WirePointer();   // [导航项图标动画 2026-09] 见下方 InfoNavItem_PointerPressed/Released
        }

        // ===================== Papers 导航项图标动画(2026-09) =====================
        // 导航项图标由静态字形 E8B9 换成 Lottie 动画(素材 = WE_Tool.AnimatedVisuals.PapersIcon,
        // 见 AnimatedVisuals/PapersIcon.cs;回退字形仍是 E8B9)。
        // 素材内容(2026-09-16 换新版):合成时间轴只有 20 帧(0.33s),两块图案第 0→10 帧靠拢、第 10→20 帧复位;
        // 首帧与第 20 帧姿态逐值相同,静止态外观不变(形状与关键帧值均未改)。
        // 素材标记[重要]:NavigationViewItem 会自己给 AnimatedIcon 设 PointerOver/Pressed/Selected 等状态,
        // 而 AnimatedIcon 的切换规则是"先跳到目标标记对的起始帧,再播到结束帧";标记缺失时会走兜底
        // ("硬切到某个位置")——那样按下就不是动画、而是直接跳到第 20 帧。所以素材里把六种状态之间
        // 全部 30 个转移都补齐了:进入按下态 = 第 0→10 帧,离开按下态 = 第 10→20 帧,其余 = 停在静止姿态。
        // 交互:鼠标按下 → 播前 10 帧;松开 → 从第 10 帧继续播完并复位。
        // [为什么还在这里排队]点一下时"按下→松开"只隔几十毫秒,而状态切换固定从第 10 帧开始播,
        // 会看出"跳到第 10 帧"。所以让每一段都完整播完再切:点一下看到的是靠拢(0.17s)+ 复位(0.17s)。
        private const int PapersNavPressMs = 167;     // 第 0→10 帧(10 帧 @60fps)
        private const int PapersNavReleaseMs = 167;   // 第 10→20 帧(10 帧 @60fps;新版素材时间轴只有 20 帧)
        private const bool PapersNavIconAnimationProbe = true;   // false = 回到"静止图标"(不播动画)
        private bool _papersNavPressed;               // 当前是否已切到"按下"态
        private long _papersNavBusyUntil;             // 当前片段预计播完的时刻(0=空闲)
        private CancellationTokenSource? _papersNavCts;

        private void PapersNavIcon_WirePointer()
        {
            // NavigationViewItem 自己会处理 PointerPressed 做选中/按下视觉,事件被标记 Handled,
            // 普通 XAML 挂法收不到,必须 handledEventsToo: true。
            PapersNavItem.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PapersNavItem_PointerPressed), true);
            PapersNavItem.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(PapersNavItem_PointerReleased), true);
            PapersNavItem.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(PapersNavItem_PointerReleased), true);   // 拖动/丢捕获也要归位
        }

        private void PapersNavItem_PointerPressed(object sender, PointerRoutedEventArgs e) => PapersNavSwitchAsync(pressed: true);
        private void PapersNavItem_PointerReleased(object sender, PointerRoutedEventArgs e) => PapersNavSwitchAsync(pressed: false);

        /// <summary>按下→播第 0→10 帧;松开→从第 10 帧继续播到第 20 帧(复位)。</summary>
        private async void PapersNavSwitchAsync(bool pressed)
        {
            if (!PapersNavIconAnimationProbe || PapersNavIcon is null) return;
            if (pressed == _papersNavPressed) return;   // 目标态 = 当前态
            var current = PapersNavIcon.GetValue(AnimatedIcon.StateProperty) as string;
            if (!pressed && !string.Equals(current, "Pressed", StringComparison.Ordinal))
            {
                // 导航项自己已经把状态切走了(它会在松开时设 PointerOverSelected/Selected 等),
                // 那一段动画它已经在播,这里不插手,免得"跳一下"
                _papersNavPressed = false;
                return;
            }
            _papersNavPressed = pressed;
            _papersNavCts?.Cancel();
            var cts = new CancellationTokenSource();
            _papersNavCts = cts;
            long wait = _papersNavBusyUntil - Environment.TickCount64;
            if (wait > 0)
            {
                try { await Task.Delay((int)wait, cts.Token); }
                catch (TaskCanceledException) { return; }   // 期间又点了一次,交给新的一次
            }
            if (cts.IsCancellationRequested) return;
            _papersNavBusyUntil = Environment.TickCount64 + (pressed ? PapersNavPressMs : PapersNavReleaseMs);
            Log.Information("[动画] Papers 导航项图标状态切换 → {State}", pressed ? "Pressed(第 0→10 帧)" : "Normal(第 10→20 帧)");
            AnimatedIcon.SetState(PapersNavIcon, pressed ? "Pressed" : "Normal");
        }

        // ===================== 已安装组件 导航项图标动画(2026-09) =====================
        // 导航项图标由静态字形 F4A5 换成 Lottie 动画(素材 = WE_Tool.AnimatedVisuals.InstalledComponentsIcon,
        // 见 AnimatedVisuals/InstalledComponentsIcon.cs;回退字形仍是 F4A5)。
        // 素材内容(2026-09-16 换新版):合成时间轴只有 20 帧(0.33s),两块图案第 0→10 帧外扩、
        // 第 10→20 帧归位;首帧与第 20 帧姿态逐值相同,静止态外观不变(形状与关键帧值均未改)。
        // 标记[重要]同 PapersIcon:NavigationViewItem 会自己设 PointerOver/Pressed/Selected 等状态,
        // 而 AnimatedIcon 切换是"先跳到目标标记对的起始帧再播到结束帧",标记缺失会走硬切兜底 ——
        // 所以素材里六种状态之间全部 30 个转移都补齐:进入按下态 = 第 0→10 帧,离开按下态 = 第 10→20 帧。
        // 交互:鼠标按下 → 播前 10 帧;松开 → 从第 10 帧继续播完并复位(每段播完再切,避免"跳一下")。
        private const int InstalledComponentsNavPressMs = 167;     // 第 0→10 帧(10 帧 @60fps)
        private const int InstalledComponentsNavReleaseMs = 167;   // 第 10→20 帧(10 帧 @60fps;新版素材时间轴只有 20 帧)
        private const bool InstalledComponentsNavIconAnimationProbe = true;   // false = 回到"静止图标"(不播动画)
        private bool _installedComponentsNavPressed;    // 当前是否已切到"按下"态
        private long _installedComponentsNavBusyUntil;  // 当前片段预计播完的时刻(0=空闲)
        private CancellationTokenSource? _installedComponentsNavCts;

        private void InstalledComponentsNavIcon_WirePointer()
        {
            // NavigationViewItem 自己会处理 PointerPressed 做选中/按下视觉,事件被标记 Handled,
            // 普通 XAML 挂法收不到,必须 handledEventsToo: true。
            InstalledComponentsNavItem.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(InstalledComponentsNavItem_PointerPressed), true);
            InstalledComponentsNavItem.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(InstalledComponentsNavItem_PointerReleased), true);
            InstalledComponentsNavItem.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(InstalledComponentsNavItem_PointerReleased), true);   // 拖动/丢捕获也要归位
        }

        private void InstalledComponentsNavItem_PointerPressed(object sender, PointerRoutedEventArgs e) => InstalledComponentsNavSwitchAsync(pressed: true);
        private void InstalledComponentsNavItem_PointerReleased(object sender, PointerRoutedEventArgs e) => InstalledComponentsNavSwitchAsync(pressed: false);

        /// <summary>按下→播第 0→10 帧;松开→从第 10 帧继续播到第 20 帧(复位)。</summary>
        private async void InstalledComponentsNavSwitchAsync(bool pressed)
        {
            if (!InstalledComponentsNavIconAnimationProbe || InstalledComponentsNavIcon is null) return;
            if (pressed == _installedComponentsNavPressed) return;   // 目标态 = 当前态
            var current = InstalledComponentsNavIcon.GetValue(AnimatedIcon.StateProperty) as string;
            if (!pressed && !string.Equals(current, "Pressed", StringComparison.Ordinal))
            {
                // 导航项自己已经把状态切走了(它会在松开时设 PointerOverSelected/Selected 等),
                // 那一段动画它已经在播,这里不插手,免得"跳一下"
                _installedComponentsNavPressed = false;
                return;
            }
            _installedComponentsNavPressed = pressed;
            _installedComponentsNavCts?.Cancel();
            var cts = new CancellationTokenSource();
            _installedComponentsNavCts = cts;
            long wait = _installedComponentsNavBusyUntil - Environment.TickCount64;
            if (wait > 0)
            {
                try { await Task.Delay((int)wait, cts.Token); }
                catch (TaskCanceledException) { return; }   // 期间又点了一次,交给新的一次
            }
            if (cts.IsCancellationRequested) return;
            _installedComponentsNavBusyUntil = Environment.TickCount64 + (pressed ? InstalledComponentsNavPressMs : InstalledComponentsNavReleaseMs);
            Log.Information("[动画] 已安装组件 导航项图标状态切换 → {State}", pressed ? "Pressed(第 0→10 帧)" : "Normal(第 10→20 帧)");
            AnimatedIcon.SetState(InstalledComponentsNavIcon, pressed ? "Pressed" : "Normal");
        }

        // ===================== 导入壁纸 导航项图标动画(2026-09) =====================
        // 导航项图标由静态字形 E8B5 换成 Lottie 动画(素材 = WE_Tool.AnimatedVisuals.LoadPapersIcon,
        // 见 AnimatedVisuals/LoadPapersIcon.cs;回退字形仍是 E8B5)。
        // 素材内容(2026-09-16 换新版):合成时间轴只有 20 帧(0.33s),一块图案第 0→10 帧靠拢、第 10→20 帧复位;
        // 首帧与第 20 帧姿态逐值相同,静止态外观不变;三个导航项图标现在都是 20 帧时间轴与同一套标记方案。
        // 标记[重要]:NavigationViewItem 会自己设 PointerOver/Pressed/Selected 等状态,
        // 而 AnimatedIcon 切换是"先跳到目标标记对的起始帧再播到结束帧",标记缺失会走硬切兜底 ——
        // 所以素材里六种状态之间全部 30 个转移都补齐:进入按下态 = 第 0→10 帧,离开按下态 = 第 10→20 帧。
        // 交互(用户口径):鼠标按下 → 播到第 10 帧;松开 → 从第 10 帧继续播完并复位(每段播完再切,避免"跳一下")。
        private const int LoadPapersNavPressMs = 167;     // 第 0→10 帧(10 帧 @60fps)
        private const int LoadPapersNavReleaseMs = 167;   // 第 10→20 帧(10 帧 @60fps;新版素材时间轴只有 20 帧)
        private const bool LoadPapersNavIconAnimationProbe = true;   // false = 回到"静止图标"(不播动画)
        private bool _loadPapersNavPressed;    // 当前是否已切到"按下"态
        private long _loadPapersNavBusyUntil;  // 当前片段预计播完的时刻(0=空闲)
        private CancellationTokenSource? _loadPapersNavCts;

        private void LoadPapersNavIcon_WirePointer()
        {
            // NavigationViewItem 自己会处理 PointerPressed 做选中/按下视觉,事件被标记 Handled,
            // 普通 XAML 挂法收不到,必须 handledEventsToo: true。
            LoadPapersNavItem.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(LoadPapersNavItem_PointerPressed), true);
            LoadPapersNavItem.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(LoadPapersNavItem_PointerReleased), true);
            LoadPapersNavItem.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(LoadPapersNavItem_PointerReleased), true);   // 拖动/丢捕获也要归位
        }

        private void LoadPapersNavItem_PointerPressed(object sender, PointerRoutedEventArgs e) => LoadPapersNavSwitchAsync(pressed: true);
        private void LoadPapersNavItem_PointerReleased(object sender, PointerRoutedEventArgs e) => LoadPapersNavSwitchAsync(pressed: false);

        /// <summary>按下→播第 0→10 帧;松开→从第 10 帧继续播到第 20 帧(复位)。</summary>
        private async void LoadPapersNavSwitchAsync(bool pressed)
        {
            if (!LoadPapersNavIconAnimationProbe || LoadPapersNavIcon is null) return;
            if (pressed == _loadPapersNavPressed) return;   // 目标态 = 当前态
            var current = LoadPapersNavIcon.GetValue(AnimatedIcon.StateProperty) as string;
            if (!pressed && !string.Equals(current, "Pressed", StringComparison.Ordinal))
            {
                // 导航项自己已经把状态切走了(它会在松开时设 PointerOverSelected/Selected 等),
                // 那一段动画它已经在播,这里不插手,免得"跳一下"
                _loadPapersNavPressed = false;
                return;
            }
            _loadPapersNavPressed = pressed;
            _loadPapersNavCts?.Cancel();
            var cts = new CancellationTokenSource();
            _loadPapersNavCts = cts;
            long wait = _loadPapersNavBusyUntil - Environment.TickCount64;
            if (wait > 0)
            {
                try { await Task.Delay((int)wait, cts.Token); }
                catch (TaskCanceledException) { return; }   // 期间又点了一次,交给新的一次
            }
            if (cts.IsCancellationRequested) return;
            _loadPapersNavBusyUntil = Environment.TickCount64 + (pressed ? LoadPapersNavPressMs : LoadPapersNavReleaseMs);
            Log.Information("[动画] 导入壁纸 导航项图标状态切换 → {State}", pressed ? "Pressed(第 0→10 帧)" : "Normal(第 10→20 帧)");
            AnimatedIcon.SetState(LoadPapersNavIcon, pressed ? "Pressed" : "Normal");
        }

        // ===================== 壁纸备份 导航项图标动画(2026-09) =====================
        // 导航项图标由静态字形 F738 换成 Lottie 动画(素材 = WE_Tool.AnimatedVisuals.WallpaperBackupIcon,
        // 见 AnimatedVisuals/WallpaperBackupIcon.cs;回退字形仍是 F738)。
        // 素材内容(2026-09-16 再换新版):时钟图标(时针/分针/表盘外轮廓各自旋转),合成时间轴 40 帧(0.67s),
        // 整圈匀速转完;外轮廓第 0→10 帧缩到 80%、第 10→30 帧胀回 100%(按下缩小的反馈);
        // 第 40 帧与第 0 帧姿态逐值相同,静止态外观不变。
        // 标记[重要]:NavigationViewItem 会自己设 PointerOver/Pressed/Selected 等状态,
        // 而 AnimatedIcon 切换是"先跳到目标标记对的起始帧再播到结束帧",标记缺失会走硬切兜底 ——
        // 所以素材里六种状态之间全部 30 个转移都补齐:进入按下态 = 第 0→10 帧,离开按下态 = 第 10→40 帧。
        // 交互(用户口径):鼠标按下 → 播到第 10 帧;松开 → 从第 10 帧继续播完并复位(每段播完再切,避免"跳一下")。
        private const int WallpaperBackupNavPressMs = 167;     // 第 0→10 帧(10 帧 @60fps)
        private const int WallpaperBackupNavReleaseMs = 500;   // 第 10→40 帧(30 帧 @60fps)
        private const bool WallpaperBackupNavIconAnimationProbe = true;   // false = 回到"静止图标"(不播动画)
        private bool _wallpaperBackupNavPressed;    // 当前是否已切到"按下"态
        private long _wallpaperBackupNavBusyUntil;  // 当前片段预计播完的时刻(0=空闲)
        private CancellationTokenSource? _wallpaperBackupNavCts;

        private void WallpaperBackupNavIcon_WirePointer()
        {
            // NavigationViewItem 自己会处理 PointerPressed 做选中/按下视觉,事件被标记 Handled,
            // 普通 XAML 挂法收不到,必须 handledEventsToo: true。
            WallpaperBackupNavItem.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(WallpaperBackupNavItem_PointerPressed), true);
            WallpaperBackupNavItem.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(WallpaperBackupNavItem_PointerReleased), true);
            WallpaperBackupNavItem.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(WallpaperBackupNavItem_PointerReleased), true);   // 拖动/丢捕获也要归位
        }

        private void WallpaperBackupNavItem_PointerPressed(object sender, PointerRoutedEventArgs e) => WallpaperBackupNavSwitchAsync(pressed: true);
        private void WallpaperBackupNavItem_PointerReleased(object sender, PointerRoutedEventArgs e) => WallpaperBackupNavSwitchAsync(pressed: false);

        /// <summary>按下→播第 0→10 帧;松开→从第 10 帧继续播到第 40 帧(复位)。</summary>
        private async void WallpaperBackupNavSwitchAsync(bool pressed)
        {
            if (!WallpaperBackupNavIconAnimationProbe || WallpaperBackupNavIcon is null) return;
            if (pressed == _wallpaperBackupNavPressed) return;   // 目标态 = 当前态
            var current = WallpaperBackupNavIcon.GetValue(AnimatedIcon.StateProperty) as string;
            if (!pressed && !string.Equals(current, "Pressed", StringComparison.Ordinal))
            {
                // 导航项自己已经把状态切走了(它会在松开时设 PointerOverSelected/Selected 等),
                // 那一段动画它已经在播,这里不插手,免得"跳一下"
                _wallpaperBackupNavPressed = false;
                return;
            }
            _wallpaperBackupNavPressed = pressed;
            _wallpaperBackupNavCts?.Cancel();
            var cts = new CancellationTokenSource();
            _wallpaperBackupNavCts = cts;
            long wait = _wallpaperBackupNavBusyUntil - Environment.TickCount64;
            if (wait > 0)
            {
                try { await Task.Delay((int)wait, cts.Token); }
                catch (TaskCanceledException) { return; }   // 期间又点了一次,交给新的一次
            }
            if (cts.IsCancellationRequested) return;
            _wallpaperBackupNavBusyUntil = Environment.TickCount64 + (pressed ? WallpaperBackupNavPressMs : WallpaperBackupNavReleaseMs);
            Log.Information("[动画] 壁纸备份 导航项图标状态切换 → {State}", pressed ? "Pressed(第 0→10 帧)" : "Normal(第 10→40 帧)");
            AnimatedIcon.SetState(WallpaperBackupNavIcon, pressed ? "Pressed" : "Normal");
        }

        // ===================== 日志 导航项图标动画(2026-09) =====================
        // 导航项图标由静态字形 E823 换成 Lottie 动画(素材 = WE_Tool.AnimatedVisuals.LogsIcon,
        // 见 AnimatedVisuals/LogsIcon.cs;回退字形仍是 E823)。
        // 素材内容:时钟图标(表盘圆环 + 时针/分针),合成时间轴 40 帧(0.67s)——指针整圈旋转,
        // 圆环第 0→10 帧缩到 80%、第 10→30 帧胀回 100%;第 40 帧与第 0 帧姿态逐值相同,静止态外观不变。
        // 标记[重要]:NavigationViewItem 会自己设 PointerOver/Pressed/Selected 等状态,
        // 而 AnimatedIcon 切换是"先跳到目标标记对的起始帧再播到结束帧",标记缺失会走硬切兜底 ——
        // 所以素材里六种状态之间全部 30 个转移都补齐:进入按下态 = 第 0→10 帧,离开按下态 = 第 10→40 帧。
        // 交互(用户口径):鼠标按下 → 播到第 10 帧;松开 → 从第 10 帧继续播完并复位(每段播完再切,避免"跳一下")。
        private const int LogsNavPressMs = 167;     // 第 0→10 帧(10 帧 @60fps)
        private const int LogsNavReleaseMs = 500;   // 第 10→40 帧(30 帧 @60fps)
        private const bool LogsNavIconAnimationProbe = true;   // false = 回到"静止图标"(不播动画)
        private bool _logsNavPressed;    // 当前是否已切到"按下"态
        private long _logsNavBusyUntil;  // 当前片段预计播完的时刻(0=空闲)
        private CancellationTokenSource? _logsNavCts;

        private void LogsNavIcon_WirePointer()
        {
            // NavigationViewItem 自己会处理 PointerPressed 做选中/按下视觉,事件被标记 Handled,
            // 普通 XAML 挂法收不到,必须 handledEventsToo: true。
            LogsNavItem.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(LogsNavItem_PointerPressed), true);
            LogsNavItem.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(LogsNavItem_PointerReleased), true);
            LogsNavItem.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(LogsNavItem_PointerReleased), true);   // 拖动/丢捕获也要归位
        }

        private void LogsNavItem_PointerPressed(object sender, PointerRoutedEventArgs e) => LogsNavSwitchAsync(pressed: true);
        private void LogsNavItem_PointerReleased(object sender, PointerRoutedEventArgs e) => LogsNavSwitchAsync(pressed: false);

        /// <summary>按下→播第 0→10 帧;松开→从第 10 帧继续播到第 40 帧(复位)。</summary>
        private async void LogsNavSwitchAsync(bool pressed)
        {
            if (!LogsNavIconAnimationProbe || LogsNavIcon is null) return;
            if (pressed == _logsNavPressed) return;   // 目标态 = 当前态
            var current = LogsNavIcon.GetValue(AnimatedIcon.StateProperty) as string;
            if (!pressed && !string.Equals(current, "Pressed", StringComparison.Ordinal))
            {
                // 导航项自己已经把状态切走了(它会在松开时设 PointerOverSelected/Selected 等),
                // 那一段动画它已经在播,这里不插手,免得"跳一下"
                _logsNavPressed = false;
                return;
            }
            _logsNavPressed = pressed;
            _logsNavCts?.Cancel();
            var cts = new CancellationTokenSource();
            _logsNavCts = cts;
            long wait = _logsNavBusyUntil - Environment.TickCount64;
            if (wait > 0)
            {
                try { await Task.Delay((int)wait, cts.Token); }
                catch (TaskCanceledException) { return; }   // 期间又点了一次,交给新的一次
            }
            if (cts.IsCancellationRequested) return;
            _logsNavBusyUntil = Environment.TickCount64 + (pressed ? LogsNavPressMs : LogsNavReleaseMs);
            Log.Information("[动画] 日志 导航项图标状态切换 → {State}", pressed ? "Pressed(第 0→10 帧)" : "Normal(第 10→40 帧)");
            AnimatedIcon.SetState(LogsNavIcon, pressed ? "Pressed" : "Normal");
        }

        // ===================== 清理 导航项图标动画(2026-09) =====================
        // 导航项图标由静态字形 E74D 换成 Lottie 动画(素材 = WE_Tool.AnimatedVisuals.DeleteIcon,
        // 见 AnimatedVisuals/DeleteIcon.cs;回退字形仍是 E74D)。
        // [为什么共用 DeleteIcon] 用户要求删除图标统一:导航项与页面顶部栏那 12 处删除按钮共用同一个
        // 生成类(DeleteIcon.cs 里既有本导航项的六态标记,也有顶部栏 AnimatedIconPlayer 用的
        // NormalToPlaying/PlayingToNormal 标记),避免同一素材生成两份、增大发布体积。
        // 素材内容(2026-09-18 换新版):垃圾桶(桶盖掀起、桶身压扁,再复位),时间轴 20 帧(0.333s):
        // 第 0→10 帧按下、第 10→20 帧复位;第 20 帧与第 0 帧姿态逐值相同,静止态外观不变。
        // 标记[重要]:NavigationViewItem 会自己设 PointerOver/Pressed/Selected 等状态,
        // 而 AnimatedIcon 切换是"先跳到目标标记对的起始帧再播到结束帧",标记缺失会走硬切兜底 ——
        // 所以素材里六种状态之间全部 30 个转移都补齐:进入按下态 = 第 0→10 帧,离开按下态 = 第 10→20 帧
        //(离开到 Normal 的那条是倒放回退段,时长同为 10 帧,不影响下面的排队计时)。
        // 交互(用户口径):鼠标按下 → 播到第 10 帧;松开 → 从第 10 帧继续播完并复位(每段播完再切,避免"跳一下")。
        private const int CleanupNavPressMs = 167;     // 第 0→10 帧(10 帧 @60fps)
        private const int CleanupNavReleaseMs = 167;   // 第 10→20 帧(含倒放回退;10 帧 @60fps)
        private const bool CleanupNavIconAnimationProbe = true;   // false = 回到"静止图标"(不播动画)
        private bool _cleanupNavPressed;    // 当前是否已切到"按下"态
        private long _cleanupNavBusyUntil;  // 当前片段预计播完的时刻(0=空闲)
        private CancellationTokenSource? _cleanupNavCts;

        private void CleanupNavIcon_WirePointer()
        {
            // NavigationViewItem 自己会处理 PointerPressed 做选中/按下视觉,事件被标记 Handled,
            // 普通 XAML 挂法收不到,必须 handledEventsToo: true。
            CleanupNavItem.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(CleanupNavItem_PointerPressed), true);
            CleanupNavItem.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(CleanupNavItem_PointerReleased), true);
            CleanupNavItem.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(CleanupNavItem_PointerReleased), true);   // 拖动/丢捕获也要归位
        }

        private void CleanupNavItem_PointerPressed(object sender, PointerRoutedEventArgs e) => CleanupNavSwitchAsync(pressed: true);
        private void CleanupNavItem_PointerReleased(object sender, PointerRoutedEventArgs e) => CleanupNavSwitchAsync(pressed: false);

        /// <summary>按下→播第 0→10 帧;松开→从第 10 帧继续播到第 20 帧(复位)。</summary>
        private async void CleanupNavSwitchAsync(bool pressed)
        {
            if (!CleanupNavIconAnimationProbe || CleanupNavIcon is null) return;
            if (pressed == _cleanupNavPressed) return;   // 目标态 = 当前态
            var current = CleanupNavIcon.GetValue(AnimatedIcon.StateProperty) as string;
            if (!pressed && !string.Equals(current, "Pressed", StringComparison.Ordinal))
            {
                // 导航项自己已经把状态切走了(它会在松开时设 PointerOverSelected/Selected 等),
                // 那一段动画它已经在播,这里不插手,免得"跳一下"
                _cleanupNavPressed = false;
                return;
            }
            _cleanupNavPressed = pressed;
            _cleanupNavCts?.Cancel();
            var cts = new CancellationTokenSource();
            _cleanupNavCts = cts;
            long wait = _cleanupNavBusyUntil - Environment.TickCount64;
            if (wait > 0)
            {
                try { await Task.Delay((int)wait, cts.Token); }
                catch (TaskCanceledException) { return; }   // 期间又点了一次,交给新的一次
            }
            if (cts.IsCancellationRequested) return;
            _cleanupNavBusyUntil = Environment.TickCount64 + (pressed ? CleanupNavPressMs : CleanupNavReleaseMs);
            Log.Information("[动画] 清理 导航项图标状态切换 → {State}", pressed ? "Pressed(第 0→10 帧)" : "Normal(第 10→20 帧)");
            AnimatedIcon.SetState(CleanupNavIcon, pressed ? "Pressed" : "Normal");
        }
        // ===================== 信息 导航项图标动画(2026-09) =====================
        // 导航项图标由静态字形 E946 换成 Lottie 动画(素材 = WE_Tool.AnimatedVisuals.InfoIcon,
        // 见 AnimatedVisuals/InfoIcon.cs;回退字形仍是 E946)。
        // 素材内容:信息图标(外圈圆环 + 中间笔画),合成时间轴 30 帧(0.5s)——
        // 外圈第 0→10 帧缩到 80%、第 10→30 帧胀回 100%,中间笔画第 5→10 帧同步缩小再胀回;
        // 第 30 帧与第 0 帧姿态逐值相同,静止态外观不变。
        // 标记[重要]:NavigationViewItem 会自己设 PointerOver/Pressed/Selected 等状态,
        // 而 AnimatedIcon 切换是"先跳到目标标记对的起始帧再播到结束帧",标记缺失会走硬切兜底 ——
        // 所以素材里六种状态之间全部 30 个转移都补齐:进入按下态 = 第 0→10 帧,离开按下态 = 第 10→30 帧。
        // 交互(用户口径):鼠标按下 → 播到第 10 帧;松开 → 从第 10 帧继续播完并复位(每段播完再切,避免"跳一下")。
        private const int InfoNavPressMs = 167;     // 第 0→10 帧(10 帧 @60fps)
        private const int InfoNavReleaseMs = 333;   // 第 10→30 帧(20 帧 @60fps)
        private const bool InfoNavIconAnimationProbe = true;   // false = 回到"静止图标"(不播动画)
        private bool _infoNavPressed;    // 当前是否已切到"按下"态
        private long _infoNavBusyUntil;  // 当前片段预计播完的时刻(0=空闲)
        private CancellationTokenSource? _infoNavCts;

        private void InfoNavIcon_WirePointer()
        {
            // NavigationViewItem 自己会处理 PointerPressed 做选中/按下视觉,事件被标记 Handled,
            // 普通 XAML 挂法收不到,必须 handledEventsToo: true。
            InfoNavItem.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(InfoNavItem_PointerPressed), true);
            InfoNavItem.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(InfoNavItem_PointerReleased), true);
            InfoNavItem.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(InfoNavItem_PointerReleased), true);   // 拖动/丢捕获也要归位
        }

        private void InfoNavItem_PointerPressed(object sender, PointerRoutedEventArgs e) => InfoNavSwitchAsync(pressed: true);
        private void InfoNavItem_PointerReleased(object sender, PointerRoutedEventArgs e) => InfoNavSwitchAsync(pressed: false);

        /// <summary>按下→播第 0→10 帧;松开→从第 10 帧继续播到第 30 帧(复位)。</summary>
        private async void InfoNavSwitchAsync(bool pressed)
        {
            if (!InfoNavIconAnimationProbe || InfoNavIcon is null) return;
            if (pressed == _infoNavPressed) return;   // 目标态 = 当前态
            var current = InfoNavIcon.GetValue(AnimatedIcon.StateProperty) as string;
            if (!pressed && !string.Equals(current, "Pressed", StringComparison.Ordinal))
            {
                // 导航项自己已经把状态切走了(它会在松开时设 PointerOverSelected/Selected 等),
                // 那一段动画它已经在播,这里不插手,免得"跳一下"
                _infoNavPressed = false;
                return;
            }
            _infoNavPressed = pressed;
            _infoNavCts?.Cancel();
            var cts = new CancellationTokenSource();
            _infoNavCts = cts;
            long wait = _infoNavBusyUntil - Environment.TickCount64;
            if (wait > 0)
            {
                try { await Task.Delay((int)wait, cts.Token); }
                catch (TaskCanceledException) { return; }   // 期间又点了一次,交给新的一次
            }
            if (cts.IsCancellationRequested) return;
            _infoNavBusyUntil = Environment.TickCount64 + (pressed ? InfoNavPressMs : InfoNavReleaseMs);
            Log.Information("[动画] 信息 导航项图标状态切换 → {State}", pressed ? "Pressed(第 0→10 帧)" : "Normal(第 10→30 帧)");
            AnimatedIcon.SetState(InfoNavIcon, pressed ? "Pressed" : "Normal");
        }

        /// <summary>导航项徽标更新(页面经 NavBadgeService 调用,count 为 null/0 时隐藏;state 决定颜色)。</summary>
        private void OnNavBadgeChanged(string pageTag, int? count, NavBadgeState state)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                InfoBadge? badge = pageTag switch
                {
                    "Papers" => PapersBadge,
                    "InstalledComponents" => InstalledComponentsBadge,
                    "LoadPapers" => LoadPapersBadge,
                    _ => null
                };
                if (badge == null) return;

                if (count is null or <= 0 || state == NavBadgeState.None)
                {
                    badge.Visibility = Visibility.Collapsed;
                    return;
                }

                // 颜色语义同 InfoBar Severity:绿=正常进行,黄=暂停/警告,红=失败
                badge.Background = state switch
                {
                    NavBadgeState.Paused => new SolidColorBrush(Color.FromArgb(255, 249, 168, 37) /* 黄 */),
                    NavBadgeState.Error => new SolidColorBrush(Color.FromArgb(255, 196, 43, 28) /* 红 */),
                    _ => new SolidColorBrush(Color.FromArgb(255, 16, 124, 16) /* 绿 */),
                };

                badge.Value = count.Value;
                badge.Visibility = Visibility.Visible;
            });
        }

        /// <summary>桥接状态事件可能来自任意线程,统一编组到 UI 线程更新徽标</summary>
        private void OnSteamworksStatusChanged()
        {
            DispatcherQueue.TryEnqueue(UpdateSteamStatusBadge);
        }

        /// <summary>
        /// 窗口级快捷键分发:内容根 Grid 挂 KeyDown(整棵视觉树的总根,任何按键都必经此处)。
        /// 页面自挂在 Page.KeyDown 上的快捷键在键盘焦点不在页面子树内时收不到事件
        /// (卡片为无焦点样式,点击卡片不会把焦点拉进页面),故由窗口统一收键后分发给当前页面。
        /// 焦点在 TextBox/AutoSuggestBox 时直接放行:文本编辑键(Ctrl+A/C、Delete)由文本框合法消费。
        /// </summary>
        private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            // 已被内层控件处理(如文本框吞掉编辑键)则不重复分发
            if (e.Handled) return;

            if (FocusManager.GetFocusedElement() is TextBox or AutoSuggestBox)
                return; // 文本输入中,让位

            // 分发到当前页面的快捷键处理(页面内部仍保留各自分支)
            if (contentFrame.Content is Papers papersPage)
                papersPage.HandleShortcutKey(e);
            else if (contentFrame.Content is InstalledComponents componentsPage)
                componentsPage.HandleShortcutKey(e);
        }

        /// <summary>焦点跟踪:提取等后台事件仅在主窗口无焦点时弹系统通知(Deactivated = 失去焦点)</summary>
        private void MainWindow_FocusChanged(object? sender, WindowActivatedEventArgs e)
        {
            NotificationService.IsWindowFocused = e.WindowActivationState != WindowActivationState.Deactivated;
        }

        /// <summary>导航栏 Info 项徽标:全绿才绿(Steamworks 在线 且 RePKG_Re 版本匹配),其余一律红</summary>
        private void UpdateSteamStatusBadge()
        {
            bool allOk = SteamWorkshopService.GetInstance().Status == SteamworksStatus.Running
                         && Info.IsRepkgStatusOk();
            if (allOk)
            {
                SteamStatusBadge.Visibility = Visibility.Visible;
                SteamStatusBadge.Background = new SolidColorBrush(Color.FromArgb(255, 16, 124, 16));
            }
            else
            {
                SteamStatusBadge.Visibility = Visibility.Visible;
                SteamStatusBadge.Background = new SolidColorBrush(Color.FromArgb(255, 196, 43, 28));
            }
        }

        private CancellationTokenSource? _positionSaveCts;
        private async void MainWindow_Activated(object? sender, WindowActivatedEventArgs e)
        {
            this.Activated -= MainWindow_Activated;
            // XamlRoot 此时已就绪:订阅 DPI 变化(跨屏拖动/缩放变更)校准 + 首次校准
            if (this.Content?.XamlRoot != null)
                this.Content.XamlRoot.Changed += OnXamlRootChanged;
            SyncTitleBarRowToCaptionButtons(); // [2026-09] 首次布局后校准标题栏行高(此时 TitleBar.Height 已可用)
            UpdateTopNavInputRegions(); // Top 模式启动:首次布局后划分顶栏可点区/窗口拖区
            try
            {
                var settings = await _configService.LoadAsync();
                var tag = settings?.StartPageTag ?? "Papers";

                var item = FindNavItemByTag(nvSample.MenuItems, tag) ?? FindNavItemByTag(nvSample.FooterMenuItems, tag);

                if (item is not null)
                {
                    item.IsSelected = true;
                    nvSample.SelectedItem = item;

                    if (MapTagToPageType(tag) is { } pageType)
                        contentFrame.Navigate(pageType);
                }

                // 自动备份-启动时备份模式:Enabled 且非服务模式 → 后台异步补齐一次
                var auto = settings?.AutoBackup;
                if (auto is { Enabled: true, ServiceEnabled: false })
                    _ = Task.Run(() => RunStartupBackupAsync(auto));

                // 恢复窗口位置和大小（在导航之后执行，确保窗口布局已完成）
                bool hasPosition = settings is { RestoreWindowGeometry: true, WindowX: >= 0, WindowY: >= 0 };
                bool hasSize = settings is { RestoreWindowGeometry: true, WindowWidth: > 0, WindowHeight: > 0 };

                if (hasPosition || hasSize)
                {
                    try
                    {
                        int x = hasPosition ? settings!.WindowX : this.AppWindow.Position.X;
                        int y = hasPosition ? settings!.WindowY : this.AppWindow.Position.Y;
                        int w = hasSize ? settings!.WindowWidth : this.AppWindow.Size.Width;
                        int h = hasSize ? settings!.WindowHeight : this.AppWindow.Size.Height;

                        var rect = new RectInt32(x, y, w, h);
                        // 检查位置是否在有效显示器范围内（防止外接显示器被移除后窗口跑出屏幕可见区域）
                        var area = DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.Nearest);
                        if (area != null)
                        {
                            this.AppWindow.MoveAndResize(rect);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "恢复窗口位置/大小时异常，将使用默认值。");
                    }
                }

                // 恢复最大化状态（放在位置/大小之后，确保 restore bounds 先设置好）
                if (settings is { RestoreWindowGeometry: true, WindowMaximized: true })
                {
                    try
                    {
                        if (this.AppWindow.Presenter is OverlappedPresenter op)
                            op.Maximize();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "恢复窗口最大化状态失败。");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"初始化失败。Tag: {ViewModel?.AppSettingsVM.StartPageTag}");
            }
        }
        private static NavigationViewItem? FindNavItemByTag(IEnumerable items, string tag)
        {
            foreach (var obj in items)
            {
                if (obj is NavigationViewItem nvi)
                {
                    if ((nvi.Tag?.ToString() ?? "") == tag)
                        return nvi;

                    if (nvi.MenuItems?.Count > 0)
                    {
                        var found = FindNavItemByTag(nvi.MenuItems, tag);
                        if (found != null) return found;
                    }
                }
            }
            return null;
        }

        private static Type? MapTagToPageType(string tag) =>
            tag switch
            {
                "Papers" => typeof(Papers),
                "LoadPapers" => typeof(LoadPapers),
                "InstalledComponents" => typeof(InstalledComponents),
                "Cleanup" => typeof(Cleanup),
                "WallpaperBackup" => typeof(WallpaperBackup),
                "Logs" => typeof(Logs),
                "Info" => typeof(Info),
                "Settings" => typeof(Settings),
                _ => typeof(Papers)
            };

        private void NvSample_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.InvokedItemContainer == null)
                return;

            string? tag = args.InvokedItemContainer.Tag.ToString();

            _ = tag switch
            {
                "Papers" => contentFrame.Navigate(typeof(Papers), null),
                "LoadPapers" => contentFrame.Navigate(typeof(LoadPapers), null),
                "InstalledComponents" => contentFrame.Navigate(typeof(InstalledComponents), null),
                "Cleanup" => contentFrame.Navigate(typeof(Cleanup), null),
                "WallpaperBackup" => contentFrame.Navigate(typeof(WallpaperBackup), null),
                "Logs" => contentFrame.Navigate(typeof(Views.Logs), null),
                "Info" => contentFrame.Navigate(typeof(Info), null),
                "Settings" => contentFrame.Navigate(typeof(Settings), null),
                _ => contentFrame.Navigate(typeof(Papers), null)
            };
        }

        public string CurrentPageTag =>
            (nvSample.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "Papers";

        internal void NavigateToPage(string tag)
        {
            var pageType = MapTagToPageType(tag);
            contentFrame.Navigate(pageType, null);
        }

        /// <summary>应用导航栏模式(App.LoadNavigationMode 调用):"Top"=顶部导航栏(隐藏自绘标题栏,顶栏贴顶兼作标题栏行),其余(默认 "Left")=左侧导航栏。</summary>
        internal void ApplyNavigationMode(string mode)
        {
            bool top = mode == "Top";
            _isTopNavMode = top;

            // 程序性设置窗格/模式不代表用户偏好:期间抑制 PaneOpening/PaneClosing 回写
            // (切到 Top 模式时 NavigationView 内部会收起窗格,否则会把已保存的偏好覆盖成 false)
            _suppressNavPaneStateSave = true;
            try
            {
                nvSample.PaneDisplayMode = top
                    ? NavigationViewPaneDisplayMode.Top
                    : NavigationViewPaneDisplayMode.Left;

                // 恢复上次的窗格展开状态(Top 模式没有窗格概念,故只在 Left 模式应用)
                if (!top)
                    nvSample.IsPaneOpen = ViewModel.AppSettingsVM.NavPaneOpen;
            }
            finally
            {
                _suppressNavPaneStateSave = false;
            }

            if (top)
            {
                // 顶栏贴近窗口顶部(消除 NavigationView 因扩展标题栏自动加的顶部留白,否则又变两层)
                nvSample.IsTitleBarAutoPaddingEnabled = false;
                // 隐藏自绘标题栏行(行高归零;恢复 Left 时由 SyncTitleBarRowToCaptionButtons 重新校准)
                TitleBarRow.Height = new GridLength(0);
                // Top 模式系统"设置"齿轮与 FooterMenuItems 被固定在顶栏最右端,而窗口右上角
                // 悬浮着系统按钮(最小化/最大化/关闭)——NavigationView 感知不到 caption 按钮
                // (microsoft-ui-xaml #6108),三者必然重叠 → 隐藏内置齿轮,日志/关于/设置
                // 全部改挂 MenuItems 末尾(与主菜单同从左排布,超宽时自动折叠进溢出菜单)
                nvSample.IsSettingsVisible = false;
                EnsureTopPaneTitle(visible: true); // 程序名放到顶栏最左端(PaneHeader 位)
                MoveFooterItemsToMenu(toMenu: true);
                EnsureTopSettingsItem(visible: true);
            }
            else
            {
                nvSample.IsTitleBarAutoPaddingEnabled = true;
                SyncTitleBarRowToCaptionButtons();
                EnsureTopPaneTitle(visible: false); // 摘除 PaneHeader,恢复原居中标题栏行
                EnsureTopSettingsItem(visible: false);
                MoveFooterItemsToMenu(toMenu: false); // 日志/关于还原到 FooterMenuItems 底部区
                nvSample.IsSettingsVisible = true;
            }

            UpdateTopNavInputRegions();
        }

        private bool _isTopNavMode;

        /// <summary>窗格状态回写抑制标记(ApplyNavigationMode 程序性设置 IsPaneOpen/PaneDisplayMode 期间为 true)。</summary>
        private bool _suppressNavPaneStateSave;

        private void NvSample_PaneOpening(NavigationView sender, object args) => SetNavPaneOpen(true);

        private void NvSample_PaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args) => SetNavPaneOpen(false);

        /// <summary>记录左侧导航栏展开状态(NavPaneOpen,经 SettingsViewModel 防抖落盘)。</summary>
        private void SetNavPaneOpen(bool open)
        {
            if (_suppressNavPaneStateSave || _isTopNavMode) return;

            try
            {
                ViewModel.AppSettingsVM.NavPaneOpen = open;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存导航栏展开状态失败");
            }
        }

        /// <summary>Top 模式下显示于顶栏最左端的程序名(NavigationView.PaneHeader 位,Left 模式摘除恢复原居中标题栏)。</summary>
        private TextBlock? _topPaneTitle;

        /// <summary>Top 模式把程序名 "WE Tool" 挂到顶栏 PaneHeader(最左端,菜单项之前);Left 模式移除。</summary>
        private void EnsureTopPaneTitle(bool visible)
        {
            if (visible)
            {
                if (_topPaneTitle != null)
                {
                    nvSample.PaneHeader = _topPaneTitle;
                    return;
                }
                _topPaneTitle = new TextBlock
                {
                    Text = "WE Tool",
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false, // 文字不挡鼠标:所在区域保留为窗口拖区
                    Margin = new Thickness(20, 0, 20, 0),
                };
                nvSample.PaneHeader = _topPaneTitle;
            }
            else if (nvSample.PaneHeader != null)
            {
                nvSample.PaneHeader = null;
            }
        }

        /// <summary>
        /// Top 模式下把 FooterMenuItems(日志/关于)临时搬进 MenuItems 末尾:Top 排布时
        /// FooterMenuItems 固定在顶栏最右端,会被右上角系统按钮盖住;与主菜单同排后右端空出。
        /// Left 模式移回 FooterMenuItems(保持 日志→关于 的原有顺序)。
        /// </summary>
        private void MoveFooterItemsToMenu(bool toMenu)
        {
            var logs = FindNavItemByTag(nvSample.MenuItems, "Logs") ?? FindNavItemByTag(nvSample.FooterMenuItems, "Logs");
            var info = FindNavItemByTag(nvSample.MenuItems, "Info") ?? FindNavItemByTag(nvSample.FooterMenuItems, "Info");
            if (logs is null || info is null) return;

            if (toMenu)
            {
                if (nvSample.FooterMenuItems.Contains(logs))
                {
                    nvSample.FooterMenuItems.Remove(logs);
                    if (!nvSample.MenuItems.Contains(logs))
                        nvSample.MenuItems.Add(logs);
                }
                if (nvSample.FooterMenuItems.Contains(info))
                {
                    nvSample.FooterMenuItems.Remove(info);
                    if (!nvSample.MenuItems.Contains(info))
                        nvSample.MenuItems.Add(info);
                }
            }
            else
            {
                if (nvSample.MenuItems.Contains(logs))
                {
                    nvSample.MenuItems.Remove(logs);
                    if (!nvSample.FooterMenuItems.Contains(logs))
                        nvSample.FooterMenuItems.Add(logs);
                }
                if (nvSample.MenuItems.Contains(info))
                {
                    nvSample.MenuItems.Remove(info);
                    if (!nvSample.FooterMenuItems.Contains(info))
                        nvSample.FooterMenuItems.Add(info);
                }
            }
        }

        private NavigationViewItem? _topSettingsItem;

        /// <summary>Top 模式把"设置"作为普通菜单项挂到末尾(齿轮被系统按钮遮挡不可用);Left 模式移除,恢复内置齿轮。</summary>
        private void EnsureTopSettingsItem(bool visible)
        {
            if (visible)
            {
                if (_topSettingsItem != null)
                {
                    if (!nvSample.MenuItems.Contains(_topSettingsItem))
                        nvSample.MenuItems.Add(_topSettingsItem);
                    return;
                }
                var item = new NavigationViewItem
                {
                    Tag = "Settings",
                    Content = LanguageHelper.GetResource("Settings.Text"),
                    Icon = new FontIcon { Glyph = "\uE713" } // Setting 齿轮
                };
                _topSettingsItem = item;
                nvSample.MenuItems.Add(item);
            }
            else if (_topSettingsItem != null)
            {
                nvSample.MenuItems.Remove(_topSettingsItem);
            }
        }

        /// <summary>
        /// Top 模式(NavigationView 顶栏进入系统标题栏区域)输入区域划分:
        /// 顶栏左侧大部分(菜单可点区)设为 Passthrough,让点击落到 NavigationView;
        /// 右侧系统按钮区 + 其左侧拖条保留为系统标题栏(可拖动窗口)。
        /// Left 模式(标题栏行在 NavigationView 之上)无需划分,清空。
        /// </summary>
        private void UpdateTopNavInputRegions()
        {
            try
            {
                var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
                if (!_isTopNavMode || this.Content?.XamlRoot is not { } root)
                {
                    source.ClearRegionRects(NonClientRegionKind.Passthrough);
                    return;
                }

                double scale = root.RasterizationScale;
                if (scale <= 0) return;
                double titleBarDip = AppWindow.TitleBar.Height / scale;   // 标题栏(顶栏)高,物理 px → DIP
                if (titleBarDip < 20) return;
                double insetDip = AppWindow.TitleBar.RightInset / scale;  // 系统按钮区宽
                double contentDip = root.Size.Width;                       // 内容区宽(DIP)
                const double dragBandDip = 110;                            // 系统按钮左侧保留的窗口拖条宽

                // 顶栏左端的程序名(PaneHeader)区域保留为窗口拖区,其右侧(菜单可点区)才设 Passthrough。
                // 注意 DesiredSize 不含 TextBlock 自身 Margin(左右各 20),拖区宽须把 Margin 一并计入。
                double titleDip = 0;
                if (_topPaneTitle is { } title)
                {
                    title.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                    double w = title.DesiredSize.Width;
                    if (w > 0)
                        titleDip = Math.Ceiling(w) + title.Margin.Left + title.Margin.Right + 8;
                }
                double passX = titleDip;
                double passW = Math.Max(0, contentDip - insetDip - dragBandDip - titleDip);

                source.SetRegionRects(NonClientRegionKind.Passthrough,
                [
                    new RectInt32((int)Math.Ceiling(passX * scale), 0, (int)Math.Ceiling(passW * scale), (int)Math.Ceiling(titleBarDip * scale))
                ]);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "更新顶部导航输入区域失败");
            }
        }

        internal void RefreshUILanguage()
        {
            // 重新加载语言资源，使 SortText 等更新
            LanguageHelper.ReloadResources();

            // Top 模式的动态"设置"菜单项是代码创建的(x:Uid 不生效),语言切换后手动刷新文本
            if (_topSettingsItem != null)
                _topSettingsItem.Content = LanguageHelper.GetResource("Settings.Text");

            // 重建当前 Page（x:Uid 重新从 .resw 加载）
            var pageType = MapTagToPageType(CurrentPageTag);
            contentFrame.BackStack.Clear();
            int originalCacheSize = contentFrame.CacheSize;
            contentFrame.CacheSize = 0;
            contentFrame.Navigate(pageType, null);
            contentFrame.CacheSize = originalCacheSize;

        }

        private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            // DPI 缩放变化(跨屏拖动/系统缩放变更)→ 重新校准标题栏内容行高度
            SyncTitleBarRowToCaptionButtons();
            // 缩放变化会改变物理像素换算,Top 模式的输入区域需重算
            UpdateTopNavInputRegions();
        }

        /// <summary>
        /// [2026-09] 让标题栏内容行(Row0)高度与系统标题栏/窗口按钮区精确对齐:
        /// 系统按物理像素算高度(TitleBar.Height),XAML 按 DIP——不同 DPI 下固定值会差 1-2px,
        /// 导致内容条与那三个窗口按钮高度不一致。运行时读 TitleBar.Height ÷ RasterizationScale 换算成 DIP 设给 Row。
        /// </summary>
        private void SyncTitleBarRowToCaptionButtons()
        {
            try
            {
                // Top 模式标题栏行恒为 0(顶栏兼作标题栏),不做高度校准
                if (_isTopNavMode) return;
                if (TitleBarRow == null || this.Content?.XamlRoot == null) return;
                double scale = this.Content.XamlRoot.RasterizationScale;
                if (scale <= 0) return;
                double titleBarDip = AppWindow.TitleBar.Height / scale;
                if (titleBarDip < 20) return; // 异常值保护(极小说明标题栏未初始化)
                double target = Math.Ceiling(titleBarDip); // 向上取整,避免内容条矮于按钮区导致底部露白
                if (Math.Abs(TitleBarRow.ActualHeight - target) > 1)
                    TitleBarRow.Height = new GridLength(target);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "同步标题栏高度失败");
            }
        }

        private async void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (!args.DidPositionChange && !args.DidSizeChange && !args.DidPresenterChange) return;

            // Top 模式:窗口尺寸变化(拖宽/最大化)会改变顶栏可点区宽度,即时重算输入区域
            UpdateTopNavInputRegions();

            // 防抖：用户拖拽过程中会连续触发，只取最后一次停止后 500ms 写入
            _positionSaveCts?.Cancel();
            _positionSaveCts = new CancellationTokenSource();
            var token = _positionSaveCts.Token;

            try
            {
                await Task.Delay(500, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var settings = await _configService.LoadAsync();
                if (args.DidPositionChange)
                {
                    settings.WindowX = sender.Position.X;
                    settings.WindowY = sender.Position.Y;
                }
                if (args.DidSizeChange)
                {
                    settings.WindowWidth = sender.Size.Width;
                    settings.WindowHeight = sender.Size.Height;
                }
                // 无论触发原因，始终记录当前窗口最大化状态
                settings.WindowMaximized =
                    sender.Presenter is OverlappedPresenter op &&
                    op.State == OverlappedPresenterState.Maximized;
                await _configService.SaveAsync(settings);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存窗口位置/大小/状态失败");
            }
        }

        /// <summary>启动时备份模式:一次补齐所有「未备份 + 命中筛选」的工坊壁纸(后台,不阻塞窗口)。
        /// [立即备份 2026-09] 补齐实现已抽到 BackupService.BackupAllMissing,与备份页「立即备份」按钮共用。</summary>
        private static void RunStartupBackupAsync(Models.AutoBackupConfig cfg)
        {
            try
            {
                var app = Application.Current as App;
                var workshopPath = app?.ViewModel.PathManagementVM.WorkshopPath;
                if (string.IsNullOrEmpty(workshopPath) || !Directory.Exists(workshopPath))
                {
                    Log.Warning("启动时备份跳过:工坊目录不存在 {Path}", workshopPath);
                    return;
                }

                int backed = BackupService.BackupAllMissing(workshopPath, cfg);
                if (backed > 0)
                    Log.Information("启动时备份完成: 新增备份 {Count} 个", backed);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "启动时备份异常");
            }
        }
    }
}

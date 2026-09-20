using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Serilog;
using System;
using System.Collections.Generic;
using Windows.UI;

namespace WE_Tool.Controls
{
    /// <summary>
    /// 会跟随主题自动变色的动画图标(继承 AnimatedIcon)。
    /// [为什么需要] Lottie 素材的填充色是导出时写死的常量(#EBEBEB,当初按深色主题选的),
    /// 不像 FontIcon 那样跟着 Foreground 走 —— 程序切到浅色主题后图标还是近白色,白底白图标读不出来。
    ///
    /// [主题怎么判定] 不看自身 ActualTheme(实测这里有坑:本应用是 App.LoadTheme() 给窗口 Content 设
    /// RequestedTheme,部分模板元素/弹层里的图标自身 ActualTheme 既不刷新、ActualThemeChanged 也不触发,
    /// 日志里出现过"实际浅色但报 Dark"的情况),而是读**主题根元素**(XamlRoot.Content)的
    /// RequestedTheme/ActualTheme —— 那正是应用设置主题的地方,最权威。
    ///
    /// [什么时候重涂] ① 加载时 ② 自身/主题根的 ActualThemeChanged ③ Foreground 变化(禁用变灰等)
    /// ④ **兜底轮询**(250ms 一次,只在取色真的变了才动手):上面三个事件在本应用里都可能漏,
    /// 轮询保证换主题后最迟 0.25 秒一定跟上,不依赖任何事件是否触发。
    /// 取色口径(浅色分支与改前一致;深色分支 2026-09-18 修正):两个主题都优先跟随 Foreground ——
    /// 浅色:前景是深色就用它,否则标准图标色 #E4000000;深色:前景是亮色或饱和色(如 Foreground="Red")就用它,
    /// 前景本身是暗色(异常场景)才退回素材原色 #EBEBEB。详见 ResolveTarget()。
    /// 例外:开关类"选中反相"场景把 TrustForeground 设为 True,则黑也照画(无条件跟随 Foreground)。
    /// </summary>
    public sealed partial class ThemedAnimatedIcon : AnimatedIcon
    {
        private static readonly Color LightThemeColor = Color.FromArgb(0xE4, 0x00, 0x00, 0x00);   // 浅色主题标准图标色
        private static readonly List<WeakReference<ThemedAnimatedIcon>> _live = new();
        private static DispatcherQueueTimer? _ticker;

        private readonly Dictionary<CompositionColorBrush, Color> _originalColors = new();
        private XamlRoot? _xamlRoot;
        private FrameworkElement? _themeRoot;
        private Color? _appliedColor;
        private bool _appliedOnce;
        private bool _trustForeground;

        public ThemedAnimatedIcon()
        {
            Loaded += (_, _) =>
            {
                Register();
                HookThemeRoot();
                ApplyColor("Loaded");
                DispatcherQueue.TryEnqueue(() => { HookThemeRoot(); ApplyColor("Loaded+队列"); });
            };
            Unloaded += (_, _) => Unregister();
            RegisterPropertyChangedCallback(IconElement.ForegroundProperty, (_, _) => ApplyColor("Foreground"));
            ActualThemeChanged += (_, _) => ApplyColor("Theme(自身)");
        }

        /// <summary>
        /// 运行时更换 Source 后手动重涂一次。
        /// [为什么需要] 换 Source 会重建整棵合成树,新画笔 (CompositionColorBrush) 是全新对象、颜色就是素材里
        /// 写死的原色(#EBEBEB),不在 _originalColors 里 —— 浅色主题下会闪一下近白色,直到 250ms 兜底轮询才跟上。
        /// 目前只有"排序方向按钮"会在升序/降序两份素材之间换源,换完立刻调一次即可无闪。
        /// </summary>
        public void RefreshColorAfterSourceChange()
        {
            ApplyColor("Source");
            DispatcherQueue.TryEnqueue(() => ApplyColor("Source+队列"));   // 合成树可能要到本帧末才挂上,再补一次
        }

        /// <summary>
        /// 为 True 时无条件跟随 Foreground(跳过"深色主题别画深色前景"的启发式护栏)。
        /// [什么时候要开] 开关类按钮(AppBarToggleButton)选中后底色变浅色胶囊、文字反相
        /// (深色主题=黑字、浅色主题=白字) —— 图标要和文字同色,就必须接受"深色主题画黑"。
        /// [为什么不默认开] 深色主题里弹层可能继承到 #E4000000 这类异常暗色(日志实测过),
        /// 护栏会把它退回素材原色防看不见;普通按钮保持默认,只有反相取色确实正确的实例才开。
        /// </summary>
        public bool TrustForeground
        {
            get => _trustForeground;
            set
            {
                if (_trustForeground == value) return;
                _trustForeground = value;
                ApplyColor("TrustForeground");
            }
        }

        /// <summary>挂到主题根(窗口 Content)的 ActualThemeChanged 上——应用就是给它设 RequestedTheme 的。</summary>
        private void HookThemeRoot()
        {
            // 注意:WinUI 3 里没有 UIElement.XamlRootChanged 这个事件,只有 XamlRoot.Changed
            // (本仓库 MainWindow.xaml.cs:207 挂 DPI 变化用的也是 XamlRoot.Changed)
            var xamlRoot = XamlRoot;
            if (!ReferenceEquals(xamlRoot, _xamlRoot))
            {
                if (_xamlRoot is not null) _xamlRoot.Changed -= OnXamlRootChanged;
                _xamlRoot = xamlRoot;
                if (_xamlRoot is not null) _xamlRoot.Changed += OnXamlRootChanged;
            }

            var root = xamlRoot?.Content as FrameworkElement;
            if (ReferenceEquals(root, _themeRoot)) return;
            if (_themeRoot is not null) _themeRoot.ActualThemeChanged -= OnThemeRootChanged;
            _themeRoot = root;
            if (_themeRoot is not null) _themeRoot.ActualThemeChanged += OnThemeRootChanged;
        }

        private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => HookThemeRoot();

        private void OnThemeRootChanged(FrameworkElement sender, object args) => ApplyColor("Theme(根元素)");

        private void Register()
        {
            foreach (var weak in _live)
                if (weak.TryGetTarget(out var same) && ReferenceEquals(same, this)) return;
            _live.Add(new WeakReference<ThemedAnimatedIcon>(this));
            if (_ticker is null)
            {
                _ticker = DispatcherQueue.GetForCurrentThread().CreateTimer();   // 与 SkiaGifView 同款写法
                _ticker.Interval = TimeSpan.FromMilliseconds(250);
                _ticker.Tick += (_, _) => SyncAll();
                _ticker.Start();
            }
        }

        private void Unregister()
        {
            for (int i = _live.Count - 1; i >= 0; i--)
                if (!_live[i].TryGetTarget(out var target) || ReferenceEquals(target, this)) _live.RemoveAt(i);
        }

        /// <summary>兜底轮询:所有活着的图标都重算一次取色,变了才涂(幂等,开销就是遍历几个图形)。</summary>
        private static void SyncAll()
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                if (_live[i].TryGetTarget(out var icon)) icon.ApplyColor("Tick");
                else _live.RemoveAt(i);
            }
        }

        /// <summary>是否深色主题:以主题根的 RequestedTheme 为准(Default=跟随系统时看它的 ActualTheme)。</summary>
        private bool IsDarkTheme()
        {
            var root = _themeRoot ?? XamlRoot?.Content as FrameworkElement;
            ElementTheme theme = root?.RequestedTheme ?? ElementTheme.Default;
            if (theme == ElementTheme.Default) theme = root?.ActualTheme ?? ActualTheme;
            return theme == ElementTheme.Dark;
        }

        private static bool IsDark(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 < 0.5;

        /// <summary>红/绿这类饱和色:按亮度算可能被判成"深色",但在深色底上依然看得清(Foreground="Red" 的破坏性按钮就是这种)。</summary>
        private static bool IsVivid(Color c)
        {
            int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
            return max - min >= 60;
        }

        /// <summary>
        /// 取色口径。浅色分支与改前完全一致,只修了深色分支。
        /// 浅色主题:前景是深色就用前景(可用 #E4000000、悬停 #9E000000、禁用 #5C000000、红),否则用标准图标色。
        /// 深色主题:前景是亮色或饱和色就用前景(可用 #FFFFFF、悬停 #C5FFFFFF、禁用 #5DFFFFFF、红),
        ///          前景本身是暗色(异常场景,如弹层里继承到 #E4000000)才退回素材原色 #EBEBEB;
        ///          TrustForeground=True 的实例例外(开关"选中反相":黑也照画,见属性注释)。
        /// [为什么改 2026-09-18] 素材原色是 #EBEBEB(92% 白),而深色主题里普通字形图标的可用态是 #FFFFFF、
        /// 悬停 77% 白、禁用 36% 白。原来深色一律"还原素材原色",于是按钮在 可用/悬停/禁用/变红 时动画图标纹丝不动 ——
        /// 顶栏与详情面板的"卸载"(DeleteIcon)在深色下禁用时图标还是亮的,和旁边灰掉的字形图标对不上;
        /// 浅色主题没这问题,因为浅色分支本来就跟随前景。
        /// </summary>
        private Color? ResolveTarget(bool dark)
        {
            if (Foreground is not SolidColorBrush solid) return dark ? null : LightThemeColor;
            if (TrustForeground) return solid.Color;   // 开关选中"反相"场景:前景就是文字色,黑也照画
            Color c = solid.Color;
            bool usable = dark ? (!IsDark(c) || IsVivid(c)) : IsDark(c);
            if (usable) return c;
            return dark ? null : LightThemeColor;
        }

        private void ApplyColor(string trigger)
        {
            bool dark = IsDarkTheme();
            Color? target = ResolveTarget(dark);
            bool changed = !_appliedOnce || target != _appliedColor;
            bool verbose = changed || trigger != "Tick";   // 轮询取色没变时不刷日志

            _appliedColor = target;
            _appliedOnce = true;
            string fg = Foreground is SolidColorBrush s ? s.Color.ToString() : (Foreground?.GetType().Name ?? "null");
            try
            {
                var visualRoot = ElementCompositionPreview.GetElementVisual(this);
                int shapes = 0, painted = 0;
                var result = "";
                if (visualRoot is not null) Apply(visualRoot, target);
                // 轮询取色没变时不打日志;但每次都要真的走一遍重涂 —— 合成树被框架重建后画笔会变回素材原色
                if (verbose)
                    // 带实例名(x:Name;未命名记"(无名)")——多条图标日志交织时靠它区分,排查用
                    Log.Information("[图标主题] 图标={Icon} 触发={T} 深色={Dark} 前景={Fg} → 目标={Target} | 形状={Shapes} 画笔={Painted} {Result}",
                        string.IsNullOrEmpty(Name) ? "(无名)" : Name, trigger, dark, fg, target?.ToString() ?? "素材原色", shapes, painted, result);

                void Apply(Visual visual, Color? color)
                {
                    if (visual is ShapeVisual shapeVisual)
                    {
                        foreach (var shape in shapeVisual.Shapes) ApplyShape(shape, color);
                    }
                    if (visual is ContainerVisual container)
                    {
                        foreach (var child in container.Children) Apply(child, color);
                    }
                }

                // 形状要往下钻:素材带"分组"时图形是包在 CompositionContainerShape 里的
                // (ViewIcon = 12 个图形 / 6 个容器)。只在图层这一层找会一个都找不到 —— 颜色算对了也没处涂,
                // 图标就一直停在素材原色(近白)。2026-09-19:筛选结果/视图两枚图标"只有一个白色"的根因。
                void ApplyShape(CompositionShape shape, Color? color)
                {
                    if (shape is CompositionContainerShape containerShape)
                    {
                        foreach (var child in containerShape.Shapes) ApplyShape(child, color);
                        return;
                    }
                    if (shape is not CompositionSpriteShape sprite) return;
                    shapes++;
                    Paint(sprite.FillBrush, color);
                    Paint(sprite.StrokeBrush, color);
                }

                void Paint(CompositionBrush brush, Color? color)
                {
                    if (brush is not CompositionColorBrush colorBrush) return;
                    if (!_originalColors.TryGetValue(colorBrush, out var original))
                    {
                        original = colorBrush.Color;                 // 素材原色(深色主题/还原用)
                        _originalColors[colorBrush] = original;
                    }
                    var wanted = color ?? original;
                    if (colorBrush.Color != wanted) colorBrush.Color = wanted;
                    if (painted == 0) result = $"原色={original} 现色={colorBrush.Color}";
                    painted++;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[图标主题] 涂色异常(合成对象可能已释放)");
            }
        }
    }
}

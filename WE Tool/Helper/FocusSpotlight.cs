using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Serilog;
using System;
using System.Numerics;
using System.Threading.Tasks;
using Windows.Foundation;

namespace WE_Tool.Helper
{
    /// <summary>
    /// 焦点聚光:二级选择窗口打开时把本窗口的内容压暗,只在唤起它的那一项上开一个洞(四周留一点呼吸,
    /// 不加描边也不加光晕);想要真糊再叠快照模糊(见 Blur 那一档)。让人在对话框与界面之间来回时不必重新找"我在改哪一条"。
    /// 常见的"背板采样"式模糊在 WinUI 3 走不通:托管投影的 Microsoft.UI.Composition.Compositor 没有
    /// CreateBackdrop(实测不存在,只有 UWP 那一套有)。这里退一步:把内容可视树快照成
    /// CompositionVisualSurface,对快照跑 Win2D 高斯模糊,再用 4 块 inset 裁剪把洞围出来 —— 快照是位图,
    /// 裁开不留接缝。遮罩层必须挂在快照源之外,否则快照喂快照会自反馈越刷越黑。
    /// 系统的文件夹/文件对话框是独立 HWND,本层碰不到它,它盖住的区域照旧看不见。
    /// </summary>
    public static class FocusSpotlight
    {
        /// <summary>
        /// Off = 只留带洞遮罩;Gaussian = 多趟真高斯;DownSample = 效果图里串两趟缩放;
        /// SnapshotScale = 快照本身按低分辨率渲染再拉回原尺寸(不靠 D2D 的大核,配一趟小半径高斯抹掉双线性台阶)。
        /// </summary>
        private enum BlurMode { Off, Gaussian, DownSample, SnapshotScale }

        // static readonly 而不是 const:const 会让另一档的建图代码被判成不可达(CS0162),
        // 退档时那段就再也编不出真话了。
        private static readonly BlurMode Blur = BlurMode.Off;
        // D2D 的 blurDiameter 上限就是 100(实测设 120 直接 E_INVALIDARG),要更糊只能串趟:
        // 高斯串高斯 = 方差相加,N 趟直径 d 等效 σ = d/3·√N。
        private const float BlurRadius = 100f;
        private const int BlurPasses = 1;
        // 实测:Quality 配直径 100 那一整层不画(没有异常、没有痕迹),Balanced 画得出来。
        // 所以强度先只靠半径/趟数顶,这一档留在 Balanced;哪天要试真高斯核,单独翻这一个词。
        private static readonly Microsoft.Graphics.Canvas.Effects.EffectOptimization BlurOptimization
            = Microsoft.Graphics.Canvas.Effects.EffectOptimization.Balanced;
        // 降采样档:整张快照先缩到 1/DownSample 再放大回原尺寸,中间那一小张就是"糊"的全部信息量。
        // 12 倍下 14 的字已经彻底读不出;要更糊就加这个数,它不像高斯那样有上限。
        private const float DownSample = 12f;
        // SnapshotScale 档:快照按 1/SnapshotDivisor 分辨率渲染,刷再 Fill 回原占位;
        // 补的那趟高斯只用小半径(20 是实测画得出来的那一档),负责把放大留下的双线性台阶抹平。
        private const float SnapshotDivisor = 8f;
        private const float SnapshotAssistRadius = 20f;

        // 洞比目标四周各让出的像素:压暗那道硬边不贴着卡片边缘,留一点呼吸
        private const double HoleInset = 6;
        // 对比度就是这一档的全部:0.32 那档是给"背景已经糊了"配的,纯暗底要压到 0.62 才显得出焦点项亮
        private static readonly double ScrimOpacity = Blur == BlurMode.Off ? 0.62 : 0.32;

        private static Canvas? _overlay;
        private static Path? _scrim;

        private static CompositionVisualSurface? _snapshot;
        private static CompositionEffectBrush? _blurBrush;
        private static SpriteVisual[]? _bands;
        // 每块带自己的 XAML 宿主:ContainerVisual.Children 在托管投影里既没有 Add 也没有 Insert,
        // 而 SetElementChildVisual(元素, visual) 是可用的,所以干脆一个宿主挂一块。
        private static Border[]? _bandHosts;
        private static UIElement? _blurSource;

        /// <summary>
        /// 压暗并挖洞。<paramref name="anchor"/> 为空时按当前焦点元素回溯出被唤起的项
        /// (点按钮时焦点已经落在那枚按钮上,所以命令执行时取到的就是它)。
        /// </summary>
        public static void Show(FrameworkElement? anchor = null)
        {
            if (App.MainWindowInstance?.Content is not Panel root)
            {
                Log.Warning("[聚光] 本进程的窗口根不是面板,这次不压暗");
                return;
            }

            EnsureOverlay(root);

            var bounds = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                Log.Warning("[聚光] 窗口根尺寸还没定({W}x{H}),这次不压暗", bounds.Width, bounds.Height);
                return;
            }

            // 焦点回溯要跑两遍可视树(找卡、找不到再找按钮),只在没拿到显式锚点时才做
            var target = anchor ?? ResolveFromFocus(root);
            var raw = ResolveHole(target, root);
            var hole = raw.HasValue ? Clip(raw.Value, bounds) : (Rect?)null;

            var group = new GeometryGroup { FillRule = Microsoft.UI.Xaml.Media.FillRule.EvenOdd };
            group.Children.Add(new RectangleGeometry { Rect = bounds });
            if (hole.HasValue) group.Children.Add(new RectangleGeometry { Rect = hole.Value });

            _scrim!.Data = group;
            _scrim.Width = bounds.Width;
            _scrim.Height = bounds.Height;

            if (hole.HasValue)
            {
                var h = hole.Value;
                Log.Information("[聚光] 档={Mode} 压暗={Dim:P0} 唤起项={Item}({From}) 洞=({X},{Y},{W}x{H}) 窗口根={RootW}x{RootH}",
                    Blur, ScrimOpacity, target!.GetType().Name, anchor != null ? "显式" : "焦点回溯",
                    Math.Round(h.X), Math.Round(h.Y), Math.Round(h.Width), Math.Round(h.Height),
                    Math.Round(bounds.Width), Math.Round(bounds.Height));
            }
            else
            {
                Log.Information("[聚光] 档={Mode} 压暗={Dim:P0} 整窗压暗,没挖洞(定位不到唤起项)", Blur, ScrimOpacity);
            }

            _overlay!.Visibility = Visibility.Visible;
            if (Blur != BlurMode.Off)
            {
                // 效果图归 GPU/驱动侧管(直径超上限、表面尺寸不合、设备丢失都在这里抛)。聚光是装饰,
                // 画不出来就退回带洞遮罩 —— 不能让人家点个"浏览"整个命令崩掉。
                try { ApplyBlur(root, hole); }
                catch (Exception ex) { Log.Warning(ex, "[聚光] 模糊层没建起来,这轮只有带洞遮罩"); }
            }
            FadeIn(_overlay);
        }

        public static void Hide()
        {
            if (_overlay == null || _overlay.Visibility != Visibility.Visible) return;
            SetBandsVisible(false);
            _overlay.Visibility = Visibility.Collapsed;
            Log.Information("[聚光] 收起");
        }

        /// <summary>
        /// 把一次"弹二级窗口选路径"包起来:开对话框前压暗挖洞,回来(含取消与异常)收起。
        /// 走不走这条由调用方决定——服务层不代做这个主,其它页面的选取动作不该被压暗。
        /// </summary>
        public static async Task<T?> AroundAsync<T>(Func<Task<T?>> pick) where T : class
        {
            Show();
            try { return await pick(); }
            finally { Hide(); }
        }

        /// <summary>
        /// 快照"遮罩前面那块内容"并模糊,再按洞的位置把模糊层裁成上下左右四条。
        /// 四条都是铺满整块源的 sprite、各自只收一侧的 inset,所以同一片像素至多被同一份模糊结果覆盖,
        /// 接缝无从出现(洞的边界处不需要"补边",因为模糊是在整块源上算好的)。
        /// </summary>
        private static void ApplyBlur(Panel root, Rect? hole)
        {
            var source = PickBlurSource(root);
            if (source == null)
            {
                Log.Warning("[聚光] 窗口根里没有可快照的内容块,这轮只有遮罩没有模糊");
                return;
            }

            var srcW = (float)source.ActualWidth;
            var srcH = (float)source.ActualHeight;
            if (srcW <= 0 || srcH <= 0)
            {
                Log.Warning("[聚光] 快照源 {Item} 尺寸还没定({W}x{H}),这轮不模糊", source.GetType().Name, srcW, srcH);
                return;
            }

            var c = ElementCompositionPreview.GetElementVisual(source).Compositor;
            if (_snapshot == null || !ReferenceEquals(_blurSource, source))
                BuildBlurTree(c, source);

            // SnapshotScale 档把快照本身按 1/8 分辨率渲染 —— 糊的来源是分辨率,不是 D2D 的大核
            // (直径超 100 会直接 E_INVALIDARG,而实测能画出来的那版只有单趟 20)。
            // 其余档沿用实测画得出来的那套:DIP 尺寸 + Stretch.None。
            var snap = Blur == BlurMode.SnapshotScale ? 1f / SnapshotDivisor : 1f;
            _snapshot!.SourceSize = new Vector2(srcW * snap, srcH * snap);

            // sprite 在 overlay(=窗口根)坐标系里定位,采样的是源自己的位图,所以两者原点必须重合
            var origin = source.TransformToVisual(root).TransformBounds(new Rect(0, 0, srcW, srcH));

            // 洞换算到源的局部坐标;NaN = 这轮没洞(定位不到唤起项),整块糊掉
            float hl = float.NaN, hr = 0, ht = 0, hb = 0;
            if (hole.HasValue)
            {
                var h = hole.Value;
                hl = (float)(h.X - origin.X);
                hr = (float)(h.X + h.Width - origin.X);
                ht = (float)(h.Y - origin.Y);
                hb = (float)(h.Y + h.Height - origin.Y);
                if (hl < 0 || ht < 0 || hr > srcW || hb > srcH)
                    Log.Warning("[聚光] 洞有一部分在快照源之外(洞左上({HL},{HT}) 洞右下({HR},{HB}) 源 {W}x{H}),那条边会糊到洞上",
                        hl, ht, hr, hb, srcW, srcH);
            }
            var cut = !float.IsNaN(hl);

            // 0=上,1=下,2=左,3=右:每块都铺满整源、只收自己那一侧,重叠处是同一份模糊结果,看不出来。
            // 收的是"洞之外":上条留 y∈[0,ht] 所以 BottomInset=srcH-ht(不是 -hb),下条留 y∈[hb,srcH]
            // 所以 TopInset=hb。写反过一轮,结果是上下两条各盖住洞、四条并起来铺满全屏。
            var insets = new (float L, float T, float R, float B)[]
            {
                (0, 0, 0, cut ? Math.Max(0, srcH - ht) : 0),
                (0, cut ? Math.Max(0, hb) : 0, 0, 0),
                (0, 0, cut ? Math.Max(0, srcW - hl) : 0, 0),
                (cut ? Math.Max(0, hr) : 0, 0, 0, 0),
            };

            for (int i = 0; i < _bands!.Length; i++)
            {
                var band = _bands[i];
                var clip = c.CreateInsetClip();
                clip.LeftInset = insets[i].L;
                clip.TopInset = insets[i].T;
                clip.RightInset = insets[i].R;
                clip.BottomInset = insets[i].B;
                band.Clip = clip;
                band.Offset = Vector3.Zero;
                band.Size = new Vector2(srcW, srcH);

                var host = _bandHosts![i];
                Canvas.SetLeft(host, origin.X);
                Canvas.SetTop(host, origin.Y);
                host.Width = srcW;
                host.Height = srcH;
                // 显隐只走宿主的 Visibility 这一处。早先用 band.Opacity 收、却没人负责复原,
                // 表现就是"只有第一次开对话框有模糊"。
                host.Visibility = Visibility.Visible;
                ElementCompositionPreview.SetElementChildVisual(host, band);
            }

            // 保留边界直接由 inset 反解出来,与洞的四边逐条对账:上条应 ≤HT、下条应 ≥HB、
            // 左条应 ≤HL、右条应 ≥HR。对不上就是 inset 方向又写反了(上一轮就是这么错的)。
            Log.Information("[聚光] 快照模糊:档={Mode} 强度={Strength} 源={Item} 占位={SW:f0}x{SH:f0} 快照={SSW:f0}x{SSH:f0} 原点=({OX:f0},{OY:f0}) 缩放={Scale:f2} "
                + "洞=({HL:f0},{HT:f0})→({HR:f0},{HB:f0}) 保留=上≤{TopKeep:f0} 下≥{BotKeep:f0} 左≤{LeftKeep:f0} 右≥{RightKeep:f0}",
                Blur,
                Blur == BlurMode.SnapshotScale ? $"快照 1/{SnapshotDivisor:0} + 直径{SnapshotAssistRadius:0}"
                    : Blur == BlurMode.DownSample ? $"1/{DownSample:0} 放大 {DownSample:0}x"
                    : $"{BlurPasses} 趟 x 直径{BlurRadius:0} {BlurOptimization}",
                source.GetType().Name, srcW, srcH, srcW * snap, srcH * snap, origin.X, origin.Y,
                root.XamlRoot?.RasterizationScale ?? 0, hl, ht, hr, hb,
                srcH - insets[0].B, insets[1].T, srcW - insets[2].R, insets[3].L);
        }

        private static void BuildBlurTree(Compositor c, UIElement source)
        {
            _snapshot = c.CreateVisualSurface();
            _snapshot.SourceVisual = ElementCompositionPreview.GetElementVisual(source);

            var surfaceBrush = c.CreateSurfaceBrush();
            surfaceBrush.Surface = _snapshot;
            // Fill 实测两个方向都不画(快照放大一档、缩小一档都一样),所以 None + 与占位同尺寸才是能画的那套。
            // 要降分辨率只能往效果图里想别的办法,不能靠刷的 Stretch。
            surfaceBrush.Stretch = Blur == BlurMode.SnapshotScale ? CompositionStretch.Fill : CompositionStretch.None;

            // 第二实参是"要做成可动画的属性名"(形如 Blur.Radius),不是源参数名:源参数在图里由
            // CompositionEffectSourceParameter 声明,建完刷再 SetSourceParameter 绑。传 "src" 进去
            // 编译期没意见,运行期抛 ArgumentException: Malformed animatable property name。
            var effectBrush = c.CreateEffectFactory(BuildBlurGraph()).CreateBrush();
            effectBrush.SetSourceParameter("src", surfaceBrush);
            _blurBrush = effectBrush;

            _bands = new SpriteVisual[4];
            for (int i = 0; i < 4; i++)
            {
                var band = c.CreateSpriteVisual();
                band.Brush = _blurBrush!;
                _bands[i] = band;
            }

            _blurSource = source;
        }

        /// <summary>
        /// 模糊效果图,各档都只吃一个名为 src 的源参数。
        /// Gaussian = N 趟直径 100 的真高斯:观感最正,但每趟一次全屏卷积,且直径有 100 这个硬上限。
        /// DownSample = 效果图里串两趟缩放:一次卷积都没有,中间那张小图就是全部信息量。
        /// 两趟缩放都以原点为基准且互逆,合起来位置不变,掉掉的只有分辨率。
        /// SnapshotScale = 降分辨率发生在快照本身(见 ApplyBlur),这里只补一趟小半径抹双线性台阶。
        /// </summary>
        private static Windows.Graphics.Effects.IGraphicsEffect BuildBlurGraph()
        {
            Windows.Graphics.Effects.IGraphicsEffectSource input = new CompositionEffectSourceParameter("src");

            if (Blur == BlurMode.SnapshotScale)
                return new Microsoft.Graphics.Canvas.Effects.GaussianBlurEffect
                {
                    Source = input,
                    BlurAmount = SnapshotAssistRadius,
                    BorderMode = Microsoft.Graphics.Canvas.Effects.EffectBorderMode.Hard,
                    Optimization = Microsoft.Graphics.Canvas.Effects.EffectOptimization.Balanced
                };

            if (Blur == BlurMode.DownSample)
            {
                input = new Microsoft.Graphics.Canvas.Effects.Transform2DEffect
                {
                    Source = input,
                    TransformMatrix = Matrix3x2.CreateScale(1f / DownSample)
                };
                return new Microsoft.Graphics.Canvas.Effects.Transform2DEffect
                {
                    Source = input,
                    TransformMatrix = Matrix3x2.CreateScale(DownSample)
                };
            }

            for (int i = 0; i < BlurPasses; i++)
                input = new Microsoft.Graphics.Canvas.Effects.GaussianBlurEffect
                {
                    Source = input,
                    BlurAmount = BlurRadius,
                    BorderMode = Microsoft.Graphics.Canvas.Effects.EffectBorderMode.Hard,
                    Optimization = BlurOptimization
                };

            return (Windows.Graphics.Effects.IGraphicsEffect)input;
        }

        private static void SetBandsVisible(bool visible)
        {
            if (_bandHosts == null) return;
            foreach (var host in _bandHosts)
                host.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 快照源 = 窗口根里遮罩之前的最后一个可见子元素(主窗口就是 NavigationView)。
        /// 不能取窗口根自己:遮罩是它的孩子,那样快照里含模糊层,模糊层又吃快照 → 每帧自我叠加越刷越黑。
        /// </summary>
        private static FrameworkElement? PickBlurSource(Panel root)
        {
            for (int i = root.Children.Count - 1; i >= 0; i--)
            {
                if (root.Children[i] is not FrameworkElement e || ReferenceEquals(e, _overlay)) continue;
                if (e.Visibility != Visibility.Visible) continue;
                return e;
            }
            return null;
        }

        /// <summary>遮罩挂在窗口根的最后,盖住导航与内容;它自己不吃命中,不影响任何点击。</summary>
        private static void EnsureOverlay(Panel root)
        {
            if (_overlay != null && ReferenceEquals(_overlay.Parent, root)) return;

            (_overlay?.Parent as Panel)?.Children.Remove(_overlay);

            _overlay = new Canvas { IsHitTestVisible = false };
            // 换窗口根 = 旧的宿主层跟着旧遮罩一起作废,模糊树得照新的重建
            _blurSource = null;

            // 四块模糊宿主排在 scrim 之前 = 压在它下面(压暗层盖在模糊上,读起来像磨砂玻璃)。
            // 实测两种顺序都画得出来,这里只是选层次。
            _bandHosts = new Border[4];
            for (int i = 0; i < 4; i++)
            {
                _bandHosts[i] = new Border { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
                _overlay.Children.Add(_bandHosts[i]);
            }

            _scrim = new Path
            {
                Fill = new SolidColorBrush(Microsoft.UI.Colors.Black) { Opacity = ScrimOpacity },
                Stretch = Stretch.None
            };
            _overlay.Children.Add(_scrim);

            if (root is Grid grid)
            {
                Grid.SetRow(_overlay, 0);
                Grid.SetRowSpan(_overlay, Math.Max(1, grid.RowDefinitions.Count));
                Grid.SetColumnSpan(_overlay, Math.Max(1, grid.ColumnDefinitions.Count));
            }

            root.Children.Add(_overlay);
        }

        private static Rect? ResolveHole(FrameworkElement? target, Panel root)
        {
            if (target == null) return null;
            if (target.ActualWidth <= 0 || target.ActualHeight <= 0)
            {
                Log.Warning("[聚光] {Item} 尺寸还没定下来,只压暗不挖洞", target.GetType().Name);
                return null;
            }

            try
            {
                return target.TransformToVisual(root)
                    .TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
            }
            catch (ArgumentException ex)
            {
                // 目标不在这棵树里(例如焦点落在别的窗口)——只压暗,不挖洞
                Log.Warning("[聚光] 无法把 {Item} 换算到窗口根坐标:{Msg}", target.GetType().Name, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 从焦点元素回溯出"被唤起的那一项":优先整张设置卡(路径那几行一张卡就是一行),
        /// 退一档取按钮本身(命令栏按钮、属性行里的取色/选文件按钮)。
        /// </summary>
        private static FrameworkElement? ResolveFromFocus(Panel root)
        {
            var focused = (FocusManager.GetFocusedElement(root.XamlRoot) ?? FocusManager.GetFocusedElement())
                as FrameworkElement;
            if (focused == null) return null;

            for (DependencyObject? d = focused; d != null; d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d))
            {
                if (d is SettingsCard or SettingsExpander) return (FrameworkElement)d;
            }

            for (DependencyObject? d = focused; d != null; d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d))
            {
                if (d is ButtonBase) return (FrameworkElement)d;
            }

            return focused;
        }

        private static Rect Clip(Rect r, Rect bounds)
        {
            var x = Math.Max(r.X, 0);
            var y = Math.Max(r.Y, 0);
            var right = Math.Min(r.X + r.Width, bounds.Width);
            var bottom = Math.Min(r.Y + r.Height, bounds.Height);
            return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
        }

        // WinUI 3 的浮层没有入场动画,照 DialogHelper 那条用 Composition 淡入
        private static void FadeIn(UIElement target)
        {
            var visual = ElementCompositionPreview.GetElementVisual(target);
            visual.Opacity = 0f;
            var anim = visual.Compositor.CreateScalarKeyFrameAnimation();
            anim.Target = "Opacity";
            anim.InsertKeyFrame(0f, 0f);
            anim.InsertKeyFrame(1f, 1f);
            anim.Duration = TimeSpan.FromMilliseconds(120);
            visual.StartAnimation("Opacity", anim);
        }
    }
}

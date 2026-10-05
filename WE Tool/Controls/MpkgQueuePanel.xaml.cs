using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using WE_Tool.Helper;
using WE_Tool.Json;
using WE_Tool.Models;
using WE_Tool.Service;

namespace WE_Tool.Controls;

/// <summary>
/// 「转为移动版」的待转队列面板。两个宿主共用这一块:Papers 页的覆盖层,和 <c>--mpkg-window</c> 副窗口(子进程)。
/// 队列、逐行参数、只读探测、开转与进度都在这儿;只有"产物写到哪、进程优先级多少、挂在哪块宿主上"由宿主在装配时给,
/// 因为副窗口进程不读 config.json、也没有主 VM,那两样只能随载荷带进来。
///
/// 队列在一次会话里只有一个持有者:detach 时整份交出去,母进程只留一份防丢的副本,
/// 编辑、探测、开转都在持有者这边做。两边各留一份再互相同步是这份代码最不该变成的形状。
/// </summary>
public sealed partial class MpkgQueuePanel : UserControl, INotifyPropertyChanged
{
    public MpkgQueuePanel() => InitializeComponent();

    /// <summary>待转 mpkg 队列:「转为移动版」先入队到这里,每行单独选缩小档位,再统一开转。</summary>
    public ObservableCollection<MpkgQueueItem> Items { get; } = [];

    // 入队去重用的键集,内容始终与 Items 一一对应(移除/清空两边一起动)
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

    // ---------- 宿主给的三样 ----------

    /// <summary>产物根。页面=下载目录,副窗口=载荷带过来的那一份;空则回落到桌面 WE_OutPut。</summary>
    public string OutputRoot { get; set; } = "";

    /// <summary>repkg 子进程的优先级档位(设置页「性能」那一格的值)。</summary>
    public int ProcessPriority { get; set; }

    private bool _hostedInWindow;

    /// <summary>这块面板挂在独立窗口里(而不是 Papers 页的覆盖层上)。决定头部露哪颗换宿主按钮,
    /// 以及标题栏摆法(独立窗口=属性窗口那条 48 高居中带,页面=左对齐带图标)。</summary>
    public bool HostedInWindow
    {
        get => _hostedInWindow;
        set
        {
            if (_hostedInWindow == value) return;
            _hostedInWindow = value;
            OnPropertyChanged(nameof(HostedInWindow));
            OnPropertyChanged(nameof(DetachButtonVisibility));
            OnPropertyChanged(nameof(DockButtonVisibility));
            OnPropertyChanged(nameof(TitleChromeVisibility));
            OnPropertyChanged(nameof(CenteredTitleVisibility));
            OnPropertyChanged(nameof(CloseButtonVisibility));
            OnPropertyChanged(nameof(HeaderMinHeight));
            OnPropertyChanged(nameof(HeaderPadding));
            Log.Information("[mpkg队列] 面板宿主切到{Host}, 标题栏行高 {Height}",
                value ? "独立窗口" : "页面覆盖层", HeaderMinHeight);
        }
    }

    /// <summary>
    /// 标题栏右侧要让开多宽(系统按钮区 + 它左边的拖条)。由独立窗口那边按 AppWindow.TitleBar.RightInset 算好了发进来,
    /// 不在这里写死:系统按钮的总宽会随 DPI 与按钮个数变,主窗口那条也是运行时取的同一个值。
    /// </summary>
    public double TitleBarRightReserve
    {
        get => _titleBarRightReserve;
        set
        {
            if (Math.Abs(_titleBarRightReserve - value) < 0.5) return;
            _titleBarRightReserve = value;
            MpkgQueueHeaderButtons.Margin = new Thickness(0, 0, value, 0);
            Log.Information("[mpkg队列] 标题栏右侧让位 {Reserve}", value);
        }
    }
    private double _titleBarRightReserve;

    /// <summary>挂在独立窗口时头部那一排可点的键 —— 窗口那边要拿它的宽去登记 Passthrough 输入区,
    /// 否则点击会落在系统标题栏上而到不了按钮。页面宿主里没有系统标题栏,所以给 null。</summary>
    public FrameworkElement? HeaderActions => HostedInWindow ? MpkgQueueHeaderButtons : null;

    public Visibility DetachButtonVisibility => HostedInWindow ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DockButtonVisibility => HostedInWindow ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>左图标+左标题那一组只在页面覆盖层里摆;独立窗口用居中那一条(属性窗口的摆法)。</summary>
    public Visibility TitleChromeVisibility => HostedInWindow ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CenteredTitleVisibility => HostedInWindow ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>独立窗口有系统关闭键,面板头部那颗 X 就是重复的;它做的事(停批+收回队列)窗口那边照样会做。</summary>
    public Visibility CloseButtonVisibility => HostedInWindow ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>标题行高度:独立窗口要够到 Tall 标题栏的 48,页面覆盖层里保持内容自撑。</summary>
    public double HeaderMinHeight => HostedInWindow ? 48 : 0;

    /// <summary>独立窗口里这一行就是标题栏本身,所以竖直方向不能再加内边距 ——
    /// 加了就顶到 56 高,居中的标题与系统按钮那排就不在同一条线上了。</summary>
    public Thickness HeaderPadding => HostedInWindow
        ? new Thickness(16, 0, 12, 0)
        : new Thickness(16, 12, 12, 12);

    /// <summary>「转到独立窗口」/「贴回页面」按下去 —— 开窗口这事归宿主,面板只把意图报上去。</summary>
    public event Action? DetachRequested;
    public event Action? DockRequested;

    /// <summary>头部那颗 X。页面宿主收起覆盖层;副窗口宿主关掉自己的窗口(队列在这之前已经交回)。</summary>
    public event Action? CloseRequested;

    /// <summary>队列或面板状态变了。副窗口据此把这一份存回母进程,防子进程被打掉时整批待转项跟着没。</summary>
    public event Action? QueueChanged;

    /// <summary>转换开始/结束(参数=还在转)。副窗口据此告诉母进程"这段时间别开提取"。</summary>
    public event Action<bool>? ConversionBusyChanged;

    /// <summary>一批跑完要不要发系统通知由宿主定:页面发,副窗口不发(它自己那条进度就一直亮着)。</summary>
    public event Action<string, string>? NotifyRequested;

    // ===================== 入队 =====================

    /// <summary>把这几张壁纸放进队列,返回新增了几条(重复的不算)。展开面板由宿主做。</summary>
    public int Enqueue(IReadOnlyList<WallpaperItem> wallpapers)
    {
        int added = 0, skipped = 0;
        foreach (var wallpaper in wallpapers)
        {
            var item = new MpkgQueueItem(wallpaper);
            if (!_keys.Add(item.Key))
            {
                skipped++;
                continue;
            }
            // 新行取那份默认档;之后按行改,不影响后面入队的行
            item.Tier = MpkgPackingDefaults.Tier;
            item.SettingsVisibility = IsCustomMode ? Visibility.Visible : Visibility.Collapsed;
            AttachRow(item);
            Items.Add(item);
            added++;
        }

        Log.Information("[mpkg队列] 入队: 选中 {Selected} 新增 {Added} 重复跳过 {Skipped} 队列共 {Total}",
            wallpapers.Count, added, skipped, Items.Count);
        if (added > 0) QueueChanged?.Invoke();
        return added;
    }

    /// <summary>另一头投递进来的行(副窗口正持有队列时,母进程把新选的几张发过来走这条)。</summary>
    public void AddRows(IReadOnlyList<MpkgQueueRowDto> rows)
    {
        int added = 0;
        var show = IsCustomMode ? Visibility.Visible : Visibility.Collapsed;
        foreach (var row in rows)
        {
            var item = MpkgQueueItem.FromRowDto(row, show);
            if (!_keys.Add(item.Key)) continue;
            AttachRow(item);
            Items.Add(item);
            added++;
        }
        Log.Information("[mpkg队列] 收到投递 {Given} 行,实际入队 {Added}, 队列共 {Total}", rows.Count, added, Items.Count);
        UpdateCountText();
        ScheduleProbe();
        if (added > 0) QueueChanged?.Invoke();
    }

    /// <summary>
    /// 读数挂在模型的 PropertyChanged 上而不是控件事件上:拖动、方向键、点轨道这三条改档路径都会走到这里。
    /// lambda 只捕获 item 自身,所以行被移除之后这条订阅不会把行模型钉住。
    /// </summary>
    private void AttachRow(MpkgQueueItem item)
    {
        item.PropertyChanged += (_, ev) =>
        {
            switch (ev.PropertyName)
            {
                case nameof(MpkgQueueItem.Tier):
                    if (_applyingMasterControl) return;
                    // 单行降回原始档时,那条 ETC2 覆盖是被护栏吃掉的,不说就成了静默改动
                    Log.Information("[mpkg队列] 档位: {Name} → {Tier}({Text}){Cleared} {Flags}",
                        item.Name, item.EffectiveTier, item.TierText,
                        item.TakeTierClearedEtc2() ? ", 顺带清掉该行的 ETC2 覆盖" : "", item.FlagsReadout);
                    ScheduleProbe();
                    QueueChanged?.Invoke();
                    return;
                case nameof(MpkgQueueItem.KeepAudioOn):
                case nameof(MpkgQueueItem.UseLz4On):
                case nameof(MpkgQueueItem.ShaderCompatOn):
                case nameof(MpkgQueueItem.Etc2On):
                case nameof(MpkgQueueItem.CopyTexturesOn):
                case nameof(MpkgQueueItem.ShrinkDxOn):
                    // 总控套档会连带改一批 ETC2,那种场合由总控那一条读数汇总,别一行刷一条
                    if (_applyingMasterControl) return;
                    Log.Information("[mpkg队列] 逐行 {Flag}: {Name} → {Value} {Flags}",
                        ev.PropertyName, item.Name, FlagOf(item, ev.PropertyName) ? "开" : "关",
                        item.FlagsReadout);
                    // 只有会影响"缩几条"的两颗键才值得重探一次;音频/LZ4/兼容改写改的是别的条目
                    if (ev.PropertyName is nameof(MpkgQueueItem.Etc2On) or nameof(MpkgQueueItem.CopyTexturesOn)
                        or nameof(MpkgQueueItem.ShrinkDxOn))
                        ScheduleProbe();
                    QueueChanged?.Invoke();
                    return;
                case nameof(MpkgQueueItem.NameModeIndex):
                    // 这格不进 repkg 的 options(名字是我们算好交给 outputName 的),但它改的是产物文件名,所以照样报一条
                    Log.Information("[mpkg队列] 逐行 文件重命名: {Name} → {Mode}", item.Name, item.NameModeReadout);
                    QueueChanged?.Invoke();
                    return;
                case nameof(MpkgQueueItem.IsExpanded):
                    QueueChanged?.Invoke();
                    return;
            }
        };
        // 每一条进来的路(页面入队、投递、搬家重建)都过这里,所以模糊判定挂在行上而不是宿主上
        UpdateRowBlur(item);
    }

    // ===================== 预览模糊(年龄段) =====================

    private bool _blurEveryone, _blurTeen, _blurAdult;

    /// <summary>预览模糊那三档年龄段开关:页面宿主由 Papers 从主 VM 推,副窗口宿主由载荷和 KindTheme 推。
    /// 关掉任何一档都会把已糊上的行撤下来 —— 开关是"现在该不该糊",不是"生成一次就留着"。</summary>
    public void SetBlurFlags(bool everyone, bool teen, bool adult)
    {
        if (_blurEveryone == everyone && _blurTeen == teen && _blurAdult == adult) return;
        _blurEveryone = everyone;
        _blurTeen = teen;
        _blurAdult = adult;
        Log.Information("[mpkg队列] 预览模糊开关 everyone={Everyone} teen={Teen} adult={Adult}, 重刷 {Rows} 行",
            everyone, teen, adult, Items.Count);
        ApplyBlur();
    }

    private bool AnyBlurOn => _blurEveryone || _blurTeen || _blurAdult;

    private bool RowWantsBlur(MpkgQueueItem item)
        => AnyBlurOn && BlurPreviewService.ShouldBlur(item.ContentRating, _blurEveryone, _blurTeen, _blurAdult);

    /// <summary>整队重算(开关变了才走这条;新行由 AttachRow 单独算一次)。</summary>
    private void ApplyBlur()
    {
        foreach (var row in Items) UpdateRowBlur(row);
    }

    /// <summary>一行该糊就异步取模糊图、该清就把原图那层亮回来。结论写在行模型上,
    /// 所以 ItemsRepeater 回收控件不会把 A 行的糊图盖到 B 行(那正是页面卡片那边要 _blurOverlayOwner 防的事)。</summary>
    private void UpdateRowBlur(MpkgQueueItem row)
    {
        if (!RowWantsBlur(row) || string.IsNullOrEmpty(row.Preview) || !File.Exists(row.Preview))
        {
            row.SetBlur(null);
            return;
        }
        _ = ApplyRowBlurAsync(row);
    }

    private async Task ApplyRowBlurAsync(MpkgQueueItem row)
    {
        try
        {
            var blurred = await BlurPreviewService.GetBlurredPreviewAsync(row.Preview!);
            if (blurred == null) return; // 探不到就留原图:这一行不该因为模糊失败而空白
            // await 期间开关可能已经变了,复查一次才上屏
            if (!RowWantsBlur(row)) return;
            row.SetBlur(blurred);
            Log.Information("[mpkg队列] 行预览已糊: {Name} ({Rating})", row.Name, row.ContentRating);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[mpkg队列] 行预览模糊失败: {Path}", row.Preview);
        }
    }

    private static bool FlagOf(MpkgQueueItem item, string? flag) => flag switch
    {
        nameof(MpkgQueueItem.KeepAudioOn) => item.KeepAudioOn,
        nameof(MpkgQueueItem.UseLz4On) => item.UseLz4On,
        nameof(MpkgQueueItem.ShaderCompatOn) => item.ShaderCompatOn,
        nameof(MpkgQueueItem.CopyTexturesOn) => item.CopyTexturesOn,
        nameof(MpkgQueueItem.ShrinkDxOn) => item.ShrinkDxOn,
        _ => item.Etc2On,
    };

    // ===================== 跨进程搬家 =====================

    /// <summary>整份队列,交给另一个宿主。档位交的是<b>存下的意图</b>而不是生效值,原因见 MpkgQueueItem.ToRowDto。</summary>
    public List<MpkgQueueRowDto> ExportRows() => Items.Select(i => i.ToRowDto()).ToList();

    public MpkgPanelStateDto ExportPanelState() => new()
    {
        MasterTier = (int)Math.Round(_masterTierValue),
        NameMode = MpkgPackingDefaults.NameMode,
        CustomMode = IsCustomMode,
    };

    /// <summary>
    /// 收下另一头搬来的整份状态。先把重命名那一档落进全局默认,再建行 ——
    /// 行上的生效值是现读 <see cref="MpkgPackingDefaults.NameMode"/> 的,顺序倒了会把"跟随总控"读成旧的默认。
    /// 总控 slider 那一路故意不走 setter:它是"命令"不是"状态",一边走一边把刚搬来的行全部套成同一档就白搬了。
    /// </summary>
    public void ImportState(MpkgPanelStateDto panel, IReadOnlyList<MpkgQueueRowDto> rows)
    {
        MpkgPackingDefaults.NameMode = panel.NameMode;
        _masterTierValue = panel.MasterTier;
        OnPropertyChanged(nameof(MasterTierValue));
        OnPropertyChanged(nameof(MasterTierText));
        // 先摆控件再改模式:赋值 IsChecked 会触发 Checked/Unchecked,那条路会把 IsCustomMode 一起带到位
        MpkgQueueCustomButton.IsChecked = panel.CustomMode;
        IsCustomMode = panel.CustomMode;

        Items.Clear();
        _keys.Clear();
        _probeSeen.Clear();
        var show = panel.CustomMode ? Visibility.Visible : Visibility.Collapsed;
        foreach (var row in rows)
        {
            var item = MpkgQueueItem.FromRowDto(row, show);
            _keys.Add(item.Key);
            AttachRow(item);
            Items.Add(item);
        }

        UpdateCountText();
        Log.Information("[mpkg队列] 收下搬来的状态: {Rows} 行, 自定义={Custom}, 总控={Master}, 重命名={Rename}",
            Items.Count, panel.CustomMode ? "开" : "关", MpkgQueueItem.TierLabel(panel.MasterTier),
            panel.NameMode == 1 ? "ID" : "标题");
    }

    // ===================== 只读探测:这一档到底会不会缩 =====================

    // repkg 的 mode:inspect 一个字节都不写,它回答的是"这一行的档位真会缩几条纹理"。
    // 值得单独问一遍,是因为转换器对"照搬"什么都不说:一张全是 DXT5 的壁纸,选 1× 和选 4× 产物一样大,
    // 而那件事原本只有等转换跑完、对着两个同样大小的文件才看得出来。
    // 全限定:这个文件里 Windows.System 也在作用域内,裸写 DispatcherQueueTimer 会挑错那一个
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _probeDebounce;
    private CancellationTokenSource? _probeCts;

    // 行键 → 已探过的那套档位口径。签名没变就不重探:一次探测是一个进程,几十行的队列不能每拖一下 slider 就来一遍。
    private readonly Dictionary<string, string> _probeSeen = new(StringComparer.Ordinal);

    /// <summary>把改动的行攒一攒再探:拖 slider / 连开几格开关只触发一次进程。</summary>
    private void ScheduleProbe()
    {
        if (Items.Count == 0) return;

        if (_probeDebounce is null)
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(600);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => _ = RunProbeAsync();
            _probeDebounce = timer;
        }

        _probeDebounce.Stop();
        _probeDebounce.Start();
    }

    private async Task RunProbeAsync()
    {
        var rows = new List<MpkgQueueItem>();
        foreach (var item in Items)
        {
            if (string.IsNullOrEmpty(item.Wallpaper.FolderPath)) continue;
            if (_probeSeen.TryGetValue(item.Key, out var seen) && seen == item.ProbeSignature) continue;
            rows.Add(item);
        }

        if (rows.Count == 0) return;

        // 上一轮还没跑完就再改档位:取消它。探测结果按签名贴回,慢回来的那一批只会被丢掉。
        _probeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _probeCts = cts;

        try
        {
            _service ??= new RepkgCliService();
            var probes = await _service.ProbeMobileAsync(
                rows.Select(r => (r.Key, r.Wallpaper, r.Snapshot())).ToList(), cts.Token);

            int warned = 0;
            foreach (var row in rows)
            {
                if (!probes.TryGetValue(row.Key, out var probe)) continue;
                _probeSeen[row.Key] = row.ProbeSignature;
                ApplyProbe(row, probe);
                if (row.ProbeIsWarning) warned++;
            }

            Log.Information("[mpkg探测] 本轮 {Rows} 行有结论,其中 {Warn} 行标了\"该档位无法缩小纹理\"",
                probes.Count, warned);
        }
        catch (OperationCanceledException)
        {
            Log.Information("[mpkg探测] 口径又变了,丢掉这一轮结果");
        }
        catch (Exception ex)
        {
            // 探测只是让界面多一句话。它坏了不能挡转换,也不能在界面上留下半个字。
            Log.Warning(ex, "[mpkg探测] 这一轮没跑成,队列上不显示档位提示");
        }
        finally
        {
            if (ReferenceEquals(_probeCts, cts)) cts.Dispose();
            _probeCts = null;
        }
    }

    private static void ApplyProbe(MpkgQueueItem row, MpkgProbe probe)
    {
        row.ProbeIsWarning = false;

        // 每条分支都要同时落"短词 + 长句":短词摆在名称下面,长句进那颗 i 的 ToolTip。
        // 同一件事写两种长度,是因为一列几十行装不下整句话(会把行高顶开、把名称挤成两行)。
        if (probe.Packages == 0 || probe.Failed || probe.Tex == 0)
        {
            // 读不动的包由转换自己报错;没有 .tex 的壁纸本来就没什么可缩。这里都不该抢话
            row.ProbeShort = null;
            row.ProbeNote = null;
            return;
        }

        // 三种"没缩"要分开说:开关是你开的、素材本来就不缩、还是只缩了一部分。
        // 合成一句的话,"照搬"这条就永远看不出是哪种,而它的处理办法完全不同。
        if (row.CopyTexturesOn)
        {
            row.ProbeShort = LanguageHelper.GetResource("MpkgQueue_ProbeCopiedShort.Text");
            row.ProbeNote = string.Format(LanguageHelper.GetResource("MpkgQueue_ProbeCopied.Text"),
                probe.Tex, probe.DxtSharePercent);
            return;
        }

        if (row.Tier > 0 && probe.WouldReduce == 0)
        {
            row.ProbeIsWarning = true;
            row.ProbeShort = LanguageHelper.GetResource("MpkgQueue_ProbeStuckShort.Text");
            row.ProbeNote = string.Format(LanguageHelper.GetResource("MpkgQueue_ProbeStuck.Text"),
                probe.Tex, probe.DxtSharePercent);
            return;
        }

        if (row.Tier > 0 && probe.WouldReduce < probe.Tex)
        {
            row.ProbeShort = string.Format(LanguageHelper.GetResource("MpkgQueue_ProbePartialShort.Text"),
                probe.WouldReduce, probe.Tex);
            row.ProbeNote = string.Format(LanguageHelper.GetResource("MpkgQueue_ProbePartial.Text"),
                probe.WouldReduce, probe.Tex);
            return;
        }

        // 原始档本来就什么都不做,不必报"一条都没缩";全缩到了也不用报告成功
        row.ProbeShort = null;
        row.ProbeNote = null;
    }

    // ===================== 总控 =====================

    private double _masterTierValue;

    /// <summary>
    /// 那根总控 slider 的绑定面:它不表示队列的某种状态,只负责"把这一档套到所有行",
    /// 所以初值就是默认的 1×,而且套完之后也不回头去显示什么"统一档位"。
    /// </summary>
    public double MasterTierValue
    {
        get => _masterTierValue;
        set
        {
            if (_masterTierValue.Equals(value)) return;
            _masterTierValue = value;
            OnPropertyChanged(nameof(MasterTierText));
            ApplyTierToAll(value);
        }
    }

    /// <summary>总控 slider 右边的倍数文字,和队列每行那一格同样式;它跟的是总控自己那根 thumb 的位置。</summary>
    public string MasterTierText => MpkgQueueItem.TierLabel((int)Math.Round(_masterTierValue));

    // -1 = 这一轮还没人碰过重命名总控,那时生效值是 MpkgPackingDefaults.NameMode 那份默认(0=标题)
    private int _masterNameModeValue = -1;

    /// <summary>
    /// 档位下面那根「文件重命名」总控。它改的是<b>磁盘上 .mpkg 的文件名</b> —— 手机读的是包内
    /// project.json 的 title,与文件名无关(WE 自家的移动导出就是拿创意工坊 ID 当文件名)。
    /// 挪它做两件事:把默认换成这一档 + 让所有行回到"跟随总控"。故意不给每行写死值,
    /// 那样一整批行都会亮"已改"角标,而那个角标的用处就是扫出少数例外行。
    /// </summary>
    public int MasterNameModeValue
    {
        get => _masterNameModeValue >= 0 ? _masterNameModeValue : MpkgPackingDefaults.NameMode;
        set
        {
            if (value is < 0 or > 1) return;   // RadioButtons 清空时会递 -1 过来,那不是任何一种模式
            if (_masterNameModeValue == value) return;
            _masterNameModeValue = value;
            MpkgPackingDefaults.NameMode = value;

            int followed = 0;
            _applyingMasterControl = true;
            try
            {
                foreach (var item in Items)
                {
                    item.RefreshGlobalDefaults();
                    if (item.FollowMasterNameMode()) followed++;
                }
            }
            finally
            {
                _applyingMasterControl = false;
            }

            Log.Information("[mpkg队列] 重命名总控 → {Mode}, {Followed} 行回到跟随总控, 队列共 {Count} 行",
                value == 1 ? "ID" : "标题", followed, Items.Count);
            QueueChanged?.Invoke();
        }
    }

    // 总控(档位或重命名)一次性改一批行时压掉每行那条读数,不然几十行队列会刷出几十行日志
    private bool _applyingMasterControl;

    private void ApplyTierToAll(double tierValue)
    {
        var tier = (int)Math.Round(tierValue);
        if (Items.Count == 0)
        {
            Log.Warning("[mpkg队列] 总控移到 {Tier},但队列为空,没有可套用的行", MpkgQueueItem.TierLabel(tier));
            return;
        }

        _applyingMasterControl = true;
        try
        {
            foreach (var item in Items) item.Tier = tier;
        }
        finally
        {
            _applyingMasterControl = false;
        }

        // 套到原始档会把「ETC2 强制开」这类非法覆盖吃掉(护栏在 MpkgQueueItem.Tier 里),这条不能不说:
        // 否则用户会以为总控只改了倍数,其实顺手清掉了他逐行写下的编码选择。
        int cleared = Items.Count(item => item.TakeTierClearedEtc2());
        // 开着照搬的行收到的是"意图",发出去的还是原始档 —— 不同步说一声,总控那一条读数就成了假话。
        int copied = tier > 0 ? Items.Count(item => item.CopyTexturesOn) : 0;
        Log.Information("[mpkg队列] 总控 → {Tier}, 已同步 {Count} 行{Cleared}{Copied}",
            MpkgQueueItem.TierLabel(tier), Items.Count,
            cleared > 0 ? $", 顺带清掉 {cleared} 行的 ETC2 覆盖" : "",
            copied > 0 ? $", {copied} 行开着照搬所以仍按 1× 发" : "");
        // 一批一起探:整条队列换档只需要一次进程,不是每行一次
        ScheduleProbe();
        QueueChanged?.Invoke();
    }

    // ===================== 自定义模式:逐行覆盖 音频 / LZ4 / 着色器改写 / ETC2 / 纹理照搬 / 缩小 DXT / 包名 =====================

    private bool _isCustomMode;

    /// <summary>底部那个「自定义」开关。它是整块面板的模式,不是某一行的状态。</summary>
    public bool IsCustomMode
    {
        get => _isCustomMode;
        set
        {
            if (_isCustomMode == value) return;
            _isCustomMode = value;

            var show = value ? Visibility.Visible : Visibility.Collapsed;
            int dropped = 0;
            foreach (var item in Items)
            {
                item.SettingsVisibility = show;
                if (!value && item.HasOverride)
                {
                    item.ResetOverrides();
                    dropped++;
                }
            }

            // 行内多出一个箭头按钮就把整行内容往左挤,总控那根 slider 的右边得跟着让,否则它不再压在各行 slider 上。
            // 44 = 那颗按钮的宽度(与移除按钮同风格)+ StackPanel 的 4px 间距;和原本的 86 一样是写死的数,
            // 只能靠眼睛校 —— 所以把值打进读数,看着不对直接报一个数就行。
            MpkgQueueMasterRow.Margin = new Thickness(0, 10, value ? 130 : 86, 0);
            // 关掉就把覆盖清空(ToolTip 里承诺了这一点):留着的话下次再开会出现"界面上看不见、清单里却带着"的幽灵覆盖
            Log.Information("[mpkg队列] 自定义模式 {State}, 逐行覆盖清空 {Dropped} 行, 队列共 {Count} 行, 总控右边距 {Margin}",
                value ? "开" : "关", dropped, Items.Count, value ? 130 : 86);
            // 只有清空了覆盖才值得重探:开着的时候切模式不动任何选项,提示也就没有变化
            if (dropped > 0) ScheduleProbe();
            QueueChanged?.Invoke();
        }
    }

    private void MpkgQueueCustom_Changed(object sender, RoutedEventArgs e)
        => IsCustomMode = MpkgQueueCustomButton.IsChecked == true;

    private void MpkgQueueRowExpand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: MpkgQueueItem item })
        {
            Log.Warning("[mpkg队列] 展开逐行参数失败:拿不到行数据");
            return;
        }
        item.IsExpanded = !item.IsExpanded;
        Log.Information("[mpkg队列] {State}逐行参数条: {Name} 档位={Tier} {Flags}",
            item.IsExpanded ? "展开" : "收起", item.Name, item.TierText, item.FlagsReadout);
    }

    private void MpkgQueueRowReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: MpkgQueueItem item })
        {
            Log.Warning("[mpkg队列] 重置逐行参数失败:拿不到行数据");
            return;
        }
        item.ResetOverrides();
        Log.Information("[mpkg队列] 逐行参数重置为全局: {Name} {Flags}", item.Name, item.FlagsReadout);
    }

    // ===================== 面板开合(可见性与动画归宿主,这里只管副作用) =====================

    /// <summary>宿主把面板展开之后调一次:补探之前被打断的那些行,并把计数写对。</summary>
    public void NotifyOpened()
    {
        UpdateCountText();
        // 收起期间开关可能改过好几轮,而挂起来的行不会重算,所以展开时按当前开关整队补一次
        ApplyBlur();
        // 补探:上一轮被收起打断的那些行签名还没记下,这里一次补回来(已探过的行按签名跳过)
        ScheduleProbe();
    }

    /// <summary>宿主收起面板时调一次:面板都收起了还留着探测进程没有意义。进度那一块跟着让位。</summary>
    public void NotifyClosed()
    {
        // 已经贴上去的提示不清 —— 收起不等于放弃队列。
        _probeDebounce?.Stop();
        _probeCts?.Cancel();
        if (_progressVisible)
        {
            _progressVisible = false;
            OnPropertyChanged(nameof(ProgressVisibility));
        }
        Log.Information("[mpkg队列] 面板收起, 队列保留 {Total} 项", Items.Count);
    }

    private void UpdateCountText()
        => MpkgQueueCountText.Text = string.Format(
            LanguageHelper.GetResource("MpkgQueue_Count.Text"), Items.Count);

    /// <summary>清空整份队列(去重键一起清)。宿主把队列搬到另一个地方之后调这个,不留第二份可改的。</summary>
    public void ClearAll()
    {
        Items.Clear();
        _keys.Clear();
        _probeSeen.Clear();
        UpdateCountText();
    }

    /// <summary>宿主展开面板时把焦点送进「开始转换」:Esc 只在焦点子树内冒泡,而按钮在控件里,外面拿不到。</summary>
    public bool FocusStartButton() => MpkgQueueStartButton.Focus(FocusState.Programmatic);

    private void MpkgQueueRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: MpkgQueueItem item })
        {
            Log.Warning("[mpkg队列] 移除失败:拿不到行数据");
            return;
        }
        Items.Remove(item);
        _keys.Remove(item.Key);
        _probeSeen.Remove(item.Key);
        UpdateCountText();
        Log.Information("[mpkg队列] 移除 {Name}, 队列剩 {Total}", item.Name, Items.Count);
        QueueChanged?.Invoke();
    }

    private void MpkgQueueClear_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("[mpkg队列] 清空 {Total} 项", Items.Count);
        Items.Clear();
        _keys.Clear();
        _probeSeen.Clear();
        UpdateCountText();
        QueueChanged?.Invoke();
    }

    private void MpkgQueueClose_Click(object sender, RoutedEventArgs e)
    {
        // 正在转的时候按 X:先把这批停下,再让宿主收面板 —— 收成一个"后台还在转、屏幕上看不见的转"是最坏的形状。
        // 停下不等于放弃队列:没转完的行原样留着(与 Stop 那条路径同一套收尾)。
        if (IsBusy)
        {
            Log.Information("[mpkg队列] 面板关闭时先停下这一批: 队列 {Total} 项", Items.Count);
            StopConversion();
        }
        CloseRequested?.Invoke();
    }

    private void MpkgQueueDetach_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            Log.Warning("[mpkg队列] 换窗口被拒:这一批还在转,先停下再换宿主");
            _ = DialogHelper.ShowMessageAsync("提示", "这一批还在转换,先停下再转到独立窗口。");
            return;
        }
        DetachRequested?.Invoke();
    }

    private void MpkgQueueDock_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            Log.Warning("[mpkg队列] 贴回被拒:这一批还在转,先停下再换宿主");
            _ = DialogHelper.ShowMessageAsync("提示", "这一批还在转换,先停下再贴回页面。");
            return;
        }
        DockRequested?.Invoke();
    }

    // ===================== 开转与进度 =====================

    private RepkgCliService? _service;
    private CancellationTokenSource? _cts;
    private int _total, _done;
    private bool _navBadgeError;
    private bool _paused;

    // 这批的每张都已结算(完成、失败都算),但 repkg 自己还在收尾、这边还在等它退出。
    private bool _allSettled;

    private bool _isBusy;

    /// <summary>这一批还在转。宿主用它挡住并发的其它动作(页面的提取入口要读它)。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanStart));
            ConversionBusyChanged?.Invoke(value);
        }
    }

    /// <summary>正在转的时候「开始转换」按不下去:两批 repkg 抢同一批核,而第二批会把这一批的收尾计数搅乱。</summary>
    public bool CanStart => !IsBusy;

    /// <summary>进度那一块亮不亮。跑完之后仍然亮着(留着一句"转换完成 / 输出在哪"),直到面板收起或换宿主。</summary>
    public Visibility ProgressVisibility => _progressVisible ? Visibility.Visible : Visibility.Collapsed;

    private bool _progressVisible;

    /// <summary>每张都结算完之后,停止与暂停就没有可下的手了:repkg 只是正在收尾退出,而 Kill 它会把
    /// 已经写好的那些包一起带走。总进度条走到头的那几秒里,这两颗键按下去只会骗人,所以当场收起。</summary>
    public Visibility StopButtonVisibility => IsBusy && !_allSettled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PauseButtonVisibility => IsBusy && !_allSettled && !_paused ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResumeButtonVisibility => IsBusy && _paused ? Visibility.Visible : Visibility.Collapsed;

    private string _progressStateText = "";
    public string ProgressStateText => _progressStateText;

    private string _progressDetailText = "";
    public string ProgressDetailText => _progressDetailText;

    private double _progressValue;
    public double ProgressValue => _progressValue;

    private async void MpkgQueueStart_Click(object sender, RoutedEventArgs e)
    {
        if (Items.Count == 0)
        {
            Log.Warning("[mpkg队列] 开始转换被拒:队列为空");
            await DialogHelper.ShowMessageAsync("提示", "队列为空。");
            return;
        }
        if (IsBusy)
        {
            Log.Warning("[mpkg队列] 开始转换被拒:这一批还没跑完");
            return;
        }

        var queue = Items.ToList();
        // 探测是另一个 repkg 进程,和转换抢同一批核。开转之前先把它停了:转换期不再需要队列上的那句提示。
        _probeDebounce?.Stop();
        _probeCts?.Cancel();
        await RunQueueAsync(queue);
    }

    /// <summary>跑完一批就把转成功的行移出队列(停止/异常时整条队列原样保留)。</summary>
    private async Task RunQueueAsync(IReadOnlyList<MpkgQueueItem> queue)
    {
        // 只用于那句"档位分布"读数:现在一批走完全队,档位数不再决定批数。
        // 统计的是生效档位 —— 开着照搬的行发出去的就是 1×,按意图分布报会让人对不上清单。
        var tiers = queue.GroupBy(i => i.EffectiveTier).OrderBy(g => g.Key).ToList();

        // 产物根由宿主给(与提取共用同一个下载目录,转出来的 .mpkg 就在提取产物旁边)
        string outputPath = OutputRoot;
        if (string.IsNullOrEmpty(outputPath))
            outputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "WE_OutPut");

        // 没包的壁纸在 Service 里直接报「失败」,不记下来就会被一起当成转完
        var failedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            IsBusy = true;
            _paused = false;
            _progressVisible = true;
            _total = queue.Count;
            _done = 0;
            _allSettled = false;
            // 上一批残留的行内条先抹平,不然开新批时旧的那根会先跳一下再被这次的事件改写;
            // 红标一起退掉 —— 这次重跑成功的那张不该继续顶着一根上一轮的错误条
            foreach (var row in queue)
            {
                row.IsError = false;
                row.EntryProgress = 0;
            }
            _progressValue = 0;
            _progressStateText = "正在转换...";
            _progressDetailText = $"已完成 0/{queue.Count} 个壁纸";
            RaiseProgress();
            TaskbarProgressService.SetProgress(0);
            _navBadgeError = false;
            NavBadgeService.SetBadge("Papers", queue.Count);

            _service = new RepkgCliService();
            _cts = new CancellationTokenSource();

            Action<string> onProgress = msg =>
            {
                var parts = msg.Split('|');
                var name = parts[0];
                // 汇总类消息("转换完成，共 N 个壁纸")不含 '|',防御性取默认值,避免越界崩溃
                var action = parts.Length > 1 ? parts[1] : "";
                double pct = parts.Length > 2 && double.TryParse(parts[2], out var parsed) ? parsed : 0;

                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (action == "失败")
                    {
                        failedNames.Add(name);
                        MarkRowError(name);
                    }
                    if (action == "开始" || action == "解析PKG")
                    {
                        // 总进度按"第几张"走,单张内部那一格走行上的条;明细这行照旧点出"正在转哪一张"。
                        SetRowProgress(name, pct);
                        _progressDetailText = $"已完成 {_done}/{_total} 个壁纸 · 正在转 {name} {pct:F0}%";
                        RaiseProgress();
                    }
                    else if (action == "完成" || action == "失败")
                    {
                        // 失败同样算"这张处理完了":否则选中项里没有 pkg 的那些永远不进计数,进度卡在 N-1/N
                        SetRowProgress(name, 100);
                        _done++;
                        if (!_allSettled && _done >= _total)
                        {
                            _allSettled = true;
                            // 进度条已经满了,这句还写"正在转换"就是它看着像假死的另一半
                            _progressStateText = "已完成";
                            Log.Information("[mpkg队列] {Done}/{Total} 全部结算,收起停止/暂停键,等 repkg 收尾退出",
                                _done, _total);
                        }
                        _progressValue = (double)_done / _total * 100;
                        _progressDetailText = $"已完成 {_done}/{_total} 个壁纸";
                        RaiseProgress();
                        TaskbarProgressService.SetProgress(_progressValue);
                        NavBadgeService.SetBadge("Papers", _total - _done);
                    }
                });
            };

            RepkgCliService.SetProcessPriorityLevel(ProcessPriority);

            // 整条队列一批发完:每行的档位(+ 自定义模式下动过的那几个键)写进 wallpapers[].options,
            // 由 repkg 逐条覆盖全局(以前一个档一批,一批一次进程)。产物平铺在输出根下、一张一个文件,
            // 且总是重做而不是跳过已有产物。
            var mobileOptions = new Dictionary<WallpaperItem, MpkgEntryOptions>(queue.Count);
            foreach (var item in queue) mobileOptions[item.Wallpaper] = item.Snapshot();
            Log.Information("[mpkg队列] 一批 {Count} 张, 档位分布 {Tiers}, 逐行覆盖 {Override} 张, 宿主={Host}, 输出={Output}",
                queue.Count,
                string.Join(" ", tiers.Select(g => $"{MpkgQueueItem.TierLabel(g.Key)}×{g.Count()}")),
                queue.Count(i => i.HasOverride),
                HostedInWindow ? "副窗口" : "页面面板",
                outputPath);

            await _service.ConvertToMobileAsync(
                queue.Select(i => i.Wallpaper).ToList(), outputPath,
                new ExtractSettings
                {
                    // 转换模式只从这里读两格:覆盖已有产物,以及并发上限(没设 = 按核数)
                    CoverAllFiles = true,
                },
                onProgress, _cts.Token, mobileOptions);

            if (!_cts.IsCancellationRequested)
            {
                _progressValue = 100;
                _progressStateText = "转换完成";
                _progressDetailText = $"已完成 {_done}/{_total} 个壁纸 → {outputPath}";
                TaskbarProgressService.SetProgress(100);
                Log.Information("[转为移动版] 转换完成: {Count} 个壁纸 / {Groups} 档 → {Output}",
                    queue.Count, tiers.Count, outputPath);
                NotifyRequested?.Invoke("转换完成", $".mpkg 已输出到 {outputPath}");

                // 转成功的移出队列,失败的留着:改了档位或换了包可以直接对剩下的再点一次「开始转换」。
                // 按这一批自己的行删(而不是扫整个队列),否则转换期间新入队的行会被连带清掉。
                int kept = 0, removed = 0;
                foreach (var row in queue)
                {
                    if (failedNames.Contains(row.Name)) { kept++; continue; }
                    if (Items.Remove(row))
                    {
                        _keys.Remove(row.Key);
                        _probeSeen.Remove(row.Key);
                        removed++;
                    }
                }
                UpdateCountText();
                Log.Information("[mpkg队列] 整批跑完: 转出 {Removed} 项, 失败保留 {Kept} 项, 队列剩 {Total} 项",
                    removed, kept, Items.Count);
                QueueChanged?.Invoke();
            }
            else
            {
                _progressStateText = "转换已停止";
                TaskbarProgressService.Clear();
                Log.Information("[转为移动版] 用户停止, 队列保留 {Total} 项可重开", Items.Count);
            }
        }
        catch (OperationCanceledException)
        {
            _progressStateText = "转换已停止";
            TaskbarProgressService.Clear();
            Log.Information("[转为移动版] 用户停止(异常路径), 队列保留 {Total} 项可重开", Items.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[转为移动版] 转换失败");
            _progressStateText = "转换失败，请查看日志";
            _progressValue = 0;
            TaskbarProgressService.SetError();
            _navBadgeError = true;
            Log.Information("[mpkg队列] 异常终止, 队列保留 {Total} 项可重开", Items.Count);
            NotifyRequested?.Invoke("转换失败", "转换失败，请查看日志");
        }

        IsBusy = false;
        _paused = false;
        // 行上那根条是这一批的过程量,批一结束就清:失败留下的行若顶着半截条,会被读成"还转到一半"
        // 失败那几行的条不归零:收尾一归零,Error 态就跟着Visibility一起塌了,
        // 而队列里留下的正是这几行 —— 用户得能看出哪几张是失败留下来的
        foreach (var row in queue)
        {
            if (!row.IsError) row.EntryProgress = 0;
        }
        RaiseProgress();
        // 徽标:转完(完成/停止)隐藏;失败 → 红色保留剩余数(与提取那套一致)
        if (_navBadgeError)
            NavBadgeService.SetBadge("Papers", Math.Max(1, _total - _done), NavBadgeState.Error);
        else
            NavBadgeService.SetBadge("Papers", null);
    }

    /// <summary>把 repkg 按名字回来的条目百分比写进行上那根条。
    /// 行名与 <c>RepkgCliService.NameOf</c> 是同一条表达式(见 <see cref="MpkgQueueItem.Name"/>),两边对不上就会静默没有分进度。
    /// 标题一样的两张会一起动 —— 它们本来也在同一批里,一起动总好过都不动。</summary>
    private void SetRowProgress(string name, double pct)
    {
        foreach (var row in Items)
        {
            if (row.Name == name) row.EntryProgress = pct;
        }
    }

    /// <summary>把这一张标成失败:行上那根条进 Error 态,并且躲过收尾的归零。
    /// 与 <see cref="SetRowProgress"/> 同一条按名匹配的口径(同名两张一起动)。</summary>
    private void MarkRowError(string name)
    {
        foreach (var row in Items)
        {
            if (row.Name == name) row.IsError = true;
        }
    }

    private void RaiseProgress()
    {
        OnPropertyChanged(nameof(ProgressVisibility));
        OnPropertyChanged(nameof(ProgressStateText));
        OnPropertyChanged(nameof(ProgressDetailText));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(StopButtonVisibility));
        OnPropertyChanged(nameof(PauseButtonVisibility));
        OnPropertyChanged(nameof(ResumeButtonVisibility));
    }

    private void MpkgQueuePause_Click(object sender, RoutedEventArgs e)
    {
        _service?.Pause();
        _paused = true;
        _progressStateText = "已暂停";
        TaskbarProgressService.SetPaused();
        NavBadgeService.SetBadge("Papers", _total - _done, NavBadgeState.Paused);
        RaiseProgress();
    }

    private void MpkgQueueResume_Click(object sender, RoutedEventArgs e)
    {
        // 不动 _cts:令牌已经交给了在跑的那次 await,换新一个只会让「停止」按不动正在转的那一批。
        _service?.Resume();
        _paused = false;
        _progressStateText = "正在转换...";
        TaskbarProgressService.SetProgress(_progressValue);
        NavBadgeService.SetBadge("Papers", _total - _done, NavBadgeState.Running);
        RaiseProgress();
    }

    private void MpkgQueueStop_Click(object sender, RoutedEventArgs e)
    {
        Log.Information("[mpkg队列] 用户按停止, 队列 {Total} 项", Items.Count);
        StopConversion();
        RaiseProgress();
    }

    private void StopConversion()
    {
        _cts?.Cancel();
        _service?.Stop();
        _paused = false;
        _progressStateText = "正在停止...";
    }

    /// <summary>承载这块面板的东西要没了(副窗口关闭/进程退出):掐掉探测与在跑的转换,别留后台进程。</summary>
    public void Shutdown()
    {
        _probeDebounce?.Stop();
        _probeCts?.Cancel();
        if (IsBusy) StopConversion();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

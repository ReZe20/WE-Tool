using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WE_Tool.Models;

/// <summary>
/// 「转为移动版」队列里的一行:一张待转 mpkg 的壁纸 + 它自己的打包参数。
/// 档位人人都有(那根 slider),其余几格开关只在「自定义模式」开着时可改,而且一律可空 ——
/// 没碰过的键不进清单,由 repkg 逐键回落全局,和它自己的 <c>wallpapers[].options</c> 同形。
/// 档位序号与 <see cref="MpkgPackingDefaults.Tier"/> 同一套(0=原始 1=2× 2=4×)。
/// </summary>
public partial class MpkgQueueItem : INotifyPropertyChanged
{
    private int _tier;
    private bool? _keepAudio;
    private bool? _useLz4;
    private bool? _shaderCompat;
    private bool? _encodeEtc2;
    private bool? _copyTextures;
    private bool? _shrinkDx;
    private int? _nameMode;
    private Visibility _settingsVisibility = Visibility.Collapsed;
    private string? _probeNote;
    private string? _probeShort;
    private bool _probeIsWarning;

    public MpkgQueueItem(WallpaperItem wallpaper)
    {
        Wallpaper = wallpaper;
        // 与 RepkgCliService.NameOf 同一条表达式:转换期的进度事件按这个名字回调,两边不一致就对不上行
        Name = wallpaper.Title ?? wallpaper.WorkshopID
            ?? (wallpaper.FolderPath != null ? new DirectoryInfo(wallpaper.FolderPath).Name : "?");
        Key = wallpaper.FolderPath ?? wallpaper.WorkshopID ?? Name;
        Preview = wallpaper.Preview;
    }

    public WallpaperItem Wallpaper { get; }

    /// <summary>行内显示名,同时是转换期间的进度回调键。</summary>
    public string Name { get; }

    /// <summary>入队去重用的稳定标识(同一路径/同一创意工坊 ID 不重复入队)。</summary>
    public string Key { get; }

    public string? Preview { get; }

    /// <summary>
    /// 行内 slider 的绑定面:Slider.Value 是 double,档位序号是 int,这里做那一次转换。
    /// <b>给出去的是生效档位</b>(照搬开着就是 0=原始),写回来的才是这一行自己的意图,
    /// 而照搬开着时这一格点不动,所以那期间的回写一律丢掉 —— 关掉照搬后 slider 会弹回他原先选的那一档。
    /// </summary>
    public double TierValue
    {
        get => EffectiveTier;
        set
        {
            // 照搬开着时这根 slider 是置灰的,所以走到这里的只可能是"生效值变 0"那次绑定回写 ——
            // 收下它就把他存的档位抹掉了。总控那一路写的是 Tier,不受这里影响。
            if (!PixelKnobsEnabled) return;
            Tier = (int)Math.Round(value);
        }
    }

    /// <summary>这一行真正会发出去的档位 —— 照搬开着时它是 0,而 <see cref="Tier"/> 只是他存下的意图。
    /// 清单快照、档位分布读数、探测缓存都读这个,不然界面上会同时存在两套"当前档位"。</summary>
    public int EffectiveTier => CopyTexturesOn ? 0 : Tier;

    /// <summary>
    /// 纹理照搬是像素那几格的<b>父</b>(与 repkg 的 mpkgNoDematerialize 派生同一份口径):
    /// 它开着时档位/ETC2/缩DXT 都没有作用对象,所以生效值一律按"不做事"显示,控件也一并置灰。
    /// 只有 LZ4 例外 —— repkg 故意不改它的值(它不改产物字节也不进读数),所以这里也只置灰、不改显示状态。
    /// </summary>
    public bool PixelKnobsEnabled => !CopyTexturesOn;

    /// <summary>当前档的文字。故意用 1×/2×/4× 这种不带语言的写法:
    /// 表头那三个档名要走 LanguageHelper,缺键的 9 种语言会把键名直接印到界面上。</summary>
    public string TierText => TierLabel(EffectiveTier);

    /// <summary>档位序号 → 显示文字。静态是因为表头那根总控报读数时手上没有具体行。</summary>
    public static string TierLabel(int tier) => tier switch { 1 => "2×", 2 => "4×", _ => "1×" };

    public int Tier
    {
        get => _tier;
        set
        {
            if (_tier == value) return;
            // 档位一变,刚探出来的结论就过期了:预警与短词一起清,等下一轮探测重新给。
            // 留着旧句子会比没有句子更坏——它会指着上一个档位说"这个档位缩不动"。
            var hadNote = _probeNote is not null;
            _probeNote = null;
            _probeShort = null;
            var wasReduced = _tier > 0;
            _tier = value;
            var isReduced = value > 0;
            // 「÷1 还开 ETC2」在 repkg 是硬报错(整批退出 1),所以降回原始档时把这条覆盖吃掉。
            // 总控 slider 也走这个 setter,两条改档路径共用这一道护栏。
            var cleared = false;
            if (!isReduced && _encodeEtc2 == true)
            {
                _encodeEtc2 = null;
                TierClearedEtc2Override = true;
                cleared = true;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(TierValue));
            OnPropertyChanged(nameof(TierText));
            if (hadNote)
            {
                OnPropertyChanged(nameof(ProbeNote));
                OnPropertyChanged(nameof(ProbeShort));
                OnPropertyChanged(nameof(ProbeNoteVisibility));
            }
            // ETC2 那三格只在"跨过原始档这条线"或覆盖真被吃掉时才变;否则一次拖档会多刷三条读数
            if (wasReduced != isReduced || cleared)
            {
                OnPropertyChanged(nameof(Etc2Enabled));
                OnPropertyChanged(nameof(Etc2On));
                OnPropertyChanged(nameof(Etc2ReasonVisibility));
            }
            if (cleared) RaiseOverrideFlags();
        }
    }

    /// <summary>最近一次改档有没有因为"原始档不许 ETC2"吃掉一条覆盖 —— 页面报读数用,取一次清一次。</summary>
    public bool TierClearedEtc2Override { get; private set; }

    public bool TakeTierClearedEtc2()
    {
        var taken = TierClearedEtc2Override;
        TierClearedEtc2Override = false;
        return taken;
    }

    /// <summary>逐行设置按钮的可见性:整页随「自定义模式」一起推,关着的时候行内只有档位 slider。</summary>
    public Visibility SettingsVisibility
    {
        get => _settingsVisibility;
        set
        {
            if (_settingsVisibility == value) return;
            _settingsVisibility = value;
            OnPropertyChanged();
            if (value == Visibility.Collapsed) IsExpanded = false;
        }
    }

    private bool _isExpanded;

    /// <summary>这一行的参数条开没开。挂在行上而不是控件上:虚拟化回收控件不该把他点开的行悄悄合上。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExpandedVisibility));
            OnPropertyChanged(nameof(ChevronGlyph));
        }
    }

    public Visibility ExpandedVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>箭头朝向:收起时朝下(点它展开),展开时朝上。字形是 Segoe MDL2 的 chevron 一对。</summary>
    public string ChevronGlyph => IsExpanded ? "" : "";

    /// <summary>保留音频(repkg 的 keepAudio)。显示的是生效值:没覆盖过就显示全局那个。</summary>
    public bool KeepAudioOn
    {
        get => _keepAudio ?? MpkgPackingDefaults.KeepAudio;
        set => SetOverride(ref _keepAudio, value, MpkgPackingDefaults.KeepAudio, nameof(KeepAudioOn));
    }

    /// <summary>纹理 LZ4 压缩(repkg 的 noLz4,这里是正向说法)。</summary>
    public bool UseLz4On
    {
        get => _useLz4 ?? MpkgPackingDefaults.UseLz4;
        set => SetOverride(ref _useLz4, value, MpkgPackingDefaults.UseLz4, nameof(UseLz4On));
    }

    /// <summary>着色器兼容改写(repkg 的 mpkgNoShaderCompat,正向说法)。</summary>
    public bool ShaderCompatOn
    {
        get => _shaderCompat ?? MpkgPackingDefaults.ShaderCompat;
        set => SetOverride(ref _shaderCompat, value, MpkgPackingDefaults.ShaderCompat, nameof(ShaderCompatOn));
    }

    /// <summary>ETC2 编码。默认由档位派生,所以原始档上它是关的、而且不给开(repkg 会拒)。
    /// 照搬开着时它更没有对象 —— 生效值一律 false,setter 也被 <see cref="Etc2Enabled"/> 挡住。</summary>
    public bool Etc2On
    {
        get => PixelKnobsEnabled && (_encodeEtc2 ?? Tier > 0);
        set
        {
            if (!Etc2Enabled) return;
            SetOverride(ref _encodeEtc2, value, true, nameof(Etc2On));
        }
    }

    public bool Etc2Enabled => Tier > 0 && PixelKnobsEnabled;

    /// <summary>
    /// 那句"要先调到 2×/4×"只在<b>原始档</b>这一种理由下摆出来:照搬开着时档位调到 4× 也不会解锁 ETC2,
    /// 把那句话留在界面上就是一句会把人引向错方向的假话 —— 照搬这件事由上面那格自己说明。
    /// </summary>
    public Visibility Etc2ReasonVisibility => Etc2Enabled || CopyTexturesOn ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// 纹理照搬(repkg 的 mpkgNoDematerialize,正向说法)。开着 = .tex 一字节不动,
    /// 产物就是"只换容器"的对照包。它是像素那几格的父:开着时档位与 ETC2/缩DXT 一起变成无事可做,
    /// 所以这里改值之后要把那几个生效属性一并重报(置灰与数值都靠这次通知刷新)。
    /// </summary>
    public bool CopyTexturesOn
    {
        get => _copyTextures ?? MpkgPackingDefaults.CopyTextures;
        set
        {
            bool was = CopyTexturesOn;
            SetOverride(ref _copyTextures, value, MpkgPackingDefaults.CopyTextures, nameof(CopyTexturesOn));
            if (was == CopyTexturesOn) return;
            RaisePixelKnobEffects();
        }
    }

    /// <summary>DXT 块格式也解码重缩(repkg 的 mpkgShrinkDx)。照搬开着时它无事可做,生效值恒 false。</summary>
    public bool ShrinkDxOn
    {
        get => PixelKnobsEnabled && (_shrinkDx ?? MpkgPackingDefaults.ShrinkDx);
        set
        {
            if (!PixelKnobsEnabled) return;
            SetOverride(ref _shrinkDx, value, MpkgPackingDefaults.ShrinkDx, nameof(ShrinkDxOn));
        }
    }

    /// <summary>父开关刚落定/抬起之后,把所有"受它影响的生效值"重报一遍(slider 位置、档名、几格开关与置灰)。</summary>
    private void RaisePixelKnobEffects()
    {
        OnPropertyChanged(nameof(TierValue));
        OnPropertyChanged(nameof(TierText));
        OnPropertyChanged(nameof(Etc2On));
        OnPropertyChanged(nameof(Etc2Enabled));
        OnPropertyChanged(nameof(Etc2ReasonVisibility));
        OnPropertyChanged(nameof(ShrinkDxOn));
        OnPropertyChanged(nameof(PixelKnobsEnabled));
    }


    /// <summary>这张壁纸有没有创意工坊 ID:导入/自制的没有,那"按 ID 重命名"就没有对象可选。</summary>
    public bool NameIdEnabled => !string.IsNullOrEmpty(Wallpaper.WorkshopID);

    public Visibility NameIdReasonVisibility => NameIdEnabled ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// 行上那组二选一绑的面:<c>RadioButtons.SelectedIndex</c> 就是重命名模式(0=壁纸标题 1=创意工坊 ID)。
    /// 给出去的是生效值 —— 没覆盖过就显示总控那个默认,和音频/LZ4 那几格同一套。
    /// </summary>
    public int NameModeIndex
    {
        get => _nameMode ?? MpkgPackingDefaults.NameMode;
        set
        {
            int? next = value == MpkgPackingDefaults.NameMode ? null : value;
            if (_nameMode == next) return;
            _nameMode = next;
            OnPropertyChanged(nameof(NameModeIndex));
            RaiseOverrideFlags();
        }
    }

    /// <summary>
    /// 总控改了重命名模式:这一行回到"跟随总控"。
    /// 套上去而不是逐行写死,是为了让角标继续只标"这一行和别人不一样"的行 ——
    /// 一批幽灵覆盖会把扫队列用的那个角标彻底弄废。
    /// </summary>
    public bool FollowMasterNameMode()
    {
        if (_nameMode is null) return false;
        _nameMode = null;
        OnPropertyChanged(nameof(NameModeIndex));
        RaiseOverrideFlags();
        return true;
    }

    /// <summary>这一行最终会被叫成什么(只给日志读数用,界面上文件名要等真开转才知道)。</summary>
    public string NameModeReadout => _nameMode is null ? "跟随总控" : _nameMode == 1 ? "ID" : "标题";

    /// <summary>
    /// 只读探测给的长句:摆在名称下面那一排右边的 i 图标 ToolTip 里。空 = 还没探或没什么要说。
    /// 它和短词一起挂在主行而不是参数条里 —— 预警要在他"还没展开、还没点开始"的时候就看得见。
    /// </summary>
    public string? ProbeNote
    {
        get => _probeNote;
        set
        {
            if (_probeNote == value) return;
            _probeNote = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProbeNoteVisibility));
        }
    }

    /// <summary>名称下面那小段短词（"缩不动"这种）。长句在 ToolTip 里，界面上不铺开。</summary>
    public string? ProbeShort
    {
        get => _probeShort;
        set
        {
            if (_probeShort == value) return;
            _probeShort = value;
            OnPropertyChanged();
        }
    }

    public Visibility ProbeNoteVisibility => string.IsNullOrEmpty(_probeNote) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>探测结论是"这个档位白选"还是只是一句构成说明 —— 前者才配那格琥珀色。</summary>
    public bool ProbeIsWarning
    {
        get => _probeIsWarning;
        set
        {
            if (_probeIsWarning == value) return;
            _probeIsWarning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProbeNoteBrush));
        }
    }

    private static SolidColorBrush? _warnBrush;
    private static Brush? _infoBrush;

    /// <summary>
    /// 预警那行的颜色。"这一档白选"用与角标同一支琥珀(它是唯一需要人动手的结论),
    /// 其余构成说明走主题的次要文字色 —— 一个颜色把两件事分开,不然真那句会被稀释。
    /// </summary>
    public Brush ProbeNoteBrush
    {
        get
        {
            if (ProbeIsWarning)
                return _warnBrush ??= new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE5, 0xA5, 0x0A));
            // 次要色从主题取:写死一支灰在亮色主题下几乎看不见
            return _infoBrush ??= Application.Current?.Resources["TextFillColorSecondaryBrush"] as Brush
                ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x9E, 0x9E, 0x9E));
        }
    }

    /// <summary>除了必发的档位之外,这一行还有没有逐行覆盖 —— 决定按钮上那个"已改"角标。</summary>
    public bool HasOverride =>
        _keepAudio is not null || _useLz4 is not null || _shaderCompat is not null || _encodeEtc2 is not null
        || _copyTextures is not null || _shrinkDx is not null || _nameMode is not null;

    public Visibility OverrideBadgeVisibility => HasOverride ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>「重置为全局」只在真有覆盖时露出来,免得摆一个按了没反应的按钮。</summary>
    public Visibility ResetVisibility => HasOverride ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 逐行开关的当前生效值 + 覆盖了几项。只给日志用:界面上开关本身就看得见状态,
    /// 走日志才需要一句能对着读的中文,也就没必要进 resw(那会牵扯 11 个语言)。
    /// </summary>
    public string FlagsReadout =>
        $"音频={(KeepAudioOn ? "开" : "关")} LZ4={(UseLz4On ? "开" : "关")}" +
        $" 兼容改写={(ShaderCompatOn ? "开" : "关")} ETC2={(Etc2On ? "开" : "关")}" +
        $" 照搬={(CopyTexturesOn ? "开" : "关")} 缩DXT={(ShrinkDxOn ? "开" : "关")} 重命名={NameModeReadout}" +
        (HasOverride ? "" : " (全跟随全局)");

    /// <summary>面板总控那一格(包名主干用标题还是 ID)变了之后由页面调一次,让已入队的行重读生效值。</summary>
    public void RefreshGlobalDefaults() => OnPropertyChanged(nameof(NameModeIndex));

    public void ResetOverrides()
    {
        if (!HasOverride) return;
        _keepAudio = _useLz4 = _shaderCompat = _encodeEtc2 = null;
        _copyTextures = _shrinkDx = null;
        _nameMode = null;
        RaiseAll();
    }

    /// <summary>
    /// 交给 RepkgCliService 写成 wallpapers[].options 的快照。写的是<b>生效值</b>而不是存着的意图:
    /// 照搬开着时档位发 0、ETC2/缩DXT 的覆盖不进清单 —— 否则清单里就留下一格"写着而 repkg 会吞掉"的键,
    /// 正是这次要消掉的那种形状。存的原意图仍在行模型里,关掉照搬就回来。
    /// </summary>
    public MpkgEntryOptions Snapshot() => new()
    {
        Tier = EffectiveTier,
        KeepAudio = _keepAudio,
        UseLz4 = _useLz4,
        ShaderCompat = _shaderCompat,
        EncodeEtc2 = PixelKnobsEnabled ? _encodeEtc2 : null,
        CopyTextures = _copyTextures,
        ShrinkDx = PixelKnobsEnabled ? _shrinkDx : null,
        NameMode = _nameMode,
    };

    /// <summary>
    /// 这一行探测要看的那套档位口径。只列真正影响"会不会动手"的四项 ——
    /// 音频/LZ4/着色器改写照不照做都不改变缩几条纹理,把它们编进签名只会让缓存频繁失效。
    /// </summary>
    public string ProbeSignature =>
        $"{EffectiveTier}|{Etc2On}|{ShrinkDxOn}|{CopyTexturesOn}";

    private void SetOverride(ref bool? field, bool value, bool globalDefault, string propertyName)
    {
        // 改成和全局一样的值 = 取消覆盖,清单里就不写这个键了:产物字节不变,但少一条"看着改了其实没改"的噪音
        bool? next = value == globalDefault ? null : value;
        if (field == next) return;
        field = next;
        OnPropertyChanged(propertyName);
        RaiseOverrideFlags();
    }

    private void RaiseOverrideFlags()
    {
        OnPropertyChanged(nameof(HasOverride));
        OnPropertyChanged(nameof(OverrideBadgeVisibility));
        OnPropertyChanged(nameof(ResetVisibility));
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(KeepAudioOn));
        OnPropertyChanged(nameof(UseLz4On));
        OnPropertyChanged(nameof(ShaderCompatOn));
        OnPropertyChanged(nameof(Etc2On));
        OnPropertyChanged(nameof(CopyTexturesOn));
        OnPropertyChanged(nameof(ShrinkDxOn));
        OnPropertyChanged(nameof(NameModeIndex));
        RaiseOverrideFlags();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

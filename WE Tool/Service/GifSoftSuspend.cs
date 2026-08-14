using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;

namespace WE_Tool.Service;

/// <summary>软挂起:闲置超时(窗口内无任何输入)后停播全部 GIF 并清缓存 —— 内存回落到基线、CPU 归零;
/// 任意输入立即唤醒(页面对账重启播放)。比系统挂起更省:挂起只冻结(内存不还),软挂起真释放。
/// 输入监听用页面根元素的冒泡事件(任一子元素命中都会冒泡);检查用 DispatcherQueueTimer(30s 一次)。</summary>
public static class GifSoftSuspend
{
    /// <summary>闲置超时:连续无输入 3 分钟触发软挂起(可调)</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);

    /// <summary>检查间隔:30s 一次,开销可忽略</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    private sealed class Registration
    {
        public required UIElement Root;
        public required Action OnSuspend;
        public required Action OnResume;
    }

    private static readonly List<Registration> _regs = [];
    private static DispatcherQueueTimer? _timer;
    private static DateTime _lastActivity = DateTime.UtcNow;
    private static bool _suspended;

    /// <summary>页面注册(Loaded 调用):root 传页面本身(输入事件冒泡到 Page);挂起=停播+清缓存,恢复=对账重启。</summary>
    public static void Register(UIElement root, Action onSuspend, Action onResume)
    {
        var reg = new Registration { Root = root, OnSuspend = onSuspend, OnResume = onResume };
        root.PointerMoved += OnActivity;      // 高频:仅更新时间戳,开销可忽略
        root.PointerPressed += OnActivity;
        root.PointerWheelChanged += OnActivity;
        root.KeyDown += OnActivity;
        _regs.Add(reg);
        _timer ??= StartTimer();
    }

    /// <summary>页面注销(Unloaded 调用):页面切走/销毁后不再参与检测</summary>
    public static void Unregister(UIElement root)
    {
        root.PointerMoved -= OnActivity;
        root.PointerPressed -= OnActivity;
        root.PointerWheelChanged -= OnActivity;
        root.KeyDown -= OnActivity;
        _regs.RemoveAll(r => r.Root == root);
        if (_regs.Count == 0)
        {
            _timer?.Stop();
            _timer = null;
            _suspended = false;
        }
    }

    private static DispatcherQueueTimer StartTimer()
    {
        var t = DispatcherQueue.GetForCurrentThread().CreateTimer();
        t.Interval = CheckInterval;
        t.Tick += OnTick;
        t.Start();
        return t;
    }

    private static void OnActivity(object sender, object e)
    {
        _lastActivity = DateTime.UtcNow;
        if (_suspended)
        {
            _suspended = false;
            foreach (var reg in _regs) reg.OnResume(); // 任意输入唤醒:页面对账重启播放
        }
    }

    private static void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (_suspended) return;
        if (DateTime.UtcNow - _lastActivity > IdleTimeout)
        {
            _suspended = true;
            foreach (var reg in _regs) reg.OnSuspend(); // 停播全部 + 清缓存 → 内存回落
        }
    }
}

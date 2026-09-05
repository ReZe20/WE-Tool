using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace WE_Tool.Controls;

/// <summary>
/// [Ctrl+滚轮 2026-09] ScrollView 子类:官方把 Ctrl+滚轮当"缩放"消费(ZoomMode=Disabled 也吞事件)。
/// 重写滚轮处理:Ctrl+左键按住时绕开缩放分支,把滚轮转为官方垂直滚动(ScrollBy 即时步进);
/// 其余(含 Ctrl 无左键)不拦截——Ctrl 切档位由上层 ItemsRepeater 拦截处理。
/// [AOT 2026-09-05] 必须 partial:自定义 WinRT 派生类要 partial 才能让 C#/WinRT 生成
/// 类型投影信息,否则 NativeAOT 下 XAML 加载即崩(0xc000027b / combase E_NOINTERFACE)。
/// </summary>
public partial class CtrlAwareScrollView : ScrollView
{
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0) { base.OnPointerWheelChanged(e); return; }

        // KeyModifiers 在 PointerRoutedEventArgs 上(PointerPoint 没有)
        bool ctrlHeld = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0;
        bool leftPressed = point.Properties.IsLeftButtonPressed;

        if (ctrlHeld && leftPressed)
        {
            // Ctrl+左键+滚轮:官方当缩放吞掉 → 这里转成官方垂直滚动
            // 逐格即时到位(无补间尾巴):每格 ~48px 步进
            double deltaPx = delta / 120.0 * 48.0;
            _ = ScrollBy(0, -deltaPx, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
            e.Handled = true;
            return;
        }

        // 其余情况交官方(普通滚轮官方滚动;Ctrl 无左键的缩放由 base 吞/上层切档位已 Handled)
        base.OnPointerWheelChanged(e);
    }
}

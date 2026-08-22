using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace WE_Tool.Controls;

/// <summary>日志面板拖拽手柄:带上下缩放光标(UIElement.ProtectedCursor 是 protected,只能从子类设置)</summary>
public sealed partial class LogResizeHandle : Grid
{
    public LogResizeHandle()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WE_Tool;

/// <summary>
/// 残留清理页:扫描并清理 Steam 取消订阅/下架后删除不彻底的遗留壁纸文件。
/// </summary>
public sealed partial class Cleanup : Page
{
    public Cleanup()
    {
        InitializeComponent();
    }
}
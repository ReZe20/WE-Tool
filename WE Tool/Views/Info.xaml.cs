using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WE_Tool;

/// <summary>
/// 关于页:软件信息卡片。
/// </summary>
public sealed partial class Info : Page
{
    /// <summary>
    /// 软件版本号(来自程序集版本,csproj &lt;Version&gt; 驱动)
    /// </summary>
    public string VersionText { get; } = GetVersionText();

    public Info()
    {
        InitializeComponent();
    }

    private static string GetVersionText()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return version == null ? string.Empty : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}

using System;
using WinRT;

namespace WE_Tool;

/// <summary>
/// AOT 入口点:自定义 Main 替代 XAML 生成的 Main
/// </summary>
public class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();
        XamlGeneratedProgram.XamlGeneratedMain();
    }
}

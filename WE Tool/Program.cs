using System;
using System.Threading;

namespace WE_Tool;

/// <summary>
/// 进程入口。XAML 生成的入口点已被 DISABLE_XAML_GENERATED_MAIN 关掉(见 csproj),
/// 分流必须放在这里、而不是 App 构造函数里:Microsoft.UI.Xaml.Application.Start 会把整套
/// XAML 运行时拉起来,而桥接子模式是全程常驻的空闲进程,一行 XAML 都不碰。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (Service.SteamBridgeChild.IsBridgeMode(args))
            Environment.Exit(Service.SteamBridgeChild.Run(args));

        // 提取后端子模式:CLI 代码编译在本 exe 里,RepkgCliService 拉起的就是自己
        if (Service.RepkgChild.IsChildMode(args))
            Environment.Exit(Service.RepkgChild.Run(args));

        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        global::Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            // 等价于 new App():partial 实现由 XAML 代码生成提供(App.g.cs)
            App.XamlGeneratedCreateApplicationInstance();
        });
    }
}

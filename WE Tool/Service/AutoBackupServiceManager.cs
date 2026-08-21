using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Serilog;

namespace WE_Tool.Service;

/// <summary>
/// AutoBackupService.exe 后台服务管理:注册表 Run 键(HKCU)实现开机登录自启,免管理员。
/// 说明:schtasks /SC ONLOGON 在标准用户下创建被拒绝(需管理员),HKCU Run 键无需管理员且语义等价。
/// 注册表值名 WE_Tool_AutoBackup,可执行文件随 WE Tool 发布(主程序 bin 同级 AutoBackupService 目录)。
/// </summary>
public class AutoBackupServiceManager
{
    public const string TaskName = "WE_Tool_AutoBackup";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ServiceSubDir = "AutoBackupService";
    private const string ServiceExeName = "AutoBackupService.exe";

    /// <summary>服务可执行文件路径(随 WE Tool 发布在 bin 下 AutoBackupService 子目录)。</summary>
    public static string ServiceExePath
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            return Path.Combine(baseDir, ServiceSubDir, ServiceExeName);
        }
    }

    /// <summary>服务是否已安装(Run 键存在)。</summary>
    public bool IsInstalled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key == null) return false;
            return key.GetValue(TaskName) != null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "查询自启项失败: {Task}", TaskName);
            return false;
        }
    }

    /// <summary>Run 键当前指向的命令(未安装返回 null)。</summary>
    public string? GetTaskCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(TaskName) as string;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取自启命令失败: {Task}", TaskName);
            return null;
        }
    }

    /// <summary>进程是否正在运行(用于"启动/停止"状态显示:进程存在即运行中)。</summary>
    public bool IsRunning()
    {
        try
        {
            var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ServiceExeName));
            return procs.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>安装:写入 HKCU Run 键(开机登录自启,免管理员)。</summary>
    public string? Install()
    {
        if (!File.Exists(ServiceExePath))
            return $"未找到服务程序: {ServiceExePath}";
        try
        {
            // Run 键值:引号包裹路径 + --run 参数
            string cmd = $"\"{ServiceExePath}\" --run";
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key?.SetValue(TaskName, cmd, RegistryValueKind.String);
            // 安装后立即启动,让服务马上生效(不必等下次登录)
            Start();
            return null;
        }
        catch (Exception ex)
        {
            return $"安装异常: {ex.Message}";
        }
    }

    /// <summary>卸载:删除 Run 键,并结束运行中的进程。</summary>
    public string? Uninstall()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            key?.DeleteValue(TaskName, false);
            StopProcess();
            return null;
        }
        catch (Exception ex)
        {
            return $"卸载异常: {ex.Message}";
        }
    }

    /// <summary>立即启动服务进程(--run,独立于自启)。</summary>
    public string? Start()
    {
        if (!File.Exists(ServiceExePath))
            return $"未找到服务程序: {ServiceExePath}";
        if (IsRunning()) return null;
        try
        {
            Process.Start(new ProcessStartInfo(ServiceExePath, "--run")
            {
                UseShellExecute = true,
                CreateNoWindow = true
            });
            return null;
        }
        catch (Exception ex)
        {
            return $"启动异常: {ex.Message}";
        }
    }

    /// <summary>停止运行中的服务进程。</summary>
    public void StopProcess()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ServiceExeName)))
            {
                try { p.Kill(); } catch { /* 已退出 */ }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "停止服务进程失败");
        }
    }
}

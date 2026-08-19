using System.IO;

namespace AutoBackupService;

/// <summary>
/// 轻量日志:追加写入 %LOCALAPPDATA%/WE_Tool/AutoBackupService.log,控制台同显。
/// 不用 Serilog,保持 NativeAOT 体积与零第三方依赖。
/// </summary>
public static class Log
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WE_Tool", "AutoBackupService.log");

    private static readonly object _lock = new();

    public static void Write(string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch { /* 日志写入失败不阻塞主流程 */ }
        try
        {
            if (!Console.IsOutputRedirected) Console.WriteLine(line);
        }
        catch { /* WinExe 无控制台时忽略 */ }
    }

    public static void Write(Exception ex, string context)
        => Write($"{context}: {ex.Message}");
}

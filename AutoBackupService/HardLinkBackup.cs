using System.Runtime.InteropServices;

namespace AutoBackupService;

/// <summary>
/// 硬链接备份核心:与主程序 WE_Tool.Helper.BackupService 逻辑一致(彼为 Serilog,此为轻量日志)。
/// 在创意工坊 content 目录内创建 .we_backup 备份目录,用硬链接实现
/// 「一份物理数据、多个目录入口」——Steam 删除原始目录时只移除该目录入口,
/// 备份目录里的硬链接仍指向同一物理数据,文件不丢、磁盘不增。
/// </summary>
public static class HardLinkBackup
{
    private const string BackupRootName = ".we_backup";
    internal const string MarkerFileName = ".backup_ok";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        IntPtr hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ_WRITE = 0x3;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const int ERROR_ALREADY_EXISTS = 183;

    public static string GetBackupRoot(string workshopContentPath)
        => Path.Combine(workshopContentPath, BackupRootName);

    /// <summary>
    /// 裸 kernel32 调用不吃 .NET 的长路径规范化(它只作用于 BCL 的 File/Directory),
    /// 所以超过 MAX_PATH 的路径必须以 0x00000003(ERROR_PATH_NOT_FOUND)失败——
    /// 而进程未声明 longPathAware 时,这条路在 Registry 关掉长路径的机器上也走不通。
    /// 统一在这里手工加 \\?\ 前缀:不依赖系统开关,也不依赖宿主 manifest。
    /// 前提:路径必须已规范化(绝对、无反斜杠以外的分隔符、无 . / ..),Path.Combine 的产物即满足。
    /// </summary>
    private static string Ext(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path.Substring(2);          // UNC: \\server\share → \\?\UNC\server\share
        return path.Length >= 2 && path[1] == ':'
            ? @"\\?\" + path                                  // 盘符: D:\a → \\?\D:\a
            : path;                                           // 相对路径交给上层规范化
    }

    public static string GetBackupDir(string workshopContentPath, string workshopId)
        => Path.Combine(GetBackupRoot(workshopContentPath), workshopId);

    /// <summary>该壁纸是否已完整备份。</summary>
    public static bool IsBackedUp(string workshopContentPath, string workshopId)
    {
        if (string.IsNullOrEmpty(workshopId)) return false;
        var dir = GetBackupDir(workshopContentPath, workshopId);
        return Directory.Exists(dir) && File.Exists(Path.Combine(dir, MarkerFileName));
    }

    public readonly record struct BackupResult(int Linked, int Skipped, string? Error);

    /// <summary>
    /// 备份一个壁纸文件夹:对源目录下每个文件在备份目录创建指向同一物理数据的硬链接。
    /// 幂等——目标已存在且与源为同一文件时跳过。不复制数据、不改动源目录。
    /// </summary>
    public static BackupResult BackupWallpaperFolder(
        string sourceDir,
        string workshopContentPath,
        string workshopId)
    {
        if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir))
            return new BackupResult(0, 0, "源目录不存在: " + sourceDir);
        if (string.IsNullOrEmpty(workshopId))
            return new BackupResult(0, 0, "缺少工坊 ID，无法备份");

        var backupDir = GetBackupDir(workshopContentPath, workshopId);
        try
        {
            Directory.CreateDirectory(backupDir);
        }
        catch (Exception ex)
        {
            Log.Write(ex, $"创建备份目录失败: {backupDir}");
            return new BackupResult(0, 0, ex.Message);
        }

        int linked = 0, skipped = 0;
        string? firstError = null;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            Log.Write(ex, $"枚举源文件失败: {sourceDir}");
            return new BackupResult(linked, skipped, ex.Message);
        }

        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(backupDir, rel);

            try
            {
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);

                if (File.Exists(target))
                {
                    if (IsSameFile(file, target))
                    {
                        skipped++;
                        continue;
                    }
                    skipped++;
                    Log.Write($"备份目标已存在且非同名硬链接,跳过: {target} (来自 {file})");
                    continue;
                }

                if (CreateHardLink(Ext(target), Ext(file), IntPtr.Zero))
                {
                    linked++;
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_ALREADY_EXISTS)
                    {
                        skipped++;
                    }
                    else
                    {
                        firstError ??= $"创建硬链接失败(0x{err:X8}): {file} → {target}";
                        Log.Write($"创建硬链接失败(0x{err:X8}): {file} → {target}");
                    }
                }
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
                Log.Write(ex, $"备份单文件失败: {file} → {target}");
            }
        }

        // 有文件没链上就不写完成标记:否则 IsBackedUp 会永久跳过这个壁纸,缺掉的文件再也补不回来
        if (firstError is null)
        {
            try
            {
                File.WriteAllText(Path.Combine(backupDir, MarkerFileName),
                    $"created={DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
            }
            catch (Exception ex)
            {
                Log.Write(ex, $"写入备份完成标记失败: {backupDir}");
            }
        }
        else
        {
            Log.Write($"备份 {workshopId} 不完整,不写完成标记(下次会重试): {firstError}");
        }

        return new BackupResult(linked, skipped, firstError);
    }

    private static bool IsSameFile(string pathA, string pathB)
    {
        try
        {
            if (!GetFileId(pathA, out var idA)) return false;
            if (!GetFileId(pathB, out var idB)) return false;
            return idA.VolumeSerial == idB.VolumeSerial
                && idA.FileIndexHigh == idB.FileIndexHigh
                && idA.FileIndexLow == idB.FileIndexLow;
        }
        catch
        {
            return false;
        }
    }

    private static bool GetFileId(string path, out (uint VolumeSerial, uint FileIndexHigh, uint FileIndexLow) id)
    {
        id = default;
        var handle = CreateFile(Ext(path), GENERIC_READ, FILE_SHARE_READ_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
        try
        {
            if (!GetFileInformationByHandle(handle, out var info)) return false;
            id = (info.dwVolumeSerialNumber, info.nFileIndexHigh, info.nFileIndexLow);
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}

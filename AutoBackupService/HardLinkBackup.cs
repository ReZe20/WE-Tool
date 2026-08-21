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

                if (CreateHardLink(target, file, IntPtr.Zero))
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
                        Log.Write($"创建硬链接失败(0x{err:X8}): {file} → {target}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write(ex, $"备份单文件失败: {file} → {target}");
            }
        }

        try
        {
            File.WriteAllText(Path.Combine(backupDir, MarkerFileName),
                $"created={DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        }
        catch (Exception ex)
        {
            Log.Write(ex, $"写入备份完成标记失败: {backupDir}");
        }

        return new BackupResult(linked, skipped, null);
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
        var handle = CreateFile(path, GENERIC_READ, FILE_SHARE_READ_WRITE,
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

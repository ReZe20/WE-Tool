using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Serilog;

namespace WE_Tool.Helper;

/// <summary>
/// 工坊壁纸备份服务：在创意工坊 content 目录内创建隐藏备份文件夹，
/// 用硬链接实现「一份物理文件、多个目录入口」——Steam 删除原始目录时
/// 只移除该目录入口，备份目录里的硬链接仍指向同一物理数据，文件不丢、磁盘不增。
/// 备份目录名以点开头并设为 Hidden，WallpaperScanner 的 AttributesToSkip=Hidden|System 自动跳过。
/// </summary>
public static class BackupService
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

    /// <summary>备份根目录路径（在创意工坊 content 目录内，隐藏目录）。</summary>
    public static string GetBackupRoot(string workshopContentPath)
        => Path.Combine(workshopContentPath, BackupRootName);

    /// <summary>
    /// 给裸 kernel32 调用的路径加 \\?\ 前缀。.NET 的长路径规范化只作用于 BCL 的 File/Directory,
    /// 不吃 P/Invoke——超过 MAX_PATH 的 target 会以 ERROR_PATH_NOT_FOUND 静默失败。
    /// 与服务端同一实现(现在是 C++ 那份:AutoBackupService/backup.cpp 的 Ext),两侧行为必须一致。
    /// </summary>
    private static string Ext(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path.Substring(2);      // \\server\share → \\?\UNC\server\share
        return path.Length >= 2 && path[1] == ':'
            ? @"\\?\" + path                             // D:\a → \\?\D:\a
            : path;
    }

    /// <summary>指定壁纸的备份目录路径。</summary>
    public static string GetBackupDir(string workshopContentPath, string workshopId)
        => Path.Combine(GetBackupRoot(workshopContentPath), workshopId);

    /// <summary>该壁纸是否已完整备份（备份目录存在且含完成标记）。</summary>
    public static bool IsBackedUp(string workshopContentPath, string workshopId)
    {
        if (string.IsNullOrEmpty(workshopId)) return false;
        var dir = GetBackupDir(workshopContentPath, workshopId);
        return Directory.Exists(dir) && File.Exists(Path.Combine(dir, MarkerFileName));
    }

    /// <summary>备份结果汇总。</summary>
    public readonly record struct BackupResult(int Linked, int Skipped, string? Error);

    /// <summary>
    /// 备份一个壁纸文件夹：对源目录下每个文件在备份目录创建指向同一物理数据的硬链接。
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
            Log.Error(ex, "创建备份目录失败: {Dir}", backupDir);
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
            Log.Error(ex, "枚举源文件失败: {Dir}", sourceDir);
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
                    // 已存在：若是同一物理文件（已是硬链接）则跳过，否则视为外部占位
                    if (IsSameFile(file, target))
                    {
                        skipped++;
                        continue;
                    }
                    skipped++;
                    Log.Warning("备份目标已存在且非同名硬链接，跳过: {Target} (来自 {Src})", target, file);
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
                        Log.Warning("创建硬链接失败(0x{Err:X8}): {Src} → {Target}", err, file, target);
                    }
                }
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
                Log.Warning(ex, "备份单文件失败: {Src} → {Target}", file, target);
            }
        }

        // 全部完成后落完成标记。有文件没链上就不写:否则 IsBackedUp 会永久跳过该壁纸,
        // 缺掉的文件再也补不回来(与服务端同一语义)。
        if (firstError is null)
        {
            try
            {
                File.WriteAllText(Path.Combine(backupDir, MarkerFileName),
                    $"created={DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "写入备份完成标记失败: {Dir}", backupDir);
            }
        }
        else
        {
            Log.Warning("备份 {Id} 不完整,不写完成标记(下次会重试): {Err}", workshopId, firstError);
        }

        return new BackupResult(linked, skipped, firstError);
    }

    /// <summary>两路径是否指向同一物理文件（通过卷序列号+文件索引判断，硬链接共享同一索引）。</summary>
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

    /// <summary>[立即备份 2026-09] 一次性补齐备份:遍历工坊 content 目录,对「未备份 + 命中自动备份筛选」的
    /// 壁纸各建一次硬链接备份。原逻辑内联在 MainWindow 的「启动时备份」,现抽到这里供两处共用
    /// (MainWindow 启动补齐 + 备份页「立即备份」按钮)。
    /// onProgress(已完成数, 总数) 可选,供 UI 显示进度;返回成功新增的备份数。</summary>
    public static int BackupAllMissing(string workshopContentPath, Models.AutoBackupConfig cfg, Action<int, int>? onProgress = null)
    {
        if (string.IsNullOrEmpty(workshopContentPath) || !Directory.Exists(workshopContentPath))
            return 0;

        // 先收集待备份清单(一次枚举 + 读元数据 + 筛类型/分级),算出总数以便 UI 报进度
        var targets = new List<(string Dir, string Id)>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(workshopContentPath))
            {
                var id = Path.GetFileName(dir);
                if (id == BackupRootName) continue;                // 备份根自身(隐藏目录)
                if (IsBackedUp(workshopContentPath, id)) continue; // 已备份
                var projPath = Path.Combine(dir, "project.json");
                if (!File.Exists(projPath)) continue;              // 不是壁纸目录

                Models.ProjectMetadata? meta;
                try
                {
                    meta = JsonSerializer.Deserialize(File.ReadAllBytes(projPath), Json.JsonContext.Default.ProjectMetadata);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "读取 project.json 失败,跳过: {Path}", projPath);
                    continue;
                }
                if (!MatchesAutoBackupFilter(cfg, meta)) continue; // 类型 + 分级筛选
                targets.Add((dir, id));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "枚举工坊目录失败: {Path}", workshopContentPath);
            return 0;
        }

        int total = targets.Count, done = 0, backed = 0;
        foreach (var (dir, id) in targets)
        {
            var result = BackupWallpaperFolder(dir, workshopContentPath, id);
            if (result.Error is null) backed++;
            else Log.Warning("补齐备份失败 {Id}: {Err}", id, result.Error);
            onProgress?.Invoke(++done, total);
        }
        return backed;
    }

    /// <summary>project.json 元数据是否命中自动备份筛选(类型 + 分级);未知分级默认放行(与服务端一致)。</summary>
    internal static bool MatchesAutoBackupFilter(Models.AutoBackupConfig cfg, Models.ProjectMetadata? meta)
    {
        if (meta == null) return false;
        var type = meta.Type?.ToLowerInvariant() ?? "";
        var rating = meta.Contentrating?.ToLowerInvariant() ?? "";

        bool typeOk = type switch
        {
            "scene" => cfg.TypeScene,
            "video" => cfg.TypeVideo,
            "web" => cfg.TypeWeb,
            "application" => cfg.TypeApplication,
            "preset" => cfg.TypePreset,
            _ => cfg.TypeUnknown,
        };
        if (!typeOk) return false;

        return rating switch
        {
            "everyone" => cfg.RatingG,
            "questionable" => cfg.RatingPg,
            "mature" => cfg.RatingR,
            "g" => cfg.RatingG,       // 兼容历史/第三方写入的短码
            "pg" => cfg.RatingPg,
            "r" => cfg.RatingR,
            _ => true,                // 未知分级默认放行
        };
    }

}

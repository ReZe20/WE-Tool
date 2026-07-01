using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RePKG_Re.Application.Package;
using RePKG_Re.Application.Texture;
using RePKG_Re.Core.Package;
using RePKG_Re.Core.Package.Enums;
using RePKG_Re.Core.Package.Interfaces;
using RePKG_Re.Core.Texture;
using WE_Tool.Models;

namespace WE_Tool.Service;

public class ExtractProgress
{
    public int Done { get; set; }
    public int Total { get; set; }
    public string? CurrentFile { get; set; }
    public bool IsComplete { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>当前处理方式描述: "解析PKG" / "转换TEX" / "拷贝文件"</summary>
    public string? Action { get; set; }
    /// <summary>统计：通过 repkg 解析的壁纸数</summary>
    public int PkgCount { get; set; }
    /// <summary>统计：直接拷贝的壁纸数</summary>
    public int CopyCount { get; set; }
}

public class RepkgExtractService
{
    private readonly ITexReader _texReader;
    private readonly ITexJsonInfoGenerator _texJsonInfoGenerator;
    private readonly IPackageReader _packageReader;
    private readonly TexToImageConverter _texToImageConverter;

    public RepkgExtractService()
    {
        _texReader = TexReader.Default;
        _texJsonInfoGenerator = new TexJsonInfoGenerator();
        _packageReader = new PackageReader();
        _texToImageConverter = new TexToImageConverter();
    }

    public async Task ExtractWallpapersAsync(
        IReadOnlyList<WallpaperItem> wallpapers,
        string outputRoot,
        ExtractSettings settings,
        IProgress<ExtractProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int done = 0;
        int total = wallpapers.Count;
        int pkgCount = 0;
        int copyCount = 0;

        if (total == 0)
        {
            progress?.Report(new ExtractProgress { IsComplete = true, ErrorMessage = "没有选中壁纸" });
            return;
        }

        foreach (var wallpaper in wallpapers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(wallpaper.FolderPath))
            {
                done++;
                Log.Warning("壁纸 {Title} 没有 FolderPath，跳过", wallpaper.Title ?? "?");
                continue;
            }

            var dir = new DirectoryInfo(wallpaper.FolderPath);
            if (!dir.Exists)
            {
                done++;
                Log.Warning("壁纸目录不存在: {Path}", wallpaper.FolderPath);
                continue;
            }

            var wallpaperOutput = GetOutputDirectory(outputRoot, wallpaper, settings);

            // 检测是否有 .pkg 文件
            var pkgFiles = dir.EnumerateFiles("*.pkg", SearchOption.AllDirectories).ToArray();

            if (pkgFiles.Length > 0)
            {
                // --- 有 .pkg → repkg 解析 ---
                progress?.Report(new ExtractProgress
                {
                    Done = done, Total = total,
                    CurrentFile = wallpaper.Title ?? wallpaper.WorkshopID,
                    Action = "解析PKG"
                });

                foreach (var pkgFile in pkgFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await Task.Run(() => ExtractSinglePkg(pkgFile, wallpaperOutput, settings),
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "提取 PKG 失败: {Path}", pkgFile.FullName);
                    }
                }

                pkgCount++;
            }
            else
            {
                // --- 无 .pkg → 直接拷贝文件 ---
                progress?.Report(new ExtractProgress
                {
                    Done = done, Total = total,
                    CurrentFile = wallpaper.Title ?? wallpaper.WorkshopID,
                    Action = "拷贝文件"
                });

                Directory.CreateDirectory(wallpaperOutput);
                CopyAllFiles(dir, wallpaperOutput, settings);
                copyCount++;
            }

            // 所有壁纸都拷贝 project.json 与预览图
            if (settings.OutProjectJSON && !settings.OneFolder)
                CopyProjectFiles(dir, wallpaperOutput, settings);

            done++;
        }

        progress?.Report(new ExtractProgress
        {
            Done = total,
            Total = total,
            IsComplete = true,
            PkgCount = pkgCount,
            CopyCount = copyCount
        });
    }

    /// <summary>Copy all files from source directory to output (recursively).</summary>
    private static void CopyAllFiles(DirectoryInfo sourceDir, string outputDir,
        ExtractSettings settings)
    {
        foreach (var file in sourceDir.EnumerateFiles())
        {
            // Apply extension filters
            if (ShouldSkipFile(file, settings))
                continue;

            // Preserve relative directory structure
            var relativePath = file.FullName.Substring(sourceDir.FullName.Length).TrimStart('\\', '/');
            var destPath = Path.Combine(outputDir, relativePath);

            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            if (!settings.CoverAllFiles && File.Exists(destPath))
                continue;

            try
            {
                File.Copy(file.FullName, destPath, true);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "拷贝文件失败: {File}", file.FullName);
            }
        }

        // Recurse into subdirectories
        foreach (var subDir in sourceDir.EnumerateDirectories())
        {
            var subOutput = Path.Combine(outputDir, subDir.Name);
            CopyAllFiles(subDir, subOutput, settings);
        }
    }

    private static bool ShouldSkipFile(FileInfo file, ExtractSettings settings)
    {
        // Ignore extensions
        if (settings.IgnoreExtension && !string.IsNullOrEmpty(settings.IgnoreExtensionList))
        {
            var skipExts = NormalizeExtensions(settings.IgnoreExtensionList.Split(','));
            if (skipExts.Any(s => file.Extension.Equals(s, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        // Only extensions
        if (settings.OnlyExtension && !string.IsNullOrEmpty(settings.OnlyExtensionList))
        {
            var onlyExts = NormalizeExtensions(settings.OnlyExtensionList.Split(','));
            if (!onlyExts.Any(s => file.Extension.Equals(s, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    private void ExtractSinglePkg(FileInfo pkgFile, string outputDirectory, ExtractSettings settings)
    {
        // Read package
        Package package;
        using (var reader = new BinaryReader(pkgFile.Open(FileMode.Open, FileAccess.Read, FileShare.Read)))
        {
            package = _packageReader.ReadFrom(reader);
        }

        // Filter entries
        var entries = FilterEntries(package.Entries, settings);

        // Extract each entry
        foreach (var entry in entries)
        {
            var filePathWithoutExtension = settings.OneFolder
                ? Path.Combine(outputDirectory, entry.Name)
                : Path.Combine(outputDirectory, entry.DirectoryPath, entry.Name);

            var filePath = filePathWithoutExtension + entry.Extension;

            // Ensure directory exists
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Write raw bytes (skip if mode is TEX-only)
            if (settings.TexExportMode != 2) // 2=只导出TEX图片, skip raw
            {
                if (settings.CoverAllFiles || !File.Exists(filePath))
                    File.WriteAllBytes(filePath, entry.Bytes);
            }

            // Convert TEX to image (skip if mode is Raw)
            if (settings.TexExportMode == 0 || entry.Type != EntryType.Tex)
                continue;

            var tex = LoadTex(entry.Bytes, entry.FullPath);
            if (tex == null)
                continue;

            try
            {
                var format = _texToImageConverter.GetConvertedFormat(tex);
                var imagePath = $"{filePathWithoutExtension}.{format.GetFileExtension()}";

                if (!settings.CoverAllFiles && File.Exists(imagePath))
                    continue;

                var resultImage = _texToImageConverter.ConvertToImage(tex);
                File.WriteAllBytes(imagePath, resultImage.Bytes);

                // Write tex-json info
                var jsonInfo = _texJsonInfoGenerator.GenerateInfo(tex);
                File.WriteAllText($"{filePathWithoutExtension}.tex-json", jsonInfo);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "TEX 转换失败: {Path}", entry.FullPath);
            }
        }
    }

    private ITex? LoadTex(byte[] bytes, string name)
    {
        try
        {
            using (var ms = new MemoryStream(bytes))
            using (var reader = new BinaryReader(ms, Encoding.UTF8))
            {
                return _texReader.ReadFrom(reader);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取 TEX 失败: {Name}", name);
            return null;
        }
    }

    private static IEnumerable<PackageEntry> FilterEntries(
        IEnumerable<PackageEntry> entries, ExtractSettings settings)
    {
        // Ignore extensions
        if (settings.IgnoreExtension && !string.IsNullOrEmpty(settings.IgnoreExtensionList))
        {
            var skipExts = NormalizeExtensions(settings.IgnoreExtensionList.Split(','));
            entries = entries.Where(e =>
                !skipExts.Any(s => e.FullPath.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
        }

        // Only extensions
        if (settings.OnlyExtension && !string.IsNullOrEmpty(settings.OnlyExtensionList))
        {
            var onlyExts = NormalizeExtensions(settings.OnlyExtensionList.Split(','));
            entries = entries.Where(e =>
                onlyExts.Any(s => e.FullPath.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
        }

        // Only TEX (mode 2)
        if (settings.TexExportMode == 2)
        {
            entries = entries.Where(e => e.Type == EntryType.Tex);
        }

        return entries;
    }

    private static string[] NormalizeExtensions(string[] array)
    {
        for (int i = 0; i < array.Length; i++)
        {
            if (array[i].StartsWith("."))
                continue;
            array[i] = '.' + array[i];
        }
        return array;
    }

    private string GetOutputDirectory(string outputRoot, WallpaperItem wallpaper,
        ExtractSettings settings)
    {
        if (settings.OneFolder)
            return outputRoot;

        if (settings.UseProjectName && !string.IsNullOrEmpty(wallpaper.Title))
        {
            var safeName = GetSafeFilename(wallpaper.Title);
            return Path.Combine(outputRoot, safeName);
        }

        // Use wallpaper's WorkshopID or folder name as subdirectory
        var subName = !string.IsNullOrEmpty(wallpaper.WorkshopID)
            ? wallpaper.WorkshopID
            : new DirectoryInfo(wallpaper.FolderPath!).Name;

        return Path.Combine(outputRoot, subName);
    }

    private static string GetSafeFilename(string filename)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new StringBuilder(filename);
        for (int i = 0; i < safe.Length; i++)
        {
            if (invalid.Contains(safe[i]))
                safe[i] = '_';
        }
        foreach (var c in new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' })
            safe.Replace(c, '_');
        return safe.ToString().Trim();
    }

    private static void CopyProjectFiles(DirectoryInfo directory, string outputDirectory, ExtractSettings settings)
    {
        if (directory == null || !directory.Exists)
            return;

        var projectFiles = directory.GetFiles().Where(f =>
            f.Name.Equals("project.json", StringComparison.OrdinalIgnoreCase) ||
            f.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            f.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase));

        foreach (var file in projectFiles)
        {
            var outputPath = Path.Combine(outputDirectory, file.Name);
            if (!settings.CoverAllFiles && File.Exists(outputPath))
                continue;

            try
            {
                File.Copy(file.FullName, outputPath, true);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "拷贝项目文件失败: {File}", file.FullName);
            }
        }
    }

    private static void CopyProjectFiles(FileInfo pkgFile, string outputDirectory, ExtractSettings settings)
    {
        CopyProjectFiles(pkgFile?.Directory, outputDirectory, settings);
    }
}

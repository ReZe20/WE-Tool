using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using WE_Tool.Models;
using WE_Tool.Service;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace WE_Tool
{
    /// <summary>
    /// 导入解包页：用户导入本地 .pkg/.mpkg 壁纸包,复制到导出目录后调用 repkg_re 解包。
    /// 提取完成后补写缺失的 project.json(桌面 pkg 包内无此文件),并触发后台扫描让壁纸出现在"我的壁纸"。
    /// </summary>
    public sealed partial class LoadPapers : Page, INotifyPropertyChanged
    {
        public ObservableCollection<ImportQueueItem> QueueItems { get; } = new();

        private readonly IPickerService _pickerService = new PickerService();
        private readonly ConfigService _configService = new();

        private RepkgCliService? _extractService;
        private CancellationTokenSource? _extractCts;
        private bool _isExtracting;
        private bool _isPaused;

        /// <summary>进度事件名(壁纸 Title=安全名)→ 队列项。</summary>
        private readonly Dictionary<string, ImportQueueItem> _itemsByName = new(StringComparer.Ordinal);

        /// <summary>拖放是否正在页面上方(用于遮罩淡入淡出的状态守卫)。</summary>
        private bool _dragOver;

        /// <summary>导出目录默认值是否已从设置填充(页面缓存复用时不重复填)。</summary>
        private bool _exportPathInitialized;

        private string _exportDirPath = "";
        private CancellationTokenSource? _saveDebounceCts;

        /// <summary>导出目录(x:Bind 双向绑定;变更后 500ms 防抖写入 config.json 的 Path.ImportExportPath)。</summary>
        public string ExportDirPath
        {
            get => _exportDirPath;
            set
            {
                if (_exportDirPath == value) return;
                _exportDirPath = value;
                OnPropertyChanged();
                ScheduleSave();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public LoadPapers()
        {
            InitializeComponent();

            QueueItems.CollectionChanged += (_, _) => UpdateState();
            UpdateState();
            UpdateRunningState();

            OverlayFadeOut.Completed += (_, _) =>
            {
                if (!_dragOver)
                    DragOverlay.Visibility = Visibility.Collapsed;
            };

            // 导出目录默认值 = 设置里保存过的值;未设置过则用 Papers 页面底部的导出目录(DownloadPath)
            Loaded += async (_, _) =>
            {
                if (_exportPathInitialized) return;
                _exportPathInitialized = true;
                try
                {
                    var s = await _configService.LoadAsync();
                    string saved = s.Path?.ImportExportPath ?? "";
                    string fallback = s.Path?.DownloadPath ?? "";
                    ExportDirPath = string.IsNullOrEmpty(saved) ? fallback : saved;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[导入解包] 读取默认导出目录失败");
                }
            };
        }

        /// <summary>空状态 ↔ 列表切换;控制按钮常驻显示,可用性由 UpdateRunningState 按队列/运行状态控制。</summary>
        private void UpdateState()
        {
            bool hasItems = QueueItems.Count > 0;
            EmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
            ImportQueueList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            UpdateRunningState();
        }

        /// <summary>按钮可用性/暂停恢复文案:控制按钮常驻,仅 IsEnabled 表达状态。</summary>
        private void UpdateRunningState()
        {
            bool hasItems = QueueItems.Count > 0;
            SelectFileButton.IsEnabled = !_isExtracting;
            ScanFolderButton.IsEnabled = !_isExtracting;
            ExportDirButton.IsEnabled = !_isExtracting;
            ClearButton.IsEnabled = !_isExtracting && hasItems;
            StartExtractButton.IsEnabled = !_isExtracting && hasItems;
            PauseButton.IsEnabled = _isExtracting;
            StopButton.IsEnabled = _isExtracting;

            PauseButton.Label = _isPaused ? "恢复" : "暂停";
            PauseButton.Icon = new FontIcon { Glyph = _isPaused ? "\uE768" : "\uE769" };
        }

        private async void SelectFiles_Click(object sender, RoutedEventArgs e)
        {
            var files = await _pickerService.PickPkgFilesAsync();
            if (files == null) return;

            AddFiles(files);
        }

        private void Page_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "导入壁纸包";

                if (!_dragOver)
                {
                    _dragOver = true;
                    ShowOverlay();
                }
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
        }

        private void Page_DragLeave(object sender, DragEventArgs e)
        {
            HideOverlay();
        }

        private async void Page_Drop(object sender, DragEventArgs e)
        {
            HideOverlay();

            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

            var items = await e.DataView.GetStorageItemsAsync();

            var files = items.OfType<StorageFile>()
                .Where(f => IsPkgFile(f.Name))
                .Select(f => f.Path)
                .ToList();

            AddFiles(files);

            int ignored = items.Count - files.Count;
            if (ignored > 0)
                ShowInfoBar($"已忽略 {ignored} 个不支持的项目", InfoBarSeverity.Warning);
        }

        private void ShowOverlay()
        {
            DragOverlay.Visibility = Visibility.Visible;
            DragOverlay.Opacity = 0;
            OverlayFadeIn.Begin();
        }

        private void HideOverlay()
        {
            if (!_dragOver) return;
            _dragOver = false;

            if (DragOverlay.Visibility != Visibility.Visible) return;
            OverlayFadeOut.Begin();
        }

        private void AddFiles(IReadOnlyList<string> paths)
        {
            foreach (var path in paths)
                AddToQueue(path);
        }

        /// <summary>入队(按路径去重;提取中禁止改动队列)。返回是否实际加入。</summary>
        private bool AddToQueue(string path)
        {
            if (_isExtracting) return false;
            if (QueueItems.Any(i => string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                return false;

            QueueItems.Add(new ImportQueueItem
            {
                FilePath = path,
                Status = "等待提取"
            });
            return true;
        }

        private void ClearQueue_Click(object sender, RoutedEventArgs e)
        {
            if (_isExtracting) return;
            QueueItems.Clear();
            ImportInfoBar.IsOpen = false;
        }

        /// <summary>移除单个队列项(列表项末尾的 ✕ 按钮)。</summary>
        private void RemoveItem_Click(object sender, RoutedEventArgs e)
        {
            if (_isExtracting) return;
            if (sender is Button { CommandParameter: ImportQueueItem item })
                QueueItems.Remove(item);
        }

        // ==================== 提取业务 ====================

        private async void StartExtractButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isExtracting || QueueItems.Count == 0) return;

            AppSettings settings;
            try
            {
                settings = await _configService.LoadAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[导入解包] 读取设置失败");
                ShowInfoBar("读取设置失败,无法获取导出目录", InfoBarSeverity.Error);
                return;
            }

            // 导出目录:绑定属性优先;留空兜底 Papers 页面底部的导出目录(DownloadPath,同一数据源)
            string outputRoot = ExportDirPath.Trim();
            if (string.IsNullOrEmpty(outputRoot))
                outputRoot = settings.Path?.DownloadPath ?? "";
            if (string.IsNullOrEmpty(outputRoot) || !Directory.Exists(outputRoot))
            {
                ShowInfoBar("导出目录为空或不存在,请点击\"导出目录\"按钮设置", InfoBarSeverity.Warning);
                return;
            }

            // 准备:为每个包分配唯一的输出文件夹名,input 直接指向源 pkg 文件
            // (batch input 兼容文件,无需再拷贝 pkg 到输出目录)
            // 重名检测:内存集合(本批次内去重)+ 磁盘(历史遗留目录不覆盖)
            _itemsByName.Clear();
            var usedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wallpapers = new List<WallpaperItem>();
            foreach (var item in QueueItems)
            {
                try
                {
                    string safeName = GetSafeName(Path.GetFileNameWithoutExtension(item.FilePath));
                    string title = safeName;
                    int seq = 1;
                    while (usedTitles.Contains(title) || Directory.Exists(Path.Combine(outputRoot, title)))
                        title = $"{safeName}_{seq++}";
                    usedTitles.Add(title);

                    item.OutputPath = Path.Combine(outputRoot, title);
                    item.Progress = 0;
                    item.Status = "等待提取";

                    // Title = 输出文件夹名(batch 按 Title 计算输出目录);事件路由键同步
                    wallpapers.Add(new WallpaperItem { FolderPath = item.FilePath, Title = title });
                    _itemsByName[title] = item;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[导入解包] 准备失败: {Path}", item.FilePath);
                    item.Status = "准备失败";
                }
            }

            if (wallpapers.Count == 0)
            {
                ShowInfoBar("没有可提取的壁纸包", InfoBarSeverity.Warning);
                return;
            }

            // 与"导入到编辑器"同款设置:输出项目文件夹,供 WE 编辑器/壁纸库直接使用
            var extractSettings = new ExtractSettings
            {
                OutputMode = 0,
                TexExportMode = 2,
                OutProjectJSON = true,
                UseProjectName = true,
                OneFolder = 0,
                CoverAllFiles = true,
                KeepSubfolderStructure = 0,
                LazyLoad = true,
            };

            _extractService = new RepkgCliService();
            _extractCts = new CancellationTokenSource();
            _isExtracting = true;
            _isPaused = false;
            UpdateRunningState();

            try
            {
                await _extractService.ExtractWallpapersAsync(
                    wallpapers, outputRoot, extractSettings,
                    OnExtractProgress, _extractCts.Token);

                FinalizeExtraction(settings, outputRoot);
            }
            catch (OperationCanceledException)
            {
                foreach (var item in QueueItems)
                {
                    if (item.Status is "等待提取" or "正在提取")
                        item.Status = "已停止";
                }
                ShowInfoBar("已停止提取", InfoBarSeverity.Warning);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[导入解包] 提取失败");
                ShowInfoBar($"提取失败:{ex.Message}", InfoBarSeverity.Error);
            }
            finally
            {
                _extractService = null;
                _extractCts = null;
                _isExtracting = false;
                _isPaused = false;
                UpdateRunningState();
            }
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isExtracting) return;

            if (_isPaused)
            {
                // 恢复:换新 cts 以撤销停止路径的取消(Papers 恢复同款)
                _extractCts?.Dispose();
                _extractCts = new CancellationTokenSource();
                _extractService?.Resume();
                _isPaused = false;
            }
            else
            {
                _extractService?.Pause();
                _isPaused = true;
            }
            UpdateRunningState();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _extractCts?.Cancel();
            _extractService?.Stop();
        }

        /// <summary>batch 进度事件 name|action|pct|entry → 队列项状态/进度(回 UI 线程更新)。</summary>
        private void OnExtractProgress(string msg)
        {
            var parts = msg.Split('|');
            if (parts.Length < 2) return; // 汇总消息(name 缺失),忽略

            if (!_itemsByName.TryGetValue(parts[0], out var item)) return;
            double pct = parts.Length > 2 && double.TryParse(parts[2], out var p) ? p : 0;

            _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                switch (parts[1])
                {
                    case "开始":
                        item.Status = "正在提取";
                        break;
                    case "解析PKG":
                        // 只前进不回退:batch 同壁纸条目跨 worker 处理,事件顺序理论上单调,
                        // 单调守卫兜底,配合 0.5% 阈值防抖让进度条平滑前进
                        if (pct > item.Progress) item.Progress = pct;
                        break;
                    case "完成":
                        item.Progress = 100;
                        item.Status = "提取完成";
                        break;
                    case "失败":
                        item.Progress = 100;
                        item.Status = "提取失败";
                        break;
                }
            });
        }

        /// <summary>扫描文件夹:递归找出所有 .pkg/.mpkg 并加入队列(按路径去重)。</summary>
        private async void ScanFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = await _pickerService.PickFolderAsync();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            List<string> found;
            try
            {
                found = await Task.Run(() =>
                {
                    var options = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System
                    };
                    return Directory.EnumerateFiles(folder, "*.pkg", options)
                        .Concat(Directory.EnumerateFiles(folder, "*.mpkg", options))
                        .ToList();
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[导入解包] 扫描文件夹失败: {Path}", folder);
                ShowInfoBar($"扫描失败:{ex.Message}", InfoBarSeverity.Error);
                return;
            }

            int added = 0;
            foreach (var path in found)
            {
                if (AddToQueue(path)) added++;
            }

            string msg = added > 0
                ? $"扫描到 {found.Count} 个壁纸包,已加入 {added} 个"
                : found.Count > 0
                    ? "这些壁纸包已在列表中"
                    : "该文件夹下没有找到 .pkg / .mpkg 文件";
            ShowInfoBar(msg, added > 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
        }

        /// <summary>Flyout 里的"选择文件夹":选完写回绑定属性(自动触发防抖保存)。</summary>
        private async void BrowseExportPath_Click(object sender, RoutedEventArgs e)
        {
            var folder = await _pickerService.PickFolderAsync();
            if (!string.IsNullOrEmpty(folder))
                ExportDirPath = folder;
        }

        /// <summary>一键填入编辑器项目目录。</summary>
        private async void UseProjectPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var s = await _configService.LoadAsync();
                string path = s.Path?.ProjectPath ?? "";
                if (!string.IsNullOrEmpty(path))
                    ExportDirPath = path;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[导入解包] 读取项目目录失败");
            }
        }

        /// <summary>一键填入 Papers 页面底部的导出目录。</summary>
        private async void UseDownloadPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var s = await _configService.LoadAsync();
                string path = s.Path?.DownloadPath ?? "";
                if (!string.IsNullOrEmpty(path))
                    ExportDirPath = path;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[导入解包] 读取导出目录失败");
            }
        }

        /// <summary>Flyout 打开时把焦点给文本框(WinUI 3 坑:Flyout 内容的键盘焦点不会自动落到 TextBox,不聚焦则无法输入)。</summary>
        private void ExportDirFlyout_Opened(object sender, object e)
        {
            ExportPathBox.Focus(FocusState.Programmatic);
        }

        // ==================== 导出目录持久化(500ms 防抖) ====================

        private void ScheduleSave()
        {
            _saveDebounceCts?.Cancel();
            _saveDebounceCts = new CancellationTokenSource();
            _ = SaveAfterDelayAsync(_saveDebounceCts.Token);
        }

        private async Task SaveAfterDelayAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(500, ct);
            }
            catch (TaskCanceledException)
            {
                return; // 又有新输入,放弃本次保存
            }

            try
            {
                var settings = await _configService.LoadAsync();
                settings.Path.ImportExportPath = _exportDirPath;
                await _configService.SaveAsync(settings);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[导入解包] 保存导出目录失败");
            }
        }

        /// <summary>提取全部结束后:补写缺失的 project.json、汇总提示、触发后台扫描(仅导出到项目路径时,否则壁纸不在库扫描范围)。</summary>
        private void FinalizeExtraction(AppSettings settings, string outputRoot)
        {
            int ok = 0, fail = 0;
            foreach (var item in QueueItems)
            {
                if (item.Status == "提取完成")
                {
                    ok++;
                    EnsureProjectJson(item);
                }
                else if (item.Status is "提取失败" or "准备失败")
                {
                    fail++;
                }
            }

            bool inLibraryPath = string.Equals(
                outputRoot.TrimEnd('\\'),
                (settings.Path?.ProjectPath ?? "").TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);

            if (ok > 0 && inLibraryPath)
            {
                try
                {
                    App.StartBackgroundScan(
                        settings.Path?.WorkshopPath ?? "",
                        settings.Path?.OfficialPath ?? "",
                        settings.Path?.ProjectPath ?? "",
                        settings.Path?.AcfPath ?? "",
                        settings.Path?.VdfPath,
                        settings.ScanCacheEnabled == "1");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[导入解包] 触发后台扫描失败");
                }
            }

            string msg = fail > 0
                ? $"提取完成:{ok} 个成功,{fail} 个失败"
                : $"提取完成,共 {ok} 个壁纸";
            ShowInfoBar(msg, fail > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }

        /// <summary>桌面 .pkg 包内没有 project.json(batch 也不复制包旁文件),缺则补写最小文件;mpkg 自带则跳过。</summary>
        private static void EnsureProjectJson(ImportQueueItem item)
        {
            if (string.IsNullOrEmpty(item.OutputPath)) return;

            var jsonPath = Path.Combine(item.OutputPath, "project.json");
            if (File.Exists(jsonPath)) return;

            try
            {
                string type = File.Exists(Path.Combine(item.OutputPath, "index.html")) ? "web" : "scene";
                File.WriteAllText(jsonPath,
                    JsonSerializer.Serialize(new { title = Path.GetFileName(item.OutputPath), type }));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[导入解包] 补写 project.json 失败: {Path}", jsonPath);
            }
        }

        private void ShowInfoBar(string message, InfoBarSeverity severity)
        {
            ImportInfoBar.Message = message;
            ImportInfoBar.Severity = severity;
            ImportInfoBar.IsOpen = true;
        }

        private static bool IsPkgFile(string name)
        {
            string ext = Path.GetExtension(name);
            return string.Equals(ext, ".pkg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".mpkg", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetSafeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name);
            foreach (var c in new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' })
                sb.Replace(c, '_');
            for (int i = 0; i < sb.Length; i++)
                if (invalid.Contains(sb[i])) sb[i] = '_';
            return sb.ToString().Trim();
        }
    }
}

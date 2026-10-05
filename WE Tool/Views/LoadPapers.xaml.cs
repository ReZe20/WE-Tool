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
using WE_Tool.Json;
using WE_Tool.Service;
using WE_Tool.Helper;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace WE_Tool
{
    /// <summary>
    /// 导入解包页：用户导入本地 .pkg/.mpkg 壁纸包,复制到导出目录后调用 repkg_re 解包。
    /// 队列按结果分态:成功项收到完成事件即整行清出,失败/停止项留在列表里(失败行进度条转 Error 红),
    /// 再点开始提取就只会重跑留下的这些。批尾对成功项补写缺失的 project.json(桌面 pkg 包内无此文件),
    /// 并触发后台扫描让壁纸出现在"我的壁纸"。
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

        /// <summary>导航徽标是否处于失败(红)状态:失败后保持红色,直到下次提取开始才复位。</summary>
        private bool _navBadgeError;

        /// <summary>进度事件名(壁纸 Title=安全名)→ 队列项。</summary>
        private readonly Dictionary<string, ImportQueueItem> _itemsByName = new(StringComparer.Ordinal);

        /// <summary>本轮已"完成即清行"的成功项输出目录:行已从列表移除,批尾的 project.json 补写和汇总计数只能靠它。</summary>
        private readonly List<string> _succeededOutputs = new();

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

        private string _projectDirTip = "";
        private string _papersExportDirTip = "";

        /// <summary>预设按钮 LoadPapers_ProjectDir 的 ToolTip:点下去填入的完整路径,未设置时显示"未设置"。</summary>
        public string ProjectDirTip
        {
            get => _projectDirTip;
            private set
            {
                if (_projectDirTip == value) return;
                _projectDirTip = value;
                OnPropertyChanged();
            }
        }

        /// <summary>预设按钮 LoadPapers_ExportDir 的 ToolTip,同上。</summary>
        public string PapersExportDirTip
        {
            get => _papersExportDirTip;
            private set
            {
                if (_papersExportDirTip == value) return;
                _papersExportDirTip = value;
                OnPropertyChanged();
            }
        }

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

            PauseButton.Label = LanguageHelper.GetResource(
                _isPaused ? "LoadPapers_Resume.Label" : "LoadPapers_Pause.Label");
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
            AnimatedIconPlayer.PlayOnce(sender);   // [删除图标动画 2026-09]
            ProbeQueue($"手动清空队列:原 {QueueItems.Count} 行");
            QueueItems.Clear();
            ImportInfoBar.IsOpen = false;
        }

        /// <summary>移除单个队列项(列表项末尾的 ✕ 按钮)。</summary>
        private void RemoveItem_Click(object sender, RoutedEventArgs e)
        {
            if (_isExtracting) return;
            if (sender is Button { CommandParameter: ImportQueueItem item })
            {
                ProbeQueue($"手动移除单行: {item.FileName} | 状态={item.Status}");
                QueueItems.Remove(item);
            }
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
                ShowInfoBar("导出目录为空或不存在,请在\"设置导出目录\"中选择", InfoBarSeverity.Warning);
                return;
            }

            // 准备:为每个包分配唯一的输出文件夹名,input 直接指向源 pkg 文件
            // (batch input 兼容文件,无需再拷贝 pkg 到输出目录)
            // 重名检测:内存集合(本批次内去重)+ 磁盘(历史遗留目录不覆盖)
            _itemsByName.Clear();
            _succeededOutputs.Clear();
            var usedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wallpapers = new List<WallpaperItem>();
            foreach (var item in QueueItems)
            {
                try
                {
                    string safeName = GetSafeName(Path.GetFileNameWithoutExtension(item.FilePath));
                    string title = safeName;
                    int seq = 1;
                    // 重名检测排除本队列项自己的 OutputPath:停止后再点开始,该项上次已分配
                    // 的输出目录不算"已存在"冲突(原样复用续提),而不是另建 _1 新目录;
                    // 其它项的历史遗留目录仍照常加序号防止覆盖
                    while (usedTitles.Contains(title) ||
                           (Directory.Exists(Path.Combine(outputRoot, title)) &&
                            !string.Equals(
                                Path.Combine(outputRoot, title),
                                item.OutputPath,
                                StringComparison.OrdinalIgnoreCase)))
                        title = $"{safeName}_{seq++}";
                    usedTitles.Add(title);

                    item.OutputPath = Path.Combine(outputRoot, title);
                    item.Progress = 0;
                    item.Status = "等待提取";
                    item.IsError = false;   // 上一轮的失败红标随重跑退掉

                    // Title = 输出文件夹名(batch 按 Title 计算输出目录);事件路由键同步
                    wallpapers.Add(new WallpaperItem { FolderPath = item.FilePath, Title = title });
                    _itemsByName[title] = item;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[导入解包] 准备失败: {Path}", item.FilePath);
                    item.Status = "准备失败";
                    item.IsError = true;
                    ProbeQueue($"准备失败 → 留行 {item.FileName} | {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (wallpapers.Count == 0)
            {
                ShowInfoBar("没有可提取的壁纸包", InfoBarSeverity.Warning);
                return;
            }

            // 导出配方来自设置页「导入解包页面输出设置」(独立于 Papers 的「已安装壁纸页面输出设置」,
            // 见 AppSettings.ImportExtract)。该分区的默认值 = 原先这里写死的"编辑器同款"
            // (全量输出 / TEX 转成图片 / 写 project.json / 每包一个文件夹 / 覆盖同名 / 保持源目录结构),
            // 所以用户不动设置时,本页产物与改动前完全一致。
            var extractSettings = settings.ImportExtract.Clone();
            // 全局「性能」区同步生效:并发线程数与进程优先级从 AppSettings.Extract(设置页「性能」区)取,
            // 导入解包页不为这两项另设开关(单一来源)。先克隆再覆盖,避免写回共享的配置对象。
            extractSettings.MaxConcurrentExtractions = settings.Extract.MaxConcurrentExtractions;
            extractSettings.ProcessPriority = settings.Extract.ProcessPriority;

            _extractService = new RepkgCliService();
            _extractCts = new CancellationTokenSource();
            _isExtracting = true;
            _isPaused = false;
            UpdateRunningState();
            TaskbarProgressService.SetProgress(0);
            // 导航栏徽标:显示本次待提取数量(新任务开始,复位失败红标)
            _navBadgeError = false;
            NavBadgeService.SetBadge("LoadPapers", wallpapers.Count);

            ProbeQueue(
                $"==== 开始一轮:待提取 {wallpapers.Count}/{QueueItems.Count} | 输出根={outputRoot} | " +
                $"OneFolder={extractSettings.OneFolder}(0=每包一夹) 覆盖={extractSettings.CoverAllFiles} " +
                $"跳过已提取={extractSettings.SkipExistingOutput} 输出project.json={extractSettings.OutProjectJSON}");

            // 进程优先级是 RepkgCliService 的静态量(启动每个 repkg 子进程时套用),Papers 侧同样在启动前设置;
            // 不设的话会用上一次的值或默认 Normal —— 所以每次开始提取都按设置页「性能」区刷新一遍。
            RepkgCliService.SetProcessPriorityLevel(settings.Extract.ProcessPriority);

            try
            {
                await _extractService.ExtractWallpapersAsync(
                    wallpapers, outputRoot, extractSettings,
                    OnExtractProgress, _extractCts.Token);

                FinalizeExtraction(settings, outputRoot);
                TaskbarProgressService.SetProgress(100);
            }
            catch (OperationCanceledException)
            {
                foreach (var item in QueueItems)
                {
                    if (item.Status is "等待提取" or "正在提取")
                        item.Status = "已停止";
                }
                ProbeQueue($"整批被停止:剩余 {QueueItems.Count} 行转「已停止」");
                ShowInfoBar("已停止提取", InfoBarSeverity.Warning);
                NotificationService.NotifyIfUnfocused("导入解包已停止", "提取已停止");
                TaskbarProgressService.Clear();
            }
            catch (Exception ex)
            {
                ProbeQueue($"整批抛异常: {ex.GetType().Name}: {ex.Message} | 剩余 {QueueItems.Count} 行");
                Log.Error(ex, "[导入解包] 提取失败");
                ShowInfoBar($"提取失败:{ex.Message}", InfoBarSeverity.Error);
                NotificationService.NotifyIfUnfocused("导入解包失败", $"提取失败:{ex.Message}");
                TaskbarProgressService.SetError();
                _navBadgeError = true;
            }
            finally
            {
                _extractService = null;
                _extractCts = null;
                _isExtracting = false;
                _isPaused = false;
                UpdateRunningState();
                // 导航栏徽标:成功项已清出,队列剩下的就是要处理的;全成功→0→徽标隐藏
                if (_navBadgeError)
                    NavBadgeService.SetBadge("LoadPapers", Math.Max(1, QueueItems.Count), NavBadgeState.Error);
                else
                    NavBadgeService.SetBadge("LoadPapers", QueueItems.Count);
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
                TaskbarProgressService.SetProgress(GetQueueProgress());
                // 导航栏徽标:恢复 → 绿色
                NavBadgeService.SetBadge("LoadPapers", QueueItems.Count, NavBadgeState.Running);
            }
            else
            {
                _extractService?.Pause();
                _isPaused = true;
                TaskbarProgressService.SetPaused();
                // 导航栏徽标:暂停 → 黄色
                NavBadgeService.SetBadge("LoadPapers", QueueItems.Count, NavBadgeState.Paused);
            }
            UpdateRunningState();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _extractCts?.Cancel();
            _extractService?.Stop();
        }

        /// <summary>
        /// 本轮整体进度(0~100)。成功项已逐条清出队列,所以分母是"已清出的 + 还留着的":
        /// 清出的按 100 计,留下的失败项也按 100 计(已是终态),其余按各自条目 pct。
        /// </summary>
        private double GetQueueProgress()
        {
            int total = _succeededOutputs.Count + QueueItems.Count;
            if (total == 0) return 0;
            double sum = _succeededOutputs.Count * 100.0;
            foreach (var it in QueueItems)
                sum += it.IsError ? 100 : it.Progress;
            return sum / total;
        }

        /// <summary>
        /// batch 进度事件 name|action|pct|entry → 队列项状态/进度(回 UI 线程更新)。
        /// _itemsByName 只在 UI 线程增删(开始提取时整表 Clear),这里读它来自子进程输出线程;
        /// 因此完成项清行后不剔键 —— 迟到的条目事件只会改到一个已经离开列表的对象上,无副作用。
        /// </summary>
        private void OnExtractProgress(string msg)
        {
            var parts = msg.Split('|');
            if (parts.Length < 2)
            {
                ProbeQueue($"汇总消息(无 name,已忽略): {msg}");
                return;
            }

            if (!_itemsByName.TryGetValue(parts[0], out var item))
            {
                ProbeQueue($"路由不命中(这一名没有对应队列项): {msg}");
                return;
            }
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
                        TaskbarProgressService.SetProgress(GetQueueProgress());
                        break;
                    case "完成":
                        // [临时埋点 2026-10-02] 清行前记下这张的输出目录实况:空/不存在却说"完成"就是漏判
                        ProbeQueue($"action=完成 → 清行 {item.FileName} | 输出目录={ProbeDirState(item.OutputPath)} | 原状态={item.Status}");
                        _succeededOutputs.Add(item.OutputPath);
                        QueueItems.Remove(item);
                        TaskbarProgressService.SetProgress(GetQueueProgress());
                        NavBadgeService.SetBadge("LoadPapers", QueueItems.Count);
                        break;
                    case "失败":
                        ProbeQueue($"action=失败 → 留行 {item.FileName} | 输出目录={ProbeDirState(item.OutputPath)} | 原状态={item.Status}");
                        item.Progress = 100;
                        item.Status = "提取失败";
                        item.IsError = true;
                        TaskbarProgressService.SetProgress(GetQueueProgress());
                        NavBadgeService.SetBadge("LoadPapers", QueueItems.Count);
                        break;
                    default:
                        ProbeQueue($"action 未识别(不改动行): {msg}");
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
        private async void ExportDirFlyout_Opened(object sender, object e)
        {
            // 弹层不自动继承主窗口运行时主题,打开时显式应用(公共逻辑见 App.ApplyFlyoutTheme)
            App.ApplyFlyoutTheme(sender, e);
            ExportPathBox.Focus(FocusState.Programmatic);
            await RefreshPresetTipsAsync();
        }

        /// <summary>读出两个预设按钮将要填入的路径,作为它们的 ToolTip(ConfigService 有进程内缓存,不重复读盘)。</summary>
        private async Task RefreshPresetTipsAsync()
        {
            string project = "", download = "";
            try
            {
                var s = await _configService.LoadAsync();
                project = s.Path?.ProjectPath ?? "";
                download = s.Path?.DownloadPath ?? "";
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[导入解包] 读取预设目录失败");
            }
            string unset = LanguageHelper.GetResource("LoadPapers_PresetUnset");
            ProjectDirTip = string.IsNullOrEmpty(project) ? unset : project;
            PapersExportDirTip = string.IsNullOrEmpty(download) ? unset : download;
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
            // 成功项已逐条清出队列,计数只能取 _succeededOutputs;project.json 的补写也留在批尾:
            // 包旁那份由 PostProcessWallpaper 在 repkg 进程退出后才复制,提前补写会抢在它前面。
            int ok = _succeededOutputs.Count;
            int fail = 0;
            foreach (var item in QueueItems)
            {
                if (item.IsError) fail++;
            }

            foreach (var output in _succeededOutputs)
            {
                EnsureProjectJson(output);
            }

            ProbeQueue(
                $"==== 批尾:成功 {_succeededOutputs.Count} | 留下 {QueueItems.Count} 行" +
                $"[{string.Join(", ", QueueItems.Select(i => $"{i.FileName}:{i.Status}"))}]");

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

            // 主窗口不在焦点时弹系统通知
            NotificationService.NotifyIfUnfocused("导入解包完成", msg);
        }

        /// <summary>桌面 .pkg 包内没有 project.json(batch 也不复制包旁文件),缺则补写最小文件;mpkg 自带则跳过。</summary>
        private static void EnsureProjectJson(string outputPath)
        {
            if (string.IsNullOrEmpty(outputPath)) return;

            var jsonPath = Path.Combine(outputPath, "project.json");
            if (File.Exists(jsonPath)) return;

            try
            {
                string type = File.Exists(Path.Combine(outputPath, "index.html")) ? "web" : "scene";
                File.WriteAllText(jsonPath,
                    JsonSerializer.Serialize(new LoadPapersEntry(Path.GetFileName(outputPath), type), JsonContext.Default.LoadPapersEntry));
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

        // ==================== 临时诊断埋点(定位"错误项被清出列表"之后整段删掉)====================
        // 直写文件,不走 Serilog:LogLevel=Off 时探针会被吞(见日志页那套口径)。

        private static readonly object ProbeLock = new();

        private static void ProbeQueue(string line)
        {
            try
            {
                var path = Path.Combine(WE_Tool.ViewModels.AppSettingsHelper.LogPath, "import_queue.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                lock (ProbeLock)
                    File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch { /* 埋点失败不影响提取 */ }
        }

        private static string ProbeDirState(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir)) return "未分配";
                if (!Directory.Exists(dir)) return "不存在";
                int n = 0;
                foreach (var _ in Directory.EnumerateFileSystemEntries(dir))
                {
                    if (++n >= 50) break;
                }
                return $"{n}{(n >= 50 ? "+" : "")} 项";
            }
            catch (Exception ex)
            {
                return $"读取失败 {ex.GetType().Name}";
            }
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

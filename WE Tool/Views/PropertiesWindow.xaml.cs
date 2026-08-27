using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Converters;
using WE_Tool.Models;
using WE_Tool.Service;
using WE_Tool.ViewModels;
using Windows.Graphics;
using WinUIEx;

namespace WE_Tool
{
    /// <summary>
    /// 壁纸属性独立窗口:双页(文件属性 = 预览/信息/文件树;壁纸属性 = 可编辑属性列表+保存)。
    /// 可同时打开多个:每次打开显示当前选中的壁纸(快照),窗口之间互不跟随。
    /// </summary>
    public sealed partial class PropertiesWindow : WindowEx, INotifyPropertyChanged
    {
        /// <summary>保持所有已打开窗口的强引用——WinUI 3 中 Window 对象被 GC 回收会导致窗口消失</summary>
        private static readonly List<PropertiesWindow> _openWindows = new();

        /// <summary>当前已打开的属性窗口数。</summary>
        public static int OpenWindowCount => _openWindows.Count;

        public SettingsViewModel ViewModel { get; }

        public PropertiesWindow()
        {
            var app = Application.Current as App;
            ViewModel = app?.ViewModel ?? new SettingsViewModel(new ConfigService(), new PickerService());
            InitializeComponent();
            // 壁纸属性页的 DataContext = 窗口自身(绑定 Properties/IsPropertyLoading 等)
            WallpaperPropsRoot.DataContext = this;
            // 虚拟化属性列表:XamlCompiler 对窗口内 ItemsRepeater 标签稳定崩溃(Pass1 MSB3073),
            // 改为 code-behind 实例化——运行时创建,虚拟化行为不变(StackLayout + 外层 ScrollViewer)
            PropertyItemsHost.Children.Add(new ItemsRepeater
            {
                ItemsSource = Properties,
                Layout = new StackLayout { Spacing = 4 },
                ItemTemplate = RootGrid.Resources["PropertyRowTemplate"] as DataTemplate
            });
            // 文件属性页虚拟化列表:ItemsRepeater 由 code-behind 创建(同 PropertyItemsHost 模式,
            // XamlCompiler 对窗口内 ItemsRepeater 标签稳定 Pass1 崩溃);直接挂 ScrollViewer.Content,
            // 获得有界视口,StackLayout 才真正虚拟化(行模板 FileInfoRowTemplate 在 RootGrid.Resources)
            ContentRoot.Content = new ItemsRepeater
            {
                Layout = new StackLayout(),
                ItemTemplate = RootGrid.Resources["FileInfoRowTemplate"] as DataTemplate
            };
            Properties.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(PropertyEmptyHintVisibility));
                OnPropertyChanged(nameof(PropertyListVisibility));
                UpdateSaveButtonState();
            };
            // 自定义标题栏:去系统标题栏,顶部 48px 留空当标题栏(Tall 高度)
            ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            // 标题:MRT Core 默认构造不依赖视图上下文(LanguageHelper 同款);
            // 旧 API GetForCurrentView 在窗口激活前(构造函数里)会挂起等待视图上下文 → 卡死崩溃;
            // GetResource 内部 '.'→'/' 转层级键(直接 GetString('X.Y.Text') 抛 0x80073B17),找不到返回键名,空值兜底,
            // 保证任务栏/Alt+Tab 显示"属性"而非组件名
            string title = "属性";
            try
            {
                var t = LanguageHelper.GetResource("PropertiesPanel_Header.Text");
                if (!string.IsNullOrEmpty(t)) title = t;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取属性窗口标题资源失败");
            }
            Title = title;
            AppWindow.Resize(new SizeInt32(560, 720));
            ApplyTheme();

            // 设置页切换主题时跟随
            ViewModel.AppSettingsVM.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AppSettingsViewModel.Theme))
                    ApplyTheme();
            };

            Closed += (s, e) => _openWindows.Remove(this);
        }

        /// <summary>打开时快照的壁纸(与主窗口选中分离,不跟随主窗口切换)</summary>
        public WallpaperItem? Selected { get; private set; }

        /// <summary>是否显示"壁纸属性"页(组件等无 project.json 可配置属性的条目传 false,只显示文件属性页)</summary>
        public bool ShowPropsPage { get; set; } = true;

        public static void Open(WallpaperItem wallpaper, bool showPropsPage = true)
        {
            // 去重:同一壁纸已有窗口则激活已有窗口,不重复创建
            var existing = _openWindows.FirstOrDefault(w => w.Selected?.FolderPath == wallpaper.FolderPath);
            if (existing != null)
            {
                existing.Activate();
                return;
            }

            // 推迟一帧创建窗口:窗口构造(XAML 解析/可视树构建/绑定首次求值)是同步重活,
            // 直接执行会短暂卡住主窗口;Low 优先级让本次点击先完成、UI 空闲后再建窗口。
            // 前提:Open 从主窗口 UI 线程调用(GetForCurrentThread 取到主窗口队列)
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                var window = new PropertiesWindow();
                _openWindows.Add(window);
                window.ShowPropsPage = showPropsPage;
                window.SetWallpaper(wallpaper);
                window.Activate();
            });
        }

        private void SetWallpaper(WallpaperItem wallpaper)
        {
            Selected = wallpaper;
            if (!ShowPropsPage)
                WallpaperPropsNavItem.Visibility = Visibility.Collapsed;
            BuildFileInfoRows(wallpaper);
            _ = RefreshFileTreeAsync();
            if (ShowPropsPage)
                _ = LoadPropertiesAsync(wallpaper);
        }

        // ========== 文件属性页:虚拟化行构建(快照语义,窗口打开时一次性取值) ==========

        private static readonly FileSizeToString FileSizeConv = new();
        private static readonly TypeToDisplay TypeConv = new();
        private static readonly SourceToDisplay SourceConv = new();
        private static readonly RatingToDisplay RatingConv = new();
        private static readonly TagToDisplay TagConv = new();
        private static readonly DescriptionToDisplay DescriptionConv = new();
        private static readonly DateTimeToString DateTimeConv = new();

        /// <summary>与旧 XAML 绑定语义一致:null 走 FallbackValue '-'、非 null 走转换器原样输出</summary>
        private static string Format(IValueConverter converter, object? value)
            => value == null ? "-" : (converter.Convert(value, typeof(string), null, null) as string) ?? "-";

        /// <summary>构建文件属性页虚拟化行(标签用 LanguageHelper 取:MRT Core 键是 '/' 层级形式,
        /// ResourceLoader.GetString 直接传 'X.Y.Text' 会抛 0x80073B17;LanguageHelper 内部 '.'→'/' + 缓存)</summary>
        private void BuildFileInfoRows(WallpaperItem? wallpaper)
        {
            var rows = new List<FileInfoRow>();

            if (wallpaper != null)
            {
                rows.Add(FileInfoRow.Preview(wallpaper.Preview));
                rows.Add(FileInfoRow.Title(wallpaper.Title));

                rows.Add(FileInfoRow.Section(LanguageHelper.GetResource("PropertiesPanel_BasicInfo.Text")));
                rows.Add(FileInfoRow.Info(LanguageHelper.GetResource("PropertiesPanel_TypeLabel.Text"), Format(TypeConv, wallpaper.Type)));
                rows.Add(FileInfoRow.Info(LanguageHelper.GetResource("PropertiesPanel_SourceLabel.Text"), Format(SourceConv, wallpaper.Source)));
                rows.Add(FileInfoRow.Info(LanguageHelper.GetResource("PropertiesPanel_RatingLabel.Text"), Format(RatingConv, wallpaper.ContentRating)));
                rows.Add(FileInfoRow.Info(LanguageHelper.GetResource("PropertiesPanel_TagLabel.Text"), Format(TagConv, wallpaper.Tags), wrap: true));
                rows.Add(FileInfoRow.Info(LanguageHelper.GetResource("Description.Text"), Format(DescriptionConv, wallpaper.Description), wrap: true));
                rows.Add(FileInfoRow.Divider());

                rows.Add(FileInfoRow.Section(LanguageHelper.GetResource("PropertiesPanel_FileInfo.Text")));
                rows.Add(FileInfoRow.Info(LanguageHelper.GetResource("FileSize.Text"), Format(FileSizeConv, wallpaper.FileSize)));
                rows.Add(FileInfoRow.Info(LanguageHelper.GetResource("AcfSize.Text"), Format(FileSizeConv, wallpaper.AcfSize)));
                rows.Add(FileInfoRow.Info(LanguageHelper.GetResource("FolderPath.Text"), wallpaper.FolderPath ?? "-", wrap: true));
                rows.Add(FileInfoRow.Info(LanguageHelper.GetResource("WorkshopID.Text"), wallpaper.WorkshopID ?? "-"));
                rows.Add(FileInfoRow.Info(LanguageHelper.GetResource("Dependency.Text"), wallpaper.Dependency ?? "-", wrap: true));
                rows.Add(FileInfoRow.Divider());

                rows.Add(FileInfoRow.Section(LanguageHelper.GetResource("PropertiesPanel_TimeInfo.Text")));
                rows.Add(FileInfoRow.Info(LanguageHelper.GetResource("CreationTime.Text"), Format(DateTimeConv, wallpaper.CreationTime)));
                rows.Add(FileInfoRow.Info(LanguageHelper.GetResource("UpdateTime.Text"), Format(DateTimeConv, wallpaper.UpdateTime)));
                rows.Add(FileInfoRow.Info(LanguageHelper.GetResource("AcfUpdateTime.Text"), Format(DateTimeConv, wallpaper.AcfUpdateTime)));
                rows.Add(FileInfoRow.Divider());

                rows.Add(FileInfoRow.Section(LanguageHelper.GetResource("FileStructure.Text")));
                rows.Add(FileInfoRow.Tree());
            }

            if (ContentRoot.Content is ItemsRepeater repeater)
                repeater.ItemsSource = rows;
        }

        // ========== 壁纸属性页:快照壁纸的 project.json 属性(懒加载/增量填充/代次号,模式同 SettingsViewModel) ==========

        public ObservableCollection<WallpaperProperty> Properties { get; } = [];

        private int _propertyLoadVersion;
        private string? _propertyFolder;

        private bool _isPropertyLoading;
        public bool IsPropertyLoading
        {
            get => _isPropertyLoading;
            private set
            {
                if (_isPropertyLoading == value) return;
                _isPropertyLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PropertyPanelLoadingVisibility));
                OnPropertyChanged(nameof(PropertyEmptyHintVisibility));
                UpdateSaveButtonState();
            }
        }

        public Visibility PropertyPanelLoadingVisibility => IsPropertyLoading ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>加载完成且无属性时显示"没有可配置属性"占位</summary>
        public Visibility PropertyEmptyHintVisibility
            => !IsPropertyLoading && _propertyFolder != null && Properties.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public Visibility PropertyListVisibility => Properties.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        private void UpdateSaveButtonState()
            => PropertySaveButton.IsEnabled = !IsPropertyLoading && _propertyFolder != null && Properties.Any(p => p.IsEditable);

        private async Task LoadPropertiesAsync(WallpaperItem? item)
        {
            int version = ++_propertyLoadVersion;
            string? folder = item?.FolderPath;
            _propertyFolder = folder;

            IsPropertyLoading = !string.IsNullOrEmpty(folder);
            Properties.Clear();

            if (string.IsNullOrEmpty(folder))
                return;

            try
            {
                var props = await Task.Run(() => WallpaperPropertyParser.Parse(folder));
                if (version != _propertyLoadVersion) return;

                // 增量填充防卡顿:大壁纸(200+ 属性)每批添加后等一帧渲染,创建+布局渐进分摊,UI 不冻结
                Properties.Clear();
                foreach (var chunk in props.Chunk(10))
                {
                    if (version != _propertyLoadVersion) return;
                    foreach (var p in chunk)
                        Properties.Add(p);
                    await Task.Delay(16);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "加载壁纸属性失败: {Folder}", folder);
            }
            finally
            {
                if (version == _propertyLoadVersion)
                    IsPropertyLoading = false;
            }
        }

        private async void PropertySaveButton_Click(object sender, RoutedEventArgs e)
        {
            var folder = _propertyFolder;
            if (string.IsNullOrEmpty(folder)) return;

            // 扁平化收集:group(Expander) 内的可编辑属性也要写回
            var props = Properties
                .SelectMany(p => p.Children.Prepend(p))
                .Where(p => p.IsEditable)
                .ToList();
            if (props.Count == 0)
            {
                await DialogHelper.ShowMessageAsync("保存属性", "没有可编辑属性");
                return;
            }

            var (ok, error) = await Task.Run(() => WallpaperPropertyWriter.Save(folder, props));
            if (ok)
                await DialogHelper.ShowMessageAsync("保存属性", "属性已保存到 project.json。");
            else
                await DialogHelper.ShowMessageAsync("保存失败", error ?? "未知错误");
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            bool isProps = (args.SelectedItem as NavigationViewItem)?.Tag as string == "props";
            WallpaperPropsRoot.Visibility = isProps ? Visibility.Visible : Visibility.Collapsed;
            ContentRoot.Visibility = isProps ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>把含 \n 的文本拆成 Run + LineBreak 序列(&lt;br/&gt; 在模型层已转为 \n)</summary>
        private static void AddTextWithLineBreaks(Paragraph paragraph, string text)
        {
            var parts = text.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) paragraph.Inlines.Add(new LineBreak());
                if (parts[i].Length > 0) paragraph.Inlines.Add(new Run { Text = parts[i] });
            }
        }

        /// <summary>链接按钮内容:TextBlock 先建再填 Inlines(内含 &lt;br/&gt; 转出的换行拆行),拷贝载体字体。</summary>
        private static TextBlock BuildLinkContent(RichTextBlock rtb, string text)
        {
            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = rtb.FontSize,
                FontWeight = rtb.FontWeight,
            };
            BuildRuns(tb, text);
            return tb;
        }

        /// <summary>同上,但写入目标 TextBlock 的 Inlines(InlineCollection 无公共构造器,不能独立创建)。</summary>
        private static void BuildRuns(TextBlock target, string text)
        {
            var parts = text.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) target.Inlines.Add(new LineBreak());
                if (parts[i].Length > 0) target.Inlines.Add(new Run { Text = parts[i] });
            }
        }

        /// <summary>
        /// 纯文本组件链接/图片渲染:含 <a href>/裸 URL 时用 InlineUIContainer + HyperlinkButton 接管显示,
        /// 含 <img> 时渲染 HTTP 图片;约定:外部链接统一 HyperlinkButton + App.xaml 的 ExternalLinkButtonStyle
        /// (无阴影无背景),样式必须从 Application.Current.Resources 取(页面/窗口 Resources 索引器抛
        /// COMException 0x80004005);载体必须用 RichTextBlock——InlineUIContainer 不能进 TextBlock.Inlines
        /// (运行时会抛 System.ArgumentException),Paragraph 才支持;内联按钮不继承字体,内容 TextBlock 显式拷贝。
        /// 图片段不显示其文本(剥标签残留的换行/占位空白段跳过)。
        /// </summary>
        private void TextBlock_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (sender is not RichTextBlock rtb || rtb.DataContext is not WallpaperProperty prop)
                return;

            rtb.Blocks.Clear();
            var paragraph = new Paragraph();

            var segments = prop.LinkSegments;
            bool hasLink = segments.Any(s => s.Url != null);
            bool hasImage = prop.ImageSegments.Count > 0;

            // 无链接无图片:纯文本,<br/> 已转的换行用 LineBreak 表达
            if (!hasLink && !hasImage)
            {
                AddTextWithLineBreaks(paragraph, prop.DisplayText);
                rtb.Blocks.Add(paragraph);
                return;
            }

            // 有链接/图片:分段渲染;空白文本段跳过(img 剥标签残留的换行/占位不显示)
            foreach (var (text, url) in segments)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (url == null)
                {
                    AddTextWithLineBreaks(paragraph, text);
                }
                else
                {
                    var container = new InlineUIContainer();
                    container.Child = new HyperlinkButton
                    {
                        NavigateUri = new Uri(url),
                        Style = (Style)Application.Current.Resources["ExternalLinkButtonStyle"],
                        Padding = new Thickness(0), // 内联:去掉 ButtonPadding,避免文字偏移
                        Content = BuildLinkContent(rtb, text)
                    };
                    paragraph.Inlines.Add(container);
                }
            }

            // 图片段(<img src>,HTTP 加载;外层 <a href> 时整图可点击)
            foreach (var (src, link, width, height) in prop.ImageSegments)
            {
                var image = new Image
                {
                    Source = new BitmapImage(new Uri(src)),
                    Stretch = Stretch.Uniform,
                    MaxWidth = 200,
                    MaxHeight = 200,
                    Margin = new Thickness(0, 4, 0, 4)
                };
                if (width.HasValue) image.Width = Math.Min(width.Value, 200);
                if (height.HasValue) image.Height = Math.Min(height.Value, 200);
                // 加载失败(网络/URL 失效)隐藏,不占位
                image.ImageFailed += (s, e) => image.Visibility = Visibility.Collapsed;

                var container = new InlineUIContainer();
                if (link != null)
                {
                    container.Child = new HyperlinkButton
                    {
                        NavigateUri = new Uri(link),
                        Style = (Style)Application.Current.Resources["ExternalLinkButtonStyle"],
                        Padding = new Thickness(0),
                        Content = image
                    };
                }
                else
                {
                    container.Child = image;
                }
                paragraph.Inlines.Add(container);
            }
            rtb.Blocks.Add(paragraph);
        }

        /// <summary>
        /// combo 类型下拉:DropDownButton + 动态菜单——显示区是普通 TextBlock(绑定 ComboDisplayText),
        /// 没有 ComboBox SelectionBoxItem 的滚动显示空白 bug(实测 SelectedIndex/SelectedItem 状态正确
        /// 仅显示区不刷新的 WinUI 缺陷,滚动/虚拟化重用均触发,各种 workaround 无效,故换控件根治)。
        /// </summary>
        private void ComboButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not DropDownButton button || button.DataContext is not WallpaperProperty prop)
                return;

            var flyout = new MenuFlyout();
            string group = $"Combo_{prop.Key}";
            for (int i = 0; i < prop.Options.Count; i++)
            {
                int index = i; // 闭包捕获
                var item = new RadioMenuFlyoutItem
                {
                    Text = prop.Options[i].Label,
                    IsChecked = i == prop.ComboIndex,
                    GroupName = group
                };
                item.Click += (s, e2) => prop.ComboIndex = index;
                flyout.Items.Add(item);
            }
            button.Flyout = flyout;
            flyout.ShowAt(button);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private void ApplyTheme()
        {
            string theme = ViewModel.AppSettingsVM.Theme ?? "";
            ElementTheme elementTheme = theme switch
            {
                "Dark" => ElementTheme.Dark,
                "Light" => ElementTheme.Light,
                _ => ElementTheme.Default
            };
            if (Content is FrameworkElement rootElement)
                rootElement.RequestedTheme = elementTheme;
        }

        // ========== File structure TreeView ==========

        /// <summary>刷新代次号:防止快速切换壁纸时异步枚举乱序完成导致文件树串台</summary>
        private int _treeLoadVersion;

        /// <summary>当前实化的文件树(模板内实例:虚拟化回收后重新实化时由 Loaded 更新)</summary>
        private TreeView? _treeView;

        /// <summary>根目录扫描结果缓存:树行重实化时同步重建,不重复扫磁盘</summary>
        private List<string>? _cachedDirs;
        private List<(string Name, FileItemType Type, long Size)>? _cachedFiles;
        private bool _cachedDenied;
        private string? _cachedRootName;

        private async Task RefreshFileTreeAsync()
        {
            int version = ++_treeLoadVersion;

            var wallpaper = Selected;
            if (wallpaper == null || string.IsNullOrEmpty(wallpaper.FolderPath) || !Directory.Exists(wallpaper.FolderPath))
                return;

            var folderPath = wallpaper.FolderPath;

            // 目录枚举是磁盘 IO,放后台线程,避免打开窗口时卡住主窗口
            List<string> dirs;
            List<(string Name, FileItemType Type, long Size)> files;
            bool denied = false;
            try
            {
                (dirs, files) = await Task.Run(() => ScanDirectory(folderPath));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"扫描目录异常: {folderPath}");
                dirs = new List<string>();
                files = new List<(string, FileItemType, long)>();
                denied = true;
            }

            // 期间已有更新的刷新请求,丢弃本次结果,避免串台
            if (version != _treeLoadVersion) return;

            // 缓存扫描结果,再填充当前实化的树(可能尚未实化,Loaded 时会用缓存补)
            _cachedDirs = dirs;
            _cachedFiles = files;
            _cachedDenied = denied;
            _cachedRootName = Path.GetFileName(folderPath);
            PopulateTree();
        }

        /// <summary>文件树行被虚拟化回收后重新实化(滚回视野)时:同步用缓存重建根节点</summary>
        private void FileStructureTree_Loaded(object sender, RoutedEventArgs e)
        {
            var tree = (TreeView)sender;
            _treeView = tree;
            if (tree.RootNodes.Count == 0 && _cachedDirs != null)
                PopulateTree();
        }

        /// <summary>用缓存数据填充当前实化的树(TreeViewNode 是 DependencyObject,须在 UI 线程创建)</summary>
        private void PopulateTree()
        {
            var tree = _treeView;
            if (tree == null || _cachedDirs == null) return;

            tree.RootNodes.Clear();

            var rootNode = new TreeViewNode
            {
                Content = new FileItem { Name = _cachedRootName ?? "", ItemType = FileItemType.Folder },
                IsExpanded = true
            };
            foreach (var dir in _cachedDirs)
                rootNode.Children.Add(new TreeViewNode
                {
                    Content = new FileItem { Name = dir, ItemType = FileItemType.Folder },
                    HasUnrealizedChildren = true
                });
            foreach (var f in _cachedFiles!)
                rootNode.Children.Add(new TreeViewNode
                {
                    Content = new FileItem { Name = f.Name, ItemType = f.Type, Size = f.Size }
                });
            if (_cachedDenied)
                rootNode.Children.Add(new TreeViewNode
                {
                    Content = new FileItem { Name = "(访问被拒绝)", ItemType = FileItemType.Other }
                });
            tree.RootNodes.Add(rootNode);
        }

        /// <summary>后台线程执行:纯磁盘扫描,不创建任何 UI 对象</summary>
        private static (List<string> Dirs, List<(string Name, FileItemType Type, long Size)> Files) ScanDirectory(string directoryPath)
        {
            var dirs = new List<string>();
            var files = new List<(string, FileItemType, long)>();

            foreach (var subDir in Directory.EnumerateDirectories(directoryPath))
                dirs.Add(Path.GetFileName(subDir));

            foreach (var file in Directory.EnumerateFiles(directoryPath))
            {
                var fileInfo = new FileInfo(file);
                files.Add((fileInfo.Name, ExtToType(fileInfo.Extension), fileInfo.Length));
            }

            return (dirs, files);
        }

        private static FileItemType ExtToType(string ext)
        {
            return ext.ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => FileItemType.Image,
                ".mp4" or ".webm" or ".avi" or ".mov" or ".mkv" => FileItemType.Video,
                ".json" or ".txt" or ".xml" or ".html" or ".htm" or ".css" or ".js" or ".md" => FileItemType.Document,
                _ => FileItemType.Other
            };
        }

        private async void FileStructureTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
        {
            var node = args.Node;

            // 查找对应的文件夹路径
            if (node.Content is FileItem fileItem && fileItem.ItemType == FileItemType.Folder && node.Parent != null)
            {
                // 仅首次展开时加载:创建时 HasUnrealizedChildren=true,填充后置 false——
                // 折叠再展开直接跳过,否则子项会重复添加(3→6,每次翻倍)
                if (!node.HasUnrealizedChildren) return;

                var path = GetNodePath(node);
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

                // 先置 false 防异步枚举期间重复触发;失败后不再重试(与原行为一致)
                node.HasUnrealizedChildren = false;

                List<string> dirs;
                List<(string Name, FileItemType Type, long Size)> files;
                bool denied = false;
                try
                {
                    (dirs, files) = await Task.Run(() => ScanDirectory(path));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, $"填充 TreeView 节点时异常: {path}");
                    dirs = new List<string>();
                    files = new List<(string, FileItemType, long)>();
                    denied = true;
                }

                foreach (var dir in dirs)
                    node.Children.Add(new TreeViewNode
                    {
                        Content = new FileItem { Name = dir, ItemType = FileItemType.Folder },
                        HasUnrealizedChildren = true
                    });
                foreach (var f in files)
                    node.Children.Add(new TreeViewNode
                    {
                        Content = new FileItem { Name = f.Name, ItemType = f.Type, Size = f.Size }
                    });
                if (denied)
                    node.Children.Add(new TreeViewNode
                    {
                        Content = new FileItem { Name = "(访问被拒绝)", ItemType = FileItemType.Other }
                    });
            }
        }

        private string? GetNodePath(TreeViewNode node)
        {
            var segments = new List<string>();
            var current = node;

            // 从叶子节点向上收集路径段
            while (current != null)
            {
                if (current.Content is FileItem fi && !string.IsNullOrEmpty(fi.Name))
                {
                    segments.Insert(0, fi.Name);
                }
                current = current.Parent;
            }

            if (segments.Count == 0) return null;

            // 根节点 = 壁纸文件夹名，需要找到对应的完整路径
            var root = segments[0];
            var basePath = Selected?.FolderPath;
            if (string.IsNullOrEmpty(basePath) || Path.GetFileName(basePath) != root)
                return null;

            var relative = segments.Count > 1
                ? string.Join(Path.DirectorySeparatorChar.ToString(), segments.Skip(1))
                : "";
            return Path.Combine(basePath, relative);
        }

        private void FileStructureTree_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            // 找到双击的 TreeViewItem
            var source = e.OriginalSource as DependencyObject;
            while (source != null && source is not TreeViewItem)
                source = VisualTreeHelper.GetParent(source);
            if (source is not TreeViewItem treeViewItem) return;

            var node = ((TreeView)sender).NodeFromContainer(treeViewItem);
            if (node?.Content is not FileItem fileItem || fileItem.ItemType == FileItemType.Folder)
                return;

            // 构造完整文件路径并打开
            var fullPath = GetNodePath(node);
            if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, $"打开文件失败: {fullPath}");
                }
            }
        }
    }

    /// <summary>文件属性页虚拟化行类型</summary>
    public enum FileInfoRowKind { Preview, Title, Section, Info, Divider, Tree }

    /// <summary>
    /// 文件属性页虚拟化行:单模板 + Kind 可见性切换(同 PropertyRowTemplate 模式);
    /// 标签/值在 BuildFileInfoRows 时预计算(窗口快照语义)。
    /// </summary>
    public sealed class FileInfoRow
    {
        public FileInfoRowKind Kind { get; }
        public string? Label { get; }
        public string? SectionTitle { get; }
        public object? Value { get; }
        public TextWrapping ValueWrap { get; }

        private FileInfoRow(FileInfoRowKind kind, string? label = null, string? sectionTitle = null, object? value = null, TextWrapping wrap = TextWrapping.NoWrap)
        {
            Kind = kind;
            Label = label;
            SectionTitle = sectionTitle;
            Value = value;
            ValueWrap = wrap;
        }

        public static FileInfoRow Preview(object? imageSource) => new(FileInfoRowKind.Preview, value: imageSource);
        public static FileInfoRow Title(string? text) => new(FileInfoRowKind.Title, value: text);
        public static FileInfoRow Section(string title) => new(FileInfoRowKind.Section, sectionTitle: title);
        public static FileInfoRow Info(string label, string? value, bool wrap = false)
            => new(FileInfoRowKind.Info, label: label, value: value, wrap: wrap ? TextWrapping.Wrap : TextWrapping.NoWrap);
        public static FileInfoRow Divider() => new(FileInfoRowKind.Divider);
        public static FileInfoRow Tree() => new(FileInfoRowKind.Tree);

        public Visibility PreviewVisibility => Kind == FileInfoRowKind.Preview ? Visibility.Visible : Visibility.Collapsed;
        public Visibility TitleVisibility => Kind == FileInfoRowKind.Title ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SectionVisibility => Kind == FileInfoRowKind.Section ? Visibility.Visible : Visibility.Collapsed;
        public Visibility InfoVisibility => Kind == FileInfoRowKind.Info ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DividerVisibility => Kind == FileInfoRowKind.Divider ? Visibility.Visible : Visibility.Collapsed;
        public Visibility TreeVisibility => Kind == FileInfoRowKind.Tree ? Visibility.Visible : Visibility.Collapsed;
    }
}

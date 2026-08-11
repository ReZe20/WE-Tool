using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Microsoft.Windows.ApplicationModel.Resources;
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
            // GetString 找不到键时返回空字符串(不抛异常),空值兜底,保证任务栏/Alt+Tab 显示"属性"而非组件名
            string title = "属性";
            try
            {
                var t = new ResourceLoader().GetString("PropertiesPanel_Header.Text");
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

        public static void Open(WallpaperItem wallpaper)
        {
            // 推迟一帧创建窗口:窗口构造(XAML 解析/可视树构建/绑定首次求值)是同步重活,
            // 直接执行会短暂卡住主窗口;Low 优先级让本次点击先完成、UI 空闲后再建窗口。
            // 前提:Open 从主窗口 UI 线程调用(GetForCurrentThread 取到主窗口队列)
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                var window = new PropertiesWindow();
                _openWindows.Add(window);
                window.SetWallpaper(wallpaper);
                window.Activate();
            });
        }

        private void SetWallpaper(WallpaperItem wallpaper)
        {
            Selected = wallpaper;
            ContentRoot.DataContext = wallpaper;
            _ = RefreshFileTreeAsync();
            _ = LoadPropertiesAsync(wallpaper);
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

            // 无链接无图片:纯文本,直接 Run
            if (!hasLink && !hasImage)
            {
                paragraph.Inlines.Add(new Run { Text = prop.DisplayText });
                rtb.Blocks.Add(paragraph);
                return;
            }

            // 有链接/图片:分段渲染;空白文本段跳过(img 剥标签残留的换行/占位不显示)
            foreach (var (text, url) in segments)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (url == null)
                {
                    paragraph.Inlines.Add(new Run { Text = text });
                }
                else
                {
                    var container = new InlineUIContainer();
                    container.Child = new HyperlinkButton
                    {
                        NavigateUri = new Uri(url),
                        Style = (Style)Application.Current.Resources["ExternalLinkButtonStyle"],
                        Padding = new Thickness(0), // 内联:去掉 ButtonPadding,避免文字偏移
                        Content = new TextBlock
                        {
                            Text = text,
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = rtb.FontSize,
                            FontWeight = rtb.FontWeight
                        }
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

        private async Task RefreshFileTreeAsync()
        {
            int version = ++_treeLoadVersion;
            FileStructureTree.RootNodes.Clear();

            var wallpaper = Selected;
            if (wallpaper == null || string.IsNullOrEmpty(wallpaper.FolderPath) || !Directory.Exists(wallpaper.FolderPath))
                return;

            var folderPath = wallpaper.FolderPath;
            var rootName = Path.GetFileName(folderPath);

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

            // TreeViewNode 是 DependencyObject,必须在 UI 线程创建
            var rootNode = new TreeViewNode
            {
                Content = new FileItem { Name = rootName, ItemType = FileItemType.Folder },
                IsExpanded = true
            };
            foreach (var dir in dirs)
                rootNode.Children.Add(new TreeViewNode
                {
                    Content = new FileItem { Name = dir, ItemType = FileItemType.Folder },
                    HasUnrealizedChildren = true
                });
            foreach (var f in files)
                rootNode.Children.Add(new TreeViewNode
                {
                    Content = new FileItem { Name = f.Name, ItemType = f.Type, Size = f.Size }
                });
            if (denied)
                rootNode.Children.Add(new TreeViewNode
                {
                    Content = new FileItem { Name = "(访问被拒绝)", ItemType = FileItemType.Other }
                });
            FileStructureTree.RootNodes.Add(rootNode);
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

            var node = FileStructureTree.NodeFromContainer(treeViewItem);
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
}

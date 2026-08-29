using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
using System.Threading;
using System.Threading.Tasks;
using WE_Tool.Helper;
using WE_Tool.Converters;
using WE_Tool.Models;
using WE_Tool.Service;
using WE_Tool.ViewModels;
using Windows.Graphics;
using Windows.UI;
using Microsoft.UI.Text;
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
            // 壁纸属性页行:代码构建(LoadPropertiesAsync 增量填充 PropertyItemsHost.Children),
            // 不用 DataTemplate/ItemsRepeater——NativeAOT 下 x:Bind 对二级模板/Visibility 枚举绑定失效。
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
            // 恢复上次尺寸(开关开启且已记录过);否则用默认 560×720。
            // 尺寸在构造时应用,避免打开后闪一下再跳(与主窗口恢复逻辑解耦,只存大小不存位置)。
            // 尺寸字段只在配置模型(主窗口 WindowX/Y 同模式),VM 仅暴露开关,此处直接读模型。
            int w = 560, h = 720;
            if (ViewModel.AppSettingsVM.RestorePropertiesWindowSize)
            {
                try
                {
                    var boot = new ConfigService().LoadAsync().GetAwaiter().GetResult();
                    if (boot.PropertiesWindowWidth > 0 && boot.PropertiesWindowHeight > 0)
                    {
                        w = boot.PropertiesWindowWidth;
                        h = boot.PropertiesWindowHeight;
                    }
                }
                catch { /* 读取失败用默认尺寸 */ }
            }
            AppWindow.Resize(new SizeInt32(w, h));
            ApplyTheme();

            // 设置页切换主题时跟随
            ViewModel.AppSettingsVM.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AppSettingsViewModel.Theme))
                    ApplyTheme();
            };

            // 尺寸防抖保存:窗口尺寸变化(手动拖拽/最大化)后 500ms 落盘,只存最后一次(照抄 MainWindow 模式)
            AppWindow.Changed += OnAppWindowChanged;

            Closed += (s, e) =>
            {
                _openWindows.Remove(this);
                AppWindow.Changed -= OnAppWindowChanged;
                SavePropertiesWindowSize(); // 关闭时兜底保存一次(防抖可能未触发)
            };
        }

        /// <summary>打开时快照的壁纸(与主窗口选中分离,不跟随主窗口切换)</summary>
        public WallpaperItem? Selected { get; private set; }

        private CancellationTokenSource? _sizeSaveCts;

        /// <summary>窗口尺寸变化(拖拽/最大化)防抖 500ms 后保存;只记录最后一次。</summary>
        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (!args.DidSizeChange) return;

            _sizeSaveCts?.Cancel();
            _sizeSaveCts = new CancellationTokenSource();
            var token = _sizeSaveCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (token.IsCancellationRequested) return;
                try
                {
                    var settings = await new ConfigService().LoadAsync();
                    settings.PropertiesWindowWidth = sender.Size.Width;
                    settings.PropertiesWindowHeight = sender.Size.Height;
                    await new ConfigService().SaveAsync(settings);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "保存属性窗口尺寸失败");
                }
            });
        }

        /// <summary>关闭时兜底保存一次当前尺寸(防抖可能未触发,如快速关闭)。</summary>
        private void SavePropertiesWindowSize()
        {
            try
            {
                var size = AppWindow.Size;
                if (size.Width <= 0 || size.Height <= 0) return;
                var settings = new ConfigService().LoadAsync().GetAwaiter().GetResult();
                settings.PropertiesWindowWidth = size.Width;
                settings.PropertiesWindowHeight = size.Height;
                new ConfigService().SaveAsync(settings).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存属性窗口尺寸失败(关闭时)");
            }
        }

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

                // 增量填充防卡顿:大壁纸(200+ 属性)每批构建+添加后等一帧,创建+布局渐进分摊,UI 不冻结
                // (代码构建行,非 DataTemplate——AOT 下 x:Bind 复杂绑定不可靠)
                Properties.Clear();
                PropertyItemsHost.Children.Clear();
                foreach (var chunk in props.Chunk(10))
                {
                    if (version != _propertyLoadVersion) return;
                    foreach (var p in chunk)
                    {
                        Properties.Add(p);
                        PropertyItemsHost.Children.Add(BuildPropertyRow(p));
                    }
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

        // ========== 壁纸属性页:代码构建行(NativeAOT 下 x:Bind 对 Visibility 枚举/二级 DataTemplate/
        //   ItemTemplateSelector 的绑定全部失效,整行代码构建彻底绕开——UI 线程调用,不依赖绑定) ==========

        /// <summary>构建文字内容:纯文本 → TextBlock;含链接 → StackPanel + HyperlinkButton(可点击跳转);
        /// 含 &lt;font color&gt; 应用文字色;含 &lt;img&gt; 渲染 HTTP 图片(外层 &lt;a href&gt; 时整图可点击)。
        /// 图片段不渲染其文本;加载失败隐藏。</summary>
        private static FrameworkElement BuildTextContent(WallpaperProperty prop)
        {
            bool hasLink = prop.LinkSegments.Any(s => s.Url != null);
            bool hasImage = prop.ImageSegments.Count > 0;
            Brush? textBrush = prop.TextColor is Color c ? new SolidColorBrush(c) : null;

            // 无链接无图片:单 TextBlock(带颜色)
            if (!hasLink && !hasImage)
            {
                var tb = new TextBlock
                {
                    Text = prop.DisplayText,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = prop.TextFontSize,
                    FontWeight = prop.TextFontWeight,
                    TextAlignment = prop.TextAlignmentValue
                };
                if (textBrush != null) tb.Foreground = textBrush;
                return tb;
            }

            var sp = new StackPanel { Spacing = 2 };

            // 文字段(链接 → HyperlinkButton;普通 → TextBlock;均应用文字色)
            foreach (var (text, url) in prop.LinkSegments)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (url == null)
                {
                    var tb = new TextBlock
                    {
                        Text = text,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = prop.TextFontSize,
                        FontWeight = prop.TextFontWeight
                    };
                    if (textBrush != null) tb.Foreground = textBrush;
                    sp.Children.Add(tb);
                }
                else
                {
                    try
                    {
                        var hb = new HyperlinkButton
                        {
                            Content = text,
                            NavigateUri = new Uri(url),
                            Style = (Style)Application.Current.Resources["ExternalLinkButtonStyle"]
                        };
                        if (textBrush != null) hb.Foreground = textBrush;
                        sp.Children.Add(hb);
                    }
                    catch
                    {
                        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
                        if (textBrush != null) tb.Foreground = textBrush;
                        sp.Children.Add(tb);
                    }
                }
            }

            // 图片段(<img src>,HTTP 加载;外层 <a href> 时整图可点击;加载失败隐藏)
            foreach (var (src, link, width, height) in prop.ImageSegments)
            {
                try
                {
                    var image = new Microsoft.UI.Xaml.Controls.Image
                    {
                        Source = new BitmapImage(new Uri(src)),
                        Stretch = Stretch.Uniform,
                        MaxWidth = 200,
                        MaxHeight = 200,
                        Margin = new Thickness(0, 4, 0, 4)
                    };
                    if (width.HasValue) image.Width = Math.Min(width.Value, 200);
                    if (height.HasValue) image.Height = Math.Min(height.Value, 200);
                    image.ImageFailed += (s, e) => image.Visibility = Visibility.Collapsed;

                    if (link != null)
                    {
                        try
                        {
                            var hb = new HyperlinkButton
                            {
                                NavigateUri = new Uri(link),
                                Style = (Style)Application.Current.Resources["ExternalLinkButtonStyle"],
                                Content = image
                            };
                            sp.Children.Add(hb);
                        }
                        catch { sp.Children.Add(image); }
                    }
                    else
                    {
                        sp.Children.Add(image);
                    }
                }
                catch { /* 无效图片 URL → 跳过 */ }
            }
            return sp;
        }

        /// <summary>构建单个属性行(Grid 两列:左标签 + 右控件);分组标题/Expander 由 BuildPropertyRow 分发。</summary>
        private FrameworkElement BuildEditableRow(WallpaperProperty prop)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4), ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 左列:标签(文字/链接)
            var label = BuildTextContent(prop);
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            // 右列:按类型构建编辑控件
            var editor = BuildEditor(prop);
            if (editor != null)
            {
                Grid.SetColumn(editor, 1);
                grid.Children.Add(editor);
            }
            return grid;
        }

        /// <summary>按类型构建右列编辑控件;只读类型返回 null(无控件)。</summary>
        private FrameworkElement? BuildEditor(WallpaperProperty prop)
        {
            switch (prop.Type)
            {
                case "bool":
                {
                    var cb = new CheckBox
                    {
                        IsChecked = prop.BoolValue,
                        MinWidth = 0,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    cb.Checked += (s, e) => prop.BoolValue = cb.IsChecked == true;
                    cb.Unchecked += (s, e) => prop.BoolValue = false;
                    return cb;
                }
                case "slider":
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                    var slider = new Slider
                    {
                        Value = prop.SliderValue,
                        Minimum = prop.SliderMin,
                        Maximum = prop.SliderMax,
                        StepFrequency = prop.SliderStep,
                        Width = 100,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var valueText = new TextBlock
                    {
                        Text = prop.SliderValueText,
                        MinWidth = 40,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    slider.ValueChanged += (s, e) =>
                    {
                        prop.SliderValue = slider.Value;
                        valueText.Text = prop.SliderValueText;
                    };
                    sp.Children.Add(slider);
                    sp.Children.Add(valueText);
                    return sp;
                }
                case "combo":
                {
                    var button = new DropDownButton
                    {
                        MinWidth = 140,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var display = new TextBlock { Text = prop.ComboDisplayText };
                    button.Content = display;
                    button.Click += (s, e) => ShowComboMenu(button, prop, display);
                    return button;
                }
                case "color":
                {
                    var button = new Button { Padding = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Center };
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    var swatch = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 14, Height = 14, Fill = prop.ColorBrush, VerticalAlignment = VerticalAlignment.Center };
                    var hex = new TextBlock { Text = prop.ColorHexText, VerticalAlignment = VerticalAlignment.Center };
                    sp.Children.Add(swatch);
                    sp.Children.Add(hex);
                    button.Content = sp;
                    var picker = new ColorPicker
                    {
                        Color = prop.ColorValue,
                        IsAlphaEnabled = false,
                        IsColorPreviewVisible = false,
                        IsColorSpectrumVisible = true,
                        IsColorSliderVisible = true,
                        IsHexInputVisible = true
                    };
                    var flyout = new Flyout { Content = picker };
                    flyout.Opened += FlyoutThemeRefresh_Opened;
                    picker.ColorChanged += (s, e) =>
                    {
                        prop.ColorValue = picker.Color;
                        swatch.Fill = prop.ColorBrush;
                        hex.Text = prop.ColorHexText;
                    };
                    button.Flyout = flyout;
                    return button;
                }
                case "textinput":
                {
                    var tb = new TextBox
                    {
                        Text = prop.TextValue,
                        TextWrapping = TextWrapping.Wrap,
                        Width = 180,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    tb.TextChanged += (s, e) => prop.TextValue = tb.Text;
                    return tb;
                }
                case "scenetexture":
                {
                    var button = new Button
                    {
                        Command = prop.PickFileCommand,
                        Content = prop.FilePathDisplay,
                        MaxWidth = 180,
                        Padding = new Thickness(10, 4, 10, 4),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    return button;
                }
                default:
                    return null; // 只读类型:无编辑控件(值已在标签区? 原 XAML ReadOnly 分支显示 DisplayValue)
            }
        }

        /// <summary>combo 下拉:DropDownButton + 动态 MenuFlyout(复用原 ComboButton_Click 逻辑;弹层主题显式应用)</summary>
        private void ShowComboMenu(DropDownButton button, WallpaperProperty prop, TextBlock display)
        {
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
                item.Click += (s, e2) =>
                {
                    prop.ComboIndex = index;
                    display.Text = prop.ComboDisplayText;
                };
                flyout.Items.Add(item);
            }
            flyout.Opened += App.ApplyFlyoutTheme;
            button.Flyout = flyout;
            flyout.ShowAt(button);
        }

        /// <summary>构建分组标题(分隔线 + 粗体文字)。分组标题是纯文本组件(无链接),直接 TextBlock。</summary>
        private FrameworkElement BuildGroupHeader(WallpaperProperty prop)
        {
            var sp = new StackPanel();
            sp.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Height = 1,
                Fill = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                Margin = new Thickness(0, 6, 0, 10)
            });
            sp.Children.Add(new TextBlock
            {
                Text = prop.DisplayText,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = prop.TextAlignmentValue
            });
            return sp;
        }

        /// <summary>构建分组 Expander(header = 文字/链接;内容 = 子属性行递归构建)。</summary>
        private FrameworkElement BuildGroupExpander(WallpaperProperty prop)
        {
            var expander = new Expander
            {
                IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 4, 0, 4),
                Header = BuildTextContent(prop)
            };
            var items = new ItemsControl { IsTabStop = false };
            foreach (var child in prop.Children)
                items.Items.Add(BuildPropertyRow(child));
            expander.Content = items;
            return expander;
        }

        /// <summary>构建单行:按类型分发(分组标题/Expander/可编辑行)。</summary>
        private FrameworkElement BuildPropertyRow(WallpaperProperty prop)
        {
            if (prop.IsGroupHeader) return BuildGroupHeader(prop);
            if (prop.IsGroup) return BuildGroupExpander(prop);
            return BuildEditableRow(prop);
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            bool isProps = (args.SelectedItem as NavigationViewItem)?.Tag as string == "props";
            // 平移动画:切入页从右滑入,切出页向左滑出(方向随切换双向)
            AnimatePageSwitch(isProps);
        }

        /// <summary>文件属性页 ↔ 壁纸属性页切换动画:iOS push 风格,方向感知——两页一左一右,
        /// 向右切(文件→壁纸):壁纸从右滑入、文件被推向左侧;向左切(壁纸→文件):文件从左滑入、壁纸被推回右侧。
        /// 同一个 Storyboard 同步驱动(两页同时动,位移一致,形成"推动"感)。切到的页 ZIndex 置顶。</summary>
        private void AnimatePageSwitch(bool toProps)
        {
            var incoming = toProps ? (FrameworkElement)WallpaperPropsRoot : ContentRoot;
            var outgoing = toProps ? (FrameworkElement)ContentRoot : WallpaperPropsRoot;

            // 旧页不可见(首次/异常)则直接切,无动画
            if (outgoing.Visibility != Visibility.Visible)
            {
                incoming.Visibility = Visibility.Visible;
                outgoing.Visibility = Visibility.Collapsed;
                incoming.RenderTransform = null;
                outgoing.RenderTransform = null;
                return;
            }

            // 平移距离 = 页面宽度(push 感);窗口未布局时兜底 300
            double w = RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 300;
            // 方向:向右切(toProps)新页从 +w 滑入、旧页滑到 -w;向左切反向
            double dir = toProps ? 1 : -1;
            var duration = new Duration(TimeSpan.FromMilliseconds(220));

            // 新页置顶,从对应侧整宽滑入
            Canvas.SetZIndex(incoming, 1);
            Canvas.SetZIndex(outgoing, 0);
            incoming.Visibility = Visibility.Visible;
            incoming.RenderTransform = new TranslateTransform { X = dir * w };
            outgoing.RenderTransform = new TranslateTransform { X = 0 };

            var sb = new Storyboard();

            var inAnim = new DoubleAnimation
            {
                From = dir * w,
                To = 0,
                Duration = duration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(inAnim, incoming);
            Storyboard.SetTargetProperty(inAnim, "(UIElement.RenderTransform).(TranslateTransform.X)");
            sb.Children.Add(inAnim);

            // 旧页同速反向滑出,动画结束收起并复位
            var outAnim = new DoubleAnimation
            {
                From = 0,
                To = -dir * w,
                Duration = duration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(outAnim, outgoing);
            Storyboard.SetTargetProperty(outAnim, "(UIElement.RenderTransform).(TranslateTransform.X)");
            sb.Children.Add(outAnim);

            sb.Completed += (s, e) =>
            {
                outgoing.Visibility = Visibility.Collapsed;
                incoming.RenderTransform = null;
                outgoing.RenderTransform = null;
                Canvas.SetZIndex(incoming, 0);
                Canvas.SetZIndex(outgoing, 0);
            };
            sb.Begin();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        // 弹层(菜单/Flyout)不自动继承窗口运行时主题,打开时显式应用(公共逻辑见 App.ApplyFlyoutTheme)
        private void FlyoutThemeRefresh_Opened(object sender, object e) => App.ApplyFlyoutTheme(sender, e);

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

        /// <summary>预览图源(仅 Preview 行有值;x:Bind 需要强类型,Value 是 object 不能直接绑 ImageSource)</summary>
        public Microsoft.UI.Xaml.Media.ImageSource? PreviewImageSource { get; }

        private FileInfoRow(FileInfoRowKind kind, string? label = null, string? sectionTitle = null, object? value = null, TextWrapping wrap = TextWrapping.NoWrap, Microsoft.UI.Xaml.Media.ImageSource? previewSource = null)
        {
            Kind = kind;
            Label = label;
            SectionTitle = sectionTitle;
            Value = value;
            ValueWrap = wrap;
            PreviewImageSource = previewSource;
        }

        public static FileInfoRow Preview(object? imageSource) => new(
            FileInfoRowKind.Preview,
            value: imageSource,
            previewSource: imageSource is string path && !string.IsNullOrEmpty(path)
                ? PathToImageSource(path)
                : imageSource as Microsoft.UI.Xaml.Media.ImageSource);
        public static FileInfoRow Title(string? text) => new(FileInfoRowKind.Title, value: text);
        public static FileInfoRow Section(string title) => new(FileInfoRowKind.Section, sectionTitle: title);
        public static FileInfoRow Info(string label, string? value, bool wrap = false)
            => new(FileInfoRowKind.Info, label: label, value: value, wrap: wrap ? TextWrapping.Wrap : TextWrapping.NoWrap);
        public static FileInfoRow Divider() => new(FileInfoRowKind.Divider);
        public static FileInfoRow Tree() => new(FileInfoRowKind.Tree);

        /// <summary>
        /// 把预览字符串转 ImageSource:ms-appx:/// 占位图 URI 直接建;本地文件路径先确认存在,
        /// 用 file:/// 绝对 URI 建 BitmapImage(x:Bind 需要强类型,且 string 不会隐式转 ImageSource)。
        /// </summary>
        private static Microsoft.UI.Xaml.Media.ImageSource? PathToImageSource(string path)
        {
            try
            {
                if (path.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase))
                    return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path, UriKind.Absolute));
                if (System.IO.File.Exists(path))
                {
                    // C:\... → file:///C:/... (Uri 不认裸盘符路径)
                    string fileUri = "file:///" + path.Replace('\\', '/');
                    return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(fileUri, UriKind.Absolute));
                }
            }
            catch { /* 无效路径/坏图 → null,Image 不显示 */ }
            return null;
        }

        public Visibility PreviewVisibility => Kind == FileInfoRowKind.Preview ? Visibility.Visible : Visibility.Collapsed;
        public Visibility TitleVisibility => Kind == FileInfoRowKind.Title ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SectionVisibility => Kind == FileInfoRowKind.Section ? Visibility.Visible : Visibility.Collapsed;
        public Visibility InfoVisibility => Kind == FileInfoRowKind.Info ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DividerVisibility => Kind == FileInfoRowKind.Divider ? Visibility.Visible : Visibility.Collapsed;
        public Visibility TreeVisibility => Kind == FileInfoRowKind.Tree ? Visibility.Visible : Visibility.Collapsed;
    }
}

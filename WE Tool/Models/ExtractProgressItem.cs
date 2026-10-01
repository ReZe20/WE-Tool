using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WE_Tool.Models;

public partial class ExtractProgressItem : INotifyPropertyChanged
{
    private string _name = "";
    private string _action = "";
    private double _progress;
    private string? _preview;
    private string? _contentRating;
    private ImageSource? _blurSource;

    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public string Action { get => _action; set { _action = value; OnPropertyChanged(); } }

    /// <summary>
    /// 壁纸内条目进度(0-100)。0.5% 阈值防抖:并发提取时进度高频到达,
    /// 值变化过小时不通知 UI,避免进度条微跳刷屏。
    /// </summary>
    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(_progress - value) < 0.5) return;
            _progress = value;
            OnPropertyChanged();
        }
    }

    public string? Preview { get => _preview; set { _preview = value; OnPropertyChanged(); } }

    /// <summary>这张的分级。提取进度面板那几行缩略图要不要糊,由它和查看菜单那三档一起判。</summary>
    public string? ContentRating { get => _contentRating; set { _contentRating = value; OnPropertyChanged(); } }

    /// <summary>命中模糊时的那张高斯模糊图;结论写在行上,所以 ListView 回收容器不会把 A 行的糊图盖到 B 行。</summary>
    public ImageSource? BlurSource
    {
        get => _blurSource;
        set
        {
            if (ReferenceEquals(_blurSource, value)) return;
            _blurSource = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BlurOverlayVisibility));
            OnPropertyChanged(nameof(RawPreviewVisibility));
        }
    }

    public Visibility BlurOverlayVisibility => _blurSource is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RawPreviewVisibility => _blurSource is null ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

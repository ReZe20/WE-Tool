using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WE_Tool.Models;

public partial class ExtractProgressItem : INotifyPropertyChanged
{
    private string _name = "";
    private string _action = "";
    private double _progress;
    private string? _preview;

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

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WE_Tool.ViewModels;

public sealed class BackupItemViewModel : INotifyPropertyChanged
{
    public string WorkshopId { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>预览图路径;无预览图为占位图 ms-appx:///Assets/NoPreview.png(与 Papers 一致,避免 Uri 转换异常)。</summary>
    public string PreviewPath { get; set; } = "";
    public string SizeText { get; set; } = "";       // "128 MB"
    /// <summary>原始字节数(排序用,不绑定 UI)。</summary>
    public long SizeBytes { get; set; }
    public string BackupTimeText { get; set; } = ""; // "2026-08-18 22:30"
    /// <summary>备份时间(排序用,不绑定 UI);解析失败为 null。</summary>
    public DateTime? BackupTime { get; set; }
    public string FullPath { get; set; } = "";       // .we_backup/<id> 完整路径
    /// <summary>源文件已删除(取消订阅/下架,content/<id> 目录不存在),仅剩备份。</summary>
    public bool IsSourceMissing { get; set; }
    /// <summary>角标可见性(与 Papers 的 Visibility 计算属性模式一致,免转换器)。</summary>
    public Microsoft.UI.Xaml.Visibility SourceMissingVisibility =>
        IsSourceMissing ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    private double _parentWidth = 240;
    /// <summary>卡片宽度(由 GridView SizeChanged 按列数计算并通知刷新)。</summary>
    public double ParentWidth
    {
        get => _parentWidth;
        set
        {
            if (_parentWidth != value)
            {
                _parentWidth = value;
                RaisePropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void RaisePropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

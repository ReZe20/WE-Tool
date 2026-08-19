using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WE_Tool.ViewModels;

public sealed class BackupItemViewModel : INotifyPropertyChanged
{
    public string WorkshopId { get; set; } = "";
    public string Title { get; set; } = "";
    public string PreviewPath { get; set; } = "";   // 预览图路径(相对或绝对)
    public string SizeText { get; set; } = "";       // "128 MB"
    public string BackupTimeText { get; set; } = ""; // "2026-08-18 22:30"
    public string FullPath { get; set; } = "";       // .we_backup/<id> 完整路径
    public double ParentWidth { get; set; } = 240;   // 卡片宽度(由 GridView ItemWidth 驱动)

    public event PropertyChangedEventHandler? PropertyChanged;
    public void RaisePropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

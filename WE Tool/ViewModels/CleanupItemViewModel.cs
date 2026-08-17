using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WE_Tool.ViewModels;

public sealed class CleanupFileItem
{
    public string Name { get; set; } = "";
    public string SizeText { get; set; } = "";
    public long Size { get; set; }
    public string FullPath { get; set; } = "";
}

public sealed class CleanupCardViewModel : INotifyPropertyChanged
{
    public string FolderId { get; set; } = "";      // 壁纸 ID
    public string TypeLabel { get; set; } = "多余";   // 卸载 / 多余
    public string FullPath { get; set; } = "";      // 文件夹完整路径
    public string StatsText { get; set; } = "";     // "N 个残留 · 1.2 GB"
    public bool IsUnloaded { get; set; }            // 已卸载类型(删整个文件夹)
    public bool IsSelected { get; set; }             // 多选状态
    public List<CleanupFileItem> Files { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RaisePropertyChanged([CallerMemberName] string propertyName = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

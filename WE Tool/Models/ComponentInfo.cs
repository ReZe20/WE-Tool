using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WE_Tool.Models;

public partial class ComponentInfo : INotifyPropertyChanged
{
    public string? Title { get; set; }
    public string? FilePath { get; set; }
    public ComponentType ComponentType { get; set; }
    public string? FolderPath { get; set; }
    public string? ParentWallpaperPath { get; set; }
    public string? WorkshopID { get; set; }
    public string? Preview { get; set; }
    public long FileSize { get; set; }
    public DateTime InstallDate { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime? AcfUpdateTime { get; set; }
    public string? ContentRating { get; set; }
    public string? Tags { get; set; }
    public string? Description { get; set; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CheckBoxOpacity));
            }
        }
    }

    private bool _isMultiSelectMode;
    public bool IsInMultiSelectMode
    {
        get => _isMultiSelectMode;
        set
        {
            if (_isMultiSelectMode != value)
            {
                _isMultiSelectMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CheckBoxOpacity));
            }
        }
    }

    private bool _isHovered;
    /// <summary>鼠标是否悬停在该组件上(checkbox 保持显示用)。UI 层 PointerEntered/Exited 设置。</summary>
    public bool IsHovered
    {
        get => _isHovered;
        set
        {
            if (_isHovered != value)
            {
                _isHovered = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CheckBoxOpacity));
            }
        }
    }

    public double CheckBoxOpacity => (IsSelected || IsInMultiSelectMode || IsHovered) ? 1.0 : 0.0;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

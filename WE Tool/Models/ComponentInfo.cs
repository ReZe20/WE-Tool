using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WE_Tool.Models;

public class ComponentInfo : INotifyPropertyChanged
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
    public string? ContentRating { get; set; }
    public string? Tags { get; set; }
    public string? Description { get; set; }

    public bool IsSelected { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WE_Tool.Models;

public class ExtractProgressItem : INotifyPropertyChanged
{
    private string _name = "";
    private string _action = "";
    private double _progress;

    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public string Action { get => _action; set { _action = value; OnPropertyChanged(); } }
    public double Progress { get => _progress; set { _progress = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

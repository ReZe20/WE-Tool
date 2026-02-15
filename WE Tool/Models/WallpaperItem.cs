using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace WE_Tool.Models
{
    public class WallpaperItem : INotifyPropertyChanged
    {
        public string Title { get; set; }
        public string FolderPath { get; set; }
        public string Preview { get; set; }
        public string ContentRating { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public List<string> Tags { get; set; }
        public DateTime CreationTime { get; set; }
        public DateTime UpdateTime { get; set; }
        public long FileSize { get; set; }
        public string FileSizeString => $"{FileSize / 1024.0 / 1024.0:F2} MB";

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
                }
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

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
        public string WorkshopID { get; set; }
        public string Title { get; set; }
        public string FolderPath { get; set; }
        public string Preview { get; set; }
        public string ContentRating { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public List<string> Tags { get; set; }
        public string Source { get; set; }
        public string Dependency { get; set; }
        public DateTime CreationTime { get; set; }
        public DateTime UpdateTime { get; set; }
        public long FileSize { get; set; }

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

        public event PropertyChangedEventHandler PropertyChanged;
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

        public double CheckBoxOpacity => (IsSelected || IsInMultiSelectMode) ? 1.0 : 0.0;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

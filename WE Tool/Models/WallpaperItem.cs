using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace WE_Tool.Models
{
    public partial class WallpaperItem : INotifyPropertyChanged
    {
        public string? WorkshopID { get; set; }
        public string? Title { get; set; }
        public string? FolderPath { get; set; }
        public string? Preview { get; set; }
        public string? ContentRating { get; set; }
        public string? Type { get; set; }
        public string? Description { get; set; }
        public string? Tags { get; set; }
        public string? Source { get; set; }
        public string? Dependency { get; set; }
        public DateTime CreationTime { get; set; }
        public DateTime UpdateTime { get; set; }
        public DateTime? AcfUpdateTime { get; set; }
        public long FileSize { get; set; }
        public long? AcfSize { get; set; }

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

        public event PropertyChangedEventHandler? PropertyChanged;
        
        private bool _shouldNotExist;
        public bool ShouldNotExist
        {
            get => _shouldNotExist;
            set
            {
                if (_shouldNotExist != value)
                {
                    _shouldNotExist = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isDelisted;
        /// <summary>壁纸已被下架(project.json visibility == "private")。下架壁纸归入"异常"订阅状态。</summary>
        public bool IsDelisted
        {
            get => _isDelisted;
            set
            {
                if (_isDelisted != value)
                {
                    _isDelisted = value;
                    OnPropertyChanged();
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
        /// <summary>鼠标是否悬停在该壁纸上(checkbox 保持显示用)。UI 层 PointerEntered/Exited 设置。</summary>
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

        public bool IsTypeScene => string.Equals(Type, "scene", StringComparison.OrdinalIgnoreCase);
        public bool IsSourceMine => string.Equals(Source, "mine", StringComparison.OrdinalIgnoreCase);

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
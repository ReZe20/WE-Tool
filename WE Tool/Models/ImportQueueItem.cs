using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace WE_Tool.Models
{
    /// <summary>
    /// 导入壁纸页的队列项：一个待导入的 .pkg/.mpkg 壁纸包文件。
    /// 当前为 UI 骨架阶段：Status 停在"等待导入"，导入执行逻辑接入后驱动 Progress/Status。
    /// </summary>
    public partial class ImportQueueItem : INotifyPropertyChanged
    {
        public string FilePath { get; set; } = "";

        public string FileName => Path.GetFileName(FilePath);

        /// <summary>文件后缀名(含点,如 .pkg / .mpkg)。</summary>
        public string Extension => Path.GetExtension(FilePath);

        /// <summary>提取目标目录(开始提取时确定,用于完成后补写 project.json)。</summary>
        public string OutputPath { get; set; } = "";

        private string _status = "";
        public string Status
        {
            get => _status;
            set { if (_status != value) { _status = value; OnPropertyChanged(); } }
        }

        private double _progress;
        /// <summary>
        /// 壁纸内条目进度(0-100)。0.5% 阈值防抖(Papers 提取面板同款):
        /// batch 进度事件高频到达(每壁纸 30ms 节流),变化过小时不通知 UI,避免进度条微跳刷屏。
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

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

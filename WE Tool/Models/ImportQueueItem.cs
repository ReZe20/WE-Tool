using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace WE_Tool.Models
{
    /// <summary>
    /// 导入壁纸页的队列项：一个待导入的 .pkg/.mpkg 壁纸包文件。
    /// 提取执行逻辑驱动 Status/Progress/IsError;成功项在收到完成事件时整行清出队列,
    /// 只有失败与停止的项会留在列表里等用户处理。
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

        private bool _isError;
        /// <summary>失败项(提取失败/准备失败):驱动进度条的 Error 视觉态。"已停止"是用户主动停的,不算失败。</summary>
        public bool IsError
        {
            get => _isError;
            set { if (_isError != value) { _isError = value; OnPropertyChanged(); } }
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

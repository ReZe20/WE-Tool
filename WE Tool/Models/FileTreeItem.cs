using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WE_Tool.Models
{
    public enum FileItemType { Folder, Image, Video, Document, Other }

    public class FileItem
    {
        public string Name { get; set; } = "";
        public FileItemType ItemType { get; set; }
        public long? Size { get; set; }
        public string SizeText => Size.HasValue ? FormatFileSize(Size.Value) : "";
        public string DisplayText => Size.HasValue ? $"{Name}    ({SizeText})" : Name;

        private static string FormatFileSize(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double size = System.Math.Abs((double)bytes);
            int unitIndex = 0;
            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }
            return $"{size:F2} {units[unitIndex]}";
        }
    }

    public class FileTreeTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? FolderTemplate { get; set; }
        public DataTemplate? ImageTemplate { get; set; }
        public DataTemplate? VideoTemplate { get; set; }
        public DataTemplate? DocumentTemplate { get; set; }
        public DataTemplate? OtherTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item)
        {
            if (item is TreeViewNode node && node.Content is FileItem fileItem)
            {
                return fileItem.ItemType switch
                {
                    FileItemType.Folder   => FolderTemplate  ?? OtherTemplate,
                    FileItemType.Image    => ImageTemplate   ?? OtherTemplate,
                    FileItemType.Video    => VideoTemplate   ?? OtherTemplate,
                    FileItemType.Document => DocumentTemplate ?? OtherTemplate,
                    _ => OtherTemplate ?? FolderTemplate
                } ?? new DataTemplate();
            }
            return OtherTemplate ?? new DataTemplate();
        }
    }
}

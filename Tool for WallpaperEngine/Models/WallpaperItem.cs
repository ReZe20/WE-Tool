using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tool_for_WallpaperEngine.Models
{
    internal class WallpaperItem
    {
        public string Title { get; set; }
        public string FolderPath { get; set; }
        public string Preview { get; set; }
        public string ContentRating { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public List<string> Tags { get; set; }
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WE_Tool.Models;

namespace WE_Tool.Converters
{
    partial class PapersTagContentChoose : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            int index = 0;
            if (Application.Current is WE_Tool.App app && app.ViewModel != null)
            {
                try
                {
                    index = app.ViewModel.WallpaperTagDisplayIndex;
                }
                catch
                {
                    index = 0;
                }
            }

            if (value is WallpaperItem model)
            {
                return index switch
                {
                    0 => new TypeToDisplay().Convert(model.Type ?? string.Empty, null, "", "") ?? string.Empty,
                    1 => new RatingToDisplay().Convert(model.ContentRating ?? string.Empty, null, "", "") ?? string.Empty,
                    2 => new SourceToDisplay().Convert(model.Source ?? string.Empty, null, "", "") ?? string.Empty,
                    3 => new TagToDisplay().Convert(model.Tags ?? string.Empty, null, "", "") ?? string.Empty,
                    4 => string.Empty,
                    _ => model.Type ?? string.Empty
                };
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}

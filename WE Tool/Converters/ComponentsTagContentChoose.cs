using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;
using WE_Tool.Helper;
using WE_Tool.Models;

namespace WE_Tool.Converters
{
    partial class ComponentsTagContentChoose : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            int index = 0;
            if (Application.Current is WE_Tool.App app && app.ViewModel != null)
            {
                try
                {
                    index = app.ViewModel.ComponentsDisplayVM.ComponentTagDisplayIndex;
                }
                catch
                {
                    index = 0;
                }
            }

            if (value is ComponentInfo model)
            {
                return index switch
                {
                    0 => model.ComponentType switch
                    {
                        ComponentType.Layer => LanguageHelper.GetString("CompType", "Layers"),
                        ComponentType.Script => LanguageHelper.GetString("CompType", "Scripts"),
                        ComponentType.Effect => LanguageHelper.GetString("CompType", "Effects"),
                        _ => LanguageHelper.GetString("CompType", "Unknown")
                    },
                    1 => new RatingToDisplay().Convert(model.ContentRating ?? string.Empty, null, "", "") ?? string.Empty,
                    2 => LanguageHelper.GetString("Source", "Workshop"),
                    3 => new TagToDisplay().Convert(model.Tags ?? string.Empty, null, "", "") ?? string.Empty,
                    4 => string.Empty,
                    _ => model.ComponentType.ToString()
                };
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}

using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WE_Tool.Helper;

namespace WE_Tool.Converters
{
    internal class RatingToDisplay : IValueConverter
    {
        public object Convert(object value, Type? targetType, object parameter, string language)
        {
            if (value == null) return string.Empty;
            var ratingStr = value as string;
            if (string.IsNullOrWhiteSpace(ratingStr)) return string.Empty;

            try
            {
                return LanguageHelper.GetString("Rating", ratingStr);
            }
            catch
            {
                return ratingStr;
            }
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}

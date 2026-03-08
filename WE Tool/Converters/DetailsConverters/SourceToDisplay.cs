using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WE_Tool.Helper;

namespace WE_Tool.Converters
{
    internal class SourceToDisplay : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value == null) return string.Empty;
            var sourceStr = value as string;
            if (string.IsNullOrWhiteSpace(sourceStr)) return string.Empty;

            try
            {
                return LanguageHelper.GetString("Source", sourceStr);
            }
            catch
            {
                return sourceStr;
            }
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}

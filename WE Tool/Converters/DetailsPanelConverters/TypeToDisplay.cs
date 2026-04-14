using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WE_Tool.Helper;

namespace WE_Tool.Converters
{
    internal class TypeToDisplay : IValueConverter
    {
        public object Convert(object value, Type? targetType, object parameter, string language)
        {
            if (value == null) return string.Empty;
            var typeStr = value as string;
            if (string.IsNullOrWhiteSpace(typeStr)) return string.Empty;

            try
            {
                return LanguageHelper.GetString("Type", typeStr);
            }
            catch
            {
                return typeStr;
            }
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}

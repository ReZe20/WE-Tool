using Microsoft.UI.Xaml.Data;
using System;
using System.Globalization;

namespace WE_Tool.Converters
{
    internal class DateTimeToString : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is DateTime dt)
                return dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

            return "-";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}

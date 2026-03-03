using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace WE_Tool.Converters
{
    public class IntToGridLengthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is int height)
            {
                return new GridLength(height, GridUnitType.Pixel);
            }
            return new GridLength(0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
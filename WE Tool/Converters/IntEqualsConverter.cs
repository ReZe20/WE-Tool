using Microsoft.UI.Xaml.Data;
using System;

namespace WE_Tool.Converters;

public class IntEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is int intValue && parameter is string paramStr && int.TryParse(paramStr, out int target))
            return intValue == target;
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is true && parameter is string paramStr && int.TryParse(paramStr, out int target))
            return target;
        return 0;
    }
}

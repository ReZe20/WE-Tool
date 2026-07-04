using Microsoft.UI.Xaml.Data;
using System;

namespace WE_Tool.Converters;

/// <summary>
/// 将绑定值与 ConverterParameter 比较，相等返回 true。
/// 用于将 RadioButton.IsChecked 绑定到 int 属性。
/// </summary>
public class EqualityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (parameter is string paramStr && int.TryParse(paramStr, out int paramInt))
            return value is int intValue && intValue == paramInt;
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is bool isChecked && isChecked && parameter is string paramStr && int.TryParse(paramStr, out int paramInt))
            return paramInt;
        return 0;
    }
}

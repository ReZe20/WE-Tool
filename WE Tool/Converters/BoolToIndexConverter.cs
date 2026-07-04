using Microsoft.UI.Xaml.Data;
using System;

namespace WE_Tool.Converters;

/// <summary>
/// 将 bool 值转换为 SelectedIndex：true→0, false→1。
/// 用于 RadioButtons.SelectedIndex 绑定到 bool 属性。
/// </summary>
public class BoolToIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b)
            return b ? 0 : 1;
        return 0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is int index)
            return index == 0;
        return true;
    }
}

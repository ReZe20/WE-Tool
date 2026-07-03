using Microsoft.UI.Xaml.Data;
using System;

namespace WE_Tool.Converters
{
    /// <summary>
    /// Converts between an int value and a ComboBox SelectedIndex.
    /// ConverterParameter format: "defaultVal,option2Val,option3Val,..."
    /// Maps: selected index → the value at that index; value → index via linear search.
    /// </summary>
    public class IntToComboIndexConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not int intVal || parameter is not string paramStr)
                return 0;

            var parts = paramStr.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                if (int.TryParse(parts[i], out var v) && v == intVal)
                    return i;
            }
            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is not int index || parameter is not string paramStr)
                return -1;

            var parts = paramStr.Split(',');
            if (index >= 0 && index < parts.Length && int.TryParse(parts[index], out var result))
                return result;
            return -1;
        }
    }
}

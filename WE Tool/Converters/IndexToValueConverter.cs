using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WE_Tool.Converters
{
    /// <summary>
    /// Converts between a ComboBox SelectedIndex and a value using a parameter-specified mapping.
    /// ConverterParameter format: "v0,v1,v2,..." — index i maps to value vi and vice versa.
    /// Example: ConverterParameter="-1,4,2,1" means index 0→-1, 1→4, 2→2, 3→1.
    /// </summary>
    public class IndexToValueConverter : IValueConverter
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

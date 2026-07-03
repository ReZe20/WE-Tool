using Microsoft.UI.Xaml.Data;
using System;

namespace WE_Tool.Converters
{
    /// <summary>
    /// Converts a memory percentage (0-100) to a proportional pixel width
    /// based on the target element's ActualWidth.
    /// Parameter: the margin to subtract from each side (default "12").
    /// </summary>
    public class PercentToFillWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not double percent || parameter is not string paramStr)
                return 0.0;

            double margin = double.TryParse(paramStr, out var m) ? m : 12.0;
            // This converter is used in a MultiBinding context or with a fixed target width.
            // We return the percentage as a factor; code-behind handles the actual pixel calc.
            return percent / 100.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}

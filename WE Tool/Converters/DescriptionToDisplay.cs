using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WE_Tool.Helper;

namespace WE_Tool.Converters
{
    internal class DescriptionToDisplay : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (!string.IsNullOrWhiteSpace((string)value))
            {
                return value;
            }
            else
            {
                return LanguageHelper.GetResource("Nodescription");
            }
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}

using Microsoft.UI.Xaml.Data;
using System;
using WE_Tool.Models;

namespace WE_Tool.Converters
{
    internal class ComponentTypeToDisplay : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value switch
            {
                ComponentType.Layer => "图层",
                ComponentType.Script => "脚本",
                ComponentType.Effect => "特效",
                ComponentType.Unknown => "未知",
                _ => value?.ToString() ?? ""
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}

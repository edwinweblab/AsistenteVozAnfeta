using Microsoft.UI.Xaml.Data;
using System;

namespace Anfeta.UI.Helpers
{
    public class StringNotEmptyConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is string str && !string.IsNullOrEmpty(str);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
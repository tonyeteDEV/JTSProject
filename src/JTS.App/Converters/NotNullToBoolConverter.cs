using Microsoft.UI.Xaml.Data;

namespace JTS_App.Converters;

public sealed class NotNullToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is not null;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

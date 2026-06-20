using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace JTS_App.Converters;

public sealed class RecordingButtonForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true
            ? new SolidColorBrush(Colors.White)
            : new SolidColorBrush(ColorHelper.FromArgb(255, 245, 247, 250));

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

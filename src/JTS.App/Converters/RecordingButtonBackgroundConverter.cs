using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace JTS_App.Converters;

public sealed class RecordingButtonBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true
            ? new SolidColorBrush(ColorHelper.FromArgb(255, 185, 28, 28))
            : new SolidColorBrush(ColorHelper.FromArgb(255, 48, 52, 59));

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace JTS_App.Converters;

public sealed class BoolToSelectionBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Transparent = new(Color.FromArgb(0, 0, 0, 0));
    private static readonly SolidColorBrush Selected = new(ColorHelper.FromArgb(255, 0xF4, 0xF7, 0xFB));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Selected : Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

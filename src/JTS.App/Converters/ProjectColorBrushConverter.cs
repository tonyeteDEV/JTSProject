using JTS_App.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace JTS_App.Converters;

public sealed class ProjectColorBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var hex = ProjectColorService.CardColor(value as string);
        return new SolidColorBrush(ColorHelper.FromArgb(
            255,
            System.Convert.ToByte(hex.Substring(1, 2), 16),
            System.Convert.ToByte(hex.Substring(3, 2), 16),
            System.Convert.ToByte(hex.Substring(5, 2), 16)));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

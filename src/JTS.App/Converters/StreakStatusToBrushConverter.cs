using JTS_App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace JTS_App.Converters;

public sealed class StreakStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Outside = new(Color.FromArgb(0, 0, 0, 0));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var intensity = value switch
        {
            StreakDay { Kind: StreakCellKind.Blank } => -1,
            StreakDay { Kind: StreakCellKind.Future } => 0,
            StreakDay day => day.Intensity,
            double number => number,
            _ => -1
        };

        if (intensity < 0) return Outside;

        var progress = Math.Clamp(intensity, 0, 1);
        var (fromR, fromG, fromB, toR, toG, toB, localProgress) = progress <= 0.5
            ? (0x26, 0x2C, 0x34, 0xD6, 0xA8, 0x21, progress / 0.5)
            : (0xD6, 0xA8, 0x21, 0x2E, 0xA0, 0x43, (progress - 0.5) / 0.5);

        var r = (byte)Math.Round(fromR + (toR - fromR) * localProgress);
        var g = (byte)Math.Round(fromG + (toG - fromG) * localProgress);
        var b = (byte)Math.Round(fromB + (toB - fromB) * localProgress);
        return new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

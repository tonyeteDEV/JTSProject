namespace JTS_App.Services;

public static class ProjectColorService
{
    private static readonly string[] Palette =
    [
        "#254D8F",
        "#2F6B56",
        "#7A4A1E",
        "#7D3446",
        "#5A438B",
        "#286A79",
        "#6B5A24",
        "#75364F",
        "#464E8A",
        "#3F6A38",
        "#70402C",
        "#2D6861"
    ];

    public static string RandomColor() => Palette[Random.Shared.Next(Palette.Length)];

    public static string CardColor(string? colorHex)
    {
        if (!TryParse(colorHex, out var r, out var g, out var b))
            return Palette[0];

        var luminance = RelativeLuminance(r, g, b);
        var factor = luminance > 0.38 ? 0.42 : luminance > 0.22 ? 0.58 : 0.78;
        r = Blend((byte)Math.Clamp((int)Math.Round(r * factor), 0, 255), 34, 0.28);
        g = Blend((byte)Math.Clamp((int)Math.Round(g * factor), 0, 255), 38, 0.28);
        b = Blend((byte)Math.Clamp((int)Math.Round(b * factor), 0, 255), 45, 0.28);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static byte Blend(byte color, byte neutral, double neutralWeight) =>
        (byte)Math.Clamp((int)Math.Round(color * (1 - neutralWeight) + neutral * neutralWeight), 0, 255);

    private static bool TryParse(string? colorHex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var value = colorHex?.Trim().TrimStart('#');
        if (value?.Length != 6) return false;
        return byte.TryParse(value[..2], System.Globalization.NumberStyles.HexNumber, null, out r) &&
               byte.TryParse(value[2..4], System.Globalization.NumberStyles.HexNumber, null, out g) &&
               byte.TryParse(value[4..6], System.Globalization.NumberStyles.HexNumber, null, out b);
    }

    private static double RelativeLuminance(byte r, byte g, byte b)
    {
        static double Channel(byte value)
        {
            var scaled = value / 255d;
            return scaled <= 0.03928 ? scaled / 12.92 : Math.Pow((scaled + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }
}

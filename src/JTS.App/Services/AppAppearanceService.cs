using JTS.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace JTS_App.Services;

public sealed class AppAppearanceService
{
    public IReadOnlyList<string> FontOptions { get; } = new[]
    {
        "Segoe UI Variable Text",
        "Aptos",
        "Inter",
        "Bahnschrift",
        "Cascadia Code",
        "Georgia"
    };

    private readonly AppSettingsService _settings;

    public string CurrentFontFamily { get; private set; } = "Segoe UI Variable Text";
    public double CurrentFontSize { get; private set; } = 13;

    public event EventHandler<string>? FontFamilyChanged;
    public event EventHandler<double>? FontSizeChanged;

    public AppAppearanceService(AppSettingsService settings)
    {
        _settings = settings;
    }

    public async Task LoadAsync()
    {
        var configured = await _settings.GetAppFontFamilyAsync();
        var configuredSize = await _settings.GetAppFontSizeAsync();
        ApplyFontFamily(string.IsNullOrWhiteSpace(configured) ? CurrentFontFamily : configured);
        if (double.TryParse(configuredSize, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var size))
            ApplyFontSize(size);
        else
            ApplyFontSize(CurrentFontSize);
    }

    public async Task SaveFontFamilyAsync(string fontFamily)
    {
        var selected = FontOptions.Contains(fontFamily) ? fontFamily : FontOptions[0];
        await _settings.SetAppFontFamilyAsync(selected);
        ApplyFontFamily(selected);
    }

    public async Task SaveFontSizeAsync(double fontSize)
    {
        var selected = Math.Clamp(Math.Round(fontSize), 11, 17);
        await _settings.SetAppFontSizeAsync(selected);
        ApplyFontSize(selected);
    }

    public void ApplyToFrameworkResources()
    {
        var font = new FontFamily(CurrentFontFamily);
        Application.Current.Resources["ContentControlThemeFontFamily"] = font;
        Application.Current.Resources["TextControlThemeFontFamily"] = font;
        Application.Current.Resources["PivotHeaderItemFontFamily"] = font;
        ApplyFontSizeToFrameworkResources();
    }

    private void ApplyFontSizeToFrameworkResources()
    {
        Application.Current.Resources["ControlContentThemeFontSize"] = CurrentFontSize;
        Application.Current.Resources["BodyTextBlockFontSize"] = CurrentFontSize;
        Application.Current.Resources["BaseTextBlockFontSize"] = CurrentFontSize;
        Application.Current.Resources["SubtitleTextBlockFontSize"] = CurrentFontSize + 5;
        Application.Current.Resources["TitleTextBlockFontSize"] = CurrentFontSize + 14;
    }

    private void ApplyFontFamily(string fontFamily)
    {
        CurrentFontFamily = fontFamily;
        try
        {
            ApplyToFrameworkResources();
        }
        catch
        {
            CurrentFontFamily = "Segoe UI Variable Text";
            ApplyToFrameworkResources();
        }

        FontFamilyChanged?.Invoke(this, CurrentFontFamily);
    }

    private void ApplyFontSize(double fontSize)
    {
        CurrentFontSize = Math.Clamp(Math.Round(fontSize), 11, 17);
        ApplyFontSizeToFrameworkResources();
        FontSizeChanged?.Invoke(this, CurrentFontSize);
    }
}

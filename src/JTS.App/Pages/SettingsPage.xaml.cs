using JTS_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace JTS_App.Pages;

public sealed partial class SettingsPage : Page, IRefreshablePage
{
    private bool _isCompactLayout;
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await ViewModel.LoadAsync();
            FontBox.ItemsSource = ViewModel.FontOptions;
            FontBox.SelectedItem = ViewModel.SelectedFontFamily;
            ApplyColorPreviews();
            ApplySettingsLayout(ActualWidth);
        };
    }

    private void SettingsRoot_SizeChanged(object sender, SizeChangedEventArgs e) => ApplySettingsLayout(e.NewSize.Width);

    public async Task RefreshAsync()
    {
        await ViewModel.LoadAsync();
        FontBox.ItemsSource = ViewModel.FontOptions;
        FontBox.SelectedItem = ViewModel.SelectedFontFamily;
        ApplyColorPreviews();
        ApplySettingsLayout(ActualWidth);
    }

    private void ApplySettingsLayout(double width)
    {
        var compact = width < 860;
        if (compact == _isCompactLayout && width > 0) return;

        _isCompactLayout = compact;
        SettingsRoot.Padding = compact ? new Thickness(18, 16, 18, 18) : new Thickness(28, 22, 28, 28);
        SettingsContent.MaxWidth = compact ? double.PositiveInfinity : 1120;

        ConfigureGrid(WorkspaceGrid, compact, 2);
        ConfigureGrid(AppearanceGrid, compact, 2);
        ConfigureGrid(PreloadColorsGrid, compact, 4);
        ConfigureGrid(AssistantOptionsGrid, compact, 2);
        ConfigureGrid(D365Grid, compact, 4);

        ConfigureButtonPanel(VoiceButtonsPanel, compact);
        ConfigureButtonPanel(DataButtonsPanel, compact);
    }

    private static void ConfigureGrid(Grid grid, bool compact, int childCount)
    {
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();

        if (compact)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < childCount; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (var i = 0; i < grid.Children.Count; i++)
            {
                if (grid.Children[i] is not FrameworkElement child) continue;
                Grid.SetRow(child, i);
                Grid.SetColumn(child, 0);
            }

            return;
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rows = Math.Max(1, (int)Math.Ceiling(childCount / 2d));
        for (var i = 0; i < rows; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < grid.Children.Count; i++)
        {
            if (grid.Children[i] is not FrameworkElement child) continue;
            Grid.SetRow(child, i / 2);
            Grid.SetColumn(child, i % 2);
        }
    }

    private static void ConfigureButtonPanel(StackPanel panel, bool compact)
    {
        panel.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        foreach (var child in panel.Children.OfType<FrameworkElement>())
            child.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (FontBox.SelectedItem is string fontFamily)
        {
            ViewModel.SelectedFontFamily = fontFamily;
        }

        await ViewModel.SaveAsync();
        ApplyColorPreviews();
    }

    private async void PreloadColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;

        var picker = new ColorPicker
        {
            Color = GetPreloadColor(key),
            IsAlphaEnabled = false,
            IsColorChannelTextInputVisible = false,
            IsColorSliderVisible = true,
            IsHexInputVisible = true,
            MinWidth = 360
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{key} color",
            Content = picker,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        SetPreloadColor(key, picker.Color);
        ApplyColorPreviews();
    }

    private Color GetPreloadColor(string key) => key switch
    {
        "Loaded" => ViewModel.PreloadLoadedColor,
        "Loading" => ViewModel.PreloadLoadingColor,
        "Pending" => ViewModel.PreloadPendingColor,
        "Error" => ViewModel.PreloadErrorColor,
        _ => ColorHelper.FromArgb(255, 47, 107, 86)
    };

    private void SetPreloadColor(string key, Color color)
    {
        switch (key)
        {
            case "Loaded":
                ViewModel.PreloadLoadedColor = color;
                break;
            case "Loading":
                ViewModel.PreloadLoadingColor = color;
                break;
            case "Pending":
                ViewModel.PreloadPendingColor = color;
                break;
            case "Error":
                ViewModel.PreloadErrorColor = color;
                break;
        }
    }

    private void ApplyColorPreviews()
    {
        ApplyColorPreview(LoadedColorPreview, LoadedColorText, ViewModel.PreloadLoadedColor);
        ApplyColorPreview(LoadingColorPreview, LoadingColorText, ViewModel.PreloadLoadingColor);
        ApplyColorPreview(PendingColorPreview, PendingColorText, ViewModel.PreloadPendingColor);
        ApplyColorPreview(ErrorColorPreview, ErrorColorText, ViewModel.PreloadErrorColor);
    }

    private static void ApplyColorPreview(Border preview, TextBlock text, Color color)
    {
        preview.Background = new SolidColorBrush(color);
        text.Text = ToHex(color);
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private async void DownloadWhisper_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DownloadWhisperRecommendedModelAsync();
    }

    private async void DownloadVosk_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DownloadVoskRecommendedModelAsync();
    }

    private async void DownloadVoskAccurate_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DownloadVoskAccurateModelAsync();
    }

    private void OpenCustomers_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(CustomersPage));
    }

    private void OpenProjects_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(ProjectsPage));
    }

    private void TestPomodoroBell_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.TogglePomodoroBellTest();
    }

}

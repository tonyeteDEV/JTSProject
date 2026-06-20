using JTS_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using JTS_App.Services;

namespace JTS_App;

public sealed partial class QuickCaptureWindow : Window
{
    public AssistantViewModel ViewModel { get; }

    public QuickCaptureWindow()
    {
        ViewModel = App.Services.GetRequiredService<AssistantViewModel>();
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 700));
        AppWindow.Title = "JTS Quick Capture";
        var appearance = App.Services.GetRequiredService<AppAppearanceService>();
        appearance.ApplyToFrameworkResources();
        appearance.FontFamilyChanged += (_, _) => appearance.ApplyToFrameworkResources();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.IsRecording))
                UpdateVoiceRecordButton();
            if (e.PropertyName == nameof(ViewModel.HasTaskPreview))
            {
                TaskPreviewPanel.Visibility = ViewModel.HasTaskPreview ? Visibility.Visible : Visibility.Collapsed;
                UpdateCreateButtonLabel();
            }
        };
        UpdateVoiceRecordButton();
        TaskPreviewPanel.Visibility = ViewModel.HasTaskPreview ? Visibility.Visible : Visibility.Collapsed;
        UpdateCreateButtonLabel();
        _ = ViewModel.LoadAsync();
    }

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ToggleRecordingAsync();
        UpdateVoiceRecordButton();
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var count = await ViewModel.CreateTasksFromTranscriptAsync();
        UpdateCreateButtonLabel();
        if (count > 0)
        {
            Close();
        }
    }

    private void UpdateVoiceRecordButton()
    {
        var isRecording = ViewModel.IsRecording;
        VoiceRecordButton.Background = new SolidColorBrush(isRecording ? ColorHelper.FromArgb(255, 185, 28, 28) : ColorHelper.FromArgb(255, 48, 52, 59));
        VoiceRecordButton.Foreground = new SolidColorBrush(Colors.White);
        VoiceRecordIcon.Glyph = isRecording ? "\uE71A" : "\uE720";
        VoiceRecordLabel.Text = isRecording ? "Recording" : "Record";
    }

    private void UpdateCreateButtonLabel()
    {
        CreatePreviewLabel.Text = ViewModel.HasTaskPreview ? "Confirm create" : "Preview task";
    }
}

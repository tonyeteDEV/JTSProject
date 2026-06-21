using JTS_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JTS_App.Pages;

public sealed partial class VideoAnalysisPage : Page, IRefreshablePage
{
    public VideoAnalysisViewModel ViewModel { get; }

    public VideoAnalysisPage()
    {
        ViewModel = App.Services.GetRequiredService<VideoAnalysisViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }

    public async Task RefreshAsync() => await ViewModel.LoadAsync(forceSync: true);

    private async void SaveDraft_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveDraftAsync();
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelEdit();
    }

    private async void EditRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id })
            await ViewModel.EditDraftAsync(id);
    }

    private async void ProcessRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;

        var result = await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Process video?",
            Content = "This will transcribe audio, sample frames, run local OCR, generate documentation with DeepSeek, and save the result in Dataverse.",
            PrimaryButtonText = "Process",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        }.ShowAsync();

        if (result == ContentDialogResult.Primary)
            await ViewModel.ProcessDraftAsync(id);
    }

    private async void ViewRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id })
        {
            await ViewModel.LoadAnalysisDetailsAsync(id);
            VideoTabs.SelectedItem = AnalysisDetailTab;
        }
    }

    private async void DeleteRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id }) return;

        var result = await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete video analysis?",
            Content = "This will delete the video analysis, its segments, task links, and generated documentation from Dataverse.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        }.ShowAsync();

        if (result == ContentDialogResult.Primary)
            await ViewModel.DeleteDraftAsync(id);
    }

    private async void SaveAnalysisChanges_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveAnalysisDocumentationAsync();
    }

    private void TaskSelection_Changed(object sender, RoutedEventArgs e)
    {
        ViewModel.UpdateSelectedTaskCount();
    }
}

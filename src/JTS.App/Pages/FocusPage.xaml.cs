using JTS_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace JTS_App.Pages;

public sealed partial class FocusPage : Page, IRefreshablePage
{
    public FocusViewModel ViewModel { get; }

    public FocusPage()
    {
        ViewModel = App.Services.GetRequiredService<FocusViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }

    private async void SummaryCalendar_SelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
    {
        var selected = args.AddedDates.FirstOrDefault();
        if (selected != default)
        {
            await ViewModel.SelectSummaryDateAsync(selected);
            SummaryCalendar.UpdateLayout();
        }
    }

    public async Task RefreshAsync()
    {
        await ViewModel.LoadAsync(forceSync: true);
        SummaryCalendar.UpdateLayout();
    }

    private void ToggleFocusBar_Click(object sender, RoutedEventArgs e) =>
        (Application.Current as App)?.ToggleFocusBar();

    private void SummaryCalendar_DayItemChanging(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
    {
        if (args.Item is null) return;
        if (ViewModel.HasWorkOnDate(args.Item.Date.Date))
        {
            args.Item.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 35, 74, 54));
        }
        else
        {
            args.Item.ClearValue(Control.BackgroundProperty);
        }
    }

    private void ToggleSummaryCalendar_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleSummaryCalendar();
    }

    private async void StartWork_Click(object sender, RoutedEventArgs e) => await ViewModel.StartTimeTrackingAsync();

    private async void StopTracking_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.StopTimeTrackingAsync();
    }

    private async void TaskList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FocusTaskOption task)
        {
            await ViewModel.SelectTaskAsync(task);
        }
    }

    private async void AddJournal_Click(object sender, RoutedEventArgs e)
    {
        var content = ViewModel.JournalDraftText;
        if (string.IsNullOrWhiteSpace(content)) return;
        await ViewModel.AddJournalEntryAsync(content);
        ViewModel.JournalDraftText = string.Empty;
    }

    private async void EditJournal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int entryId }) return;
        var entry = ViewModel.ActiveTaskJournal.FirstOrDefault(j => j.Id == entryId);
        if (entry is null) return;
        if (!entry.IsEditable) return;

        var textBox = new TextBox
        {
            AcceptsReturn = true,
            Height = 160,
            Text = entry.Content,
            TextWrapping = TextWrapping.Wrap
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Edit note",
            Content = textBox,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                await ViewModel.UpdateJournalEntryAsync(entryId, textBox.Text);
            }
            catch (InvalidOperationException ex)
            {
                await ShowMessageAsync("Note locked", ex.Message);
            }
        }
    }

    private async void DeleteJournal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int entryId }) return;
        var entry = ViewModel.ActiveTaskJournal.FirstOrDefault(j => j.Id == entryId);
        if (entry is null || !entry.IsEditable) return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete note?",
            Content = $"Delete this note from Dataverse?\n\n{entry.Content}",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await ViewModel.DeleteJournalEntryAsync(entryId);
        }
        catch (InvalidOperationException ex)
        {
            await ShowMessageAsync("Note locked", ex.Message);
        }
    }

    private async void AudioNote_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ToggleVoiceNoteAsync();
    }

    private async void DeleteSelectedTask_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTask is not { } task || !ViewModel.CanDeleteSelectedTask) return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete task?",
            Content = $"Delete \"{task.Title}\"? Calendar assignments, comments and tracked time linked to this task will also be deleted from Dataverse.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await ViewModel.DeleteSelectedTaskAsync();
        }
        catch (InvalidOperationException ex)
        {
            await ShowMessageAsync("Task locked", ex.Message);
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK"
        }.ShowAsync();
    }

}

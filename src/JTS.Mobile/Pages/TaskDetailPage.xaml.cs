using System.Collections.ObjectModel;
using JTS.Mobile.Services;

namespace JTS.Mobile.Pages;

public partial class TaskDetailPage : ContentPage
{
    private static readonly (string Label, string Status, string ColorHex)[] ColumnOptions =
    [
        ("Pending",    "Todo",       "#6B7280"),
        ("Assigned",   "Assigned",   "#3B82F6"),
        ("Ongoing",    "InProgress", "#F59E0B"),
        ("Testing",    "Testing",    "#8B5CF6"),
        ("UAT",        "UAT",        "#EC4899"),
        ("Production", "Done",       "#10B981"),
    ];

    private readonly DataverseMobileService _dataverse;
    private readonly ObservableCollection<CommentView> _comments = new();
    private readonly ObservableCollection<TimeEntryView> _timeEntries = new();
    private MobileTask? _task;

    public TaskDetailPage(DataverseMobileService dataverse)
    {
        _dataverse = dataverse;
        InitializeComponent();
        CommentsList.ItemsSource = _comments;
        TimeList.ItemsSource = _timeEntries;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _task = _dataverse.SelectedTask;
        if (_task is null) return;
        BindTask(_task);
        await LoadDetailsAsync(_task.Id);
    }

    private void BindTask(MobileTask task)
    {
        Title = task.Title.Length > 30 ? task.Title[..30] + "…" : task.Title;
        PriorityLabel.Text = task.PriorityIcon;
        PriorityLabel.TextColor = task.PriorityColor;
        TitleLabel.Text = task.Title;
        ProjectLabel.Text = task.ProjectName;
        StatusLabel.Text = task.Status;

        if (task.HasDueDate)
        {
            DueBadge.IsVisible = true;
            DueLabel.Text = task.DueDateShort;
        }
        else
        {
            DueBadge.IsVisible = false;
        }

        if (!string.IsNullOrWhiteSpace(task.Description))
        {
            DescriptionBorder.IsVisible = true;
            DescriptionLabel.Text = task.Description;
        }
        else
        {
            DescriptionBorder.IsVisible = false;
        }
    }

    private async Task LoadDetailsAsync(Guid taskId)
    {
        try
        {
            _comments.Clear();
            _timeEntries.Clear();

            var comments = await _dataverse.GetCommentsAsync(taskId);
            foreach (var c in comments)
                _comments.Add(new CommentView(c.CreatedAt.ToLocalTime().ToString("dd/MM HH:mm"), c.Content));

            CommentsEmptyLabel.IsVisible = _comments.Count == 0;
            CommentsList.IsVisible = _comments.Count > 0;

            var timeEntries = await _dataverse.GetTimeEntriesAsync(taskId);
            foreach (var t in timeEntries)
            {
                var localStart = t.StartedAt.ToLocalTime();
                var localEnd = t.EndedAt.ToLocalTime();
                _timeEntries.Add(new TimeEntryView(
                    localStart.ToString("dd/MM"),
                    $"{localStart:HH:mm} - {localEnd:HH:mm}",
                    FormatMinutes(t.ActualMinutes)));
            }

            TimeEmptyLabel.IsVisible = _timeEntries.Count == 0;
            TimeList.IsVisible = _timeEntries.Count > 0;
        }
        catch
        {
            // Details are optional — silently ignore errors
        }
    }

    private async void OnMoveToColumn(object? sender, EventArgs e)
    {
        if (_task is null) return;
        var labels = ColumnOptions.Select(c => c.Label).ToArray();
        var chosen = await DisplayActionSheetAsync("Move to column", "Cancel", null, labels);
        if (string.IsNullOrWhiteSpace(chosen) || chosen == "Cancel") return;
        var option = ColumnOptions.FirstOrDefault(c => c.Label == chosen);
        if (option == default) return;

        try
        {
            await _dataverse.UpdateTaskStatusAsync(_task.Id, option.Status);
            _dataverse.InvalidateCache();
            var updated = _task with { Status = option.Status };
            _dataverse.SelectedTask = updated;
            _task = updated;
            StatusLabel.Text = updated.Status;
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error", ex.Message, "OK");
        }
    }

    private async void OnAddComment(object? sender, EventArgs e)
    {
        if (_task is null) return;
        var comment = await DisplayPromptAsync("Comment", "What do you want to save?", keyboard: Keyboard.Text);
        if (string.IsNullOrWhiteSpace(comment)) return;

        try
        {
            await _dataverse.AddCommentAsync(_task.Id, _task.Title, comment);
            await LoadDetailsAsync(_task.Id);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error", ex.Message, "OK");
        }
    }

    private async void OnStartFocus(object? sender, EventArgs e)
    {
        if (_task is null) return;
        _dataverse.PendingFocusTaskId = _task.Id;
        await Shell.Current.GoToAsync("//focus");
    }

    private static string FormatMinutes(int minutes)
    {
        if (minutes <= 0) return "0m";
        var h = minutes / 60;
        var m = minutes % 60;
        return h > 0 ? $"{h}h {m:00}m" : $"{m}m";
    }
}

public sealed record CommentView(string CreatedAtText, string Content);
public sealed record TimeEntryView(string DateText, string TimeRangeText, string DurationText);

using JTS.Mobile.Services;

namespace JTS.Mobile.Pages;

public partial class FocusPage : ContentPage
{
    private readonly DataverseMobileService _dataverse;
    private List<MobileTask> _tasks = [];
    private DateTime? _timerStartedAt;
    private IDispatcherTimer? _timer;
    private int _sessionCount;

    public FocusPage(DataverseMobileService dataverse)
    {
        _dataverse = dataverse;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshTaskPickerAsync();
        ApplyPendingFocusTask();
    }

    private async Task RefreshTaskPickerAsync()
    {
        try
        {
            var cached = await _dataverse.GetCachedTasksAsync();
            _tasks = cached
                .Where(t => !string.Equals(t.Status, "Done", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(t.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.Title)
                .ToList();
            FocusTaskPicker.ItemsSource = _tasks;
            FocusTaskPicker.ItemDisplayBinding = new Binding(nameof(MobileTask.Title));
        }
        catch
        {
            // Use existing list if refresh fails
        }
    }

    private void ApplyPendingFocusTask()
    {
        if (_dataverse.PendingFocusTaskId is Guid taskId)
        {
            _dataverse.PendingFocusTaskId = null;
            var task = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (task is not null)
                FocusTaskPicker.SelectedItem = task;
        }
    }

    private void OnStartStop(object? sender, EventArgs e)
    {
        if (_timerStartedAt is null)
        {
            if (FocusTaskPicker.SelectedItem is not MobileTask)
            {
                StatusLabel.Text = "Select a task first.";
                return;
            }
            _timerStartedAt = DateTime.Now;
            StartStopButton.Text = "Pause";
            StartStopButton.BackgroundColor = Color.FromArgb("#92400E");
            _timer = Dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (_, _) => UpdateTimerLabel();
            _timer.Start();
            UpdateTimerLabel();
            StatusLabel.Text = "Session in progress...";
        }
        else
        {
            _timer?.Stop();
            _timer = null;
            _timerStartedAt = null;
            StartStopButton.Text = "Resume";
            StartStopButton.BackgroundColor = Color.FromArgb("#2563EB");
            StatusLabel.Text = "Paused.";
        }
    }

    private async void OnSaveTime(object? sender, EventArgs e)
    {
        if (FocusTaskPicker.SelectedItem is not MobileTask task || _timerStartedAt is not DateTime startedAt)
        {
            StatusLabel.Text = "No active time to save.";
            return;
        }

        _timer?.Stop();
        _timer = null;
        var endedAt = DateTime.Now;
        var note = FocusNoteEditor.Text?.Trim();

        try
        {
            StatusLabel.Text = "Saving...";
            await _dataverse.AddTimeEntryAsync(task.Id, task.Title, startedAt, endedAt);
            if (!string.IsNullOrWhiteSpace(note))
                await _dataverse.AddCommentAsync(task.Id, task.Title, note);
            _dataverse.InvalidateCache();
            _sessionCount++;
            ResetTimer();
            SessionLabel.Text = $"{_sessionCount} session{(_sessionCount == 1 ? "" : "s")} saved";
            StatusLabel.Text = "Time saved.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Error saving.";
            await DisplayAlertAsync("JTS", ex.Message, "OK");
        }
    }

    private void OnReset(object? sender, EventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        _timerStartedAt = null;
        ResetTimer();
        StatusLabel.Text = "";
    }

    private void ResetTimer()
    {
        _timerStartedAt = null;
        TimerLabel.Text = "00:00:00";
        StartStopButton.Text = "Start";
        StartStopButton.BackgroundColor = Color.FromArgb("#2563EB");
        FocusNoteEditor.Text = string.Empty;
    }

    private void UpdateTimerLabel()
    {
        if (_timerStartedAt is not DateTime startedAt) return;
        var elapsed = DateTime.Now - startedAt;
        TimerLabel.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
}

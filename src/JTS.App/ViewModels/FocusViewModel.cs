using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using JTS.AI;
using JTS.Core;
using JTS.Data.Entities;
using JTS_App.Services;

namespace JTS_App.ViewModels;

public sealed partial class FocusViewModel : ObservableObject
{
    private const string PomodoroStateKey = "Focus.PomodoroState";
    private const int TrackingDurationHours = 12;

    private readonly DataverseAppDataService _data;
    private readonly PomodoroService _pomodoro;
    private readonly NotificationService _notifications;
    private readonly PomodoroAlarmService _alarm;
    private readonly AppSettingsService _settings;
    private readonly DeepSeekClient _deepSeek;
    private readonly WhisperTranscriber _transcriber;
    private readonly AudioCaptureService _audioCapture;
    private readonly WindowsSpeechDictationService _windowsDictation;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private DateTime? _startedAt;
    private DateTime? _activeTaskStartedAt;
    private int _activeTaskSavedSeconds;
    private int _interruptionCount;
    private int _sequenceIndex;
    private bool _isRestored;
    private bool _isPersistingPomodoroState;
    private bool _isUsingWindowsDictation;
    private string _voiceNotePrefix = string.Empty;

    public event EventHandler<PomodoroCompletedEventArgs>? PomodoroCompleted;

    public ObservableCollection<FocusTaskOption> TodayTasks { get; } = new();
    public ObservableCollection<FocusTaskOption> TomorrowTasks { get; } = new();
    public ObservableCollection<FocusTaskOption> FutureTasks { get; } = new();
    public ObservableCollection<FocusJournalEntryView> ActiveTaskJournal { get; } = new();
    public ObservableCollection<FocusTaskSummaryView> TodayTaskSummaries { get; } = new();
    public ObservableCollection<FocusTimeSliceView> TodayTimeSlices { get; } = new();
    private readonly HashSet<DateTime> _workDates = new();

    [ObservableProperty] private FocusTaskOption? _selectedTask;
    [ObservableProperty] private FocusTaskOption? _activeTask;
    [ObservableProperty] private string _timerText = "00:00:00";
    [ObservableProperty] private string _sessionLabel = "Ready";
    [ObservableProperty] private string _activeTaskLabel = "No active task";
    [ObservableProperty] private string _activeTaskTotalText = "Task total: 0m";
    [ObservableProperty] private string _todaySessionSummary = "No sessions today";
    [ObservableProperty] private DateTimeOffset _selectedSummaryDate = DateTimeOffset.Now;
    [ObservableProperty] private string _summaryDateTitle = "Today summary";
    [ObservableProperty] private bool _isSummaryCalendarVisible;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private double _focusGoalHours = 8;
    [ObservableProperty] private double _todayFocusProgress;
    [ObservableProperty] private string _todayFocusGoalText = "0m / 8h";
    [ObservableProperty] private bool _hasActiveSession;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _canStopTracking;
    [ObservableProperty] private bool _isRecordingNote;
    [ObservableProperty] private bool _isTranscribingNote;
    [ObservableProperty] private double _workMinutes = 25;
    [ObservableProperty] private double _shortBreakMinutes = 5;
    [ObservableProperty] private double _longBreakMinutes = 15;
    [ObservableProperty] private int _longBreakEvery = 4;
    [ObservableProperty] private string _statusText = "Choose a task and start tracking time.";
    [ObservableProperty] private string _voiceNoteStatus = "Ready for voice notes.";
    [ObservableProperty] private string _journalDraftText = string.Empty;
    [ObservableProperty] private bool _selectedTaskHasTimesheetLinks;

    public string VoiceNoteButtonGlyph => IsRecordingNote ? "\uE71A" : "\uE720";
    public string VoiceNoteButtonText => IsRecordingNote ? "Stop" : "Record";
    public string SelectedSummaryDateText => DisplayFormat.SummaryDate(SelectedSummaryDate.Date);
    public bool CanDeleteSelectedTask => SelectedTask is not null && !_pomodoro.HasActiveSession && !SelectedTaskHasTimesheetLinks;
    public string SelectedTaskLockMessage => SelectedTaskHasTimesheetLinks
        ? "This task has time entries or comments already exported to a timesheet. Neither the task nor those records can be edited or deleted."
        : string.Empty;

    partial void OnSelectedTaskChanged(FocusTaskOption? value)
    {
        SelectedTaskHasTimesheetLinks = false;
        OnPropertyChanged(nameof(CanDeleteSelectedTask));
    }

    partial void OnSelectedTaskHasTimesheetLinksChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDeleteSelectedTask));
        OnPropertyChanged(nameof(SelectedTaskLockMessage));
    }

    partial void OnIsRecordingNoteChanged(bool value)
    {
        OnPropertyChanged(nameof(VoiceNoteButtonGlyph));
        OnPropertyChanged(nameof(VoiceNoteButtonText));
    }

    partial void OnSelectedSummaryDateChanged(DateTimeOffset value) => OnPropertyChanged(nameof(SelectedSummaryDateText));

    public FocusViewModel(
        DataverseAppDataService data,
        PomodoroService pomodoro,
        NotificationService notifications,
        PomodoroAlarmService alarm,
        AppSettingsService settings,
        DeepSeekClient deepSeek,
        WhisperTranscriber transcriber,
        AudioCaptureService audioCapture,
        WindowsSpeechDictationService windowsDictation)
    {
        _data = data;
        _pomodoro = pomodoro;
        _notifications = notifications;
        _alarm = alarm;
        _settings = settings;
        _deepSeek = deepSeek;
        _transcriber = transcriber;
        _audioCapture = audioCapture;
        _windowsDictation = windowsDictation;
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _windowsDictation.TextChanged += OnWindowsDictationTextChanged;
        _pomodoro.Tick += (_, _) => RefreshTimerState();
        _pomodoro.Completed += async (_, _) => await CompleteCurrentSessionAsync();
        RefreshTimerState();
    }

    public async Task LoadAsync(bool forceSync = false)
    {
        if (!_isRestored)
        {
            await LoadPomodoroSettingsAsync();
            await RestorePomodoroStateAsync();
            _isRestored = true;
        }

        var goalRaw = await _settings.GetFocusGoalHoursAsync();
        FocusGoalHours = double.TryParse(goalRaw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var goal) && goal > 0 ? goal : 8;

        var snapshot = await _data.LoadTaskSnapshotAsync(forceSync);
        var today = DateTime.Today;
        var tomorrow = today.AddDays(1);
        var dayAfterTomorrow = today.AddDays(2);

        var scheduledBlocks = snapshot.Tasks
            .Where(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled)
            .SelectMany(t => t.ScheduleBlocks)
            .Where(b => b.End >= today)
            .OrderBy(b => b.Start)
            .ThenBy(b => b.TaskItem!.Title)
            .ToList();

        TodayTasks.Clear();
        TomorrowTasks.Clear();
        FutureTasks.Clear();
        AddScheduledTasks(TodayTasks, scheduledBlocks.Where(b => b.Start.Date <= today));
        AddScheduledTasks(TomorrowTasks, scheduledBlocks.Where(b => b.Start.Date == tomorrow));
        AddScheduledTasks(FutureTasks, scheduledBlocks.Where(b => b.Start.Date >= dayAfterTomorrow));

        if (SelectedTask is not null && AllVisibleTasks().All(t => t.Id != SelectedTask.Id))
        {
            SelectedTask = null;
        }

        if (ActiveTask is not null && AllVisibleTasks().All(t => t.Id != ActiveTask.Id))
        {
            ActiveTask = null;
            _activeTaskStartedAt = null;
        }

        await LoadActiveTaskDetailsAsync();
        await LoadSummaryAsync(SelectedSummaryDate.Date, forceSync);
    }

    public async Task StartOrResumePomodoroAsync()
    {
        if (_pomodoro.HasActiveSession)
        {
            Resume();
            return;
        }

        var sessionType = CurrentSequenceType();
        Start(sessionType, MinutesFor(sessionType));
        await PersistPomodoroStateAsync();
    }

    public async Task StartTimeTrackingAsync()
    {
        if (SelectedTask is null)
        {
            StatusText = "Choose a task before starting time tracking.";
            return;
        }

        if (_pomodoro.HasActiveSession)
        {
            StatusText = ActiveTask is not null
                ? $"Tracking time for {ActiveTask.Title}."
                : "Time tracking is already running.";
            return;
        }

        ActiveTask = SelectedTask;
        ActiveTaskLabel = ActiveTask.DisplayName;
        Start(PomodoroSessionType.Work, TrackingDurationHours * 60);
        _activeTaskStartedAt = _startedAt;
        StatusText = $"Tracking time for {ActiveTask.Title}.";
        await PersistPomodoroStateAsync();
        await LoadActiveTaskDetailsAsync();
    }

    public async Task StopTimeTrackingAsync()
    {
        _alarm.StopCompletionBell();
        await SaveActiveTaskSliceAsync(completed: false);
        _pomodoro.Stop();
        _startedAt = null;
        _activeTaskStartedAt = null;
        _interruptionCount = 0;
        ActiveTask = null;
        StatusText = "Time tracking stopped and saved.";
        await PersistPomodoroStateAsync(clearSession: true);
        await LoadActiveTaskDetailsAsync();
        await LoadSummaryAsync(SelectedSummaryDate.Date);
    }

    public async Task ResetPomodoroCycleAsync()
    {
        _alarm.StopCompletionBell();
        await SaveActiveTaskSliceAsync(completed: false);
        _pomodoro.Stop();
        _sequenceIndex = 0;
        _startedAt = null;
        _interruptionCount = 0;
        _activeTaskStartedAt = null;
        SyncReadyTimer();
        await PersistPomodoroStateAsync(clearSession: true);
        StatusText = "Pomodoro cycle reset.";
    }

    public async Task SelectSummaryDateAsync(DateTimeOffset date)
    {
        SelectedSummaryDate = date;
        IsSummaryCalendarVisible = false;
        await LoadSummaryAsync(date.Date);
    }

    public void ToggleSummaryCalendar() => IsSummaryCalendarVisible = !IsSummaryCalendarVisible;

    public bool HasWorkOnDate(DateTime date) => _workDates.Contains(date.Date);

    public void StartWork() => Start(CurrentSequenceType(), MinutesFor(CurrentSequenceType()));
    public void StartShortBreak() => Start(CurrentSequenceType(), MinutesFor(CurrentSequenceType()));
    public void StartLongBreak() => Start(PomodoroSessionType.LongBreak, LongBreakMinutes);

    public async Task SelectTaskAsync(FocusTaskOption task)
    {
        if (_pomodoro.IsRunning && ActiveTask?.Id != task.Id)
        {
            await SaveActiveTaskSliceAsync(completed: false);
            SelectedTask = task;
            ActiveTask = task;
            ActiveTaskLabel = task.DisplayName;
            _activeTaskStartedAt = DateTime.UtcNow;
            StatusText = $"Switched tracking to {task.Title}.";
            App.Log($"[FocusViewModel] Switched active task taskId={task.Id} title='{task.Title}'");
            await LoadActiveTaskDetailsAsync();
            await LoadSummaryAsync(SelectedSummaryDate.Date);
            return;
        }

        SelectedTask = task;
        StatusText = $"Selected task: {task.Title}";
        App.Log($"[FocusViewModel] Selected task taskId={task.Id} title='{task.Title}'");
        await LoadActiveTaskDetailsAsync();
    }

    public async Task PauseTimerAsync()
    {
        await SaveActiveTaskSliceAsync(completed: false);
        _pomodoro.Pause();
        await PersistPomodoroStateAsync();
    }

    public async void Resume()
    {
        _alarm.StopCompletionBell();
        _pomodoro.Resume();
        _startedAt ??= DateTime.UtcNow;
        if (ActiveTask is not null && _pomodoro.SessionType == PomodoroSessionType.Work)
        {
            _activeTaskStartedAt = DateTime.UtcNow;
        }
        await PersistPomodoroStateAsync();
    }

    public async Task StartSelectedTaskAsync()
    {
        if (SelectedTask is null) return;
        await SaveActiveTaskSliceAsync(completed: false);
        ActiveTask = SelectedTask;
        ActiveTaskLabel = ActiveTask.DisplayName;
        _activeTaskStartedAt = _pomodoro.HasActiveSession && _pomodoro.SessionType == PomodoroSessionType.Work && _pomodoro.IsRunning
            ? DateTime.UtcNow
            : null;
        StatusText = $"Task active: {ActiveTask.Title}";
        await LoadActiveTaskDetailsAsync();
    }

    public async Task PauseActiveTaskAsync()
    {
        await SaveActiveTaskSliceAsync(completed: false);
        ActiveTask = null;
        ActiveTaskLabel = "No active task";
        StatusText = "Task paused. Timer can keep running without assigning time.";
        await LoadActiveTaskDetailsAsync();
        await LoadSummaryAsync(SelectedSummaryDate.Date);
    }

    public async Task FinishActiveTaskAsync()
    {
        if (ActiveTask is null) return;
        await SaveActiveTaskSliceAsync(completed: true);

        if (ActiveTask.DataverseId is Guid taskDataverseId)
            await _data.SetStatusAsync(taskDataverseId, TaskItemStatus.Done);

        ActiveTask = null;
        SelectedTask = null;
        ActiveTaskLabel = "No active task";
        StatusText = "Task completed.";
        await LoadAsync();
    }

    public async Task StopAsync()
    {
        _alarm.StopCompletionBell();
        if (!_pomodoro.HasActiveSession || _startedAt is null)
        {
            _pomodoro.Stop();
            return;
        }

        await SaveActiveTaskSliceAsync(completed: false);
        await SaveSessionAsync(completed: false);
        _pomodoro.Stop();
        _startedAt = null;
        _interruptionCount = 0;
        StatusText = "Session stopped and saved as incomplete.";
        await PersistPomodoroStateAsync(clearSession: true);
    }

    public async Task FlushActiveTimeBeforeCloseAsync()
    {
        if (!_pomodoro.HasActiveSession) return;
        await SaveActiveTaskSliceAsync(completed: false);
        _pomodoro.Stop();
        _startedAt = null;
        _activeTaskStartedAt = null;
        await PersistPomodoroStateAsync(clearSession: true);
        App.Log("[FocusViewModel] Active focus time flushed before app close.");
    }

    public void RecordInterruption()
    {
        if (!_pomodoro.HasActiveSession) return;
        _interruptionCount++;
        StatusText = _interruptionCount == 1
            ? "1 interruption recorded."
            : $"{_interruptionCount} interruptions recorded.";
    }

    public async Task AddJournalEntryAsync(string content)
    {
        var task = ActiveTask ?? SelectedTask;
        if (task?.DataverseId is not Guid taskDataverseId || string.IsNullOrWhiteSpace(content)) return;
        var reviewed = await ReviewJournalContentAsync(task, content);
        await _data.AddCommentAsync(taskDataverseId, task.Title, reviewed);
        await LoadActiveTaskDetailsAsync();
    }

    public async Task UpdateJournalEntryAsync(int entryId, string content)
    {
        var task = ActiveTask ?? SelectedTask;
        if (task?.DataverseId is not Guid taskDataverseId || string.IsNullOrWhiteSpace(content)) return;

        var entries = await _data.LoadCommentsAsync(taskDataverseId);
        var entry = entries.FirstOrDefault(e => e.Id == entryId);
        if (entry?.DataverseId is not Guid commentId)
        {
            VoiceNoteStatus = "Could not find the Dataverse comment to update.";
            return;
        }

        await _data.UpdateCommentAsync(commentId, content);
        VoiceNoteStatus = "Note updated.";
        await LoadActiveTaskDetailsAsync();
    }

    public async Task DeleteJournalEntryAsync(int entryId)
    {
        var task = ActiveTask ?? SelectedTask;
        if (task?.DataverseId is not Guid taskDataverseId) return;

        var entries = await _data.LoadCommentsAsync(taskDataverseId);
        var entry = entries.FirstOrDefault(e => e.Id == entryId);
        if (entry?.DataverseId is not Guid commentId)
        {
            VoiceNoteStatus = "Could not find the Dataverse comment to delete.";
            return;
        }

        await _data.DeleteCommentAsync(commentId);
        VoiceNoteStatus = "Note deleted.";
        await LoadActiveTaskDetailsAsync();
    }

    public async Task DeleteSelectedTaskAsync()
    {
        if (SelectedTask?.DataverseId is not Guid taskDataverseId) return;
        if (_pomodoro.HasActiveSession)
        {
            StatusText = "Stop time tracking before deleting a task.";
            return;
        }

        await _data.DeleteTaskAsync(taskDataverseId);
        SelectedTask = null;
        ActiveTask = null;
        ActiveTaskLabel = "No active task";
        StatusText = "Task deleted.";
        await LoadAsync(forceSync: true);
    }

    public async Task ToggleVoiceNoteAsync()
    {
        if (IsTranscribingNote) return;

        if (!IsRecordingNote)
        {
            if (ActiveTask is null && SelectedTask is null)
            {
                VoiceNoteStatus = "Select or start a task before recording a note.";
                return;
            }

            IsRecordingNote = true;
            _voiceNotePrefix = JournalDraftText;
            VoiceNoteStatus = "Dictating note...";
            try
            {
                await _windowsDictation.StartAsync();
                _isUsingWindowsDictation = true;
            }
            catch (Exception ex)
            {
                _isUsingWindowsDictation = false;
                VoiceNoteStatus = $"Windows speech unavailable, recording fallback audio: {ex.Message}";
                _audioCapture.Start();
            }
            return;
        }

        if (_isUsingWindowsDictation)
        {
            var transcript = await _windowsDictation.StopAsync();
            _isUsingWindowsDictation = false;
            JournalDraftText = BuildDictationText(_voiceNotePrefix, transcript);
            IsRecordingNote = false;
            VoiceNoteStatus = string.IsNullOrWhiteSpace(transcript)
                ? "No speech detected."
                : "Dictation stopped. Review it and press Send.";
            return;
        }

        var wav = await _audioCapture.StopAsync();
        IsRecordingNote = false;
        if (wav is null)
        {
            VoiceNoteStatus = "No recording found.";
            return;
        }

        var model = await _settings.GetWhisperModelPathAsync();
        if (string.IsNullOrWhiteSpace(model))
        {
            VoiceNoteStatus = $"Audio saved. Configure a Whisper .bin model in Settings to transcribe: {wav}";
            return;
        }

        IsTranscribingNote = true;
        VoiceNoteStatus = "Transcribing note...";
        try
        {
            var transcript = await _transcriber.TranscribeAsync(model, wav);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                VoiceNoteStatus = "No speech detected.";
                return;
            }

            JournalDraftText = BuildDictationText(_voiceNotePrefix, transcript);
            VoiceNoteStatus = "Audio transcribed. Review it and press Send.";
        }
        catch (Exception ex)
        {
            App.Log("[FocusViewModel] Voice note failed: " + ex);
            VoiceNoteStatus = $"Voice note error: {ex.Message}";
        }
        finally
        {
            IsTranscribingNote = false;
        }
    }

    private void OnWindowsDictationTextChanged(object? sender, SpeechDictationTextChangedEventArgs e)
    {
        if (!IsRecordingNote || !_isUsingWindowsDictation) return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            JournalDraftText = BuildDictationText(_voiceNotePrefix, e.Text);
            VoiceNoteStatus = string.IsNullOrWhiteSpace(e.Text)
                ? "Dictating note..."
                : "Writing note as you speak...";
        });
    }

    private void Start(PomodoroSessionType sessionType, double minutes)
    {
        _alarm.StopCompletionBell();
        var safeMinutes = Math.Clamp((int)Math.Round(minutes), 1, 180);
        if (minutes > 180) safeMinutes = (int)Math.Round(minutes);
        _startedAt = DateTime.UtcNow;
        if (sessionType == PomodoroSessionType.Work && ActiveTask is not null)
        {
            _activeTaskStartedAt = _startedAt;
        }
        _interruptionCount = 0;
        _pomodoro.Start(sessionType, TimeSpan.FromMinutes(safeMinutes), null);
        StatusText = sessionType == PomodoroSessionType.Work
            ? "Time tracking running."
            : "Break running.";
    }

    private async Task CompleteCurrentSessionAsync()
    {
        await SaveActiveTaskSliceAsync(completed: true);
        await SaveSessionAsync(completed: true);
        var taskId = ActiveTask?.Id;
        var sessionType = _pomodoro.SessionType;
        _startedAt = null;
        _interruptionCount = 0;
        _sequenceIndex = (_sequenceIndex + 1) % SequenceLength;
        StatusText = sessionType == PomodoroSessionType.Work
            ? "Pomodoro completed. Log what you did."
            : "Break completed.";
        _alarm.StartCompletionBell();
        _notifications.Show(
            sessionType == PomodoroSessionType.Work ? "Pomodoro complete" : "Break complete",
            sessionType == PomodoroSessionType.Work ? "Nice focus session. Add a journal note." : "Time to get back into flow.");

        PomodoroCompleted?.Invoke(this, new PomodoroCompletedEventArgs(taskId, sessionType));
        await PersistPomodoroStateAsync(clearSession: true);
        await LoadActiveTaskDetailsAsync();
        await LoadSummaryAsync(SelectedSummaryDate.Date);
    }

    public void StopCompletionBell() => _alarm.StopCompletionBell();

    private Task SaveSessionAsync(bool completed)
    {
        return Task.CompletedTask;
    }

    private async Task SaveActiveTaskSliceAsync(bool completed)
    {
        if (ActiveTask is null || _activeTaskStartedAt is null) return;
        if (!_pomodoro.HasActiveSession || !_pomodoro.IsRunning || _pomodoro.SessionType != PomodoroSessionType.Work)
        {
            _activeTaskStartedAt = null;
            return;
        }

        var endedAt = DateTime.UtcNow;
        if (endedAt <= _activeTaskStartedAt.Value) return;
        var actualSeconds = Math.Max(1, (int)Math.Round((endedAt - _activeTaskStartedAt.Value).TotalSeconds));
        if (actualSeconds < 60)
        {
            _activeTaskStartedAt = null;
            return;
        }

        var roundedMinutes = Math.Max(1, (int)Math.Round(actualSeconds / 60d, MidpointRounding.AwayFromZero));
        var roundedEndAt = _activeTaskStartedAt.Value.AddMinutes(roundedMinutes);

        if (ActiveTask.DataverseId is Guid taskDataverseId)
        {
            await _data.AddTimeEntryAsync(
                taskDataverseId,
                ActiveTask.Title,
                _activeTaskStartedAt.Value,
                roundedEndAt,
                completed ? "Completado desde escritorio" : "Guardado desde escritorio");
            _activeTaskSavedSeconds += roundedMinutes * 60;
            RefreshActiveTaskTotalText();
        }
        _activeTaskStartedAt = null;
    }

    private async Task LoadActiveTaskDetailsAsync()
    {
        ActiveTaskJournal.Clear();
        var taskId = ActiveTask?.Id ?? SelectedTask?.Id;
        if (taskId is null)
        {
            ActiveTaskLabel = "No active task";
            ActiveTaskTotalText = "Task total: 0m";
            SelectedTaskHasTimesheetLinks = false;
            return;
        }

        var task = ActiveTask ?? SelectedTask;
        if (task?.DataverseId is not Guid taskDataverseId) return;

        var details = await _data.LoadTaskDetailsSnapshotAsync([taskDataverseId]);
        var entries = details.CommentsByTask.TryGetValue(taskDataverseId, out var cachedComments)
            ? cachedComments
            : [];
        foreach (var entry in entries)
        {
            var isTransferred = entry.TimesheetLineDataverseId is not null;
            ActiveTaskJournal.Add(new FocusJournalEntryView(
                entry.Id,
                entry.DataverseId,
                DisplayFormat.DateTimeFromUtc(entry.CreatedAt),
                entry.Content,
                isTransferred,
                !isTransferred));
        }

        var timeEntries = details.TimeEntriesByTask.TryGetValue(taskDataverseId, out var cachedTimeEntries)
            ? cachedTimeEntries
            : [];
        _activeTaskSavedSeconds = timeEntries.Sum(entry => Math.Max(0, entry.ActualMinutes * 60));
        SelectedTaskHasTimesheetLinks =
            entries.Any(entry => entry.TimesheetLineDataverseId is not null) ||
            timeEntries.Any(entry => entry.TimesheetLineDataverseId is not null);
        ActiveTaskLabel = ActiveTask?.DisplayName ?? SelectedTask?.DisplayName ?? "No active task";
        ActiveTaskTotalText = $"Task total: {FormatSeconds(_activeTaskSavedSeconds + ActiveTaskCurrentSeconds())}";
    }

    private async Task<string> ReviewJournalContentAsync(FocusTaskOption task, string content)
    {
        var fallback = content.Trim();
        if (string.IsNullOrWhiteSpace(fallback)) return fallback;

        var reviewEnabled = string.Equals(await _settings.GetAsync("AI.ReviewTaskComments"), "true", StringComparison.OrdinalIgnoreCase);
        if (!reviewEnabled) return fallback;

        var apiKey = await _settings.GetDeepSeekApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey)) return fallback;

        try
        {
            var response = await _deepSeek.ChatAsync(apiKey, new[]
            {
                new DeepSeekMessage("system", AiCommentReviewGuard.ConservativeSystemPrompt),
                new DeepSeekMessage("user", AiCommentReviewGuard.BuildUserPrompt(task.Title, task.ProjectName, fallback))
            });

            return AiCommentReviewGuard.KeepOriginalIfUnsafe(fallback, response);
        }
        catch (Exception ex)
        {
            App.Log("[FocusViewModel] Journal review failed: " + ex);
            return fallback;
        }
    }

    private async Task LoadSummaryAsync(DateTime date, bool forceSync = false)
    {
        var today = date.Date;
        var snapshot = await _data.LoadTaskSnapshotAsync(forceSync);
        var tasksWithDataverseId = snapshot.Tasks.Where(t => t.DataverseId is not null).ToList();
        var entriesByTask = await _data.LoadTimeEntriesByTaskAsync(tasksWithDataverseId.Select(t => t.DataverseId!.Value), forceSync);
        var allTaskSessions = tasksWithDataverseId
            .SelectMany(task =>
            {
                var sessions = entriesByTask.TryGetValue(task.DataverseId!.Value, out var cached) ? cached : [];
                return sessions.Select(session => (Task: task, Session: session));
            })
            .ToList();

        _workDates.Clear();
        foreach (var workDate in allTaskSessions.Select(s => DisplayFormat.ToSpainTime(s.Session.StartedAt).Date).Distinct())
            _workDates.Add(workDate);

        SummaryDateTitle = today == DateTime.Today ? "Today summary" : $"{DisplayFormat.SummaryDate(today)} summary";
        TodayTaskSummaries.Clear();
        TodayTimeSlices.Clear();

        var daySessions = allTaskSessions
            .Where(s => DisplayFormat.ToSpainTime(s.Session.StartedAt).Date == today)
            .ToList();
        TodaySessionSummary = $"{daySessions.Count} entries · Work {FormatMinutes(daySessions.Sum(s => s.Session.ActualMinutes))}";

        var todayMinutes = allTaskSessions
            .Where(s => DisplayFormat.ToSpainTime(s.Session.StartedAt).Date == DateTime.Today)
            .Sum(s => s.Session.ActualMinutes);
        UpdateFocusGoal(todayMinutes);

        foreach (var item in daySessions
            .GroupBy(s => s.Task.Title)
            .Select(g => new FocusTaskSummaryView(g.Key, FormatMinutes(g.Sum(s => s.Session.ActualMinutes))))
            .OrderByDescending(s => s.TimeText))
        {
            TodayTaskSummaries.Add(item);
        }

        foreach (var item in daySessions
            .OrderBy(s => DisplayFormat.ToSpainTime(s.Session.StartedAt))
            .ThenBy(s => s.Task.Title)
            .Select(s =>
            {
                var start = DisplayFormat.ToSpainTime(s.Session.StartedAt);
                var end = s.Session.EndedAt is DateTime endedAt
                    ? DisplayFormat.ToSpainTime(endedAt)
                    : start.AddMinutes(s.Session.ActualMinutes);

                return new FocusTimeSliceView(
                    $"{start:HH:mm} - {end:HH:mm}",
                    s.Task.Title,
                    s.Task.Project?.Name ?? "No project",
                    FormatMinutes(s.Session.ActualMinutes));
            }))
        {
            TodayTimeSlices.Add(item);
        }
    }

    private int _todayFocusMinutes;

    partial void OnFocusGoalHoursChanged(double value) => UpdateFocusGoal(_todayFocusMinutes);

    private void UpdateFocusGoal(int todayMinutes)
    {
        _todayFocusMinutes = todayMinutes;
        var goalMinutes = Math.Max(1, FocusGoalHours * 60);
        TodayFocusProgress = Math.Clamp(todayMinutes / goalMinutes, 0, 1);
        TodayFocusGoalText = $"{FormatMinutes(todayMinutes)} / {FocusGoalHours:0.#}h";
    }

    private IEnumerable<FocusTaskOption> AllVisibleTasks() =>
        TodayTasks.Concat(TomorrowTasks).Concat(FutureTasks);

    private Task LoadWorkDatesAsync()
    {
        return Task.CompletedTask;
    }

    private static void AddScheduledTasks(ObservableCollection<FocusTaskOption> target, IEnumerable<TaskScheduleBlock> blocks)
    {
        foreach (var group in blocks
            .Where(b => b.TaskItem is not null)
            .GroupBy(b => b.TaskItemId)
            .OrderBy(g => g.Min(b => b.Start)))
        {
            var task = group.First().TaskItem!;
            var periods = string.Join(", ", group
                .OrderBy(b => b.Start)
                .Select(b => b.Start.Date == b.End.Date
                    ? $"{b.Start:ddd HH:mm}-{b.End:HH:mm}"
                    : $"{b.Start:ddd HH:mm}-{b.End:ddd HH:mm}"));

            target.Add(new FocusTaskOption(
                task.Id,
                task.DataverseId,
                task.Title,
                task.Project?.Name ?? "No project",
                ProjectColorService.CardColor(task.Project?.ColorHex),
                periods));
        }
    }

    private void RefreshTimerState()
    {
        HasActiveSession = _pomodoro.HasActiveSession;
        IsRunning = _pomodoro.IsRunning;
        CanStopTracking = _pomodoro.HasActiveSession;
        OnPropertyChanged(nameof(CanDeleteSelectedTask));
        TimerText = _pomodoro.HasActiveSession ? FormatDuration(_pomodoro.ActiveElapsed) : "00:00:00";
        SessionLabel = _pomodoro.HasActiveSession
            ? _pomodoro.IsRunning ? "Tracking time" : "Time tracking paused"
            : "Ready to track time";
        ProgressValue = 0;
        _ = PersistPomodoroStateAsync();
        RefreshActiveTaskTotalText();
    }

    private void RefreshActiveTaskTotalText()
    {
        var taskId = ActiveTask?.Id ?? SelectedTask?.Id;
        if (taskId is null)
        {
            ActiveTaskTotalText = "Task total: 0m";
            return;
        }

        ActiveTaskTotalText = $"Task total: {FormatSeconds(_activeTaskSavedSeconds + ActiveTaskCurrentSeconds())}";
    }

    private int ActiveTaskCurrentSeconds()
    {
        if (ActiveTask is null ||
            _activeTaskStartedAt is not DateTime startedAt ||
            !_pomodoro.IsRunning ||
            _pomodoro.SessionType != PomodoroSessionType.Work)
        {
            return 0;
        }

        return Math.Max(0, (int)Math.Round((DateTime.UtcNow - startedAt).TotalSeconds));
    }

    private int SequenceLength => Math.Max(1, LongBreakEvery) * 2;

    private PomodoroSessionType CurrentSequenceType()
    {
        var longBreakEvery = Math.Max(1, LongBreakEvery);
        var step = Math.Clamp(_sequenceIndex, 0, SequenceLength - 1);
        if (step % 2 == 0) return PomodoroSessionType.Work;
        var breakNumber = (step + 1) / 2;
        return breakNumber % longBreakEvery == 0 ? PomodoroSessionType.LongBreak : PomodoroSessionType.ShortBreak;
    }

    private double MinutesFor(PomodoroSessionType sessionType) =>
        sessionType switch
        {
            PomodoroSessionType.Work => WorkMinutes,
            PomodoroSessionType.LongBreak => LongBreakMinutes,
            _ => ShortBreakMinutes
        };

    private async Task RestorePomodoroStateAsync()
    {
        var json = await _settings.GetAsync(PomodoroStateKey);
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            var state = JsonSerializer.Deserialize<FocusPomodoroState>(json);
            if (state is null) return;
            _sequenceIndex = Math.Clamp(state.SequenceIndex, 0, SequenceLength - 1);
            if (!state.HasActiveSession) return;

                _pomodoro.Restore(
                state.SessionType,
                TimeSpan.FromSeconds(Math.Max(1, state.DurationSeconds)),
                TimeSpan.FromSeconds(Math.Max(1, state.RemainingSeconds)));
            _startedAt = null;
            StatusText = "Pomodoro restored paused.";
        }
        catch (Exception ex)
        {
            App.Log("[FocusViewModel] Restore pomodoro failed: " + ex);
        }
    }

    private async Task PersistPomodoroStateAsync(bool clearSession = false)
    {
        if (_isPersistingPomodoroState) return;
        _isPersistingPomodoroState = true;
        try
        {
            var state = new FocusPomodoroState(
                _sequenceIndex,
                !clearSession && _pomodoro.HasActiveSession,
                _pomodoro.SessionType,
                (int)Math.Round(_pomodoro.Duration.TotalSeconds),
                (int)Math.Round(_pomodoro.Remaining.TotalSeconds));
            await _settings.SetAsync(PomodoroStateKey, JsonSerializer.Serialize(state));
        }
        finally
        {
            _isPersistingPomodoroState = false;
        }
    }

    private async Task LoadPomodoroSettingsAsync()
    {
        WorkMinutes = await GetDoubleSettingAsync("Pomodoro.WorkMinutes", 25);
        ShortBreakMinutes = await GetDoubleSettingAsync("Pomodoro.ShortBreakMinutes", 5);
        LongBreakMinutes = await GetDoubleSettingAsync("Pomodoro.LongBreakMinutes", 15);
        LongBreakEvery = await GetIntSettingAsync("Pomodoro.LongBreakEvery", 4);
        SyncReadyTimer();
    }

    private void SyncReadyTimer()
    {
        if (_pomodoro.HasActiveSession) return;
        var sessionType = CurrentSequenceType();
        _pomodoro.SetReady(sessionType, TimeSpan.FromMinutes(MinutesFor(sessionType)));
    }

    private async Task<double> GetDoubleSettingAsync(string key, double fallback)
    {
        var value = await _settings.GetAsync(key);
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 1, 240)
            : fallback;
    }

    private async Task<int> GetIntSettingAsync(string key, int fallback)
    {
        var value = await _settings.GetAsync(key);
        return int.TryParse(value, out var parsed) ? Math.Clamp(parsed, 1, 12) : fallback;
    }

    private static string FormatMinutes(int minutes)
    {
        if (minutes <= 0) return "0m";
        var hours = minutes / 60;
        var remainder = minutes % 60;
        return hours > 0 ? $"{hours}h {remainder:00}m" : $"{remainder}m";
    }

    private static string FormatSeconds(int seconds)
    {
        if (seconds <= 0) return "0m";
        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes:00}m {duration.Seconds:00}s"
            : $"{duration.Minutes}m {duration.Seconds:00}s";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var safe = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        return safe.TotalHours >= 1
            ? $"{(int)safe.TotalHours:00}:{safe.Minutes:00}:{safe.Seconds:00}"
            : $"00:{safe.Minutes:00}:{safe.Seconds:00}";
    }

    private static string BuildDictationText(string prefix, string? dictation)
    {
        var cleanPrefix = prefix.TrimEnd();
        var cleanDictation = dictation?.Trim();
        if (string.IsNullOrWhiteSpace(cleanPrefix)) return cleanDictation ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cleanDictation)) return cleanPrefix;
        return $"{cleanPrefix} {cleanDictation}";
    }
}

public sealed record FocusTaskOption(int Id, Guid? DataverseId, string Title, string ProjectName, string ProjectColorHex, string ScheduleText)
{
    public string DisplayName => $"{Title} - {ProjectName}";
    public string Detail => string.IsNullOrWhiteSpace(ScheduleText) ? ProjectName : $"{ProjectName} · {ScheduleText}";
}

public sealed record PomodoroCompletedEventArgs(int? TaskId, PomodoroSessionType SessionType);

public sealed record FocusJournalEntryView(int Id, Guid? DataverseId, string CreatedAtText, string Content, bool IsTimesheetTransferred, bool IsEditable);

public sealed record FocusTaskSummaryView(string TaskName, string TimeText);

public sealed record FocusTimeSliceView(string TimeRangeText, string TaskName, string ProjectName, string DurationText);

public sealed record FocusPomodoroState(
    int SequenceIndex,
    bool HasActiveSession,
    PomodoroSessionType SessionType,
    int DurationSeconds,
    int RemainingSeconds);

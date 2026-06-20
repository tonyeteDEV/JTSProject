using System.Collections.ObjectModel;
using JTS.Mobile.Services;

namespace JTS.Mobile;

public partial class MainPage : ContentPage
{
    private readonly DataverseMobileService _dataverse;
    private readonly MobileAgentService _agent;
    private readonly MobileVoiceService _voice;
    private readonly ObservableCollection<MobileTask> _visibleTasks = new();
    private readonly ObservableCollection<AgentChatMessage> _agentMessages = new();
    private List<MobileTask> _allTasks = new();
    private List<MobileProject> _projects = new();
    private MobileTask? _selectedTask;
    private AgentActionPreview? _pendingPreview;
    private bool _conversationMode;
    private DateTime? _timerStartedAt;
    private IDispatcherTimer? _timer;

    public MainPage(DataverseMobileService dataverse, MobileAgentService agent, MobileVoiceService voice)
    {
        _dataverse = dataverse;
        _agent = agent;
        _voice = voice;
        InitializeComponent();
        foreach (var item in new[] { "Today", "Tomorrow", "Future", "All" })
            HorizonPicker.Items.Add(item);
        HorizonPicker.SelectedIndex = 0;
        foreach (var item in new[] { "deepseek-v4-flash", "deepseek-v4-pro", "deepseek-reasoner", "deepseek-chat" })
            DeepSeekModelPicker.Items.Add(item);
        DeepSeekModelPicker.SelectedIndex = 0;
        TaskList.ItemsSource = _visibleTasks;
        AgentMessageList.ItemsSource = _agentMessages;
        _agentMessages.Add(new AgentChatMessage("Agent", "Tell me what you need. Before changing anything I'll show you a preview to confirm.", "#182536"));
        AgentVoiceRateSlider.ValueChanged += (_, e) => AgentVoiceRateLabel.Text = $"{e.NewValue:0.00}x";
        WireVoice();
        Loaded += async (_, _) => await LoadInitialAsync();
    }

    private async Task LoadInitialAsync()
    {
        await LoadSettingsIntoFormAsync();
        if (!await _dataverse.HasSettingsAsync())
        {
            ShowSection(SettingsView);
            StatusLabel.Text = "Set up Dataverse to get started.";
            return;
        }

        await RefreshDataAsync();
        ShowSection(AgentView);
    }

    private async Task RefreshDataAsync()
    {
        try
        {
            StatusLabel.Text = "Syncing with Dataverse...";
            _projects = (await _dataverse.GetProjectsAsync()).ToList();
            _allTasks = (await _dataverse.GetTasksAsync()).ToList();
            FocusTaskPicker.ItemsSource = _allTasks;
            FocusTaskPicker.ItemDisplayBinding = new Binding(nameof(MobileTask.Title));
            ApplyHorizonFilter();
            StatusLabel.Text = $"{_visibleTasks.Count} tasks visible.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Connection error.";
            await DisplayAlertAsync("Dataverse", ex.Message, "OK");
            ShowSection(SettingsView);
        }
    }

    private void ApplyHorizonFilter()
    {
        var today = DateTime.Today;
        var tomorrow = today.AddDays(1);
        IEnumerable<MobileTask> query = _allTasks
            .Where(t => !string.Equals(t.Status, "Done", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(t.Status, "Cancelled", StringComparison.OrdinalIgnoreCase));

        query = HorizonPicker.SelectedIndex switch
        {
            0 => query.Where(t => (t.ScheduledStart ?? t.DueDate)?.Date <= today),
            1 => query.Where(t => (t.ScheduledStart ?? t.DueDate)?.Date == tomorrow),
            2 => query.Where(t => (t.ScheduledStart ?? t.DueDate)?.Date > tomorrow || (t.ScheduledStart is null && t.DueDate is null)),
            _ => _allTasks
        };

        _visibleTasks.Clear();
        foreach (var task in query.OrderBy(t => t.ScheduledStart ?? t.DueDate ?? DateTime.MaxValue).ThenBy(t => t.Title))
            _visibleTasks.Add(task);
    }

    private async Task LoadSettingsIntoFormAsync()
    {
        var settings = await _dataverse.GetSettingsAsync();
        EnvironmentEntry.Text = settings.EnvironmentUrl;
        TenantEntry.Text = settings.TenantId;
        ClientEntry.Text = settings.ClientId;
        SecretEntry.Text = settings.ClientSecret;
        DeepSeekKeyEntry.Text = settings.DeepSeekApiKey;
        DeepSeekModelPicker.SelectedItem = settings.DeepSeekModel;
        DeepSeekThinkingCheckBox.IsChecked = settings.DeepSeekThinking;
    }

    private void ShowSection(View section)
    {
        AgentView.IsVisible = section == AgentView;
        TasksView.IsVisible = section == TasksView;
        FocusView.IsVisible = section == FocusView;
        SettingsView.IsVisible = section == SettingsView;
        AgentTabButton.BackgroundColor = section == AgentView ? Color.FromArgb("#2563EB") : Color.FromArgb("#252B33");
        TasksTabButton.BackgroundColor = section == TasksView ? Color.FromArgb("#2563EB") : Color.FromArgb("#252B33");
        FocusTabButton.BackgroundColor = section == FocusView ? Color.FromArgb("#2563EB") : Color.FromArgb("#252B33");
        SettingsTabButton.BackgroundColor = section == SettingsView ? Color.FromArgb("#2563EB") : Color.FromArgb("#252B33");
    }

    private void ShowAgent_Clicked(object? sender, EventArgs e) => ShowSection(AgentView);
    private void ShowTasks_Clicked(object? sender, EventArgs e) => ShowSection(TasksView);
    private void ShowFocus_Clicked(object? sender, EventArgs e) => ShowSection(FocusView);
    private void ShowSettings_Clicked(object? sender, EventArgs e) => ShowSection(SettingsView);

    private async void TasksView_Refreshing(object? sender, EventArgs e)
    {
        await RefreshDataAsync();
        TasksView.IsRefreshing = false;
    }

    private void HorizonPicker_SelectedIndexChanged(object? sender, EventArgs e) => ApplyHorizonFilter();

    private void TaskList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedTask = e.CurrentSelection.FirstOrDefault() as MobileTask;
        TaskDetailPanel.IsVisible = _selectedTask is not null;
        if (_selectedTask is null) return;
        TaskTitleLabel.Text = _selectedTask.Title;
        TaskMetaLabel.Text = _selectedTask.Meta;
        TaskDescriptionLabel.Text = string.IsNullOrWhiteSpace(_selectedTask.Description) ? "No description." : _selectedTask.Description;
    }

    private async void NewTask_Clicked(object? sender, EventArgs e)
    {
        if (_projects.Count == 0)
        {
            await DisplayAlertAsync("Project", "Sync projects before creating tasks.", "OK");
            return;
        }

        var title = await DisplayPromptAsync("New task", "Title");
        if (string.IsNullOrWhiteSpace(title)) return;
        var projectName = await DisplayActionSheetAsync("Project", "Cancel", null, _projects.Select(p => p.Name).ToArray());
        if (string.IsNullOrWhiteSpace(projectName) || projectName == "Cancel") return;
        var project = _projects.First(p => p.Name == projectName);
        var description = await DisplayPromptAsync("New task", "Description", initialValue: string.Empty) ?? string.Empty;

        await RunBusyAsync(async () =>
        {
            await _dataverse.CreateTaskAsync(new MobileTaskDraft(project.Id, title, description, "DeepWork", 60, DateTime.Today));
            await RefreshDataAsync();
        });
    }

    private async void MarkDone_Clicked(object? sender, EventArgs e)
    {
        if (_selectedTask is null) return;
        await RunBusyAsync(async () =>
        {
            await _dataverse.UpdateTaskStatusAsync(_selectedTask.Id, "Done");
            await RefreshDataAsync();
            TaskDetailPanel.IsVisible = false;
        });
    }

    private async void AddComment_Clicked(object? sender, EventArgs e)
    {
        if (_selectedTask is null) return;
        var comment = await DisplayPromptAsync("Comment", "What do you want to save?", keyboard: Keyboard.Text);
        if (string.IsNullOrWhiteSpace(comment)) return;
        await RunBusyAsync(async () => await _dataverse.AddCommentAsync(_selectedTask.Id, _selectedTask.Title, comment));
    }

    private void StartStop_Clicked(object? sender, EventArgs e)
    {
        if (_timerStartedAt is null)
        {
            if (FocusTaskPicker.SelectedItem is not MobileTask)
            {
                StatusLabel.Text = "Select a task.";
                return;
            }

            _timerStartedAt = DateTime.Now;
            StartStopButton.Text = "Pause";
            _timer = Dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (_, _) => UpdateTimerLabel();
            _timer.Start();
            UpdateTimerLabel();
        }
        else
        {
            _timer?.Stop();
            StartStopButton.Text = "Resume";
        }
    }

    private async void SaveTime_Clicked(object? sender, EventArgs e)
    {
        if (FocusTaskPicker.SelectedItem is not MobileTask task || _timerStartedAt is not DateTime startedAt)
        {
            StatusLabel.Text = "No active time to save.";
            return;
        }

        var endedAt = DateTime.Now;
        var note = FocusNoteEditor.Text?.Trim();
        await RunBusyAsync(async () =>
        {
            await _dataverse.AddTimeEntryAsync(task.Id, task.Title, startedAt, endedAt);
            if (!string.IsNullOrWhiteSpace(note))
                await _dataverse.AddCommentAsync(task.Id, task.Title, note);
        });

        _timer?.Stop();
        _timerStartedAt = null;
        TimerLabel.Text = "00:00:00";
        StartStopButton.Text = "Start";
        FocusNoteEditor.Text = string.Empty;
    }

    private async void SaveSettings_Clicked(object? sender, EventArgs e)
    {
        var secret = NormalizeSecret(SecretEntry.Text);
        if (Guid.TryParse(secret, out _))
        {
            await DisplayAlertAsync("Client secret", "That value looks like a Secret ID. I need the secret VALUE, the one Azure only shows when you create it.", "OK");
            return;
        }

        await _dataverse.SaveSettingsAsync(new MobileSettings(
            TenantEntry.Text ?? string.Empty,
            ClientEntry.Text ?? string.Empty,
            secret,
            EnvironmentEntry.Text ?? string.Empty,
            DeepSeekKeyEntry.Text ?? string.Empty,
            DeepSeekModelPicker.SelectedItem as string ?? "deepseek-v4-flash",
            DeepSeekThinkingCheckBox.IsChecked));
        SecretEntry.Text = secret;
        SecretHintLabel.Text = $"Secret saved ({secret.Length} characters). Testing connection...";
        await RefreshDataAsync();
        ShowSection(TasksView);
    }

    private async void PasteSecret_Clicked(object? sender, EventArgs e)
    {
        var text = await Clipboard.Default.GetTextAsync();
        SecretEntry.Text = NormalizeSecret(text);
        SecretHintLabel.Text = string.IsNullOrEmpty(SecretEntry.Text)
            ? "No secret on the clipboard."
            : $"Secret pasted ({SecretEntry.Text.Length} characters).";
    }

    private async void SendAgentText_Clicked(object? sender, EventArgs e)
    {
        var text = AgentInputEditor.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        AgentInputEditor.Text = string.Empty;
        await SendAgentTextAsync(text, speak: false);
    }

    private async Task SendAgentTextAsync(string text, bool speak)
    {
        try
        {
            _voice.StopSpeaking();
            _agentMessages.Add(new AgentChatMessage("You", text, "#243043"));
            _agentMessages.Add(new AgentChatMessage("Agent", "Thinking...", "#182536"));
            StatusLabel.Text = "Agent thinking...";
            var settings = await _dataverse.GetSettingsAsync();
            var result = await _agent.SendAsync(text, settings, _projects, _allTasks);
            _agentMessages.RemoveAt(_agentMessages.Count - 1);
            _agentMessages.Add(new AgentChatMessage("Agent", result.Message, "#182536"));
            SetAgentPreview(result.Preview);
            StatusLabel.Text = result.Preview is null ? "Agent ready." : "Preview ready.";
            if (speak)
                await _voice.SpeakAsync(result.Message, AgentVoiceRateSlider.Value);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Agent error.";
            _agentMessages.Add(new AgentChatMessage("Agent", ex.Message, "#3A1F26"));
        }
    }

    private async void AgentConversation_Clicked(object? sender, EventArgs e)
    {
        _conversationMode = true;
        _voice.StopSpeaking();
        AgentConversationButton.Text = "Listening";
        await _voice.StartListeningAsync();
    }

    private async void AgentDictate_Clicked(object? sender, EventArgs e)
    {
        _conversationMode = false;
        _voice.StopSpeaking();
        AgentDictateButton.Text = "Dictating";
        await _voice.StartListeningAsync();
    }

    private async void ApplyAgentPreview_Clicked(object? sender, EventArgs e)
    {
        if (_pendingPreview is null) return;
        await RunBusyAsync(async () =>
        {
            await ApplyAgentPreviewAsync(_pendingPreview);
            SetAgentPreview(null);
            await RefreshDataAsync();
            _agentMessages.Add(new AgentChatMessage("Agent", "I've applied the change.", "#182536"));
        });
    }

    private async void EditAgentPreview_Clicked(object? sender, EventArgs e)
    {
        if (_pendingPreview is null) return;
        if (!AgentPreviewEditor.IsVisible)
        {
            AgentPreviewEditor.Text = _agent.BuildEditableText(_pendingPreview);
            AgentPreviewEditor.IsVisible = true;
            return;
        }

        try
        {
            var preview = _agent.FromEditableText(AgentPreviewEditor.Text ?? string.Empty, _projects, _allTasks);
            if (preview is null)
            {
                await DisplayAlertAsync("Preview", "I couldn't read the change. Check the text.", "OK");
                return;
            }

            SetAgentPreview(preview);
            AgentPreviewEditor.IsVisible = false;
            _agentMessages.Add(new AgentChatMessage("Agent", MobileAgentService.BuildPreviewMessage(preview), "#182536"));
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Preview", ex.Message, "OK");
        }
    }

    private void CancelAgentPreview_Clicked(object? sender, EventArgs e)
    {
        SetAgentPreview(null);
        _agentMessages.Add(new AgentChatMessage("Agent", "Preview cancelled.", "#182536"));
    }

    private async Task ApplyAgentPreviewAsync(AgentActionPreview preview)
    {
        switch (preview.Kind)
        {
            case AgentActionKind.CreateTask:
                if (preview.Project is null) throw new InvalidOperationException("Missing the project.");
                await _dataverse.CreateTaskAsync(new MobileTaskDraft(preview.Project.Id, preview.Title, preview.Description, preview.WorkType, preview.EstimatedMinutes, preview.DueDate));
                break;
            case AgentActionKind.CreateAndScheduleTask:
                if (preview.Project is null || preview.Start is null || preview.End is null) throw new InvalidOperationException("Missing project or schedule.");
                var taskId = await _dataverse.CreateTaskAsync(new MobileTaskDraft(preview.Project.Id, preview.Title, preview.Description, preview.WorkType, preview.EstimatedMinutes, preview.DueDate));
                if (taskId != Guid.Empty) await _dataverse.SetTaskScheduleAsync(taskId, preview.Start.Value, preview.End.Value);
                break;
            case AgentActionKind.UpdateTask:
                if (preview.Task is null) throw new InvalidOperationException("Missing the task.");
                await _dataverse.UpdateTaskAsync(new MobileTaskChange(
                    preview.Task.Id,
                    preview.Title == preview.Task.Title ? null : preview.Title,
                    preview.Description == preview.Task.Description ? null : preview.Description,
                    preview.Status == preview.Task.Status ? null : preview.Status,
                    preview.WorkType == preview.Task.WorkType ? null : preview.WorkType,
                    preview.DueDate,
                    preview.EstimatedMinutes > 0 ? preview.EstimatedMinutes : null));
                break;
            case AgentActionKind.DeleteTask:
                if (preview.Task is null) throw new InvalidOperationException("Missing the task.");
                await _dataverse.DeleteTaskAsync(preview.Task.Id);
                break;
            case AgentActionKind.ScheduleTask:
            case AgentActionKind.UpdateCalendar:
                if (preview.Task is null || preview.Start is null || preview.End is null) throw new InvalidOperationException("Missing task or schedule.");
                await _dataverse.SetTaskScheduleAsync(preview.Task.Id, preview.Start.Value, preview.End.Value);
                break;
            case AgentActionKind.DeleteCalendar:
                if (preview.Task is null) throw new InvalidOperationException("Missing the task.");
                await _dataverse.ClearTaskScheduleAsync(preview.Task.Id);
                break;
            case AgentActionKind.AddTaskComment:
                if (preview.Task is null) throw new InvalidOperationException("Missing the task.");
                await _dataverse.AddCommentAsync(preview.Task.Id, preview.Task.Title, preview.Comment);
                break;
        }
    }

    private void SetAgentPreview(AgentActionPreview? preview)
    {
        _pendingPreview = preview;
        AgentPreviewPanel.IsVisible = preview is not null;
        AgentPreviewEditor.IsVisible = false;
        AgentPreviewTitle.Text = preview?.Kind.ToString() ?? string.Empty;
        AgentPreviewSummary.Text = preview?.Summary ?? string.Empty;
    }

    private void WireVoice()
    {
        _voice.PartialText += (_, text) => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_conversationMode) AgentInputEditor.Text = text;
            StatusLabel.Text = text;
        });
        _voice.FinalText += (_, text) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            AgentConversationButton.Text = "Talk";
            AgentDictateButton.Text = "Dictate";
            StatusLabel.Text = "Voice received.";
            if (_conversationMode)
                await SendAgentTextAsync(text, speak: true);
            else
                AgentInputEditor.Text = text;
        });
        _voice.StatusChanged += (_, status) => MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = status);
        _voice.Error += (_, error) => MainThread.BeginInvokeOnMainThread(() =>
        {
            AgentConversationButton.Text = "Talk";
            AgentDictateButton.Text = "Dictate";
            StatusLabel.Text = error;
        });
    }

    private void UpdateTimerLabel()
    {
        if (_timerStartedAt is not DateTime startedAt) return;
        var elapsed = DateTime.Now - startedAt;
        TimerLabel.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        try
        {
            StatusLabel.Text = "Saving...";
            await action();
            StatusLabel.Text = "Done.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Error.";
            await DisplayAlertAsync("JTS", ex.Message, "OK");
        }
    }

    private static string NormalizeSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value.Trim().Trim('"', '\'')
            .Where(c => !char.IsWhiteSpace(c) && c != '\u200B' && c != '\u200C' && c != '\u200D' && c != '\uFEFF');
        return new string(chars.ToArray());
    }
}

public sealed record AgentChatMessage(string Author, string Content, string BubbleColor);

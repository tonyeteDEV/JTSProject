using System.Collections.ObjectModel;
using JTS.Mobile.Services;

namespace JTS.Mobile.Pages;

public partial class AgentPage : ContentPage
{
    private readonly DataverseMobileService _dataverse;
    private readonly MobileAgentService _agent;
    private readonly MobileVoiceService _voice;
    private readonly ObservableCollection<AgentChatMessage> _messages = new();
    private List<MobileTask> _tasks = [];
    private List<MobileProject> _projects = [];
    private AgentActionPreview? _pendingPreview;
    private bool _conversationMode;
    private bool _voiceWired;

    public AgentPage(DataverseMobileService dataverse, MobileAgentService agent, MobileVoiceService voice)
    {
        _dataverse = dataverse;
        _agent = agent;
        _voice = voice;
        InitializeComponent();
        AgentMessageList.ItemsSource = _messages;
        _messages.Add(new AgentChatMessage("Agent", "Tell me what you need. Before changing anything I'll show you a preview to confirm.", "#182536"));
        AgentVoiceRateSlider.ValueChanged += (_, e) => AgentVoiceRateLabel.Text = $"{e.NewValue:0.00}x";
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_voiceWired)
        {
            _voiceWired = true;
            WireVoice();
        }
        try
        {
            _projects = (await _dataverse.GetCachedProjectsAsync()).ToList();
            _tasks = (await _dataverse.GetCachedTasksAsync()).ToList();
        }
        catch { }
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

    private async void OnSendText(object? sender, EventArgs e)
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
            _messages.Add(new AgentChatMessage("You", text, "#243043"));
            _messages.Add(new AgentChatMessage("Agent", "Thinking...", "#182536"));
            StatusLabel.Text = "Agent thinking...";
            var settings = await _dataverse.GetSettingsAsync();
            var result = await _agent.SendAsync(text, settings, _projects, _tasks);
            _messages.RemoveAt(_messages.Count - 1);
            _messages.Add(new AgentChatMessage("Agent", result.Message, "#182536"));
            SetPreview(result.Preview);
            StatusLabel.Text = result.Preview is null ? "Agent ready." : "Preview ready.";
            if (speak)
                await _voice.SpeakAsync(result.Message, AgentVoiceRateSlider.Value);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Agent error.";
            _messages.Add(new AgentChatMessage("Agent", ex.Message, "#3A1F26"));
        }
    }

    private async void OnConversation(object? sender, EventArgs e)
    {
        _conversationMode = true;
        _voice.StopSpeaking();
        AgentConversationButton.Text = "Listening";
        await _voice.StartListeningAsync();
    }

    private async void OnDictate(object? sender, EventArgs e)
    {
        _conversationMode = false;
        _voice.StopSpeaking();
        AgentDictateButton.Text = "Dictating";
        await _voice.StartListeningAsync();
    }

    private async void OnApplyPreview(object? sender, EventArgs e)
    {
        if (_pendingPreview is null) return;
        try
        {
            StatusLabel.Text = "Applying...";
            await ApplyPreviewAsync(_pendingPreview);
            _dataverse.InvalidateCache();
            _tasks = (await _dataverse.GetTasksAsync()).ToList();
            _projects = (await _dataverse.GetCachedProjectsAsync()).ToList();
            SetPreview(null);
            _messages.Add(new AgentChatMessage("Agent", "I've applied the change.", "#182536"));
            StatusLabel.Text = "Done.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Error.";
            await DisplayAlertAsync("JTS", ex.Message, "OK");
        }
    }

    private async void OnEditPreview(object? sender, EventArgs e)
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
            var preview = _agent.FromEditableText(AgentPreviewEditor.Text ?? string.Empty, _projects, _tasks);
            if (preview is null)
            {
                await DisplayAlertAsync("Preview", "I couldn't read the change. Check the text.", "OK");
                return;
            }
            SetPreview(preview);
            AgentPreviewEditor.IsVisible = false;
            _messages.Add(new AgentChatMessage("Agent", MobileAgentService.BuildPreviewMessage(preview), "#182536"));
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Preview", ex.Message, "OK");
        }
    }

    private void OnCancelPreview(object? sender, EventArgs e)
    {
        SetPreview(null);
        _messages.Add(new AgentChatMessage("Agent", "Preview cancelled.", "#182536"));
    }

    private async Task ApplyPreviewAsync(AgentActionPreview preview)
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

    private void SetPreview(AgentActionPreview? preview)
    {
        _pendingPreview = preview;
        AgentPreviewPanel.IsVisible = preview is not null;
        AgentPreviewEditor.IsVisible = false;
        AgentPreviewTitle.Text = preview?.Kind.ToString() ?? string.Empty;
        AgentPreviewSummary.Text = preview?.Summary ?? string.Empty;
    }
}

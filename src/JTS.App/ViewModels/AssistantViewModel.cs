using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using JTS.AI;
using JTS.Core;
using JTS.Data.Entities;
using JTS_App.Services;

namespace JTS_App.ViewModels;

public sealed partial class AssistantViewModel : ObservableObject
{
    private readonly AppSettingsService _settings;
    private readonly AppDataContextService _context;
    private readonly DeepSeekClient _deepSeek;
    private readonly WhisperTranscriber _transcriber;
    private readonly AudioCaptureService _audioCapture;
    private readonly WindowsSpeechDictationService _windowsDictation;
    private readonly SpeechPlaybackService _speechPlayback;
    private readonly LocalVoiceAgentService _localVoice;
    private readonly DataverseAppDataService _data;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;

    public ObservableCollection<ChatMessageView> Messages { get; } = new();
    public ObservableCollection<Project> Projects { get; } = new();
    public ObservableCollection<string> PreviewChecklistItems { get; } = new();
    public ObservableCollection<string> DeepSeekModelOptions { get; } = new()
    {
        "deepseek-v4-flash",
        "deepseek-v4-pro"
    };

    [ObservableProperty] private string _prompt = string.Empty;
    [ObservableProperty] private string _voiceTranscript = string.Empty;
    [ObservableProperty] private string _voiceStatus = "Ready";
    [ObservableProperty] private Project? _selectedVoiceProject;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private bool _isSpeakingResponse;
    [ObservableProperty] private bool _isRealtimeVoiceConnected;
    [ObservableProperty] private string _realtimeVoiceStatus = "Local voice disconnected.";
    [ObservableProperty] private string _realtimeUserTranscript = string.Empty;
    [ObservableProperty] private string _realtimeAssistantTranscript = string.Empty;
    [ObservableProperty] private string _selectedDeepSeekModel = DeepSeekClient.DefaultModel;
    [ObservableProperty] private bool _isDeepSeekThinkingEnabled;
    [ObservableProperty] private double _voiceSpeakingRate = 1.05;
    [ObservableProperty] private string _voiceSpeakingRateLabel = "Voice speed 1.05x";
    [ObservableProperty] private bool _hasTaskPreview;
    [ObservableProperty] private string _previewTitle = string.Empty;
    [ObservableProperty] private string _previewDescription = string.Empty;
    [ObservableProperty] private string _previewProjectName = string.Empty;
    [ObservableProperty] private string _previewWorkType = string.Empty;
    [ObservableProperty] private string _previewPriority = string.Empty;
    [ObservableProperty] private string _previewEstimate = string.Empty;
    [ObservableProperty] private string _previewDueDate = string.Empty;
    [ObservableProperty] private bool _hasAgentActionPreview;
    [ObservableProperty] private string _agentActionTitle = string.Empty;
    [ObservableProperty] private string _agentActionDescription = string.Empty;
    [ObservableProperty] private string _agentActionMeta = string.Empty;

    private TaskDraft? _previewDraft;
    private Project? _previewProject;
    private PendingAgentAction? _pendingAgentAction;
    private VoiceCaptureMode _voiceCaptureMode = VoiceCaptureMode.None;
    private bool _isUsingLiveDictation;

    private string _agentAudioPromptPrefix = string.Empty;
    private int? _liveUserMessageIndex;
    private int? _liveAssistantMessageIndex;
    private int? _pendingPreviewMessageIndex;
    private int? _thinkingMessageIndex;

    public string RecordingButtonText => IsRecording ? "Recording" : "Record";
    public string RecordingButtonGlyph => IsRecording ? "\uE71A" : "\uE720";
    public string VoiceConversationButtonText => IsRecording && _voiceCaptureMode == VoiceCaptureMode.ConversationTurn ? "Stop and send" : "Speak";
    public string VoiceConversationButtonGlyph => IsRecording && _voiceCaptureMode == VoiceCaptureMode.ConversationTurn ? "\uE71A" : "\uE720";
    public string VoiceResponseState => IsSpeakingResponse ? "Speaking response..." : VoiceStatus;
    public string AgentAudioButtonText => IsRecording && _voiceCaptureMode == VoiceCaptureMode.AgentMessage ? "Stop recording" : "Record audio";
    public string AgentAudioButtonGlyph => IsRecording && _voiceCaptureMode == VoiceCaptureMode.AgentMessage ? "\uE71A" : "\uE720";
    public bool CanUseAgentAudioButton => !IsRealtimeVoiceConnected && !IsBusy;
    public string RealtimeVoiceButtonText => IsRealtimeVoiceConnected ? "Stop and send" : "Start conversation";
    public string RealtimeVoiceButtonGlyph => IsRealtimeVoiceConnected ? "\uE71A" : "\uE720";

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUseAgentAudioButton));
    }

    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordingButtonText));
        OnPropertyChanged(nameof(RecordingButtonGlyph));
        OnPropertyChanged(nameof(VoiceConversationButtonText));
        OnPropertyChanged(nameof(VoiceConversationButtonGlyph));
        OnPropertyChanged(nameof(AgentAudioButtonText));
        OnPropertyChanged(nameof(AgentAudioButtonGlyph));
    }

    partial void OnIsSpeakingResponseChanged(bool value)
    {
        OnPropertyChanged(nameof(VoiceResponseState));
    }

    partial void OnVoiceStatusChanged(string value)
    {
        OnPropertyChanged(nameof(VoiceResponseState));
    }

    partial void OnIsRealtimeVoiceConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(RealtimeVoiceButtonText));
        OnPropertyChanged(nameof(RealtimeVoiceButtonGlyph));
        OnPropertyChanged(nameof(CanUseAgentAudioButton));
    }

    partial void OnVoiceSpeakingRateChanged(double value)
    {
        VoiceSpeakingRateLabel = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Voice speed {value:0.00}x");
    }

    public AssistantViewModel(
        AppSettingsService settings,
        AppDataContextService context,
        DeepSeekClient deepSeek,
        WhisperTranscriber transcriber,
        AudioCaptureService audioCapture,
        WindowsSpeechDictationService windowsDictation,
        SpeechPlaybackService speechPlayback,
        LocalVoiceAgentService localVoice,
        DataverseAppDataService data)
    {
        _settings = settings;
        _context = context;
        _deepSeek = deepSeek;
        _transcriber = transcriber;
        _audioCapture = audioCapture;
        _windowsDictation = windowsDictation;
        _speechPlayback = speechPlayback;
        _localVoice = localVoice;
        _data = data;
        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _windowsDictation.TextChanged += OnWindowsDictationTextChanged;
        WireLocalVoiceEvents();
    }

    public async Task LoadAsync(bool forceSync = false)
    {
        var snapshot = await _data.LoadTaskSnapshotAsync(forceSync);
        var projects = snapshot.Projects.OrderBy(p => p.Name).ToList();
        Projects.Clear();
        foreach (var project in projects) Projects.Add(project);
        SelectedVoiceProject ??= Projects.FirstOrDefault();

        var rateSetting = await _settings.GetVoiceSpeakingRateAsync();
        VoiceSpeakingRate = double.TryParse(rateSetting, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate)
            ? Math.Clamp(rate, 0.75, 1.45)
            : 1.05;

        var model = await _settings.GetDeepSeekModelAsync();
        SelectedDeepSeekModel = model is not null && DeepSeekModelOptions.Contains(model) ? model : DeepSeekClient.DefaultModel;
        IsDeepSeekThinkingEnabled = string.Equals(await _settings.GetDeepSeekThinkingEnabledAsync(), "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SaveVoiceSpeakingRateAsync(double value)
    {
        VoiceSpeakingRate = Math.Clamp(Math.Round(value / 0.05) * 0.05, 0.75, 1.45);
        await _settings.SetVoiceSpeakingRateAsync(VoiceSpeakingRate);
    }

    public async Task SaveDeepSeekModelAsync(string? model)
    {
        SelectedDeepSeekModel = model is not null && DeepSeekModelOptions.Contains(model) ? model : DeepSeekClient.DefaultModel;
        await _settings.SetDeepSeekModelAsync(SelectedDeepSeekModel);
    }

    public async Task SaveDeepSeekThinkingAsync(bool value)
    {
        IsDeepSeekThinkingEnabled = value;
        await _settings.SetDeepSeekThinkingEnabledAsync(value);
    }

    private DeepSeekRequestOptions GetDeepSeekOptions() =>
        new(SelectedDeepSeekModel, IsDeepSeekThinkingEnabled);

    public async Task ToggleRealtimeVoiceAsync()
    {
        if (IsBusy) return;

        if (IsRealtimeVoiceConnected)
        {
            RealtimeVoiceStatus = "Sending voice turn...";
            await _localVoice.FinishManualTurnAsync();
            IsRealtimeVoiceConnected = false;
            return;
        }

        IsBusy = true;
        try
        {
            RealtimeVoiceStatus = "Starting local voice...";
            RealtimeUserTranscript = string.Empty;
            RealtimeAssistantTranscript = string.Empty;
            await _localVoice.StartManualTurnAsync();
            IsRealtimeVoiceConnected = true;
        }
        catch (Exception ex)
        {
            RealtimeVoiceStatus = $"Local voice error: {ex.Message}";
            IsRealtimeVoiceConnected = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(Prompt) || IsBusy) return;
        var userText = Prompt.Trim();
        Prompt = string.Empty;

        if (IsRealtimeVoiceConnected)
        {
            await _localVoice.SendTextAsync(userText);
            return;
        }

        IsBusy = true;

        try
        {
            await SendUserTextToAgentAsync(userText, VoiceTurnMode.Text);
        }
        catch (Exception ex)
        {
            ClearThinkingMessage();
            Messages.Add(new ChatMessageView("Assistant", $"Error: {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<string?> SendUserTextToAgentAsync(string userText, VoiceTurnMode mode)
    {
        Messages.Add(new ChatMessageView("You", userText));
        var thinkingIndex = ShowThinkingMessage();

        var apiKey = await _settings.GetDeepSeekApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            const string missingKeyMessage = "Add your DeepSeek API key in Settings first.";
            ReplaceThinkingMessage(thinkingIndex, new ChatMessageView("Assistant", missingKeyMessage));
            await TryBuildAgentActionPreviewAsync(userText);
            return missingKeyMessage;
        }

        if (mode == VoiceTurnMode.Text && await TryBuildAgentActionPreviewAsync(userText))
        {
            var previewResponse = BuildAgentPreviewIntro(_pendingAgentAction);
            RemoveThinkingMessage(thinkingIndex);
            await SaveConversationMessageAsync("user", userText);
            await SaveConversationMessageAsync("assistant", previewResponse);
            return previewResponse;
        }

        var contextText = mode == VoiceTurnMode.Spoken
            ? await _context.BuildVoiceAssistantContextAsync()
            : await _context.BuildAssistantContextAsync();
        var systemPrompt = mode == VoiceTurnMode.Spoken
            ? "You are JTS's voice agent. Respond in natural, human, conversational English unless the user uses another language. Keep it short, like a person: one or two sentences usually. Use the internal context without showing IDs, keys, field names, or technical details. If the user asks for an action, confirm that you'll prepare the preview before saving."
            : "You are the JTS assistant. Respond in natural, human, conversational English, like a colleague who understands the user's work. Use the app data and recent conversation history to resolve references like 'that task', 'that project', 'the MobileApp one', or answers to questions you just asked. Distinguish between planned calendar, actual tracked time, comments, and timesheets. Don't show internal IDs, database keys, field names, or technical details unless the user explicitly asks for them. If you list tasks, use titles, dates, times, and project when it adds context; don't use #IDs or heavy Markdown. If there are several tasks, group them clearly and briefly. If the user fills in data for a pending action, continue that action and let the app show the preview before saving to Dataverse.";
        var messages = new List<DeepSeekMessage>
        {
            new DeepSeekMessage("system", systemPrompt),
            new DeepSeekMessage("system", contextText)
        };
        messages.AddRange(BuildRecentConversationMessages(userText));
        messages.Add(new DeepSeekMessage("user", userText));

        var response = await _deepSeek.ChatAsync(apiKey, messages, GetDeepSeekOptions());
        ReplaceThinkingMessage(thinkingIndex, new ChatMessageView("Assistant", response));
        await SaveConversationMessageAsync("user", userText);
        await SaveConversationMessageAsync("assistant", response);
        return response;
    }

    public void StartRecording()
    {
        StartRecording(VoiceCaptureMode.TaskDraft, "Recording...");
    }

    private void StartRecording(VoiceCaptureMode mode, string status)
    {
        _audioCapture.Start();
        _voiceCaptureMode = mode;
        IsRecording = true;
        VoiceStatus = status;
        OnPropertyChanged(nameof(VoiceConversationButtonText));
        OnPropertyChanged(nameof(VoiceConversationButtonGlyph));
    }

    public async Task StopRecordingAndTranscribeAsync()
    {
        if (_voiceCaptureMode == VoiceCaptureMode.ConversationTurn)
        {
            VoiceStatus = "Finish the agent voice turn with the Speak button.";
            return;
        }

        var wav = await _audioCapture.StopAsync();
        IsRecording = false;
        _voiceCaptureMode = VoiceCaptureMode.None;
        OnPropertyChanged(nameof(VoiceConversationButtonText));
        OnPropertyChanged(nameof(VoiceConversationButtonGlyph));
        if (wav is null)
        {
            VoiceStatus = "No recording found.";
            return;
        }

        var model = await _settings.GetWhisperModelPathAsync();
        if (string.IsNullOrWhiteSpace(model))
        {
            VoiceStatus = $"Audio saved: {wav}. Configure a Whisper .bin model in Settings to transcribe.";
            return;
        }

        IsBusy = true;
        try
        {
            VoiceStatus = "Transcribing...";
            VoiceTranscript = await _transcriber.TranscribeAsync(model, wav);
            ClearPreview();
            VoiceStatus = "Transcript ready. Review the text, then create a preview.";
        }
        catch (Exception ex)
        {
            VoiceStatus = $"Transcription error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ToggleRecordingAsync()
    {
        if (IsBusy) return;

        if (IsRecording)
        {
            if (_voiceCaptureMode != VoiceCaptureMode.TaskDraft)
            {
                VoiceStatus = "Finish the current agent voice turn first.";
                return;
            }

            await StopRecordingAndTranscribeAsync();
        }
        else
            StartRecording();
    }

    public async Task ToggleVoiceConversationAsync()
    {
        if (IsBusy) return;

        if (IsRecording)
        {
            if (_voiceCaptureMode != VoiceCaptureMode.ConversationTurn)
            {
                VoiceStatus = "Finish the current task recording first.";
                return;
            }

            await StopVoiceConversationTurnAsync();
            return;
        }

        _speechPlayback.Stop();
        StartRecording(VoiceCaptureMode.ConversationTurn, "Listening. Press Stop and send when you finish speaking.");
    }

    public async Task ToggleAgentAudioAsync()
    {
        if (IsRealtimeVoiceConnected)
        {
            RealtimeVoiceStatus = "Stop the live conversation before recording a single audio turn.";
            return;
        }

        if (IsBusy) return;

        if (IsRecording)
        {
            if (_voiceCaptureMode != VoiceCaptureMode.AgentMessage)
            {
                VoiceStatus = "Finish the current recording first.";
                return;
            }

            await StopAgentAudioTurnAsync();
            return;
        }

        _speechPlayback.Stop();
        _voiceCaptureMode = VoiceCaptureMode.AgentMessage;
        IsRecording = true;
        _agentAudioPromptPrefix = Prompt;
        RealtimeVoiceStatus = "Starting voice dictation...";
        OnPropertyChanged(nameof(AgentAudioButtonText));
        OnPropertyChanged(nameof(AgentAudioButtonGlyph));

        try
        {
            await _localVoice.StartDictationAsync();
            _isUsingLiveDictation = true;
        }
        catch (Exception ex)
        {
            _isUsingLiveDictation = false;
            RealtimeVoiceStatus = $"Voice dictation unavailable, recording audio: {ex.Message}";
            _audioCapture.Start();
        }
    }

    private async Task StopAgentAudioTurnAsync()
    {
        string? transcript = null;
        string? wav = null;

        if (_isUsingLiveDictation)
        {
            transcript = await _localVoice.StopDictationAsync();
            _isUsingLiveDictation = false;
            Prompt = BuildDictationText(_agentAudioPromptPrefix, transcript);
        }
        else
        {
            wav = await _audioCapture.StopAsync();
        }

        IsRecording = false;
        _voiceCaptureMode = VoiceCaptureMode.None;
        OnPropertyChanged(nameof(VoiceConversationButtonText));
        OnPropertyChanged(nameof(VoiceConversationButtonGlyph));
        OnPropertyChanged(nameof(AgentAudioButtonText));
        OnPropertyChanged(nameof(AgentAudioButtonGlyph));

        if (wav is null && _voiceCaptureMode == VoiceCaptureMode.AgentMessage && string.IsNullOrWhiteSpace(transcript))
        {
            RealtimeVoiceStatus = "No recording found.";
            return;
        }

        if (wav is null)
        {
            RealtimeVoiceStatus = string.IsNullOrWhiteSpace(transcript)
                ? "No speech detected."
                : "Dictation stopped. Review it and press Send.";
            return;
        }

        if (string.IsNullOrWhiteSpace(transcript))
        {
            var model = await _settings.GetWhisperModelPathAsync();
            if (string.IsNullOrWhiteSpace(model))
            {
                RealtimeVoiceStatus = $"Audio saved: {wav}. Configure a Whisper .bin model in Settings to transcribe.";
                return;
            }

            IsBusy = true;
            try
            {
                RealtimeVoiceStatus = "Transcribing fallback audio...";
                transcript = await _transcriber.TranscribeAsync(model, wav!);
            }
            finally
            {
                IsBusy = false;
            }
        }

        try
        {
            if (string.IsNullOrWhiteSpace(transcript))
            {
                RealtimeVoiceStatus = "No speech detected.";
                return;
            }

            var cleanTranscript = transcript.Trim();
            Prompt = string.IsNullOrWhiteSpace(Prompt)
                ? cleanTranscript
                : $"{Prompt.TrimEnd()} {cleanTranscript}";
            RealtimeVoiceStatus = "Audio transcribed. Review it and press Send.";
        }
        catch (Exception ex)
        {
            RealtimeVoiceStatus = $"Audio transcription error: {ex.Message}";
        }
    }

    private void OnWindowsDictationTextChanged(object? sender, SpeechDictationTextChangedEventArgs e)
    {
        if (_voiceCaptureMode != VoiceCaptureMode.AgentMessage || !_isUsingLiveDictation) return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            Prompt = BuildDictationText(_agentAudioPromptPrefix, e.Text);
            RealtimeVoiceStatus = string.IsNullOrWhiteSpace(e.Text)
                ? "Listening with Windows speech..."
                : "Dictating into the message...";
        });
    }

    private static string BuildDictationText(string prefix, string? dictation)
    {
        var cleanPrefix = prefix.TrimEnd();
        var cleanDictation = dictation?.Trim();
        if (string.IsNullOrWhiteSpace(cleanPrefix)) return cleanDictation ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cleanDictation)) return cleanPrefix;
        return $"{cleanPrefix} {cleanDictation}";
    }

    private async Task StopVoiceConversationTurnAsync()
    {
        var wav = await _audioCapture.StopAsync();
        IsRecording = false;
        _voiceCaptureMode = VoiceCaptureMode.None;
        OnPropertyChanged(nameof(VoiceConversationButtonText));
        OnPropertyChanged(nameof(VoiceConversationButtonGlyph));
        if (wav is null)
        {
            VoiceStatus = "No recording found.";
            return;
        }

        var model = await _settings.GetWhisperModelPathAsync();
        if (string.IsNullOrWhiteSpace(model))
        {
            VoiceStatus = $"Audio saved: {wav}. Configure a Whisper .bin model in Settings to transcribe.";
            return;
        }

        IsBusy = true;
        try
        {
            VoiceStatus = "Transcribing your voice turn...";
            var transcript = await _transcriber.TranscribeAsync(model, wav);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                VoiceStatus = "No speech detected.";
                return;
            }

            VoiceTranscript = transcript;
            ClearPreview();
            VoiceStatus = "Sending voice turn to agent...";
            var response = await SendUserTextToAgentAsync(transcript.Trim(), VoiceTurnMode.Spoken);
            if (!string.IsNullOrWhiteSpace(response))
            {
                VoiceStatus = "Speaking response...";
                IsSpeakingResponse = true;
                await _speechPlayback.SpeakAsync(response);
            }

            VoiceStatus = "Preparing action preview if needed...";
            await TryBuildAgentActionPreviewAsync(transcript.Trim());
            VoiceStatus = "Voice turn complete. Press Speak for the next turn.";
        }
        catch (Exception ex)
        {
            VoiceStatus = $"Voice conversation error: {ex.Message}";
        }
        finally
        {
            IsSpeakingResponse = false;
            IsBusy = false;
        }
    }

    public async Task<bool> BuildTaskPreviewAsync()
    {
        if (string.IsNullOrWhiteSpace(VoiceTranscript))
        {
            VoiceStatus = "Write or transcribe something first.";
            return false;
        }

        if (Projects.Count == 0)
        {
            VoiceStatus = "Create a project first.";
            return false;
        }

        IsBusy = true;
        try
        {
            var draft = await ParseTaskDraftAsync(VoiceTranscript.Trim());
            var defaultProject = SelectedVoiceProject ?? Projects.First();
            var project = Projects.FirstOrDefault(p => draft.ProjectHint is not null && p.Name.Contains(draft.ProjectHint, StringComparison.OrdinalIgnoreCase))
                ?? defaultProject;

            SetPreview(draft, project);
            VoiceStatus = "Preview ready. Confirm to create the task.";
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<int> ConfirmPreviewTaskAsync()
    {
        if (_previewDraft is null || _previewProject is null)
        {
            VoiceStatus = "Create a task preview first.";
            return 0;
        }

        IsBusy = true;
        try
        {
            var task = new TaskItem
            {
                ProjectId = _previewProject.Id,
                Project = _previewProject,
                Title = _previewDraft.Title,
                Description = _previewDraft.Description,
                WorkType = _previewDraft.WorkType,
                Priority = _previewDraft.Priority,
                EstimatedPomodoros = _previewDraft.EstimatedPomodoros,
                DueDate = _previewDraft.DueDate,
                Status = TaskItemStatus.Todo
            };

            if (_previewProject.DataverseId is not Guid projectDataverseId)
                throw new InvalidOperationException("Project is not linked to Dataverse.");
            await _data.CreateTaskAsync(task, projectDataverseId);

            VoiceTranscript = string.Empty;
            ClearPreview();
            VoiceStatus = "Task created. Ready for the next capture.";
            return 1;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<int> CreateTasksFromTranscriptAsync()
    {
        if (!HasTaskPreview)
        {
            var previewReady = await BuildTaskPreviewAsync();
            return previewReady ? 0 : 0;
        }

        return await ConfirmPreviewTaskAsync();
    }

    public async Task SendVoiceTranscriptToAgentAsync()
    {
        if (string.IsNullOrWhiteSpace(VoiceTranscript))
        {
            VoiceStatus = "Record, transcribe, or paste something first.";
            return;
        }

        Prompt = VoiceTranscript.Trim();
        VoiceTranscript = string.Empty;
        ClearPreview();
        VoiceStatus = "Sent to agent.";
        await SendAsync();
    }

    public async Task ApplyAgentActionAsync(PendingAgentAction? action = null)
    {
        action ??= _pendingAgentAction;
        if (action is null)
        {
            Messages.Add(new ChatMessageView("Assistant", "No action is ready to apply."));
            return;
        }

        IsBusy = true;
        try
        {
            var summary = await ApplyPendingAgentActionAsync(action);
            ClearAgentActionPreview();
            Messages.Add(new ChatMessageView("Assistant", summary));
            await SaveConversationMessageAsync("assistant", summary);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessageView("Assistant", $"I could not apply that action: {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void CancelAgentActionPreview(PendingAgentAction? action = null)
    {
        if (action is not null && _pendingAgentAction is not null && !IsSameAction(action, _pendingAgentAction))
            return;

        ClearAgentActionPreview();
        Messages.Add(new ChatMessageView("Assistant", "Preview cancelled."));
    }

    public string GetEditableAgentActionText(PendingAgentAction action)
    {
        var dto = ToDto(action);
        return JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<bool> ReplaceAgentActionPreviewFromTextAsync(string editableText)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<AgentActionDto>(
                ExtractJson(editableText),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto is null) return false;

            var action = await BuildPendingActionAsync(dto);
            if (action is null) return false;

            ClearAgentActionPreview();
            SetAgentActionPreview(action);
            return true;
        }
        catch (Exception ex)
        {
            App.Log("[AssistantViewModel] Editable preview parse failed: " + ex);
            return false;
        }
    }

    public async Task<bool> ReplaceAgentActionPreviewAsync(AgentActionDto dto)
    {
        var action = await BuildPendingActionAsync(dto);
        if (action is null) return false;

        ClearAgentActionPreview();
        SetAgentActionPreview(action);
        return true;
    }

    private IReadOnlyList<DeepSeekMessage> BuildRecentConversationMessages(string? latestUserTextToExclude = null, int maxMessages = 12)
    {
        var items = Messages.ToList();
        if (!string.IsNullOrWhiteSpace(latestUserTextToExclude) &&
            items.LastOrDefault() is { Author: "You" } last &&
            string.Equals(last.Content, latestUserTextToExclude, StringComparison.Ordinal))
        {
            items.RemoveAt(items.Count - 1);
        }

        return items
            .Where(m => !string.IsNullOrWhiteSpace(m.Content) && !string.Equals(m.Content, "Thinking...", StringComparison.Ordinal))
            .TakeLast(maxMessages)
            .Select(m => new DeepSeekMessage(m.Author == "You" ? "user" : "assistant", m.Content))
            .ToList();
    }

    private int ShowThinkingMessage()
    {
        Messages.Add(new ChatMessageView("Assistant", "Thinking..."));
        _thinkingMessageIndex = Messages.Count - 1;
        return _thinkingMessageIndex.Value;
    }

    private void ReplaceThinkingMessage(int index, ChatMessageView message)
    {
        if (index >= 0 && index < Messages.Count && _thinkingMessageIndex == index)
            Messages[index] = message;
        else
            Messages.Add(message);

        if (_thinkingMessageIndex == index)
            _thinkingMessageIndex = null;
    }

    private void RemoveThinkingMessage(int index)
    {
        if (index >= 0 && index < Messages.Count && _thinkingMessageIndex == index)
            Messages.RemoveAt(index);

        if (_thinkingMessageIndex == index)
            _thinkingMessageIndex = null;
    }

    private void ClearThinkingMessage()
    {
        if (_thinkingMessageIndex is int index)
            RemoveThinkingMessage(index);
    }

    private void WireLocalVoiceEvents()
    {
        _localVoice.StatusChanged += (_, status) => EnqueueUi(() => RealtimeVoiceStatus = status);
        _localVoice.DictationTextChanged += (_, text) => EnqueueUi(() =>
        {
            Prompt = BuildDictationText(_agentAudioPromptPrefix, text);
            RealtimeVoiceStatus = string.IsNullOrWhiteSpace(text) ? "Dictating..." : "Dictating into message...";
        });
        _localVoice.UserTranscriptDelta += (_, transcript) => EnqueueUi(() =>
        {
            RealtimeUserTranscript = transcript;
            UpsertLiveMessage("You", transcript, ref _liveUserMessageIndex);
        });
        _localVoice.Error += (_, error) => EnqueueUi(() =>
        {
            RealtimeVoiceStatus = $"Local voice error: {error}";
            Messages.Add(new ChatMessageView("Assistant", $"Local voice error: {error}"));
        });
        _localVoice.UserTranscriptCompleted += (_, transcript) => EnqueueUi(async () =>
        {
            RealtimeUserTranscript = transcript;
            if (_liveUserMessageIndex is int index && index >= 0 && index < Messages.Count)
            {
                Messages[index] = new ChatMessageView("You", transcript);
                _liveUserMessageIndex = null;
                await SaveConversationMessageAsync("user", transcript);
            }
            else if (!Messages.Any(m => m.Author == "You" && string.Equals(m.Content, transcript, StringComparison.Ordinal)))
            {
                Messages.Add(new ChatMessageView("You", transcript));
                await SaveConversationMessageAsync("user", transcript);
            }

            await TryBuildAgentActionPreviewAsync(transcript);
        });
        _localVoice.AssistantTranscriptCompleted += (_, transcript) => EnqueueUi(async () =>
        {
            RealtimeAssistantTranscript = string.Empty;
            if (_liveAssistantMessageIndex is int index && index >= 0 && index < Messages.Count)
            {
                Messages[index] = new ChatMessageView("Assistant", transcript);
                _liveAssistantMessageIndex = null;
            }
            else
            {
                Messages.Add(new ChatMessageView("Assistant", transcript));
            }

            await SaveConversationMessageAsync("assistant", transcript);
        });
    }

    private void UpsertLiveMessage(string author, string content, ref int? index)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        if (index is int existing && existing >= 0 && existing < Messages.Count && Messages[existing].Author == author)
        {
            Messages[existing] = new ChatMessageView(author, content);
            return;
        }

        Messages.Add(new ChatMessageView(author, content));
        index = Messages.Count - 1;
    }

    private void EnqueueUi(Action action)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception ex) { App.Log("[AssistantViewModel] UI realtime event failed: " + ex); }
        });
    }

    private void EnqueueUi(Func<Task> action)
    {
        _dispatcherQueue.TryEnqueue(async () =>
        {
            try { await action(); }
            catch (Exception ex) { App.Log("[AssistantViewModel] UI realtime async event failed: " + ex); }
        });
    }

    private async Task<bool> TryBuildAgentActionPreviewAsync(string text)
    {
        if (string.Equals(await _settings.GetAsync("AI.AllowNaturalLanguageAgentActions"), "true", StringComparison.OrdinalIgnoreCase))
        {
            var aiAction = await TryBuildAiAgentActionAsync(text);
            if (aiAction is not null)
            {
                ClearAgentActionPreview();
                SetAgentActionPreview(aiAction);
                return true;
            }
        }

        var action = await ParseLocalActionAsync(text);
        if (action is null) return false;
        ClearAgentActionPreview();
        SetAgentActionPreview(action);
        return true;
    }

    private async Task<PendingAgentAction?> TryBuildAiAgentActionAsync(string text)
    {
        var apiKey = await _settings.GetDeepSeekApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        try
        {
            var actionPrompt = string.Join(Environment.NewLine, new[]
            {
                "Infer one JTS Dataverse app action from the user's natural-language message and recent conversation history.",
                $"Today is {DateTime.Now:yyyy-MM-dd}. Use local time.",
                "Return only one JSON object:",
                "{",
                "  \"action\": \"None|CreateTask|UpdateTask|DeleteTask|ScheduleTask|CreateAndScheduleTask|UpdateCalendar|DeleteCalendar|AddTaskComment|DeleteTaskComment|AddTimeEntry|UpdateTimeEntry|DeleteTimeEntry\",",
                "  \"title\": \"task title for new tasks\",",
                "  \"description\": \"optional task description\",",
                "  \"projectHint\": \"project name/id fragment mentioned by the user\",",
                "  \"taskId\": 123,",
                "  \"taskHint\": \"task title fragment mentioned by the user\",",
                "  \"scheduleBlockId\": 123,",
                "  \"journalEntryId\": 123,",
                "  \"timeEntryId\": 123,",
                "  \"comment\": \"comment to add\",",
                "  \"start\": \"yyyy-MM-dd HH:mm or null\",",
                "  \"end\": \"yyyy-MM-dd HH:mm or null\",",
                "  \"dueDate\": \"yyyy-MM-dd or null\",",
                "  \"workType\": \"DeepWork|Admin|Meeting|Learning|Communication|Planning|Other\",",
                "  \"priority\": \"Low|Medium|High|Critical\",",
                "  \"status\": \"Backlog|Todo|Assigned|InProgress|Blocked|Done|Cancelled\"",
                "}",
                "Use recent conversation history and the app data snapshot to resolve follow-up references and missing details.",
                "Every app-changing action must become a preview. Never imply that a change was already applied.",
                "The source of truth is Dataverse; local ids in the snapshot are only temporary handles for previews.",
                "Calendar actions manage planned schedule blocks. Time-entry actions manage real dedication/work already performed.",
                "If the assistant just asked for a missing project, date, time, task, title, or comment and the latest user message provides it, infer the original pending action.",
                "When creating a task, put the complete proposed task content in the JSON itself. Use title for the task name and description for all useful details, objectives, acceptance criteria, context, and notes requested by the user.",
                "If the user asks for an invented or example task to create, still return CreateTask with a concrete high-quality proposal instead of only explaining it.",
                "For due-date, title, description, project, priority, work type, or status changes, use UpdateTask.",
                "For adding a task to the calendar, use ScheduleTask. For changing or moving an existing calendar block, use UpdateCalendar. For removing a calendar block, use DeleteCalendar.",
                "For adding or deleting comments, use AddTaskComment or DeleteTaskComment. Keep comments literal; do not invent details.",
                "For real dedication/tracked time, use AddTimeEntry, UpdateTimeEntry, or DeleteTimeEntry. Use start/end as the actual worked interval.",
                "Timesheets are created from pending tracked time and task comments only; do not create or change timesheets from the assistant unless the UI specifically supports it.",
                "If the user asks to change the preview you just proposed, return a replacement action with the corrected values.",
                "Use action None only when neither the latest user message nor the recent conversation clearly indicates one of the supported app-changing actions.",
                "Do not say anything except JSON."
            });
            var messages = new List<DeepSeekMessage>
            {
                new DeepSeekMessage("system", actionPrompt),
                new DeepSeekMessage("system", await _context.BuildAssistantContextAsync())
            };
            messages.AddRange(BuildRecentConversationMessages(text));
            messages.Add(new DeepSeekMessage("user", text));

            var response = await _deepSeek.ChatAsync(apiKey, messages, GetDeepSeekOptions());

            var dto = JsonSerializer.Deserialize<AgentActionDto>(
                ExtractJson(response),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return dto is null ? null : await BuildPendingActionAsync(dto);
        }
        catch (Exception ex)
        {
            App.Log("[AssistantViewModel] AI action inference failed: " + ex);
            return null;
        }
    }

    private async Task<PendingAgentAction?> BuildPendingActionAsync(AgentActionDto dto)
    {
        var kind = ParseAction(dto.Action);
        if (kind == AgentActionKind.None) return null;

        var snapshot = await _data.LoadTaskSnapshotAsync();
        var project = ResolveProject(snapshot.Projects, dto.ProjectHint);
        if (kind is AgentActionKind.CreateTask or AgentActionKind.CreateAndScheduleTask)
            project ??= SelectedVoiceProject ?? Projects.FirstOrDefault();
        var start = ParseDateTime(dto.Start);
        var end = ParseDateTime(dto.End);
        var dueDate = ParseDate(dto.DueDate);
        var task = ResolveTask(snapshot.Tasks, dto.TaskId, dto.TaskHint);
        var scheduleBlock = ResolveScheduleBlock(task, dto.ScheduleBlockId);
        var journalEntry = await ResolveJournalEntryAsync(task, dto.JournalEntryId, dto.Comment);
        var timeEntry = await ResolveTimeEntryAsync(task, dto.TimeEntryId, dueDate ?? start?.Date);
        task ??= timeEntry?.TaskItem;

        if (start is not null && end is null) end = start.Value.AddHours(1);
        if (start is not null && end <= start) end = start.Value.AddHours(1);

        if (kind is AgentActionKind.CreateTask or AgentActionKind.CreateAndScheduleTask &&
            (project is null || string.IsNullOrWhiteSpace(dto.Title)))
        {
            return null;
        }

        if ((kind is AgentActionKind.UpdateTask or AgentActionKind.DeleteTask or AgentActionKind.ScheduleTask or AgentActionKind.UpdateCalendar or AgentActionKind.DeleteCalendar or AgentActionKind.AddTaskComment or AgentActionKind.DeleteTaskComment or AgentActionKind.AddTimeEntry) && task is null) return null;
        if ((kind is AgentActionKind.ScheduleTask or AgentActionKind.CreateAndScheduleTask or AgentActionKind.UpdateCalendar) && (start is null || end is null)) return null;
        if (kind == AgentActionKind.AddTimeEntry && (start is null || end is null)) return null;
        if (kind == AgentActionKind.UpdateTimeEntry && timeEntry is null) return null;
        if (kind == AgentActionKind.DeleteTimeEntry && timeEntry is null) return null;
        if (kind == AgentActionKind.AddTaskComment && string.IsNullOrWhiteSpace(dto.Comment)) return null;
        if (kind is AgentActionKind.UpdateCalendar or AgentActionKind.DeleteCalendar && scheduleBlock is null && task?.ScheduledStart is null) return null;
        if (kind == AgentActionKind.DeleteTaskComment && journalEntry is null) return null;
        if (kind == AgentActionKind.UpdateTimeEntry)
        {
            var existingTimeEntry = timeEntry!;
            start ??= existingTimeEntry.StartedAt;
            end ??= existingTimeEntry.EndedAt ?? existingTimeEntry.StartedAt.AddMinutes(Math.Max(1, existingTimeEntry.ActualMinutes));
        }
        if (kind == AgentActionKind.UpdateTask &&
            string.IsNullOrWhiteSpace(dto.Title) &&
            dto.Description is null &&
            project is null &&
            dueDate is null &&
            string.IsNullOrWhiteSpace(dto.WorkType) &&
            string.IsNullOrWhiteSpace(dto.Priority) &&
            string.IsNullOrWhiteSpace(dto.Status))
        {
            return null;
        }

        Enum.TryParse(dto.WorkType, true, out WorkType workType);
        Enum.TryParse(dto.Priority, true, out TaskPriority priority);
        Enum.TryParse(dto.Status, true, out TaskItemStatus status);

        return new PendingAgentAction(
            kind,
            dto.Title?.Trim(),
            dto.Description?.Trim(),
            project,
            task,
            dto.Comment?.Trim(),
            start,
            end,
            dueDate,
            string.IsNullOrWhiteSpace(dto.WorkType) ? WorkType.DeepWork : workType,
            string.IsNullOrWhiteSpace(dto.Priority) ? TaskPriority.Medium : priority,
            scheduleBlock?.Id ?? dto.ScheduleBlockId,
            journalEntry?.Id ?? dto.JournalEntryId,
            string.IsNullOrWhiteSpace(dto.Status) ? null : status,
            !string.IsNullOrWhiteSpace(dto.WorkType),
            !string.IsNullOrWhiteSpace(dto.Priority),
            timeEntry?.Id ?? dto.TimeEntryId);
    }

    private async Task<PendingAgentAction?> ParseLocalActionAsync(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;

        var snapshot = await _data.LoadTaskSnapshotAsync();
        var commentMatch = Regex.Match(trimmed, @"(?i)^(?:comment|comenta|add comment|añade comentario)\s+(?:task|tarea)?\s*#?(?<id>\d+)\s*[:\-]\s*(?<comment>.+)$");
        if (commentMatch.Success)
        {
            var task = ResolveTask(snapshot.Tasks, int.Parse(commentMatch.Groups["id"].Value), null);
            return task is null
                ? null
                : new PendingAgentAction(AgentActionKind.AddTaskComment, null, null, null, task, commentMatch.Groups["comment"].Value.Trim(), null, null, null, WorkType.DeepWork, TaskPriority.Medium);
        }

        var scheduleMatch = Regex.Match(trimmed, @"(?i)^(?:schedule|agenda|planifica)\s+(?:task|tarea)?\s*#?(?<id>\d+)\s+(?:from|desde|at|el)\s+(?<start>.+?)\s+(?:to|hasta|-)\s+(?<end>.+)$");
        if (scheduleMatch.Success)
        {
            var task = ResolveTask(snapshot.Tasks, int.Parse(scheduleMatch.Groups["id"].Value), null);
            var start = ParseDateTime(scheduleMatch.Groups["start"].Value);
            var end = ParseDateTime(scheduleMatch.Groups["end"].Value);
            if (task is null || start is null || end is null) return null;
            return new PendingAgentAction(AgentActionKind.ScheduleTask, null, null, null, task, null, start, end, start.Value.Date, WorkType.DeepWork, TaskPriority.Medium);
        }

        var createScheduleMatch = Regex.Match(trimmed, @"(?i)^(?:create and schedule|crea y agenda|crear y agendar)\s+(?<title>.+?)\s+(?:project|proyecto)\s+(?<project>.+?)\s+(?:from|desde|at|el)\s+(?<start>.+?)\s+(?:to|hasta|-)\s+(?<end>.+)$");
        if (createScheduleMatch.Success)
        {
            var project = ResolveProject(snapshot.Projects, createScheduleMatch.Groups["project"].Value);
            var start = ParseDateTime(createScheduleMatch.Groups["start"].Value);
            var end = ParseDateTime(createScheduleMatch.Groups["end"].Value);
            if (project is null || start is null || end is null) return null;
            return new PendingAgentAction(AgentActionKind.CreateAndScheduleTask, createScheduleMatch.Groups["title"].Value.Trim(), null, project, null, null, start, end, start.Value.Date, WorkType.DeepWork, TaskPriority.Medium);
        }

        var createMatch = Regex.Match(trimmed, @"(?i)^(?:create task|crea tarea|crear tarea)\s+(?<title>.+?)(?:\s+(?:project|proyecto)\s+(?<project>.+))?$");
        if (createMatch.Success)
        {
            var projectHint = createMatch.Groups["project"].Success ? createMatch.Groups["project"].Value : null;
            var project = ResolveProject(snapshot.Projects, projectHint) ?? SelectedVoiceProject ?? Projects.FirstOrDefault();
            return project is null
                ? null
                : new PendingAgentAction(AgentActionKind.CreateTask, createMatch.Groups["title"].Value.Trim(), null, project, null, null, null, null, null, WorkType.DeepWork, TaskPriority.Medium);
        }

        return null;
    }

    private async Task<string> ApplyPendingAgentActionAsync(PendingAgentAction action)
    {
        return action.Kind switch
        {
            AgentActionKind.CreateTask => await CreateTaskFromActionAsync(action, schedule: false),
            AgentActionKind.CreateAndScheduleTask => await CreateTaskFromActionAsync(action, schedule: true),
            AgentActionKind.ScheduleTask => await ScheduleTaskFromActionAsync(action),
            AgentActionKind.UpdateTask => await UpdateTaskFromActionAsync(action),
            AgentActionKind.DeleteTask => await DeleteTaskFromActionAsync(action),
            AgentActionKind.UpdateCalendar => await UpdateCalendarFromActionAsync(action),
            AgentActionKind.DeleteCalendar => await DeleteCalendarFromActionAsync(action),
            AgentActionKind.AddTaskComment => await AddCommentFromActionAsync(action),
            AgentActionKind.DeleteTaskComment => await DeleteCommentFromActionAsync(action),
            AgentActionKind.AddTimeEntry => await AddTimeEntryFromActionAsync(action),
            AgentActionKind.UpdateTimeEntry => await UpdateTimeEntryFromActionAsync(action),
            AgentActionKind.DeleteTimeEntry => await DeleteTimeEntryFromActionAsync(action),
            _ => "No action applied."
        };
    }

    private async Task<string> CreateTaskFromActionAsync(PendingAgentAction action, bool schedule)
    {
        var task = new TaskItem
        {
            ProjectId = action.Project!.Id,
            Project = action.Project,
            Title = action.Title!,
            Description = action.Description,
            WorkType = action.WorkType,
            Priority = action.Priority,
            DueDate = action.DueDate ?? action.Start?.Date,
            Status = TaskItemStatus.Todo
        };
        if (schedule && action.Start is not null && action.End is not null)
        {
            task.ScheduleBlocks.Add(new TaskScheduleBlock { Start = action.Start.Value, End = action.End.Value });
            task.ScheduledStart = action.Start;
            task.ScheduledEnd = action.End;
            task.EstimatedPomodoros = EstimateFromBlock(action.Start.Value, action.End.Value);
        }

        if (action.Project.DataverseId is not Guid projectDataverseId)
            throw new InvalidOperationException("Project is not linked to Dataverse.");
        task.DataverseId = await _data.CreateTaskAsync(task, projectDataverseId);
        if (schedule && action.Start is not null && action.End is not null)
            await _data.AddCalendarBlockAsync(task.DataverseId.Value, task.Title, action.Start.Value, action.End.Value);
        return schedule
            ? $"Created and scheduled \"{task.Title}\" for {action.Start:ddd dd/MM HH:mm}-{action.End:HH:mm}."
            : $"Created \"{task.Title}\".";
    }

    private async Task<string> ScheduleTaskFromActionAsync(PendingAgentAction action)
    {
        var task = action.Task!;
        if (action.Start is not DateTime start || action.End is not DateTime end)
            throw new InvalidOperationException("Schedule preview needs a start and end date.");
        if (task.DataverseId is not Guid taskDataverseId)
            throw new InvalidOperationException("Task is not linked to Dataverse.");
        task.ScheduledStart = start;
        task.ScheduledEnd = end;
        task.EstimatedPomodoros = EstimateFromBlock(start, end);
        await _data.UpdateTaskPlanningFieldsAsync(task);
        await _data.AddCalendarBlockAsync(taskDataverseId, task.Title, start, end);
        return $"Scheduled \"{task.Title}\" for {start:ddd dd/MM HH:mm}-{end:HH:mm}.";
    }

    private async Task<string> UpdateTaskFromActionAsync(PendingAgentAction action)
    {
        var task = action.Task!;
        var changes = new List<string>();

        if (!string.IsNullOrWhiteSpace(action.Title) && !string.Equals(task.Title, action.Title, StringComparison.Ordinal))
        {
            changes.Add($"title to \"{action.Title}\"");
            task.Title = action.Title!;
        }

        if (action.Description is not null && !string.Equals(task.Description ?? string.Empty, action.Description, StringComparison.Ordinal))
        {
            changes.Add("description");
            task.Description = action.Description;
        }

        if (action.Project is not null && action.Project.Id != task.ProjectId)
        {
            changes.Add($"project to \"{action.Project.Name}\"");
            task.ProjectId = action.Project.Id;
            task.Project = action.Project;
        }

        if (action.DueDate is not null && task.DueDate?.Date != action.DueDate.Value.Date)
        {
            changes.Add($"due date to {action.DueDate:ddd dd/MM/yyyy}");
            task.DueDate = action.DueDate.Value.Date;
        }

        if (action.HasWorkType && task.WorkType != action.WorkType)
        {
            changes.Add($"work type to {action.WorkType}");
            task.WorkType = action.WorkType;
        }

        if (action.HasPriority && task.Priority != action.Priority)
        {
            changes.Add($"priority to {action.Priority}");
            task.Priority = action.Priority;
        }

        if (action.Status is TaskItemStatus status && task.Status != status)
        {
            changes.Add($"status to {status}");
            task.Status = status;
            task.CompletedAt = status == TaskItemStatus.Done ? DateTime.UtcNow : null;
        }

        if (task.Project?.DataverseId is not Guid projectDataverseId)
            throw new InvalidOperationException("Task project is not linked to Dataverse.");
        await _data.UpdateTaskAsync(task, projectDataverseId);
        return changes.Count == 0
            ? $"No changes were needed for \"{task.Title}\"."
            : $"Updated \"{task.Title}\": {string.Join(", ", changes)}.";
    }

    private async Task<string> DeleteTaskFromActionAsync(PendingAgentAction action)
    {
        var task = action.Task!;
        if (task.DataverseId is not Guid taskDataverseId)
            throw new InvalidOperationException("Task is not linked to Dataverse.");
        await _data.DeleteTaskAsync(taskDataverseId);
        return $"Deleted \"{task.Title}\".";
    }

    private async Task<string> UpdateCalendarFromActionAsync(PendingAgentAction action)
    {
        var task = action.Task!;
        if (action.Start is not DateTime start || action.End is not DateTime end)
            throw new InvalidOperationException("Calendar preview needs a start and end date.");
        var block = ResolveBlockForApply(task, action.ScheduleBlockId);
        if (block?.DataverseId is Guid blockDataverseId)
            await _data.UpdateCalendarBlockAsync(blockDataverseId, start, end);
        else if (task.DataverseId is Guid taskDataverseId)
            await _data.AddCalendarBlockAsync(taskDataverseId, task.Title, start, end);

        task.ScheduledStart = start;
        task.ScheduledEnd = end;
        task.EstimatedPomodoros = EstimateFromBlock(start, end);
        await _data.UpdateTaskPlanningFieldsAsync(task);
        return $"Moved \"{task.Title}\" to {start:ddd dd/MM HH:mm}-{end:HH:mm}.";
    }

    private async Task<string> DeleteCalendarFromActionAsync(PendingAgentAction action)
    {
        var task = action.Task!;
        var block = ResolveBlockForApply(task, action.ScheduleBlockId);
        if (block is null && task.ScheduledStart is null)
            return $"\"{task.Title}\" was not scheduled.";

        if (block?.DataverseId is Guid blockDataverseId)
            await _data.DeleteCalendarBlockAsync(blockDataverseId);

        task.ScheduledStart = null;
        task.ScheduledEnd = null;
        task.EstimatedPomodoros = 0;
        await _data.UpdateTaskPlanningFieldsAsync(task);
        return $"Removed \"{task.Title}\" from the calendar.";
    }

    private async Task<string> AddCommentFromActionAsync(PendingAgentAction action)
    {
        var content = await ReviewCommentIfEnabledAsync(action.Task!, action.Comment!);
        if (action.Task!.DataverseId is not Guid taskDataverseId)
            throw new InvalidOperationException("Task is not linked to Dataverse.");
        await _data.AddCommentAsync(taskDataverseId, action.Task.Title, content);
        return $"Added a comment to \"{action.Task.Title}\".";
    }

    private async Task<string> DeleteCommentFromActionAsync(PendingAgentAction action)
    {
        var comment = await ResolveJournalEntryAsync(action.Task, action.JournalEntryId, action.Comment);
        if (comment?.DataverseId is not Guid commentDataverseId)
            throw new InvalidOperationException("Comment is not linked to Dataverse.");
        await _data.DeleteCommentAsync(commentDataverseId);
        return $"Deleted a comment from \"{action.Task?.Title ?? "the task"}\".";
    }

    private async Task<string> AddTimeEntryFromActionAsync(PendingAgentAction action)
    {
        var task = action.Task!;
        if (task.DataverseId is not Guid taskDataverseId)
            throw new InvalidOperationException("Task is not linked to Dataverse.");
        var minutes = Math.Max(1, (int)Math.Round((action.End!.Value - action.Start!.Value).TotalMinutes));
        await _data.AddTimeEntryAsync(taskDataverseId, task.Title, action.Start.Value, action.End.Value, "Registrado desde agente");
        return $"Registered {FormatMinutes(minutes)} for \"{task.Title}\".";
    }

    private async Task<string> UpdateTimeEntryFromActionAsync(PendingAgentAction action)
    {
        var session = await ResolveTimeEntryAsync(action.Task, action.TimeEntryId, action.Start?.Date);
        if (session?.DataverseId is not Guid timeEntryDataverseId)
            throw new InvalidOperationException("Time entry is not linked to Dataverse.");
        var start = action.Start ?? session.StartedAt;
        var end = action.End ?? session.EndedAt ?? start.AddMinutes(Math.Max(1, session.ActualMinutes));
        var minutes = Math.Max(1, (int)Math.Round((end - start).TotalMinutes));
        await _data.UpdateTimeEntryAsync(timeEntryDataverseId, start, minutes);
        return $"Updated tracked time for \"{session.TaskItem?.Title ?? "the task"}\" to {start:ddd dd/MM HH:mm}-{end:HH:mm}.";
    }

    private async Task<string> DeleteTimeEntryFromActionAsync(PendingAgentAction action)
    {
        var session = await ResolveTimeEntryAsync(action.Task, action.TimeEntryId, action.Start?.Date);
        if (session?.DataverseId is not Guid timeEntryDataverseId)
            throw new InvalidOperationException("Time entry is not linked to Dataverse.");
        var title = session.TaskItem?.Title ?? action.Task?.Title ?? "the task";
        await _data.DeleteTimeEntryAsync(timeEntryDataverseId);
        return $"Deleted tracked time from \"{title}\".";
    }

    private async Task<string> ReviewCommentIfEnabledAsync(TaskItem task, string comment)
    {
        var fallback = comment.Trim();
        if (!string.Equals(await _settings.GetAsync("AI.ReviewTaskComments"), "true", StringComparison.OrdinalIgnoreCase)) return fallback;
        var apiKey = await _settings.GetDeepSeekApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey)) return fallback;

        try
        {
            var taskTitle = task.Title;
            var projectName = task.Project?.Name;
            var response = await _deepSeek.ChatAsync(apiKey, new[]
            {
                new DeepSeekMessage("system", AiCommentReviewGuard.ConservativeSystemPrompt),
                new DeepSeekMessage("user", AiCommentReviewGuard.BuildUserPrompt(taskTitle, projectName, fallback))
            }, GetDeepSeekOptions());
            return AiCommentReviewGuard.KeepOriginalIfUnsafe(fallback, response);
        }
        catch
        {
            return fallback;
        }
    }

    private static Project? ResolveProject(IEnumerable<Project> projects, string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return null;
        if (int.TryParse(hint.Trim(), out var id))
        {
            var byId = projects.FirstOrDefault(p => p.Id == id);
            if (byId is not null) return byId;
        }

        hint = hint.Trim();
        return projects.FirstOrDefault(p => p.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static TaskItem? ResolveTask(IEnumerable<TaskItem> tasks, int? taskId, string? hint)
    {
        if (taskId is int id)
        {
            var byId = tasks.FirstOrDefault(t => t.Id == id);
            if (byId is not null) return byId;
        }

        if (string.IsNullOrWhiteSpace(hint)) return null;
        hint = hint.Trim();
        return tasks
            .Where(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled)
            .FirstOrDefault(t => t.Title.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static TaskScheduleBlock? ResolveScheduleBlock(TaskItem? task, int? scheduleBlockId)
    {
        if (task is null) return null;
        if (scheduleBlockId is int id)
            return task.ScheduleBlocks.FirstOrDefault(b => b.Id == id);

        return task.ScheduleBlocks.OrderByDescending(b => b.Start).FirstOrDefault();
    }

    private async Task<TaskJournalEntry?> ResolveJournalEntryAsync(TaskItem? task, int? journalEntryId, string? hint)
    {
        if (task?.DataverseId is not Guid taskDataverseId) return null;
        var comments = await _data.LoadCommentsAsync(taskDataverseId);
        IEnumerable<TaskJournalEntry> query = comments.Select((entry, index) =>
        {
            entry.Id = index + 1;
            entry.TaskItem = task;
            entry.TaskItemId = task.Id;
            return entry;
        });
        if (journalEntryId is int id)
            return query.FirstOrDefault(e => e.Id == id);
        if (!string.IsNullOrWhiteSpace(hint))
            query = query.Where(e => e.Content.Contains(hint, StringComparison.OrdinalIgnoreCase));

        return query.OrderByDescending(e => e.CreatedAt).FirstOrDefault();
    }

    private async Task<PomodoroSession?> ResolveTimeEntryAsync(TaskItem? task, int? timeEntryId, DateTime? date)
    {
        var sessions = new List<PomodoroSession>();
        if (task?.DataverseId is Guid taskDataverseId)
        {
            sessions.AddRange(await _data.LoadTimeEntriesAsync(taskDataverseId));
        }
        else
        {
            var recent = await _data.LoadRecentTimeEntryContextAsync(DateTime.UtcNow.AddDays(-14), 60);
            var snapshot = await _data.LoadTaskSnapshotAsync();
            var tasksByDataverseId = snapshot.Tasks.Where(t => t.DataverseId is not null).ToDictionary(t => t.DataverseId!.Value);
            sessions.AddRange(recent.Select((entry, index) =>
            {
                var mappedTask = entry.TaskDataverseId is Guid id && tasksByDataverseId.TryGetValue(id, out var t) ? t : null;
                return new PomodoroSession
                {
                    Id = index + 1,
                    DataverseId = entry.Id,
                    TaskItemId = mappedTask?.Id,
                    TaskItem = mappedTask,
                    StartedAt = entry.StartedAt,
                    EndedAt = entry.EndedAt,
                    ActualMinutes = entry.ActualMinutes,
                    PlannedMinutes = entry.ActualMinutes,
                    SessionType = PomodoroSessionType.Work,
                    Completed = true
                };
            }));
        }

        sessions = sessions.Select((entry, index) =>
        {
            entry.Id = index + 1;
            entry.TaskItem ??= task;
            entry.TaskItemId ??= task?.Id;
            return entry;
        }).ToList();

        if (timeEntryId is int id)
            return sessions.FirstOrDefault(s => s.Id == id);

        IEnumerable<PomodoroSession> query = sessions;
        if (date is DateTime day)
            query = query.Where(s => s.StartedAt.Date == day.Date);

        return query.OrderByDescending(s => s.StartedAt).FirstOrDefault();
    }

    private static TaskScheduleBlock? ResolveBlockForApply(TaskItem task, int? scheduleBlockId)
    {
        if (scheduleBlockId is int id)
            return task.ScheduleBlocks.FirstOrDefault(b => b.Id == id);

        return task.ScheduleBlocks.OrderByDescending(b => b.Start).FirstOrDefault();
    }

    public string GetAgentActionTitle(PendingAgentAction action) => action.Kind switch
    {
        AgentActionKind.CreateTask => "Create task",
        AgentActionKind.CreateAndScheduleTask => "Create and schedule task",
        AgentActionKind.UpdateTask => "Update task",
        AgentActionKind.DeleteTask => "Delete task",
        AgentActionKind.ScheduleTask => "Add to calendar",
        AgentActionKind.UpdateCalendar => "Modify calendar",
        AgentActionKind.DeleteCalendar => "Remove from calendar",
        AgentActionKind.AddTaskComment => "Add comment",
        AgentActionKind.DeleteTaskComment => "Delete comment",
        AgentActionKind.AddTimeEntry => "Log time",
        AgentActionKind.UpdateTimeEntry => "Modify time",
        AgentActionKind.DeleteTimeEntry => "Delete time",
        _ => "Action"
    };

    public string GetAgentActionDescription(PendingAgentAction action)
    {
        var taskName = action.Task?.Title ?? action.Title ?? "Untitled";
        return action.Kind switch
        {
            AgentActionKind.CreateTask or AgentActionKind.CreateAndScheduleTask => action.Title ?? "New task",
            AgentActionKind.DeleteTask => $"Delete \"{taskName}\"",
            AgentActionKind.DeleteTaskComment => $"Delete a comment from \"{taskName}\"",
            AgentActionKind.AddTimeEntry => $"Log time on \"{taskName}\"",
            AgentActionKind.UpdateTimeEntry => $"Modify time on \"{taskName}\"",
            AgentActionKind.DeleteTimeEntry => $"Delete time from \"{taskName}\"",
            _ => taskName
        };
    }

    public string GetAgentActionMeta(PendingAgentAction action)
    {
        var parts = new List<string>();
        if (action.Project is not null) parts.Add($"Project: {action.Project.Name}");
        if (action.Start is not null) parts.Add($"Start: {action.Start:ddd dd/MM HH:mm}");
        if (action.End is not null) parts.Add($"End: {action.End:ddd dd/MM HH:mm}");
        if (action.DueDate is not null) parts.Add($"Due: {action.DueDate:ddd dd/MM/yyyy}");
        if (action.HasPriority) parts.Add($"Priority: {action.Priority}");
        if (action.HasWorkType) parts.Add($"Type: {action.WorkType}");
        if (action.Status is not null) parts.Add($"Status: {action.Status}");
        if (action.TimeEntryId is not null) parts.Add($"Time entry: {action.TimeEntryId}");
        if (!string.IsNullOrWhiteSpace(action.Comment)) parts.Add($"Comment: {action.Comment}");
        return parts.Count == 0 ? "No additional details." : string.Join(" | ", parts);
    }

    private static string BuildAgentPreviewIntro(PendingAgentAction? action)
    {
        if (action is null) return "I'll prepare this change for you to confirm before touching anything.";
        if (action.Kind is AgentActionKind.AddTimeEntry or AgentActionKind.UpdateTimeEntry or AgentActionKind.DeleteTimeEntry)
            return $"Sure, here's the tracked-time change for \"{action.Task?.Title}\":";

        return action.Kind switch
        {
            AgentActionKind.CreateTask or AgentActionKind.CreateAndScheduleTask =>
                $"Sure, here's a task I propose for the {action.Project?.Name ?? "selected"} project:",
            AgentActionKind.UpdateTask =>
                $"Sure, here's the change I propose for the task \"{action.Task?.Title}\":",
            AgentActionKind.DeleteTask =>
                $"OK, before deleting the task \"{action.Task?.Title}\", here's the preview:",
            AgentActionKind.ScheduleTask or AgentActionKind.UpdateCalendar or AgentActionKind.DeleteCalendar =>
                $"Sure, here's the calendar change for \"{action.Task?.Title}\":",
            AgentActionKind.AddTaskComment or AgentActionKind.DeleteTaskComment =>
                $"Sure, here's the comment change for \"{action.Task?.Title}\":",
            _ => "I'll prepare this change for you to confirm before touching anything."
        };
    }

    private static bool IsSameAction(PendingAgentAction left, PendingAgentAction right) =>
        left.Kind == right.Kind &&
        left.Task?.Id == right.Task?.Id &&
        left.Project?.Id == right.Project?.Id &&
        left.ScheduleBlockId == right.ScheduleBlockId &&
        left.JournalEntryId == right.JournalEntryId &&
        left.TimeEntryId == right.TimeEntryId &&
        string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
        string.Equals(left.Comment, right.Comment, StringComparison.Ordinal) &&
        left.Start == right.Start &&
        left.End == right.End &&
        left.DueDate == right.DueDate;

    private static AgentActionDto ToDto(PendingAgentAction action) => new()
    {
        Action = action.Kind.ToString(),
        Title = action.Title,
        Description = action.Description,
        ProjectHint = action.Project?.Name,
        TaskId = action.Task?.Id,
        TaskHint = action.Task?.Title,
        ScheduleBlockId = action.ScheduleBlockId,
        JournalEntryId = action.JournalEntryId,
        TimeEntryId = action.TimeEntryId,
        Comment = action.Comment,
        Start = action.Start?.ToString("yyyy-MM-dd HH:mm"),
        End = action.End?.ToString("yyyy-MM-dd HH:mm"),
        DueDate = action.DueDate?.ToString("yyyy-MM-dd"),
        WorkType = action.HasWorkType ? action.WorkType.ToString() : null,
        Priority = action.HasPriority ? action.Priority.ToString() : null,
        Status = action.Status?.ToString()
    };

    private void SetAgentActionPreview(PendingAgentAction action)
    {
        _pendingAgentAction = action;
        HasAgentActionPreview = true;
        AgentActionTitle = GetAgentActionTitle(action);
        AgentActionDescription = GetAgentActionDescription(action);
        AgentActionMeta = GetAgentActionMeta(action); /*
        {
            AgentActionKind.CreateTask => $"{action.Project?.Name} · {action.Priority} · {action.WorkType}",
            AgentActionKind.CreateAndScheduleTask => $"{action.Project?.Name} · {action.Start:ddd dd/MM HH:mm}-{action.End:HH:mm}",
            AgentActionKind.ScheduleTask => $"{action.Start:ddd dd/MM HH:mm}-{action.End:HH:mm}",
            AgentActionKind.AddTaskComment => action.Comment ?? "",
            _ => ""
        */
        var previewMessage = new ChatMessageView("Assistant", BuildAgentPreviewIntro(action), action);
        if (_thinkingMessageIndex is int thinkingIndex && thinkingIndex >= 0 && thinkingIndex < Messages.Count)
        {
            Messages[thinkingIndex] = previewMessage;
            _pendingPreviewMessageIndex = thinkingIndex;
            _thinkingMessageIndex = null;
        }
        else if (_pendingPreviewMessageIndex is int index && index >= 0 && index < Messages.Count)
        {
            Messages[index] = previewMessage;
        }
        else
        {
            Messages.Add(previewMessage);
            _pendingPreviewMessageIndex = Messages.Count - 1;
        }
    }

    private void ClearAgentActionPreview()
    {
        _pendingAgentAction = null;
        HasAgentActionPreview = false;
        AgentActionTitle = string.Empty;
        AgentActionDescription = string.Empty;
        AgentActionMeta = string.Empty;
        if (_pendingPreviewMessageIndex is int index && index >= 0 && index < Messages.Count)
            Messages.RemoveAt(index);
        _pendingPreviewMessageIndex = null;
    }

    private static AgentActionKind ParseAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return AgentActionKind.None;
        return action.Trim().Replace(" ", "", StringComparison.OrdinalIgnoreCase).Replace("-", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant() switch
        {
            "addcalendar" or "addtocalendar" or "createcalendar" => AgentActionKind.ScheduleTask,
            "modifycalendar" or "movecalendar" or "reschedule" or "rescheduletask" => AgentActionKind.UpdateCalendar,
            "removecalendar" or "deletecalendarblock" or "removefromcalendar" => AgentActionKind.DeleteCalendar,
            "addcomment" => AgentActionKind.AddTaskComment,
            "deletecomment" or "removecomment" => AgentActionKind.DeleteTaskComment,
            "addtime" or "addtimeentry" or "tracktime" or "registertime" or "adddedication" or "registrardedicacion" => AgentActionKind.AddTimeEntry,
            "updatetime" or "updatetimeentry" or "modifytime" or "modifytimeentry" or "updatededication" or "modificardedicacion" => AgentActionKind.UpdateTimeEntry,
            "deletetime" or "deletetimeentry" or "removetime" or "deletededication" or "eliminardedicacion" => AgentActionKind.DeleteTimeEntry,
            _ => Enum.TryParse(action, true, out AgentActionKind parsed) ? parsed : AgentActionKind.None
        };
    }

    private static DateTime? ParseDateTime(string? value) =>
        DateTime.TryParse(value, out var parsed) ? parsed : null;

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, out var parsed) ? parsed.Date : null;

    private static int EstimateFromBlock(DateTime start, DateTime end)
    {
        var minutes = Math.Max(0, (int)Math.Round((end - start).TotalMinutes));
        if (minutes <= 0) return 0;
        return (int)Math.Ceiling(Math.Round((minutes / 60d) * 2, MidpointRounding.AwayFromZero) / 2d);
    }

    private static string FormatMinutes(int minutes)
    {
        var safe = Math.Max(0, minutes);
        var hours = safe / 60;
        var remainder = safe % 60;
        return hours > 0 ? $"{hours}h {remainder:00}m" : $"{remainder}m";
    }

    private async Task<TaskDraft> ParseTaskDraftAsync(string transcript)
    {
        var apiKey = await _settings.GetDeepSeekApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return TaskDraft.FromText(transcript);
        }

        try
        {
            var response = await _deepSeek.ChatAsync(apiKey, new[]
            {
                new DeepSeekMessage("system", "Parse the user text into exactly one task draft. If the text sounds like steps, acceptance criteria, errands inside one goal, or a mini-plan, use checklistItems instead of creating multiple tasks. Return only one JSON object. Shape: {\"title\":\"...\",\"description\":\"...\",\"projectHint\":\"...\",\"workType\":\"DeepWork|Admin|Meeting|Learning|Communication|Planning|Other\",\"priority\":\"Low|Medium|High|Critical\",\"estimatedPomodoros\":1,\"dueDate\":\"yyyy-MM-dd or null\",\"checklistItems\":[\"...\"]}. Never return an array."),
                new DeepSeekMessage("user", transcript)
            }, GetDeepSeekOptions());
            var json = ExtractJson(response);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            TaskDraftDto? parsed;
            if (json.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                parsed = JsonSerializer.Deserialize<List<TaskDraftDto>>(json, options)?.FirstOrDefault();
            }
            else
            {
                parsed = JsonSerializer.Deserialize<TaskDraftDto>(json, options);
            }

            return parsed is null || string.IsNullOrWhiteSpace(parsed.Title)
                ? TaskDraft.FromText(transcript)
                : TaskDraft.FromDto(parsed);
        }
        catch
        {
            return TaskDraft.FromText(transcript);
        }
    }

    private static string ExtractJson(string text)
    {
        var objectStart = text.IndexOf('{');
        var objectEnd = text.LastIndexOf('}');
        if (objectStart >= 0 && objectEnd > objectStart)
            return text[objectStart..(objectEnd + 1)];

        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    private void SetPreview(TaskDraft draft, Project project)
    {
        _previewDraft = draft;
        _previewProject = project;

        PreviewTitle = draft.Title;
        PreviewDescription = string.IsNullOrWhiteSpace(draft.Description) ? "No description" : draft.Description;
        PreviewProjectName = project.Name;
        PreviewWorkType = draft.WorkType.ToString();
        PreviewPriority = draft.Priority.ToString();
        PreviewEstimate = $"{draft.EstimatedPomodoros} pomodoro(s)";
        PreviewDueDate = draft.DueDate is DateTime due ? DisplayFormat.Date(due) : "No due date";

        PreviewChecklistItems.Clear();
        foreach (var item in draft.ChecklistItems)
            PreviewChecklistItems.Add(item);

        HasTaskPreview = true;
    }

    private void ClearPreview()
    {
        _previewDraft = null;
        _previewProject = null;
        PreviewTitle = string.Empty;
        PreviewDescription = string.Empty;
        PreviewProjectName = string.Empty;
        PreviewWorkType = string.Empty;
        PreviewPriority = string.Empty;
        PreviewEstimate = string.Empty;
        PreviewDueDate = string.Empty;
        PreviewChecklistItems.Clear();
        HasTaskPreview = false;
    }

    private async Task SaveConversationMessageAsync(string role, string content)
    {
        await Task.CompletedTask;
    }
}

public sealed record ChatMessageView(string Author, string Content, PendingAgentAction? Preview = null);

public sealed record TaskDraft(string Title, string? Description, string? ProjectHint, WorkType WorkType, TaskPriority Priority, int EstimatedPomodoros, DateTime? DueDate, IReadOnlyList<string> ChecklistItems)
{
    public static TaskDraft FromText(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var checklist = lines
            .Skip(1)
            .Select(l => l.TrimStart('-', '*', ' ', '\t'))
            .Where(l => l.Length > 0)
            .ToList();
        var title = lines.FirstOrDefault() ?? text.Trim();
        return new(title, text.Trim(), null, WorkType.DeepWork, TaskPriority.Medium, 1, null, checklist);
    }

    public static TaskDraft FromDto(TaskDraftDto dto)
    {
        Enum.TryParse(dto.WorkType, true, out WorkType workType);
        Enum.TryParse(dto.Priority, true, out TaskPriority priority);
        var pomos = Math.Clamp(dto.EstimatedPomodoros ?? 1, 0, 50);
        DateTime? due = DateTime.TryParse(dto.DueDate, out var parsed) ? parsed.Date : null;
        var checklist = dto.ChecklistItems?
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        return new TaskDraft(dto.Title ?? "Untitled task", dto.Description, dto.ProjectHint, workType, priority, pomos, due, checklist);
    }
}

public sealed class TaskDraftDto
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? ProjectHint { get; set; }
    public string? WorkType { get; set; }
    public string? Priority { get; set; }
    public int? EstimatedPomodoros { get; set; }
    public string? DueDate { get; set; }
    public List<string>? ChecklistItems { get; set; }
}

public sealed class AgentActionDto
{
    public string? Action { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? ProjectHint { get; set; }
    public int? TaskId { get; set; }
    public string? TaskHint { get; set; }
    public int? ScheduleBlockId { get; set; }
    public int? JournalEntryId { get; set; }
    public int? TimeEntryId { get; set; }
    public string? Comment { get; set; }
    public string? Start { get; set; }
    public string? End { get; set; }
    public string? DueDate { get; set; }
    public string? WorkType { get; set; }
    public string? Priority { get; set; }
    public string? Status { get; set; }
}

public enum AgentActionKind
{
    None,
    CreateTask,
    UpdateTask,
    DeleteTask,
    ScheduleTask,
    CreateAndScheduleTask,
    UpdateCalendar,
    DeleteCalendar,
    AddTaskComment,
    DeleteTaskComment,
    AddTimeEntry,
    UpdateTimeEntry,
    DeleteTimeEntry
}

internal enum VoiceCaptureMode
{
    None,
    TaskDraft,
    ConversationTurn,
    AgentMessage
}

internal enum VoiceTurnMode
{
    Text,
    Spoken
}

public sealed record PendingAgentAction(
    AgentActionKind Kind,
    string? Title,
    string? Description,
    Project? Project,
    TaskItem? Task,
    string? Comment,
    DateTime? Start,
    DateTime? End,
    DateTime? DueDate,
    WorkType WorkType,
    TaskPriority Priority,
    int? ScheduleBlockId = null,
    int? JournalEntryId = null,
    TaskItemStatus? Status = null,
    bool HasWorkType = false,
    bool HasPriority = false,
    int? TimeEntryId = null);

using System.Text;
using System.Text.Json;
using JTS.AI;
using NAudio.Wave;
using Vosk;

namespace JTS_App.Services;

public sealed class LocalVoiceAgentService : IDisposable
{
    private const int SampleRate = 16000;
    private const int MinSpeechChunkLength = 140;
    private const int MaxSpeechChunkLength = 320;
    private readonly AppSettingsService _settings;
    private readonly AppDataContextService _context;
    private readonly DeepSeekClient _deepSeek;
    private readonly SpeechPlaybackService _speechPlayback;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private Model? _model;
    private string? _loadedModelPath;
    private VoskRecognizer? _recognizer;
    private WaveInEvent? _waveIn;
    private CancellationTokenSource? _sessionCts;
    private CancellationTokenSource? _responseCts;
    private readonly StringBuilder _manualTurnBuffer = new();
    private string _lastPartial = string.Empty;
    private string _currentAssistantResponse = string.Empty;
    private bool _manualTurnMode;
    private bool _isDictationMode;
    private readonly StringBuilder _dictationBuffer = new();
    private bool _isAssistantSpeaking;
    private bool _turnWasInterrupted;
    private readonly List<DeepSeekMessage> _conversation = new();

    public bool IsRunning => _waveIn is not null;
    public bool IsManualTurnActive => _manualTurnMode;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? DictationTextChanged;
    public event EventHandler<string>? UserTranscriptDelta;
    public event EventHandler<string>? UserTranscriptCompleted;
    public event EventHandler<string>? AssistantTranscriptCompleted;
    public event EventHandler<string>? Error;

    public LocalVoiceAgentService(
        AppSettingsService settings,
        AppDataContextService context,
        DeepSeekClient deepSeek,
        SpeechPlaybackService speechPlayback)
    {
        _settings = settings;
        _context = context;
        _deepSeek = deepSeek;
        _speechPlayback = speechPlayback;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;

        var apiKey = await _settings.GetDeepSeekApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("DeepSeek API key is missing.");

        var modelPath = await _settings.GetVoskModelPathAsync();
        if (string.IsNullOrWhiteSpace(modelPath) || !Directory.Exists(modelPath))
            throw new InvalidOperationException("Vosk Spanish model folder is not configured or does not exist.");

        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        StatusChanged?.Invoke(this, "Loading local speech model...");
        await EnsureModelLoadedAsync(modelPath, _sessionCts.Token);

        _recognizer?.Dispose();
        _recognizer = new VoskRecognizer(_model, SampleRate);
        _recognizer.SetWords(false);
        _recognizer.SetMaxAlternatives(0);
        _lastPartial = string.Empty;
        _conversation.Clear();

        StartMicrophone();
        StatusChanged?.Invoke(this, "Local voice ready. Speak naturally.");
    }

    public async Task StartManualTurnAsync(CancellationToken cancellationToken = default)
    {
        if (_manualTurnMode) return;

        var apiKey = await _settings.GetDeepSeekApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("DeepSeek API key is missing.");

        var modelPath = await _settings.GetVoskModelPathAsync();
        if (string.IsNullOrWhiteSpace(modelPath) || !Directory.Exists(modelPath))
            throw new InvalidOperationException("Vosk Spanish model folder is not configured or does not exist.");

        _sessionCts ??= CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        StatusChanged?.Invoke(this, "Loading local speech model...");
        await EnsureModelLoadedAsync(modelPath, _sessionCts.Token);

        _recognizer?.Dispose();
        _recognizer = new VoskRecognizer(_model, SampleRate);
        _recognizer.SetWords(false);
        _recognizer.SetMaxAlternatives(0);
        _manualTurnBuffer.Clear();
        _lastPartial = string.Empty;
        _manualTurnMode = true;

        StartMicrophone();
        StatusChanged?.Invoke(this, "Listening. Press Stop and send when you finish.");
    }

    public async Task FinishManualTurnAsync(CancellationToken cancellationToken = default)
    {
        if (!_manualTurnMode) return;

        _manualTurnMode = false;
        StopMicrophone();
        AppendManualText(ExtractText(_recognizer?.FinalResult() ?? string.Empty, "text"));
        var text = _manualTurnBuffer.ToString().Trim();
        _manualTurnBuffer.Clear();
        _lastPartial = string.Empty;
        UserTranscriptDelta?.Invoke(this, string.Empty);

        if (string.IsNullOrWhiteSpace(text))
        {
            StatusChanged?.Invoke(this, "No speech detected.");
            return;
        }

        await ProcessUserTurnAsync(text, cancellationToken, waitForTurn: true);
    }

    public async Task StartDictationAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;

        var modelPath = await _settings.GetVoskModelPathAsync();
        if (string.IsNullOrWhiteSpace(modelPath) || !Directory.Exists(modelPath))
            throw new InvalidOperationException("Vosk Spanish model folder is not configured or does not exist.");

        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        StatusChanged?.Invoke(this, "Loading local speech model...");
        await EnsureModelLoadedAsync(modelPath, _sessionCts.Token);

        _recognizer?.Dispose();
        _recognizer = new VoskRecognizer(_model, SampleRate);
        _recognizer.SetWords(false);
        _recognizer.SetMaxAlternatives(0);
        _dictationBuffer.Clear();
        _lastPartial = string.Empty;
        _isDictationMode = true;

        StartMicrophone();
        StatusChanged?.Invoke(this, "Dictating...");
    }

    public async Task<string> StopDictationAsync()
    {
        if (!_isDictationMode) return string.Empty;

        _isDictationMode = false;
        StopMicrophone();

        var finalText = ExtractText(_recognizer?.FinalResult() ?? string.Empty, "text");
        if (!string.IsNullOrWhiteSpace(finalText))
        {
            if (_dictationBuffer.Length > 0) _dictationBuffer.Append(' ');
            _dictationBuffer.Append(finalText.Trim());
        }

        _recognizer?.Dispose();
        _recognizer = null;
        _lastPartial = string.Empty;

        var result = _dictationBuffer.ToString().Trim();
        _dictationBuffer.Clear();

        _sessionCts?.Dispose();
        _sessionCts = null;

        return await Task.FromResult(result);
    }

    public async Task StopAsync()
    {
        _sessionCts?.Cancel();
        _responseCts?.Cancel();
        StopMicrophone();
        _recognizer?.Dispose();
        _recognizer = null;
        _sessionCts?.Dispose();
        _sessionCts = null;
        _responseCts?.Dispose();
        _responseCts = null;
        _lastPartial = string.Empty;
        _manualTurnMode = false;
        _manualTurnBuffer.Clear();
        _isDictationMode = false;
        _dictationBuffer.Clear();
        _currentAssistantResponse = string.Empty;
        _isAssistantSpeaking = false;
        _turnWasInterrupted = false;
        _speechPlayback.Stop();
        StatusChanged?.Invoke(this, "Local voice stopped.");
        await Task.CompletedTask;
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_turnLock.CurrentCount == 0)
            InterruptCurrentTurn();

        await ProcessUserTurnAsync(text.Trim(), cancellationToken, waitForTurn: true);
    }

    private async Task EnsureModelLoadedAsync(string modelPath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(modelPath);
        if (_model is not null && string.Equals(_loadedModelPath, fullPath, StringComparison.OrdinalIgnoreCase)) return;

        await Task.Run(() =>
        {
            Vosk.Vosk.SetLogLevel(-1);
            _model?.Dispose();
            _model = new Model(fullPath);
            _loadedModelPath = fullPath;
        }, cancellationToken);
    }

    private void StartMicrophone()
    {
        if (_waveIn is not null) return;

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 50
        };
        _waveIn.DataAvailable += OnAudioAvailable;
        _waveIn.StartRecording();
    }

    private void StopMicrophone()
    {
        if (_waveIn is null) return;
        _waveIn.DataAvailable -= OnAudioAvailable;
        try { _waveIn.StopRecording(); } catch { }
        _waveIn.Dispose();
        _waveIn = null;
    }

    private void OnAudioAvailable(object? sender, WaveInEventArgs e)
    {
        if (_recognizer is null || _sessionCts?.IsCancellationRequested == true) return;

        try
        {
            if (_recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded))
            {
                var text = ExtractText(_recognizer.Result(), "text");
                if (_isDictationMode)
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (_dictationBuffer.Length > 0) _dictationBuffer.Append(' ');
                        _dictationBuffer.Append(text.Trim());
                    }
                    _lastPartial = string.Empty;
                    DictationTextChanged?.Invoke(this, _dictationBuffer.ToString().Trim());
                    return;
                }
                if (_manualTurnMode)
                {
                    AppendManualText(text);
                    UserTranscriptDelta?.Invoke(this, BuildManualPreview());
                }
                else if (!string.IsNullOrWhiteSpace(text))
                {
                    _ = ProcessRecognizedTurnAsync(text);
                }
                _lastPartial = string.Empty;
                if (!_manualTurnMode)
                    UserTranscriptDelta?.Invoke(this, string.Empty);
                return;
            }

            var partial = ExtractText(_recognizer.PartialResult(), "partial");
            if (!string.IsNullOrWhiteSpace(partial) && !string.Equals(partial, _lastPartial, StringComparison.Ordinal))
            {
                _lastPartial = partial;
                if (_isDictationMode)
                {
                    var preview = _dictationBuffer.Length > 0
                        ? $"{_dictationBuffer} {partial.Trim()}"
                        : partial.Trim();
                    DictationTextChanged?.Invoke(this, preview);
                }
                else
                {
                    UserTranscriptDelta?.Invoke(this, _manualTurnMode ? BuildManualPreview(partial) : partial);
                    if (!_manualTurnMode && _isAssistantSpeaking && LooksLikeUserInterruption(partial))
                        InterruptCurrentTurn();
                }
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
        }
    }

    private async Task ProcessRecognizedTurnAsync(string text)
    {
        if (_sessionCts is null) return;
        if (_turnLock.CurrentCount == 0)
            InterruptCurrentTurn();

        await ProcessUserTurnAsync(text, _sessionCts.Token, waitForTurn: true);
    }

    private void AppendManualText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_manualTurnBuffer.Length > 0)
            _manualTurnBuffer.Append(' ');
        _manualTurnBuffer.Append(text.Trim());
    }

    private string BuildManualPreview(string? partial = null)
    {
        var text = _manualTurnBuffer.ToString().Trim();
        if (string.IsNullOrWhiteSpace(partial)) return text;
        return string.IsNullOrWhiteSpace(text) ? partial.Trim() : $"{text} {partial.Trim()}";
    }

    private async Task ProcessUserTurnAsync(string text, CancellationToken cancellationToken, bool waitForTurn = false)
    {
        var acquired = waitForTurn
            ? await _turnLock.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken)
            : await _turnLock.WaitAsync(0, cancellationToken);
        if (!acquired)
        {
            StatusChanged?.Invoke(this, "Still answering. Wait a moment.");
            return;
        }

        var fullResponse = string.Empty;
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _responseCts?.Dispose();
        _responseCts = turnCts;
        _turnWasInterrupted = false;
        _currentAssistantResponse = string.Empty;

        try
        {
            _speechPlayback.Stop();
            UserTranscriptCompleted?.Invoke(this, text);
            StatusChanged?.Invoke(this, "Thinking with DeepSeek...");

            var apiKey = await _settings.GetDeepSeekApiKeyAsync();
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("DeepSeek API key is missing.");

            var contextText = await _context.BuildVoiceAssistantContextAsync();
            var messages = BuildMessages(contextText, text);
            var options = await GetDeepSeekOptionsAsync();
            fullResponse = (await _deepSeek.ChatAsync(apiKey, messages, options, turnCts.Token)).Trim();
            _currentAssistantResponse = fullResponse;

            var final = fullResponse;
            if (!string.IsNullOrWhiteSpace(final))
            {
                _conversation.Add(new DeepSeekMessage("user", text));
                _conversation.Add(new DeepSeekMessage("assistant", final));
                TrimConversation();
                AssistantTranscriptCompleted?.Invoke(this, final);
                await SpeakAsync(final, turnCts.Token);
            }

            StatusChanged?.Invoke(this, "Ready. Speak again when you want.");
        }
        catch (OperationCanceledException) when (_turnWasInterrupted)
        {
            var partial = fullResponse.Trim();
            if (!string.IsNullOrWhiteSpace(partial))
            {
                _conversation.Add(new DeepSeekMessage("user", text));
                _conversation.Add(new DeepSeekMessage("assistant", partial));
                TrimConversation();
                AssistantTranscriptCompleted?.Invoke(this, partial);
            }

            StatusChanged?.Invoke(this, "Interrupted. Listening to you...");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusChanged?.Invoke(this, "Local voice stopped.");
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
        }
        finally
        {
            _isAssistantSpeaking = false;
            if (ReferenceEquals(_responseCts, turnCts))
                _responseCts = null;
            _turnLock.Release();
        }
    }

    private IReadOnlyList<DeepSeekMessage> BuildMessages(string contextText, string userText)
    {
        var messages = new List<DeepSeekMessage>
        {
            new("system", "You are JTS's voice agent. Always speak in natural English, like a person who knows the user's work. Respond briefly, warmly, and conversationally. Use the internal data only to understand context: don't show IDs, database keys, field names, or technical details unless the user explicitly asks. If you list tasks, say it the way a colleague would: title, time, and useful context, without heavy Markdown. Distinguish planned calendar from actual tracked time. If the user asks to create, update, delete, schedule, comment, or log time, recognize the intent and remember the app will show a preview before saving to Dataverse."),
            new("system", contextText)
        };
        messages.AddRange(_conversation.TakeLast(8));
        messages.Add(new DeepSeekMessage("user", userText));
        return messages;
    }

    private async Task<DeepSeekRequestOptions> GetDeepSeekOptionsAsync()
    {
        var model = await _settings.GetDeepSeekModelAsync();
        if (string.IsNullOrWhiteSpace(model))
            model = DeepSeekClient.DefaultModel;

        var thinkingEnabled = string.Equals(
            await _settings.GetDeepSeekThinkingEnabledAsync(),
            "true",
            StringComparison.OrdinalIgnoreCase);

        return new DeepSeekRequestOptions(model, thinkingEnabled);
    }

    private void InterruptCurrentTurn()
    {
        _turnWasInterrupted = true;
        _speechPlayback.Stop();
        _responseCts?.Cancel();
        StatusChanged?.Invoke(this, "Interrupting...");
    }

    private bool LooksLikeUserInterruption(string text)
    {
        var normalized = NormalizeSpeechText(text);
        if (normalized.Length == 0) return false;

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3) return false;

        var assistant = NormalizeSpeechText(_currentAssistantResponse);
        if (assistant.Length == 0) return true;
        if (assistant.Contains(normalized, StringComparison.OrdinalIgnoreCase)) return false;

        var overlappingWords = words.Count(word => word.Length > 2 && assistant.Contains(word, StringComparison.OrdinalIgnoreCase));
        return overlappingWords < Math.Max(2, words.Length / 2);
    }

    private static string NormalizeSpeechText(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
            builder.Append(char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) ? c : ' ');
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool TryTakeSpeechChunk(StringBuilder buffer, out string chunk)
    {
        chunk = string.Empty;
        if (buffer.Length < MinSpeechChunkLength) return false;

        var text = buffer.ToString();
        var splitIndex = FindSentenceBoundary(text);
        if (splitIndex < 0 && buffer.Length >= MaxSpeechChunkLength)
            splitIndex = FindSoftBoundary(text);
        if (splitIndex < 0) return false;

        chunk = text[..splitIndex].Trim();
        buffer.Remove(0, splitIndex);
        return !string.IsNullOrWhiteSpace(chunk);
    }

    private static int FindSentenceBoundary(string text)
    {
        var upperBound = Math.Min(text.Length - 1, MaxSpeechChunkLength);
        for (var i = upperBound; i >= MinSpeechChunkLength - 1; i--)
        {
            if (text[i] is not ('.' or '?' or '!' or '\n')) continue;
            if (i == text.Length - 1 || char.IsWhiteSpace(text[i + 1]))
                return i + 1;
        }

        return -1;
    }

    private static int FindSoftBoundary(string text)
    {
        var upperBound = Math.Min(text.Length - 1, MaxSpeechChunkLength);
        for (var i = upperBound; i >= MinSpeechChunkLength - 1; i--)
        {
            if (text[i] is ',' or ';' or ':')
                return i + 1;
        }

        for (var i = upperBound; i >= MinSpeechChunkLength - 1; i--)
        {
            if (char.IsWhiteSpace(text[i]))
                return i + 1;
        }

        return upperBound + 1;
    }

    private async Task SpeakBufferedSentenceAsync(StringBuilder buffer, CancellationToken cancellationToken, bool force = false)
    {
        var text = buffer.ToString().Trim();
        if (text.Length == 0 || (!force && text.Length < MinSpeechChunkLength)) return;
        buffer.Clear();
        await SpeakAsync(text, cancellationToken);
    }

    private async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        StatusChanged?.Invoke(this, "Speaking...");
        _isAssistantSpeaking = true;
        try
        {
            await _speechPlayback.SpeakAsync(text, cancellationToken);
        }
        finally
        {
            _isAssistantSpeaking = false;
        }
    }

    private void TrimConversation()
    {
        while (_conversation.Count > 10)
            _conversation.RemoveAt(0);
    }

    private static string? ExtractText(string json, string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(propertyName, out var value)
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _ = StopAsync();
        _recognizer?.Dispose();
        _model?.Dispose();
        _turnLock.Dispose();
    }
}

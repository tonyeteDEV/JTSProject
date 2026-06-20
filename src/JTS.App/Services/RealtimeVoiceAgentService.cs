using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.Wave;

namespace JTS_App.Services;

public sealed class RealtimeVoiceAgentService : IDisposable
{
    private const int AudioSampleRate = 24000;
    private readonly Uri _endpoint = new("wss://api.openai.com/v1/realtime?model=gpt-realtime");
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private ClientWebSocket? _socket;
    private WaveInEvent? _waveIn;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _playbackBuffer;
    private CancellationTokenSource? _sessionCts;
    private Task? _receiveTask;
    private string _currentResponseTranscript = string.Empty;
    private string _currentInputTranscript = string.Empty;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? UserTranscriptDelta;
    public event EventHandler<string>? UserTranscriptCompleted;
    public event EventHandler<string>? AssistantTranscriptDelta;
    public event EventHandler<string>? AssistantTranscriptCompleted;
    public event EventHandler<string>? Error;

    public async Task StartAsync(string apiKey, string instructions, CancellationToken cancellationToken = default)
    {
        if (IsConnected) return;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OpenAI API key is missing.");

        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _socket = new ClientWebSocket();
        _socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");

        StatusChanged?.Invoke(this, "Connecting realtime voice...");
        await _socket.ConnectAsync(_endpoint, _sessionCts.Token);
        await ConfigureSessionAsync(instructions, _sessionCts.Token);
        StartPlayback();
        StartMicrophone();
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_sessionCts.Token));
        StatusChanged?.Invoke(this, "Realtime voice connected. Speak naturally.");
    }

    public async Task StopAsync()
    {
        _sessionCts?.Cancel();
        StopMicrophone();
        StopPlayback();

        if (_socket is { State: WebSocketState.Open } socket)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
            }
            catch
            {
                // Closing should stay best-effort.
            }
        }

        _socket?.Dispose();
        _socket = null;
        _sessionCts?.Dispose();
        _sessionCts = null;
        StatusChanged?.Invoke(this, "Realtime voice disconnected.");
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        EnsureConnected();

        await SendJsonAsync(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content = new object[]
                {
                    new { type = "input_text", text = text.Trim() }
                }
            }
        }, cancellationToken);
        await SendJsonAsync(new { type = "response.create" }, cancellationToken);
    }

    private async Task ConfigureSessionAsync(string instructions, CancellationToken cancellationToken)
    {
        await SendJsonAsync(new
        {
            type = "session.update",
            session = new
            {
                type = "realtime",
                model = "gpt-realtime",
                instructions,
                output_modalities = new[] { "audio" },
                max_output_tokens = 900,
                audio = new
                {
                    input = new
                    {
                        format = new { type = "audio/pcm", rate = AudioSampleRate },
                        noise_reduction = new { type = "near_field" },
                        transcription = new { model = "gpt-4o-transcribe", language = "es" },
                        turn_detection = new
                        {
                            type = "server_vad",
                            threshold = 0.5,
                            prefix_padding_ms = 300,
                            silence_duration_ms = 450,
                            create_response = true,
                            interrupt_response = true
                        }
                    },
                    output = new
                    {
                        format = new { type = "audio/pcm", rate = AudioSampleRate },
                        voice = "marin",
                        speed = 1.03
                    }
                }
            }
        }, cancellationToken);
    }

    private void StartMicrophone()
    {
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(AudioSampleRate, 16, 1),
            BufferMilliseconds = 40
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

    private void StartPlayback()
    {
        _playbackBuffer = new BufferedWaveProvider(new WaveFormat(AudioSampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(8),
            DiscardOnBufferOverflow = true
        };
        _waveOut = new WaveOutEvent { DesiredLatency = 120 };
        _waveOut.Init(_playbackBuffer);
        _waveOut.Play();
    }

    private void StopPlayback()
    {
        _playbackBuffer?.ClearBuffer();
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        _playbackBuffer = null;
    }

    private void OnAudioAvailable(object? sender, WaveInEventArgs e)
    {
        if (!IsConnected || _sessionCts?.IsCancellationRequested == true) return;
        var bytes = e.Buffer.AsSpan(0, e.BytesRecorded).ToArray();
        _ = SendAudioChunkAsync(bytes);
    }

    private async Task SendAudioChunkAsync(byte[] bytes)
    {
        try
        {
            await SendJsonAsync(new
            {
                type = "input_audio_buffer.append",
                audio = Convert.ToBase64String(bytes)
            }, _sessionCts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();

        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket is { State: WebSocketState.Open } socket)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(message.ToArray());
                HandleServerEvent(json);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
            StatusChanged?.Invoke(this, "Realtime voice disconnected unexpectedly.");
        }
        finally
        {
            message.Dispose();
        }
    }

    private void HandleServerEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("type", out var typeElement)) return;
        var type = typeElement.GetString();

        switch (type)
        {
            case "session.created":
            case "session.updated":
                StatusChanged?.Invoke(this, "Realtime voice ready.");
                break;
            case "input_audio_buffer.speech_started":
                _playbackBuffer?.ClearBuffer();
                _currentInputTranscript = string.Empty;
                UserTranscriptDelta?.Invoke(this, string.Empty);
                StatusChanged?.Invoke(this, "Listening...");
                break;
            case "input_audio_buffer.speech_stopped":
                StatusChanged?.Invoke(this, "Thinking...");
                break;
            case "conversation.item.input_audio_transcription.delta":
                AppendInputTranscript(ReadString(doc.RootElement, "delta"));
                break;
            case "conversation.item.input_audio_transcription.completed":
            case "conversation.item.input_audio_transcription.done":
                CompleteInputTranscript(ReadString(doc.RootElement, "transcript"));
                break;
            case "response.output_audio.delta":
                AddAudioDelta(ReadString(doc.RootElement, "delta"));
                break;
            case "response.output_audio_transcript.delta":
                AppendAssistantTranscript(ReadString(doc.RootElement, "delta"));
                break;
            case "response.output_audio_transcript.done":
                CompleteAssistantTranscript(ReadString(doc.RootElement, "transcript"));
                break;
            case "response.done":
                if (!string.IsNullOrWhiteSpace(_currentResponseTranscript))
                    CompleteAssistantTranscript(_currentResponseTranscript);
                StatusChanged?.Invoke(this, "Ready. Keep speaking when you want.");
                break;
            case "error":
                Error?.Invoke(this, ReadNestedError(doc.RootElement));
                break;
        }
    }

    private void AddAudioDelta(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64) || _playbackBuffer is null) return;
        var bytes = Convert.FromBase64String(base64);
        _playbackBuffer.AddSamples(bytes, 0, bytes.Length);
    }

    private void AppendInputTranscript(string? delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        _currentInputTranscript += delta;
        UserTranscriptDelta?.Invoke(this, _currentInputTranscript);
    }

    private void CompleteInputTranscript(string? transcript)
    {
        var final = string.IsNullOrWhiteSpace(transcript) ? _currentInputTranscript : transcript;
        if (string.IsNullOrWhiteSpace(final)) return;
        _currentInputTranscript = string.Empty;
        UserTranscriptCompleted?.Invoke(this, final.Trim());
    }

    private void AppendAssistantTranscript(string? delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        _currentResponseTranscript += delta;
        AssistantTranscriptDelta?.Invoke(this, _currentResponseTranscript);
    }

    private void CompleteAssistantTranscript(string? transcript)
    {
        var final = string.IsNullOrWhiteSpace(transcript) ? _currentResponseTranscript : transcript;
        if (string.IsNullOrWhiteSpace(final)) return;
        _currentResponseTranscript = string.Empty;
        AssistantTranscriptCompleted?.Invoke(this, final.Trim());
    }

    private async Task SendJsonAsync(object payload, CancellationToken cancellationToken)
    {
        EnsureConnected();
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket!.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void EnsureConnected()
    {
        if (!IsConnected) throw new InvalidOperationException("Realtime voice session is not connected.");
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) ? property.GetString() : null;

    private static string ReadNestedError(JsonElement element)
    {
        if (!element.TryGetProperty("error", out var error)) return "Realtime API error.";
        if (error.TryGetProperty("message", out var message)) return message.GetString() ?? "Realtime API error.";
        return error.ToString();
    }

    public void Dispose()
    {
        _ = StopAsync();
        _sendLock.Dispose();
    }
}

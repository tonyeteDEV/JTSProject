using JTS.Data;
using NAudio.Wave;

namespace JTS_App.Services;

public sealed class AudioCaptureService : IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<string?>? _stopCompletion;

    public bool IsRecording => _waveIn is not null;
    public string? CurrentFilePath { get; private set; }

    public string Start()
    {
        if (_waveIn is not null) return CurrentFilePath!;

        AppPaths.EnsureCreated();
        CurrentFilePath = Path.Combine(AppPaths.AppDataRoot, $"voice-task-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 100
        };
        _writer = new WaveFileWriter(CurrentFilePath, _waveIn.WaveFormat);
        _stopCompletion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waveIn.DataAvailable += (_, e) => _writer?.Write(e.Buffer, 0, e.BytesRecorded);
        _waveIn.RecordingStopped += (_, _) =>
        {
            var completedPath = CurrentFilePath;
            try
            {
                _writer?.Dispose();
            }
            finally
            {
                _writer = null;
                _waveIn?.Dispose();
                _waveIn = null;
                _stopCompletion?.TrySetResult(completedPath);
            }
        };
        _waveIn.StartRecording();
        return CurrentFilePath;
    }

    public async Task<string?> StopAsync()
    {
        if (_waveIn is null) return CurrentFilePath;

        var completion = _stopCompletion;
        _waveIn.StopRecording();
        return completion is null ? CurrentFilePath : await completion.Task;
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _waveIn?.Dispose();
        _writer = null;
        _waveIn = null;
        _stopCompletion?.TrySetResult(CurrentFilePath);
    }
}

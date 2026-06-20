using System.Text;
using Whisper.net;

namespace JTS.AI;

public sealed class WhisperTranscriber : IDisposable
{
    private readonly SemaphoreSlim _factoryLock = new(1, 1);
    private WhisperFactory? _factory;
    private string? _loadedModelPath;

    public async Task<string> TranscribeAsync(string modelPath, string wavPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Whisper model file was not found.", modelPath);
        if (!File.Exists(wavPath))
            throw new FileNotFoundException("Audio file was not found.", wavPath);

        var factory = await GetFactoryAsync(modelPath, cancellationToken);
        using var processor = factory.CreateBuilder()
            .WithLanguage("es")
            .WithThreads(Math.Max(1, Environment.ProcessorCount - 1))
            .WithNoContext()
            .Build();

        await using var stream = File.OpenRead(wavPath);
        var sb = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(stream, cancellationToken))
        {
            sb.Append(segment.Text);
            sb.Append(' ');
        }

        return sb.ToString().Trim();
    }

    private async Task<WhisperFactory> GetFactoryAsync(string modelPath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(modelPath);
        if (_factory is not null && string.Equals(_loadedModelPath, fullPath, StringComparison.OrdinalIgnoreCase))
            return _factory;

        await _factoryLock.WaitAsync(cancellationToken);
        try
        {
            if (_factory is not null && string.Equals(_loadedModelPath, fullPath, StringComparison.OrdinalIgnoreCase))
                return _factory;

            _factory?.Dispose();
            _factory = WhisperFactory.FromPath(fullPath);
            _loadedModelPath = fullPath;
            return _factory;
        }
        finally
        {
            _factoryLock.Release();
        }
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _factoryLock.Dispose();
    }
}

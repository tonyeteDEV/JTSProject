using System.Text.RegularExpressions;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace JTS_App.Services;

public sealed class SpeechPlaybackService : IDisposable
{
    private readonly MediaPlayer _player = new();
    private readonly SemaphoreSlim _playbackLock = new(1, 1);
    private readonly AppSettingsService _settings;

    public SpeechPlaybackService(AppSettingsService settings)
    {
        _settings = settings;
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        var cleanText = CleanForSpeech(text);
        if (string.IsNullOrWhiteSpace(cleanText)) return;

        await _playbackLock.WaitAsync(cancellationToken);
        try
        {
            using var synthesizer = new SpeechSynthesizer();
            synthesizer.Voice = SelectVoice();
            synthesizer.Options.SpeakingRate = await GetSpeakingRateAsync();
            synthesizer.Options.AudioPitch = 1.0;
            using var stream = await synthesizer.SynthesizeTextToStreamAsync(cleanText);
            using var source = MediaSource.CreateFromStream(stream, stream.ContentType);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnEnded(MediaPlayer sender, object args) => completion.TrySetResult();
            void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
                completion.TrySetException(new InvalidOperationException(args.ErrorMessage));

            _player.MediaEnded += OnEnded;
            _player.MediaFailed += OnFailed;
            try
            {
                _player.Source = source;
                _player.Play();

                await using var _ = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
                await completion.Task;
            }
            finally
            {
                _player.MediaEnded -= OnEnded;
                _player.MediaFailed -= OnFailed;
                _player.Source = null;
            }
        }
        finally
        {
            _playbackLock.Release();
        }
    }

    public void Stop()
    {
        _player.Pause();
        _player.Source = null;
    }

    private static string CleanForSpeech(string text)
    {
        var withoutCodeBlocks = Regex.Replace(text, "```[\\s\\S]*?```", " ");
        var withoutTables = Regex.Replace(withoutCodeBlocks, @"^\s*\|.*\|\s*$", " ", RegexOptions.Multiline);
        var withoutMarkdown = Regex.Replace(withoutTables, @"[#>*_`|\[\]\(\)]", " ");
        withoutMarkdown = Regex.Replace(withoutMarkdown, @"https?://\S+", " ");
        return Regex.Replace(withoutMarkdown, @"\s+", " ").Trim();
    }

    private static VoiceInformation SelectVoice()
    {
        var voices = SpeechSynthesizer.AllVoices;
        return voices.FirstOrDefault(v => v.Language.StartsWith("es-ES", StringComparison.OrdinalIgnoreCase))
            ?? voices.FirstOrDefault(v => v.Language.StartsWith("es", StringComparison.OrdinalIgnoreCase))
            ?? SpeechSynthesizer.DefaultVoice;
    }

    private async Task<double> GetSpeakingRateAsync()
    {
        var configured = await _settings.GetVoiceSpeakingRateAsync();
        if (!double.TryParse(configured, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate))
            rate = 1.05;

        return Math.Clamp(rate, 0.75, 1.45);
    }

    public void Dispose()
    {
        _player.Dispose();
        _playbackLock.Dispose();
    }
}

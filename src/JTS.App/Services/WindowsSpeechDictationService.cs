using System.Text;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace JTS_App.Services;

public sealed class WindowsSpeechDictationService : IDisposable
{
    private readonly StringBuilder _finalText = new();
    private SpeechRecognizer? _recognizer;
    private string _hypothesis = string.Empty;
    private bool _isRunning;

    public event EventHandler<SpeechDictationTextChangedEventArgs>? TextChanged;

    public bool IsRunning => _isRunning;

    public async Task StartAsync()
    {
        if (_isRunning) return;

        _finalText.Clear();
        _hypothesis = string.Empty;

        var language = SpeechRecognizer.SupportedGrammarLanguages
            .FirstOrDefault(l => l.LanguageTag.StartsWith("es", StringComparison.OrdinalIgnoreCase))
            ?? SpeechRecognizer.SystemSpeechLanguage
            ?? new Language("es-ES");

        _recognizer?.Dispose();
        _recognizer = new SpeechRecognizer(language);
        _recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
        _recognizer.HypothesisGenerated += OnHypothesisGenerated;
        _recognizer.ContinuousRecognitionSession.ResultGenerated += OnResultGenerated;

        var result = await _recognizer.CompileConstraintsAsync();
        if (result.Status != SpeechRecognitionResultStatus.Success)
            throw new InvalidOperationException($"Windows speech recognizer failed to initialize: {result.Status}");

        await _recognizer.ContinuousRecognitionSession.StartAsync();
        _isRunning = true;
    }

    public async Task<string> StopAsync()
    {
        if (_recognizer is not null && _isRunning)
            await _recognizer.ContinuousRecognitionSession.StopAsync();

        _isRunning = false;
        var final = _finalText.ToString().Trim();
        var hypothesis = _hypothesis.Trim();

        if (string.IsNullOrWhiteSpace(final)) return hypothesis;
        if (string.IsNullOrWhiteSpace(hypothesis)) return final;
        if (final.Contains(hypothesis, StringComparison.OrdinalIgnoreCase)) return final;

        return $"{final} {hypothesis}".Trim();
    }

    private void OnHypothesisGenerated(SpeechRecognizer sender, SpeechRecognitionHypothesisGeneratedEventArgs args)
    {
        _hypothesis = args.Hypothesis.Text ?? string.Empty;
        RaiseTextChanged();
    }

    private void OnResultGenerated(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        if (args.Result.Status != SpeechRecognitionResultStatus.Success) return;
        var text = args.Result.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        if (_finalText.Length > 0)
            _finalText.Append(' ');
        _finalText.Append(text.Trim());
        _hypothesis = string.Empty;
        RaiseTextChanged();
    }

    private void RaiseTextChanged()
    {
        var final = _finalText.ToString().Trim();
        var hypothesis = _hypothesis.Trim();
        var text = string.IsNullOrWhiteSpace(final)
            ? hypothesis
            : string.IsNullOrWhiteSpace(hypothesis)
                ? final
                : $"{final} {hypothesis}".Trim();

        TextChanged?.Invoke(this, new SpeechDictationTextChangedEventArgs(final, hypothesis, text));
    }

    public void Dispose()
    {
        if (_recognizer is null) return;
        _recognizer.HypothesisGenerated -= OnHypothesisGenerated;
        _recognizer.ContinuousRecognitionSession.ResultGenerated -= OnResultGenerated;
        _recognizer.Dispose();
    }
}

public sealed record SpeechDictationTextChangedEventArgs(string FinalText, string Hypothesis, string Text);

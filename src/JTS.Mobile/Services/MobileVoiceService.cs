using System.Globalization;

#if ANDROID
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Speech;
using Android.Speech.Tts;
using Java.Util;
#endif

namespace JTS.Mobile.Services;

public sealed class MobileVoiceService
{
#if ANDROID
    private SpeechRecognizer? _recognizer;
    private Intent? _speechIntent;
    private Android.Speech.Tts.TextToSpeech? _tts;
    private TaskCompletionSource<bool>? _ttsReady;
    private bool _isListening;
#endif

    public event EventHandler<string>? PartialText;
    public event EventHandler<string>? FinalText;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? Error;

    public async Task StartListeningAsync()
    {
#if ANDROID
        if (_isListening) return;

        var permission = await Permissions.RequestAsync<Permissions.Microphone>();
        if (permission != PermissionStatus.Granted)
        {
            Error?.Invoke(this, "Microphone permission denied.");
            return;
        }

        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        if (!SpeechRecognizer.IsRecognitionAvailable(context))
        {
            Error?.Invoke(this, "Android speech recognition is not available.");
            return;
        }

        _recognizer?.Destroy();
        _recognizer = SpeechRecognizer.CreateSpeechRecognizer(context);
        if (_recognizer is null)
        {
            Error?.Invoke(this, "Couldn't start Android speech recognition.");
            return;
        }

        _recognizer.SetRecognitionListener(new Listener(this));
        _speechIntent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
        _speechIntent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
        _speechIntent.PutExtra(RecognizerIntent.ExtraLanguage, "es-ES");
        _speechIntent.PutExtra(RecognizerIntent.ExtraPartialResults, true);
        _speechIntent.PutExtra(RecognizerIntent.ExtraMaxResults, 3);
        _isListening = true;
        StatusChanged?.Invoke(this, "Listening...");
        _recognizer.StartListening(_speechIntent);
#else
        await Task.CompletedTask;
        Error?.Invoke(this, "Voice is only available on Android.");
#endif
    }

    public Task StopListeningAsync()
    {
#if ANDROID
        if (!_isListening) return Task.CompletedTask;
        _recognizer?.StopListening();
        _isListening = false;
        StatusChanged?.Invoke(this, "Processing speech...");
#endif
        return Task.CompletedTask;
    }

    public async Task SpeakAsync(string text, double rate = 1.05)
    {
#if ANDROID
        if (string.IsNullOrWhiteSpace(text)) return;
        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        if (_tts is null)
        {
            _ttsReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _tts = new Android.Speech.Tts.TextToSpeech(context, new TtsInitListener(_ttsReady));
            await _ttsReady.Task;
            _tts.SetLanguage(Java.Util.Locale.ForLanguageTag("es-ES"));
        }

        _tts.SetSpeechRate((float)Math.Clamp(rate, 0.75, 1.45));
        _tts.Speak(text, QueueMode.Flush, null, "jts-agent");
#else
        await Task.CompletedTask;
#endif
    }

    public void StopSpeaking()
    {
#if ANDROID
        _tts?.Stop();
#endif
    }

    public void Dispose()
    {
#if ANDROID
        _recognizer?.Destroy();
        _tts?.Stop();
        _tts?.Shutdown();
#endif
    }

#if ANDROID
    private sealed class Listener : Java.Lang.Object, IRecognitionListener
    {
        private readonly MobileVoiceService _owner;

        public Listener(MobileVoiceService owner)
        {
            _owner = owner;
        }

        public void OnBeginningOfSpeech() => _owner.StatusChanged?.Invoke(_owner, "I'm listening...");
        public void OnBufferReceived(byte[]? buffer) { }
        public void OnEndOfSpeech()
        {
            _owner._isListening = false;
            _owner.StatusChanged?.Invoke(_owner, "Processing speech...");
        }
        public void OnEvent(int eventType, Bundle? @params) { }
        public void OnReadyForSpeech(Bundle? @params) => _owner.StatusChanged?.Invoke(_owner, "Speak whenever you're ready.");
        public void OnRmsChanged(float rmsdB) { }

        public void OnError([GeneratedEnum] SpeechRecognizerError error)
        {
            _owner._isListening = false;
            _owner.Error?.Invoke(_owner, $"Voice: {error}");
        }

        public void OnPartialResults(Bundle? partialResults)
        {
            var text = ExtractBest(partialResults);
            if (!string.IsNullOrWhiteSpace(text))
                _owner.PartialText?.Invoke(_owner, text);
        }

        public void OnResults(Bundle? results)
        {
            _owner._isListening = false;
            var text = ExtractBest(results);
            if (!string.IsNullOrWhiteSpace(text))
                _owner.FinalText?.Invoke(_owner, text);
            else
                _owner.StatusChanged?.Invoke(_owner, "I didn't detect any speech.");
        }

        private static string ExtractBest(Bundle? bundle)
        {
            var matches = bundle?.GetStringArrayList(SpeechRecognizer.ResultsRecognition);
            return matches?.FirstOrDefault() ?? string.Empty;
        }
    }

    private sealed class TtsInitListener : Java.Lang.Object, Android.Speech.Tts.TextToSpeech.IOnInitListener
    {
        private readonly TaskCompletionSource<bool> _ready;

        public TtsInitListener(TaskCompletionSource<bool> ready)
        {
            _ready = ready;
        }

        public void OnInit([GeneratedEnum] OperationResult status) =>
            _ready.TrySetResult(status == OperationResult.Success);
    }
#endif
}

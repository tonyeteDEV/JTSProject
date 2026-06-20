using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using JTS.Data;
using JTS_App.Services;
using Microsoft.UI;
using Windows.UI;

namespace JTS_App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private const string WhisperRecommendedModelName = "ggml-large-v3-turbo.bin";
    private const string WhisperRecommendedModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin";
    private const string VoskRecommendedModelName = "vosk-model-small-es-0.42";
    private const string VoskRecommendedModelArchiveName = VoskRecommendedModelName + ".zip";
    private const string VoskRecommendedModelUrl = "https://alphacephei.com/vosk/models/vosk-model-small-es-0.42.zip";
    private const string VoskAccurateModelName = "vosk-model-es-0.42";
    private const string VoskAccurateModelArchiveName = VoskAccurateModelName + ".zip";
    private const string VoskAccurateModelUrl = "https://alphacephei.com/vosk/models/vosk-model-es-0.42.zip";

    private readonly AppSettingsService _settings;
    private readonly AppAppearanceService _appearance;
    private readonly PomodoroAlarmService _pomodoroAlarm;
    private readonly DataversePreloadService _preload;
    private readonly HttpClient _httpClient = new();

    public IReadOnlyList<string> FontOptions => _appearance.FontOptions;

    [ObservableProperty] private string _deepSeekApiKey = string.Empty;
    [ObservableProperty] private string _openAIApiKey = string.Empty;
    [ObservableProperty] private string _whisperModelPath = string.Empty;
    [ObservableProperty] private string _voskModelPath = string.Empty;
    [ObservableProperty] private string _selectedFontFamily = "Segoe UI Variable Text";
    [ObservableProperty] private double _selectedFontSize = 13;
    [ObservableProperty] private string _d365TenantId = string.Empty;
    [ObservableProperty] private string _d365ClientId = string.Empty;
    [ObservableProperty] private string _d365ClientSecret = string.Empty;
    [ObservableProperty] private string _d365EnvironmentUrl = string.Empty;
    [ObservableProperty] private double _pomodoroWorkMinutes = 25;
    [ObservableProperty] private double _pomodoroShortBreakMinutes = 5;
    [ObservableProperty] private double _pomodoroLongBreakMinutes = 15;
    [ObservableProperty] private double _pomodoroLongBreakEvery = 4;
    [ObservableProperty] private string _pomodoroPreview = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _reviewTaskCommentsWithAi;
    [ObservableProperty] private bool _allowNaturalLanguageAgentActions;
    [ObservableProperty] private bool _remindersEnabled = true;
    [ObservableProperty] private double _remindersLeadDays;
    [ObservableProperty] private bool _isTestingPomodoroBell;
    [ObservableProperty] private bool _isDownloadingWhisperModel;
    [ObservableProperty] private bool _isDownloadingVoskModel;
    [ObservableProperty] private Color _preloadLoadedColor = ColorHelper.FromArgb(255, 47, 107, 86);
    [ObservableProperty] private Color _preloadLoadingColor = ColorHelper.FromArgb(255, 167, 120, 30);
    [ObservableProperty] private Color _preloadPendingColor = ColorHelper.FromArgb(255, 125, 52, 70);
    [ObservableProperty] private Color _preloadErrorColor = ColorHelper.FromArgb(255, 166, 58, 58);

    public string PomodoroBellTestButtonText => IsTestingPomodoroBell ? "Stop bell" : "Test pomodoro bell";
    public string SelectedFontSizeLabel => $"Font size {SelectedFontSize:0}px";

    partial void OnPomodoroWorkMinutesChanged(double value) => RefreshPomodoroPreview();
    partial void OnPomodoroShortBreakMinutesChanged(double value) => RefreshPomodoroPreview();
    partial void OnPomodoroLongBreakMinutesChanged(double value) => RefreshPomodoroPreview();
    partial void OnPomodoroLongBreakEveryChanged(double value) => RefreshPomodoroPreview();
    partial void OnSelectedFontSizeChanged(double value) => OnPropertyChanged(nameof(SelectedFontSizeLabel));

    partial void OnIsTestingPomodoroBellChanged(bool value) => OnPropertyChanged(nameof(PomodoroBellTestButtonText));

    public SettingsViewModel(
        AppSettingsService settings,
        AppAppearanceService appearance,
        PomodoroAlarmService pomodoroAlarm,
        DataversePreloadService preload)
    {
        _settings = settings;
        _appearance = appearance;
        _pomodoroAlarm = pomodoroAlarm;
        _preload = preload;
    }

    public async Task LoadAsync()
    {
        DeepSeekApiKey = await _settings.GetDeepSeekApiKeyAsync() ?? string.Empty;
        OpenAIApiKey = await _settings.GetOpenAIApiKeyAsync() ?? string.Empty;
        WhisperModelPath = await _settings.GetWhisperModelPathAsync() ?? string.Empty;
        VoskModelPath = await _settings.GetVoskModelPathAsync() ?? string.Empty;
        SelectedFontFamily = _appearance.CurrentFontFamily;
        SelectedFontSize = _appearance.CurrentFontSize;
        D365TenantId = await _settings.GetD365TenantIdAsync() ?? string.Empty;
        D365ClientId = await _settings.GetD365ClientIdAsync() ?? string.Empty;
        D365ClientSecret = await _settings.GetD365ClientSecretAsync() ?? string.Empty;
        D365EnvironmentUrl = await _settings.GetD365EnvironmentUrlAsync() ?? string.Empty;
        PomodoroWorkMinutes = await GetDoubleSettingAsync("Pomodoro.WorkMinutes", 25);
        PomodoroShortBreakMinutes = await GetDoubleSettingAsync("Pomodoro.ShortBreakMinutes", 5);
        PomodoroLongBreakMinutes = await GetDoubleSettingAsync("Pomodoro.LongBreakMinutes", 15);
        PomodoroLongBreakEvery = await GetDoubleSettingAsync("Pomodoro.LongBreakEvery", 4);
        ReviewTaskCommentsWithAi = string.Equals(await _settings.GetAsync("AI.ReviewTaskComments"), "true", StringComparison.OrdinalIgnoreCase);
        AllowNaturalLanguageAgentActions = string.Equals(await _settings.GetAsync("AI.AllowNaturalLanguageAgentActions"), "true", StringComparison.OrdinalIgnoreCase);
        RemindersEnabled = !string.Equals(await _settings.GetRemindersEnabledAsync(), "false", StringComparison.OrdinalIgnoreCase);
        RemindersLeadDays = await GetDoubleSettingAsync("Reminders.LeadDays", 0);
        var preloadColors = await _preload.GetConfiguredColorsAsync();
        PreloadLoadedColor = ParseColor(preloadColors.Loaded);
        PreloadLoadingColor = ParseColor(preloadColors.Loading);
        PreloadPendingColor = ParseColor(preloadColors.Pending);
        PreloadErrorColor = ParseColor(preloadColors.Error);
        RefreshPomodoroPreview();
    }

    public async Task SaveAsync()
    {
        await _settings.SetDeepSeekApiKeyAsync(DeepSeekApiKey);
        await _settings.SetOpenAIApiKeyAsync(OpenAIApiKey);
        await _settings.SetWhisperModelPathAsync(WhisperModelPath);
        await _settings.SetVoskModelPathAsync(VoskModelPath);
        await _appearance.SaveFontFamilyAsync(SelectedFontFamily);
        await _appearance.SaveFontSizeAsync(SelectedFontSize);
        await _settings.SetD365TenantIdAsync(D365TenantId);
        await _settings.SetD365ClientIdAsync(D365ClientId);
        await _settings.SetD365ClientSecretAsync(D365ClientSecret);
        await _settings.SetD365EnvironmentUrlAsync(D365EnvironmentUrl);
        await _settings.SetAsync("Pomodoro.WorkMinutes", ClampMinutes(PomodoroWorkMinutes, 1, 240).ToString(System.Globalization.CultureInfo.InvariantCulture));
        await _settings.SetAsync("Pomodoro.ShortBreakMinutes", ClampMinutes(PomodoroShortBreakMinutes, 1, 120).ToString(System.Globalization.CultureInfo.InvariantCulture));
        await _settings.SetAsync("Pomodoro.LongBreakMinutes", ClampMinutes(PomodoroLongBreakMinutes, 1, 180).ToString(System.Globalization.CultureInfo.InvariantCulture));
        await _settings.SetAsync("Pomodoro.LongBreakEvery", ClampLongBreakEvery(PomodoroLongBreakEvery).ToString(System.Globalization.CultureInfo.InvariantCulture));
        await _settings.SetAsync("AI.ReviewTaskComments", ReviewTaskCommentsWithAi ? "true" : "false");
        await _settings.SetAsync("AI.AllowNaturalLanguageAgentActions", AllowNaturalLanguageAgentActions ? "true" : "false");
        await _settings.SetRemindersEnabledAsync(RemindersEnabled);
        await _settings.SetRemindersLeadDaysAsync((int)Math.Clamp(Math.Round(RemindersLeadDays), 0, 30));
        await _preload.SaveColorsAsync(
            ToHex(PreloadLoadedColor),
            ToHex(PreloadLoadingColor),
            ToHex(PreloadPendingColor),
            ToHex(PreloadErrorColor));
        Status = $"Saved at {DateTime.Now:HH:mm:ss}";
    }

    public void TogglePomodoroBellTest()
    {
        if (IsTestingPomodoroBell)
        {
            _pomodoroAlarm.StopCompletionBell();
            IsTestingPomodoroBell = false;
            Status = "Pomodoro bell stopped.";
            return;
        }

        _pomodoroAlarm.StartCompletionBell();
        IsTestingPomodoroBell = true;
        Status = "Pomodoro bell test running.";
    }

    public async Task DownloadWhisperRecommendedModelAsync()
    {
        if (IsDownloadingWhisperModel) return;

        IsDownloadingWhisperModel = true;
        try
        {
            AppPaths.EnsureCreated();
            var targetPath = Path.Combine(AppPaths.ModelsDirectory, WhisperRecommendedModelName);
            if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
            {
                WhisperModelPath = targetPath;
                await _settings.SetWhisperModelPathAsync(targetPath);
                Status = "Whisper large-v3-turbo already exists and is selected.";
                return;
            }

            Status = "Downloading Whisper large-v3-turbo...";
            await using var source = await _httpClient.GetStreamAsync(WhisperRecommendedModelUrl);
            await using var destination = File.Create(targetPath);
            await source.CopyToAsync(destination);

            WhisperModelPath = targetPath;
            await _settings.SetWhisperModelPathAsync(targetPath);
            Status = "Whisper large-v3-turbo downloaded and selected.";
        }
        catch (Exception ex)
        {
            Status = $"Whisper model download failed: {ex.Message}";
        }
        finally
        {
            IsDownloadingWhisperModel = false;
        }
    }

    public async Task DownloadVoskRecommendedModelAsync()
    {
        await DownloadVoskModelAsync(
            VoskRecommendedModelName,
            VoskRecommendedModelArchiveName,
            VoskRecommendedModelUrl,
            "Vosk small Spanish");
    }

    public async Task DownloadVoskAccurateModelAsync()
    {
        await DownloadVoskModelAsync(
            VoskAccurateModelName,
            VoskAccurateModelArchiveName,
            VoskAccurateModelUrl,
            "Vosk accurate Spanish");
    }

    private async Task DownloadVoskModelAsync(string modelName, string archiveName, string url, string label)
    {
        if (IsDownloadingVoskModel) return;

        IsDownloadingVoskModel = true;
        try
        {
            AppPaths.EnsureCreated();
            var targetDirectory = Path.Combine(AppPaths.ModelsDirectory, modelName);
            if (Directory.Exists(targetDirectory) && Directory.EnumerateFileSystemEntries(targetDirectory).Any())
            {
                VoskModelPath = targetDirectory;
                await _settings.SetVoskModelPathAsync(targetDirectory);
                Status = $"{label} model already exists and is selected.";
                return;
            }

            var archivePath = Path.Combine(AppPaths.ModelsDirectory, archiveName);
            Status = $"Downloading {label} model...";
            await using (var source = await _httpClient.GetStreamAsync(url))
            await using (var destination = File.Create(archivePath))
            {
                await source.CopyToAsync(destination);
            }

            Status = $"Extracting {label} model...";
            ZipFile.ExtractToDirectory(archivePath, AppPaths.ModelsDirectory, overwriteFiles: true);
            File.Delete(archivePath);

            VoskModelPath = targetDirectory;
            await _settings.SetVoskModelPathAsync(targetDirectory);
            Status = $"{label} model downloaded and selected.";
        }
        catch (Exception ex)
        {
            Status = $"{label} model download failed: {ex.Message}";
        }
        finally
        {
            IsDownloadingVoskModel = false;
        }
    }

    private async Task<double> GetDoubleSettingAsync(string key, double fallback)
    {
        var value = await _settings.GetAsync(key);
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private void RefreshPomodoroPreview()
    {
        var every = ClampLongBreakEvery(PomodoroLongBreakEvery);
        var work = FormatMinutes(ClampMinutes(PomodoroWorkMinutes, 1, 240));
        var shortBreak = FormatMinutes(ClampMinutes(PomodoroShortBreakMinutes, 1, 120));
        var longBreak = FormatMinutes(ClampMinutes(PomodoroLongBreakMinutes, 1, 180));
        var segments = Enumerable.Range(1, every)
            .SelectMany(i => new[]
            {
                $"Work {work}",
                i == every ? $"Long break {longBreak}" : $"Break {shortBreak}"
            });
        PomodoroPreview = string.Join(" -> ", segments);
    }

    private static int ClampLongBreakEvery(double value) => Math.Clamp((int)Math.Round(value), 1, 12);

    private static double ClampMinutes(double value, double min, double max) => Math.Clamp(Math.Round(value), min, max);

    private static string FormatMinutes(double minutes) => $"{minutes:0}m";

    private static Color ParseColor(string hex)
    {
        var value = hex.Trim().TrimStart('#');
        if (value.Length != 6) return ColorHelper.FromArgb(255, 47, 107, 86);
        return ColorHelper.FromArgb(
            255,
            Convert.ToByte(value[..2], 16),
            Convert.ToByte(value.Substring(2, 2), 16),
            Convert.ToByte(value.Substring(4, 2), 16));
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}

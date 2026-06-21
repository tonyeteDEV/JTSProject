using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace JTS_App.Services;

public sealed class DataversePreloadService
{
    public const string LoadedColorKey = "Preload.Color.Loaded";
    public const string LoadingColorKey = "Preload.Color.Loading";
    public const string PendingColorKey = "Preload.Color.Pending";
    public const string ErrorColorKey = "Preload.Color.Error";

    public const string DefaultLoadedColor = "#2F6B56";
    public const string DefaultLoadingColor = "#A7781E";
    public const string DefaultPendingColor = "#7D3446";
    public const string DefaultErrorColor = "#A63A3A";

    private readonly DataverseAppDataService _data;
    private readonly AppSettingsService _settings;
    private readonly SemaphoreSlim _preloadLock = new(1, 1);
    private bool _hasStarted;

    public ObservableCollection<PreloadProgressItem> Items { get; } =
    [
        new("Tasks", PreloadState.Pending, DefaultPendingColor),
        new("Calendar", PreloadState.Pending, DefaultPendingColor),
        new("Comments", PreloadState.Pending, DefaultPendingColor),
        new("Time entries", PreloadState.Pending, DefaultPendingColor)
    ];

    public DataversePreloadService(DataverseAppDataService data, AppSettingsService settings)
    {
        _data = data;
        _settings = settings;
    }

    public async Task StartAsync(bool forceSync = false)
    {
        await LoadColorsAsync();
        if (_hasStarted && !forceSync) return;

        await _preloadLock.WaitAsync();
        try
        {
            if (_hasStarted && !forceSync) return;
            _hasStarted = true;
            if (forceSync) _data.ClearCache();

            Set("Tasks", PreloadState.Loading);
            Set("Calendar", PreloadState.Loading);
            var snapshot = await _data.LoadTaskSnapshotAsync(forceSync);
            Set("Tasks", PreloadState.Loaded);
            Set("Calendar", PreloadState.Loaded);

            var dataverseTaskIds = snapshot.Tasks
                .Where(task => task.DataverseId is not null)
                .Select(task => task.DataverseId!.Value)
                .Distinct()
                .ToList();

            Set("Comments", PreloadState.Loading);
            Set("Time entries", PreloadState.Loading);
            var commentsTask = _data.PreloadCommentsAsync(dataverseTaskIds, forceSync);
            var timeEntriesTask = _data.PreloadTimeEntriesAsync(dataverseTaskIds, forceSync);
            await Task.WhenAll(commentsTask, timeEntriesTask);
            Set("Comments", PreloadState.Loaded);
            Set("Time entries", PreloadState.Loaded);
        }
        catch (Exception ex)
        {
            App.Log("[DataversePreloadService] Preload failed: " + ex);
            foreach (var item in Items.Where(item => item.State == PreloadState.Loading))
                item.SetState(PreloadState.Error, ColorFor(PreloadState.Error));
        }
        finally
        {
            _preloadLock.Release();
        }
    }

    public async Task LoadColorsAsync()
    {
        foreach (var item in Items)
            item.SetState(item.State, ColorFor(item.State, await GetConfiguredColorsAsync()));
    }

    public async Task SaveColorsAsync(string loaded, string loading, string pending, string error)
    {
        await _settings.SetAsync(LoadedColorKey, NormalizeHex(loaded) ?? DefaultLoadedColor);
        await _settings.SetAsync(LoadingColorKey, NormalizeHex(loading) ?? DefaultLoadingColor);
        await _settings.SetAsync(PendingColorKey, NormalizeHex(pending) ?? DefaultPendingColor);
        await _settings.SetAsync(ErrorColorKey, NormalizeHex(error) ?? DefaultErrorColor);
        await LoadColorsAsync();
    }

    public async Task<PreloadColorSettings> GetConfiguredColorsAsync() => new(
        NormalizeHex(await _settings.GetAsync(LoadedColorKey)) ?? DefaultLoadedColor,
        NormalizeHex(await _settings.GetAsync(LoadingColorKey)) ?? DefaultLoadingColor,
        NormalizeHex(await _settings.GetAsync(PendingColorKey)) ?? DefaultPendingColor,
        NormalizeHex(await _settings.GetAsync(ErrorColorKey)) ?? DefaultErrorColor);

    private void Set(string label, PreloadState state)
    {
        var item = Items.First(i => i.Label == label);
        item.SetState(state, ColorFor(state));
    }

    private string ColorFor(PreloadState state) =>
        ColorFor(state, GetConfiguredColorsAsync().GetAwaiter().GetResult());

    private static string ColorFor(PreloadState state, PreloadColorSettings colors) => state switch
    {
        PreloadState.Loaded => colors.Loaded,
        PreloadState.Loading => colors.Loading,
        PreloadState.Error => colors.Error,
        _ => colors.Pending
    };

    private static string? NormalizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('#')) trimmed = "#" + trimmed;
        return trimmed.Length == 7 ? trimmed.ToUpperInvariant() : null;
    }
}

public sealed partial class PreloadProgressItem(string label, PreloadState state, string colorHex) : ObservableObject
{
    [ObservableProperty] private string _label = label;
    [ObservableProperty] private PreloadState _state = state;
    [ObservableProperty] private string _colorHex = colorHex;

    public string StatusText => State.ToString();

    partial void OnStateChanged(PreloadState value) => OnPropertyChanged(nameof(StatusText));

    public void SetState(PreloadState state, string colorHex)
    {
        State = state;
        ColorHex = colorHex;
    }
}

public enum PreloadState
{
    Pending,
    Loading,
    Loaded,
    Error
}

public sealed record PreloadColorSettings(string Loaded, string Loading, string Pending, string Error);

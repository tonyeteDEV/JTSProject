using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using JTS.Data;
using JTS.Data.Entities;
using JTS_App.Services;

namespace JTS_App.ViewModels;

public enum StreakCellKind
{
    Blank,  // padding cell so the 1st lands on the right weekday
    Future, // a day that hasn't happened yet
    Day     // a real, trackable day (past or today)
}

public sealed partial class StreakViewModel : ObservableObject
{
    private static readonly CultureInfo EnGb = CultureInfo.GetCultureInfo("en-GB");
    private static readonly JsonSerializerOptions CacheJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DataverseAppDataService _data;
    private readonly AppSettingsService _settings;

    private readonly Dictionary<DateTime, DayData> _daysData = new();
    private int _todayRefreshGeneration;
    private double _goalHours = 8;
    private StreakDay? _todayCell;

    [ObservableProperty] private int _year = DateTime.Today.Year;
    [ObservableProperty] private string _yearText = DateTime.Today.Year.ToString();
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canGoNext;

    [ObservableProperty] private string _goalText = "Daily goal: 8h";
    [ObservableProperty] private string _daysMetText = "0";
    [ObservableProperty] private string _daysPartialText = "0";
    [ObservableProperty] private string _currentStreakText = "0";
    [ObservableProperty] private string _bestStreakText = "0";
    [ObservableProperty] private bool _showCurrentStreak = true;

    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _noSelection = true;
    [ObservableProperty] private string _selectedDateText = string.Empty;
    [ObservableProperty] private string _selectedTotalText = string.Empty;
    [ObservableProperty] private bool _selectedHasComments;
    [ObservableProperty] private bool _selectedHasTasks;

    public ObservableCollection<StreakMonth> Months { get; } = new();
    public ObservableCollection<StreakMonthRow> MonthRows { get; } = new();
    public ObservableCollection<StreakTaskTime> SelectedTasks { get; } = new();
    public ObservableCollection<string> SelectedComments { get; } = new();

    private StreakDay? _selectedDay;

    public StreakViewModel(DataverseAppDataService data, AppSettingsService settings)
    {
        _data = data;
        _settings = settings;
    }

    partial void OnHasSelectionChanged(bool value) => NoSelection = !value;

    public async Task LoadAsync(bool forceSync = false)
    {
        if (IsBusy) return;
        var refreshTodayInBackground = false;
        IsBusy = true;
        try
        {
            var goalRaw = await _settings.GetFocusGoalHoursAsync();
            _goalHours = double.TryParse(goalRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var goal) && goal > 0 ? goal : 8;
            GoalText = $"Daily goal: {_goalHours:0.#}h";

            refreshTodayInBackground = await LoadYearDataAsync(forceSync);
            BuildMonths();
            BuildStats();
            SelectDefaultDay();
        }
        finally
        {
            IsBusy = false;
        }

        if (refreshTodayInBackground)
            _ = RefreshTodayInBackgroundAsync(Year, ++_todayRefreshGeneration);
    }

    public async Task GoToPreviousYearAsync()
    {
        Year--;
        await ReloadForYearChangeAsync();
    }

    public async Task GoToNextYearAsync()
    {
        if (!CanGoNext) return;
        Year++;
        await ReloadForYearChangeAsync();
    }

    private async Task ReloadForYearChangeAsync()
    {
        YearText = Year.ToString();
        CanGoNext = Year < DateTime.Today.Year;
        await LoadAsync();
    }

    private async Task<bool> LoadYearDataAsync(bool forceSync)
    {
        _daysData.Clear();
        CanGoNext = Year < DateTime.Today.Year;

        var today = DateTime.Today;
        if (!forceSync && await TryLoadCachedYearAsync())
            return Year == today.Year;

        await LoadYearDataFromDataverseAsync(forceSync);
        await SaveCachedYearAsync();
        return false;
    }

    private async Task LoadYearDataFromDataverseAsync(bool forceSync)
    {
        var yearStart = new DateTime(Year, 1, 1);
        var yearEnd = new DateTime(Year, 12, 31);

        var snapshot = await _data.LoadTaskSnapshotAsync(forceSync);
        var tasksWithDataverseId = snapshot.Tasks.Where(t => t.DataverseId is not null).ToList();
        var details = await _data.LoadTaskDetailsSnapshotAsync(tasksWithDataverseId.Select(t => t.DataverseId!.Value), forceSync);
        ApplyTaskDetailsToDays(tasksWithDataverseId, details, yearStart, yearEnd);
    }

    private async Task LoadSingleDayFromDataverseAsync(DateTime day)
    {
        var snapshot = await _data.LoadTaskSnapshotAsync(forceSync: false);
        var tasksWithDataverseId = snapshot.Tasks.Where(t => t.DataverseId is not null).ToList();
        var details = await _data.LoadTaskDetailsForSpainDateAsync(tasksWithDataverseId.Select(t => t.DataverseId!.Value), day);
        ApplyTaskDetailsToDays(tasksWithDataverseId, details, day.Date, day.Date);
    }

    private async Task RefreshTodayInBackgroundAsync(int requestedYear, int generation)
    {
        try
        {
            var today = DateTime.Today;
            if (requestedYear != today.Year) return;

            _daysData.Remove(today);
            await LoadSingleDayFromDataverseAsync(today);
            await SaveCachedYearAsync();

            if (generation != _todayRefreshGeneration || Year != requestedYear) return;

            var selectedDate = _selectedDay?.Date;
            BuildMonths();
            BuildStats();
            SelectDay(FindDayCell(selectedDate) ?? _todayCell ?? FindDayCell(today)!);
        }
        catch
        {
            // The cached view is still valid enough to use; the next manual refresh will retry Dataverse.
        }
    }

    private void ApplyTaskDetailsToDays(
        IReadOnlyList<TaskItem> tasksWithDataverseId,
        DataverseTaskDetailsSnapshot details,
        DateTime rangeStart,
        DateTime rangeEnd)
    {
        foreach (var task in tasksWithDataverseId)
        {
            var sessions = details.TimeEntriesByTask.TryGetValue(task.DataverseId!.Value, out var cachedSessions)
                ? cachedSessions
                : [];
            foreach (var session in sessions)
            {
                var minutes = session.ActualMinutes;
                if (minutes <= 0) continue;
                var day = DisplayFormat.ToSpainTime(session.StartedAt).Date;
                if (day < rangeStart || day > rangeEnd) continue;

                var data = GetOrCreate(day);
                data.Minutes += minutes;
                var projectName = task.Project?.Name ?? "No project";
                if (data.Tasks.TryGetValue(task.Title, out var existing))
                    data.Tasks[task.Title] = (projectName, existing.Minutes + minutes);
                else
                    data.Tasks[task.Title] = (projectName, minutes);
            }

            var comments = details.CommentsByTask.TryGetValue(task.DataverseId.Value, out var cachedComments)
                ? cachedComments
                : [];
            foreach (var comment in comments)
            {
                if (string.IsNullOrWhiteSpace(comment.Content)) continue;
                var day = DisplayFormat.ToSpainTime(comment.CreatedAt).Date;
                if (day < rangeStart || day > rangeEnd) continue;
                GetOrCreate(day).Comments.Add(comment.Content.Trim());
            }
        }
    }

    private async Task<bool> TryLoadCachedYearAsync()
    {
        try
        {
            var path = await GetCachePathAsync();
            if (!File.Exists(path)) return false;

            await using var stream = File.OpenRead(path);
            var cache = await JsonSerializer.DeserializeAsync<StreakYearCache>(stream, CacheJsonOptions);
            if (cache is null || cache.Version != 1 || cache.Year != Year) return false;

            foreach (var day in cache.Days)
            {
                var data = GetOrCreate(day.Date.Date);
                data.Minutes = Math.Max(0, day.Minutes);
                data.Comments.Clear();
                data.Comments.AddRange(day.Comments.Where(comment => !string.IsNullOrWhiteSpace(comment)));
                data.Tasks.Clear();
                foreach (var task in day.Tasks.Where(task => !string.IsNullOrWhiteSpace(task.Title)))
                    data.Tasks[task.Title] = (task.Project, Math.Max(0, task.Minutes));
            }

            return true;
        }
        catch
        {
            _daysData.Clear();
            return false;
        }
    }

    private async Task SaveCachedYearAsync()
    {
        try
        {
            var today = DateTime.Today;
            var cacheThrough = Year < today.Year
                ? new DateTime(Year, 12, 31)
                : Year == today.Year
                    ? today
                    : DateTime.MinValue;

            var days = _daysData
                .Where(kvp => kvp.Key.Year == Year && kvp.Key <= cacheThrough)
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => new StreakCachedDay(
                    kvp.Key,
                    kvp.Value.Minutes,
                    kvp.Value.Tasks
                        .OrderBy(task => task.Key)
                        .Select(task => new StreakCachedTask(task.Key, task.Value.Project, task.Value.Minutes))
                        .ToList(),
                    kvp.Value.Comments.ToList()))
                .ToList();

            var cache = new StreakYearCache(1, Year, DateTime.UtcNow, cacheThrough, days);
            var path = await GetCachePathAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, cache, CacheJsonOptions);
        }
        catch
        {
            // Cache failures should never block the Streak view; Dataverse remains the source of truth.
        }
    }

    private async Task<string> GetCachePathAsync()
    {
        AppPaths.EnsureCreated();
        var environment = (await _settings.GetD365EnvironmentUrlAsync())?.Trim().ToLowerInvariant() ?? "default";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(environment)))[..12];
        return Path.Combine(AppPaths.AppDataRoot, "cache", $"streak-{hash}-{Year}.json");
    }

    private DayData GetOrCreate(DateTime day)
    {
        if (!_daysData.TryGetValue(day, out var data))
        {
            data = new DayData();
            _daysData[day] = data;
        }
        return data;
    }

    private void BuildMonths()
    {
        Months.Clear();
        MonthRows.Clear();
        _todayCell = null;
        var goalMinutes = Math.Max(1, _goalHours * 60);
        var today = DateTime.Today;

        for (var month = 1; month <= 12; month++)
        {
            var first = new DateTime(Year, month, 1);
            var daysInMonth = DateTime.DaysInMonth(Year, month);
            var cells = new List<StreakDay>();

            // Leading blanks so the 1st sits under the correct weekday (Monday-first).
            var lead = ((int)first.DayOfWeek + 6) % 7;
            for (var i = 0; i < lead; i++)
                cells.Add(StreakDay.Blank());

            for (var d = 1; d <= daysInMonth; d++)
            {
                var date = new DateTime(Year, month, d);
                if (date > today)
                {
                    cells.Add(StreakDay.Future(date));
                    continue;
                }

                var minutes = _daysData.TryGetValue(date, out var data) ? data.Minutes : 0;
                // Gradient: 0 = empty, 1 = goal reached. Floor any worked day so it stays visible.
                var intensity = minutes <= 0 ? 0 : Math.Clamp(minutes / goalMinutes, 0, 1);
                var tooltip = minutes > 0
                    ? $"{date:dd/MM/yyyy} · {FormatMinutes(minutes)}"
                    : $"{date:dd/MM/yyyy} · nothing tracked";

                var cell = StreakDay.Day(date, intensity, date == today, tooltip);
                cells.Add(cell);
                if (date == today) _todayCell = cell;
            }

            Months.Add(new StreakMonth(first.ToString("MMMM", EnGb), cells));
        }

        foreach (var row in Months
            .Select((month, index) => new { month, index })
            .GroupBy(item => item.index / 4)
            .Select(group => new StreakMonthRow(group.Select(item => item.month).ToList())))
        {
            MonthRows.Add(row);
        }
    }

    private void BuildStats()
    {
        var goalMinutes = Math.Max(1, _goalHours * 60);
        var metDays = _daysData
            .Where(kvp => kvp.Value.Minutes >= goalMinutes)
            .Select(kvp => kvp.Key)
            .ToHashSet();
        var partialDays = _daysData.Count(kvp => kvp.Value.Minutes > 0 && kvp.Value.Minutes < goalMinutes);

        DaysMetText = metDays.Count.ToString();
        DaysPartialText = partialDays.ToString();

        // Best streak: longest run of consecutive met days within the year.
        var best = 0;
        var run = 0;
        var yearEnd = new DateTime(Year, 12, 31);
        for (var date = new DateTime(Year, 1, 1); date <= yearEnd; date = date.AddDays(1))
        {
            if (metDays.Contains(date))
            {
                run++;
                best = Math.Max(best, run);
            }
            else
            {
                run = 0;
            }
        }
        BestStreakText = best.ToString();

        // Current streak only makes sense for the ongoing year.
        ShowCurrentStreak = Year == DateTime.Today.Year;
        if (ShowCurrentStreak)
        {
            var current = 0;
            var cursor = DateTime.Today;
            if (!metDays.Contains(cursor)) cursor = cursor.AddDays(-1);
            while (cursor.Year == Year && metDays.Contains(cursor))
            {
                current++;
                cursor = cursor.AddDays(-1);
            }
            CurrentStreakText = current.ToString();
        }
    }

    private void SelectDefaultDay()
    {
        if (Year == DateTime.Today.Year && _todayCell is not null)
            SelectDay(_todayCell);
        else
            ClearSelection();
    }

    private StreakDay? FindDayCell(DateTime? date)
    {
        if (date is null) return null;
        return Months
            .SelectMany(month => month.Cells)
            .FirstOrDefault(cell => cell.Kind == StreakCellKind.Day && cell.Date == date.Value.Date);
    }

    public void SelectDay(StreakDay day)
    {
        if (day.Kind != StreakCellKind.Day) return;

        if (_selectedDay is not null) _selectedDay.IsSelected = false;
        _selectedDay = day;
        day.IsSelected = true;

        SelectedTasks.Clear();
        SelectedComments.Clear();

        SelectedDateText = day.Date.ToString("dddd, d MMMM yyyy", EnGb);
        if (_daysData.TryGetValue(day.Date, out var data) && (data.Minutes > 0 || data.Comments.Count > 0))
        {
            SelectedTotalText = $"{FormatMinutes(data.Minutes)} tracked";
            foreach (var (title, info) in data.Tasks.OrderByDescending(t => t.Value.Minutes))
                SelectedTasks.Add(new StreakTaskTime(title, info.Project, FormatMinutes(info.Minutes)));
            foreach (var comment in data.Comments)
                SelectedComments.Add(comment);
        }
        else
        {
            SelectedTotalText = "Nothing tracked this day";
        }

        SelectedHasTasks = SelectedTasks.Count > 0;
        SelectedHasComments = SelectedComments.Count > 0;
        HasSelection = true;
    }

    private void ClearSelection()
    {
        if (_selectedDay is not null) _selectedDay.IsSelected = false;
        _selectedDay = null;
        SelectedTasks.Clear();
        SelectedComments.Clear();
        HasSelection = false;
    }

    private static string FormatMinutes(int minutes)
    {
        if (minutes <= 0) return "0m";
        var hours = minutes / 60;
        var mins = minutes % 60;
        if (hours <= 0) return $"{mins}m";
        return mins == 0 ? $"{hours}h" : $"{hours}h {mins}m";
    }

    private sealed class DayData
    {
        public int Minutes;
        public Dictionary<string, (string Project, int Minutes)> Tasks { get; } = new();
        public List<string> Comments { get; } = new();
    }

    private sealed record StreakYearCache(
        int Version,
        int Year,
        DateTime CachedAtUtc,
        DateTime CacheThrough,
        List<StreakCachedDay> Days);

    private sealed record StreakCachedDay(
        DateTime Date,
        int Minutes,
        List<StreakCachedTask> Tasks,
        List<string> Comments);

    private sealed record StreakCachedTask(string Title, string Project, int Minutes);
}

public sealed partial class StreakDay : ObservableObject
{
    private StreakDay(StreakCellKind kind, DateTime date, double intensity, bool isToday, string tooltip)
    {
        Kind = kind;
        Date = date;
        Intensity = intensity;
        IsToday = isToday;
        Tooltip = tooltip;
        DayText = kind == StreakCellKind.Blank ? string.Empty : date.Day.ToString(CultureInfo.InvariantCulture);
        ColorHex = ColorFor(kind, intensity);
        IsCompleted = kind == StreakCellKind.Day && intensity >= 1;
    }

    public StreakCellKind Kind { get; }
    public DateTime Date { get; }
    public double Intensity { get; }
    public bool IsToday { get; }
    public string Tooltip { get; }
    public string DayText { get; }
    public string ColorHex { get; }
    public bool IsCompleted { get; }
    public string BorderHex => IsSelected
        ? "#F4F7FB"
        : IsCompleted
            ? "#D7B45A"
            : Kind == StreakCellKind.Blank
                ? "#181B20"
                : "#2A3038";

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(BorderHex));

    public static StreakDay Blank() => new(StreakCellKind.Blank, default, 0, false, string.Empty);
    public static StreakDay Future(DateTime date) => new(StreakCellKind.Future, date, 0, false, string.Empty);
    public static StreakDay Day(DateTime date, double intensity, bool isToday, string tooltip) =>
        new(StreakCellKind.Day, date, intensity, isToday, tooltip);

    private static string ColorFor(StreakCellKind kind, double intensity)
    {
        if (kind == StreakCellKind.Blank) return "#181B20";
        if (kind == StreakCellKind.Future) return "#1D2229";

        var progress = Math.Clamp(intensity, 0, 1);
        var (fromR, fromG, fromB, toR, toG, toB, localProgress) = progress <= 0.5
            ? (0x26, 0x2C, 0x34, 0xD6, 0xA8, 0x21, progress / 0.5)
            : (0xD6, 0xA8, 0x21, 0x2E, 0xA0, 0x43, (progress - 0.5) / 0.5);

        var r = (byte)Math.Round(fromR + (toR - fromR) * localProgress);
        var g = (byte)Math.Round(fromG + (toG - fromG) * localProgress);
        var b = (byte)Math.Round(fromB + (toB - fromB) * localProgress);
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}

public sealed record StreakMonth(string Name, IReadOnlyList<StreakDay> Cells);
public sealed record StreakMonthRow(IReadOnlyList<StreakMonth> Months);
public sealed record StreakTaskTime(string Title, string Project, string Time);

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using JTS.AI;
using JTS.Core;
using JTS.Data.Entities;
using JTS_App.Services;

namespace JTS_App.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly DataverseAppDataService _data;
    private readonly AppSettingsService _settings;
    private readonly DeepSeekClient _deepSeek;
    private readonly DataverseTimesheetService _timesheets;

    public ObservableCollection<DashboardProjectTimeRow> ProjectTimeRows { get; } = new();
    public ObservableCollection<DashboardTaskTimeRow> TaskTimeRows { get; } = new();
    public ObservableCollection<TimesheetPreviewLineView> TimesheetPreviewLines { get; } = new();

    [ObservableProperty] private string _openTasksText = "Open tasks: 0";
    [ObservableProperty] private string _weekTimeText = "This week: 0h";
    [ObservableProperty] private string _timesheetReadyText = "Ready for timesheet: 0h";
    [ObservableProperty] private DateTimeOffset? _timesheetStartDate;
    [ObservableProperty] private DateTimeOffset? _timesheetEndDate;
    [ObservableProperty] private string _timesheetStartDayText = string.Empty;
    [ObservableProperty] private string _timesheetEndDayText = string.Empty;
    [ObservableProperty] private string _timesheetStatus = "Select a date range and generate a preview.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasTimesheetPreview;
    [ObservableProperty] private string _timesheetPreviewTotal = "Total: 0h";

    private TimesheetPreview? _preview;

    public DashboardViewModel(
        DataverseAppDataService data,
        AppSettingsService settings,
        DeepSeekClient deepSeek,
        DataverseTimesheetService timesheets)
    {
        _data = data;
        _settings = settings;
        _deepSeek = deepSeek;
        _timesheets = timesheets;

        var weekStart = StartOfWeek(DateTime.Today);
        TimesheetStartDate = weekStart;
        TimesheetEndDate = weekStart.AddDays(6);
        RefreshTimesheetDateText();
    }

    partial void OnTimesheetStartDateChanged(DateTimeOffset? value) => RefreshTimesheetDateText();
    partial void OnTimesheetEndDateChanged(DateTimeOffset? value) => RefreshTimesheetDateText();

    private void RefreshTimesheetDateText()
    {
        TimesheetStartDayText = TimesheetStartDate is DateTimeOffset start ? FormatDateWithWeekday(start.Date) : "Select start date";
        TimesheetEndDayText = TimesheetEndDate is DateTimeOffset end ? FormatDateWithWeekday(end.Date) : "Select end date";
    }

    public async Task LoadAsync(bool forceSync = false)
    {
        var snapshot = await _data.LoadTaskSnapshotAsync(forceSync);
        var today = DateTime.Today;
        var weekStart = StartOfWeek(today);
        var weekEndExclusive = weekStart.AddDays(7);
        var timeEntries = await LoadTimeForTasksAsync(snapshot.Tasks, forceSync);
        var weekEntries = timeEntries
            .Where(e => DisplayFormat.ToSpainTime(e.Session.StartedAt).Date >= weekStart &&
                DisplayFormat.ToSpainTime(e.Session.StartedAt).Date < weekEndExclusive)
            .ToList();
        var pendingWeekEntries = weekEntries
            .Where(e => e.Session.TimesheetLineDataverseId is null)
            .ToList();

        OpenTasksText = $"Open tasks: {snapshot.Tasks.Count(t => t.Status != TaskItemStatus.Done && t.Status != TaskItemStatus.Cancelled)}";
        WeekTimeText = $"This week: {FormatHours(weekEntries.Sum(e => e.Session.ActualMinutes) / 60m)}";
        TimesheetReadyText = $"Ready for timesheet: {FormatHours(pendingWeekEntries.Sum(e => e.Session.ActualMinutes) / 60m)}";
        ProjectTimeRows.Clear();
        foreach (var row in weekEntries
            .Where(e => e.Task.Project is not null)
            .GroupBy(e => e.Task.Project!)
            .OrderByDescending(g => g.Sum(e => e.Session.ActualMinutes)))
        {
            var hours = FormatHours(row.Sum(e => e.Session.ActualMinutes) / 60m);
            ProjectTimeRows.Add(new DashboardProjectTimeRow(
                ProjectTitle(row.Key),
                ProjectCustomer(row.Key),
                ProjectDescription(row.Key),
                hours,
                "Dataverse"));
        }

        TaskTimeRows.Clear();
        foreach (var row in weekEntries
            .GroupBy(e => new { e.Task.Id, e.Task.Title, Project = e.Task.Project })
            .OrderByDescending(g => g.Sum(e => e.Session.ActualMinutes)))
        {
            var minutes = row.Sum(e => e.Session.ActualMinutes);
            TaskTimeRows.Add(new DashboardTaskTimeRow(
                row.Key.Title,
                row.Key.Project is null ? "No project" : ProjectTitle(row.Key.Project),
                row.Key.Project is null ? string.Empty : ProjectCustomer(row.Key.Project),
                FormatHours(minutes / 60m),
                row.Count()));
        }
    }

    public async Task BuildTimesheetPreviewAsync()
    {
        IsBusy = true;
        TimesheetStatus = "Generating timesheet preview from Dataverse...";
        HasTimesheetPreview = false;
        TimesheetPreviewLines.Clear();
        _preview = null;

        try
        {
            if (TimesheetStartDate is not DateTimeOffset startOffset || TimesheetEndDate is not DateTimeOffset endOffset)
            {
                TimesheetStatus = "Select a start date and an end date.";
                return;
            }

            var start = StartOfWeek(startOffset.Date);
            var end = StartOfWeek(endOffset.Date).AddDays(6);
            if (end < start)
            {
                TimesheetStatus = "End date must be after start date.";
                return;
            }
            TimesheetStartDate = start;
            TimesheetEndDate = end;

            var snapshot = await _data.LoadTaskSnapshotAsync();
            var timeEntries = await LoadTimeForTasksAsync(snapshot.Tasks);
            var commentsByTask = await LoadCommentsForTasksAsync(snapshot.Tasks);
            var rangeEntries = timeEntries
                .Where(e => e.Session.TimesheetLineDataverseId is null)
                .Where(e =>
                {
                    var day = DisplayFormat.ToSpainTime(e.Session.StartedAt).Date;
                    return day >= start && day <= end;
                })
                .ToList();

            var apiKey = await _settings.GetDeepSeekApiKeyAsync();
            var model = await _settings.GetDeepSeekModelAsync();
            var options = new DeepSeekRequestOptions(string.IsNullOrWhiteSpace(model) ? DeepSeekClient.DefaultModel : model!, ThinkingEnabled: false);
            var lines = new List<TimesheetPreviewLine>();

            foreach (var group in rangeEntries
                .Where(e => e.Task.Project?.DataverseId is not null)
                .GroupBy(e => new
                {
                    ProjectDataverseId = e.Task.Project!.DataverseId!.Value,
                    ProjectName = e.Task.Project.Name,
                    Date = DisplayFormat.ToSpainTime(e.Session.StartedAt).Date
                })
                .OrderBy(g => g.Key.Date)
                .ThenBy(g => g.Key.ProjectName))
            {
                var minutes = group.Sum(e => Math.Max(0, e.Session.ActualMinutes));
                if (minutes <= 0) continue;

                var tasks = group
                    .GroupBy(e => e.Task.DataverseId ?? Guid.Empty)
                    .Where(g => g.Key != Guid.Empty)
                    .Select(g => new TimesheetTaskContext(
                        g.First().Task.Title,
                        g.First().Task.WorkType.ToString(),
                        g.First().Task.Description ?? string.Empty,
                        g.Sum(e => e.Session.ActualMinutes)))
                    .ToList();
                var taskIds = group.Select(e => e.Task.DataverseId).Where(id => id is not null).Select(id => id!.Value).Distinct().ToHashSet();
                var dayComments = commentsByTask
                    .Where(pair => taskIds.Contains(pair.Key))
                    .SelectMany(pair => pair.Value)
                    .Where(c => c.TimesheetLineDataverseId is null)
                    .Where(c => DisplayFormat.ToSpainTime(c.CreatedAt).Date == group.Key.Date)
                    .ToList();
                var dayCommentContents = dayComments
                    .Select(c => c.Content)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .ToList();
                var commentIds = dayComments
                    .Select(c => c.DataverseId)
                    .Where(id => id is not null)
                    .Select(id => id!.Value)
                    .Distinct()
                    .ToList();

                var roundedHours = RoundUpToHalfHour(minutes);
                var summary = await BuildProfessionalSummaryAsync(apiKey, options, group.Key.ProjectName, group.Key.Date, tasks, dayCommentContents);
                var timeEntryIds = group
                    .Select(e => e.Session.DataverseId)
                    .Where(id => id is not null)
                    .Select(id => id!.Value)
                    .Distinct()
                    .ToList();

                lines.Add(new TimesheetPreviewLine(
                    LocalKey(group.Key.ProjectDataverseId, group.Key.Date),
                    group.Key.ProjectDataverseId,
                    group.Key.ProjectName,
                    group.Key.Date,
                    minutes,
                    roundedHours,
                    summary,
                    timeEntryIds,
                    commentIds));
            }

            _preview = new TimesheetPreview(start, end, lines);
            foreach (var line in lines)
                TimesheetPreviewLines.Add(new TimesheetPreviewLineView(line));

            HasTimesheetPreview = lines.Count > 0;
            TimesheetPreviewTotal = $"Total: {FormatHours(lines.Sum(l => l.RoundedHours))}";
            TimesheetStatus = lines.Count == 0
                ? "No tracked time was found in that range."
                : $"Preview ready: {lines.Count} line(s), {FormatHours(lines.Sum(l => l.RoundedHours))}.";
        }
        catch (Exception ex)
        {
            TimesheetStatus = $"Could not generate preview: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RegisterTimesheetAsync()
    {
        if (_preview is null || _preview.Lines.Count == 0) return;

        IsBusy = true;
        TimesheetStatus = "Creating timesheet in Dataverse...";
        try
        {
            var draft = new TimesheetRegistrationDraft(
                $"Timesheet {DisplayFormat.Date(_preview.StartDate)} - {DisplayFormat.Date(_preview.EndDate)}",
                _preview.StartDate,
                _preview.EndDate,
                _preview.Lines.Sum(l => l.RoundedHours),
                _preview.Lines.Select(l => new TimesheetLineRegistrationDraft(
                    l.LocalKey,
                    l.ProjectDataverseId,
                    l.ProjectName,
                    l.Date,
                    l.RoundedHours,
                    l.Summary,
                    l.TimeEntryDataverseIds,
                    l.CommentDataverseIds)).ToList());

            await _timesheets.CreateAsync(draft);
            TimesheetStatus = $"Timesheet created: {FormatHours(_preview.Lines.Sum(l => l.RoundedHours))} exported.";
            _preview = null;
            TimesheetPreviewLines.Clear();
            HasTimesheetPreview = false;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            TimesheetStatus = $"Could not create timesheet: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<List<DashboardTimeEntryContext>> LoadTimeForTasksAsync(IEnumerable<TaskItem> tasks, bool forceSync = false)
    {
        var entries = new List<DashboardTimeEntryContext>();
        foreach (var task in tasks.Where(t => t.DataverseId is not null))
        {
            var taskEntries = await _data.LoadTimeEntriesAsync(task.DataverseId!.Value, forceSync);
            entries.AddRange(taskEntries.Select(session => new DashboardTimeEntryContext(task, session)));
        }

        return entries;
    }

    private async Task<Dictionary<Guid, IReadOnlyList<TaskJournalEntry>>> LoadCommentsForTasksAsync(IEnumerable<TaskItem> tasks, bool forceSync = false)
    {
        var result = new Dictionary<Guid, IReadOnlyList<TaskJournalEntry>>();
        foreach (var task in tasks.Where(t => t.DataverseId is not null))
            result[task.DataverseId!.Value] = await _data.LoadCommentsAsync(task.DataverseId.Value, forceSync);
        return result;
    }

    private async Task<string> BuildProfessionalSummaryAsync(
        string? apiKey,
        DeepSeekRequestOptions options,
        string projectName,
        DateTime date,
        IReadOnlyList<TimesheetTaskContext> tasks,
        IReadOnlyList<string> comments)
    {
        var fallback = BuildFallbackSummary(tasks, comments);
        if (string.IsNullOrWhiteSpace(apiKey)) return fallback;

        try
        {
            var taskText = string.Join("\n", tasks.Select(t => $"- {t.Title} ({t.WorkType}, {t.Minutes} min): {t.Description}"));
            var commentText = comments.Count == 0 ? "- No task comments." : string.Join("\n", comments.Select(c => "- " + c));
            var response = await _deepSeek.ChatAsync(apiKey, new[]
            {
                new DeepSeekMessage("system", "Write professional, concise timesheet summaries in English. Be specific and concrete, and don't invent work that isn't in the context."),
                new DeepSeekMessage("user", $"""
Project: {projectName}
Day: {date:dd/MM/yyyy}
Tasks with time:
{taskText}

Comments for the day:
{commentText}

Return a single professional paragraph describing the tasks done and progress made.
""")
            }, options);

            return string.IsNullOrWhiteSpace(response) ? fallback : response.Trim();
        }
        catch
        {
            return fallback;
        }
    }

    private static string BuildFallbackSummary(IReadOnlyList<TimesheetTaskContext> tasks, IReadOnlyList<string> comments)
    {
        var titles = tasks.Select(t => t.Title).Distinct().Take(5).ToList();
        var summary = titles.Count == 0
            ? "Work done on project tasks."
            : "Work done on " + string.Join("; ", titles) + ".";
        if (comments.Count > 0)
            summary += " " + string.Join(" ", comments.Take(3));
        return summary;
    }

    private static decimal RoundUpToHalfHour(int minutes) =>
        Math.Ceiling(minutes / 30m) * 0.5m;

    private static string FormatHours(decimal hours) => $"{hours:0.##}h";

    private static string FormatDateWithWeekday(DateTime date) =>
        date.ToString("dddd, d MMMM yyyy", new CultureInfo("en-GB"));

    private static string ProjectTitle(Project project) => project.Name;

    private static string ProjectCustomer(Project project) =>
        string.IsNullOrWhiteSpace(project.Customer?.Name) ? "Customer: no customer assigned" : $"Customer: {project.Customer.Name}";

    private static string ProjectDescription(Project project) =>
        string.IsNullOrWhiteSpace(project.Description) ? "No project description" : project.Description.Trim();

    private static DateTime StartOfWeek(DateTime value)
    {
        var offset = ((int)value.DayOfWeek + 6) % 7;
        return value.Date.AddDays(-offset);
    }

    private static string LocalKey(Guid projectId, DateTime date) => $"{projectId:N}:{date:yyyyMMdd}";
}

public sealed record DashboardTimeEntryContext(TaskItem Task, PomodoroSession Session);
public sealed record DashboardProjectTimeRow(string Project, string Customer, string Description, string Total, string Exported);
public sealed record DashboardTaskTimeRow(string Task, string Project, string Customer, string Total, int Entries);
public sealed record TimesheetTaskContext(string Title, string WorkType, string Description, int Minutes);
public sealed record TimesheetPreview(DateTime StartDate, DateTime EndDate, IReadOnlyList<TimesheetPreviewLine> Lines);
public sealed record TimesheetPreviewLine(
    string LocalKey,
    Guid ProjectDataverseId,
    string ProjectName,
    DateTime Date,
    int ActualMinutes,
    decimal RoundedHours,
    string Summary,
    IReadOnlyList<Guid> TimeEntryDataverseIds,
    IReadOnlyList<Guid> CommentDataverseIds);

public sealed class TimesheetPreviewLineView
{
    public TimesheetPreviewLineView(TimesheetPreviewLine line)
    {
        Project = line.ProjectName;
        Date = DisplayFormat.Date(line.Date);
        Actual = $"{line.ActualMinutes} min";
        Rounded = $"{line.RoundedHours:0.##}h";
        Summary = line.Summary;
    }

    public string Project { get; }
    public string Date { get; }
    public string Actual { get; }
    public string Rounded { get; }
    public string Summary { get; }
}

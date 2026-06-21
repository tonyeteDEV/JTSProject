using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using JTS.Core;
using JTS.Data.Entities;
using JTS_App.Services;
using Microsoft.UI.Xaml.Media;

namespace JTS_App.ViewModels;

public partial class TasksViewModel : ObservableObject
{
    private readonly DataverseAppDataService _data;
    private readonly Dictionary<int, TaskItem> _tasksById = new();

    public ObservableCollection<TaskRowView> Tasks { get; } = new();
    public ObservableCollection<TaskRowView> AssignedTasks { get; } = new();
    public ObservableCollection<TaskRowView> OngoingTasks { get; } = new();
    public ObservableCollection<TaskRowView> TestingTasks { get; } = new();
    public ObservableCollection<TaskRowView> TestedTasks { get; } = new();
    public ObservableCollection<TaskRowView> UatTasks { get; } = new();
    public ObservableCollection<TaskRowView> ProductionTasks { get; } = new();
    public ObservableCollection<Project> AllProjects { get; } = new();
    public ObservableCollection<TaskJournalEntryView> SelectedTaskJournal { get; } = new();
    public ObservableCollection<TaskTimeByDayView> SelectedTaskTimeByDay { get; } = new();
    public ObservableCollection<TaskCalendarBlockView> SelectedTaskCalendarBlocks { get; } = new();
    public ObservableCollection<ChecklistItemView> SelectedTaskChecklist { get; } = new();
    public ObservableCollection<RecurrenceDayRow> RecurrenceDays { get; } = new();

    [ObservableProperty] private TaskRowView? _selectedTask;
    [ObservableProperty] private bool _isRecurrenceEditorVisible;
    [ObservableProperty] private bool _hasExistingRecurrence;
    [ObservableProperty] private DateTimeOffset? _recurrenceStartDate;
    [ObservableProperty] private DateTimeOffset? _recurrenceEndDate;
    [ObservableProperty] private TimeSpan _recurrenceMasterFrom = TimeSpan.FromHours(10);
    [ObservableProperty] private TimeSpan _recurrenceMasterTo = TimeSpan.FromHours(12);
    [ObservableProperty] private string _recurrenceStatus = string.Empty;
    [ObservableProperty] private string _selectedTaskChecklistProgress = "0/0";
    [ObservableProperty] private string _selectedTaskTimeTotalText = "Total tracked: 0m";
    [ObservableProperty] private bool _selectedTaskHasTimesheetLinks;
    [ObservableProperty] private int _assignedCount;
    [ObservableProperty] private int _ongoingCount;
    [ObservableProperty] private int _testingCount;
    [ObservableProperty] private int _testedCount;
    [ObservableProperty] private int _uatCount;
    [ObservableProperty] private int _productionCount;

    partial void OnSelectedTaskChanged(TaskRowView? value)
    {
        SelectedTaskHasTimesheetLinks = false;
        NotifySelectedTaskActionsChanged();
        _ = LoadSelectedTaskDetailsAsync();
    }

    partial void OnSelectedTaskHasTimesheetLinksChanged(bool value) => NotifySelectedTaskActionsChanged();

    public bool CanEditSelectedTask => SelectedTask is not null;
    public bool CanDeleteSelectedTask => SelectedTask is not null && !SelectedTaskHasTimesheetLinks;
    public bool CanChangeSelectedTaskStatus => SelectedTask is not null;
    public string SelectedTaskLockMessage => SelectedTaskHasTimesheetLinks
        ? "This task has time entries or comments already exported to a timesheet. Those records can't be edited or deleted, but you can still move the task and edit its details freely."
        : string.Empty;

    public TasksViewModel(DataverseAppDataService data)
    {
        _data = data;
        foreach (var (day, name) in new[]
        {
            (DayOfWeek.Monday, "Monday"), (DayOfWeek.Tuesday, "Tuesday"), (DayOfWeek.Wednesday, "Wednesday"),
            (DayOfWeek.Thursday, "Thursday"), (DayOfWeek.Friday, "Friday"), (DayOfWeek.Saturday, "Saturday"),
            (DayOfWeek.Sunday, "Sunday")
        })
        {
            RecurrenceDays.Add(new RecurrenceDayRow(day, name));
        }
    }

    public async Task LoadAsync(bool forceSync = false)
    {
        var snapshot = await _data.LoadTaskSnapshotAsync(forceSync);
        AllProjects.Clear();
        foreach (var project in snapshot.Projects.OrderBy(p => p.Name))
            AllProjects.Add(project);

        _tasksById.Clear();
        foreach (var task in snapshot.Tasks)
            _tasksById[task.Id] = task;

        Tasks.Clear();
        AssignedTasks.Clear();
        OngoingTasks.Clear();
        TestingTasks.Clear();
        TestedTasks.Clear();
        UatTasks.Clear();
        ProductionTasks.Clear();

        foreach (var task in snapshot.Tasks
            .Where(t => t.Status != TaskItemStatus.Cancelled)
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
            .ThenBy(t => t.Title))
        {
            var row = new TaskRowView(task);
            Tasks.Add(row);
            switch (task.Status)
            {
                case TaskItemStatus.Backlog:
                case TaskItemStatus.Todo:
                case TaskItemStatus.Assigned:
                    AssignedTasks.Add(row);
                    break;
                case TaskItemStatus.InProgress:
                case TaskItemStatus.Blocked:
                    OngoingTasks.Add(row);
                    break;
                case TaskItemStatus.Testing:
                    TestingTasks.Add(row);
                    break;
                case TaskItemStatus.Tested:
                    TestedTasks.Add(row);
                    break;
                case TaskItemStatus.UAT:
                    UatTasks.Add(row);
                    break;
                case TaskItemStatus.Done:
                    ProductionTasks.Add(row);
                    break;
            }
        }

        AssignedCount = AssignedTasks.Count;
        OngoingCount = OngoingTasks.Count;
        TestingCount = TestingTasks.Count;
        TestedCount = TestedTasks.Count;
        UatCount = UatTasks.Count;
        ProductionCount = ProductionTasks.Count;

        if (SelectedTask is not null && !_tasksById.ContainsKey(SelectedTask.Id))
            SelectedTask = null;
    }

    public async Task LoadJournalAsync()
    {
        await LoadSelectedTaskDetailsAsync();
    }

    public async Task LoadSelectedTaskDetailsAsync()
    {
        SelectedTaskJournal.Clear();
        SelectedTaskTimeByDay.Clear();
        SelectedTaskCalendarBlocks.Clear();
        SelectedTaskChecklist.Clear();
        SelectedTaskChecklistProgress = "0/0";
        SelectedTaskTimeTotalText = "Total tracked: 0m";
        SelectedTaskHasTimesheetLinks = false;
        if (SelectedTask?.DataverseId is not Guid taskDataverseId) return;

        if (_tasksById.TryGetValue(SelectedTask.Id, out var selectedTaskModel))
            RebuildChecklistView(selectedTaskModel);

        var details = await _data.LoadTaskDetailsSnapshotAsync([taskDataverseId], forceSync: true);
        var comments = details.CommentsByTask.TryGetValue(taskDataverseId, out var cachedComments)
            ? cachedComments
            : [];
        foreach (var comment in comments)
        {
            var isTransferred = comment.TimesheetLineDataverseId is not null;
            SelectedTaskJournal.Add(new TaskJournalEntryView(
                comment.DataverseId,
                DisplayFormat.DateTimeFromUtc(comment.CreatedAt),
                comment.Content,
                isTransferred,
                !isTransferred));
        }

        if (_tasksById.TryGetValue(SelectedTask.Id, out var task))
        {
            foreach (var block in task.ScheduleBlocks.OrderBy(b => b.Start))
            {
                SelectedTaskCalendarBlocks.Add(new TaskCalendarBlockView(
                    DisplayFormat.Date(block.Start),
                    $"{block.Start:HH:mm} - {block.End:HH:mm}",
                    FormatDurationSeconds(Math.Max(0, (int)Math.Round((block.End - block.Start).TotalSeconds)))));
            }
        }

        var timeEntries = details.TimeEntriesByTask.TryGetValue(taskDataverseId, out var cachedTimeEntries)
            ? cachedTimeEntries
            : [];
        SelectedTaskTimeTotalText = $"Total tracked: {FormatDurationSeconds(timeEntries.Sum(session => session.ActualMinutes) * 60)}";
        foreach (var session in timeEntries
            .Where(s => s.DataverseId is not null)
            .OrderByDescending(s => s.StartedAt))
        {
            var startedSpain = DisplayFormat.ToSpainTime(session.StartedAt);
            var endedSpain = session.EndedAt is DateTime endedAt
                ? DisplayFormat.ToSpainTime(endedAt)
                : startedSpain.AddMinutes(session.ActualMinutes);
            var isTransferred = session.TimesheetLineDataverseId is not null;
            SelectedTaskTimeByDay.Add(new TaskTimeByDayView(
                session.DataverseId!.Value,
                DisplayFormat.Date(startedSpain),
                $"{startedSpain:HH:mm} - {endedSpain:HH:mm}",
                FormatDurationSeconds(session.ActualMinutes * 60),
                startedSpain,
                endedSpain,
                session.ActualMinutes,
                isTransferred,
                !isTransferred));
        }

        SelectedTaskHasTimesheetLinks =
            SelectedTaskJournal.Any(entry => entry.IsTimesheetTransferred) ||
            SelectedTaskTimeByDay.Any(entry => entry.IsTimesheetTransferred);
    }

    public async Task<TaskItem> AddTaskAsync(int projectId, string title, string? description, WorkType workType,
        TaskPriority priority, int estimatedPomodoros, DateTime? dueDate)
    {
        var project = AllProjects.FirstOrDefault(p => p.Id == projectId)
            ?? throw new InvalidOperationException("Project not found.");
        if (project.DataverseId is not Guid projectDataverseId)
            throw new InvalidOperationException("Project is not linked to Dataverse.");

        var task = new TaskItem
        {
            ProjectId = project.Id,
            Project = project,
            Title = title,
            Description = description,
            WorkType = workType,
            Priority = priority,
            EstimatedPomodoros = estimatedPomodoros,
            DueDate = dueDate,
            Status = TaskItemStatus.Assigned
        };
        task.DataverseId = await _data.CreateTaskAsync(task, projectDataverseId);
        return task;
    }

    public async Task UpdateTaskAsync(TaskItem task)
    {
        var project = AllProjects.FirstOrDefault(p => p.Id == task.ProjectId)
            ?? throw new InvalidOperationException("Project not found.");
        if (project.DataverseId is not Guid projectDataverseId)
            throw new InvalidOperationException("Project is not linked to Dataverse.");

        await _data.UpdateTaskAsync(task, projectDataverseId);
    }

    public async Task SetStatusAsync(int taskId, TaskItemStatus status)
    {
        if (!_tasksById.TryGetValue(taskId, out var task) || task.DataverseId is not Guid taskDataverseId) return;
        await _data.SetStatusAsync(taskDataverseId, status);
    }

    public async Task DeleteTaskAsync(int taskId)
    {
        if (!_tasksById.TryGetValue(taskId, out var task) || task.DataverseId is not Guid taskDataverseId) return;
        await _data.DeleteTaskAsync(taskDataverseId);
    }

    public async Task AddJournalEntryAsync(int taskId, string content)
    {
        if (!_tasksById.TryGetValue(taskId, out var task) || task.DataverseId is not Guid taskDataverseId) return;
        await _data.AddCommentAsync(taskDataverseId, task.Title, content);
    }

    public async Task AddTimeEntryAsync(DateTime startedAtSpain, DateTime endedAtSpain, string? note)
    {
        if (SelectedTask is null || !_tasksById.TryGetValue(SelectedTask.Id, out var task) ||
            task.DataverseId is not Guid taskDataverseId) return;
        if (endedAtSpain <= startedAtSpain)
            throw new InvalidOperationException("End date and time must be after the start.");

        await _data.AddTimeEntryAsync(
            taskDataverseId,
            task.Title,
            DisplayFormat.SpainTimeToUtc(startedAtSpain),
            DisplayFormat.SpainTimeToUtc(endedAtSpain),
            string.IsNullOrWhiteSpace(note) ? "Manual time entry from task detail" : note.Trim());
        await LoadSelectedTaskDetailsAsync();
    }

    public async Task UpdateTimeEntryAsync(Guid timeEntryId, DateTime startedAtSpain, int actualMinutes)
    {
        await _data.UpdateTimeEntryAsync(timeEntryId, DisplayFormat.SpainTimeToUtc(startedAtSpain), actualMinutes);
        await LoadSelectedTaskDetailsAsync();
    }

    public async Task DeleteTimeEntryAsync(Guid timeEntryId)
    {
        await _data.DeleteTimeEntryAsync(timeEntryId);
        await LoadSelectedTaskDetailsAsync();
    }

    public async Task DeleteJournalEntryAsync(Guid commentId)
    {
        await _data.DeleteCommentAsync(commentId);
        await LoadSelectedTaskDetailsAsync();
    }

    public async Task AddChecklistItemAsync(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        if (SelectedTask is null || !_tasksById.TryGetValue(SelectedTask.Id, out var task) ||
            task.DataverseId is not Guid taskDataverseId) return;

        task.ChecklistItems.Add(new TaskChecklistItem
        {
            Title = title.Trim(),
            IsCompleted = false,
            SortOrder = task.ChecklistItems.Count
        });
        await PersistChecklistAsync(task, taskDataverseId);
    }

    public async Task ToggleChecklistItemAsync(int index, bool isCompleted)
    {
        if (SelectedTask is null || !_tasksById.TryGetValue(SelectedTask.Id, out var task) ||
            task.DataverseId is not Guid taskDataverseId) return;
        if (index < 0 || index >= task.ChecklistItems.Count) return;
        if (task.ChecklistItems[index].IsCompleted == isCompleted) return;

        task.ChecklistItems[index].IsCompleted = isCompleted;
        task.ChecklistItems[index].CompletedAt = isCompleted ? DateTime.UtcNow : null;
        await PersistChecklistAsync(task, taskDataverseId);
    }

    public async Task DeleteChecklistItemAsync(int index)
    {
        if (SelectedTask is null || !_tasksById.TryGetValue(SelectedTask.Id, out var task) ||
            task.DataverseId is not Guid taskDataverseId) return;
        if (index < 0 || index >= task.ChecklistItems.Count) return;

        task.ChecklistItems.RemoveAt(index);
        await PersistChecklistAsync(task, taskDataverseId);
    }

    private async Task PersistChecklistAsync(TaskItem task, Guid taskDataverseId)
    {
        await _data.UpdateTaskChecklistAsync(taskDataverseId, task.ChecklistItems);
        RebuildChecklistView(task);
    }

    private void RebuildChecklistView(TaskItem task)
    {
        SelectedTaskChecklist.Clear();
        for (var i = 0; i < task.ChecklistItems.Count; i++)
        {
            var item = task.ChecklistItems[i];
            SelectedTaskChecklist.Add(new ChecklistItemView(i, item.Title, item.IsCompleted));
        }
        var done = task.ChecklistItems.Count(i => i.IsCompleted);
        SelectedTaskChecklistProgress = $"{done}/{task.ChecklistItems.Count}";
    }

    public void OpenRecurrenceEditor()
    {
        if (SelectedTask is null || !_tasksById.TryGetValue(SelectedTask.Id, out var task)) return;

        foreach (var row in RecurrenceDays)
        {
            row.IsSelected = false;
            row.From = TimeSpan.FromHours(10);
            row.To = TimeSpan.FromHours(12);
        }
        RecurrenceMasterFrom = TimeSpan.FromHours(10);
        RecurrenceMasterTo = TimeSpan.FromHours(12);

        var def = ParseRecurrence(task.RecurrenceJson);
        if (def is not null)
        {
            HasExistingRecurrence = true;
            RecurrenceStartDate = new DateTimeOffset(def.StartDate);
            RecurrenceEndDate = new DateTimeOffset(def.EndDate);
            foreach (var dayDef in def.Days)
            {
                var row = RecurrenceDays.FirstOrDefault(r => r.Day == dayDef.Day);
                if (row is null) continue;
                row.IsSelected = true;
                if (TimeSpan.TryParse(dayDef.From, out var from)) row.From = from;
                if (TimeSpan.TryParse(dayDef.To, out var to)) row.To = to;
            }
            RecurrenceStatus = "Saving updates all recurring blocks from tomorrow onward (manual moves included).";
        }
        else
        {
            HasExistingRecurrence = false;
            RecurrenceStartDate = new DateTimeOffset(DateTime.Today);
            RecurrenceEndDate = new DateTimeOffset(DateTime.Today.AddDays(7));
            RecurrenceStatus = string.Empty;
        }

        IsRecurrenceEditorVisible = true;
    }

    public void ApplyMasterHoursToAllDays()
    {
        foreach (var row in RecurrenceDays)
        {
            row.From = RecurrenceMasterFrom;
            row.To = RecurrenceMasterTo;
        }
    }

    public void CancelRecurrenceEditor() => IsRecurrenceEditorVisible = false;

    public async Task SaveRecurrenceAsync()
    {
        if (SelectedTask is null || !_tasksById.TryGetValue(SelectedTask.Id, out var task) ||
            task.DataverseId is not Guid taskDataverseId) return;
        if (RecurrenceStartDate is not DateTimeOffset start || RecurrenceEndDate is not DateTimeOffset end)
        {
            RecurrenceStatus = "Pick a start and end date.";
            return;
        }
        if (end.Date < start.Date)
        {
            RecurrenceStatus = "The end date must be on or after the start date.";
            return;
        }
        var selectedRows = RecurrenceDays.Where(r => r.IsSelected).ToList();
        if (selectedRows.Count == 0)
        {
            RecurrenceStatus = "Select at least one weekday.";
            return;
        }
        if (selectedRows.Any(r => r.To <= r.From))
        {
            RecurrenceStatus = "Each selected day needs an end time after its start time.";
            return;
        }

        var def = new TaskRecurrenceDefinition
        {
            StartDate = start.Date,
            EndDate = end.Date,
            Days = selectedRows.Select(r => new RecurrenceDay
            {
                Day = r.Day,
                From = r.From.ToString(@"hh\:mm"),
                To = r.To.ToString(@"hh\:mm")
            }).ToList()
        };
        var json = JsonSerializer.Serialize(def);
        var wasExisting = HasExistingRecurrence;

        RecurrenceStatus = "Saving recurrence...";
        await _data.UpdateTaskRecurrenceAsync(taskDataverseId, json);

        var tomorrow = DateTime.Today.AddDays(1);
        DateTime generateFrom;
        if (wasExisting)
        {
            await _data.DeleteRecurrenceBlocksFromAsync(taskDataverseId, tomorrow);
            generateFrom = def.StartDate < tomorrow ? tomorrow : def.StartDate;
        }
        else
        {
            generateFrom = def.StartDate;
        }

        var blocks = BuildRecurrenceBlocks(def, generateFrom, def.EndDate);
        await _data.AddCalendarBlocksAsync(taskDataverseId, task.Title, blocks, "Recurrence");

        task.RecurrenceJson = json;
        IsRecurrenceEditorVisible = false;
        await LoadAsync(forceSync: true);
        await LoadSelectedTaskDetailsAsync();
    }

    public async Task DeleteRecurrenceAsync()
    {
        if (SelectedTask is null || !_tasksById.TryGetValue(SelectedTask.Id, out var task) ||
            task.DataverseId is not Guid taskDataverseId) return;

        await _data.UpdateTaskRecurrenceAsync(taskDataverseId, null);
        await _data.DeleteRecurrenceBlocksFromAsync(taskDataverseId, DateTime.Today.AddDays(1));
        task.RecurrenceJson = null;
        IsRecurrenceEditorVisible = false;
        await LoadAsync(forceSync: true);
        await LoadSelectedTaskDetailsAsync();
    }

    private static TaskRecurrenceDefinition? ParseRecurrence(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<TaskRecurrenceDefinition>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<(DateTime Start, DateTime End)> BuildRecurrenceBlocks(TaskRecurrenceDefinition def, DateTime fromDate, DateTime toDate)
    {
        var byDay = def.Days
            .GroupBy(d => d.Day)
            .ToDictionary(g => g.Key, g => g.First());
        var result = new List<(DateTime, DateTime)>();
        for (var date = fromDate.Date; date <= toDate.Date; date = date.AddDays(1))
        {
            if (!byDay.TryGetValue(date.DayOfWeek, out var dayDef)) continue;
            if (!TimeSpan.TryParse(dayDef.From, out var from) ||
                !TimeSpan.TryParse(dayDef.To, out var to) || to <= from) continue;
            result.Add((date.Add(from), date.Add(to)));
        }
        return result;
    }

    public Task<TaskItem?> GetFullTaskAsync(int taskId)
    {
        if (!_tasksById.TryGetValue(taskId, out var task))
            return Task.FromResult<TaskItem?>(null);

        return Task.FromResult<TaskItem?>(new TaskItem
        {
            Id = task.Id,
            DataverseId = task.DataverseId,
            ProjectId = task.ProjectId,
            Project = task.Project,
            Title = task.Title,
            Description = task.Description,
            WorkType = task.WorkType,
            Priority = task.Priority,
            EstimatedPomodoros = task.EstimatedPomodoros,
            DueDate = task.DueDate,
            ScheduledStart = task.ScheduledStart,
            ScheduledEnd = task.ScheduledEnd,
            Status = task.Status,
            ScheduleBlocks = task.ScheduleBlocks.ToList()
        });
    }

    private static string FormatDurationSeconds(int seconds)
    {
        if (seconds <= 0) return "0s";
        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes:00}m {duration.Seconds:00}s"
            : duration.TotalMinutes >= 1
                ? $"{duration.Minutes}m {duration.Seconds:00}s"
                : $"{duration.Seconds}s";
    }

    private void NotifySelectedTaskActionsChanged()
    {
        OnPropertyChanged(nameof(CanEditSelectedTask));
        OnPropertyChanged(nameof(CanDeleteSelectedTask));
        OnPropertyChanged(nameof(CanChangeSelectedTaskStatus));
        OnPropertyChanged(nameof(SelectedTaskLockMessage));
    }
}

public sealed record TaskTimeByDayView(
    Guid DataverseId,
    string DateText,
    string TimeRangeText,
    string DurationText,
    DateTime StartedAtSpain,
    DateTime EndedAtSpain,
    int ActualMinutes,
    bool IsTimesheetTransferred,
    bool IsEditable);

public sealed record TaskCalendarBlockView(string DateText, string TimeRange, string DurationText);

public sealed record TaskJournalEntryView(Guid? DataverseId, string CreatedAtText, string Content, bool IsTimesheetTransferred, bool IsEditable);

public sealed record ChecklistItemView(int Index, string Title, bool IsCompleted);

public sealed class TaskRecurrenceDefinition
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public List<RecurrenceDay> Days { get; set; } = new();
}

public sealed class RecurrenceDay
{
    public DayOfWeek Day { get; set; }
    public string From { get; set; } = "10:00";
    public string To { get; set; } = "12:00";
}

public partial class RecurrenceDayRow : ObservableObject
{
    public DayOfWeek Day { get; }
    public string DayName { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private TimeSpan _from = TimeSpan.FromHours(10);
    [ObservableProperty] private TimeSpan _to = TimeSpan.FromHours(12);

    public RecurrenceDayRow(DayOfWeek day, string dayName)
    {
        Day = day;
        DayName = dayName;
    }
}

public class TaskRowView
{
    public int Id { get; }
    public Guid? DataverseId { get; }
    public string Title { get; }
    public string ProjectName { get; }
    public string ProjectColorHex { get; }
    public string WorkType { get; }
    public string Priority { get; }
    public string WorkTypeLabel { get; }
    public string PriorityLabel { get; }
    public string DueLabel { get; }
    public double EstimatedPomodoros { get; }
    public string EstimatedPomodorosLabel { get; }
    public string Due { get; }
    public string Status { get; }
    public bool IsDone { get; }
    public string Schedule { get; }
    public string ScheduleLabel { get; }
    public TaskItemStatus StatusEnum { get; }
    public string CustomerName { get; }
    public bool HasCustomerName { get; }
    public bool HasDueDate { get; }
    public string PriorityIcon { get; }
    public SolidColorBrush PriorityBrush { get; }
    public string Summary { get; }

    public TaskRowView(TaskItem task)
    {
        Id = task.Id;
        DataverseId = task.DataverseId;
        Title = task.Title;
        ProjectName = task.Project?.Name ?? "?";
        CustomerName = task.Project?.Customer?.Name ?? "";
        HasCustomerName = !string.IsNullOrWhiteSpace(CustomerName);
        ProjectColorHex = ProjectColorService.CardColor(task.Project?.ColorHex);
        WorkType = task.WorkType.ToString();
        Priority = task.Priority.ToString();
        WorkTypeLabel = $"Type {WorkType}";
        PriorityLabel = $"Priority {Priority}";
        var scheduledMinutes = task.ScheduleBlocks.Sum(b => Math.Max(0, (int)Math.Round((b.End - b.Start).TotalMinutes)));
        EstimatedPomodoros = CalculateEstimatedPomodoros(scheduledMinutes);
        EstimatedPomodorosLabel = EstimatedPomodoros <= 0 ? "" : EstimatedPomodoros.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        Due = task.DueDate is DateTime due ? DisplayFormat.Date(due) : "";
        HasDueDate = !string.IsNullOrWhiteSpace(Due);
        DueLabel = string.IsNullOrWhiteSpace(Due) ? "No due date" : $"Due {Due}";
        PriorityIcon = task.Priority switch
        {
            TaskPriority.Critical or TaskPriority.High => "↑",
            TaskPriority.Low => "↓",
            _ => "—"
        };
        PriorityBrush = new SolidColorBrush(task.Priority switch
        {
            TaskPriority.Critical or TaskPriority.High => Windows.UI.Color.FromArgb(255, 248, 113, 113),
            TaskPriority.Low => Windows.UI.Color.FromArgb(255, 96, 165, 250),
            _ => Windows.UI.Color.FromArgb(255, 156, 163, 175)
        });
        StatusEnum = task.Status;
        Status = task.Status.ToString();
        IsDone = task.Status == TaskItemStatus.Done;
        var nextBlock = task.ScheduleBlocks.OrderBy(b => b.Start).FirstOrDefault(b => b.End >= DateTime.Today);
        Schedule = nextBlock is null ? "Unscheduled" : $"{nextBlock.Start:ddd HH:mm}-{nextBlock.End:HH:mm}";
        ScheduleLabel = $"Schedule {Schedule}";
        Summary = string.Join(" · ", new[] { ProjectName, Priority, Schedule }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static double CalculateEstimatedPomodoros(int scheduledMinutes)
    {
        if (scheduledMinutes <= 0) return 0;
        return Math.Round((scheduledMinutes / 60d) * 2, MidpointRounding.AwayFromZero) / 2d;
    }
}

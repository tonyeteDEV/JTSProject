using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JTS.Core;
using JTS.Data.Entities;
using JTS_App.Services;

namespace JTS_App.ViewModels;

public sealed partial class WeeklyPlannerViewModel : ObservableObject
{
    private const int PlanningSnapMinutes = 15;

    private readonly DataverseAppDataService _data;
    private readonly PomodoroService _pomodoro;
    private readonly Dictionary<int, PlannerTaskView> _taskById = new();
    private readonly Dictionary<int, TaskItem> _taskModelsById = new();
    private readonly HashSet<int> _addedBlockIds = new();
    private readonly HashSet<int> _updatedBlockIds = new();
    private readonly Dictionary<Guid, int> _deletedBlockIds = new();
    private readonly HashSet<int> _affectedTaskIds = new();
    private int _nextDraftBlockId = -1;

    public ObservableCollection<PlannerTaskView> PendingTasks { get; } = new();
    public ObservableCollection<PlannerTaskView> AssignedTasks { get; } = new();
    public ObservableCollection<PlannerBlockView> PlannedBlocks { get; } = new();
    public ObservableCollection<PlannerBlockView> SelectedTaskBlocks { get; } = new();

    [ObservableProperty] private DateTime _weekStart = StartOfWeek(DateTime.Today);
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private PlannerTaskView? _selectedTask;
    [ObservableProperty] private PlannerBlockView? _selectedBlock;
    [ObservableProperty] private bool _hasPendingChanges;

    partial void OnSelectedTaskChanged(PlannerTaskView? value)
    {
        if (value is null || SelectedBlock?.TaskId != value.Id)
            SelectedBlock = null;
        RefreshSelectedTaskBlocks();
    }

    partial void OnSelectedBlockChanged(PlannerBlockView? value)
    {
        if (value is not null && _taskById.TryGetValue(value.TaskId, out var task) && SelectedTask?.Id != task.Id)
            SelectedTask = task;
        RefreshSelectedTaskBlocks();
    }

    public WeeklyPlannerViewModel(DataverseAppDataService data, PomodoroService pomodoro)
    {
        _data = data;
        _pomodoro = pomodoro;
    }

    public async Task LoadAsync(bool forceSync = false)
    {
        var snapshot = await _data.LoadTaskSnapshotAsync(forceSync);
        var selectedTaskId = SelectedTask?.Id;
        var selectedBlockId = SelectedBlock?.BlockId;
        var weekEnd = WeekStart.AddDays(7);
        var tasks = snapshot.Tasks
            .Where(IsVisibleInTaskQueue)
            .OrderBy(t => t.DueDate)
            .ThenByDescending(t => t.Priority)
            .ToList();

        var blocks = snapshot.Tasks
            .SelectMany(t => t.ScheduleBlocks.Select(b =>
            {
                b.TaskItem = t;
                return b;
            }))
            .Where(b => b.Start >= WeekStart && b.Start < weekEnd && b.TaskItem!.Status != TaskItemStatus.Cancelled)
            .OrderBy(b => b.Start)
            .ToList();

        var tasksWithDataverseId = snapshot.Tasks.Where(t => t.DataverseId is not null).ToList();
        var entriesByTask = await _data.LoadTimeEntriesByTaskAsync(tasksWithDataverseId.Select(t => t.DataverseId!.Value), forceSync);
        var actualFocusSecondsByTask = tasksWithDataverseId.ToDictionary(
            task => task.Id,
            task => entriesByTask.TryGetValue(task.DataverseId!.Value, out var entries)
                ? entries.Sum(s => s.ActualMinutes) * 60
                : 0);

        _taskById.Clear();
        _taskModelsById.Clear();
        PendingTasks.Clear();
        AssignedTasks.Clear();
        PlannedBlocks.Clear();
        SelectedTaskBlocks.Clear();
        ClearPendingChangeTrackers();
        HasPendingChanges = false;

        var plannedIds = blocks.Select(b => b.TaskItemId).ToHashSet();
        foreach (var task in tasks)
        {
            var view = new PlannerTaskView(task);
            view.SetSavedFocusSeconds(actualFocusSecondsByTask.GetValueOrDefault(task.Id));
            _taskById[task.Id] = view;
            _taskModelsById[task.Id] = task;
            if (plannedIds.Contains(task.Id))
            {
                AssignedTasks.Add(view);
            }
            else
            {
                PendingTasks.Add(view);
            }
        }

        foreach (var task in blocks.Select(b => b.TaskItem).Where(t => t is not null).GroupBy(t => t!.Id).Select(g => g.First()!))
        {
            if (_taskById.ContainsKey(task.Id)) continue;
            var view = new PlannerTaskView(task);
            view.SetSavedFocusSeconds(actualFocusSecondsByTask.GetValueOrDefault(task.Id));
            _taskById[task.Id] = view;
            _taskModelsById[task.Id] = task;
        }

        foreach (var block in blocks)
        {
            PlannedBlocks.Add(new PlannerBlockView(block));
        }

        RefreshTaskScheduleMetrics();
        RefreshActivePomodoroTime();

        SelectedTask = selectedTaskId is int taskId && _taskById.TryGetValue(taskId, out var selectedTask)
            ? selectedTask
            : null;
        SelectedBlock = selectedBlockId is int blockId
            ? PlannedBlocks.FirstOrDefault(block => block.BlockId == blockId)
            : null;
        RefreshSelectedTaskBlocks();

        Status = "Ready";
    }

    public Task<PlannerBlockView?> AddDraftBlockAsync(int taskId, DateTime start, DateTime end)
    {
        App.Log($"[WeeklyPlannerViewModel] AddDraftBlock requested taskId={taskId} start={start:O} end={end:O}");
        if (!_taskById.TryGetValue(taskId, out var task)) return Task.FromResult<PlannerBlockView?>(null);
        if (!_taskModelsById.TryGetValue(taskId, out var taskModel) || taskModel.DataverseId is not Guid taskDataverseId)
        {
            Status = "Task is not linked to Dataverse";
            return Task.FromResult<PlannerBlockView?>(null);
        }
        if (!task.IsSchedulable)
        {
            Status = "Cancelled tasks are visible as history and cannot be scheduled again";
            return Task.FromResult<PlannerBlockView?>(null);
        }

        start = RoundToPlanningIncrement(start);
        end = NormalizeEnd(start, RoundToPlanningIncrement(end));
        App.Log($"[WeeklyPlannerViewModel] AddDraftBlock normalized taskId={taskId} start={start:O} end={end:O}");

        _ = taskDataverseId;
        var block = new PlannerBlockView(_nextDraftBlockId--, null, task, start, end, true);
        PlannedBlocks.Add(block);
        _addedBlockIds.Add(block.BlockId);
        MarkTaskPending(taskId);
        RefreshTaskModelFromLocalBlocks(taskId);
        RefreshTaskScheduleMetrics();
        MoveTaskToPlanned(task);
        SelectedTask = task;
        SelectedBlock = block;
        HasPendingChanges = true;
        Status = "Local change pending. Click Confirm changes to save to Dataverse.";
        App.Log($"[WeeklyPlannerViewModel] AddDraftBlock added blockId={block.BlockId} plannedCount={PlannedBlocks.Count}");
        return Task.FromResult<PlannerBlockView?>(block);
    }

    public Task UpdateDraftBlockAsync(int blockId, DateTime start, DateTime end)
    {
        App.Log($"[WeeklyPlannerViewModel] UpdateDraftBlock requested blockId={blockId} start={start:O} end={end:O}");
        var index = IndexOfBlock(blockId);
        if (index < 0)
        {
            App.Log($"[WeeklyPlannerViewModel] UpdateDraftBlock skipped missing blockId={blockId}");
            return Task.CompletedTask;
        }

        var current = PlannedBlocks[index];
        if (!current.IsEditable)
        {
            Status = "Completed calendar blocks are read-only";
            return Task.CompletedTask;
        }

        App.Log($"[WeeklyPlannerViewModel] UpdateDraftBlock current blockId={blockId} taskId={current.TaskId} start={current.Start:O} end={current.End:O}");
        start = RoundToPlanningIncrement(start);
        end = NormalizeEnd(start, RoundToPlanningIncrement(end));

        var updated = current with { Start = start, End = end, HasLocalChanges = true };
        PlannedBlocks[index] = updated;
        if (!_addedBlockIds.Contains(blockId))
            _updatedBlockIds.Add(blockId);
        MarkTaskPending(current.TaskId);
        RefreshTaskModelFromLocalBlocks(current.TaskId);
        if (SelectedBlock?.BlockId == blockId)
            SelectedBlock = updated;
        RefreshTaskScheduleMetrics();
        RefreshSelectedTaskBlocks();
        HasPendingChanges = true;
        Status = "Local change pending. Click Confirm changes to save to Dataverse.";
        App.Log($"[WeeklyPlannerViewModel] UpdateDraftBlock updated blockId={blockId} taskId={updated.TaskId} start={updated.Start:O} end={updated.End:O}");
        return Task.CompletedTask;
    }

    public Task DeleteDraftBlockAsync(int blockId)
    {
        var index = IndexOfBlock(blockId);
        if (index < 0) return Task.CompletedTask;
        if (!PlannedBlocks[index].IsEditable)
        {
            Status = "Completed calendar blocks are read-only";
            return Task.CompletedTask;
        }

        var taskId = PlannedBlocks[index].TaskId;
        var dataverseId = PlannedBlocks[index].DataverseId;
        if (dataverseId is Guid blockDataverseId)
            _deletedBlockIds[blockDataverseId] = taskId;
        _addedBlockIds.Remove(blockId);
        _updatedBlockIds.Remove(blockId);

        PlannedBlocks.RemoveAt(index);
        MarkTaskPending(taskId);
        RefreshTaskModelFromLocalBlocks(taskId);
        if (SelectedBlock?.BlockId == blockId)
            SelectedBlock = null;
        RefreshTaskScheduleMetrics();
        RefreshTaskQueuePlacement(taskId);
        RefreshSelectedTaskBlocks();
        HasPendingChanges = true;
        Status = "Local change pending. Click Confirm changes to save to Dataverse.";
        return Task.CompletedTask;
    }

    public async Task ConfirmChangesAsync()
    {
        if (!HasPendingChanges)
        {
            Status = "No pending changes.";
            return;
        }

        Status = "Saving changes to Dataverse...";

        foreach (var blockDataverseId in _deletedBlockIds.Keys.ToList())
        {
            await _data.DeleteCalendarBlockAsync(blockDataverseId);
        }

        foreach (var blockId in _updatedBlockIds.ToList())
        {
            var block = PlannedBlocks.FirstOrDefault(b => b.BlockId == blockId);
            if (block?.DataverseId is Guid blockDataverseId)
                await _data.UpdateCalendarBlockAsync(blockDataverseId, block.Start, block.End);
        }

        foreach (var blockId in _addedBlockIds.ToList())
        {
            var index = IndexOfBlock(blockId);
            if (index < 0) continue;
            var block = PlannedBlocks[index];
            if (!_taskModelsById.TryGetValue(block.TaskId, out var taskModel) || taskModel.DataverseId is not Guid taskDataverseId)
                continue;

            var dataverseId = await _data.AddCalendarBlockAsync(taskDataverseId, block.Title, block.Start, block.End);
            var saved = block with
            {
                BlockId = BlockIdFromDataverse(dataverseId),
                DataverseId = dataverseId,
                HasLocalChanges = false
            };
            PlannedBlocks[index] = saved;
            if (SelectedBlock?.BlockId == blockId)
                SelectedBlock = saved;
        }

        foreach (var taskId in _affectedTaskIds.ToList())
        {
            await SaveTaskPlanningSummaryAsync(taskId);
        }

        ClearPendingChangeTrackers();
        HasPendingChanges = false;
        Status = "Planning saved";
        await LoadAsync(forceSync: true);
    }

    public void PreviousWeek() => WeekStart = WeekStart.AddDays(-7);
    public void NextWeek() => WeekStart = WeekStart.AddDays(7);
    public void CurrentWeek() => WeekStart = StartOfWeek(DateTime.Today);

    public bool IsTaskSelected(int taskId) => SelectedTask?.Id == taskId;

    public bool IsBlockSelected(int blockId) => SelectedBlock?.BlockId == blockId;

    public bool TrySelectTask(int taskId)
    {
        if (!_taskById.TryGetValue(taskId, out var task)) return false;
        SelectedTask = task;
        return true;
    }

    public bool TrySelectBlock(int blockId)
    {
        var block = PlannedBlocks.FirstOrDefault(b => b.BlockId == blockId);
        if (block is null) return false;
        SelectedBlock = block;
        return true;
    }

    private void MoveTaskToPlanned(PlannerTaskView task)
    {
        if (!AssignedTasks.Contains(task)) AssignedTasks.Add(task);
        if (PendingTasks.Contains(task)) PendingTasks.Remove(task);
    }

    private void RefreshTaskQueuePlacement(int taskId)
    {
        if (!_taskById.TryGetValue(taskId, out var task)) return;
        var hasBlocks = PlannedBlocks.Any(block => block.TaskId == taskId);
        if (hasBlocks)
        {
            MoveTaskToPlanned(task);
            return;
        }

        if (!PendingTasks.Contains(task)) PendingTasks.Add(task);
        if (AssignedTasks.Contains(task)) AssignedTasks.Remove(task);
    }

    private int IndexOfBlock(int blockId)
    {
        for (var i = 0; i < PlannedBlocks.Count; i++)
        {
            if (PlannedBlocks[i].BlockId == blockId) return i;
        }

        return -1;
    }

    private void RefreshSelectedTaskBlocks()
    {
        SelectedTaskBlocks.Clear();
        if (SelectedBlock is not null)
        {
            SelectedTaskBlocks.Add(SelectedBlock);
            return;
        }

        if (SelectedTask is null) return;
        foreach (var block in PlannedBlocks.Where(b => b.TaskId == SelectedTask.Id).OrderBy(b => b.Start))
        {
            SelectedTaskBlocks.Add(block);
        }
    }

    private void RefreshTaskScheduleMetrics()
    {
        foreach (var task in _taskById.Values)
        {
            task.ScheduledMinutes = PlannedBlocks
                .Where(b => b.TaskId == task.Id)
                .Sum(b => Math.Max(0, (int)Math.Round((b.End - b.Start).TotalMinutes)));
        }
    }

    private void MarkTaskPending(int taskId)
    {
        _affectedTaskIds.Add(taskId);
    }

    private void RefreshTaskModelFromLocalBlocks(int taskId)
    {
        if (!_taskModelsById.TryGetValue(taskId, out var taskModel)) return;
        var blocks = PlannedBlocks
            .Where(block => block.TaskId == taskId)
            .OrderBy(block => block.Start)
            .ToList();

        if (blocks.Count == 0)
        {
            taskModel.ScheduledStart = null;
            taskModel.ScheduledEnd = null;
            taskModel.EstimatedPomodoros = 0;
            return;
        }

        taskModel.ScheduledStart = blocks.First().Start;
        taskModel.ScheduledEnd = blocks.Last().End;
        var scheduledMinutes = blocks.Sum(block => Math.Max(0, (int)Math.Round((block.End - block.Start).TotalMinutes)));
        taskModel.EstimatedPomodoros = (int)Math.Ceiling(PlannerTaskView.CalculateEstimatedPomodoros(scheduledMinutes));
    }

    private async Task SaveTaskPlanningSummaryAsync(int taskId)
    {
        if (!_taskModelsById.TryGetValue(taskId, out var taskModel)) return;

        RefreshTaskModelFromLocalBlocks(taskId);
        await _data.UpdateTaskPlanningFieldsAsync(taskModel);
    }

    private void ClearPendingChangeTrackers()
    {
        _addedBlockIds.Clear();
        _updatedBlockIds.Clear();
        _deletedBlockIds.Clear();
        _affectedTaskIds.Clear();
    }

    public void RefreshActivePomodoroTime()
    {
        foreach (var task in _taskById.Values)
        {
            var activeSeconds = _pomodoro.ActiveTaskId == task.Id && _pomodoro.SessionType == PomodoroSessionType.Work
                ? Math.Max(0, (int)Math.Round(_pomodoro.ActiveElapsed.TotalSeconds))
                : 0;

            task.ActualFocusSeconds = task.SavedFocusSeconds + activeSeconds;
        }
    }

    private static DateTime NormalizeEnd(DateTime start, DateTime end) =>
        end <= start ? start.AddMinutes(PlanningSnapMinutes) : end;

    private static bool IsVisibleInTaskQueue(TaskItem task) =>
        task.Status is not TaskItemStatus.Done and not TaskItemStatus.Cancelled;

    public static DateTime StartOfWeek(DateTime date)
    {
        var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.Date.AddDays(-diff);
    }

    public static DateTime RoundToThirtyMinutes(DateTime value)
    {
        var minutes = value.Hour * 60 + value.Minute;
        var rounded = (int)(Math.Round(minutes / 30d, MidpointRounding.AwayFromZero) * 30);
        return value.Date.AddMinutes(Math.Clamp(rounded, 0, 24 * 60 - 30));
    }

    public static DateTime RoundToPlanningIncrement(DateTime value)
    {
        var minutes = value.Hour * 60 + value.Minute;
        var rounded = (int)(Math.Round(minutes / (double)PlanningSnapMinutes, MidpointRounding.AwayFromZero) * PlanningSnapMinutes);
        return value.Date.AddMinutes(Math.Clamp(rounded, 0, 24 * 60 - PlanningSnapMinutes));
    }

    public static int BlockIdFromDataverse(Guid id)
    {
        var value = BitConverter.ToInt32(id.ToByteArray(), 0);
        return value == int.MinValue ? int.MaxValue : Math.Abs(value);
    }
}

public sealed partial class PlannerTaskView : ObservableObject
{
    public int Id { get; }
    public Guid? DataverseId { get; }
    public string Title { get; }
    public string ProjectName { get; }
    public string ProjectColorHex { get; }
    public string Priority { get; }
    public TaskItemStatus Status { get; }
    public bool IsCancelled => Status == TaskItemStatus.Cancelled;
    public bool IsSchedulable => !IsCancelled;
    public int SavedFocusSeconds { get; private set; }
    public double EstimatedPomodoros => CalculateEstimatedPomodoros(ScheduledMinutes);
    public string EstimateLabel => $"Estimated {FormatPomodoros(EstimatedPomodoros)} ({FormatMinutes(ScheduledMinutes)})";
    public string ActualLabel => $"Actual {FormatSeconds(ActualFocusSeconds)}";
    public string AssignmentSummary => $"{EstimateLabel} · {ActualLabel}";

    [ObservableProperty] private int _scheduledMinutes;
    [ObservableProperty] private int _actualFocusSeconds;

    public PlannerTaskView(TaskItem task)
    {
        Id = task.Id;
        DataverseId = task.DataverseId;
        Title = task.Title;
        ProjectName = task.Project?.Name ?? "?";
        ProjectColorHex = ProjectColorService.CardColor(task.Project?.ColorHex);
        Priority = task.Priority.ToString();
        Status = task.Status;
        ScheduledMinutes = Math.Max(0, task.EstimatedPomodoros) * 60;
    }

    public void SetSavedFocusSeconds(int seconds)
    {
        SavedFocusSeconds = Math.Max(0, seconds);
        ActualFocusSeconds = SavedFocusSeconds;
    }

    public static double CalculateEstimatedPomodoros(int scheduledMinutes)
    {
        if (scheduledMinutes <= 0) return 0;
        return Math.Round((scheduledMinutes / 60d) * 2, MidpointRounding.AwayFromZero) / 2d;
    }

    partial void OnScheduledMinutesChanged(int value)
    {
        OnPropertyChanged(nameof(EstimatedPomodoros));
        OnPropertyChanged(nameof(EstimateLabel));
        OnPropertyChanged(nameof(AssignmentSummary));
    }

    partial void OnActualFocusSecondsChanged(int value)
    {
        OnPropertyChanged(nameof(ActualLabel));
        OnPropertyChanged(nameof(AssignmentSummary));
    }

    private static string FormatPomodoros(double value)
    {
        if (value <= 0) return "0 pomodoros";
        var formatted = value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        return value == 1 ? "1 pomodoro" : $"{formatted} pomodoros";
    }

    private static string FormatMinutes(int minutes)
    {
        if (minutes <= 0) return "0h";
        var hours = minutes / 60;
        var remainder = minutes % 60;
        return hours > 0 && remainder > 0
            ? $"{hours}h {remainder:00}m"
            : hours > 0
                ? $"{hours}h"
                : $"{remainder}m";
    }

    private static string FormatSeconds(int seconds)
    {
        if (seconds <= 0) return "0m";
        var duration = TimeSpan.FromSeconds(seconds);
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes:00}m {duration.Seconds:00}s"
            : $"{duration.Minutes}m {duration.Seconds:00}s";
    }
}

public sealed record PlannerBlockView(int BlockId, Guid? DataverseId, int TaskId, string Title, string ProjectName, string ProjectColorHex, TaskItemStatus Status, DateTime Start, DateTime End, bool HasLocalChanges = false)
{
    public bool IsCompleted => Status == TaskItemStatus.Done;
    public bool IsCancelled => Status == TaskItemStatus.Cancelled;
    public bool IsEditable => !IsCancelled;
    public string TimeRange => $"{Start:ddd HH:mm} - {End:HH:mm}";
    public string StartDateText => DisplayFormat.Date(Start);
    public string StartTimeText => Start.ToString("HH:mm");
    public string EndDateText => DisplayFormat.Date(End);
    public string EndTimeText => End.ToString("HH:mm");
    public string SyncLabel => HasLocalChanges ? "Pending" : "Saved";

    public PlannerBlockView(TaskScheduleBlock block)
        : this(block.DataverseId is Guid id ? WeeklyPlannerViewModel.BlockIdFromDataverse(id) : block.Id, block.DataverseId, block.TaskItemId, block.TaskItem?.Title ?? "?", block.TaskItem?.Project?.Name ?? "?", ProjectColorService.CardColor(block.TaskItem?.Project?.ColorHex), block.TaskItem?.Status ?? TaskItemStatus.Todo, block.Start, block.End)
    {
    }

    public PlannerBlockView(int blockId, Guid? dataverseId, PlannerTaskView task, DateTime start, DateTime end, bool hasLocalChanges = false)
        : this(blockId, dataverseId, task.Id, task.Title, task.ProjectName, task.ProjectColorHex, task.Status, start, end, hasLocalChanges)
    {
    }
}

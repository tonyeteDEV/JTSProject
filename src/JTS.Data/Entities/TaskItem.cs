using JTS.Core;

namespace JTS.Data.Entities;

public class TaskItem
{
    public int Id { get; set; }
    public Guid? DataverseId { get; set; }
    public int ProjectId { get; set; }
    public Project? Project { get; set; }

    public int? ParentTaskId { get; set; }
    public TaskItem? ParentTask { get; set; }
    public List<TaskItem> Subtasks { get; set; } = new();

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    public WorkType WorkType { get; set; } = WorkType.DeepWork;
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public int EstimatedPomodoros { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? ScheduledStart { get; set; }
    public DateTime? ScheduledEnd { get; set; }
    public TaskItemStatus Status { get; set; } = TaskItemStatus.Todo;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public string? RecurrenceJson { get; set; }

    public List<TaskJournalEntry> JournalEntries { get; set; } = new();
    public List<TaskChecklistItem> ChecklistItems { get; set; } = new();
    public List<PomodoroSession> PomodoroSessions { get; set; } = new();
    public List<TaskScheduleBlock> ScheduleBlocks { get; set; } = new();
}

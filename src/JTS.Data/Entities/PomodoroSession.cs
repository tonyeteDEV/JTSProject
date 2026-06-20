using System.ComponentModel.DataAnnotations.Schema;
using JTS.Core;

namespace JTS.Data.Entities;

public class PomodoroSession
{
    public int Id { get; set; }
    public Guid? DataverseId { get; set; }
    public int? TaskItemId { get; set; }
    public TaskItem? TaskItem { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int PlannedMinutes { get; set; }
    public int ActualMinutes { get; set; }
    public PomodoroSessionType SessionType { get; set; }
    public bool Completed { get; set; }
    public int InterruptionCount { get; set; }
    [NotMapped] public Guid? TimesheetLineDataverseId { get; set; }
}

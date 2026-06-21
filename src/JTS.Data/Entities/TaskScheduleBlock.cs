namespace JTS.Data.Entities;

public class TaskScheduleBlock
{
    public int Id { get; set; }
    public Guid? DataverseId { get; set; }
    public int TaskItemId { get; set; }
    public TaskItem? TaskItem { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string? Source { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

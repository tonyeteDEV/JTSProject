namespace JTS.Data.Entities;

public class TaskChecklistItem
{
    public int Id { get; set; }
    public Guid? DataverseId { get; set; }
    public int TaskItemId { get; set; }
    public TaskItem? TaskItem { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

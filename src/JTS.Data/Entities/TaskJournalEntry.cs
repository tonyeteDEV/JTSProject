using System.ComponentModel.DataAnnotations.Schema;

namespace JTS.Data.Entities;

public class TaskJournalEntry
{
    public int Id { get; set; }
    public Guid? DataverseId { get; set; }
    public int TaskItemId { get; set; }
    public TaskItem? TaskItem { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [NotMapped] public Guid? TimesheetLineDataverseId { get; set; }
}

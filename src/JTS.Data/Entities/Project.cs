namespace JTS.Data.Entities;

public class Project
{
    public int Id { get; set; }
    public Guid? DataverseId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ColorHex { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? ParentProjectId { get; set; }
    public Project? ParentProject { get; set; }
    public List<Project> Children { get; set; } = new();

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public List<TaskItem> Tasks { get; set; } = new();
    public List<ProjectRelation> RelationsFrom { get; set; } = new();
    public List<ProjectRelation> RelationsTo { get; set; } = new();
}

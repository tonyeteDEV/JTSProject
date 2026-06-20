using JTS.Core;

namespace JTS.Data.Entities;

public class ProjectRelation
{
    public int Id { get; set; }
    public int FromProjectId { get; set; }
    public Project? FromProject { get; set; }
    public int ToProjectId { get; set; }
    public Project? ToProject { get; set; }
    public ProjectRelationType RelationType { get; set; }
    public string? Note { get; set; }
}

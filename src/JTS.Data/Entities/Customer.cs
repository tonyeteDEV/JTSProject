namespace JTS.Data.Entities;

public class Customer
{
    public int Id { get; set; }
    public Guid? DataverseId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ContactInfo { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Project> Projects { get; set; } = new();
}

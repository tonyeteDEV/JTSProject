namespace JTS.Data.Entities;

public class AiConversation
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<AiMessage> Messages { get; set; } = new();
}

public class AiMessage
{
    public int Id { get; set; }
    public int AiConversationId { get; set; }
    public AiConversation? AiConversation { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

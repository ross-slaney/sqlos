namespace SqlOS.Todo.Api.Models;

public sealed class TodoItem
{
    public Guid Id { get; set; }
    public string SqlOSUserId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

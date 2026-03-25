using SqlOS.Fga.Interfaces;

namespace SqlOS.Todo.Api.Models;

public sealed class TodoItem : IHasResourceId
{
    public Guid Id { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public string SqlOSUserId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

using SqlOS.Fga;
using SqlOS.Todo.Api.Services;

namespace SqlOS.Todo.Api.Models;

public sealed class TodoItem : SqlOSResourceEntity
{
    public Guid Id { get; set; }
    public string OwnerSubjectId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public override string ResourceTypeId => TodoFgaService.TodoResourceTypeId;
    public override string ResourceName => Title;
    public override string? ParentResourceId => string.IsNullOrWhiteSpace(OwnerSubjectId)
        ? null
        : TodoFgaService.GetTenantResourceId(OwnerSubjectId);
}

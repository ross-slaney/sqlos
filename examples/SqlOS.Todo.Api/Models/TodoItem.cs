using SqlOS.Fga.Interfaces;
using SqlOS.Todo.Api.Services;

namespace SqlOS.Todo.Api.Models;

public sealed class TodoItem : ISqlOSResourceEntity
{
    public Guid Id { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public string OwnerSubjectId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public string ResourceTypeId => TodoFgaService.TodoResourceTypeId;
    public string ResourceName => Title;
    public string? ParentResourceId => string.IsNullOrWhiteSpace(OwnerSubjectId)
        ? null
        : TodoFgaService.GetTenantResourceId(OwnerSubjectId);
    public string? ResourceDescription => null;
    public bool ResourceIsActive => true;
}

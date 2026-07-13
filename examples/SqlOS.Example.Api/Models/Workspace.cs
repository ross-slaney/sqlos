using SqlOS.Fga.Interfaces;

namespace SqlOS.Example.Api.Models;

public sealed class Workspace : ISqlOSResourceEntity
{
    public string Id { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public string ResourceTypeId => "workspace";
    public string ResourceName => Name;
    public string ParentResourceId => $"org::{OrganizationId}";
    public string? ResourceDescription => null;
    public bool ResourceIsActive => true;
}

using SqlOS.Fga;

namespace SqlOS.Example.Api.Models;

public sealed class Workspace : SqlOSResourceEntity
{
    public string Id { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public override string ResourceTypeId => "workspace";
    public override string ResourceName => Name;
    public override string? ParentResourceId => $"org::{OrganizationId}";
}

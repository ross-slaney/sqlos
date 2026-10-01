using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.Fga.Models;

/// <summary>
/// A permission that can be granted. Permissions are capabilities that gate
/// whether a principal can access a feature/endpoint.
/// </summary>
/// <remarks>
/// A permission applies to the resources of its resource type, or to every resource when it has
/// none, so it is its own aggregate and references its type by ID.
/// </remarks>
public sealed class SqlOSFgaPermission : ISqlOSAggregate
{
    internal const int MaxKeyLength = 450;

    private readonly DomainEventBuffer _events = new();

    private SqlOSFgaPermission()
    {
    }

    public string Id { get; private set; } = string.Empty;
    public string? ResourceTypeId { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }

    // Navigation
    public SqlOSFgaResourceType? ResourceType { get; private set; }
    public ICollection<SqlOSFgaRolePermission> RolePermissions { get; private set; } = new List<SqlOSFgaRolePermission>();

    DomainEventBuffer ISqlOSAggregate.Events => _events;

    /// <summary>Defines a permission for the resources of a type, or for every resource when the type is null.</summary>
    internal static SqlOSFgaPermission Define(string id, string key, string name, string? description, string? resourceTypeId)
    {
        var permission = new SqlOSFgaPermission { Id = id };
        permission.Apply(key, name, description, resourceTypeId);
        return permission;
    }

    internal void Redefine(string key, string name, string? description, string? resourceTypeId)
    {
        if (Key == key && Name == name && Description == description && ResourceTypeId == resourceTypeId)
        {
            return;
        }

        Apply(key, name, description, resourceTypeId);
    }

    private void Apply(string key, string name, string? description, string? resourceTypeId)
    {
        if (key.Length > MaxKeyLength)
        {
            throw new InvalidOperationException($"FGA permission keys cannot exceed {MaxKeyLength} characters.");
        }

        (Key, Name, Description, ResourceTypeId) = (key, name, description, resourceTypeId);
        _events.Raise(new FgaPermissionDefined(Id, Key, ResourceTypeId));
    }
}

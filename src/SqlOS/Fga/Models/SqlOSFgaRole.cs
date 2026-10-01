using SqlOS.Domain;
using SqlOS.Domain.Events;

namespace SqlOS.Fga.Models;

/// <summary>
/// A role that groups permissions.
/// </summary>
/// <remarks>
/// The role owns its permission assignments. A role may hold permissions of any resource type: a
/// grant at an ancestor reaches descendants of each permission's type, so a role is not tied to
/// the type of the resource it is granted on.
/// </remarks>
public sealed class SqlOSFgaRole : ISqlOSAggregate
{
    private readonly DomainEventBuffer _events = new();
    private readonly List<SqlOSFgaRolePermission> _rolePermissions = [];

    private SqlOSFgaRole()
    {
    }

    public string Id { get; private set; } = string.Empty;
    public string Key { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public bool IsVirtual { get; private set; }

    // Navigation
    public ICollection<SqlOSFgaRolePermission> RolePermissions => _rolePermissions.AsReadOnly();
    public ICollection<SqlOSFgaGrant> Grants { get; private set; } = new List<SqlOSFgaGrant>();

    DomainEventBuffer ISqlOSAggregate.Events => _events;

    internal static SqlOSFgaRole Define(string id, string key, string name, string? description, bool isVirtual, FgaActor actor)
    {
        var role = new SqlOSFgaRole { Id = id, Key = key, Name = name, Description = description, IsVirtual = isVirtual };
        role._events.Raise(new FgaRoleDefined(id, key, isVirtual, actor));
        return role;
    }

    internal void Redefine(string key, string name, string? description, bool isVirtual, FgaActor actor)
    {
        if (Key == key && Name == name && Description == description && IsVirtual == isVirtual)
        {
            return;
        }

        (Key, Name, Description, IsVirtual) = (key, name, description, isVirtual);
        _events.Raise(new FgaRoleRedefined(Id, key, isVirtual, actor));
    }

    /// <summary>
    /// Lets the role grant <paramref name="permission"/>, once. The assignments must be loaded:
    /// the role decides from them whether it already has it.
    /// </summary>
    internal void Allow(SqlOSFgaPermission permission, FgaActor actor)
    {
        ArgumentNullException.ThrowIfNull(permission);
        if (_rolePermissions.Any(assignment => assignment.PermissionId == permission.Id))
        {
            return;
        }

        _rolePermissions.Add(new SqlOSFgaRolePermission(Id, permission.Id));
        _events.Raise(new FgaRolePermissionAdded(Id, permission.Id, actor));
    }
}

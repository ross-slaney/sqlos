using SqlOS.Fga.Models;
using SqlOS.Fga.Services;

namespace SqlOS.Fga.Configuration;

/// <summary>
/// Builds the resource types, permissions, roles, and role-permission assignments that SqlOS
/// reconciles into the FGA store during host startup.
/// </summary>
public sealed class SqlOSFgaSeedBuilder
{
    private readonly Dictionary<string, SqlOSFgaResourceType> _resourceTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SqlOSFgaPermission> _permissionsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SqlOSFgaPermission> _permissionsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SqlOSFgaRole> _rolesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SqlOSFgaRole> _rolesByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _rolePermissions = new(StringComparer.Ordinal);

    /// <summary>Creates an empty FGA startup seed builder.</summary>
    public SqlOSFgaSeedBuilder()
    {
    }

    internal SqlOSFgaSeedBuilder(SqlOSFgaSeedData existing)
    {
        if (existing.ResourceTypes != null)
        {
            foreach (var resourceType in existing.ResourceTypes)
            {
                _resourceTypes[resourceType.Id] = Clone(resourceType);
            }
        }

        if (existing.Permissions != null)
        {
            foreach (var permission in existing.Permissions)
            {
                var clone = Clone(permission);
                _permissionsById[clone.Id] = clone;
                _permissionsByKey[clone.Key] = clone;
            }
        }

        if (existing.Roles != null)
        {
            foreach (var role in existing.Roles)
            {
                var clone = Clone(role);
                _rolesById[clone.Id] = clone;
                _rolesByKey[clone.Key] = clone;
            }
        }

        if (existing.RolePermissions != null)
        {
            foreach (var (roleKey, permissionKeys) in existing.RolePermissions)
            {
                foreach (var permissionKey in permissionKeys)
                {
                    RolePermission(roleKey, permissionKey);
                }
            }
        }
    }

    /// <summary>Adds or replaces a resource type in the startup seed.</summary>
    /// <param name="id">The stable resource type identifier referenced by protected resources and permissions.</param>
    /// <param name="name">The resource type display name.</param>
    /// <param name="description">An optional description.</param>
    /// <returns>The same builder instance.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="id"/> or <paramref name="name"/> is empty.</exception>
    public SqlOSFgaSeedBuilder ResourceType(string id, string name, string? description = null)
    {
        var normalizedId = RequireValue(id, nameof(id));
        _resourceTypes[normalizedId] = new SqlOSFgaResourceType
        {
            Id = normalizedId,
            Name = RequireValue(name, nameof(name)),
            Description = NormalizeOptional(description)
        };
        return this;
    }

    /// <summary>Adds or replaces a permission with independent identifier and key values.</summary>
    /// <param name="id">The stable permission identifier.</param>
    /// <param name="key">The application-facing permission key used in access checks.</param>
    /// <param name="name">The permission display name.</param>
    /// <param name="resourceTypeId">The resource type to which the permission applies.</param>
    /// <param name="description">An optional description.</param>
    /// <returns>The same builder instance.</returns>
    /// <exception cref="InvalidOperationException">A required value is empty.</exception>
    public SqlOSFgaSeedBuilder Permission(string id, string key, string name, string resourceTypeId, string? description = null)
    {
        var normalizedId = RequireValue(id, nameof(id));
        var normalizedKey = RequireValue(key, nameof(key));
        if (normalizedKey.Length > SqlOSFgaPermission.MaxKeyLength)
        {
            throw new InvalidOperationException(
                $"FGA permission keys cannot exceed {SqlOSFgaPermission.MaxKeyLength} characters.");
        }
        if (_permissionsById.TryGetValue(normalizedId, out var existingPermission))
        {
            _permissionsByKey.Remove(existingPermission.Key);
        }

        if (_permissionsByKey.TryGetValue(normalizedKey, out var permissionWithSameKey)
            && !string.Equals(permissionWithSameKey.Id, normalizedId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"FGA permission key '{normalizedKey}' is already assigned to permission '{permissionWithSameKey.Id}'. Permission keys must be unique.");
        }

        var permission = new SqlOSFgaPermission
        {
            Id = normalizedId,
            Key = normalizedKey,
            Name = RequireValue(name, nameof(name)),
            ResourceTypeId = RequireValue(resourceTypeId, nameof(resourceTypeId)),
            Description = NormalizeOptional(description)
        };

        _permissionsById[permission.Id] = permission;
        _permissionsByKey[permission.Key] = permission;
        return this;
    }

    /// <summary>Adds or replaces a permission whose identifier and application-facing key are the same.</summary>
    /// <param name="key">The stable permission identifier and application-facing key.</param>
    /// <param name="name">The permission display name.</param>
    /// <param name="resourceTypeId">The resource type to which the permission applies.</param>
    /// <returns>The same builder instance.</returns>
    /// <exception cref="InvalidOperationException">A required value is empty.</exception>
    public SqlOSFgaSeedBuilder Permission(string key, string name, string resourceTypeId)
        => Permission(key, key, name, resourceTypeId);

    /// <summary>Adds or replaces a role with independent identifier and key values.</summary>
    /// <param name="id">The stable role identifier stored on grants.</param>
    /// <param name="key">The application-facing role key used by grant helpers.</param>
    /// <param name="name">The role display name.</param>
    /// <param name="description">An optional description.</param>
    /// <param name="isVirtual">Whether the role is virtual.</param>
    /// <returns>The same builder instance.</returns>
    /// <exception cref="InvalidOperationException">A required value is empty.</exception>
    public SqlOSFgaSeedBuilder Role(string id, string key, string name, string? description = null, bool isVirtual = false)
    {
        var normalizedId = RequireValue(id, nameof(id));
        var normalizedKey = RequireValue(key, nameof(key));
        if (_rolesById.TryGetValue(normalizedId, out var existingRole))
        {
            _rolesByKey.Remove(existingRole.Key);
        }

        var role = new SqlOSFgaRole
        {
            Id = normalizedId,
            Key = normalizedKey,
            Name = RequireValue(name, nameof(name)),
            Description = NormalizeOptional(description),
            IsVirtual = isVirtual
        };

        _rolesById[role.Id] = role;
        _rolesByKey[role.Key] = role;
        return this;
    }

    /// <summary>
    /// Adds or replaces a role whose identifier and application-facing key are the same, then
    /// starts a fluent permission assignment for that role.
    /// </summary>
    /// <param name="key">The stable role identifier and application-facing key.</param>
    /// <param name="name">The role display name.</param>
    /// <returns>A role seed builder for assigning one or more permissions with <see cref="SqlOSFgaRoleSeedBuilder.Can"/>.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="key"/> or <paramref name="name"/> is empty.</exception>
    public SqlOSFgaRoleSeedBuilder Role(string key, string name)
    {
        var normalizedKey = RequireValue(key, nameof(key));
        Role(normalizedKey, normalizedKey, name);
        return new SqlOSFgaRoleSeedBuilder(this, normalizedKey);
    }

    /// <summary>Assigns a permission to a role in the startup seed.</summary>
    /// <param name="roleKey">The application-facing key of a seeded role.</param>
    /// <param name="permissionKey">The application-facing key of a seeded permission.</param>
    /// <returns>The same builder instance.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="roleKey"/> or <paramref name="permissionKey"/> is empty.</exception>
    public SqlOSFgaSeedBuilder RolePermission(string roleKey, string permissionKey)
    {
        var normalizedRoleKey = RequireValue(roleKey, nameof(roleKey));
        var normalizedPermissionKey = RequireValue(permissionKey, nameof(permissionKey));

        if (!_rolePermissions.TryGetValue(normalizedRoleKey, out var permissionKeys))
        {
            permissionKeys = new HashSet<string>(StringComparer.Ordinal);
            _rolePermissions[normalizedRoleKey] = permissionKeys;
        }

        permissionKeys.Add(normalizedPermissionKey);
        return this;
    }

    internal SqlOSFgaSeedData Build()
        => new()
        {
            ResourceTypes = _resourceTypes.Values.Select(Clone).ToList(),
            Permissions = _permissionsById.Values.Select(Clone).ToList(),
            Roles = _rolesById.Values.Select(Clone).ToList(),
            RolePermissions = _rolePermissions
                .OrderBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static item => (item.Key, item.Value.OrderBy(static value => value, StringComparer.Ordinal).ToArray()))
                .ToList()
        };

    private static string RequireValue(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{paramName} is required for SqlOS FGA startup seeding.");
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SqlOSFgaResourceType Clone(SqlOSFgaResourceType source)
        => new()
        {
            Id = source.Id,
            Name = source.Name,
            Description = source.Description
        };

    private static SqlOSFgaPermission Clone(SqlOSFgaPermission source)
        => new()
        {
            Id = source.Id,
            Key = source.Key,
            Name = source.Name,
            Description = source.Description,
            ResourceTypeId = source.ResourceTypeId
        };

    private static SqlOSFgaRole Clone(SqlOSFgaRole source)
        => new()
        {
            Id = source.Id,
            Key = source.Key,
            Name = source.Name,
            Description = source.Description,
            IsVirtual = source.IsVirtual
        };
}

/// <summary>Assigns permissions to a role declared through the fluent FGA startup seed API.</summary>
public sealed class SqlOSFgaRoleSeedBuilder
{
    private readonly SqlOSFgaSeedBuilder _builder;
    private readonly string _roleKey;

    internal SqlOSFgaRoleSeedBuilder(SqlOSFgaSeedBuilder builder, string roleKey)
    {
        _builder = builder;
        _roleKey = roleKey;
    }

    /// <summary>Assigns one or more permission keys to the role.</summary>
    /// <param name="permissionKeys">The seeded permission keys to assign.</param>
    /// <returns>The parent FGA seed builder so that model declarations can continue.</returns>
    /// <exception cref="InvalidOperationException">A permission key is empty.</exception>
    public SqlOSFgaSeedBuilder Can(params string[] permissionKeys)
    {
        foreach (var permissionKey in permissionKeys)
        {
            _builder.RolePermission(_roleKey, permissionKey);
        }

        return _builder;
    }
}

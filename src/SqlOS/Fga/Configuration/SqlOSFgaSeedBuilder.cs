using SqlOS.Fga.Models;
using SqlOS.Fga.Services;

namespace SqlOS.Fga.Configuration;

public sealed class SqlOSFgaSeedBuilder
{
    private readonly Dictionary<string, SqlOSFgaResourceType> _resourceTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SqlOSFgaPermission> _permissionsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SqlOSFgaPermission> _permissionsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SqlOSFgaRole> _rolesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SqlOSFgaRole> _rolesByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _rolePermissions = new(StringComparer.Ordinal);

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

    public SqlOSFgaSeedBuilder Permission(string id, string key, string name, string resourceTypeId, string? description = null)
    {
        var normalizedId = RequireValue(id, nameof(id));
        var normalizedKey = RequireValue(key, nameof(key));
        if (_permissionsById.TryGetValue(normalizedId, out var existingPermission))
        {
            _permissionsByKey.Remove(existingPermission.Key);
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

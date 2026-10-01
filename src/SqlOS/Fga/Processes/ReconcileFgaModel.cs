using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SqlOS.Domain;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;

namespace SqlOS.Fga.Processes;

/// <summary>
/// Reconciles the authorization model of a seed (resource types, permissions, roles and role
/// permissions) into the store, in one save: <c>options.Fga.Seed</c> at startup and
/// <see cref="SqlOSFgaSeedService.SeedAuthorizationDataAsync"/> from host code.
/// </summary>
/// <remarks>
/// Records are matched by ID (role permissions by role and permission key) and created or
/// redefined; nothing is deleted. A role permission naming an unknown role or permission is skipped
/// with a warning.
/// </remarks>
internal sealed class ReconcileFgaModel(ISqlOSFgaDbContext context, ILogger logger)
{
    public async Task ExecuteAsync(SqlOSFgaSeedData data, FgaActor actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        foreach (var seed in data.ResourceTypes ?? [])
        {
            var type = await context.Set<SqlOSFgaResourceType>().FindAsync([seed.Id], cancellationToken);
            if (type == null)
            {
                context.Set<SqlOSFgaResourceType>().Add(SqlOSFgaResourceType.Define(seed.Id, seed.Name, seed.Description));
            }
            else
            {
                type.Redefine(seed.Name, seed.Description);
            }
        }

        foreach (var seed in data.Roles ?? [])
        {
            var role = await context.Set<SqlOSFgaRole>().FindAsync([seed.Id], cancellationToken);
            if (role == null)
            {
                context.Set<SqlOSFgaRole>().Add(SqlOSFgaRole.Define(seed.Id, seed.Key, seed.Name, seed.Description, seed.IsVirtual, actor));
            }
            else
            {
                role.Redefine(seed.Key, seed.Name, seed.Description, seed.IsVirtual, actor);
            }
        }

        foreach (var seed in data.Permissions ?? [])
        {
            if (seed.ResourceTypeId != null
                && await context.Set<SqlOSFgaResourceType>().FindAsync([seed.ResourceTypeId], cancellationToken) == null)
            {
                throw new InvalidOperationException(
                    $"FGA permission '{seed.Key}' applies to resource type '{seed.ResourceTypeId}', which is not defined. Seed the resource type first.");
            }

            var permission = await context.Set<SqlOSFgaPermission>().FindAsync([seed.Id], cancellationToken);
            if (permission == null)
            {
                context.Set<SqlOSFgaPermission>().Add(SqlOSFgaPermission.Define(seed.Id, seed.Key, seed.Name, seed.Description, seed.ResourceTypeId));
            }
            else
            {
                permission.Redefine(seed.Key, seed.Name, seed.Description, seed.ResourceTypeId);
            }
        }

        var loadedRoles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (roleKey, permissionKeys) in data.RolePermissions ?? [])
        {
            var role = context.Set<SqlOSFgaRole>().Local.FirstOrDefault(candidate => candidate.Key == roleKey)
                ?? await context.Set<SqlOSFgaRole>().FirstOrDefaultAsync(candidate => candidate.Key == roleKey, cancellationToken);
            if (role == null)
            {
                logger.LogWarning("Role with key {RoleKey} not found for role-permission mapping", roleKey);
                continue;
            }

            if (loadedRoles.Add(role.Id))
            {
                await context.Set<SqlOSFgaRolePermission>().Where(assignment => assignment.RoleId == role.Id).LoadAsync(cancellationToken);
            }

            foreach (var permissionKey in permissionKeys)
            {
                var permission = context.Set<SqlOSFgaPermission>().Local.FirstOrDefault(candidate => candidate.Key == permissionKey)
                    ?? await context.Set<SqlOSFgaPermission>().FirstOrDefaultAsync(candidate => candidate.Key == permissionKey, cancellationToken);
                if (permission == null)
                {
                    logger.LogWarning("Permission with key {PermKey} not found for role {RoleKey}", permissionKey, roleKey);
                    continue;
                }

                role.Allow(permission, actor);
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}

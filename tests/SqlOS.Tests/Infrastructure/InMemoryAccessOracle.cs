using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Models;

namespace SqlOS.Tests.Infrastructure;

/// <summary>
/// The access rule, evaluated by walking the in-memory store: a grant on the resource or an active ancestor,
/// made to the subject or one of its active groups, whose role includes the permission (scoped to the target's
/// type). SqlOS evaluates the same rule in the database from the resource lineage, which an in-memory store
/// does not have; the integration tests cover that on SQL Server and PostgreSQL. Unit tests use this oracle
/// only to check what grants a feature (SCIM, the control plane) left behind.
/// </summary>
internal static class InMemoryAccessOracle
{
    public static async Task<bool> AllowsAsync(DbContext db, string subjectId, string permissionKey, string resourceId)
    {
        var permission = await db.Set<SqlOSFgaPermission>().AsNoTracking().SingleAsync(p => p.Key == permissionKey);
        var target = await db.Set<SqlOSFgaResource>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == resourceId);
        if (target == null || (permission.ResourceTypeId != null && permission.ResourceTypeId != target.ResourceTypeId))
        {
            return false;
        }

        var subjects = new List<string> { subjectId };
        subjects.AddRange(await db.Set<SqlOSFgaUserGroupMembership>().AsNoTracking()
            .Where(m => m.SubjectId == subjectId)
            .Join(db.Set<SqlOSFgaUserGroup>().AsNoTracking().Where(g => g.IsActive), m => m.UserGroupId, g => g.Id, (m, g) => g.SubjectId)
            .ToListAsync());
        var roles = await db.Set<SqlOSFgaRolePermission>().AsNoTracking()
            .Where(rp => rp.PermissionId == permission.Id)
            .Select(rp => rp.RoleId)
            .ToListAsync();

        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var resource = target; resource != null && resource.IsActive && visited.Add(resource.Id);)
        {
            var id = resource.Id;
            if (await db.Set<SqlOSFgaGrant>().AsNoTracking().AnyAsync(g => g.ResourceId == id && subjects.Contains(g.SubjectId) && roles.Contains(g.RoleId)))
            {
                return true;
            }

            resource = resource.ParentId == null
                ? null
                : await db.Set<SqlOSFgaResource>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == resource.ParentId);
        }

        return false;
    }
}

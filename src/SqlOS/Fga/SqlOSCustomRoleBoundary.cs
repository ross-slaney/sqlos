using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Fga;

/// <summary>Fail closed even for legacy/raw grants and resources subsequently moved outside the owner's tree.</summary>
internal static class SqlOSCustomRoleBoundary
{
    internal static async Task<bool> AllowsAsync(ISqlOSFgaDbContext db, SqlOSFgaRole role, string resource, string permission)
    {
        if (role.ArchivedAt != null) return false;
        if (role.DefinitionSource == SqlOSFgaRoleSources.Application) return role.OwnerResourceId == null;
        if (role.DefinitionSource != SqlOSFgaRoleSources.Customer || role.OwnerResourceId == null) return false;
        if (!await db.Set<SqlOSFgaPermission>().AnyAsync(x => x.Id == permission && x.AllowInCustomRoles)) return false;
        return await new SqlOSFgaSubtreeResolver(db.Set<SqlOSFgaResource>(), SqlOSFgaHierarchyDepth.Resolve(db.Database, db as DbContext))
            .CheckAsync(resource, role.OwnerResourceId) == SqlOSFgaSubtreeMembership.Within;
    }
}

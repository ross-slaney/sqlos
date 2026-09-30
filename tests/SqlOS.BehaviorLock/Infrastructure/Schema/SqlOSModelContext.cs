using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.BehaviorLock.Infrastructure.Schema;

/// <summary>
/// A context holding only the SqlOS EF model (<c>UseSqlOS</c>), for snapshotting the DDL EF would
/// generate for it. Hosts query SqlOS entities through this mapping, so a mapping change is a
/// public behavior change even when the migration scripts do not move.
/// </summary>
public sealed class SqlOSModelContext(DbContextOptions<SqlOSModelContext> options)
    : DbContext(options), ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
{
    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(string resourceId, string subjectIds, string permissionId)
        => FromExpression(() => IsResourceAccessible(resourceId, subjectIds, permissionId));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseSqlOS(GetType(), Database.ProviderName);
    }
}

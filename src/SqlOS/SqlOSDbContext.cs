using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Extensions;
using SqlOS.Fga;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS;

/// <summary>
/// Base DbContext for applications that host SqlOS auth server and FGA in the same EF Core model.
/// </summary>
public abstract class SqlOSDbContext<TContext>(DbContextOptions<TContext> options)
    : DbContext(options), ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
    where TContext : SqlOSDbContext<TContext>
{
    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string resourceId,
        string subjectIds,
        string permissionId)
        => FromExpression(() => IsResourceAccessible(resourceId, subjectIds, permissionId));

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseSqlOS(Database.IsRelational() ? typeof(TContext) : null);
        OnApplicationModelCreating(modelBuilder);
    }

    /// <summary>
    /// Configure application-owned EF entities after SqlOS has registered its auth server and FGA model.
    /// </summary>
    protected virtual void OnApplicationModelCreating(ModelBuilder modelBuilder)
    {
    }

    public override int SaveChanges()
        => SaveChanges(acceptAllChangesOnSuccess: true);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        SqlOSResourceEntitySynchronizer.Sync(this);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken);

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        await SqlOSResourceEntitySynchronizer.SyncAsync(this, cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}

using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Database;
using SqlOS.Extensions;
using SqlOS.Fga;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS;

/// <summary>
/// Base DbContext for applications that host SqlOS auth server and FGA in the same EF Core model.
/// </summary>
/// <typeparam name="TContext">The concrete application context type.</typeparam>
/// <remarks>
/// SqlOS registers its auth, email, calendar, and FGA entities before invoking
/// <see cref="OnApplicationModelCreating"/>. Save operations synchronize tracked
/// <see cref="ISqlOSResourceEntity"/> instances with their backing FGA resources.
/// </remarks>
public abstract class SqlOSDbContext<TContext> : DbContext, ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
    where TContext : SqlOSDbContext<TContext>
{
    /// <param name="options">The EF Core options registered for the concrete application context.</param>
    protected SqlOSDbContext(DbContextOptions<TContext> options)
        : base(options)
    {
        if (SqlOSDatabase.IsPostgreSql(Database.ProviderName))
        {
            SqlOSDatabase.EnablePostgreSqlTimestampCompatibility();
        }
    }

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string resourceId,
        string subjectIds,
        string permissionId)
        => FromExpression(() => IsResourceAccessible(resourceId, subjectIds, permissionId));

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseSqlOS(Database.IsRelational() ? typeof(TContext) : null, Database.ProviderName);
        OnApplicationModelCreating(modelBuilder);
    }

    /// <summary>
    /// Configure application-owned EF entities after SqlOS has registered its auth server and FGA model.
    /// </summary>
    /// <param name="modelBuilder">The builder for the combined application and SqlOS EF Core model.</param>
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

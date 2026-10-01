using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Database;
using SqlOS.Extensions;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.IntegrationTests.Infrastructure;

public sealed class TestSqlOSDbContext : DbContext, ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
{
    public TestSqlOSDbContext(DbContextOptions<TestSqlOSDbContext> options) : base(options)
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<LifecycleProtectedEntity>(entity =>
        {
            entity.ToTable("LifecycleProtectedEntities");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.Rank);
        });

        // Scope columns on: the application table carries the lineage, so BuildFilterAsync reads it from the
        // row. PlainTestSqlOSDbContext maps the same tables without them, so both forms of the filter are tested.
        modelBuilder.UseSqlOS(GetType(), Database.ProviderName, new SqlOSFgaOptions { ScopeColumns = true });
    }
}

/// <summary>The same tables as <see cref="TestSqlOSDbContext"/>, mapped without the scope columns.</summary>
public sealed class PlainTestSqlOSDbContext : DbContext, ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
{
    public PlainTestSqlOSDbContext(DbContextOptions<PlainTestSqlOSDbContext> options) : base(options)
    {
    }

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string resourceId,
        string subjectIds,
        string permissionId)
        => FromExpression(() => IsResourceAccessible(resourceId, subjectIds, permissionId));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<LifecycleProtectedEntity>(entity =>
        {
            entity.ToTable("LifecycleProtectedEntities");
            entity.HasKey(item => item.Id);
        });
        modelBuilder.UseSqlOS(GetType(), Database.ProviderName);
    }
}

public sealed class LifecycleProtectedEntity : IHasResourceId
{
    public string Id { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;

    /// <summary>An order the application pages in; its index is mirrored per level by the scope columns.</summary>
    public int Rank { get; set; }
}

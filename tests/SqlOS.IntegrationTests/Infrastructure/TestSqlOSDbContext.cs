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

        // Last, after the application's entity: the SqlOS model and the scope column on LifecycleProtectedEntities.
        modelBuilder.UseSqlOS(GetType(), Database.ProviderName);
    }
}

public sealed class LifecycleProtectedEntity : IHasResourceId
{
    public string Id { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;

    /// <summary>An order the application pages in; SqlOS mirrors its index per level of the scope column.</summary>
    public int Rank { get; set; }
}

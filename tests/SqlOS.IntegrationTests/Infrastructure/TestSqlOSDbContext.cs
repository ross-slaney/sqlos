using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Database;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.IntegrationTests.Infrastructure;

/// <summary>
/// The host context SqlOS services see in integration tests. Like <see cref="SqlOSDbContext{TContext}"/>
/// and every context <c>AddSqlOS</c> configures, it runs SqlOS's domain events interceptor, so
/// services built by hand in a test write the audit rows of their events the way they do in a host.
/// </summary>
public sealed class TestSqlOSDbContext : DbContext, ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
{
    public TestSqlOSDbContext(DbContextOptions<TestSqlOSDbContext> options)
        : base(SqlOSDomainEventsInterceptor.AttachTo(options))
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
        });
        modelBuilder.UseSqlOS(GetType(), Database.ProviderName);
    }
}

public sealed class LifecycleProtectedEntity : IHasResourceId
{
    public string Id { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
}

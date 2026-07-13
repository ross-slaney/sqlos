using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.IntegrationTests.Infrastructure;

public sealed class TestSqlOSDbContext : DbContext, ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
{
    public TestSqlOSDbContext(DbContextOptions<TestSqlOSDbContext> options) : base(options)
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
        modelBuilder.UseSqlOS(GetType());
    }
}

public sealed class LifecycleProtectedEntity : IHasResourceId
{
    public string Id { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
}

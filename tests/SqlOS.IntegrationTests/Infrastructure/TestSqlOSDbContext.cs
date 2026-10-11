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

    /// <summary>Runs SqlOS's point-check function (<c>fn_IsResourceAccessible</c>) directly: whether any grant reaches the resource.</summary>
    public Task<bool> FunctionAllowsAsync(string resourceId, string subjectIdsJson, string permissionId)
        => Set<SqlOSFgaAccessMatch>()
            .FromSqlRaw(SqlOSDatabase.Resolve(Database).BuildAccessMatchQuerySql(new SqlOSFgaOptions()), resourceId, subjectIdsJson, permissionId)
            .AnyAsync();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<LifecycleProtectedEntity>(entity =>
        {
            entity.ToTable("LifecycleProtectedEntities");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.Rank);
        });

        // Last, after the application's entity: the SqlOS model and the resource id index on LifecycleProtectedEntities.
        modelBuilder.UseSqlOS(Database.ProviderName);
    }
}

public sealed class LifecycleProtectedEntity : IHasResourceId
{

    public string Id { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;

    /// <summary>An order the application pages in.</summary>
    public int Rank { get; set; }
}

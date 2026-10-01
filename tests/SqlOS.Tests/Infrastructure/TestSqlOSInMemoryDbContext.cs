using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Database;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Tests.Infrastructure;

/// <summary>
/// The host context SqlOS services see in tests. Like <see cref="SqlOSDbContext{TContext}"/> and every
/// context <c>AddSqlOS</c> configures, it runs SqlOS's domain events interceptor, so services built
/// by hand in a test write the audit rows of their events the way they do in a host.
/// </summary>
public sealed class TestSqlOSInMemoryDbContext : DbContext, ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
{
    public TestSqlOSInMemoryDbContext(DbContextOptions<TestSqlOSInMemoryDbContext> options)
        : base(SqlOSDomainEventsInterceptor.AttachTo(options))
    {
    }

    public int SaveChangesAsyncCallCount { get; private set; }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveChangesAsyncCallCount++;
        return base.SaveChangesAsync(cancellationToken);
    }

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string resourceId,
        string subjectIds,
        string permissionId)
        => throw new NotSupportedException("TVFs are not supported for the in-memory test context.");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseSqlOS();
    }
}

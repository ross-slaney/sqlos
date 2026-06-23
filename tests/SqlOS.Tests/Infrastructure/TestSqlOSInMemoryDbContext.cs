using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Tests.Infrastructure;

public sealed class TestSqlOSInMemoryDbContext : DbContext, ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
{
    public TestSqlOSInMemoryDbContext(DbContextOptions<TestSqlOSInMemoryDbContext> options) : base(options)
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

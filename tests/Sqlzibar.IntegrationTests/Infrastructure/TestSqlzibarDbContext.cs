using Microsoft.EntityFrameworkCore;
using Sqlzibar.Extensions;
using Sqlzibar.Interfaces;
using Sqlzibar.Models;

namespace Sqlzibar.IntegrationTests.Infrastructure;

public class TestSqlzibarDbContext : DbContext, ISqlzibarDbContext
{
    public TestSqlzibarDbContext(DbContextOptions<TestSqlzibarDbContext> options) : base(options) { }

    public IQueryable<SqlzibarAccessibleResource> IsResourceAccessible(
        string resourceId, string subjectIds, string permissionId)
        => FromExpression(() => IsResourceAccessible(resourceId, subjectIds, permissionId));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplySqlzibarModel(GetType());
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSScimAdminHardeningTests
{
    [TestMethod]
    public async Task AdminMutation_OpportunisticallyCleansOneBoundedBatchOfExpiredCommitMarkers()
    {
        using var context = CreateContext();
        context.Set<SqlOSOrganization>().Add(new SqlOSOrganization
        {
            Id = "org_cleanup",
            Slug = "cleanup",
            Name = "Cleanup",
            CreatedAt = DateTime.UtcNow
        });
        var expiredAt = DateTime.UtcNow.AddDays(-2);
        context.Set<SqlOSScimOperationCommit>().AddRange(Enumerable.Range(0, 300).Select(index =>
            new SqlOSScimOperationCommit
            {
                Id = $"expired_{index:D4}",
                OccurredAt = expiredAt
            }));
        context.Set<SqlOSScimOperationCommit>().Add(new SqlOSScimOperationCommit
        {
            Id = "recent_marker",
            OccurredAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var admin = CreateAdmin(context);

        var connection = await admin.CreateScimConnectionDraftAsync(
            new SqlOSCreateScimConnectionRequest("org_cleanup", "Cleanup directory", Enabled: false));

        (await context.Set<SqlOSScimOperationCommit>()
                .CountAsync(marker => marker.OccurredAt == expiredAt))
            .Should().Be(44, "normal operations should retire at most one 256-row batch");
        (await context.Set<SqlOSScimOperationCommit>().AnyAsync(marker => marker.Id == "recent_marker"))
            .Should().BeTrue();

        await admin.RotateScimTokenAsync(connection.Id);

        (await context.Set<SqlOSScimOperationCommit>()
                .AnyAsync(marker => marker.OccurredAt == expiredAt))
            .Should().BeFalse("later operations should continue draining an old backlog");
        (await context.Set<SqlOSScimOperationCommit>().AnyAsync(marker => marker.Id == "recent_marker"))
            .Should().BeTrue();
    }

    private static SqlOSAdminService CreateAdmin(TestSqlOSInMemoryDbContext context)
    {
        var options = Options.Create(new SqlOSAuthServerOptions());
        return new SqlOSAdminService(context, options, new SqlOSCryptoService(context, options));
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase($"sqlos-scim-admin-hardening-{Guid.NewGuid():N}")
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }
}

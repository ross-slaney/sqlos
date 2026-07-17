using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.AuditLogs;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSSessionRevocationServiceTests
{
    [TestMethod]
    public async Task Preview_ThenExecute_WithCombinedFilters_IsBoundedAndIdempotent()
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        Seed(context);
        await context.SaveChangesAsync();
        var request = new SqlOSAdminSessionRevocationRequest(
            UserId: "user-1",
            OrganizationId: "org-1",
            ClientApplicationId: "client-1",
            Reason: "incident-42",
            OperationId: "op-42");

        var preview = await service.PreviewAsync(request);
        preview.Preview.Should().BeTrue();
        preview.MatchedSessions.Should().Be(2);
        preview.AlreadyRevokedSessions.Should().Be(1);
        preview.ActiveRefreshTokens.Should().Be(1);

        var executed = await service.RevokeAsync(request with { Confirm = true });
        executed.NewlyRevokedSessions.Should().Be(1);
        executed.NewlyRevokedRefreshTokens.Should().Be(1);
        executed.OperationId.Should().Be("op-42");

        var repeated = await service.RevokeAsync(request with { Confirm = true });
        repeated.NewlyRevokedSessions.Should().Be(0);
        repeated.NewlyRevokedRefreshTokens.Should().Be(0);
        repeated.AlreadyRevokedSessions.Should().Be(2);

        (await context.Set<SqlOSSession>().SingleAsync(x => x.Id == "matching-active"))
            .RevocationReason.Should().Be("incident-42");
        (await context.Set<SqlOSRefreshToken>().SingleAsync(x => x.SessionId == "matching-active"))
            .ReplacementTokenResponse.Should().BeNull();
        (await context.Set<SqlOSSession>().SingleAsync(x => x.Id == "other-org"))
            .RevokedAt.Should().BeNull();
    }

    [TestMethod]
    public async Task Execute_RequiresConfirmationAndSelector()
    {
        await using var context = CreateContext();
        var service = CreateService(context);

        await FluentActions.Invoking(() => service.RevokeAsync(
                new SqlOSAdminSessionRevocationRequest(UserId: "user-1")))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*confirmation*");
        await FluentActions.Invoking(() => service.PreviewAsync(
                new SqlOSAdminSessionRevocationRequest()))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*selector*");
    }

    [TestMethod]
    public async Task Execute_RecordsRedactedAuditMetadata()
    {
        await using var context = CreateContext();
        var service = CreateService(context);
        Seed(context);
        await context.SaveChangesAsync();

        await service.RevokeAsync(new SqlOSAdminSessionRevocationRequest(
            SessionId: "matching-active",
            Reason: "compromised device",
            OperationId: "incident-op",
            Confirm: true));

        var audit = await context.Set<SqlOSAuditEvent>().SingleAsync(x => x.EventType == "session.admin-revoked");
        audit.DataJson.Should().Contain("incident-op");
        audit.DataJson.Should().Contain("compromised device");
        audit.DataJson.Should().NotContain("cached-token-response");
    }

    [TestMethod]
    public async Task Preview_RejectsAnUnboundedBulkOperation()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.Set<SqlOSSession>().AddRange(Enumerable.Range(0, 10_001).Select(index => new SqlOSSession
        {
            Id = $"bulk-{index}", UserId = "bulk-user", CreatedAt = now, LastSeenAt = now,
            IdleExpiresAt = now.AddHours(1), AbsoluteExpiresAt = now.AddDays(1)
        }));
        await context.SaveChangesAsync();

        await FluentActions.Invoking(() => CreateService(context).PreviewAsync(
                new SqlOSAdminSessionRevocationRequest(UserId: "bulk-user")))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*10,000*");
    }

    [TestMethod]
    public async Task Execute_MissingCrossTenantSelectors_ReturnsGenericZeroWithoutAudit()
    {
        await using var context = CreateContext();
        var result = await CreateService(context).RevokeAsync(new SqlOSAdminSessionRevocationRequest(
            UserId: "unknown-user",
            OrganizationId: "unknown-organization",
            Confirm: true));

        result.MatchedSessions.Should().Be(0);
        result.AuditEventId.Should().BeNull();
        (await context.Set<SqlOSAuditEvent>().CountAsync()).Should().Be(0);
    }

    private static SqlOSSessionRevocationService CreateService(TestSqlOSInMemoryDbContext context)
    {
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        return new SqlOSSessionRevocationService(context, new SqlOSAuditLogService(context, crypto));
    }

    private static void Seed(TestSqlOSInMemoryDbContext context)
    {
        var now = DateTime.UtcNow;
        context.Set<SqlOSSession>().AddRange(
            Session("matching-active", "org-1", "client-1"),
            Session("matching-revoked", "org-1", "client-1", now.AddMinutes(-2)),
            Session("other-org", "org-2", "client-1"),
            Session("other-client", "org-1", "client-2"));
        context.Set<SqlOSRefreshToken>().Add(new SqlOSRefreshToken
        {
            Id = "refresh-1", SessionId = "matching-active", TokenHash = "hash", FamilyId = "family",
            CreatedAt = now, ExpiresAt = now.AddDays(1), ReplacementTokenResponse = "cached-token-response",
            ReplacementOrganizationId = "org-1", ReplacementAccessTokenExpiresAt = now.AddMinutes(5)
        });
    }

    private static SqlOSSession Session(string id, string organizationId, string clientId, DateTime? revokedAt = null)
        => new()
        {
            Id = id, UserId = "user-1", OrganizationId = organizationId, ClientApplicationId = clientId,
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
            IdleExpiresAt = DateTime.UtcNow.AddHours(1), AbsoluteExpiresAt = DateTime.UtcNow.AddDays(1),
            RevokedAt = revokedAt, RevocationReason = revokedAt == null ? null : "previous"
        };

    private static TestSqlOSInMemoryDbContext CreateContext()
        => new(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}

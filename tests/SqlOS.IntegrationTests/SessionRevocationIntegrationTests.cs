using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.AuditLogs;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class SessionRevocationIntegrationTests
{
    [TestMethod]
    public async Task ConcurrentRealSqlRevocation_CountsEachSessionAndRefreshTokenAsNewOnce()
    {
        await using var setup = await AspireFixture.CreateIsolatedAuthContextAsync("SessionRevoke");
        try
        {
            var now = DateTime.UtcNow;
            setup.Set<SqlOSOrganization>().Add(new SqlOSOrganization
            {
                Id = "org-concurrent", Slug = "org-concurrent", Name = "Concurrent org", CreatedAt = now
            });
            setup.Set<SqlOSUser>().Add(new SqlOSUser
            {
                Id = "user-concurrent", DisplayName = "Concurrent user", CreatedAt = now, UpdatedAt = now
            });
            setup.Set<SqlOSClientApplication>().Add(new SqlOSClientApplication
            {
                Id = "client-concurrent", ClientId = "client-concurrent", Name = "Concurrent client",
                Audience = "sqlos", RedirectUrisJson = "[]", CreatedAt = now
            });
            setup.Set<SqlOSSession>().Add(new SqlOSSession
            {
                Id = "session-concurrent", UserId = "user-concurrent", OrganizationId = "org-concurrent",
                ClientApplicationId = "client-concurrent", CreatedAt = now, LastSeenAt = now,
                IdleExpiresAt = now.AddHours(1), AbsoluteExpiresAt = now.AddDays(1)
            });
            setup.Set<SqlOSRefreshToken>().Add(new SqlOSRefreshToken
            {
                Id = "refresh-concurrent", SessionId = "session-concurrent", TokenHash = "hash",
                FamilyId = "family", CreatedAt = now, ExpiresAt = now.AddDays(1),
                ReplacementTokenResponse = "sensitive-cache"
            });
            await setup.SaveChangesAsync();
            var connectionString = setup.Database.GetConnectionString()!;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<SqlOSAdminSessionRevocationResult> RunAsync(string operationId)
            {
                await using var context = new TestSqlOSDbContext(
                    new DbContextOptionsBuilder<TestSqlOSDbContext>().UseTestProvider(connectionString).Options);
                var options = Options.Create(new SqlOSAuthServerOptions());
                var crypto = new SqlOSCryptoService(context, options);
                var service = new SqlOSSessionRevocationService(context, new SqlOSAuditLogService(context, crypto));
                await ready.Task;
                return await service.RevokeAsync(new SqlOSAdminSessionRevocationRequest(
                    UserId: "user-concurrent", Reason: "incident", OperationId: operationId, Confirm: true,
                    ExpectedMatchedSessions: 1));
            }

            var first = RunAsync("operation-a");
            var second = RunAsync("operation-b");
            ready.SetResult();
            var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(45));

            results.Sum(x => x.NewlyRevokedSessions).Should().Be(1);
            results.Sum(x => x.NewlyRevokedRefreshTokens).Should().Be(1);
            results.Should().ContainSingle(x => x.AlreadyRevokedSessions == 1);
            setup.ChangeTracker.Clear();
            (await setup.Set<SqlOSRefreshToken>().AsNoTracking().SingleAsync()).ReplacementTokenResponse.Should().BeNull();
            (await setup.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "session.admin-revoked")).Should().Be(2);
        }
        finally
        {
            await setup.Database.EnsureDeletedAsync();
        }
    }
}

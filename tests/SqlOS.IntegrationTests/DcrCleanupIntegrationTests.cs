using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SqlOS.IntegrationTests;

/// <summary>
/// Real-SQL coverage for stale dynamic-client cleanup, where the
/// FK_SqlOSConsentGrants_ClientApplication constraint is actually enforced: a stale DCR
/// client whose only remaining reference is a consent grant must still be deletable at
/// startup instead of crashing bootstrap with a DbUpdateException.
/// </summary>
[TestClass]
public sealed class DcrCleanupIntegrationTests
{
    [TestMethod]
    public async Task CleanupStaleDynamicClients_RemovesConsentGrantsWithTheClient()
    {
        TestSqlOSDbContext? context = null;

        try
        {
            context = await AspireFixture.CreateIsolatedAuthContextAsync("SqlOSDcrCleanup");

            var optionsValue = new SqlOSAuthServerOptions
            {
                Issuer = "https://auth.example.test/sqlos/auth",
                BasePath = "/sqlos/auth"
            };
            optionsValue.ClientRegistration.Dcr.StaleClientRetention = TimeSpan.FromDays(30);
            var options = Options.Create(optionsValue);
            var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
            var admin = new SqlOSAdminService(context, options, crypto);

            var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "DCR Cleanup User",
                $"dcr-cleanup-{Guid.NewGuid():N}@example.test",
                "P@ssword123!"));

            var staleClient = new SqlOSClientApplication
            {
                Id = "cli_stale_dcr_grant",
                ClientId = "stale-dcr-with-grant",
                Name = "Stale DCR With Grant",
                Audience = "sqlos",
                RedirectUrisJson = "[\"https://client.example.test/callback\"]",
                RegistrationSource = "dcr",
                CreatedAt = DateTime.UtcNow.AddDays(-60),
                LastSeenAt = DateTime.UtcNow.AddDays(-60),
                IsActive = true
            };
            context.Set<SqlOSClientApplication>().Add(staleClient);
            context.Set<SqlOSConsentGrant>().Add(new SqlOSConsentGrant
            {
                Id = "cgr_stale_dcr",
                UserId = user.Id,
                ClientApplicationId = staleClient.Id,
                Scope = "openid todo:read",
                GrantedAt = DateTime.UtcNow.AddDays(-60),
                UpdatedAt = DateTime.UtcNow.AddDays(-60)
            });
            await context.SaveChangesAsync();

            var removed = await admin.CleanupStaleDynamicClientsAsync();

            removed.Should().Be(1, "a consent grant without sessions must not block cleanup");
            (await context.Set<SqlOSClientApplication>().AnyAsync(x => x.Id == staleClient.Id))
                .Should().BeFalse("the stale DCR client is deleted");
            (await context.Set<SqlOSConsentGrant>().AnyAsync(x => x.ClientApplicationId == staleClient.Id))
                .Should().BeFalse("grants are meaningless once the client is gone");
            (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.cleanup.removed"))
                .Should().BeTrue("cleanup keeps its existing audit trail");
        }
        finally
        {
            if (context != null)
            {
                await context.Database.EnsureDeletedAsync();
                await context.DisposeAsync();
            }
        }
    }
}

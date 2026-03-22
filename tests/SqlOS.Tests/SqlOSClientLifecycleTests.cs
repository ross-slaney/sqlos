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
public sealed class SqlOSClientLifecycleTests
{
    [TestMethod]
    public async Task DisableClientAsync_RevokesSessions_AndSurvivesSeededUpsert()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedBrowserClient("seeded-client", "Seeded Client", "https://client.example.test/callback");
        var options = Options.Create(optionsValue);
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);

        await admin.UpsertSeededClientsAsync();
        var client = await context.Set<SqlOSClientApplication>().SingleAsync();
        var user = await SeedUserAsync(context);
        context.Set<SqlOSSession>().Add(new SqlOSSession
        {
            Id = "sess_seeded",
            UserId = user.Id,
            ClientApplicationId = client.Id,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            IdleExpiresAt = DateTime.UtcNow.AddHours(1),
            AbsoluteExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        context.Set<SqlOSRefreshToken>().Add(new SqlOSRefreshToken
        {
            Id = "rfr_seeded",
            SessionId = "sess_seeded",
            TokenHash = "hash_seeded",
            FamilyId = "fam_seeded",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        await context.SaveChangesAsync();

        await admin.DisableClientAsync(client.Id, "manual review");
        await admin.UpsertSeededClientsAsync();

        var updatedClient = await context.Set<SqlOSClientApplication>().SingleAsync();
        var session = await context.Set<SqlOSSession>().SingleAsync();
        var refreshToken = await context.Set<SqlOSRefreshToken>().SingleAsync();
        updatedClient.IsActive.Should().BeFalse();
        updatedClient.DisabledAt.Should().NotBeNull();
        updatedClient.DisabledReason.Should().Be("manual review");
        updatedClient.RegistrationSource.Should().Be("seeded");
        session.RevokedAt.Should().NotBeNull();
        refreshToken.RevokedAt.Should().NotBeNull();
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.disabled")).Should().BeTrue();
    }

    [TestMethod]
    public async Task EnableClientAsync_RestoresActiveState_AndAudits()
    {
        using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);

        var client = new SqlOSClientApplication
        {
            Id = "cli_disabled",
            ClientId = "disabled-client",
            Name = "Disabled Client",
            Audience = "sqlos",
            RedirectUrisJson = "[\"https://client.example.test/callback\"]",
            RegistrationSource = "manual",
            CreatedAt = DateTime.UtcNow,
            IsActive = false,
            DisabledAt = DateTime.UtcNow,
            DisabledReason = "manual review"
        };
        context.Set<SqlOSClientApplication>().Add(client);
        await context.SaveChangesAsync();

        await admin.EnableClientAsync(client.Id);

        client.IsActive.Should().BeTrue();
        client.DisabledAt.Should().BeNull();
        client.DisabledReason.Should().BeNull();
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.enabled")).Should().BeTrue();
    }

    [TestMethod]
    public async Task CleanupStaleDynamicClientsAsync_RemovesOnlyStaleDcrClientsWithoutSessions()
    {
        using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.ClientRegistration.Dcr.StaleClientRetention = TimeSpan.FromDays(30);
        var options = Options.Create(optionsValue);
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);

        context.Set<SqlOSClientApplication>().AddRange(
            new SqlOSClientApplication
            {
                Id = "cli_stale_dcr",
                ClientId = "stale-dcr",
                Name = "Stale DCR",
                Audience = "sqlos",
                RedirectUrisJson = "[\"https://client.example.test/callback\"]",
                RegistrationSource = "dcr",
                CreatedAt = DateTime.UtcNow.AddDays(-60),
                LastSeenAt = DateTime.UtcNow.AddDays(-60),
                IsActive = true
            },
            new SqlOSClientApplication
            {
                Id = "cli_recent_dcr",
                ClientId = "recent-dcr",
                Name = "Recent DCR",
                Audience = "sqlos",
                RedirectUrisJson = "[\"https://client.example.test/callback\"]",
                RegistrationSource = "dcr",
                CreatedAt = DateTime.UtcNow.AddDays(-5),
                LastSeenAt = DateTime.UtcNow.AddDays(-1),
                IsActive = true
            },
            new SqlOSClientApplication
            {
                Id = "cli_manual",
                ClientId = "manual-client",
                Name = "Manual Client",
                Audience = "sqlos",
                RedirectUrisJson = "[\"https://client.example.test/callback\"]",
                RegistrationSource = "manual",
                CreatedAt = DateTime.UtcNow.AddDays(-60),
                LastSeenAt = DateTime.UtcNow.AddDays(-60),
                IsActive = true
            },
            new SqlOSClientApplication
            {
                Id = "cli_dcr_with_session",
                ClientId = "dcr-with-session",
                Name = "DCR With Session",
                Audience = "sqlos",
                RedirectUrisJson = "[\"https://client.example.test/callback\"]",
                RegistrationSource = "dcr",
                CreatedAt = DateTime.UtcNow.AddDays(-60),
                LastSeenAt = DateTime.UtcNow.AddDays(-60),
                IsActive = true
            });
        context.Set<SqlOSSession>().Add(new SqlOSSession
        {
            Id = "sess_active",
            UserId = "usr_stale",
            ClientApplicationId = "cli_dcr_with_session",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            LastSeenAt = DateTime.UtcNow.AddDays(-1),
            IdleExpiresAt = DateTime.UtcNow.AddHours(1),
            AbsoluteExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        await context.SaveChangesAsync();

        var removed = await admin.CleanupStaleDynamicClientsAsync();

        removed.Should().Be(1);
        (await context.Set<SqlOSClientApplication>().AnyAsync(x => x.Id == "cli_stale_dcr")).Should().BeFalse();
        (await context.Set<SqlOSClientApplication>().AnyAsync(x => x.Id == "cli_recent_dcr")).Should().BeTrue();
        (await context.Set<SqlOSClientApplication>().AnyAsync(x => x.Id == "cli_manual")).Should().BeTrue();
        (await context.Set<SqlOSClientApplication>().AnyAsync(x => x.Id == "cli_dcr_with_session")).Should().BeTrue();
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "client.cleanup.removed")).Should().BeTrue();
    }

    [TestMethod]
    public async Task ValidateAccessTokenAsync_UpdatesClientLastSeen()
    {
        using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions
        {
            Issuer = "https://app.example.com/sqlos/auth",
            PublicOrigin = "https://app.example.com"
        });
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var settings = new SqlOSSettingsService(context, options);
        var auth = new SqlOSAuthService(context, options, admin, crypto, settings);

        var user = await SeedUserAsync(context);
        var client = new SqlOSClientApplication
        {
            Id = "cli_seen",
            ClientId = "seen-client",
            Name = "Seen Client",
            Audience = "sqlos",
            RedirectUrisJson = "[\"https://client.example.test/callback\"]",
            RegistrationSource = "manual",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        context.Set<SqlOSClientApplication>().Add(client);
        await context.SaveChangesAsync();

        var tokens = await auth.CreateSessionTokensForUserAsync(user, client, null, "password", "agent", "127.0.0.1");
        client.LastSeenAt = null;
        await context.SaveChangesAsync();

        var validated = await auth.ValidateAccessTokenAsync(tokens.AccessToken, "sqlos");

        validated.Should().NotBeNull();
        client.LastSeenAt.Should().NotBeNull();
    }

    private static async Task<SqlOSUser> SeedUserAsync(TestSqlOSInMemoryDbContext context)
    {
        var user = new SqlOSUser
        {
            Id = "usr_stale",
            DisplayName = "Alice",
            DefaultEmail = "alice@example.com",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Set<SqlOSUser>().Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }
}

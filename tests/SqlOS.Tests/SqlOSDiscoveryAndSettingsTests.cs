using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSDiscoveryAndSettingsTests
{
    [TestMethod]
    public async Task DiscoverAsync_ReturnsSso_WhenMatchingPrimaryDomainHasEnabledConnection()
    {
        await using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var discovery = new SqlOSHomeRealmDiscoveryService(context);

        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Contoso", null, "contoso.com"));
        await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            organization.Id,
            "Contoso SSO",
            "urn:test:idp",
            "https://idp.example.test/sso",
            "-----BEGIN CERTIFICATE-----\nTEST\n-----END CERTIFICATE-----",
            true,
            false,
            "email",
            "first_name",
            "last_name"));

        var result = await discovery.DiscoverAsync(new SqlOSHomeRealmDiscoveryRequest("alice@contoso.com"));

        result.Mode.Should().Be("sso");
        result.OrganizationId.Should().Be(organization.Id);
        result.PrimaryDomain.Should().Be("contoso.com");
        result.ConnectionId.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task SettingsService_SeedsDefaults_AndCanBeUpdated()
    {
        await using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions
        {
            RefreshTokenLifetime = TimeSpan.FromDays(90),
            SessionIdleTimeout = TimeSpan.FromDays(2),
            SessionAbsoluteLifetime = TimeSpan.FromDays(30)
        });
        var settingsService = new SqlOSSettingsService(context, options);

        var defaults = await settingsService.GetSecuritySettingsAsync();
        defaults.RefreshTokenLifetimeMinutes.Should().Be((int)TimeSpan.FromDays(90).TotalMinutes);
        defaults.SessionIdleTimeoutMinutes.Should().Be((int)TimeSpan.FromDays(2).TotalMinutes);
        defaults.SessionAbsoluteLifetimeMinutes.Should().Be((int)TimeSpan.FromDays(30).TotalMinutes);

        var updated = await settingsService.UpdateSecuritySettingsAsync(new SqlOSUpdateSecuritySettingsRequest(1440, 60, 2880, 90, 7, 30));
        updated.RefreshTokenLifetimeMinutes.Should().Be(1440);
        updated.SessionIdleTimeoutMinutes.Should().Be(60);
        updated.SessionAbsoluteLifetimeMinutes.Should().Be(2880);
        updated.SigningKeyRotationIntervalDays.Should().Be(90);
        updated.SigningKeyGraceWindowDays.Should().Be(7);
        updated.SigningKeyRetiredCleanupDays.Should().Be(30);
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }
}

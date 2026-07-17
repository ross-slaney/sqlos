using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
public sealed class SqlOSSamlConnectionSeedingTests
{
    [TestMethod]
    public async Task Seed_CreateRerunRenameAndOrphan_AreOwnershipSafe()
    {
        await using var context = CreateContext();
        var certificate = CreateCertificatePem();
        var optionsValue = new SqlOSAuthServerOptions();
        var options = Options.Create(optionsValue);
        var admin = new SqlOSAdminService(context, options, TestCryptoService.Create(context, options));
        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Seed org", "seed-org"));
        var dashboardOrganization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Dashboard org", "dashboard-org"));
        var dashboard = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            dashboardOrganization.Id, "Dashboard SSO", "urn:dashboard:idp", "https://dashboard-idp.example.test/sso",
            certificate, true, false, "email", "first_name", "last_name"));
        optionsValue.SeedSamlConnection("workforce", seed =>
        {
            seed.OrganizationSlug = organization.Slug;
            seed.DisplayName = "Workforce SSO";
            seed.IdentityProviderEntityId = "urn:workforce:idp";
            seed.SingleSignOnUrl = "https://idp.example.test/sso";
            seed.X509CertificatePem = certificate;
            seed.PrimaryDomain = "example.test";
        });

        await admin.UpsertSeededSamlConnectionsAsync();
        await admin.UpsertSeededSamlConnectionsAsync();
        var seeded = await context.Set<SqlOSSsoConnection>().SingleAsync(x => x.ConfigurationSourceKey == "workforce");
        seeded.ConfigurationOwner.Should().Be(SqlOSConfigurationOwners.Code);
        seeded.DisplayName.Should().Be("Workforce SSO");
        organization.PrimaryDomain.Should().Be("example.test");
        (await context.Set<SqlOSSsoConnection>().CountAsync()).Should().Be(2);
        (await context.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "configuration.reconciled")).Should().Be(1);

        seeded.IsEnabled = false;
        optionsValue.SamlConnectionSeeds[0].DisplayName = "Renamed Workforce SSO";
        await context.SaveChangesAsync();
        await admin.UpsertSeededSamlConnectionsAsync();
        seeded.DisplayName.Should().Be("Renamed Workforce SSO");
        seeded.IsEnabled.Should().BeFalse("operator emergency disable survives restart");
        (await context.Set<SqlOSSsoConnection>().SingleAsync(x => x.Id == dashboard.Id)).DisplayName.Should().Be("Dashboard SSO");

        optionsValue.SamlConnectionSeeds.Clear();
        await admin.UpsertSeededSamlConnectionsAsync();
        seeded.ConfigurationOrphanedAt.Should().NotBeNull();
        seeded.IsEnabled.Should().BeFalse();
    }

    [TestMethod]
    public async Task Seed_DoesNotAdoptDashboardConnectionWithSameEntityId()
    {
        await using var context = CreateContext();
        var certificate = CreateCertificatePem();
        var optionsValue = new SqlOSAuthServerOptions();
        var options = Options.Create(optionsValue);
        var admin = new SqlOSAdminService(context, options, TestCryptoService.Create(context, options));
        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Org", "org"));
        await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            organization.Id, "Manual", "urn:collision:idp", "https://idp.example.test/sso", certificate,
            true, false, "email", "first_name", "last_name"));
        optionsValue.SeedSamlConnection("collision", seed =>
        {
            seed.OrganizationId = organization.Id;
            seed.DisplayName = "Seeded";
            seed.IdentityProviderEntityId = "urn:collision:idp";
            seed.SingleSignOnUrl = "https://idp.example.test/sso";
            seed.X509CertificatePem = certificate;
        });

        await FluentActions.Invoking(() => admin.UpsertSeededSamlConnectionsAsync())
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*existing 'dashboard' connection*");
        (await context.Set<SqlOSSsoConnection>().SingleAsync()).ConfigurationOwner.Should().Be(SqlOSConfigurationOwners.Dashboard);
    }

    [TestMethod]
    public async Task Seed_UpdateCannotCollideWithAnotherEntityIdOrMoveOrganizations()
    {
        await using var context = CreateContext();
        var certificate = CreateCertificatePem();
        var optionsValue = new SqlOSAuthServerOptions();
        var options = Options.Create(optionsValue);
        var admin = new SqlOSAdminService(context, options, TestCryptoService.Create(context, options));
        var firstOrganization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("First", "first"));
        var secondOrganization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Second", "second"));
        optionsValue.SeedSamlConnection("first", seed =>
        {
            seed.OrganizationId = firstOrganization.Id;
            seed.DisplayName = "First SSO";
            seed.IdentityProviderEntityId = "urn:first:idp";
            seed.SingleSignOnUrl = "https://first.example.test/sso";
            seed.X509CertificatePem = certificate;
        });
        await admin.UpsertSeededSamlConnectionsAsync();
        await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            secondOrganization.Id, "Second SSO", "urn:second:idp", "https://second.example.test/sso", certificate,
            true, false, "email", "first_name", "last_name"));

        optionsValue.SamlConnectionSeeds[0].IdentityProviderEntityId = "urn:second:idp";
        await FluentActions.Invoking(() => admin.UpsertSeededSamlConnectionsAsync())
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*same IdP entity ID*");

        optionsValue.SamlConnectionSeeds[0].IdentityProviderEntityId = "urn:first:idp";
        optionsValue.SamlConnectionSeeds[0].OrganizationId = secondOrganization.Id;
        await FluentActions.Invoking(() => admin.UpsertSeededSamlConnectionsAsync())
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot be moved*");

        var original = await context.Set<SqlOSSsoConnection>().SingleAsync(x => x.ConfigurationSourceKey == "first");
        original.OrganizationId.Should().Be(firstOrganization.Id);
        original.IdentityProviderEntityId.Should().Be("urn:first:idp");
    }

    [TestMethod]
    public async Task Seed_RejectsExpiredCertificateAndMixedMetadataModes()
    {
        await using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        var options = Options.Create(optionsValue);
        var admin = new SqlOSAdminService(context, options, TestCryptoService.Create(context, options));
        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Org", "org"));
        optionsValue.SeedSamlConnection("expired", seed =>
        {
            seed.OrganizationId = organization.Id;
            seed.DisplayName = "Expired";
            seed.IdentityProviderEntityId = "urn:expired:idp";
            seed.SingleSignOnUrl = "https://idp.example.test/sso";
            seed.X509CertificatePem = CreateCertificatePem(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1));
        });

        await FluentActions.Invoking(() => admin.UpsertSeededSamlConnectionsAsync())
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*expired*");
    }

    private static string CreateCertificatePem(
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSSamlSeed", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(30));
        return certificate.ExportCertificatePem();
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
        => new(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}

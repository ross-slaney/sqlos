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
public sealed class SqlOSDiscoveryAndSettingsTests
{
    [TestMethod]
    public async Task DiscoverAsync_ReturnsSso_WhenMatchingPrimaryDomainHasEnabledConnection()
    {
        await using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var discovery = new SqlOSHomeRealmDiscoveryService(context);

        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Contoso", null, "contoso.com"));
        await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            organization.Id,
            "Contoso SSO",
            "urn:test:idp",
            "https://idp.example.test/sso",
            CreateCertificatePem(),
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
    public async Task DiscoverAsync_ReturnsSso_ForExistingVerifiedMemberWhenRequireSsoIsEnabled()
    {
        await using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var discovery = new SqlOSHomeRealmDiscoveryService(context);

        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Member Org", null, "member.test"));
        var user = await CreateVerifiedUserAsync(context, admin, "Member User", "user@member.test");
        context.Set<SqlOSMembership>().Add(new SqlOSMembership
        {
            OrganizationId = organization.Id,
            UserId = user.Id,
            Role = "member",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            organization.Id,
            "Member SSO",
            "urn:member:idp",
            "https://idp.member.test/sso",
            CreateCertificatePem(),
            false,
            true,
            "email",
            "first_name",
            "last_name"));
        await context.SaveChangesAsync();

        var result = await discovery.DiscoverAsync(new SqlOSHomeRealmDiscoveryRequest("user@member.test"));

        result.Mode.Should().Be("sso");
        result.OrganizationId.Should().Be(organization.Id);
    }

    [TestMethod]
    public async Task DiscoverAsync_ReturnsPassword_ForMissingMemberWhenJitIsDisabled()
    {
        await using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var discovery = new SqlOSHomeRealmDiscoveryService(context);

        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("No JIT Org", null, "nojit.test"));
        await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            organization.Id,
            "No JIT SSO",
            "urn:nojit:idp",
            "https://idp.nojit.test/sso",
            CreateCertificatePem(),
            false,
            true,
            "email",
            "first_name",
            "last_name"));

        var result = await discovery.DiscoverAsync(new SqlOSHomeRealmDiscoveryRequest("new@nojit.test"));

        result.Mode.Should().Be("password");
        result.OrganizationId.Should().BeNull();
    }

    [TestMethod]
    public async Task DiscoverAsync_ReturnsPassword_ForExistingMemberWhenRequireSsoIsDisabledAndJitIsDisabled()
    {
        await using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var discovery = new SqlOSHomeRealmDiscoveryService(context);

        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Password Org", null, "password.test"));
        var user = await CreateVerifiedUserAsync(context, admin, "Password User", "user@password.test");
        context.Set<SqlOSMembership>().Add(new SqlOSMembership
        {
            OrganizationId = organization.Id,
            UserId = user.Id,
            Role = "member",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            organization.Id,
            "Password SSO",
            "urn:password:idp",
            "https://idp.password.test/sso",
            CreateCertificatePem(),
            false,
            false,
            "email",
            "first_name",
            "last_name"));
        await context.SaveChangesAsync();

        var result = await discovery.DiscoverAsync(new SqlOSHomeRealmDiscoveryRequest("user@password.test"));

        result.Mode.Should().Be("password");
        result.OrganizationId.Should().BeNull();
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
        var settingsService = new SqlOSSettingsService(context, options, new TestAuthEmailSender());

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

    [TestMethod]
    public async Task SettingsService_RejectsSigningKeyCleanupBeforeJwksGraceEnds()
    {
        await using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var settingsService = new SqlOSSettingsService(context, options, new TestAuthEmailSender());

        var act = async () => await settingsService.UpdateSecuritySettingsAsync(
            new SqlOSUpdateSecuritySettingsRequest(1440, 60, 2880, 90, 7, 6));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cleanup must not run before the JWKS grace window ends*");
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }

    private static async Task<SqlOSUser> CreateVerifiedUserAsync(
        TestSqlOSInMemoryDbContext context,
        SqlOSAdminService admin,
        string displayName,
        string email)
    {
        var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest(displayName, email, "P@ssword123!"));
        var userEmail = await context.Set<SqlOSUserEmail>().SingleAsync(x => x.UserId == user.Id);
        userEmail.IsVerified = true;
        userEmail.VerifiedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
        return user;
    }

    private static string CreateCertificatePem()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSDiscoveryIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return certificate.ExportCertificatePem();
    }
}

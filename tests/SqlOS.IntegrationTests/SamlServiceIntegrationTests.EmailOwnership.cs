using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// Email ownership at the SAML boundary: look-alike assertions never select another account
/// (#422), a SAML first link claims an unverified email only after evicting the credentials
/// attached before verification (#423), and just-in-time provisioning only creates emails inside
/// the connection organization's verified domains (#420).
/// </summary>
public sealed partial class SamlServiceIntegrationTests
{
    private const string SquatterPassword = "Squatter-P@ssword123!";

    [TestMethod]
    public async Task SamlAssertion_ForLookAlikeDomain_DoesNotLinkTheVictim()
    {
        var (_, admin, saml) = CreateSamlServices();
        var unique = Guid.NewGuid().ToString("N")[..12];
        var victimEmail = $"bob{unique}@business.example";
        var lookAlike = $"bob{unique}@busineß.example";
        var victim = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Victim", victimEmail, "P@ssword123!"));
        await MarkEmailVerifiedAsync(victim.Id);
        // The tenant legitimately owns the internationalized look-alike domain.
        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Look-alike Org {unique}", null, PrimaryDomain: "busineß.example"));
        await AddVerifiedDomainAsync(organization.Id, "xn--busine-gta.example");
        using var certificate = CreateCertificate("CN=SqlOSLookAlikeIdP");
        var connection = await CreatePolicySamlConnectionAsync(admin, organization.Id, certificate, "lookalike", autoProvisionUsers: true, autoLinkByEmail: true);
        var client = await CreateSamlClientAsync(admin, "lookalike");
        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);

        await CaptureAcsAsync(
            saml,
            connection.Id,
            BuildSignedSamlResponse(certificate, connection.IdentityProviderEntityId, lookAlike, "Look", "Alike", flow, nameId: $"lookalike-{unique}"),
            flow.RelayState);

        (await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>().AnyAsync(x => x.UserId == victim.Id)).Should().BeFalse();
        (await AspireFixture.SharedContext.Set<SqlOSMembership>()
                .AnyAsync(x => x.UserId == victim.Id && x.OrganizationId == organization.Id))
            .Should().BeFalse("a look-alike assertion must never be linked onto {0}", victimEmail);
    }

    [TestMethod]
    public async Task SamlFirstLink_ToSquattedUnverifiedEmail_EvictsPreVerificationCredentials()
    {
        var (_, admin, saml) = CreateSamlServices();
        var auth = CreateSharedAuthService(admin);
        var unique = Guid.NewGuid().ToString("N")[..12];
        var domain = $"claim{unique}.example";
        var victimEmail = $"victim@{domain}";
        var squatter = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Squatter", victimEmail, SquatterPassword));
        var squatterLogin = await auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(victimEmail, SquatterPassword, "test-client", null),
            new DefaultHttpContext());
        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Claim Org {unique}", null));
        await AddVerifiedDomainAsync(organization.Id, domain);
        using var certificate = CreateCertificate("CN=SqlOSClaimIdP");
        var connection = await CreatePolicySamlConnectionAsync(admin, organization.Id, certificate, "claim", autoProvisionUsers: true, autoLinkByEmail: true);
        var client = await CreateSamlClientAsync(admin, "claim");
        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);

        var redirect = await saml.HandleAcsAsync(
            connection.Id,
            BuildSignedSamlResponse(certificate, connection.IdentityProviderEntityId, victimEmail, "Real", "Owner", flow, nameId: $"owner-{unique}"),
            flow.RelayState,
            default);

        redirect.Should().StartWith("https://client.example.local/callback?code=");
        var squatterPasswordLogin = async () => await auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(victimEmail, SquatterPassword, "test-client", null),
            new DefaultHttpContext());
        await squatterPasswordLogin.Should().ThrowAsync<InvalidOperationException>(
            "the SAML first link proves the mailbox and must evict the password set before verification");
        var squatterRefresh = async () => await auth.RefreshAsync(new SqlOSRefreshRequest(squatterLogin.Tokens!.RefreshToken, null));
        await squatterRefresh.Should().ThrowAsync<InvalidOperationException>();
        var email = await AspireFixture.SharedContext.Set<SqlOSUserEmail>().AsNoTracking().SingleAsync(x => x.UserId == squatter.Id);
        email.IsVerified.Should().BeTrue();
        (await AspireFixture.SharedContext.Set<SqlOSAuditEvent>()
                .AnyAsync(x => x.EventType == "user.email.claimed" && x.UserId == squatter.Id))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task SamlJit_WithEmailOutsideVerifiedDomains_IsDeniedWithoutSideEffects()
    {
        var (_, admin, saml) = CreateSamlServices();
        var unique = Guid.NewGuid().ToString("N")[..12];
        var outsideEmail = $"victim{unique}@gmail.example";
        var subject = $"jit-outside-{unique}";
        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Jit Org {unique}", null));
        await AddVerifiedDomainAsync(organization.Id, $"tenant{unique}.example");
        await AssertJitDeniedAsync(admin, saml, organization.Id, outsideEmail, subject);
    }

    [TestMethod]
    public async Task SamlJit_WithOnlyUnverifiedPrimaryDomain_IsDenied()
    {
        var (_, admin, saml) = CreateSamlServices();
        var unique = Guid.NewGuid().ToString("N")[..12];
        var domain = $"primary{unique}.example";
        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Primary Org {unique}", null, PrimaryDomain: domain));
        await AssertJitDeniedAsync(admin, saml, organization.Id, $"user@{domain}", $"jit-primary-{unique}");
    }

    [TestMethod]
    public async Task SamlJit_WithEmailInsideVerifiedDomain_ProvisionsAndIssuesCode()
    {
        var (_, admin, saml) = CreateSamlServices();
        var unique = Guid.NewGuid().ToString("N")[..12];
        var domain = $"jit{unique}.example";
        var email = $"new.hire@{domain}";
        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Jit Inside {unique}", null));
        await AddVerifiedDomainAsync(organization.Id, domain);
        using var certificate = CreateCertificate("CN=SqlOSJitIdP");
        var connection = await CreatePolicySamlConnectionAsync(admin, organization.Id, certificate, "jit-inside", autoProvisionUsers: true, autoLinkByEmail: false);
        var client = await CreateSamlClientAsync(admin, "jitin");
        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);

        var redirect = await saml.HandleAcsAsync(
            connection.Id,
            BuildSignedSamlResponse(certificate, connection.IdentityProviderEntityId, email, "New", "Hire", flow, nameId: $"jit-inside-{unique}"),
            flow.RelayState,
            default);

        redirect.Should().StartWith("https://client.example.local/callback?code=");
        var stored = await AspireFixture.SharedContext.Set<SqlOSUserEmail>()
            .AsNoTracking()
            .SingleAsync(x => x.NormalizedEmail == SqlOSAdminService.NormalizeEmail(email));
        stored.IsVerified.Should().BeTrue();
        (await AspireFixture.SharedContext.Set<SqlOSMembership>()
                .AnyAsync(x => x.UserId == stored.UserId && x.OrganizationId == organization.Id && x.IsActive))
            .Should().BeTrue();
        (await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
                .AnyAsync(x => x.UserId == stored.UserId && x.SsoConnectionId == connection.Id))
            .Should().BeTrue();
    }

    private static async Task AssertJitDeniedAsync(
        SqlOSAdminService admin,
        SqlOSSamlService saml,
        string organizationId,
        string email,
        string subject)
    {
        using var certificate = CreateCertificate("CN=SqlOSJitDeniedIdP");
        var connection = await CreatePolicySamlConnectionAsync(admin, organizationId, certificate, "jit-denied", autoProvisionUsers: true, autoLinkByEmail: true);
        var client = await CreateSamlClientAsync(admin, "jitdeny");
        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);

        var action = async () => await saml.HandleAcsAsync(
            connection.Id,
            BuildSignedSamlResponse(certificate, connection.IdentityProviderEntityId, email, "Outside", "Domain", flow, nameId: subject),
            flow.RelayState,
            default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No user could be resolved from the SAML assertion.");
        var context = AspireFixture.SharedContext;
        (await context.Set<SqlOSUserEmail>().AnyAsync(x => x.NormalizedEmail == SqlOSAdminService.NormalizeEmail(email)))
            .Should().BeFalse("JIT must not create an email outside the organization's verified domains");
        (await context.Set<SqlOSExternalIdentity>().AnyAsync(x => x.SsoConnectionId == connection.Id))
            .Should().BeFalse();
        (await context.Set<SqlOSMembership>().AnyAsync(x => x.OrganizationId == organizationId))
            .Should().BeFalse();
        var authorizationRequest = await context.Set<SqlOSAuthorizationRequest>().AsNoTracking().SingleAsync(x => x.Id == flow.RelayState);
        authorizationRequest.CompletedAt.Should().BeNull();
        (await context.Set<SqlOSAuthorizationCode>().AnyAsync(x => x.AuthorizationRequestId == flow.RelayState))
            .Should().BeFalse();
        var denial = await context.Set<SqlOSAuditEvent>()
            .Where(x => x.EventType == "user.login.saml.link_denied" && x.OrganizationId == organizationId)
            .OrderByDescending(x => x.OccurredAt)
            .FirstOrDefaultAsync();
        denial.Should().NotBeNull();
        denial!.DataJson.Should().Contain("untrusted_email_domain");
        denial.DataJson.Should().NotContain(email);
    }

    private static async Task AddVerifiedDomainAsync(string organizationId, string domain)
    {
        AspireFixture.SharedContext.Set<SqlOSOrganizationDomain>().Add(VerifiedDomainClaim(organizationId, domain));
        await AspireFixture.SharedContext.SaveChangesAsync();
    }

    /// <summary>A verified domain nobody else uses, so shared-database discovery is unaffected.</summary>
    private static async Task<string> AddUniqueVerifiedDomainAsync(string organizationId)
    {
        var domain = $"jit-{Guid.NewGuid():N}.example";
        await AddVerifiedDomainAsync(organizationId, domain);
        return domain;
    }

    private static SqlOSOrganizationDomain VerifiedDomainClaim(string organizationId, string domain)
        => new()
        {
            Id = $"dom_{Guid.NewGuid():N}"[..28],
            OrganizationId = organizationId,
            Domain = domain,
            Status = SqlOSOrganizationDomainStatuses.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            VerifiedAt = DateTime.UtcNow
        };

    private static X509Certificate2 CreateCertificate(string subjectName)
    {
        // The key stays alive with the certificate for signing later in the test.
        var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static SqlOSAuthService CreateSharedAuthService(SqlOSAdminService admin)
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(AspireFixture.SharedContext, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(AspireFixture.SharedContext, admin, crypto, settings, emailSender, options);
        return new SqlOSAuthService(AspireFixture.SharedContext, options, admin, crypto, settings, emailOtp);
    }
}

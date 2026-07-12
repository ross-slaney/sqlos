using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Security;
using System.Text;
using System.IO.Compression;
using System.Xml;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class SamlServiceIntegrationTests
{
    [TestMethod]
    public async Task SignedSamlResponse_ProducesExchangeableAuthCode()
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var saml = new SqlOSSamlService(AspireFixture.SharedContext, options, admin, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(AspireFixture.SharedContext, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(AspireFixture.SharedContext, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(AspireFixture.SharedContext, options, admin, crypto, settings, emailOtp);
        var discovery = new SqlOSHomeRealmDiscoveryService(AspireFixture.SharedContext);
        var ssoAuth = new SqlOSSsoAuthorizationService(AspireFixture.SharedContext, admin, crypto, discovery, saml, auth);

        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"SAML {Guid.NewGuid():N}", null));
        var client = await admin.CreateClientAsync(new SqlOSCreateClientRequest(
            $"saml-client-{Guid.NewGuid():N}"[..18],
            "SAML Client",
            "sqlos-tests",
            new List<string> { "https://client.example.local/callback" }));

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSTestIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "Test SSO",
            "urn:test:idp",
            "https://idp.example.test/sso",
            cert.ExportCertificatePem(),
            true,
            false,
            "email",
            "first_name",
            "last_name"));

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(cert, "urn:test:idp", "user@example.com", "Saml", "User", flow);
        var redirectUrl = await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);
        redirectUrl.Should().StartWith("https://client.example.local/callback?code=");

        var code = QueryHelpers.ParseQuery(new Uri(redirectUrl).Query)["code"].ToString();
        code.Should().NotBeNull();

        var tokens = await ssoAuth.ExchangeCodeAsync(
            new SqlOSPkceExchangeRequest(
                code!,
                client.ClientId,
                "https://client.example.local/callback",
                flow.CodeVerifier!),
            new DefaultHttpContext());
        tokens.OrganizationId.Should().Be(org.Id);
        tokens.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task PkceSamlAuthorizationFlow_CanExchangeCode()
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(AspireFixture.SharedContext, options, emailSender);
        var saml = new SqlOSSamlService(AspireFixture.SharedContext, options, admin, crypto);
        var emailOtp = new SqlOSEmailOtpService(AspireFixture.SharedContext, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(AspireFixture.SharedContext, options, admin, crypto, settings, emailOtp);
        var discovery = new SqlOSHomeRealmDiscoveryService(AspireFixture.SharedContext);
        var ssoAuth = new SqlOSSsoAuthorizationService(AspireFixture.SharedContext, admin, crypto, discovery, saml, auth);

        var domain = $"contoso-{Guid.NewGuid():N}".ToLowerInvariant()[..20] + ".com";
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"PKCE {Guid.NewGuid():N}", null, domain));
        var client = await admin.CreateClientAsync(new SqlOSCreateClientRequest(
            $"pkce-client-{Guid.NewGuid():N}"[..20],
            "PKCE Client",
            "sqlos-tests",
            new List<string> { "https://client.example.local/auth/callback" }));

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSPkceIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "PKCE SSO",
            "urn:pkce:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            true,
            false,
            "email",
            "first_name",
            "last_name"));

        var codeVerifier = crypto.GenerateOpaqueToken();
        var state = crypto.GenerateOpaqueToken();
        var authorizationRequestCount = await AspireFixture.SharedContext.Set<SqlOSAuthorizationRequest>().CountAsync();
        var missingPkce = async () => await ssoAuth.StartAuthorizationAsync(new SqlOSSsoAuthorizationStartRequest(
            $"user@{domain}",
            client.ClientId,
            "https://client.example.local/auth/callback",
            state,
            string.Empty,
            "S256"));
        await missingPkce.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires an S256 PKCE code challenge*");

        var downgradedPkce = async () => await ssoAuth.StartAuthorizationAsync(new SqlOSSsoAuthorizationStartRequest(
            $"user@{domain}",
            client.ClientId,
            "https://client.example.local/auth/callback",
            state,
            crypto.CreatePkceCodeChallenge(codeVerifier),
            "plain"));
        await downgradedPkce.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires an S256 PKCE code challenge*");
        (await AspireFixture.SharedContext.Set<SqlOSAuthorizationRequest>().CountAsync())
            .Should().Be(authorizationRequestCount,
                "invalid PKCE requests must be rejected before transaction state is persisted");

        var start = await ssoAuth.StartAuthorizationAsync(new SqlOSSsoAuthorizationStartRequest(
            $"user@{domain}",
            client.ClientId,
            "https://client.example.local/auth/callback",
            state,
            crypto.CreatePkceCodeChallenge(codeVerifier),
            "S256"));

        start.AuthorizationUrl.Should().Contain("SAMLRequest=");
        var flow = ParseSamlFlow(start.AuthorizationUrl);
        flow.RelayState.Should().NotBeNullOrWhiteSpace();

        var samlResponse = BuildSignedSamlResponse(certificate, "urn:pkce:idp", $"user@{domain}", "Pkce", "User", flow);
        var redirectUrl = await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);
        redirectUrl.Should().Contain("state=");

        var query = QueryHelpers.ParseQuery(new Uri(redirectUrl).Query);
        var code = query["code"].ToString();
        query["state"].ToString().Should().Be(state);

        var missingVerifier = async () => await ssoAuth.ExchangeCodeAsync(
            new SqlOSPkceExchangeRequest(code!, client.ClientId, "https://client.example.local/auth/callback", string.Empty),
            new DefaultHttpContext());
        await missingVerifier.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("PKCE verification failed.");

        var wrongVerifier = async () => await ssoAuth.ExchangeCodeAsync(
            new SqlOSPkceExchangeRequest(
                code!,
                client.ClientId,
                "https://client.example.local/auth/callback",
                crypto.GenerateOpaqueToken()),
            new DefaultHttpContext());
        await wrongVerifier.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("PKCE verification failed.");

        var wrongRedirect = async () => await ssoAuth.ExchangeCodeAsync(
            new SqlOSPkceExchangeRequest(code!, client.ClientId, "https://attacker.example.test/callback", codeVerifier),
            new DefaultHttpContext());
        await wrongRedirect.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Redirect URI does not match the authorization request.");

        var tokens = await ssoAuth.ExchangeCodeAsync(
            new SqlOSPkceExchangeRequest(code!, client.ClientId, "https://client.example.local/auth/callback", codeVerifier),
            new DefaultHttpContext());

        tokens.OrganizationId.Should().Be(org.Id);
        tokens.AccessToken.Should().NotBeNullOrWhiteSpace();

        var interceptedReplay = async () => await ssoAuth.ExchangeCodeAsync(
            new SqlOSPkceExchangeRequest(code!, client.ClientId, "https://client.example.local/auth/callback", codeVerifier),
            new DefaultHttpContext());
        await interceptedReplay.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Authorization code is no longer valid.");
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithExistingEmail_ReusesUserWhenAutoProvisioning()
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var saml = new SqlOSSamlService(AspireFixture.SharedContext, options, admin, crypto);

        var email = $"existing-saml-{Guid.NewGuid():N}@example.com";
        var existingUser = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Existing SAML User", email, "P@ssword123!"));
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Existing SAML {Guid.NewGuid():N}", null));
        var client = await admin.CreateClientAsync(new SqlOSCreateClientRequest(
            $"existing-saml-{Guid.NewGuid():N}"[..20],
            "Existing SAML Client",
            "sqlos-tests",
            new List<string> { "https://client.example.local/callback" }));

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSExistingSamlIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "Existing User SSO",
            "urn:existing:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            true,
            false,
            "email",
            "first_name",
            "last_name"));

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(certificate, "urn:existing:idp", email, "Existing", "User", flow);
        var redirectUrl = await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);
        redirectUrl.Should().StartWith("https://client.example.local/callback?code=");

        var normalizedEmail = SqlOSAdminService.NormalizeEmail(email);
        var matchingEmails = await AspireFixture.SharedContext.Set<SqlOSUserEmail>()
            .Where(x => x.NormalizedEmail == normalizedEmail)
            .ToListAsync();
        matchingEmails.Should().ContainSingle();
        matchingEmails.Single().UserId.Should().Be(existingUser.Id);

        var externalIdentity = await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
            .SingleAsync(x => x.SsoConnectionId == connection.Id && x.Subject == email);
        externalIdentity.UserId.Should().Be(existingUser.Id);

        (await AspireFixture.SharedContext.Set<SqlOSMembership>()
            .AnyAsync(x => x.OrganizationId == org.Id && x.UserId == existingUser.Id && x.IsActive))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithExistingOrgMemberAndRequireSso_LinksExternalIdentityWithoutCreatingUser()
    {
        var (_, admin, saml) = CreateSamlServices();
        var email = $"existing-member-{Guid.NewGuid():N}@example.com";
        var existingUser = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Existing Member", email, "P@ssword123!"));
        await MarkEmailVerifiedAsync(existingUser.Id);
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Existing Member {Guid.NewGuid():N}", null));
        AspireFixture.SharedContext.Set<SqlOSMembership>().Add(new SqlOSMembership
        {
            OrganizationId = org.Id,
            UserId = existingUser.Id,
            Role = "member",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await AspireFixture.SharedContext.SaveChangesAsync();
        var client = await CreateSamlClientAsync(admin, "existing-member");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSExistingMemberSamlIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "Existing Member SSO",
            "urn:existing-member:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            false,
            true,
            "email",
            "first_name",
            "last_name"));

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(certificate, "urn:existing-member:idp", email, "Existing", "Member", flow);
        var redirectUrl = await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        redirectUrl.Should().StartWith("https://client.example.local/callback?code=");
        var normalizedEmail = SqlOSAdminService.NormalizeEmail(email);
        (await AspireFixture.SharedContext.Set<SqlOSUserEmail>().CountAsync(x => x.NormalizedEmail == normalizedEmail))
            .Should().Be(1);
        (await AspireFixture.SharedContext.Set<SqlOSMembership>().CountAsync(x => x.OrganizationId == org.Id && x.UserId == existingUser.Id))
            .Should().Be(1);
        var externalIdentity = await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
            .SingleAsync(x => x.SsoConnectionId == connection.Id && x.Subject == email);
        externalIdentity.UserId.Should().Be(existingUser.Id);
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithExistingNonMemberAndJitOff_IsDenied()
    {
        var (_, admin, saml) = CreateSamlServices();
        var email = $"existing-nonmember-{Guid.NewGuid():N}@example.com";
        var existingUser = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Existing Nonmember", email, "P@ssword123!"));
        await MarkEmailVerifiedAsync(existingUser.Id);
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Existing Nonmember {Guid.NewGuid():N}", null));
        var client = await CreateSamlClientAsync(admin, "existing-nonmember");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSExistingNonmemberSamlIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "Existing Nonmember SSO",
            "urn:existing-nonmember:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            false,
            true,
            "email",
            "first_name",
            "last_name"));

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(certificate, "urn:existing-nonmember:idp", email, "Existing", "Nonmember", flow);
        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No user could be resolved from the SAML assertion.");
        (await AspireFixture.SharedContext.Set<SqlOSMembership>().AnyAsync(x => x.OrganizationId == org.Id && x.UserId == existingUser.Id))
            .Should().BeFalse();
        (await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>().AnyAsync(x => x.SsoConnectionId == connection.Id && x.Subject == email))
            .Should().BeFalse();
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithMissingUserAndJitOff_IsDenied()
    {
        var (_, admin, saml) = CreateSamlServices();
        var email = $"missing-jit-off-{Guid.NewGuid():N}@example.com";
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Missing JIT Off {Guid.NewGuid():N}", null));
        var client = await CreateSamlClientAsync(admin, "missing-jit-off");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSMissingJitOffSamlIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "Missing JIT Off SSO",
            "urn:missing-jit-off:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            false,
            true,
            "email",
            "first_name",
            "last_name"));

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(certificate, "urn:missing-jit-off:idp", email, "Missing", "User", flow);
        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No user could be resolved from the SAML assertion.");
        var normalizedEmail = SqlOSAdminService.NormalizeEmail(email);
        (await AspireFixture.SharedContext.Set<SqlOSUserEmail>().AnyAsync(x => x.NormalizedEmail == normalizedEmail))
            .Should().BeFalse();
    }

    [TestMethod]
    public async Task AuthorizationUrl_UsesRedirectBindingDeflateEncoding()
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var saml = new SqlOSSamlService(AspireFixture.SharedContext, options, admin, crypto);

        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Redirect {Guid.NewGuid():N}", null));
        var client = await admin.CreateClientAsync(new SqlOSCreateClientRequest(
            $"redir-client-{Guid.NewGuid():N}"[..20],
            "Redirect Client",
            "sqlos-tests",
            new List<string> { "https://client.example.local/callback" }));

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSRedirectIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "Redirect SSO",
            "urn:redirect:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            true,
            false,
            "email",
            "first_name",
            "last_name"));

        var codeVerifier = crypto.GenerateOpaqueToken();
        var loginUrl = await saml.CreateAuthorizationUrlAsync(new SqlOSAuthorizationUrlRequest(
            connection.Id,
            client.ClientId,
            "https://client.example.local/callback",
            crypto.GenerateOpaqueToken(),
            crypto.CreatePkceCodeChallenge(codeVerifier),
            "S256"));

        var samlRequest = QueryHelpers.ParseQuery(new Uri(loginUrl).Query)["SAMLRequest"].ToString();
        samlRequest.Should().NotBeNullOrWhiteSpace();

        var xml = InflateSamlRequest(samlRequest!);
        xml.Should().Contain("<samlp:AuthnRequest");
        xml.Should().Contain("AssertionConsumerServiceURL=");
        xml.Should().Contain(connection.SingleSignOnUrl);
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithExtraUnsignedAssertion_IsRejected()
    {
        var (_, admin, saml) = CreateSamlServices();
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Wrapping {Guid.NewGuid():N}", null));
        var client = await CreateSamlClientAsync(admin, "wrapping");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSWrappingSamlIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "Wrapping SSO",
            "urn:wrapping:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            true,
            false,
            "email",
            "first_name",
            "last_name"));

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(
            certificate,
            "urn:wrapping:idp",
            "legitimate@example.com",
            "Legitimate",
            "User",
            flow,
            signAssertion: true,
            addExtraUnsignedAssertion: true);
        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("SAML response must contain exactly one assertion.");
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithWrongAudience_IsRejected()
    {
        var (_, admin, saml) = CreateSamlServices();
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Audience {Guid.NewGuid():N}", null));
        var client = await CreateSamlClientAsync(admin, "audience");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSAudienceSamlIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "Audience SSO",
            "urn:audience:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            true,
            false,
            "email",
            "first_name",
            "last_name"));

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(certificate, "urn:audience:idp", "user@example.com", "Audience", "User", flow, audience: "urn:wrong:audience");
        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("SAML assertion audience mismatch.");
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithWrongInResponseTo_IsRejected()
    {
        var (_, admin, saml) = CreateSamlServices();
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"InResponseTo {Guid.NewGuid():N}", null));
        var client = await CreateSamlClientAsync(admin, "inresponse");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSInResponseSamlIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "InResponseTo SSO",
            "urn:inresponse:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            true,
            false,
            "email",
            "first_name",
            "last_name"));

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(certificate, "urn:inresponse:idp", "user@example.com", "Response", "User", flow, inResponseTo: "_wrong");
        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("SAML response InResponseTo mismatch.");
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithExpiredAssertion_IsRejected()
    {
        var (_, admin, saml) = CreateSamlServices();
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Expired {Guid.NewGuid():N}", null));
        var client = await CreateSamlClientAsync(admin, "expired");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSExpiredSamlIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "Expired SSO",
            "urn:expired:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            true,
            false,
            "email",
            "first_name",
            "last_name"));

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(
            certificate,
            "urn:expired:idp",
            "user@example.com",
            "Expired",
            "User",
            flow,
            notBefore: DateTime.UtcNow.AddMinutes(-20),
            notOnOrAfter: DateTime.UtcNow.AddMinutes(-10));
        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("SAML assertion has expired.");
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithSha1Signature_IsRejected()
    {
        var (_, admin, saml) = CreateSamlServices();
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Sha1 {Guid.NewGuid():N}", null));
        var client = await CreateSamlClientAsync(admin, "sha1");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSSha1SamlIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "Sha1 SSO",
            "urn:sha1:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            true,
            false,
            "email",
            "first_name",
            "last_name"));

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(
            certificate,
            "urn:sha1:idp",
            "user@example.com",
            "Sha",
            "One",
            flow,
            mutateAfterSigning: (document, _) =>
            {
                var signatureMethod = document.GetElementsByTagName("SignatureMethod", SignedXml.XmlDsigNamespaceUrl).OfType<XmlElement>().Single();
                signatureMethod.SetAttribute("Algorithm", SignedXml.XmlDsigRSASHA1Url);
            });
        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("SAML signature algorithm is not allowed.");
    }

    [TestMethod]
    public async Task SamlResponse_WithDtd_IsRejectedBeforeXmlEntityResolution()
    {
        var (_, admin, saml) = CreateSamlServices();
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Dtd {Guid.NewGuid():N}", null));
        var client = await CreateSamlClientAsync(admin, "dtd");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSDtdSamlIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "Dtd SSO",
            "urn:dtd:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            true,
            false,
            "email",
            "first_name",
            "last_name"));

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var xml = """
        <!DOCTYPE samlp:Response [
          <!ENTITY xxe SYSTEM "file:///etc/passwd">
        ]>
        <samlp:Response xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol" xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion" ID="_dtd" Version="2.0">
          <saml:Issuer>&xxe;</saml:Issuer>
        </samlp:Response>
        """;
        var samlResponse = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        await action.Should().ThrowAsync<XmlException>();
    }

    private static (SqlOSCryptoService Crypto, SqlOSAdminService Admin, SqlOSSamlService Saml) CreateSamlServices()
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var saml = new SqlOSSamlService(AspireFixture.SharedContext, options, admin, crypto);
        return (crypto, admin, saml);
    }

    private static async Task<SqlOSClientApplication> CreateSamlClientAsync(SqlOSAdminService admin, string prefix)
        => await admin.CreateClientAsync(new SqlOSCreateClientRequest(
            $"{prefix}-{Guid.NewGuid():N}"[..20],
            $"{prefix} client",
            "sqlos-tests",
            new List<string> { "https://client.example.local/callback" }));

    private static async Task<SamlFlow> StartSamlRequestAsync(SqlOSSamlService saml, string connectionId, string clientId)
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        var codeVerifier = crypto.GenerateOpaqueToken();
        var authUrl = await saml.CreateAuthorizationUrlAsync(new SqlOSAuthorizationUrlRequest(
            connectionId,
            clientId,
            "https://client.example.local/callback",
            crypto.GenerateOpaqueToken(),
            crypto.CreatePkceCodeChallenge(codeVerifier),
            "S256"));
        return ParseSamlFlow(authUrl) with { CodeVerifier = codeVerifier };
    }

    private static async Task MarkEmailVerifiedAsync(string userId)
    {
        var email = await AspireFixture.SharedContext.Set<SqlOSUserEmail>().SingleAsync(x => x.UserId == userId);
        email.IsVerified = true;
        email.VerifiedAt = DateTime.UtcNow;
        await AspireFixture.SharedContext.SaveChangesAsync();
    }

    private static string BuildSignedSamlResponse(
        X509Certificate2 certificate,
        string issuer,
        string email,
        string firstName,
        string lastName,
        SamlFlow flow,
        bool signAssertion = false,
        bool includeConditions = true,
        bool addExtraUnsignedAssertion = false,
        string? audience = null,
        string? recipient = null,
        string? inResponseTo = null,
        DateTime? notBefore = null,
        DateTime? notOnOrAfter = null,
        Action<XmlDocument, XmlElement, XmlElement>? mutateBeforeSigning = null,
        Action<XmlDocument, XmlElement>? mutateAfterSigning = null)
    {
        var responseId = $"_{Guid.NewGuid():N}";
        var assertionId = $"_{Guid.NewGuid():N}";
        var issueInstant = DateTime.UtcNow.ToString("o");
        var effectiveAudience = audience ?? AspireFixture.Options.Issuer;
        var effectiveRecipient = recipient ?? flow.AssertionConsumerServiceUrl;
        var effectiveInResponseTo = inResponseTo ?? flow.RequestId;
        var effectiveNotBefore = (notBefore ?? DateTime.UtcNow.AddMinutes(-1)).ToString("o");
        var effectiveNotOnOrAfter = (notOnOrAfter ?? DateTime.UtcNow.AddMinutes(5)).ToString("o");
        var conditionsXml = includeConditions
            ? $"""
                <saml:Conditions NotBefore="{effectiveNotBefore}" NotOnOrAfter="{effectiveNotOnOrAfter}">
                  <saml:AudienceRestriction><saml:Audience>{SecurityElement.Escape(effectiveAudience)}</saml:Audience></saml:AudienceRestriction>
                </saml:Conditions>
            """
            : string.Empty;
        var xml = $"""
        <samlp:Response xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol" xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion" ID="{responseId}" Version="2.0" IssueInstant="{issueInstant}" Destination="{effectiveRecipient}" InResponseTo="{effectiveInResponseTo}">
          <saml:Issuer>{SecurityElement.Escape(issuer)}</saml:Issuer>
          <samlp:Status><samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success" /></samlp:Status>
          <saml:Assertion ID="{assertionId}" Version="2.0" IssueInstant="{issueInstant}">
            <saml:Issuer>{SecurityElement.Escape(issuer)}</saml:Issuer>
            <saml:Subject>
              <saml:NameID>{SecurityElement.Escape(email)}</saml:NameID>
              <saml:SubjectConfirmation Method="urn:oasis:names:tc:SAML:2.0:cm:bearer">
                <saml:SubjectConfirmationData InResponseTo="{effectiveInResponseTo}" Recipient="{effectiveRecipient}" NotOnOrAfter="{effectiveNotOnOrAfter}" />
              </saml:SubjectConfirmation>
            </saml:Subject>
            {conditionsXml}
            <saml:AttributeStatement>
              <saml:Attribute Name="email"><saml:AttributeValue>{SecurityElement.Escape(email)}</saml:AttributeValue></saml:Attribute>
              <saml:Attribute Name="first_name"><saml:AttributeValue>{SecurityElement.Escape(firstName)}</saml:AttributeValue></saml:Attribute>
              <saml:Attribute Name="last_name"><saml:AttributeValue>{SecurityElement.Escape(lastName)}</saml:AttributeValue></saml:Attribute>
            </saml:AttributeStatement>
          </saml:Assertion>
        </samlp:Response>
        """;

        var xmlDoc = new XmlDocument { PreserveWhitespace = true };
        xmlDoc.LoadXml(xml);
        var responseElement = xmlDoc.DocumentElement!;
        var assertionElement = (XmlElement)responseElement.GetElementsByTagName("Assertion", "urn:oasis:names:tc:SAML:2.0:assertion")[0]!;
        if (addExtraUnsignedAssertion)
        {
            var extraAssertion = xmlDoc.CreateElement("saml", "Assertion", "urn:oasis:names:tc:SAML:2.0:assertion");
            extraAssertion.SetAttribute("ID", $"_{Guid.NewGuid():N}");
            extraAssertion.SetAttribute("Version", "2.0");
            extraAssertion.SetAttribute("IssueInstant", issueInstant);
            extraAssertion.InnerXml = $"""
              <saml:Issuer xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion">{SecurityElement.Escape(issuer)}</saml:Issuer>
              <saml:Subject xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion">
                <saml:NameID>attacker@example.com</saml:NameID>
              </saml:Subject>
            """;
            responseElement.InsertBefore(extraAssertion, assertionElement);
        }

        mutateBeforeSigning?.Invoke(xmlDoc, responseElement, assertionElement);
        var privateKey = certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("Test certificate does not contain an RSA private key.");
        var signedElement = signAssertion ? assertionElement : responseElement;
        var signedXml = new SignedXml(signedElement)
        {
            SigningKey = privateKey
        };
        signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signedXml.SignedInfo.SignatureMethod = SignedXml.XmlDsigRSASHA256Url;
        var reference = new Reference { Uri = $"#{signedElement.GetAttribute("ID")}", DigestMethod = SignedXml.XmlDsigSHA256Url };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signedXml.AddReference(reference);
        signedXml.KeyInfo = new KeyInfo();
        signedXml.KeyInfo.AddClause(new KeyInfoX509Data(certificate));
        signedXml.ComputeSignature();
        signedElement.InsertAfter(xmlDoc.ImportNode(signedXml.GetXml(), true), signedElement.FirstChild);
        mutateAfterSigning?.Invoke(xmlDoc, responseElement);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(xmlDoc.OuterXml));
    }

    private static SamlFlow ParseSamlFlow(string loginUrl)
    {
        var query = QueryHelpers.ParseQuery(new Uri(loginUrl).Query);
        var relayState = query["RelayState"].ToString();
        var samlRequest = query["SAMLRequest"].ToString();
        relayState.Should().NotBeNullOrWhiteSpace();
        samlRequest.Should().NotBeNullOrWhiteSpace();

        var xml = InflateSamlRequest(samlRequest!);
        var xmlDoc = new XmlDocument { XmlResolver = null };
        xmlDoc.LoadXml(xml);
        var root = xmlDoc.DocumentElement!;
        return new SamlFlow(
            relayState!,
            root.GetAttribute("ID"),
            root.GetAttribute("AssertionConsumerServiceURL"));
    }

    private static string InflateSamlRequest(string samlRequest)
    {
        var bytes = Convert.FromBase64String(samlRequest);
        using var compressed = new MemoryStream(bytes);
        using var inflater = new DeflateStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(inflater, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed record SamlFlow(
        string RelayState,
        string RequestId,
        string AssertionConsumerServiceUrl,
        string? CodeVerifier = null);
}

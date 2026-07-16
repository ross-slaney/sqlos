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
using Microsoft.Extensions.Logging.Abstractions;
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
    private const string TrustedSamlMfaContext =
        "urn:oasis:names:tc:SAML:2.0:ac:classes:TimeSyncToken";

    [TestMethod]
    public async Task SamlAuthnContext_WithoutTrustPolicy_DoesNotSatisfyMfa()
    {
        var (_, admin, saml) = CreateSamlServices();
        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest(
            $"SAML no trust {Guid.NewGuid():N}",
            null));
        var client = await CreateSamlClientAsync(admin, "mfa-no-trust");
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest(
            "CN=SqlOSSamlNoTrust",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreatePolicySamlConnectionAsync(
            admin,
            organization.Id,
            certificate,
            "mfa-no-trust",
            autoProvisionUsers: true,
            autoLinkByEmail: true);
        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var response = BuildSignedSamlResponse(
            certificate,
            connection.IdentityProviderEntityId,
            $"mfa-no-trust-{Guid.NewGuid():N}@example.com",
            "No",
            "Trust",
            flow,
            authnContextClassRef: TrustedSamlMfaContext);

        await saml.HandleAcsAsync(connection.Id, response, flow.RelayState, default);

        var request = await AspireFixture.SharedContext.Set<SqlOSAuthorizationRequest>()
            .AsNoTracking()
            .SingleAsync(item => item.Id == flow.RelayState);
        request.ResolvedAuthMethod.Should().Be("saml");
    }

    [TestMethod]
    public async Task SamlTrustedAuthnContext_SatisfiesMfaAndIsAudited()
    {
        var (_, admin, saml) = CreateSamlServices();
        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest(
            $"SAML trusted {Guid.NewGuid():N}",
            null));
        var client = await CreateSamlClientAsync(admin, "mfa-trusted");
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest(
            "CN=SqlOSSamlTrusted",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreatePolicySamlConnectionAsync(
            admin,
            organization.Id,
            certificate,
            "mfa-trusted",
            autoProvisionUsers: true,
            autoLinkByEmail: true,
            trustUpstreamMfa: true,
            acceptedAuthnContextClassRefs: [TrustedSamlMfaContext]);
        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var response = BuildSignedSamlResponse(
            certificate,
            connection.IdentityProviderEntityId,
            $"mfa-trusted-{Guid.NewGuid():N}@example.com",
            "Trusted",
            "MFA",
            flow,
            authnContextClassRef: TrustedSamlMfaContext);

        await saml.HandleAcsAsync(connection.Id, response, flow.RelayState, default);

        var request = await AspireFixture.SharedContext.Set<SqlOSAuthorizationRequest>()
            .AsNoTracking()
            .SingleAsync(item => item.Id == flow.RelayState);
        request.ResolvedAuthMethod.Should().Be("saml+upstream_mfa");
        var audit = await AspireFixture.SharedContext.Set<SqlOSAuditEvent>()
            .AsNoTracking()
            .OrderByDescending(item => item.OccurredAt)
            .FirstAsync(item => item.EventType == "user.login.saml.assurance"
                && item.DataJson != null
                && item.DataJson.Contains(connection.Id));
        audit.DataJson.Should().Contain("\"Accepted\":true");
        audit.DataJson.Should().Contain(TrustedSamlMfaContext);
    }

    [TestMethod]
    public async Task SamlTamperedAuthnContext_IsRejectedBeforeTrustEvaluation()
    {
        var (_, admin, saml) = CreateSamlServices();
        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest(
            $"SAML tampered {Guid.NewGuid():N}",
            null));
        var client = await CreateSamlClientAsync(admin, "mfa-tampered");
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest(
            "CN=SqlOSSamlTampered",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreatePolicySamlConnectionAsync(
            admin,
            organization.Id,
            certificate,
            "mfa-tampered",
            autoProvisionUsers: true,
            autoLinkByEmail: true,
            trustUpstreamMfa: true,
            acceptedAuthnContextClassRefs: [TrustedSamlMfaContext]);
        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var response = BuildSignedSamlResponse(
            certificate,
            connection.IdentityProviderEntityId,
            $"mfa-tampered-{Guid.NewGuid():N}@example.com",
            "Tampered",
            "MFA",
            flow,
            mutateAfterSigning: (_, responseElement) =>
            {
                var authnContext = responseElement
                    .GetElementsByTagName(
                        "AuthnContextClassRef",
                        "urn:oasis:names:tc:SAML:2.0:assertion")
                    .OfType<XmlElement>()
                    .Single();
                authnContext.InnerText = TrustedSamlMfaContext;
            },
            authnContextClassRef:
                "urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport");

        var action = () => saml.HandleAcsAsync(
            connection.Id,
            response,
            flow.RelayState,
            default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*signature*");
        (await AspireFixture.SharedContext.Set<SqlOSAuthorizationRequest>()
            .AsNoTracking()
            .SingleAsync(item => item.Id == flow.RelayState))
            .ResolvedAuthMethod.Should().BeNull();
    }

    [TestMethod]
    public async Task InactiveUser_OidcAndSamlLogin_IsRejected()
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var oidc = new SqlOSOidcAuthService(
            AspireFixture.SharedContext,
            admin,
            crypto,
            new FakeOidcProviderHttpClientFactory(),
            NullLogger<SqlOSOidcAuthService>.Instance);
        var saml = new SqlOSSamlService(AspireFixture.SharedContext, options, admin, crypto);
        var email = $"inactive-federated-{Guid.NewGuid():N}@example.com";
        var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Inactive Federated User",
            email,
            "P@ssword123!"));

        var oidcClient = await admin.CreateClientAsync(new SqlOSCreateClientRequest(
            $"oidc-inactive-{Guid.NewGuid():N}"[..22],
            "Inactive OIDC Client",
            "sqlos-tests",
            ["https://client.example.local/callback/google"]));
        var oidcConnection = await admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.Google,
            $"Google inactive {Guid.NewGuid():N}",
            "google-client",
            "google-secret",
            ["https://client.example.local/callback/google"],
            true,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null));
        var oidcRequest = new SqlOSCompleteOidcAuthorizationRequest(
            oidcConnection.Id,
            oidcClient.ClientId,
            "https://client.example.local/callback/google",
            $"success:{email}:nonce-inactive-federated",
            "verifier",
            "nonce-inactive-federated",
            null);
        (await oidc.CompleteAuthorizationAsync(oidcRequest)).UserId.Should().Be(user.Id);

        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest(
            $"Inactive SAML {Guid.NewGuid():N}",
            null));
        var samlClient = await CreateSamlClientAsync(admin, "inactive-user");
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest(
            "CN=SqlOSInactiveUserSamlIdP",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var certificate = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
        var samlConnection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            organization.Id,
            "Inactive User SSO",
            "urn:inactive-user:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            true,
            true,
            "email",
            "first_name",
            "last_name"));
        var firstSamlFlow = await StartSamlRequestAsync(saml, samlConnection.Id, samlClient.ClientId);
        var firstSamlResponse = BuildSignedSamlResponse(
            certificate,
            "urn:inactive-user:idp",
            email,
            "Inactive",
            "User",
            firstSamlFlow);
        (await saml.HandleAcsAsync(
            samlConnection.Id,
            firstSamlResponse,
            firstSamlFlow.RelayState,
            default)).Should().Contain("code=");

        await using (var offboardingContext = new TestSqlOSDbContext(
            new DbContextOptionsBuilder<TestSqlOSDbContext>()
                .UseSqlServer(AspireFixture.SqlConnectionString)
                .Options))
        {
            var offboardedUser = await offboardingContext.Set<SqlOSUser>()
                .SingleAsync(x => x.Id == user.Id);
            offboardedUser.IsActive = false;
            await offboardingContext.SaveChangesAsync();
        }

        var oidcAction = async () => await oidc.CompleteAuthorizationAsync(oidcRequest);
        await oidcAction.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Social sign-in could not be completed.");

        var secondSamlFlow = await StartSamlRequestAsync(saml, samlConnection.Id, samlClient.ClientId);
        var secondSamlResponse = BuildSignedSamlResponse(
            certificate,
            "urn:inactive-user:idp",
            email,
            "Inactive",
            "User",
            secondSamlFlow);
        var samlAction = async () => await saml.HandleAcsAsync(
            samlConnection.Id,
            secondSamlResponse,
            secondSamlFlow.RelayState,
            default);
        await samlAction.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No user could be resolved from the SAML assertion.");

        (await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
            .CountAsync(x => x.UserId == user.Id
                && (x.OidcConnectionId == oidcConnection.Id || x.SsoConnectionId == samlConnection.Id)))
            .Should().Be(2, "inactive callbacks must not create replacement identities");
    }

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

    [DataTestMethod]
    [DataRow(true, false, DisplayName = "Duplicate response ID is rejected")]
    [DataRow(false, true, DisplayName = "Duplicate assertion ID is rejected")]
    public async Task SignedSamlResponses_WithEitherDuplicateIdentifier_AreRejectedAcrossAuthorizationRequests(
        bool reuseResponseId,
        bool reuseAssertionId)
    {
        var (_, admin, saml) = CreateSamlServices();
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Replay {Guid.NewGuid():N}", null));
        var client = await CreateSamlClientAsync(admin, "replay");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSReplayIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id, "Replay SSO", $"urn:replay:{Guid.NewGuid():N}:idp", "https://idp.example.test/sso",
            certificate.ExportCertificatePem(), true, false, "email", "first_name", "last_name"));
        var responseId = $"_{Guid.NewGuid():N}";
        var assertionId = $"_{Guid.NewGuid():N}";

        var firstFlow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var firstResponse = BuildSignedSamlResponse(
            certificate, connection.IdentityProviderEntityId, $"replay-{Guid.NewGuid():N}@example.com", "Replay", "User",
            firstFlow, responseId: responseId, assertionId: assertionId);
        (await saml.HandleAcsAsync(connection.Id, firstResponse, firstFlow.RelayState)).Should().Contain("code=");

        var secondFlow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var duplicateResponse = BuildSignedSamlResponse(
            certificate, connection.IdentityProviderEntityId, $"replay-{Guid.NewGuid():N}@example.com", "Replay", "User",
            secondFlow,
            responseId: reuseResponseId ? responseId : $"_{Guid.NewGuid():N}",
            assertionId: reuseAssertionId ? assertionId : $"_{Guid.NewGuid():N}");
        var action = async () => await saml.HandleAcsAsync(connection.Id, duplicateResponse, secondFlow.RelayState);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("SAML response has already been consumed.");
    }

    [TestMethod]
    public async Task SignedSamlResponses_WithDuplicateIdentifiersConcurrently_AllowExactlyOne()
    {
        var (_, admin, saml) = CreateSamlServices();
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Concurrent replay {Guid.NewGuid():N}", null));
        var client = await CreateSamlClientAsync(admin, "concurrent-replay");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSConcurrentReplayIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id, "Concurrent Replay SSO", $"urn:concurrent-replay:{Guid.NewGuid():N}:idp", "https://idp.example.test/sso",
            certificate.ExportCertificatePem(), true, false, "email", "first_name", "last_name"));
        var firstFlow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var secondFlow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var responseId = $"_{Guid.NewGuid():N}";
        var assertionId = $"_{Guid.NewGuid():N}";
        var email = $"concurrent-replay-{Guid.NewGuid():N}@example.com";
        var firstResponse = BuildSignedSamlResponse(certificate, connection.IdentityProviderEntityId, email, "Replay", "User", firstFlow,
            responseId: responseId, assertionId: assertionId);
        var secondResponse = BuildSignedSamlResponse(certificate, connection.IdentityProviderEntityId, email, "Replay", "User", secondFlow,
            responseId: responseId, assertionId: assertionId);

        await using var firstContext = CreateIsolatedContext();
        await using var secondContext = CreateIsolatedContext();
        var firstService = CreateSamlService(firstContext);
        var secondService = CreateSamlService(secondContext);
        var results = await Task.WhenAll(
            CaptureAcsAsync(firstService, connection.Id, firstResponse, firstFlow.RelayState),
            CaptureAcsAsync(secondService, connection.Id, secondResponse, secondFlow.RelayState));

        results.Count(x => x.Redirect?.Contains("code=", StringComparison.Ordinal) == true).Should().Be(1);
        results.Count(x => x.Error?.Message == "SAML response has already been consumed.").Should().Be(1);
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithoutBearerConfirmationExpiry_IsRejected()
    {
        var (_, admin, saml) = CreateSamlServices();
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Missing confirmation expiry {Guid.NewGuid():N}", null));
        var client = await CreateSamlClientAsync(admin, "missing-expiry");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSMissingExpiryIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id, "Missing Expiry SSO", $"urn:missing-expiry:{Guid.NewGuid():N}:idp", "https://idp.example.test/sso",
            certificate.ExportCertificatePem(), true, false, "email", "first_name", "last_name"));
        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(
            certificate, connection.IdentityProviderEntityId, $"expiry-{Guid.NewGuid():N}@example.com", "Expiry", "User", flow,
            mutateBeforeSigning: (_, _, assertion) => assertion
                .GetElementsByTagName("SubjectConfirmationData", "urn:oasis:names:tc:SAML:2.0:assertion")
                .OfType<XmlElement>().Single().RemoveAttribute("NotOnOrAfter"));

        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState);
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("SAML subject confirmation mismatch.");
    }

    [TestMethod]
    public async Task LegacyTemporaryRelayState_CannotReachSamlCodeIssuance()
    {
        var (crypto, admin, saml) = CreateSamlServices();
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest(
            $"Legacy Relay {Guid.NewGuid():N}",
            null));
        var client = await CreateSamlClientAsync(admin, "legacy-relay");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=LegacyRelayIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            org.Id,
            "Legacy Relay SSO",
            "urn:legacy-relay:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            true,
            false,
            "email",
            "first_name",
            "last_name"));
        var relayState = await crypto.CreateTemporaryTokenAsync(
            "sso_request",
            null,
            client.Id,
            org.Id,
            new { clientId = client.ClientId, redirectUri = "https://client.example.local/callback" });

        var action = async () => await saml.HandleAcsAsync(
            connection.Id,
            "legacy-saml-response",
            relayState,
            new DefaultHttpContext());
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("SAML authorization request is invalid or expired.");

        (await AspireFixture.SharedContext.Set<SqlOSAuthorizationCode>()
            .CountAsync(x => x.ClientApplicationId == client.Id)).Should().Be(0);
        (await AspireFixture.SharedContext.Set<SqlOSSession>()
            .CountAsync(x => x.ClientApplicationId == client.Id)).Should().Be(0);
        var storedRelay = await crypto.FindTemporaryTokenAsync("sso_request", relayState);
        storedRelay.Should().NotBeNull();
        storedRelay!.ConsumedAt.Should().BeNull(
            "the retired temporary-token path must not even consume the legacy relay state");
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

        var invalidChallenge = async () => await ssoAuth.StartAuthorizationAsync(new SqlOSSsoAuthorizationStartRequest(
            $"user@{domain}",
            client.ClientId,
            "https://client.example.local/auth/callback",
            state,
            new string('A', 42),
            "S256"));
        await invalidChallenge.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*valid RFC 7636 S256 PKCE code challenge*");

        var caseVariantRedirect = async () => await ssoAuth.StartAuthorizationAsync(new SqlOSSsoAuthorizationStartRequest(
            $"user@{domain}",
            client.ClientId,
            "https://client.example.local/auth/Callback",
            state,
            crypto.CreatePkceCodeChallenge(codeVerifier),
            "S256"));
        await caseVariantRedirect.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Redirect URI*not allowed*");
        (await AspireFixture.SharedContext.Set<SqlOSAuthorizationRequest>().CountAsync())
            .Should().Be(authorizationRequestCount,
                "invalid PKCE and redirect requests must be rejected before transaction state is persisted");

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

        foreach (var invalidVerifier in new[] { new string('A', 42), new string('A', 129), new string('A', 42) + "!" })
        {
            var invalidVerifierExchange = async () => await ssoAuth.ExchangeCodeAsync(
                new SqlOSPkceExchangeRequest(
                    code!,
                    client.ClientId,
                    "https://client.example.local/auth/callback",
                    invalidVerifier),
                new DefaultHttpContext());
            await invalidVerifierExchange.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("PKCE verification failed.");
        }

        await using var attackerContext = CreateIsolatedContext();
        var attackerSso = BuildSsoAuthorizationService(attackerContext);
        var storedCode = await attackerContext.Set<SqlOSAuthorizationCode>()
            .SingleAsync(x => x.CodeHash == crypto.HashToken(code!));
        var sessionsBeforeAttack = await attackerContext.Set<SqlOSSession>()
            .CountAsync(x => x.UserId == storedCode.UserId
                && x.ClientApplicationId == storedCode.ClientApplicationId);
        var wrongVerifier = async () => await attackerSso.ExchangeCodeAsync(
            new SqlOSPkceExchangeRequest(
                code!,
                client.ClientId,
                "https://client.example.local/auth/callback",
                crypto.GenerateOpaqueToken()),
            new DefaultHttpContext());
        await wrongVerifier.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("PKCE verification failed.");
        storedCode.ConsumedAt.Should().BeNull();
        (await attackerContext.Set<SqlOSSession>()
            .CountAsync(x => x.UserId == storedCode.UserId
                && x.ClientApplicationId == storedCode.ClientApplicationId))
            .Should().Be(sessionsBeforeAttack,
                "an intercepted code with the wrong verifier cannot create a session");

        var wrongRedirect = async () => await ssoAuth.ExchangeCodeAsync(
            new SqlOSPkceExchangeRequest(code!, client.ClientId, "https://attacker.example.test/callback", codeVerifier),
            new DefaultHttpContext());
        await wrongRedirect.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Redirect URI does not match the authorization request.");

        await using var legitimateContext = CreateIsolatedContext();
        var legitimateSso = BuildSsoAuthorizationService(legitimateContext);
        var tokens = await legitimateSso.ExchangeCodeAsync(
            new SqlOSPkceExchangeRequest(code!, client.ClientId, "https://client.example.local/auth/callback", codeVerifier),
            new DefaultHttpContext());

        tokens.OrganizationId.Should().Be(org.Id);
        tokens.AccessToken.Should().NotBeNullOrWhiteSpace();

        await using var replayContext = CreateIsolatedContext();
        var replaySso = BuildSsoAuthorizationService(replayContext);
        var interceptedReplay = async () => await replaySso.ExchangeCodeAsync(
            new SqlOSPkceExchangeRequest(code!, client.ClientId, "https://client.example.local/auth/callback", codeVerifier),
            new DefaultHttpContext());
        await interceptedReplay.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Authorization code is no longer valid.");

        await using var verifyContext = CreateIsolatedContext();
        var verifiedCode = await verifyContext.Set<SqlOSAuthorizationCode>()
            .SingleAsync(x => x.CodeHash == crypto.HashToken(code!));
        verifiedCode.ConsumedAt.Should().NotBeNull();
        (await verifyContext.Set<SqlOSSession>()
            .CountAsync(x => x.UserId == verifiedCode.UserId
                && x.ClientApplicationId == verifiedCode.ClientApplicationId))
            .Should().Be(sessionsBeforeAttack + 1);
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
    public async Task SignedSamlResponse_WithActiveScimProvisioning_LinksExistingMemberWithoutBroadEmailLinking()
    {
        var (_, admin, saml) = CreateSamlServices();
        var email = $"scim-saml-{Guid.NewGuid():N}@example.com";
        var existingUser = await admin.CreateUserAsync(new SqlOSCreateUserRequest("SCIM SAML Member", email, "P@ssword123!"));
        await MarkEmailVerifiedAsync(existingUser.Id);
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"SCIM SAML {Guid.NewGuid():N}", null));
        await AddMembershipAsync(org.Id, existingUser.Id, isActive: true);
        await AddScimUserLinkAsync(admin, org.Id, existingUser.Id, email);
        var client = await CreateSamlClientAsync(admin, "scim-saml");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSScimSamlIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreateRestrictedSamlConnectionAsync(admin, org.Id, certificate, "scim-saml");

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(certificate, connection.IdentityProviderEntityId, email, "SCIM", "Member", flow);
        var redirectUrl = await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        redirectUrl.Should().StartWith("https://client.example.local/callback?code=");
        var externalIdentity = await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
            .SingleAsync(x => x.SsoConnectionId == connection.Id && x.Subject == email);
        externalIdentity.UserId.Should().Be(existingUser.Id);
        (await AspireFixture.SharedContext.Set<SqlOSUserEmail>()
                .CountAsync(x => x.NormalizedEmail == SqlOSAdminService.NormalizeEmail(email)))
            .Should().Be(1);
        (await AspireFixture.SharedContext.Set<SqlOSMembership>()
                .CountAsync(x => x.OrganizationId == org.Id && x.UserId == existingUser.Id))
            .Should().Be(1);
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithOnlyCrossOrganizationScimProvisioning_DoesNotAutoLink()
    {
        var (_, admin, saml) = CreateSamlServices();
        var email = $"cross-org-scim-saml-{Guid.NewGuid():N}@example.com";
        var existingUser = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Cross-org SCIM Member", email, "P@ssword123!"));
        await MarkEmailVerifiedAsync(existingUser.Id);
        var targetOrg = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Target SAML {Guid.NewGuid():N}", null));
        var sourceOrg = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Source SCIM {Guid.NewGuid():N}", null));
        await AddMembershipAsync(targetOrg.Id, existingUser.Id, isActive: true);
        await AddMembershipAsync(sourceOrg.Id, existingUser.Id, isActive: true);
        await AddScimUserLinkAsync(admin, sourceOrg.Id, existingUser.Id, email);
        var client = await CreateSamlClientAsync(admin, "cross-org-scim");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSCrossOrgScimSamlIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreateRestrictedSamlConnectionAsync(admin, targetOrg.Id, certificate, "cross-org-scim");

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(certificate, connection.IdentityProviderEntityId, email, "Cross", "Org", flow);
        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No user could be resolved from the SAML assertion.");
        (await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
                .AnyAsync(x => x.SsoConnectionId == connection.Id && x.Subject == email))
            .Should().BeFalse();
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithStaleEmailOutsideCurrentScimRecord_DoesNotAutoLink()
    {
        var (_, admin, saml) = CreateSamlServices();
        var staleEmail = $"stale-scim-saml-{Guid.NewGuid():N}@example.com";
        var currentEmail = $"current-scim-saml-{Guid.NewGuid():N}@example.com";
        var existingUser = await admin.CreateUserAsync(new SqlOSCreateUserRequest("SCIM Renamed Member", staleEmail, "P@ssword123!"));
        await MarkEmailVerifiedAsync(existingUser.Id);
        var staleEmailRecord = await AspireFixture.SharedContext.Set<SqlOSUserEmail>()
            .SingleAsync(x => x.UserId == existingUser.Id);
        staleEmailRecord.IsPrimary = false;
        AspireFixture.SharedContext.Set<SqlOSUserEmail>().Add(new SqlOSUserEmail
        {
            Id = $"eml_{Guid.NewGuid():N}",
            UserId = existingUser.Id,
            Email = currentEmail,
            NormalizedEmail = SqlOSAdminService.NormalizeEmail(currentEmail),
            IsPrimary = true,
            IsVerified = true,
            VerifiedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        existingUser.DefaultEmail = currentEmail;
        existingUser.UpdatedAt = DateTime.UtcNow;
        await AspireFixture.SharedContext.SaveChangesAsync();

        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"SCIM Rename {Guid.NewGuid():N}", null));
        await AddMembershipAsync(org.Id, existingUser.Id, isActive: true);
        await AddScimUserLinkAsync(admin, org.Id, existingUser.Id, currentEmail);
        var client = await CreateSamlClientAsync(admin, "stale-scim-email");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSStaleScimEmailIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreateRestrictedSamlConnectionAsync(admin, org.Id, certificate, "stale-scim-email");

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(certificate, connection.IdentityProviderEntityId, staleEmail, "Stale", "Email", flow);
        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No user could be resolved from the SAML assertion.");
        (await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
                .AnyAsync(x => x.SsoConnectionId == connection.Id && x.Subject == staleEmail))
            .Should().BeFalse();
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithOnlyLoginHintMatchingScimRecord_DoesNotAutoLink()
    {
        var (_, admin, saml) = CreateSamlServices();
        var email = $"login-hint-scim-{Guid.NewGuid():N}@example.com";
        var existingUser = await admin.CreateUserAsync(new SqlOSCreateUserRequest("SCIM Login Hint Member", email, "P@ssword123!"));
        await MarkEmailVerifiedAsync(existingUser.Id);
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"SCIM Login Hint {Guid.NewGuid():N}", null));
        await AddMembershipAsync(org.Id, existingUser.Id, isActive: true);
        await AddScimUserLinkAsync(admin, org.Id, existingUser.Id, email);
        var client = await CreateSamlClientAsync(admin, "scim-login-hint");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSScimLoginHintIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreateRestrictedSamlConnectionAsync(admin, org.Id, certificate, "scim-login-hint");
        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var authorizationRequest = await AspireFixture.SharedContext.Set<SqlOSAuthorizationRequest>()
            .SingleAsync(x => x.Id == flow.RelayState);
        authorizationRequest.LoginHintEmail = email;
        await AspireFixture.SharedContext.SaveChangesAsync();

        var samlResponse = BuildSignedSamlResponse(
            certificate,
            connection.IdentityProviderEntityId,
            email,
            "Login",
            "Hint",
            flow,
            mutateBeforeSigning: RemoveEmailAttributeBeforeSigning);
        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No user could be resolved from the SAML assertion.");
        (await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
                .AnyAsync(x => x.SsoConnectionId == connection.Id && x.Subject == email))
            .Should().BeFalse();
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithBroadEmailLinkingButOnlyVictimLoginHint_DoesNotLink()
    {
        var (_, admin, saml) = CreateSamlServices();
        var victimEmail = $"victim-login-hint-{Guid.NewGuid():N}@example.com";
        var attackerSubject = $"attacker-subject-{Guid.NewGuid():N}";
        var victim = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Victim Member", victimEmail, "P@ssword123!"));
        await MarkEmailVerifiedAsync(victim.Id);
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Victim Login Hint {Guid.NewGuid():N}", null));
        await AddMembershipAsync(org.Id, victim.Id, isActive: true);
        var client = await CreateSamlClientAsync(admin, "victim-login-hint");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSVictimLoginHintIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreatePolicySamlConnectionAsync(
            admin,
            org.Id,
            certificate,
            "victim-login-hint",
            autoProvisionUsers: false,
            autoLinkByEmail: true);
        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var authorizationRequest = await AspireFixture.SharedContext.Set<SqlOSAuthorizationRequest>()
            .SingleAsync(x => x.Id == flow.RelayState);
        authorizationRequest.LoginHintEmail = victimEmail;
        await AspireFixture.SharedContext.SaveChangesAsync();

        var samlResponse = BuildSignedSamlResponse(
            certificate,
            connection.IdentityProviderEntityId,
            attackerSubject,
            "Attacker",
            "Subject",
            flow,
            mutateBeforeSigning: RemoveEmailAttributeBeforeSigning);
        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No user could be resolved from the SAML assertion.");
        (await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
                .AnyAsync(x => x.SsoConnectionId == connection.Id && x.Subject == attackerSubject))
            .Should().BeFalse();
    }

    [TestMethod]
    public async Task SignedSamlResponse_WithJitEnabledButOnlyLoginHint_DoesNotProvision()
    {
        var (_, admin, saml) = CreateSamlServices();
        var loginHintEmail = $"jit-login-hint-{Guid.NewGuid():N}@example.com";
        var attackerSubject = $"jit-attacker-subject-{Guid.NewGuid():N}";
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"JIT Login Hint {Guid.NewGuid():N}", null));
        var client = await CreateSamlClientAsync(admin, "jit-login-hint");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSJitLoginHintIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreatePolicySamlConnectionAsync(
            admin,
            org.Id,
            certificate,
            "jit-login-hint",
            autoProvisionUsers: true,
            autoLinkByEmail: false);
        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var authorizationRequest = await AspireFixture.SharedContext.Set<SqlOSAuthorizationRequest>()
            .SingleAsync(x => x.Id == flow.RelayState);
        authorizationRequest.LoginHintEmail = loginHintEmail;
        await AspireFixture.SharedContext.SaveChangesAsync();

        var samlResponse = BuildSignedSamlResponse(
            certificate,
            connection.IdentityProviderEntityId,
            attackerSubject,
            "Attacker",
            "Subject",
            flow,
            mutateBeforeSigning: RemoveEmailAttributeBeforeSigning);
        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No user could be resolved from the SAML assertion.");
        var normalizedLoginHint = SqlOSAdminService.NormalizeEmail(loginHintEmail);
        (await AspireFixture.SharedContext.Set<SqlOSUserEmail>()
                .AnyAsync(x => x.NormalizedEmail == normalizedLoginHint))
            .Should().BeFalse();
        (await AspireFixture.SharedContext.Set<SqlOSMembership>()
                .AnyAsync(x => x.OrganizationId == org.Id))
            .Should().BeFalse();
        (await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
                .AnyAsync(x => x.SsoConnectionId == connection.Id && x.Subject == attackerSubject))
            .Should().BeFalse();
    }

    [TestMethod]
    public async Task SignedSamlResponse_ExistingSubjectBindingWinsOverScimEmailCandidate()
    {
        var (_, admin, saml) = CreateSamlServices();
        var scimEmail = $"subject-conflict-scim-{Guid.NewGuid():N}@example.com";
        var boundEmail = $"subject-conflict-bound-{Guid.NewGuid():N}@example.com";
        var scimUser = await admin.CreateUserAsync(new SqlOSCreateUserRequest("SCIM Candidate", scimEmail, "P@ssword123!"));
        var boundUser = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Already Bound User", boundEmail, "P@ssword123!"));
        await MarkEmailVerifiedAsync(scimUser.Id);
        await MarkEmailVerifiedAsync(boundUser.Id);
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"SCIM Subject Conflict {Guid.NewGuid():N}", null));
        await AddMembershipAsync(org.Id, scimUser.Id, isActive: true);
        await AddMembershipAsync(org.Id, boundUser.Id, isActive: true);
        await AddScimUserLinkAsync(admin, org.Id, scimUser.Id, scimEmail);
        var client = await CreateSamlClientAsync(admin, "scim-subject-conflict");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSScimSubjectConflictIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreateRestrictedSamlConnectionAsync(admin, org.Id, certificate, "scim-subject-conflict");
        AspireFixture.SharedContext.Set<SqlOSExternalIdentity>().Add(new SqlOSExternalIdentity
        {
            Id = $"ext_{Guid.NewGuid():N}",
            UserId = boundUser.Id,
            SsoConnectionId = connection.Id,
            Issuer = connection.IdentityProviderEntityId,
            Subject = scimEmail,
            Email = boundEmail,
            CreatedAt = DateTime.UtcNow
        });
        await AspireFixture.SharedContext.SaveChangesAsync();

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(certificate, connection.IdentityProviderEntityId, scimEmail, "SCIM", "Candidate", flow);
        var redirectUrl = await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        redirectUrl.Should().StartWith("https://client.example.local/callback?code=");
        var authorizationCode = await AspireFixture.SharedContext.Set<SqlOSAuthorizationCode>()
            .SingleAsync(x => x.AuthorizationRequestId == flow.RelayState);
        authorizationCode.UserId.Should().Be(boundUser.Id);
        (await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
                .CountAsync(x => x.SsoConnectionId == connection.Id && x.Subject == scimEmail))
            .Should().Be(1);
    }

    [DataTestMethod]
    [DataRow("inactive_link")]
    [DataRow("deleted_link")]
    [DataRow("disabled_connection")]
    [DataRow("inactive_membership")]
    [DataRow("inactive_user")]
    public async Task SignedSamlResponse_WithDeactivatedScimState_DoesNotAutoLink(string deactivatedState)
    {
        var (_, admin, saml) = CreateSamlServices();
        var email = $"deactivated-scim-{deactivatedState}-{Guid.NewGuid():N}@example.com";
        var existingUser = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Deactivated SCIM Member", email, "P@ssword123!"));
        await MarkEmailVerifiedAsync(existingUser.Id);
        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Deactivated SCIM {Guid.NewGuid():N}", null));
        await AddMembershipAsync(org.Id, existingUser.Id, isActive: deactivatedState != "inactive_membership");
        await AddScimUserLinkAsync(
            admin,
            org.Id,
            existingUser.Id,
            email,
            isActive: deactivatedState != "inactive_link",
            deletedAt: deactivatedState == "deleted_link" ? DateTime.UtcNow : null,
            connectionEnabled: deactivatedState != "disabled_connection");
        if (deactivatedState == "inactive_user")
        {
            existingUser.IsActive = false;
            existingUser.UpdatedAt = DateTime.UtcNow;
            await AspireFixture.SharedContext.SaveChangesAsync();
        }

        var clientPrefix = deactivatedState switch
        {
            "inactive_link" => "deact-link",
            "deleted_link" => "deact-delete",
            "disabled_connection" => "deact-connection",
            "inactive_membership" => "deact-member",
            "inactive_user" => "deact-user",
            _ => throw new InvalidOperationException($"Unknown deactivated SCIM state '{deactivatedState}'.")
        };
        var client = await CreateSamlClientAsync(admin, clientPrefix);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SqlOSDeactivatedScimIdP", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var connection = await CreateRestrictedSamlConnectionAsync(admin, org.Id, certificate, $"deactivated-{deactivatedState}");

        var flow = await StartSamlRequestAsync(saml, connection.Id, client.ClientId);
        var samlResponse = BuildSignedSamlResponse(certificate, connection.IdentityProviderEntityId, email, "Deactivated", "Member", flow);
        var action = async () => await saml.HandleAcsAsync(connection.Id, samlResponse, flow.RelayState, default);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("No user could be resolved from the SAML assertion.");
        (await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
                .AnyAsync(x => x.SsoConnectionId == connection.Id && x.Subject == email))
            .Should().BeFalse();
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

    private static TestSqlOSDbContext CreateIsolatedContext()
    {
        var dbOptions = new DbContextOptionsBuilder<TestSqlOSDbContext>()
            .UseSqlServer(AspireFixture.SqlConnectionString)
            .Options;
        return new TestSqlOSDbContext(dbOptions);
    }

    private static SqlOSSamlService CreateSamlService(TestSqlOSDbContext context)
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
        return new SqlOSSamlService(context, options, new SqlOSAdminService(context, options, crypto), crypto);
    }

    private static async Task<(string? Redirect, Exception? Error)> CaptureAcsAsync(
        SqlOSSamlService service,
        string connectionId,
        string response,
        string relayState)
    {
        try
        {
            return (await service.HandleAcsAsync(connectionId, response, relayState), null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    private static SqlOSSsoAuthorizationService BuildSsoAuthorizationService(TestSqlOSDbContext context)
    {
        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(context, options, crypto);
        var saml = new SqlOSSamlService(context, options, admin, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
        var discovery = new SqlOSHomeRealmDiscoveryService(context);
        return new SqlOSSsoAuthorizationService(context, admin, crypto, discovery, saml, auth);
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

    private static async Task AddMembershipAsync(string organizationId, string userId, bool isActive)
    {
        AspireFixture.SharedContext.Set<SqlOSMembership>().Add(new SqlOSMembership
        {
            OrganizationId = organizationId,
            UserId = userId,
            Role = "member",
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow
        });
        await AspireFixture.SharedContext.SaveChangesAsync();
    }

    private static async Task AddScimUserLinkAsync(
        SqlOSAdminService admin,
        string organizationId,
        string userId,
        string primaryEmail,
        bool isActive = true,
        DateTime? deletedAt = null,
        bool connectionEnabled = true)
    {
        var setup = await admin.CreateScimConnectionAsync(new SqlOSCreateScimConnectionRequest(
            organizationId,
            $"SAML provenance {Guid.NewGuid():N}",
            Enabled: connectionEnabled));
        var now = DateTime.UtcNow;
        AspireFixture.SharedContext.Set<SqlOSScimExternalId>().Add(new SqlOSScimExternalId
        {
            Id = $"scx_{Guid.NewGuid():N}",
            ConnectionId = setup.ConnectionId,
            ResourceType = "User",
            ExternalId = $"directory-{Guid.NewGuid():N}",
            EntityId = userId,
            UserName = primaryEmail,
            PrimaryEmail = primaryEmail,
            DisplayName = primaryEmail,
            OwnsUserLifecycle = true,
            IsActive = isActive,
            DeletedAt = deletedAt,
            CreatedAt = now,
            UpdatedAt = now,
            LastSyncedAt = now
        });
        await AspireFixture.SharedContext.SaveChangesAsync();
    }

    private static async Task<SqlOSSsoConnection> CreateRestrictedSamlConnectionAsync(
        SqlOSAdminService admin,
        string organizationId,
        X509Certificate2 certificate,
        string prefix)
        => await CreatePolicySamlConnectionAsync(
            admin,
            organizationId,
            certificate,
            prefix,
            autoProvisionUsers: false,
            autoLinkByEmail: false);

    private static async Task<SqlOSSsoConnection> CreatePolicySamlConnectionAsync(
        SqlOSAdminService admin,
        string organizationId,
        X509Certificate2 certificate,
        string prefix,
        bool autoProvisionUsers,
        bool autoLinkByEmail,
        bool trustUpstreamMfa = false,
        List<string>? acceptedAuthnContextClassRefs = null)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return await admin.CreateSsoConnectionAsync(new SqlOSCreateSsoConnectionRequest(
            organizationId,
            $"{prefix} SSO",
            $"urn:{prefix}:{suffix}:idp",
            "https://idp.example.test/sso",
            certificate.ExportCertificatePem(),
            AutoProvisionUsers: autoProvisionUsers,
            AutoLinkByEmail: autoLinkByEmail,
            "email",
            "first_name",
            "last_name",
            trustUpstreamMfa,
            acceptedAuthnContextClassRefs));
    }

    private static void RemoveEmailAttributeBeforeSigning(
        XmlDocument _,
        XmlElement __,
        XmlElement assertion)
    {
        var emailAttribute = assertion
            .GetElementsByTagName("Attribute", "urn:oasis:names:tc:SAML:2.0:assertion")
            .OfType<XmlElement>()
            .Single(element => element.GetAttribute("Name") == "email");
        emailAttribute.ParentNode!.RemoveChild(emailAttribute);
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
        string? responseId = null,
        string? assertionId = null,
        Action<XmlDocument, XmlElement, XmlElement>? mutateBeforeSigning = null,
        Action<XmlDocument, XmlElement>? mutateAfterSigning = null,
        string? authnContextClassRef = null)
    {
        responseId ??= $"_{Guid.NewGuid():N}";
        assertionId ??= $"_{Guid.NewGuid():N}";
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
        var authnStatementXml = string.IsNullOrWhiteSpace(authnContextClassRef)
            ? string.Empty
            : $"""
                <saml:AuthnStatement AuthnInstant="{issueInstant}">
                  <saml:AuthnContext>
                    <saml:AuthnContextClassRef>{SecurityElement.Escape(authnContextClassRef)}</saml:AuthnContextClassRef>
                  </saml:AuthnContext>
                </saml:AuthnStatement>
            """;
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
            {authnStatementXml}
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

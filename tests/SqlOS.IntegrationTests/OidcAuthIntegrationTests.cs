using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
public sealed class OidcAuthIntegrationTests
{
    [TestMethod]
    public async Task CreateUpdateDisableOidcConnection_Works()
    {
        await ResetOidcStateAsync();

        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        const string customLogo = "data:image/svg+xml;charset=utf-8,%3Csvg%20viewBox%3D%220%200%2024%2024%22%3E%3C%2Fsvg%3E";

        var connection = await admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.Google,
            "Google",
            "google-client",
            "google-secret",
            ["https://app.example.local/callback/google"],
            true,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            ["openid", "email", "profile"],
            null,
            null,
            null,
            null,
            null,
            null,
            LogoDataUrl: customLogo,
            TrustUpstreamMfa: true,
            AcceptedAmrValues: ["mfa"],
            AcceptedAcrValues: ["urn:example:loa:2"]));
        connection.LogoDataUrl.Should().Be(customLogo);
        connection.TrustUpstreamMfa.Should().BeTrue();
        connection.AcceptedAmrValuesJson.Should().Contain("mfa");

        var updated = await admin.UpdateOidcConnectionAsync(connection.Id, new SqlOSUpdateOidcConnectionRequest(
            "Google Login",
            "google-client-updated",
            null,
            ["https://app.example.local/callback/google", "https://app.example.local/callback/google-2"],
            true,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            ["openid", "email"],
            null,
            null,
            null,
            null,
            null,
            LogoDataUrl: null,
            TrustUpstreamMfa: false,
            AcceptedAmrValues: [],
            AcceptedAcrValues: []));

        updated.DisplayName.Should().Be("Google Login");
        updated.ClientId.Should().Be("google-client-updated");
        updated.LogoDataUrl.Should().BeNull();
        updated.TrustUpstreamMfa.Should().BeFalse();
        updated.AcceptedAmrValuesJson.Should().Be("[]");

        var disabled = await admin.SetOidcConnectionEnabledAsync(connection.Id, false);
        disabled.IsEnabled.Should().BeFalse();
    }

    [TestMethod]
    public async Task CompleteOidcLogin_ProvisionsExternalIdentity_AndIssuesTokens()
    {
        await ResetOidcStateAsync();

        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(AspireFixture.SharedContext, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(AspireFixture.SharedContext, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(AspireFixture.SharedContext, options, admin, crypto, settings, emailOtp);
        var oidc = new SqlOSOidcAuthService(AspireFixture.SharedContext, admin, crypto, new FakeOidcProviderHttpClientFactory(), NullLogger<SqlOSOidcAuthService>.Instance);

        var client = await EnsureClientAsync(admin, "example-web-oidc");
        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"Oidc Org {Guid.NewGuid():N}", null));
        var connection = await admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.Google,
            "Google",
            "google-client",
            "google-secret",
            ["https://app.example.local/callback/google"],
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

        var completion = await oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            client.ClientId,
            "https://app.example.local/callback/google",
            "success:provisioned@example.com:nonce-google",
            "verifier",
            "nonce-google",
            null));

        var user = await AspireFixture.SharedContext.Set<SqlOSUser>().FirstAsync(x => x.Id == completion.UserId);
        await admin.CreateMembershipAsync(organization.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));

        var postMembershipCompletion = await oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            client.ClientId,
            "https://app.example.local/callback/google",
            "success:provisioned@example.com:nonce-google",
            "verifier",
            "nonce-google",
            null));

        var tokens = await auth.CreateSessionTokensForUserAsync(
            user,
            client,
            postMembershipCompletion.OrganizationId,
            postMembershipCompletion.AuthenticationMethod,
            "integration-test",
            "127.0.0.1");

        tokens.AccessToken.Should().NotBeNullOrWhiteSpace();
        tokens.RefreshToken.Should().NotBeNullOrWhiteSpace();
        postMembershipCompletion.OrganizationId.Should().Be(organization.Id);
        (await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>().CountAsync(x => x.OidcConnectionId != null)).Should().Be(1);
    }

    [TestMethod]
    public async Task TrustedOidcAmr_ProducesSessionAndTokenAssuranceWithoutLocalStepUp()
    {
        await ResetOidcStateAsync();

        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(
            AspireFixture.SharedContext,
            options,
            AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(AspireFixture.SharedContext, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(
            AspireFixture.SharedContext,
            admin,
            crypto,
            settings,
            emailSender,
            options);
        var auth = new SqlOSAuthService(
            AspireFixture.SharedContext,
            options,
            admin,
            crypto,
            settings,
            emailOtp);
        var oidc = new SqlOSOidcAuthService(
            AspireFixture.SharedContext,
            admin,
            crypto,
            new FakeOidcProviderHttpClientFactory(),
            NullLogger<SqlOSOidcAuthService>.Instance);
        var client = await EnsureClientAsync(admin, "example-web-upstream-mfa");
        var connection = await admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.Google,
            "Google upstream MFA",
            "google-client",
            "google-secret",
            ["https://app.example.local/callback/google"],
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
        connection.TrustUpstreamMfa = true;
        connection.AcceptedAmrValuesJson = """["mfa"]""";
        await AspireFixture.SharedContext.SaveChangesAsync();

        var completion = await oidc.CompleteAuthorizationAsync(
            new SqlOSCompleteOidcAuthorizationRequest(
                connection.Id,
                client.ClientId,
                "https://app.example.local/callback/google",
                "amr-mfa:upstream-mfa@example.com:nonce-upstream-mfa",
                "verifier",
                "nonce-upstream-mfa",
                null));
        var user = await AspireFixture.SharedContext.Set<SqlOSUser>()
            .SingleAsync(item => item.Id == completion.UserId);
        var tokens = await auth.CreateSessionTokensForUserAsync(
            user,
            client,
            completion.OrganizationId,
            completion.AuthenticationMethod,
            "integration-test",
            "127.0.0.1");

        completion.AuthenticationMethod.Should().Be("google+upstream_mfa");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(tokens.AccessToken);
        jwt.Claims.Where(claim => claim.Type == "amr")
            .Select(claim => claim.Value)
            .Should().Contain(["google", "upstream_mfa"]);
        (await crypto.ValidateAccessTokenAsync(tokens.AccessToken, client.Audience))
            .Should().NotBeNull();
    }

    [TestMethod]
    public async Task OidcAuthorizationRequestCallback_StampsUpstreamAuthTimeOnIssuedCode()
    {
        await ResetOidcStateAsync();

        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(AspireFixture.SharedContext, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(AspireFixture.SharedContext, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(AspireFixture.SharedContext, options, admin, crypto, settings, emailOtp);
        var authPageSession = new SqlOSAuthPageSessionService(AspireFixture.SharedContext, crypto, settings);
        var authorization = new SqlOSAuthorizationServerService(
            AspireFixture.SharedContext,
            admin,
            auth,
            crypto,
            settings,
            authPageSession,
            options);
        var oidc = new SqlOSOidcAuthService(
            AspireFixture.SharedContext,
            admin,
            crypto,
            new FakeOidcProviderHttpClientFactory(),
            NullLogger<SqlOSOidcAuthService>.Instance);
        var browser = new SqlOSOidcBrowserAuthService(
            AspireFixture.SharedContext,
            admin,
            auth,
            authorization,
            crypto,
            oidc,
            options);

        var client = await EnsureClientAsync(admin, "example-web-auth-time");
        // The provider callback URI the browser service derives from the fixture
        // options (issuer origin + base path).
        var connection = await CreateGoogleConnectionAsync(admin, "https://tests/sqlos/auth/oidc/callback");

        var authorizationRequest = await authorization.CreateAuthorizationRequestAsync(new SqlOSAuthorizeRequestInput(
            "code",
            client.ClientId,
            $"https://app.example.local/callback/{client.ClientId}",
            Guid.NewGuid().ToString("N"),
            "openid",
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            "S256",
            null,
            null,
            null,
            null,
            "hosted",
            null));

        var startContext = new DefaultHttpContext();
        startContext.Request.Scheme = "https";
        startContext.Request.Host = new HostString("tests");
        var startResult = await browser.CreateAuthorizationUrlForAuthRequestAsync(
            authorizationRequest.Id,
            connection.Id,
            "stale-session@example.com",
            startContext);

        var providerQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers
            .ParseQuery(new Uri(startResult.AuthorizationUrl).Query);
        var state = providerQuery["state"].ToString();
        var nonce = providerQuery["nonce"].ToString();

        // The fake provider mints an ID token whose auth_time is 45 minutes ago,
        // simulating a silently reused upstream session.
        var callbackContext = new DefaultHttpContext();
        callbackContext.Request.Scheme = "https";
        callbackContext.Request.Host = new HostString("tests");
        callbackContext.Request.Method = "GET";
        callbackContext.Request.QueryString = new QueryString(
            $"?state={Uri.EscapeDataString(state)}&code={Uri.EscapeDataString($"stale-auth-time:stale-session@example.com:{nonce}")}");

        await browser.HandleCallbackAsync(callbackContext);

        // The minted authorization code must carry the upstream authentication
        // moment, not the callback time.
        var code = await AspireFixture.SharedContext.Set<SqlOSAuthorizationCode>()
            .SingleAsync(x => x.AuthorizationRequestId == authorizationRequest.Id);
        code.AuthTime.Should().NotBeNull();
        code.AuthTime!.Value.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(-45), TimeSpan.FromMinutes(1));
    }

    [TestMethod]
    public async Task OidcLogin_Tokens_CanBeRefreshed()
    {
        await ResetOidcStateAsync();

        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(AspireFixture.SharedContext, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(AspireFixture.SharedContext, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(AspireFixture.SharedContext, options, admin, crypto, settings, emailOtp);
        var oidc = new SqlOSOidcAuthService(AspireFixture.SharedContext, admin, crypto, new FakeOidcProviderHttpClientFactory(), NullLogger<SqlOSOidcAuthService>.Instance);

        var client = await EnsureClientAsync(admin, "example-web-refresh");
        var connection = await admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.Microsoft,
            "Microsoft",
            "microsoft-client",
            "microsoft-secret",
            ["https://app.example.local/callback/microsoft"],
            true,
            null,
            null,
            null,
            null,
            null,
            null,
            "common",
            null,
            null,
            null,
            null,
            null,
            null));

        var completion = await oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            client.ClientId,
            "https://app.example.local/callback/microsoft",
            "success:refresh@example.com:nonce-microsoft",
            "verifier",
            "nonce-microsoft",
            null));

        var user = await AspireFixture.SharedContext.Set<SqlOSUser>().FirstAsync(x => x.Id == completion.UserId);
        var initialTokens = await auth.CreateSessionTokensForUserAsync(
            user,
            client,
            completion.OrganizationId,
            completion.AuthenticationMethod,
            "integration-test",
            "127.0.0.1");

        var refreshed = await auth.RefreshAsync(new SqlOSRefreshRequest(initialTokens.RefreshToken, completion.OrganizationId));

        refreshed.RefreshToken.Should().NotBe(initialTokens.RefreshToken);
        refreshed.SessionId.Should().Be(initialTokens.SessionId);
        refreshed.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task OidcCallback_ValidAttackerTokenAndVictimCallbackEmail_DoesNotLinkVictim_InSql()
    {
        await ResetOidcStateAsync();

        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var oidc = new SqlOSOidcAuthService(AspireFixture.SharedContext, admin, crypto, new FakeOidcProviderHttpClientFactory(), NullLogger<SqlOSOidcAuthService>.Instance);
        var suffix = Guid.NewGuid().ToString("N");
        var victimEmail = $"victim-{suffix}@example.com";
        var attackerEmail = $"attacker-{suffix}@example.com";
        var callbackUri = $"https://app.example.local/callback/apple-{suffix}";
        var client = await EnsureClientAsync(admin, $"example-web-apple-attack-{suffix}");
        var victim = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Victim", victimEmail, null));
        var connection = await CreateAppleConnectionAsync(admin, callbackUri);
        var astralName = string.Concat(Enumerable.Repeat("😀", 150));

        var result = await oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            client.ClientId,
            callbackUri,
            $"success:{attackerEmail}:nonce-apple",
            "verifier",
            "nonce-apple",
            $"{{\"email\":\"{victimEmail}\",\"name\":{{\"firstName\":\"{astralName}\",\"lastName\":\"Account\"}}}}"));

        result.UserId.Should().NotBe(victim.Id);
        result.Email.Should().Be(attackerEmail);
        result.DisplayName.Length.Should().BeLessThanOrEqualTo(200);
        char.IsHighSurrogate(result.DisplayName[^1]).Should().BeFalse();
        var identity = await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
            .SingleAsync(x => x.OidcConnectionId == connection.Id);
        identity.UserId.Should().Be(result.UserId);
        identity.Subject.Should().Be($"apple-{attackerEmail}");
        identity.Email.Should().Be(attackerEmail);
        (await AspireFixture.SharedContext.Set<SqlOSUser>().SingleAsync(x => x.Id == result.UserId))
            .DisplayName.Should().Be(result.DisplayName);
    }

    [TestMethod]
    public async Task OidcUserInfo_SubMismatch_IsRejectedAndAudited_InSql()
    {
        await ResetOidcStateAsync();

        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var oidc = new SqlOSOidcAuthService(AspireFixture.SharedContext, admin, crypto, new FakeOidcProviderHttpClientFactory(), NullLogger<SqlOSOidcAuthService>.Instance);
        var suffix = Guid.NewGuid().ToString("N");
        var email = $"mismatch-{suffix}@example.com";
        var callbackUri = $"https://app.example.local/callback/google-{suffix}";
        var client = await EnsureClientAsync(admin, $"example-web-google-mismatch-{suffix}");
        var connection = await CreateGoogleConnectionAsync(admin, callbackUri);

        var action = () => oidc.CompleteAuthorizationAsync(
            new SqlOSCompleteOidcAuthorizationRequest(
                connection.Id,
                client.ClientId,
                callbackUri,
                $"userinfo-sub-mismatch:{email}:nonce-google",
                "verifier",
                "nonce-google",
                null),
            "203.0.113.154");

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The social login could not be completed.");
        (await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
            .CountAsync(x => x.OidcConnectionId == connection.Id)).Should().Be(0);
        var audit = await AspireFixture.SharedContext.Set<SqlOSAuditEvent>()
            .Where(x => x.EventType == "user.login.oidc.claim_mismatch" && x.ActorId == connection.Id)
            .OrderByDescending(x => x.OccurredAt)
            .FirstAsync();
        audit.IpAddress.Should().Be("203.0.113.154");
        audit.MetadataJson.Should().Contain("userinfo_subject_mismatch");
        audit.MetadataJson.Should().Contain($"google-{email}");
        audit.MetadataJson.Should().Contain($"mismatched-google-{email}");
    }

    [TestMethod]
    public async Task OidcEmailVerification_CannotBeCombinedAcrossClaimSources_InSql()
    {
        await ResetOidcStateAsync();

        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var oidc = new SqlOSOidcAuthService(AspireFixture.SharedContext, admin, crypto, new FakeOidcProviderHttpClientFactory(), NullLogger<SqlOSOidcAuthService>.Instance);
        var suffix = Guid.NewGuid().ToString("N");
        var victimEmail = $"split-victim-{suffix}@example.com";
        var attackerEmail = $"split-attacker-{suffix}@example.com";
        var callbackUri = $"https://app.example.local/callback/google-split-{suffix}";
        var client = await EnsureClientAsync(admin, $"example-web-google-split-{suffix}");
        var victim = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Split Victim", victimEmail, null));
        var connection = await CreateGoogleConnectionAsync(admin, callbackUri);

        var action = () => oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            client.ClientId,
            callbackUri,
            $"split-claims:{attackerEmail}:{victimEmail}:nonce-google",
            "verifier",
            "nonce-google",
            null));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The social login could not be completed.");
        (await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
            .CountAsync(x => x.OidcConnectionId == connection.Id)).Should().Be(0);
        (await AspireFixture.SharedContext.Set<SqlOSUser>()
            .SingleAsync(x => x.Id == victim.Id)).DefaultEmail.Should().Be(victimEmail);
    }

    [TestMethod]
    public async Task AppleAndCustomOidcConnections_Work_WithSharedRuntime()
    {
        await ResetOidcStateAsync();

        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var oidc = new SqlOSOidcAuthService(AspireFixture.SharedContext, admin, crypto, new FakeOidcProviderHttpClientFactory(), NullLogger<SqlOSOidcAuthService>.Instance);
        await EnsureClientAsync(admin, "example-web-apple");

        var appleConnection = await admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.Apple,
            "Apple",
            "com.example.service",
            null,
            ["https://app.example.local/callback/apple"],
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
            "TEAM123",
            "KEY123",
            TestApplePrivateKeyPem.Value));

        var customConnection = await admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.Custom,
            "Acme OIDC",
            "custom-client",
            "custom-secret",
            ["https://app.example.local/callback/custom"],
            false,
            null,
            "https://oidc.example.local",
            "https://oidc.example.local/authorize",
            "https://oidc.example.local/token",
            "https://oidc.example.local/userinfo",
            "https://oidc.example.local/jwks",
            null,
            ["openid", "profile", "email"],
            new SqlOSOidcClaimMapping
            {
                SubjectClaim = "custom_sub",
                EmailClaim = "email_address",
                EmailVerifiedClaim = "email_verified_flag",
                DisplayNameClaim = "full_name"
            },
            SqlOSOidcClientAuthMethod.ClientSecretPost,
            true,
            null,
            null,
            null));

        var appleResult = await oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            appleConnection.Id,
            "example-web-apple",
            "https://app.example.local/callback/apple",
            "success:apple@example.com:nonce-apple",
            "verifier",
            "nonce-apple",
            "{\"name\":{\"firstName\":\"Apple\",\"lastName\":\"User\"},\"email\":\"apple@example.com\"}"));

        var customResult = await oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            customConnection.Id,
            "example-web-apple",
            "https://app.example.local/callback/custom",
            "success:custom@example.com:nonce-custom",
            "verifier",
            "nonce-custom",
            null));

        appleResult.AuthenticationMethod.Should().Be("apple");
        customResult.AuthenticationMethod.Should().Be("oidc");
    }

    private static async Task<SqlOSClientApplication> EnsureClientAsync(SqlOSAdminService admin, string clientId)
    {
        var existing = await AspireFixture.SharedContext.Set<SqlOSClientApplication>()
            .FirstOrDefaultAsync(x => x.ClientId == clientId);
        if (existing != null)
        {
            return existing;
        }

        // These tests pin upstream social-login mechanics for the operator's own
        // app; first-party keeps them off the third-party consent interstitial.
        return await admin.CreateClientAsync(new SqlOSCreateClientRequest(clientId, clientId, "sqlos-example", [$"https://app.example.local/callback/{clientId}"], IsFirstParty: true));
    }

    private static Task<SqlOSOidcConnection> CreateGoogleConnectionAsync(SqlOSAdminService admin, string callbackUri)
        => admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.Google,
            "Google",
            "google-client",
            "google-secret",
            [callbackUri],
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

    private static Task<SqlOSOidcConnection> CreateAppleConnectionAsync(SqlOSAdminService admin, string callbackUri)
        => admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.Apple,
            "Apple",
            "com.example.service",
            null,
            [callbackUri],
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
            "TEAM123",
            "KEY123",
            TestApplePrivateKeyPem.Value));

    private static async Task ResetOidcStateAsync()
    {
        var externalIdentities = await AspireFixture.SharedContext.Set<SqlOSExternalIdentity>()
            .Where(x => x.OidcConnectionId != null)
            .ToListAsync();
        AspireFixture.SharedContext.Set<SqlOSExternalIdentity>().RemoveRange(externalIdentities);

        var connections = await AspireFixture.SharedContext.Set<SqlOSOidcConnection>().ToListAsync();
        AspireFixture.SharedContext.Set<SqlOSOidcConnection>().RemoveRange(connections);
        await AspireFixture.SharedContext.SaveChangesAsync();
    }

    private static readonly Lazy<string> TestApplePrivateKeyPem = new(() =>
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return ecdsa.ExportPkcs8PrivateKeyPem();
    });
}

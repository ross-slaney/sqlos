using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Security.Cryptography;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSOidcAuthServiceTests
{
    [TestMethod]
    public async Task CompleteAuthorization_GoogleVerifiedEmail_LinksExistingUser()
    {
        using var context = CreateContext();
        var (admin, oidc) = CreateServices(context);

        await admin.CreateClientAsync(new SqlOSCreateClientRequest("example-web", "Example Web", "sqlos-example", ["https://app.example.local/callback/google"]));
        var existingUser = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Existing User", "link@example.com", null));
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

        var result = await oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            "example-web",
            "https://app.example.local/callback/google",
            "success:link@example.com:nonce-google",
            "verifier",
            "nonce-google",
            null));

        result.UserId.Should().Be(existingUser.Id);
        result.Email.Should().Be("link@example.com");
        context.Set<SqlOSExternalIdentity>().Count().Should().Be(1);
    }

    [TestMethod]
    public async Task CompleteAuthorization_MicrosoftPreferredUsername_FallsBackAndProvisionsUser()
    {
        using var context = CreateContext();
        var (admin, oidc) = CreateServices(context);

        await admin.CreateClientAsync(new SqlOSCreateClientRequest("example-web", "Example Web", "sqlos-example", ["https://app.example.local/callback/microsoft"]));
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

        var result = await oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            "example-web",
            "https://app.example.local/callback/microsoft",
            "success:preferred@example.com:nonce-microsoft",
            "verifier",
            "nonce-microsoft",
            null));

        result.Email.Should().Be("preferred@example.com");
        context.Set<SqlOSUserEmail>().Single().IsVerified.Should().BeFalse();
    }

    [TestMethod]
    public async Task CompleteAuthorization_AppleWebFlow_UsesAppleConnectionAndCallbackPayload()
    {
        using var context = CreateContext();
        var (admin, oidc) = CreateServices(context);

        await admin.CreateClientAsync(new SqlOSCreateClientRequest("example-web", "Example Web", "sqlos-example", ["https://app.example.local/callback/apple"]));
        var connection = await admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
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

        var result = await oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            "example-web",
            "https://app.example.local/callback/apple",
            "success:apple-user@example.com:nonce-apple",
            "verifier",
            "nonce-apple",
            "{\"name\":{\"firstName\":\"Apple\",\"lastName\":\"User\"},\"email\":\"apple-user@example.com\"}"));

        result.Email.Should().Be("apple-user@example.com");
        result.DisplayName.Should().Contain("Apple");
        result.AuthenticationMethod.Should().Be("apple");
    }

    [TestMethod]
    public async Task OidcCallback_UnsignedUserEmailCannotOverrideSignedIdTokenEmail()
    {
        using var context = CreateContext();
        var (admin, oidc) = CreateServices(context);
        const string callbackUri = "https://app.example.local/callback/google";

        await admin.CreateClientAsync(new SqlOSCreateClientRequest("example-web", "Example Web", "sqlos-example", [callbackUri]));
        var connection = await CreateGoogleConnectionAsync(admin, callbackUri);

        var result = await oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            "example-web",
            callbackUri,
            "success:signed-user@example.com:nonce-google",
            "verifier",
            "nonce-google",
            "{\"email\":\"forged-victim@example.com\",\"name\":{\"firstName\":\"Forged\",\"lastName\":\"Name\"}}"));

        result.Email.Should().Be("signed-user@example.com");
        result.DisplayName.Should().Be("Google signed-user@example.com");
        (await context.Set<SqlOSExternalIdentity>().SingleAsync()).Subject.Should().Be("google-signed-user@example.com");
    }

    [TestMethod]
    public async Task OidcCallback_ValidAttackerTokenAndVictimCallbackEmail_DoesNotLinkVictim()
    {
        using var context = CreateContext();
        var (admin, oidc) = CreateServices(context);
        const string callbackUri = "https://app.example.local/callback/apple";

        await admin.CreateClientAsync(new SqlOSCreateClientRequest("example-web", "Example Web", "sqlos-example", [callbackUri]));
        var victim = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Victim", "victim@example.com", null));
        var connection = await CreateAppleConnectionAsync(admin, callbackUri);

        var result = await oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            "example-web",
            callbackUri,
            "success:attacker@example.com:nonce-apple",
            "verifier",
            "nonce-apple",
            "{\"email\":\"victim@example.com\",\"name\":{\"firstName\":\"Attacker\",\"lastName\":\"Account\"}}"));

        result.UserId.Should().NotBe(victim.Id);
        result.Email.Should().Be("attacker@example.com");
        var identity = await context.Set<SqlOSExternalIdentity>().SingleAsync();
        identity.UserId.Should().Be(result.UserId);
        identity.Subject.Should().Be("apple-attacker@example.com");
        identity.Email.Should().Be("attacker@example.com");
    }

    [TestMethod]
    public async Task OidcUserInfo_SubMismatch_RejectsAllUserInfoClaims()
    {
        using var context = CreateContext();
        var (admin, oidc) = CreateServices(context);
        const string callbackUri = "https://app.example.local/callback/google";

        await admin.CreateClientAsync(new SqlOSCreateClientRequest("example-web", "Example Web", "sqlos-example", [callbackUri]));
        var connection = await CreateGoogleConnectionAsync(admin, callbackUri);

        var action = () => oidc.CompleteAuthorizationAsync(
            new SqlOSCompleteOidcAuthorizationRequest(
                connection.Id,
                "example-web",
                callbackUri,
                "userinfo-sub-mismatch:user@example.com:nonce-google",
                "verifier",
                "nonce-google",
                null),
            "203.0.113.154");

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The social login could not be completed.");
        context.Set<SqlOSUser>().Should().BeEmpty();
        context.Set<SqlOSExternalIdentity>().Should().BeEmpty();

        var audit = await context.Set<SqlOSAuditEvent>()
            .SingleAsync(x => x.EventType == "user.login.oidc.claim_mismatch");
        audit.IpAddress.Should().Be("203.0.113.154");
        audit.MetadataJson.Should().Contain("userinfo_subject_mismatch");
        audit.MetadataJson.Should().Contain("google-user@example.com");
        audit.MetadataJson.Should().Contain("mismatched-google-user@example.com");
    }

    [TestMethod]
    public async Task OidcEmailVerification_CannotBeCombinedAcrossClaimSources()
    {
        using var context = CreateContext();
        var (admin, oidc) = CreateServices(context);
        const string callbackUri = "https://app.example.local/callback/google";

        await admin.CreateClientAsync(new SqlOSCreateClientRequest("example-web", "Example Web", "sqlos-example", [callbackUri]));
        var victim = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Victim", "victim@example.com", null));
        var connection = await CreateGoogleConnectionAsync(admin, callbackUri);

        var action = () => oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            "example-web",
            callbackUri,
            "split-claims:attacker@example.com:victim@example.com:nonce-google",
            "verifier",
            "nonce-google",
            null));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The social login could not be completed.");
        context.Set<SqlOSExternalIdentity>().Should().BeEmpty();
        context.Set<SqlOSUser>().Single().Id.Should().Be(victim.Id);
        (await context.Set<SqlOSAuditEvent>()
            .SingleAsync(x => x.EventType == "user.login.oidc.claim_mismatch"))
            .MetadataJson.Should().Contain("verified_email_missing_from_source");
    }

    [TestMethod]
    public async Task AppleCallback_UserPayloadMayOnlySupplySanitizedDisplayName()
    {
        using var context = CreateContext();
        var (admin, oidc) = CreateServices(context);
        const string callbackUri = "https://app.example.local/callback/apple";

        await admin.CreateClientAsync(new SqlOSCreateClientRequest("example-web", "Example Web", "sqlos-example", [callbackUri]));
        var connection = await CreateAppleConnectionAsync(admin, callbackUri);
        var longName = string.Concat(Enumerable.Repeat("😀", 150));

        var result = await oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            "example-web",
            callbackUri,
            "success:signed-apple@example.com:nonce-apple",
            "verifier",
            "nonce-apple",
            $"{{\"email\":\"forged@example.com\",\"sub\":\"forged-subject\",\"email_verified\":false,\"name\":{{\"firstName\":\"  <Admin>\\u0000  Jane   {longName}\",\"lastName\":\"  Doe\\n<script>  \"}}}}"));

        result.Email.Should().Be("signed-apple@example.com");
        result.DisplayName.Should().StartWith("Admin Jane ");
        result.DisplayName.Should().NotContain("<").And.NotContain(">").And.NotContain("\0").And.NotContain("\n");
        result.DisplayName.Length.Should().BeLessThanOrEqualTo(200);
        char.IsHighSurrogate(result.DisplayName[^1]).Should().BeFalse();
        (await context.Set<SqlOSExternalIdentity>().SingleAsync()).Subject.Should().Be("apple-signed-apple@example.com");
    }

    [TestMethod]
    public async Task CompleteAuthorization_GitHubOAuthProfile_ProvisionsUserWithVerifiedPrimaryEmail()
    {
        using var context = CreateContext();
        var (admin, oidc) = CreateServices(context);

        await admin.CreateClientAsync(new SqlOSCreateClientRequest("example-web", "Example Web", "sqlos-example", ["https://app.example.local/callback/github"]));
        var connection = await admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.GitHub,
            "GitHub",
            "github-client",
            "github-secret",
            ["https://app.example.local/callback/github"],
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

        var result = await oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            "example-web",
            "https://app.example.local/callback/github",
            "success:github-user@example.com:nonce-github",
            "verifier",
            "nonce-github",
            null));

        result.Email.Should().Be("github-user@example.com");
        result.AuthenticationMethod.Should().Be("github");
        result.UserCreated.Should().BeTrue();
        connection.Protocol.Should().Be(SqlOSSocialProviderProtocol.OAuthProfile);

        var externalIdentity = await context.Set<SqlOSExternalIdentity>().SingleAsync();
        externalIdentity.Issuer.Should().Be("https://github.com");
        externalIdentity.Subject.Should().Be("123456789");
        externalIdentity.OidcConnectionId.Should().Be(connection.Id);

        var second = await oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            "example-web",
            "https://app.example.local/callback/github",
            "success:github-user@example.com:nonce-github",
            "verifier",
            "nonce-github",
            null));

        second.UserId.Should().Be(result.UserId);
        second.UserCreated.Should().BeFalse();
        context.Set<SqlOSExternalIdentity>().Count().Should().Be(1);
    }

    [TestMethod]
    public async Task CompleteAuthorization_GitHubWithoutVerifiedPrimaryEmail_Fails()
    {
        using var context = CreateContext();
        var (admin, oidc) = CreateServices(context);

        await admin.CreateClientAsync(new SqlOSCreateClientRequest("example-web", "Example Web", "sqlos-example", ["https://app.example.local/callback/github"]));
        var connection = await admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.GitHub,
            "GitHub",
            "github-client",
            "github-secret",
            ["https://app.example.local/callback/github"],
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

        var action = () => oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            "example-web",
            "https://app.example.local/callback/github",
            "unverified:github-user@example.com:nonce-github",
            "verifier",
            "nonce-github",
            null));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*verified primary email*");
    }

    [TestMethod]
    public async Task CompleteAuthorization_CustomManualConfig_UsesClaimMapping()
    {
        using var context = CreateContext();
        var (admin, oidc) = CreateServices(context);

        await admin.CreateClientAsync(new SqlOSCreateClientRequest("example-web", "Example Web", "sqlos-example", ["https://app.example.local/callback/custom"]));
        var connection = await admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
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

        var result = await oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            "example-web",
            "https://app.example.local/callback/custom",
            "success:custom-user@example.com:nonce-custom",
            "verifier",
            "nonce-custom",
            null));

        result.Email.Should().Be("custom-user@example.com");
        result.DisplayName.Should().Be("Custom custom-user@example.com");
        result.AuthenticationMethod.Should().Be("oidc");
    }

    [TestMethod]
    public async Task CompleteAuthorization_MissingEmail_Fails()
    {
        using var context = CreateContext();
        var (admin, oidc) = CreateServices(context);

        await admin.CreateClientAsync(new SqlOSCreateClientRequest("example-web", "Example Web", "sqlos-example", ["https://app.example.local/callback/google"]));
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

        var action = () => oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            "example-web",
            "https://app.example.local/callback/google",
            "missing-email",
            "verifier",
            "nonce",
            null));

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*usable email*");
    }

    [TestMethod]
    public async Task CompleteAuthorization_WithMultipleOrganizations_Fails()
    {
        using var context = CreateContext();
        var (admin, oidc) = CreateServices(context);

        await admin.CreateClientAsync(new SqlOSCreateClientRequest("example-web", "Example Web", "sqlos-example", ["https://app.example.local/callback/google"]));
        var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Multi Org", "multi@example.com", null));
        var firstOrg = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("First", null));
        var secondOrg = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Second", null));
        await admin.CreateMembershipAsync(firstOrg.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));
        await admin.CreateMembershipAsync(secondOrg.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));
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

        var result = await oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            "example-web",
            "https://app.example.local/callback/google",
            "success:multi@example.com:nonce-multi",
            "verifier",
            "nonce-multi",
            null));

        result.Email.Should().Be("multi@example.com");
        result.OrganizationId.Should().BeNull();
    }

    [TestMethod]
    public async Task ListEnabledProviders_UsesBuiltInAndCustomLogoDataUrls()
    {
        using var context = CreateContext();
        var (admin, oidc) = CreateServices(context);

        await admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
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

        var customConnection = await admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.Custom,
            "Acme Identity",
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
            null,
            SqlOSOidcClientAuthMethod.ClientSecretPost,
            true,
            null,
            null,
            null,
            "data:image/png;base64,custom-logo"));

        var providers = await oidc.ListEnabledProvidersAsync();

        providers.Should().Contain(x =>
            x.ProviderType == "Microsoft" &&
            !string.IsNullOrWhiteSpace(x.LogoDataUrl) &&
            x.LogoDataUrl.StartsWith("data:image/svg+xml", StringComparison.Ordinal));
        providers.Should().Contain(x =>
            x.ConnectionId == customConnection.Id &&
            x.LogoDataUrl == "data:image/png;base64,custom-logo");
    }

    private static (SqlOSAdminService admin, SqlOSOidcAuthService oidc) CreateServices(TestSqlOSInMemoryDbContext context)
    {
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = new SqlOSCryptoService(context, options, new EphemeralDataProtectionProvider());
        var admin = new SqlOSAdminService(context, options, crypto);
        var oidc = new SqlOSOidcAuthService(context, admin, crypto, new FakeOidcProviderHttpClientFactory(), NullLogger<SqlOSOidcAuthService>.Instance);
        return (admin, oidc);
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

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }

    private static readonly Lazy<string> TestApplePrivateKeyPem = new(() =>
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return ecdsa.ExportPkcs8PrivateKeyPem();
    });
}

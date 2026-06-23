using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
        context.Set<SqlOSUserEmail>().Single().IsVerified.Should().BeTrue();
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
    public async Task StartAuthorization_RejectsOversizedDiscoveryResponse_AndAuditsReason()
    {
        using var context = CreateContext();
        var httpFactory = new FakeOidcProviderHttpClientFactory(request =>
            RequestUriContains(request, ".well-known/openid-configuration")
                ? OversizedJsonResponse()
                : null);
        var (admin, oidc) = CreateServices(context, httpFactory);

        var connection = await CreateGoogleConnectionAsync(admin);

        var action = () => oidc.StartAuthorizationAsync(new SqlOSStartOidcAuthorizationRequest(
            connection.Id,
            "user@example.com",
            "example-web",
            "https://app.example.local/callback/google",
            "state",
            "nonce",
            "challenge",
            "S256"), "127.0.0.1");

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The social login provider response could not be processed.");
        await AuditShouldContainAsync(context, "user.login.oidc.start_error", "OIDC discovery response exceeded");
    }

    [TestMethod]
    public async Task StartAuthorization_RejectsNonJsonDiscoveryResponse_AndAuditsReason()
    {
        using var context = CreateContext();
        var httpFactory = new FakeOidcProviderHttpClientFactory(request =>
            RequestUriContains(request, ".well-known/openid-configuration")
                ? TextResponse(HttpStatusCode.OK, "<html>not json</html>", "text/html")
                : null);
        var (admin, oidc) = CreateServices(context, httpFactory);

        var connection = await CreateGoogleConnectionAsync(admin);

        var action = () => oidc.StartAuthorizationAsync(new SqlOSStartOidcAuthorizationRequest(
            connection.Id,
            "user@example.com",
            "example-web",
            "https://app.example.local/callback/google",
            "state",
            "nonce",
            "challenge",
            "S256"), "127.0.0.1");

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The social login provider response could not be processed.");
        await AuditShouldContainAsync(context, "user.login.oidc.start_error", "must be JSON");
    }

    [TestMethod]
    public async Task CompleteAuthorization_RejectsOversizedJwksResponse_AndAuditsReason()
    {
        using var context = CreateContext();
        var httpFactory = new FakeOidcProviderHttpClientFactory(request =>
            RequestUriContains(request, "/certs")
                ? OversizedJsonResponse()
                : null);
        var (admin, oidc) = CreateServices(context, httpFactory);

        await admin.CreateClientAsync(new SqlOSCreateClientRequest("example-web", "Example Web", "sqlos-example", ["https://app.example.local/callback/google"]));
        var connection = await CreateGoogleConnectionAsync(admin);

        var action = () => oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            "example-web",
            "https://app.example.local/callback/google",
            "success:jwks@example.com:nonce-google",
            "verifier",
            "nonce-google",
            null), "127.0.0.1");

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The social login provider response could not be processed.");
        await AuditShouldContainAsync(context, "user.login.oidc.error", "OIDC JWKS response exceeded");
    }

    [TestMethod]
    public async Task CompleteAuthorization_RejectsOversizedUserInfoResponse_AndAuditsReason()
    {
        using var context = CreateContext();
        var httpFactory = new FakeOidcProviderHttpClientFactory(request =>
            RequestUriContains(request, "userinfo")
                ? OversizedJsonResponse()
                : null);
        var (admin, oidc) = CreateServices(context, httpFactory);

        await admin.CreateClientAsync(new SqlOSCreateClientRequest("example-web", "Example Web", "sqlos-example", ["https://app.example.local/callback/custom"]));
        var connection = await CreateCustomConnectionAsync(admin);

        var action = () => oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            "example-web",
            "https://app.example.local/callback/custom",
            "success:userinfo@example.com:nonce-custom",
            "verifier",
            "nonce-custom",
            null), "127.0.0.1");

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The social login provider response could not be processed.");
        await AuditShouldContainAsync(context, "user.login.oidc.error", "OIDC userinfo response exceeded");
    }

    [TestMethod]
    public async Task CompleteAuthorization_RejectsOversizedGitHubProfileResponse_AndAuditsReason()
    {
        using var context = CreateContext();
        var httpFactory = new FakeOidcProviderHttpClientFactory(request =>
            string.Equals(request.RequestUri?.AbsoluteUri, "https://api.github.com/user", StringComparison.OrdinalIgnoreCase)
                ? OversizedJsonResponse()
                : null);
        var (admin, oidc) = CreateServices(context, httpFactory);

        await admin.CreateClientAsync(new SqlOSCreateClientRequest("example-web", "Example Web", "sqlos-example", ["https://app.example.local/callback/github"]));
        var connection = await CreateGitHubConnectionAsync(admin);

        var action = () => oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id,
            "example-web",
            "https://app.example.local/callback/github",
            "success:github-profile@example.com:nonce-github",
            "verifier",
            "nonce-github",
            null), "127.0.0.1");

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The social login provider response could not be processed.");
        await AuditShouldContainAsync(context, "user.login.oidc.error", "GitHub profile response exceeded");
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

    private static (SqlOSAdminService admin, SqlOSOidcAuthService oidc) CreateServices(
        TestSqlOSInMemoryDbContext context,
        IHttpClientFactory? httpClientFactory = null)
    {
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = new SqlOSCryptoService(context, options, new EphemeralDataProtectionProvider());
        var admin = new SqlOSAdminService(context, options, crypto);
        var oidc = new SqlOSOidcAuthService(
            context,
            admin,
            crypto,
            httpClientFactory ?? new FakeOidcProviderHttpClientFactory(),
            NullLogger<SqlOSOidcAuthService>.Instance);
        return (admin, oidc);
    }

    private static Task<SqlOSOidcConnection> CreateGoogleConnectionAsync(SqlOSAdminService admin)
        => admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
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

    private static Task<SqlOSOidcConnection> CreateGitHubConnectionAsync(SqlOSAdminService admin)
        => admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
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

    private static Task<SqlOSOidcConnection> CreateCustomConnectionAsync(SqlOSAdminService admin)
        => admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
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

    private static async Task AuditShouldContainAsync(
        TestSqlOSInMemoryDbContext context,
        string eventType,
        string expectedDetail)
    {
        var auditDetails = await context.Set<SqlOSAuditEvent>()
            .Where(x => x.EventType == eventType)
            .Select(x => (x.DataJson ?? string.Empty) + (x.MetadataJson ?? string.Empty))
            .ToListAsync();

        auditDetails.Any(x => x.Contains(expectedDetail))
            .Should().BeTrue($"audit details were: {string.Join(" | ", auditDetails)}");
    }

    private static bool RequestUriContains(HttpRequestMessage request, string value)
        => request.RequestUri?.AbsoluteUri.Contains(value, StringComparison.OrdinalIgnoreCase) == true;

    private static HttpResponseMessage OversizedJsonResponse()
        => TextResponse(
            HttpStatusCode.OK,
            "{\"padding\":\"" + new string('a', 1024 * 1024) + "\"}",
            "application/json");

    private static HttpResponseMessage TextResponse(HttpStatusCode statusCode, string content, string mediaType)
        => new(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, mediaType)
        };

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

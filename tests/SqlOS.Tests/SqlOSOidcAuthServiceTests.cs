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

    private static (SqlOSAdminService admin, SqlOSOidcAuthService oidc) CreateServices(TestSqlOSInMemoryDbContext context)
    {
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = new SqlOSCryptoService(context, options, new EphemeralDataProtectionProvider());
        var admin = new SqlOSAdminService(context, options, crypto);
        var oidc = new SqlOSOidcAuthService(context, admin, crypto, new FakeOidcProviderHttpClientFactory(), NullLogger<SqlOSOidcAuthService>.Instance);
        return (admin, oidc);
    }

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

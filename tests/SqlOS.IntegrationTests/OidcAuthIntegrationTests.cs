using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
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
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, new EphemeralDataProtectionProvider());
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);

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
            null));

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
            null));

        updated.DisplayName.Should().Be("Google Login");
        updated.ClientId.Should().Be("google-client-updated");

        var disabled = await admin.SetOidcConnectionEnabledAsync(connection.Id, false);
        disabled.IsEnabled.Should().BeFalse();
    }

    [TestMethod]
    public async Task CompleteOidcLogin_ProvisionsExternalIdentity_AndIssuesTokens()
    {
        await ResetOidcStateAsync();

        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, new EphemeralDataProtectionProvider());
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var settings = new SqlOSSettingsService(AspireFixture.SharedContext, options);
        var auth = new SqlOSAuthService(AspireFixture.SharedContext, options, admin, crypto, settings);
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
    public async Task OidcLogin_Tokens_CanBeRefreshed()
    {
        await ResetOidcStateAsync();

        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, new EphemeralDataProtectionProvider());
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, options, crypto);
        var settings = new SqlOSSettingsService(AspireFixture.SharedContext, options);
        var auth = new SqlOSAuthService(AspireFixture.SharedContext, options, admin, crypto, settings);
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
    public async Task AppleAndCustomOidcConnections_Work_WithSharedRuntime()
    {
        await ResetOidcStateAsync();

        var options = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, options, new EphemeralDataProtectionProvider());
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

        return await admin.CreateClientAsync(new SqlOSCreateClientRequest(clientId, clientId, "sqlos-example", [$"https://app.example.local/callback/{clientId}"]));
    }

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

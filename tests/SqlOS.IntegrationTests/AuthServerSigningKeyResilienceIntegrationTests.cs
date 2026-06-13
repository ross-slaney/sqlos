using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class AuthServerSigningKeyResilienceIntegrationTests
{
    [TestMethod]
    public async Task AuthorizationCodeExchange_WithUnreadablePersistedSigningKey_RotatesAndIssuesTokens()
    {
        TestSqlOSDbContext? setupContext = null;
        string? connectionString = null;

        try
        {
            setupContext = await AspireFixture.CreateIsolatedAuthContextAsync("SqlOSKeyResilience");
            connectionString = setupContext.Database.GetConnectionString();
            connectionString.Should().NotBeNullOrWhiteSpace();

            var clientId = $"key-resilience-{Guid.NewGuid():N}"[..30];
            var redirectUri = $"https://client.example.test/{clientId}/callback";
            var setupOptions = CreateOptions(clientId, redirectUri, protectSigningKeys: true);
            var setupStack = BuildStack(
                setupContext,
                setupOptions,
                new EphemeralDataProtectionProvider());

            await setupStack.Admin.UpsertSeededClientsAsync();
            var protectedKey = await setupStack.Crypto.EnsureActiveSigningKeyAsync();
            protectedKey.PrivateKeyPem.Should().StartWith("dp:");

            var user = await setupStack.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Key Resilience User",
                $"key-resilience-{Guid.NewGuid():N}@example.com",
                "P@ssword123!"));
            var organization = await setupStack.Admin.CreateOrganizationAsync(
                new SqlOSCreateOrganizationRequest($"Key Resilience {Guid.NewGuid():N}", null));
            await setupStack.Admin.CreateMembershipAsync(
                organization.Id,
                new SqlOSCreateMembershipRequest(user.Id, "owner"));

            var codeVerifier = setupStack.Crypto.GenerateOpaqueToken();
            var authorizationRequest = await setupStack.AuthorizationServer.CreateAuthorizationRequestAsync(
                new SqlOSAuthorizeRequestInput(
                    "code",
                    clientId,
                    redirectUri,
                    "key-resilience-state",
                    "openid profile email offline_access",
                    setupStack.Crypto.CreatePkceCodeChallenge(codeVerifier),
                    "S256",
                    null,
                    user.DefaultEmail,
                    null,
                    null,
                    "hosted",
                    null));

            var redirect = await setupStack.AuthorizationServer.IssueAuthorizationRedirectAsync(
                authorizationRequest,
                user,
                organization.Id,
                "email_otp",
                CreateHttpContext());
            var code = QueryHelpers.ParseQuery(new Uri(redirect).Query)["code"].ToString();
            code.Should().NotBeNullOrWhiteSpace();

            await setupContext.DisposeAsync();
            setupContext = null;

            await using var replacementContext = CreateContext(connectionString!);
            var replacementStack = BuildStack(
                replacementContext,
                CreateOptions(clientId, redirectUri, protectSigningKeys: false),
                new EphemeralDataProtectionProvider());

            var tokenResult = await replacementStack.AuthorizationServer.ExchangeAuthorizationCodeAsync(
                new SqlOSTokenRequest(
                    "authorization_code",
                    code,
                    redirectUri,
                    clientId,
                    codeVerifier,
                    null,
                    null),
                CreateHttpContext());

            tokenResult.Tokens.AccessToken.Should().NotBeNullOrWhiteSpace();
            tokenResult.Tokens.RefreshToken.Should().NotBeNullOrWhiteSpace();
            tokenResult.Tokens.OrganizationId.Should().Be(organization.Id);

            var retiredKey = await replacementContext.Set<SqlOSSigningKey>()
                .SingleAsync(x => x.Id == protectedKey.Id);
            retiredKey.IsActive.Should().BeFalse();
            retiredKey.RetiredAt.Should().NotBeNull();

            var activeKey = await replacementContext.Set<SqlOSSigningKey>()
                .SingleAsync(x => x.IsActive);
            activeKey.Id.Should().NotBe(protectedKey.Id);
            activeKey.PrivateKeyPem.Should().Contain("BEGIN PRIVATE KEY");
            activeKey.PrivateKeyPem.Should().NotStartWith("dp:");
        }
        finally
        {
            if (setupContext != null)
            {
                await setupContext.DisposeAsync();
            }

            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                await using var cleanupContext = CreateContext(connectionString);
                await cleanupContext.Database.EnsureDeletedAsync();
            }
        }
    }

    [TestMethod]
    public async Task AuthorizationCodeExchange_DefaultSigningKeyStorage_SurvivesReplacementInstance()
    {
        TestSqlOSDbContext? setupContext = null;
        string? connectionString = null;

        try
        {
            setupContext = await AspireFixture.CreateIsolatedAuthContextAsync("SqlOSDefaultKey");
            connectionString = setupContext.Database.GetConnectionString();
            connectionString.Should().NotBeNullOrWhiteSpace();

            var clientId = $"default-key-{Guid.NewGuid():N}"[..30];
            var redirectUri = $"https://client.example.test/{clientId}/callback";
            var options = CreateOptions(clientId, redirectUri, protectSigningKeys: false);
            var setupStack = BuildStack(
                setupContext,
                options,
                new EphemeralDataProtectionProvider());

            await setupStack.Admin.UpsertSeededClientsAsync();
            var signingKey = await setupStack.Crypto.EnsureActiveSigningKeyAsync();
            signingKey.PrivateKeyPem.Should().Contain("BEGIN PRIVATE KEY");
            signingKey.PrivateKeyPem.Should().NotStartWith("dp:");

            var user = await setupStack.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Default Key User",
                $"default-key-{Guid.NewGuid():N}@example.com",
                "P@ssword123!"));
            var organization = await setupStack.Admin.CreateOrganizationAsync(
                new SqlOSCreateOrganizationRequest($"Default Key {Guid.NewGuid():N}", null));
            await setupStack.Admin.CreateMembershipAsync(
                organization.Id,
                new SqlOSCreateMembershipRequest(user.Id, "member"));

            var codeVerifier = setupStack.Crypto.GenerateOpaqueToken();
            var authorizationRequest = await setupStack.AuthorizationServer.CreateAuthorizationRequestAsync(
                new SqlOSAuthorizeRequestInput(
                    "code",
                    clientId,
                    redirectUri,
                    "default-key-state",
                    "openid profile email offline_access",
                    setupStack.Crypto.CreatePkceCodeChallenge(codeVerifier),
                    "S256",
                    null,
                    user.DefaultEmail,
                    null,
                    null,
                    "hosted",
                    null));

            var redirect = await setupStack.AuthorizationServer.IssueAuthorizationRedirectAsync(
                authorizationRequest,
                user,
                organization.Id,
                "password",
                CreateHttpContext());
            var code = QueryHelpers.ParseQuery(new Uri(redirect).Query)["code"].ToString();
            code.Should().NotBeNullOrWhiteSpace();

            await setupContext.DisposeAsync();
            setupContext = null;

            await using var replacementContext = CreateContext(connectionString!);
            var replacementStack = BuildStack(
                replacementContext,
                CreateOptions(clientId, redirectUri, protectSigningKeys: false),
                new EphemeralDataProtectionProvider());

            var tokenResult = await replacementStack.AuthorizationServer.ExchangeAuthorizationCodeAsync(
                new SqlOSTokenRequest(
                    "authorization_code",
                    code,
                    redirectUri,
                    clientId,
                    codeVerifier,
                    null,
                    null),
                CreateHttpContext());

            tokenResult.Tokens.AccessToken.Should().NotBeNullOrWhiteSpace();
            tokenResult.Tokens.RefreshToken.Should().NotBeNullOrWhiteSpace();

            var activeKeys = await replacementContext.Set<SqlOSSigningKey>()
                .Where(x => x.IsActive)
                .ToListAsync();
            activeKeys.Should().ContainSingle();
            activeKeys[0].Id.Should().Be(signingKey.Id);
        }
        finally
        {
            if (setupContext != null)
            {
                await setupContext.DisposeAsync();
            }

            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                await using var cleanupContext = CreateContext(connectionString);
                await cleanupContext.Database.EnsureDeletedAsync();
            }
        }
    }

    private static SqlOSAuthServerOptions CreateOptions(
        string clientId,
        string redirectUri,
        bool protectSigningKeys)
    {
        var options = new SqlOSAuthServerOptions
        {
            Issuer = AspireFixture.Options.Issuer,
            BasePath = AspireFixture.Options.BasePath,
            ProtectSigningKeysWithDataProtection = protectSigningKeys
        };
        options.SeedBrowserClient(clientId, $"Key Resilience Client {clientId}", redirectUri);
        options.SeedAuthPage(page =>
        {
            page.EnabledCredentialTypes = ["password", "email_otp"];
            page.EnablePasswordSignup = true;
        });
        return options;
    }

    private static ServiceStack BuildStack(
        TestSqlOSDbContext context,
        SqlOSAuthServerOptions optionsValue,
        IDataProtectionProvider dataProtectionProvider)
    {
        var options = Options.Create(optionsValue);
        var crypto = new SqlOSCryptoService(context, options, dataProtectionProvider);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender { IsConfigured = true };
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
        var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
        var authPageSession = new SqlOSAuthPageSessionService(context, crypto, settings);
        var authorizationServer = new SqlOSAuthorizationServerService(
            context,
            admin,
            auth,
            crypto,
            settings,
            authPageSession,
            options);
        return new ServiceStack(crypto, admin, authorizationServer);
    }

    private static TestSqlOSDbContext CreateContext(string connectionString)
    {
        var dbOptions = new DbContextOptionsBuilder<TestSqlOSDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new TestSqlOSDbContext(dbOptions);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("auth.example.test");
        context.Request.Headers.UserAgent = "SqlOS signing-key resilience integration test";
        return context;
    }

    private sealed record ServiceStack(
        SqlOSCryptoService Crypto,
        SqlOSAdminService Admin,
        SqlOSAuthorizationServerService AuthorizationServer);
}

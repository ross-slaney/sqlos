using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

/// <summary>
/// RFC 7636 §4.4.1: PKCE binds only authorization codes that were issued with a
/// code challenge. A confidential client that authorized without PKCE (authorize
/// enforces PKCE for public clients only) exchanges without a verifier, and a
/// verifier presented for a non-PKCE code fails closed.
/// </summary>
[TestClass]
public sealed class SqlOSPkceExchangeTests
{
    [TestMethod]
    public async Task NonPkceConfidentialCode_ExchangesWithoutVerifier()
    {
        await using var harness = await Harness.CreateAsync();
        var code = await harness.IssueNonPkceCodeAsync();

        var tokens = await harness.ExchangeAsync(code, codeVerifier: null);

        tokens.Tokens.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task NonPkceConfidentialCode_WithVerifier_FailsClosed()
    {
        await using var harness = await Harness.CreateAsync();
        var code = await harness.IssueNonPkceCodeAsync();

        var exchange = () => harness.ExchangeAsync(code, harness.CodeVerifier);

        await exchange.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(
                "PKCE verification failed.",
                "a verifier for a code issued without a challenge must fail closed instead of being ignored");
    }

    [TestMethod]
    public async Task PkceCode_WithoutVerifier_StillFails()
    {
        await using var harness = await Harness.CreateAsync();
        var code = await harness.IssuePkceCodeAsync();

        var exchange = () => harness.ExchangeAsync(code, codeVerifier: null);

        await exchange.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("PKCE verification failed.");
    }

    [TestMethod]
    public async Task PkceCode_WithMatchingVerifier_StillSucceeds()
    {
        await using var harness = await Harness.CreateAsync();
        var code = await harness.IssuePkceCodeAsync();

        var tokens = await harness.ExchangeAsync(code, harness.CodeVerifier);

        tokens.Tokens.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    private sealed class Harness : IAsyncDisposable
    {
        public const string ClientId = "pkce-confidential-client";
        public const string RedirectUri = "https://app.example.test/auth/callback";
        private const string Secret = "pkce-confidential-secret-with-256-bits-of-entropy-01234567";

        private Harness(
            TestSqlOSInMemoryDbContext context,
            SqlOSAuthorizationServerService authorization,
            SqlOSAdminService admin,
            DefaultHttpContext http,
            string codeVerifier,
            string codeChallenge,
            SqlOSUser user)
        {
            Context = context;
            Authorization = authorization;
            Admin = admin;
            Http = http;
            CodeVerifier = codeVerifier;
            CodeChallenge = codeChallenge;
            User = user;
        }

        public TestSqlOSInMemoryDbContext Context { get; }
        public SqlOSAuthorizationServerService Authorization { get; }
        public SqlOSAdminService Admin { get; }
        public DefaultHttpContext Http { get; }
        public string CodeVerifier { get; }
        public string CodeChallenge { get; }
        public SqlOSUser User { get; }

        public static async Task<Harness> CreateAsync()
        {
            var context = new TestSqlOSInMemoryDbContext(
                new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options);
            var optionsValue = new SqlOSAuthServerOptions
            {
                Issuer = "https://auth.example.test/sqlos/auth",
                PublicOrigin = "https://auth.example.test"
            };
            optionsValue.SeedClient(client =>
            {
                client.ClientId = ClientId;
                client.Name = "PKCE Confidential Client";
                client.RedirectUris = [RedirectUri];
                client.AllowedScopes = ["openid"];
                client.ClientType = "confidential";
                client.RequirePkce = false;
                client.ClientSecretResolver = () => Secret;
            });

            var options = Options.Create(optionsValue);
            var crypto = TestCryptoService.Create(context, options);
            var admin = new SqlOSAdminService(context, options, crypto);
            var emailSender = new TestAuthEmailSender();
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var authPageSession = new SqlOSAuthPageSessionService(context, crypto, settings);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
            var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
            var authorization = new SqlOSAuthorizationServerService(
                context,
                admin,
                auth,
                crypto,
                settings,
                authPageSession,
                options);
            var http = new DefaultHttpContext();
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("auth.example.test");
            var codeVerifier = crypto.GenerateOpaqueToken();

            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
            var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Grace Hopper",
                "grace@example.test",
                "P@ssword123!"));

            return new Harness(
                context,
                authorization,
                admin,
                http,
                codeVerifier,
                crypto.CreatePkceCodeChallenge(codeVerifier),
                user);
        }

        public Task<string> IssueNonPkceCodeAsync() => IssueCodeAsync(withPkce: false);

        public Task<string> IssuePkceCodeAsync() => IssueCodeAsync(withPkce: true);

        public async Task<SqlOSTokenEndpointResult> ExchangeAsync(string code, string? codeVerifier)
            => await Authorization.ExchangeAuthorizationCodeAsync(
                new SqlOSTokenRequest(
                    "authorization_code",
                    code,
                    RedirectUri,
                    ClientId,
                    codeVerifier,
                    null,
                    null),
                Http);

        private async Task<string> IssueCodeAsync(bool withPkce)
        {
            var request = await Authorization.CreateAuthorizationRequestAsync(new SqlOSAuthorizeRequestInput(
                "code",
                ClientId,
                RedirectUri,
                "state-pkce",
                "openid",
                withPkce ? CodeChallenge : null,
                withPkce ? "S256" : null,
                null,
                null,
                null,
                null,
                "hosted",
                null));
            var redirect = await Authorization.IssueAuthorizationRedirectAsync(
                request,
                User,
                null,
                "password",
                Http);
            var code = QueryHelpers.ParseQuery(new Uri(redirect).Query)["code"].ToString();
            code.Should().NotBeNullOrWhiteSpace();
            return code;
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }
}

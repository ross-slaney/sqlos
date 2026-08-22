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

[TestClass]
public sealed class SqlOSAuthorizationScopeTests
{
    [TestMethod]
    public async Task CreateAuthorizationRequestAsync_IntersectsRequestedScopesWithAllowlist()
    {
        await using var harness = await Harness.CreateAsync(["openid", "profile"]);

        var request = await harness.CreateAuthorizationRequestAsync("openid profile email todos.read");

        request.Scope.Should().Be("openid profile");
    }

    [TestMethod]
    public async Task CreateAuthorizationRequestAsync_EmptyAllowlistGrantsNoRequestedScopes()
    {
        await using var harness = await Harness.CreateAsync([]);

        var request = await harness.CreateAuthorizationRequestAsync("openid profile email");

        request.Scope.Should().BeEmpty();
    }

    [TestMethod]
    public async Task CreateAuthorizationRequestAsync_DropsScopesMissingFromAllowlist()
    {
        await using var harness = await Harness.CreateAsync(["openid"]);

        var request = await harness.CreateAuthorizationRequestAsync("openid email profile");

        request.Scope.Should().Be("openid");
    }

    [TestMethod]
    public async Task TokenResponse_OmitsOpenidWhenItIsNotAllowlisted()
    {
        await using var harness = await Harness.CreateAsync(["profile"]);
        var user = await harness.CreateUserAsync();

        var request = await harness.CreateAuthorizationRequestAsync("openid profile");
        request.Scope.Should().Be("profile");

        var redirect = await harness.Authorization.IssueAuthorizationRedirectAsync(
            request,
            user,
            null,
            "password",
            harness.Http);
        var code = QueryHelpers.ParseQuery(new Uri(redirect).Query)["code"].ToString();
        var grantedScope = QueryHelpers.ParseQuery(new Uri(redirect).Query)["scope"].ToString();
        grantedScope.Should().Be("profile");

        var tokens = await harness.Authorization.ExchangeAuthorizationCodeAsync(
            new SqlOSTokenRequest(
                "authorization_code",
                code,
                Harness.RedirectUri,
                Harness.ClientId,
                harness.CodeVerifier,
                null,
                null),
            harness.Http);

        tokens.Scope.Should().Be("profile");
    }

    [TestMethod]
    public async Task CreateAuthorizationRequestAsync_WhitespaceStateOverLimit_IsRejectedAsProtocolError()
    {
        await using var harness = await Harness.CreateAsync(["openid"]);

        var create = () => harness.Authorization.CreateAuthorizationRequestAsync(new SqlOSAuthorizeRequestInput(
            "code",
            Harness.ClientId,
            Harness.RedirectUri,
            new string(' ', 3000),
            "openid",
            harness.CodeChallenge,
            "S256",
            null,
            null,
            null,
            null,
            "hosted",
            null));

        await create.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(
                "State cannot exceed 2048 characters.",
                "a whitespace-only state past the NVARCHAR(2048) column limit must fail protocol validation, not database truncation");
    }

    [TestMethod]
    public async Task WhitespaceNonce_RoundTripsVerbatimIntoTheIdToken()
    {
        await using var harness = await Harness.CreateAsync(["openid"]);
        var user = await harness.CreateUserAsync();

        var request = await harness.Authorization.CreateAuthorizationRequestAsync(new SqlOSAuthorizeRequestInput(
            "code",
            Harness.ClientId,
            Harness.RedirectUri,
            "state-nonce",
            "openid",
            harness.CodeChallenge,
            "S256",
            null,
            null,
            null,
            " ",
            "hosted",
            null));
        request.Nonce.Should().Be(" ", "the authorization request must persist the nonce unmodified");

        var redirect = await harness.Authorization.IssueAuthorizationRedirectAsync(
            request,
            user,
            null,
            "password",
            harness.Http);
        var code = QueryHelpers.ParseQuery(new Uri(redirect).Query)["code"].ToString();

        var tokens = await harness.Authorization.ExchangeAuthorizationCodeAsync(
            new SqlOSTokenRequest(
                "authorization_code",
                code,
                Harness.RedirectUri,
                Harness.ClientId,
                harness.CodeVerifier,
                null,
                null),
            harness.Http);

        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler { MapInboundClaims = false }
            .ReadJwtToken(tokens.Tokens.IdToken);
        jwt.Payload["nonce"].Should().Be(
            " ",
            "OIDC Core requires the request nonce back in the ID token unmodified, whitespace included");
    }

    [TestMethod]
    public async Task DcrRegisteredClient_CannotBeGrantedArbitraryRequestedScopes()
    {
        await using var harness = await Harness.CreateAsync(["openid", "profile"]);
        var registration = await harness.RegisterDcrClientAsync();
        var stored = await harness.Context.Set<SqlOSClientApplication>()
            .SingleAsync(x => x.ClientId == registration.ClientId);
        stored.AllowedScopesJson.Should().Be("[]");

        var request = await harness.Authorization.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                registration.ClientId,
                "https://client.example.test/callback",
                "dcr-state",
                "openid email profile todos.admin",
                harness.CodeChallenge,
                "S256",
                null,
                null,
                null,
                null,
                "hosted",
                null));

        request.Scope.Should().BeEmpty();
    }

    private sealed class Harness : IAsyncDisposable
    {
        public const string ClientId = "scope-client";
        public const string RedirectUri = "https://app.example.test/auth/callback";

        private Harness(
            TestSqlOSInMemoryDbContext context,
            SqlOSAuthorizationServerService authorization,
            SqlOSAdminService admin,
            SqlOSDynamicClientRegistrationService dcr,
            SqlOSCryptoService crypto,
            DefaultHttpContext http,
            string codeVerifier,
            string codeChallenge)
        {
            Context = context;
            Authorization = authorization;
            Admin = admin;
            Dcr = dcr;
            Crypto = crypto;
            Http = http;
            CodeVerifier = codeVerifier;
            CodeChallenge = codeChallenge;
        }

        public TestSqlOSInMemoryDbContext Context { get; }
        public SqlOSAuthorizationServerService Authorization { get; }
        public SqlOSAdminService Admin { get; }
        public SqlOSDynamicClientRegistrationService Dcr { get; }
        public SqlOSCryptoService Crypto { get; }
        public DefaultHttpContext Http { get; }
        public string CodeVerifier { get; }
        public string CodeChallenge { get; }

        public static async Task<Harness> CreateAsync(IReadOnlyList<string> allowedScopes)
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
            optionsValue.ClientRegistration.Dcr.Enabled = true;
            optionsValue.SeedClient(client =>
            {
                client.ClientId = ClientId;
                client.Name = "Scope Client";
                client.RedirectUris = [RedirectUri];
                client.AllowedScopes = [.. allowedScopes];
                client.ClientType = "public_pkce";
                client.RequirePkce = true;
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
            var dcr = new SqlOSDynamicClientRegistrationService(
                context,
                options,
                crypto,
                admin,
                new SqlOSDynamicClientRegistrationRateLimiter());
            var http = new DefaultHttpContext();
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("auth.example.test");
            var codeVerifier = crypto.GenerateOpaqueToken();

            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();

            return new Harness(
                context,
                authorization,
                admin,
                dcr,
                crypto,
                http,
                codeVerifier,
                crypto.CreatePkceCodeChallenge(codeVerifier));
        }

        public Task<SqlOSAuthorizationRequest> CreateAuthorizationRequestAsync(string scope)
            => Authorization.CreateAuthorizationRequestAsync(new SqlOSAuthorizeRequestInput(
                "code",
                ClientId,
                RedirectUri,
                "state-scope",
                scope,
                CodeChallenge,
                "S256",
                null,
                null,
                null,
                null,
                "hosted",
                null));

        public Task<SqlOSUser> CreateUserAsync()
            => Admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Ada Lovelace",
                "ada@example.test",
                "P@ssword123!"));

        public Task<SqlOSDynamicClientRegistrationResponse> RegisterDcrClientAsync()
        {
            var http = new DefaultHttpContext();
            http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.20");
            return Dcr.RegisterAsync(new SqlOSDynamicClientRegistrationRequest
            {
                ClientName = "ChatGPT Client",
                RedirectUris = ["https://client.example.test/callback"],
                GrantTypes = ["authorization_code", "refresh_token"],
                ResponseTypes = ["code"],
                TokenEndpointAuthMethod = "none"
            }, http);
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }
}

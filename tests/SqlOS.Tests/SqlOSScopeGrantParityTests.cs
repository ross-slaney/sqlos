using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
public sealed class SqlOSScopeGrantParityTests
{
    private const string PkceChallenge = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Callback = "https://app.example.test/callback";
    private const string Audience = "https://api.example.test";
    private const string Secret = "parity-secret-with-at-least-256-bits-of-randomness-123456789";

    [TestMethod]
    [DataRow("openid profile", "openid profile", "openid profile", DisplayName = "allowed")]
    [DataRow("openid profile", "openid profile admin", "openid profile", DisplayName = "partially-allowed")]
    [DataRow("openid", "admin", "", DisplayName = "disallowed-empty-grant")]
    [DataRow("", "openid profile", "", DisplayName = "empty-allowlist")]
    public async Task SameAllowlistAndRequest_GrantsTheSameScopes_OnAuthorizeDeviceAndClientCredentials(
        string allowedCsv,
        string requested,
        string expected)
    {
        var allowed = SplitCsv(allowedCsv);
        await using var harness = await Harness.CreateAsync(allowed);

        var authorizationRequest = await harness.Authorization.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                "parity-web",
                Callback,
                "state-parity",
                requested,
                PkceChallenge,
                "S256",
                null,
                null,
                null,
                null,
                "hosted",
                null));

        var device = await harness.Device.StartAsync(
            new SqlOSDeviceAuthorizationStartRequest("parity-cli", requested, Audience),
            harness.Http);

        var clientCredentials = await harness.ClientCredentials.ExchangeAsync(
            "parity-worker",
            Secret,
            Audience,
            requested,
            harness.Http,
            default);

        var storedDevice = await harness.Context.Set<SqlOSDeviceAuthorization>()
            .SingleAsync(x => x.DeviceCodeHash == harness.Crypto.HashToken(device.DeviceCode));

        authorizationRequest.Scope.Should().Be(expected);
        storedDevice.Scope.Should().Be(expected);
        SqlOSScopePolicy.Join(clientCredentials.Scopes).Should().Be(expected);
    }

    [TestMethod]
    public async Task IssueAuthorizationRedirectAsync_IncludesGrantedScopeQueryParameter()
    {
        await using var harness = await Harness.CreateAsync(["openid", "profile", "offline_access"]);
        var user = await harness.SeedUserAsync();
        var request = await harness.Authorization.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                "parity-web",
                Callback,
                "state-scope-pin",
                "openid profile",
                PkceChallenge,
                "S256",
                null,
                null,
                null,
                null,
                "hosted",
                null));

        var redirectUrl = await harness.Authorization.IssueAuthorizationRedirectAsync(
            request,
            user,
            "org_parity",
            "password",
            harness.Http);

        redirectUrl.Should().StartWith($"{Callback}?");
        redirectUrl.Should().Contain("code=");
        redirectUrl.Should().Contain("state=state-scope-pin");
        redirectUrl.Should().Contain("scope=openid%20profile");
    }

    private static string[] SplitCsv(string value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(
            TestSqlOSInMemoryDbContext context,
            SqlOSAuthorizationServerService authorization,
            SqlOSDeviceAuthorizationService device,
            SqlOSClientCredentialsService clientCredentials,
            SqlOSCryptoService crypto,
            SqlOSAdminService admin,
            DefaultHttpContext http)
        {
            Context = context;
            Authorization = authorization;
            Device = device;
            ClientCredentials = clientCredentials;
            Crypto = crypto;
            Admin = admin;
            Http = http;
        }

        public TestSqlOSInMemoryDbContext Context { get; }
        public SqlOSAuthorizationServerService Authorization { get; }
        public SqlOSDeviceAuthorizationService Device { get; }
        public SqlOSClientCredentialsService ClientCredentials { get; }
        public SqlOSCryptoService Crypto { get; }
        public SqlOSAdminService Admin { get; }
        public DefaultHttpContext Http { get; }

        public static async Task<Harness> CreateAsync(IReadOnlyList<string> allowedScopes)
        {
            var context = new TestSqlOSInMemoryDbContext(
                new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options);
            var authOptions = new SqlOSAuthServerOptions
            {
                PublicOrigin = "https://auth.example.test",
                Issuer = "https://auth.example.test/sqlos/auth",
                DefaultAudience = Audience
            };
            authOptions.ResourceIndicators.Enabled = true;
            authOptions.SeedClient(client =>
            {
                client.ClientId = "parity-web";
                client.Name = "Parity Web";
                client.RedirectUris = [Callback];
                client.ClientType = "public_pkce";
                client.RequirePkce = true;
                client.AllowedScopes = [.. allowedScopes];
            });
            authOptions.SeedCliClient("parity-cli", "Parity CLI", Audience, [.. allowedScopes]);

            var options = Options.Create(authOptions);
            var crypto = TestCryptoService.Create(context, options);
            var admin = new SqlOSAdminService(context, options, crypto);
            var emailSender = new TestAuthEmailSender();
            var settings = new SqlOSSettingsService(context, options, emailSender);
            var authPageSession = new SqlOSAuthPageSessionService(context, crypto, settings);
            var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
            var mfaPolicy = new SqlOSMfaPolicyService(context, settings, options);
            var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp, mfaPolicyService: mfaPolicy);
            var authorization = new SqlOSAuthorizationServerService(
                context, admin, auth, crypto, settings, authPageSession, options, mfaPolicyService: mfaPolicy);
            var device = new SqlOSDeviceAuthorizationService(context, admin, auth, crypto, options, mfaPolicy);
            var clientCredentials = new SqlOSClientCredentialsService(context, crypto, admin, options);
            var http = new DefaultHttpContext();
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("auth.example.test");
            http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("127.0.0.1");

            await crypto.EnsureActiveSigningKeyAsync();
            await settings.EnsureDefaultAuthPageSettingsAsync();
            await settings.EnsureDefaultMfaSettingsAsync();
            await admin.UpsertSeededClientsAsync();

            context.Set<SqlOSClientApplication>().Add(new SqlOSClientApplication
            {
                Id = "app-parity-worker",
                ClientId = "parity-worker",
                Name = "Parity Worker",
                ClientType = "confidential",
                TokenEndpointAuthMethod = "client_secret_basic",
                GrantTypesJson = "[\"client_credentials\"]",
                AllowedScopesJson = System.Text.Json.JsonSerializer.Serialize(allowedScopes),
                Audience = Audience,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
            context.Set<SqlOSClientCredential>().Add(new SqlOSClientCredential
            {
                Id = "clcred-parity-worker",
                ClientApplicationId = "app-parity-worker",
                SecretHash = crypto.HashPassword(Secret),
                CreatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();

            return new Harness(context, authorization, device, clientCredentials, crypto, admin, http);
        }

        public async Task<SqlOSUser> SeedUserAsync()
        {
            var now = DateTime.UtcNow;
            var organization = new SqlOSOrganization
            {
                Id = "org_parity",
                Slug = "parity",
                Name = "Parity Org",
                CreatedAt = now,
                IsActive = true
            };
            var user = await Admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Ada Lovelace",
                "ada@example.test",
                "P@ssword123!"));
            Context.Set<SqlOSOrganization>().Add(organization);
            Context.Set<SqlOSMembership>().Add(new SqlOSMembership
            {
                OrganizationId = organization.Id,
                UserId = user.Id,
                Role = "admin",
                CreatedAt = now,
                IsActive = true
            });
            await Context.SaveChangesAsync();
            return user;
        }

        public async ValueTask DisposeAsync() => await Context.DisposeAsync();
    }
}

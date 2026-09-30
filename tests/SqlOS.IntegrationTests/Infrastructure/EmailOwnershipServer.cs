using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Email.Interfaces;
using SqlOS.Extensions;
using SqlOS.Services;

namespace SqlOS.IntegrationTests.Infrastructure;

/// <summary>
/// A real SqlOS host (AddSqlOS + MapAuthServer) on an isolated SQL database with password,
/// email-code, and magic-link sign-in enabled, SCIM on, a first-party browser client, a
/// capturing email sender, and the fake upstream OIDC provider. Email-ownership tests drive
/// the production services and routes through it so every assertion crosses a real SQL boundary.
/// </summary>
internal sealed class EmailOwnershipServer : IAsyncDisposable
{
    public const string ClientId = "email-ownership-client";
    public const string RedirectUri = "https://client.example.test/callback";
    public const string Origin = "https://email-ownership.integration.test";
    public const string ScimBasePath = "/sqlos/scim/v2";
    public const string Password = "P@ssword123!";

    private readonly WebApplication _app;

    private EmailOwnershipServer(WebApplication app, TestAuthEmailSender emails, HttpClient http)
    {
        _app = app;
        Emails = emails;
        Http = http;
    }

    public TestAuthEmailSender Emails { get; }

    public HttpClient Http { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<EmailOwnershipServer> CreateAsync(string databasePrefix)
    {
        string connectionString;
        await using (var bootstrap = await AspireFixture.CreateIsolatedAuthContextAsync(databasePrefix))
        {
            connectionString = bootstrap.Database.GetConnectionString()!;
        }

        var emails = new TestAuthEmailSender { IsConfigured = true };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<TestSqlOSDbContext>(options => options.UseTestProvider(connectionString));
        builder.Services.AddSqlOS<TestSqlOSDbContext>(options =>
        {
            options.AuthServer.PublicOrigin = Origin;
            options.AuthServer.Issuer = $"{Origin}/sqlos/auth";
            options.AuthServer.BasePath = "/sqlos/auth";
            options.AuthServer.EnableScim = true;
            options.AuthServer.ScimBasePath = ScimBasePath;
            options.AuthServer.SeedBrowserClient(ClientId, "Email Ownership Client", RedirectUri);
            options.AuthServer.SeedAuthPage(page =>
            {
                page.EnabledCredentialTypes = ["password", "email_otp", "magic_link"];
                page.EnablePasswordSignup = true;
            });
            options.AuthServer.EmailOtp.ResendCooldown = TimeSpan.Zero;
            options.AuthServer.EmailOtp.MaxChallengesPerIpPerHour = 10_000;
            options.AuthServer.MagicLink.ResendCooldown = TimeSpan.Zero;
            options.AuthServer.MagicLink.MaxLinksPerIpPerWindow = 10_000;
        });
        builder.Services.AddSingleton<IDataProtectionProvider>(AspireFixture.DataProtectionProvider);
        builder.Services.RemoveAll<ISqlOSAuthEmailSender>();
        builder.Services.AddSingleton<ISqlOSAuthEmailSender>(emails);
        builder.Services.RemoveAll<ISqlOSEmailSender>();
        builder.Services.AddSingleton<ISqlOSEmailSender>(emails);
        builder.Services.RemoveAll<IHttpClientFactory>();
        builder.Services.AddSingleton<IHttpClientFactory>(new FakeOidcProviderHttpClientFactory());
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.RemoveAll<IStartupFilter>();

        var app = builder.Build();
        app.MapAuthServer("/sqlos/auth");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SqlOSBootstrapper>().InitializeAsync();
        }

        await app.StartAsync();
        var http = app.GetTestClient();
        http.BaseAddress = new Uri(Origin);
        return new EmailOwnershipServer(app, emails, http);
    }

    public Scope CreateScope() => new(_app.Services.CreateAsyncScope());

    public const string GoogleCallbackUri = "https://app.example.local/callback/google";
    public const string CustomCallbackUri = "https://app.example.local/callback/custom";
    private string? _googleConnectionId;
    private string? _customConnectionId;

    /// <summary>The single Google connection of this host (SqlOS allows one per built-in provider).</summary>
    public async Task<string> EnsureGoogleConnectionAsync()
    {
        if (_googleConnectionId != null)
        {
            return _googleConnectionId;
        }

        await using var scope = CreateScope();
        var connection = await scope.Admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.Google,
            "Google",
            "google-client",
            "google-secret",
            [GoogleCallbackUri],
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
            null));
        _googleConnectionId = connection.Id;
        return connection.Id;
    }

    /// <summary>A custom OIDC connection that, like Microsoft or Apple, may omit a verified-email claim.</summary>
    public async Task<string> EnsureCustomConnectionAsync()
    {
        if (_customConnectionId != null)
        {
            return _customConnectionId;
        }

        await using var scope = CreateScope();
        var connection = await scope.Admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.Custom,
            "Upstream Directory",
            "custom-client",
            "custom-secret",
            [CustomCallbackUri],
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
            true));
        _customConnectionId = connection.Id;
        return connection.Id;
    }

    /// <summary>Completes an upstream login. <paramref name="mode"/> is "success" (verified) or "unverified".</summary>
    public async Task<SqlOSCompleteOidcAuthorizationResult> CompleteGoogleLoginAsync(string email, string mode = "success")
    {
        var connectionId = await EnsureGoogleConnectionAsync();
        await using var scope = CreateScope();
        return await scope.Oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connectionId,
            ClientId,
            GoogleCallbackUri,
            $"{mode}:{email}:nonce-google",
            "verifier",
            "nonce-google",
            null));
    }

    public async Task<SqlOSCompleteOidcAuthorizationResult> CompleteCustomLoginAsync(string email, string mode = "unverified")
    {
        var connectionId = await EnsureCustomConnectionAsync();
        await using var scope = CreateScope();
        return await scope.Oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connectionId,
            ClientId,
            CustomCallbackUri,
            $"{mode}:{email}:nonce-custom",
            "verifier",
            "nonce-custom",
            null));
    }

    /// <summary>The user a token response was issued to, read from its persisted session.</summary>
    public async Task<string> SessionUserIdAsync(SqlOSTokenResponse tokens)
    {
        await using var verification = CreateVerificationContext();
        return (await verification.Set<SqlOSSession>().SingleAsync(x => x.Id == tokens.SessionId)).UserId;
    }

    /// <summary>A fresh DbContext on the isolated database for verification queries.</summary>
    public TestSqlOSDbContext CreateVerificationContext()
    {
        var scope = _app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        return new TestSqlOSDbContext(
            new DbContextOptionsBuilder<TestSqlOSDbContext>()
                .UseTestProvider(context.Database.GetConnectionString()!)
                .Options);
    }

    public async Task<SqlOSUser> CreateUserAsync(string email, string? password = Password, bool verified = true, string displayName = "Account Owner")
    {
        await using var scope = CreateScope();
        var user = await scope.Admin.CreateUserAsync(new SqlOSCreateUserRequest(displayName, email, password));
        if (verified)
        {
            var row = await scope.Context.Set<SqlOSUserEmail>().SingleAsync(x => x.UserId == user.Id);
            row.IsVerified = true;
            row.VerifiedAt = DateTime.UtcNow;
            await scope.Context.SaveChangesAsync();
        }

        return user;
    }

    public async Task<SqlOSOrganization> CreateOrganizationAsync(string name, string? primaryDomain = null, params string[] verifiedDomains)
    {
        await using var scope = CreateScope();
        var organization = await scope.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest(name, null, primaryDomain));
        foreach (var domain in verifiedDomains)
        {
            scope.Context.Set<SqlOSOrganizationDomain>().Add(new SqlOSOrganizationDomain
            {
                Id = $"dom_{Guid.NewGuid():N}"[..28],
                OrganizationId = organization.Id,
                Domain = domain,
                Status = SqlOSOrganizationDomainStatuses.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                VerifiedAt = DateTime.UtcNow
            });
        }

        await scope.Context.SaveChangesAsync();
        return organization;
    }

    public async Task<(string ConnectionId, string Token)> CreateScimConnectionAsync(string organizationId)
    {
        await using var scope = CreateScope();
        var connection = await scope.Admin.CreateScimConnectionAsync(new SqlOSCreateScimConnectionRequest(
            organizationId,
            $"Directory {Guid.NewGuid():N}",
            Enabled: true));
        return (connection.ConnectionId, connection.Token);
    }

    public async Task<HttpResponseMessage> SendScimAsync(string token, HttpMethod method, string relativePath, JsonObject? body = null)
    {
        var request = new HttpRequestMessage(method, $"{ScimBasePath}{relativePath}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/scim+json");
        }

        return await Http.SendAsync(request);
    }

    public static JsonObject ScimUser(string externalId, string userName, string email, string displayName, bool active = true)
        => new()
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:User"),
            ["externalId"] = externalId,
            ["userName"] = userName,
            ["displayName"] = displayName,
            ["active"] = active,
            ["emails"] = new JsonArray(new JsonObject
            {
                ["value"] = email,
                ["type"] = "work",
                ["primary"] = true
            })
        };

    public static JsonObject ScimPatch(JsonObject value)
        => new()
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:api:messages:2.0:PatchOp"),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "replace",
                ["value"] = value
            })
        };

    public static DefaultHttpContext HttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = "EmailOwnershipIntegrationTest";
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse($"203.0.113.{Random.Shared.Next(1, 250)}");
        return context;
    }

    public IReadOnlyList<SqlOSAuthEmailMessage> MessagesTo(string recipient)
        => Emails.Messages.Where(x => string.Equals(x.To, recipient, StringComparison.Ordinal)).ToList();

    public static string ExtractCode(SqlOSAuthEmailMessage message)
        => Regex.Match(message.TextBody ?? message.HtmlBody, @"\b(\d{6})\b").Groups[1].Value;

    public static string ExtractToken(SqlOSAuthEmailMessage message)
        => Uri.UnescapeDataString(Regex.Match(message.TextBody ?? message.HtmlBody, @"token=([A-Za-z0-9_\-%]+)").Groups[1].Value);

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>().Database.EnsureDeletedAsync();
        }

        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    internal sealed class Scope : IAsyncDisposable
    {
        private readonly AsyncServiceScope _scope;

        public Scope(AsyncServiceScope scope)
        {
            _scope = scope;
        }

        public IServiceProvider Services => _scope.ServiceProvider;
        public TestSqlOSDbContext Context => Services.GetRequiredService<TestSqlOSDbContext>();
        public SqlOSAdminService Admin => Services.GetRequiredService<SqlOSAdminService>();
        public SqlOSAuthService Auth => Services.GetRequiredService<SqlOSAuthService>();
        public SqlOSEmailOtpService EmailOtp => Services.GetRequiredService<SqlOSEmailOtpService>();
        public SqlOSMagicLinkService MagicLink => Services.GetRequiredService<SqlOSMagicLinkService>();
        public SqlOSInvitationService Invitations => Services.GetRequiredService<SqlOSInvitationService>();
        public SqlOSOidcAuthService Oidc => Services.GetRequiredService<SqlOSOidcAuthService>();
        public SqlOSSamlService Saml => Services.GetRequiredService<SqlOSSamlService>();
        public SqlOSAuthorizationServerService AuthorizationServer => Services.GetRequiredService<SqlOSAuthorizationServerService>();

        public ValueTask DisposeAsync() => _scope.DisposeAsync();
    }
}

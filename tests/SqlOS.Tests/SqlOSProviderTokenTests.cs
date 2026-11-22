using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

/// <summary>
/// A social login connection can keep the provider's tokens (#557): stored on every sign-in when the
/// switch is on, read back through one method that refreshes when expired, and never seen anywhere else.
/// </summary>
[TestClass]
public sealed class SqlOSProviderTokenTests
{
    private const string GitHubCallback = "https://app.example.local/callback/github";
    private const string GoogleCallback = "https://app.example.local/callback/google";

    [TestMethod]
    public async Task GitHub_NewAndReturningPerson_StoreTheTokenEncrypted_AndReadItAsIs()
    {
        using var context = CreateContext();
        var (admin, oidc, _) = CreateServices(context);
        var connection = await CreateGitHubConnectionAsync(admin, storeProviderTokens: true);

        var first = await SignInGitHubAsync(oidc, connection, "success:octo@example.com:n1");
        first.UserCreated.Should().BeTrue();

        var identity = await context.Set<SqlOSExternalIdentity>().AsNoTracking().SingleAsync();
        identity.ProviderAccessTokenEncrypted.Should().NotBeNullOrWhiteSpace();
        identity.ProviderAccessTokenEncrypted.Should().NotContain("github|", "the token is protected at rest");
        identity.ProviderRefreshTokenEncrypted.Should().BeNull("GitHub OAuth Apps issue no refresh token");
        identity.ProviderAccessTokenExpiresAt.Should().BeNull("GitHub OAuth App tokens do not expire");

        var token = await oidc.GetProviderAccessTokenAsync(first.UserId, connection.Id);
        token.Should().NotBeNull();
        token!.AccessToken.Should().Be("github|success:octo@example.com:n1");
        token.ExpiresAt.Should().BeNull();
        token.Scopes.Should().Equal("read:user", "user:email");
        token.Subject.Should().Be("123456789");
        token.ProviderType.Should().Be(SqlOSOidcProviderType.GitHub);

        var second = await SignInGitHubAsync(oidc, connection, "success:octo@example.com:n2");
        second.UserCreated.Should().BeFalse();
        second.UserId.Should().Be(first.UserId);
        (await oidc.GetProviderAccessTokenAsync(first.UserId, connection.Id))!.AccessToken
            .Should().Be("github|success:octo@example.com:n2", "a returning person's sign-in replaces the stored token");
    }

    [TestMethod]
    public async Task GitHub_ReadBySeedKey_FindsTheSeededConnection()
    {
        using var context = CreateContext();
        var options = new SqlOSAuthServerOptions();
        options.SeedGitHubConnection("github-client", "github-secret", [GitHubCallback], storeProviderTokens: true);
        var (admin, oidc, _) = CreateServices(context, options);
        await admin.UpsertSeededOidcConnectionsAsync();
        var connection = await context.Set<SqlOSOidcConnection>().SingleAsync();

        var result = await SignInGitHubAsync(oidc, connection, "success:seeded@example.com:n");

        (await oidc.GetProviderAccessTokenAsync(result.UserId, "github"))!.AccessToken
            .Should().Be("github|success:seeded@example.com:n");
    }

    [TestMethod]
    public async Task GitHubAppUserToken_Expired_IsRefreshed_AndTheRotatedRefreshTokenIsKept()
    {
        using var context = CreateContext();
        var (admin, oidc, _) = CreateServices(context);
        var connection = await CreateGitHubConnectionAsync(admin, storeProviderTokens: true);
        var result = await SignInGitHubAsync(oidc, connection, "expiring:app-user@example.com:n");

        var fresh = await oidc.GetProviderAccessTokenAsync(result.UserId, connection.Id);
        fresh!.AccessToken.Should().Be("github|expiring:app-user@example.com:n");
        fresh.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddHours(8), TimeSpan.FromMinutes(1));

        await ExpireStoredTokenAsync(context);
        var refreshed = await oidc.GetProviderAccessTokenAsync(result.UserId, connection.Id);

        refreshed!.AccessToken.Should().StartWith("github-refreshed|");
        refreshed.ExpiresAt.Should().BeAfter(DateTime.UtcNow.AddHours(7));
        refreshed.Scopes.Should().BeEmpty("GitHub's refresh answered with an empty scope");
        var identity = await context.Set<SqlOSExternalIdentity>().AsNoTracking().SingleAsync();
        Unprotect(context, identity.ProviderRefreshTokenEncrypted!).Should().StartWith("refresh|rotated-");
    }

    [TestMethod]
    public async Task Google_NewAndReturningPerson_StoreRefreshTokenAndExpiry_AndRefreshWhenExpired()
    {
        using var context = CreateContext();
        var (admin, oidc, _) = CreateServices(context);
        var connection = await CreateGoogleConnectionAsync(admin, storeProviderTokens: true);

        var first = await SignInGoogleAsync(oidc, connection, "person@example.com", "n1");
        var identity = await context.Set<SqlOSExternalIdentity>().AsNoTracking().SingleAsync();
        identity.ProviderRefreshTokenEncrypted.Should().NotBeNull();
        identity.ProviderAccessTokenExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddHours(1), TimeSpan.FromMinutes(1));

        var token = await oidc.GetProviderAccessTokenAsync(first.UserId, connection.Id);
        token!.AccessToken.Should().Be("google|success:person@example.com:n1");
        token.Scopes.Should().Equal("openid", "email", "profile");

        var second = await SignInGoogleAsync(oidc, connection, "person@example.com", "n2");
        second.UserId.Should().Be(first.UserId);
        (await oidc.GetProviderAccessTokenAsync(first.UserId, connection.Id))!.AccessToken
            .Should().Be("google|success:person@example.com:n2");

        await ExpireStoredTokenAsync(context);
        var refreshed = await oidc.GetProviderAccessTokenAsync(first.UserId, connection.Id);
        refreshed!.AccessToken.Should().StartWith("google-refreshed|");
        refreshed.Scopes.Should().Equal(new[] { "openid", "email", "profile" }, "a refresh without a scope keeps the granted scopes");
    }

    [TestMethod]
    public async Task Google_StartAuthorization_AsksForOfflineAccess_OnlyWhenStoringTokens()
    {
        using var context = CreateContext();
        var (admin, oidc, _) = CreateServices(context);
        var connection = await CreateGoogleConnectionAsync(admin, storeProviderTokens: false);

        var without = await StartAsync(oidc, connection.Id, GoogleCallback);
        without.AuthorizationUrl.Should().NotContain("access_type=offline");

        connection.StoreProviderTokens = true;
        await context.SaveChangesAsync();
        var with = await StartAsync(oidc, connection.Id, GoogleCallback);
        with.AuthorizationUrl.Should().Contain("access_type=offline");
    }

    [TestMethod]
    public async Task RefreshRefused_ReturnsNull_ClearsTheTokens_AndAuditsWithoutThem()
    {
        using var context = CreateContext();
        var (admin, oidc, _) = CreateServices(context);
        var connection = await CreateGoogleConnectionAsync(admin, storeProviderTokens: true);
        var result = await SignInGoogleAsync(oidc, connection, "revoked@example.com", "n");

        var identity = await context.Set<SqlOSExternalIdentity>().SingleAsync();
        identity.ProviderRefreshTokenEncrypted = Protect(context, "refresh|revoked-by-the-person");
        identity.ProviderAccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(-5);
        await context.SaveChangesAsync();

        var token = await oidc.GetProviderAccessTokenAsync(result.UserId, connection.Id);

        token.Should().BeNull();
        var cleared = await context.Set<SqlOSExternalIdentity>().AsNoTracking().SingleAsync();
        cleared.ProviderAccessTokenEncrypted.Should().BeNull();
        cleared.ProviderRefreshTokenEncrypted.Should().BeNull();
        var audit = await context.Set<SqlOSAuditEvent>().SingleAsync(x => x.EventType == "user.provider_token.refresh_refused");
        audit.DataJson.Should().Contain("invalid_grant");
        audit.DataJson.Should().NotContain("refresh|");
        (await oidc.GetProviderAccessTokenAsync(result.UserId, connection.Id)).Should().BeNull();
    }

    [TestMethod]
    public async Task GitHubRefreshRefusedWithA200ErrorBody_ReturnsNull()
    {
        using var context = CreateContext();
        var (admin, oidc, _) = CreateServices(context);
        var connection = await CreateGitHubConnectionAsync(admin, storeProviderTokens: true);
        var result = await SignInGitHubAsync(oidc, connection, "expiring:revoked@example.com:n");
        var identity = await context.Set<SqlOSExternalIdentity>().SingleAsync();
        identity.ProviderRefreshTokenEncrypted = Protect(context, "refresh|revoked");
        identity.ProviderAccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(-5);
        await context.SaveChangesAsync();

        (await oidc.GetProviderAccessTokenAsync(result.UserId, connection.Id)).Should().BeNull();
        (await context.Set<SqlOSExternalIdentity>().AsNoTracking().SingleAsync()).ProviderAccessTokenEncrypted.Should().BeNull();
    }

    [TestMethod]
    public async Task ProviderUnreachableDuringRefresh_ReturnsNull_AndKeepsTheTokens()
    {
        using var context = CreateContext();
        var failRefresh = false;
        var factory = new FakeOidcProviderHttpClientFactory(request =>
            failRefresh && request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri.Contains("/token", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : null);
        var (admin, oidc, logger) = CreateServices(context, httpClientFactory: factory);
        var connection = await CreateGoogleConnectionAsync(admin, storeProviderTokens: true);
        var result = await SignInGoogleAsync(oidc, connection, "outage@example.com", "n");
        await ExpireStoredTokenAsync(context);
        failRefresh = true;

        var token = await oidc.GetProviderAccessTokenAsync(result.UserId, connection.Id);

        token.Should().BeNull();
        (await context.Set<SqlOSExternalIdentity>().AsNoTracking().SingleAsync()).ProviderRefreshTokenEncrypted
            .Should().NotBeNull("an outage is not a refusal");
        logger.Messages.Should().Contain(x => x.Contains("Refreshing the provider token", StringComparison.Ordinal));
        failRefresh = false;
        (await oidc.GetProviderAccessTokenAsync(result.UserId, connection.Id))!.AccessToken.Should().StartWith("google-refreshed|");
    }

    [TestMethod]
    public async Task ExpiredWithoutRefreshToken_ReturnsNull()
    {
        using var context = CreateContext();
        var (admin, oidc, _) = CreateServices(context);
        var connection = await CreateGoogleConnectionAsync(admin, storeProviderTokens: true);
        var result = await SignInGoogleAsync(oidc, connection, "no-refresh@example.com", "n");
        var identity = await context.Set<SqlOSExternalIdentity>().SingleAsync();
        identity.ProviderRefreshTokenEncrypted = null;
        identity.ProviderAccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(-5);
        await context.SaveChangesAsync();

        (await oidc.GetProviderAccessTokenAsync(result.UserId, connection.Id)).Should().BeNull();
    }

    [TestMethod]
    public async Task SwitchOff_StoresNothing_AndTurningItOffClearsWhatWasStored()
    {
        using var context = CreateContext();
        var (admin, oidc, _) = CreateServices(context);
        var connection = await CreateGitHubConnectionAsync(admin, storeProviderTokens: false);

        var result = await SignInGitHubAsync(oidc, connection, "success:off@example.com:n");
        var identity = await context.Set<SqlOSExternalIdentity>().AsNoTracking().SingleAsync();
        identity.ProviderAccessTokenEncrypted.Should().BeNull();
        identity.ProviderGrantedScopesJson.Should().BeNull();
        identity.ProviderTokensUpdatedAt.Should().BeNull();
        (await oidc.GetProviderAccessTokenAsync(result.UserId, connection.Id)).Should().BeNull();

        await admin.UpdateOidcConnectionAsync(connection.Id, GitHubUpdate(storeProviderTokens: true));
        await SignInGitHubAsync(oidc, connection, "success:off@example.com:n2");
        (await oidc.GetProviderAccessTokenAsync(result.UserId, connection.Id)).Should().NotBeNull();

        await admin.UpdateOidcConnectionAsync(connection.Id, GitHubUpdate(storeProviderTokens: false));
        (await context.Set<SqlOSExternalIdentity>().AsNoTracking().SingleAsync()).ProviderAccessTokenEncrypted
            .Should().BeNull("turning the switch off clears the stored tokens");
        (await oidc.GetProviderAccessTokenAsync(result.UserId, connection.Id)).Should().BeNull();
    }

    [TestMethod]
    public async Task SignInWithTheSwitchOff_ClearsTokensAnEarlierSignInStored()
    {
        using var context = CreateContext();
        var (admin, oidc, _) = CreateServices(context);
        var connection = await CreateGitHubConnectionAsync(admin, storeProviderTokens: true);
        await SignInGitHubAsync(oidc, connection, "success:later-off@example.com:n");
        connection.StoreProviderTokens = false; // flipped without the admin service, e.g. by a restored backup
        await context.SaveChangesAsync();

        await SignInGitHubAsync(oidc, connection, "success:later-off@example.com:n2");

        (await context.Set<SqlOSExternalIdentity>().AsNoTracking().SingleAsync()).ProviderAccessTokenEncrypted.Should().BeNull();
    }

    [TestMethod]
    public async Task DisabledConnection_ReturnsNull()
    {
        using var context = CreateContext();
        var (admin, oidc, _) = CreateServices(context);
        var connection = await CreateGitHubConnectionAsync(admin, storeProviderTokens: true);
        var result = await SignInGitHubAsync(oidc, connection, "success:disabled@example.com:n");

        await admin.SetOidcConnectionEnabledAsync(connection.Id, false);

        (await oidc.GetProviderAccessTokenAsync(result.UserId, connection.Id)).Should().BeNull();
    }

    [TestMethod]
    public async Task DeletingTheUser_ClearsTheTokens()
    {
        using var context = CreateContext();
        var (admin, oidc, _) = CreateServices(context);
        var connection = await CreateGitHubConnectionAsync(admin, storeProviderTokens: true);
        var result = await SignInGitHubAsync(oidc, connection, "success:leaving@example.com:n");

        await new SqlOSIdentityDeletionService(context, []).DeleteUserAsync(result.UserId, "operator", confirm: true);

        var identity = await context.Set<SqlOSExternalIdentity>().AsNoTracking().SingleAsync();
        identity.ProviderAccessTokenEncrypted.Should().BeNull();
        identity.ProviderRefreshTokenEncrypted.Should().BeNull();
        (await oidc.GetProviderAccessTokenAsync(result.UserId, connection.Id)).Should().BeNull();
    }

    [TestMethod]
    public async Task DeletingTheIdentity_DeletesTheTokens()
    {
        using var context = CreateContext();
        var (admin, oidc, _) = CreateServices(context);
        var connection = await CreateGitHubConnectionAsync(admin, storeProviderTokens: true);
        var result = await SignInGitHubAsync(oidc, connection, "success:unlinked@example.com:n");

        context.Set<SqlOSExternalIdentity>().Remove(await context.Set<SqlOSExternalIdentity>().SingleAsync());
        await context.SaveChangesAsync();

        (await oidc.GetProviderAccessTokenAsync(result.UserId, connection.Id)).Should().BeNull();
    }

    [TestMethod]
    public async Task OnePersonsRead_NeverReturnsAnotherPersonsToken()
    {
        using var context = CreateContext();
        var (admin, oidc, _) = CreateServices(context);
        var google = await CreateGoogleConnectionAsync(admin, storeProviderTokens: true);
        var gitHub = await CreateGitHubConnectionAsync(admin, storeProviderTokens: true);
        var alice = await SignInGoogleAsync(oidc, google, "alice@example.com", "n");
        var bob = await SignInGoogleAsync(oidc, google, "bob@example.com", "n");
        var carol = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Carol", "carol@example.com", "P@ssword123!"));

        (await oidc.GetProviderAccessTokenAsync(alice.UserId, google.Id))!.AccessToken.Should().Be("google|success:alice@example.com:n");
        (await oidc.GetProviderAccessTokenAsync(bob.UserId, google.Id))!.AccessToken.Should().Be("google|success:bob@example.com:n");
        (await oidc.GetProviderAccessTokenAsync(carol.Id, google.Id)).Should().BeNull("Carol never signed in with Google");
        (await oidc.GetProviderAccessTokenAsync(alice.UserId, gitHub.Id)).Should().BeNull("Alice never signed in with GitHub");
        (await oidc.GetProviderAccessTokenAsync("usr_missing", google.Id)).Should().BeNull();
        (await oidc.GetProviderAccessTokenAsync(alice.UserId, "no-such-connection")).Should().BeNull();
    }

    [TestMethod]
    public async Task Tokens_NeverReachLogsAuditRowsAdminResponsesOrToString()
    {
        using var context = CreateContext();
        var (admin, oidc, logger) = CreateServices(context);
        var connection = await CreateGoogleConnectionAsync(admin, storeProviderTokens: true);
        var result = await SignInGoogleAsync(oidc, connection, "secretive@example.com", "n");
        await ExpireStoredTokenAsync(context);
        var token = await oidc.GetProviderAccessTokenAsync(result.UserId, connection.Id);
        token.Should().NotBeNull();

        token!.ToString().Should().NotContain(token.AccessToken).And.Contain("[redacted]");
        var audits = await context.Set<SqlOSAuditEvent>().Select(x => (x.DataJson ?? "") + (x.MetadataJson ?? "")).ToListAsync();
        audits.Should().NotContain(x => x.Contains("google|", StringComparison.Ordinal) || x.Contains("refresh|", StringComparison.Ordinal) || x.Contains("google-refreshed|", StringComparison.Ordinal));
        logger.Messages.Should().NotContain(x => x.Contains("google|", StringComparison.Ordinal) || x.Contains("refresh|", StringComparison.Ordinal));
        var adminList = JsonSerializer.Serialize(await admin.ListOidcConnectionsAsync());
        adminList.Should().Contain("\"StoreProviderTokens\":true");
        adminList.Should().NotContain("refresh|").And.NotContain("google|");
    }

    private static async Task ExpireStoredTokenAsync(TestSqlOSInMemoryDbContext context)
    {
        var identity = await context.Set<SqlOSExternalIdentity>().SingleAsync();
        identity.ProviderAccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(30);
        await context.SaveChangesAsync();
    }

    private static Task<SqlOSStartOidcAuthorizationResult> StartAsync(SqlOSOidcAuthService oidc, string connectionId, string callback)
        => oidc.StartAuthorizationAsync(new SqlOSStartOidcAuthorizationRequest(
            connectionId,
            "person@example.com",
            "example-web",
            callback,
            "state",
            "nonce",
            "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk",
            "S256"));

    private static Task<SqlOSCompleteOidcAuthorizationResult> SignInGitHubAsync(SqlOSOidcAuthService oidc, SqlOSOidcConnection connection, string code)
        => oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id, "example-web", GitHubCallback, code, "verifier", "nonce", null));

    private static Task<SqlOSCompleteOidcAuthorizationResult> SignInGoogleAsync(SqlOSOidcAuthService oidc, SqlOSOidcConnection connection, string email, string nonce)
        => oidc.CompleteAuthorizationAsync(new SqlOSCompleteOidcAuthorizationRequest(
            connection.Id, "example-web", GoogleCallback, $"success:{email}:{nonce}", "verifier", nonce, null));

    private static Task<SqlOSOidcConnection> CreateGitHubConnectionAsync(SqlOSAdminService admin, bool storeProviderTokens)
        => admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.GitHub, "GitHub", "github-client", "github-secret", [GitHubCallback],
            true, null, null, null, null, null, null, null, null, null, null, null,
            StoreProviderTokens: storeProviderTokens));

    private static SqlOSUpdateOidcConnectionRequest GitHubUpdate(bool storeProviderTokens)
        => new("GitHub", "github-client", null, [GitHubCallback],
            true, null, null, null, null, null, null, null, null, null, null, null,
            StoreProviderTokens: storeProviderTokens);

    private static Task<SqlOSOidcConnection> CreateGoogleConnectionAsync(SqlOSAdminService admin, bool storeProviderTokens)
        => admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.Google, "Google", "google-client", "google-secret", [GoogleCallback],
            true, null, null, null, null, null, null, null, null, null, null, null,
            StoreProviderTokens: storeProviderTokens));

    private static readonly EphemeralDataProtectionProvider DataProtection = new();

    private static string Protect(TestSqlOSInMemoryDbContext context, string value)
        => TestCryptoService.Create(context, Options.Create(new SqlOSAuthServerOptions()), DataProtection).ProtectSecret(value);

    private static string Unprotect(TestSqlOSInMemoryDbContext context, string value)
        => TestCryptoService.Create(context, Options.Create(new SqlOSAuthServerOptions()), DataProtection).UnprotectSecret(value);

    private static (SqlOSAdminService admin, SqlOSOidcAuthService oidc, CapturingLogger logger) CreateServices(
        TestSqlOSInMemoryDbContext context,
        SqlOSAuthServerOptions? options = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        var wrapped = Options.Create(options ?? new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, wrapped, DataProtection);
        var admin = new SqlOSAdminService(context, wrapped, crypto);
        var logger = new CapturingLogger();
        var oidc = new SqlOSOidcAuthService(context, admin, crypto, httpClientFactory ?? new FakeOidcProviderHttpClientFactory(), logger);
        return (admin, oidc, logger);
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
        => new(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed class CapturingLogger : ILogger<SqlOSOidcAuthService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = new StringBuilder(formatter(state, exception));
            for (var current = exception; current != null; current = current.InnerException)
            {
                message.Append(" | ").Append(current.Message);
            }

            Messages.Add(message.ToString());
        }
    }
}

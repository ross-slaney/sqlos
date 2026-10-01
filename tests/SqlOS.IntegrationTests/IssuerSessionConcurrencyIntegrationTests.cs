using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
public sealed class IssuerSessionConcurrencyIntegrationTests
{
    [TestMethod]
    public async Task TwoRenewalsOfSameCookie_ThenLogout_InvalidatesEverySuccessor()
    {
        await using var setupContext = await AspireFixture.CreateIsolatedAuthContextAsync("AuthPageRenewRace");
        var connectionString = setupContext.Database.GetConnectionString()!;
        var options = CreateOptions();
        var setup = BuildStack(setupContext, options);
        await setup.Crypto.EnsureActiveSigningKeyAsync();
        var user = await setup.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Renew Race",
            $"renew-race-{Guid.NewGuid():N}@example.test",
            "P@ssword123!"));

        var seed = CreateHttpContext();
        seed.Request.Scheme = "https";
        await setup.IssuerSession.SignInAsync(seed, user, organizationId: null, "password");
        var cookieA = ReadIssuerSessionCookie(seed);

        await using var first = BuildStack(CreateContext(connectionString), options);
        await using var second = BuildStack(CreateContext(connectionString), options);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTask = Task.Run(async () =>
        {
            await ready.Task;
            var http = CreateHttpContext();
            http.Request.Headers.Cookie = $"sqlos_auth_page={cookieA}";
            await first.IssuerSession.SignInAsync(http, user, organizationId: null, "password");
            return ReadIssuerSessionCookie(http);
        });
        var secondTask = Task.Run(async () =>
        {
            await ready.Task;
            var http = CreateHttpContext();
            http.Request.Headers.Cookie = $"sqlos_auth_page={cookieA}";
            await second.IssuerSession.SignInAsync(http, user, organizationId: null, "password");
            return ReadIssuerSessionCookie(http);
        });
        ready.SetResult(true);
        var cookies = await Task.WhenAll(firstTask, secondTask);

        await using var logoutStack = BuildStack(CreateContext(connectionString), options);
        var logout = CreateHttpContext();
        logout.Request.Headers.Cookie = $"sqlos_auth_page={cookies[0]}";
        await logoutStack.IssuerSession.SignOutAsync(logout);

        await using var verify = CreateContext(connectionString);
        var family = await verify.Set<SqlOSIssuerSessionFamily>().SingleAsync();
        family.RevokedAt.Should().NotBeNull();

        var liveTokens = await verify.Set<SqlOSTemporaryToken>()
            .Where(x => x.Purpose == SqlOSTemporaryTokenKinds.IssuerSession.Purpose
                && x.UserId == user.Id
                && x.ConsumedAt == null)
            .ToListAsync();
        liveTokens.Should().BeEmpty();

        await using var replay = BuildStack(CreateContext(connectionString), options);
        foreach (var cookie in cookies.Append(cookieA))
        {
            var http = CreateHttpContext();
            http.Request.Headers.Cookie = $"sqlos_auth_page={cookie}";
            (await replay.IssuerSession.TryGetSessionAsync(http)).Should().BeNull();
        }
    }

    [TestMethod]
    public async Task LogoutThenRenewal_WithExplicitOrdering_LeavesNoUsableSuccessor()
    {
        await using var setupContext = await AspireFixture.CreateIsolatedAuthContextAsync("AuthPageLogoutRace");
        var connectionString = setupContext.Database.GetConnectionString()!;
        var options = CreateOptions();
        var setup = BuildStack(setupContext, options);
        await setup.Crypto.EnsureActiveSigningKeyAsync();
        var user = await setup.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Logout Race",
            $"logout-race-{Guid.NewGuid():N}@example.test",
            "P@ssword123!"));

        var seed = CreateHttpContext();
        await setup.IssuerSession.SignInAsync(seed, user, organizationId: null, "password");
        var cookieA = ReadIssuerSessionCookie(seed);

        await using var logoutStack = BuildStack(CreateContext(connectionString), options);
        var logout = CreateHttpContext();
        logout.Request.Headers.Cookie = $"sqlos_auth_page={cookieA}";
        await logoutStack.IssuerSession.SignOutAsync(logout);

        await using var renewalStack = BuildStack(CreateContext(connectionString), options);
        var renewal = CreateHttpContext();
        renewal.Request.Headers.Cookie = $"sqlos_auth_page={cookieA}";
        var act = () => renewalStack.IssuerSession.SignInAsync(
            renewal,
            user,
            organizationId: null,
            "password",
            authenticatedAt: null,
            continueExistingSession: true);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSIssuerSessionService.SessionNoLongerActiveMessage);
        renewal.Response.Headers.SetCookie.ToString().Should().NotContain("sqlos_auth_page=");

        await using var verify = CreateContext(connectionString);
        var family = await verify.Set<SqlOSIssuerSessionFamily>().SingleAsync();
        family.RevokedAt.Should().NotBeNull();
        (await verify.Set<SqlOSTemporaryToken>()
            .CountAsync(x => x.Purpose == SqlOSTemporaryTokenKinds.IssuerSession.Purpose
                && x.UserId == user.Id
                && x.ConsumedAt == null)).Should().Be(0);
    }

    [TestMethod]
    public async Task HostedAuthorize_TwoParallelRenewals_ThenLogout_ReplayFails()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("AuthPageHttpRace");
        await fixture.SetClientAllowedScopesAsync("openid");
        var started = await fixture.StartAuthorizeAsync("openid");
        var login = await fixture.SubmitPasswordLoginWithSessionAsync(started);
        using var tokens = await fixture.ExchangeAuthorizationCodeAsync(login.Code, started.CodeVerifier);
        tokens.RootElement.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();

        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () =>
        {
            await ready.Task;
            return await fixture.AuthorizeWithSessionAsync("openid", login.IssuerSessionCookie, prompt: "none");
        });
        var second = Task.Run(async () =>
        {
            await ready.Task;
            return await fixture.AuthorizeWithSessionAsync("openid", login.IssuerSessionCookie, prompt: "none");
        });
        ready.SetResult(true);
        using var firstRenewal = await first;
        using var secondRenewal = await second;
        var cookieB = HostedAuthorizeTokenFixture.TryExtractCookie(firstRenewal.Response, "sqlos_auth_page=");
        var cookieC = HostedAuthorizeTokenFixture.TryExtractCookie(secondRenewal.Response, "sqlos_auth_page=");
        cookieB.Should().NotBeNull();
        cookieC.Should().NotBeNull();

        using var loggedOut = await fixture.LogoutAsync(cookieB!);
        loggedOut.StatusCode.Should().NotBe(System.Net.HttpStatusCode.InternalServerError);

        foreach (var cookie in new[] { login.IssuerSessionCookie, cookieB!, cookieC! })
        {
            using var replay = await fixture.AuthorizeWithSessionAsync("openid", cookie, prompt: "none");
            replay.Response.Headers.Location.Should().NotBeNull();
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(replay.Response.Headers.Location!.Query);
            query["error"].ToString().Should().Be("login_required");
            query.ContainsKey("code").Should().BeFalse();
        }
    }

    private static SqlOSAuthServerOptions CreateOptions()
        => new()
        {
            PublicOrigin = "https://auth.example.test",
            Issuer = "https://auth.example.test/sqlos/auth",
            BasePath = "/sqlos/auth"
        };

    private static SessionStack BuildStack(TestSqlOSDbContext context, SqlOSAuthServerOptions optionsValue)
    {
        var options = Options.Create(optionsValue);
        var crypto = new SqlOSCryptoService(context, options, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(context, options, crypto);
        var settings = new SqlOSSettingsService(context, options, new TestAuthEmailSender { IsConfigured = true });
        var issuerSession = new SqlOSIssuerSessionService(context, crypto, settings);
        return new SessionStack(context, crypto, admin, issuerSession);
    }

    private static TestSqlOSDbContext CreateContext(string connectionString)
        => new(new DbContextOptionsBuilder<TestSqlOSDbContext>()
            .UseTestProvider(connectionString)
            .Options);

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("auth.example.test");
        return context;
    }

    private static string ReadIssuerSessionCookie(HttpContext httpContext)
    {
        var pair = httpContext.Response.Headers.SetCookie.ToString().Split(';', 2)[0];
        const string prefix = "sqlos_auth_page=";
        return pair.StartsWith(prefix, StringComparison.Ordinal)
            ? pair[prefix.Length..]
            : throw new InvalidOperationException($"Issuer-session sign-in did not set a cookie: {pair}");
    }

    private sealed class SessionStack(
        TestSqlOSDbContext context,
        SqlOSCryptoService crypto,
        SqlOSAdminService admin,
        SqlOSIssuerSessionService issuerSession) : IAsyncDisposable
    {
        public TestSqlOSDbContext Context { get; } = context;
        public SqlOSCryptoService Crypto { get; } = crypto;
        public SqlOSAdminService Admin { get; } = admin;
        public SqlOSIssuerSessionService IssuerSession { get; } = issuerSession;

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }
}

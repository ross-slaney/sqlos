using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSLogoutTests
{
    [TestMethod]
    public async Task ResolvePostLogoutRedirectAsync_AllowsConfiguredClientOrigin()
    {
        var (service, httpContext) = await CreateAuthorizationServerAsync();

        var redirect = await service.ResolvePostLogoutRedirectAsync(
            httpContext,
            "https://app.example.test/signed-out");

        redirect.Should().Be("https://app.example.test/signed-out");
    }

    [TestMethod]
    public async Task ResolvePostLogoutRedirectAsync_RejectsUnknownExternalOrigin()
    {
        var (service, httpContext) = await CreateAuthorizationServerAsync();

        var redirect = await service.ResolvePostLogoutRedirectAsync(
            httpContext,
            "https://evil.example.test/signed-out");

        redirect.Should().BeNull();
    }

    [TestMethod]
    [DataRow("dcr", "dcr_attacker")]
    [DataRow("cimd", "https://evil.example.test/client.json")]
    public async Task ResolvePostLogoutRedirectAsync_IgnoresDynamicallyRegisteredClientOrigins(
        string registrationSource,
        string clientId)
    {
        var fixture = await CreateLogoutFixtureAsync();
        await AddDynamicClientAsync(fixture.Context, clientId, registrationSource, "https://evil.example.test/cb");

        (await fixture.Service.ResolvePostLogoutRedirectAsync(fixture.HttpContext, "https://evil.example.test/login"))
            .Should().BeNull();
        (await fixture.Service.ResolvePostLogoutRedirectAsync(fixture.HttpContext, "https://evil.example.test/login", clientId))
            .Should().BeNull("an unauthenticated client_id must not re-enable a dynamic client's origin");
        (await fixture.Service.ResolvePostLogoutRedirectAsync(fixture.HttpContext, "/settings", clientId))
            .Should().Be("/settings");
    }

    [TestMethod]
    public async Task ResolvePostLogoutRedirectAsync_AllowsActiveAdminCreatedClientOrigin()
    {
        var fixture = await CreateLogoutFixtureAsync();
        await fixture.Admin.CreateClientAsync(new SqlOSCreateClientRequest(
            "dashboard-web",
            "Dashboard Web",
            "sqlos",
            ["https://dashboard-app.example.test/callback"]));

        (await fixture.Service.ResolvePostLogoutRedirectAsync(fixture.HttpContext, "https://dashboard-app.example.test/bye"))
            .Should().Be("https://dashboard-app.example.test/bye");
        (await fixture.Service.ResolvePostLogoutRedirectAsync(fixture.HttpContext, "https://dashboard-app.example.test/bye", "dashboard-web"))
            .Should().Be("https://dashboard-app.example.test/bye");
    }

    [TestMethod]
    public async Task ResolvePostLogoutRedirectAsync_StopsHonoringDisabledClientOrigin_UntilReEnabled()
    {
        var fixture = await CreateLogoutFixtureAsync();
        var dashboardClient = await fixture.Admin.CreateClientAsync(new SqlOSCreateClientRequest(
            "dashboard-web",
            "Dashboard Web",
            "sqlos",
            ["https://dashboard-app.example.test/callback"]));
        var seededClient = await fixture.Context.Set<SqlOSClientApplication>().SingleAsync(x => x.ClientId == "example-web");

        await fixture.Admin.DisableClientAsync(dashboardClient.Id, "test");
        await fixture.Admin.EmergencyDisableClientAsync(seededClient.Id);
        foreach (var (url, clientId) in new[]
        {
            ("https://dashboard-app.example.test/bye", (string?)null),
            ("https://dashboard-app.example.test/bye", "dashboard-web"),
            ("https://app.example.test/signed-out", null),
            ("https://app.example.test/signed-out", "example-web")
        })
        {
            (await fixture.Service.ResolvePostLogoutRedirectAsync(fixture.HttpContext, url, clientId))
                .Should().BeNull("{0} belongs to a disabled client", url);
        }

        await fixture.Admin.EnableClientAsync(dashboardClient.Id);
        await fixture.Admin.EmergencyEnableClientAsync(seededClient.Id);
        (await fixture.Service.ResolvePostLogoutRedirectAsync(fixture.HttpContext, "https://dashboard-app.example.test/bye"))
            .Should().Be("https://dashboard-app.example.test/bye");
        (await fixture.Service.ResolvePostLogoutRedirectAsync(fixture.HttpContext, "https://app.example.test/signed-out"))
            .Should().Be("https://app.example.test/signed-out");
    }

    [TestMethod]
    public async Task ResolvePostLogoutRedirectAsync_IgnoresInactiveClientOrigin()
    {
        var fixture = await CreateLogoutFixtureAsync();
        var client = await fixture.Context.Set<SqlOSClientApplication>().SingleAsync(x => x.ClientId == "example-web");
        client.IsActive = false;
        await fixture.Context.SaveChangesAsync();

        (await fixture.Service.ResolvePostLogoutRedirectAsync(fixture.HttpContext, "https://app.example.test/signed-out"))
            .Should().BeNull();
        (await fixture.Service.ResolvePostLogoutRedirectAsync(fixture.HttpContext, "https://app.example.test/signed-out", "example-web"))
            .Should().BeNull();
        (await fixture.Service.ResolvePostLogoutRedirectAsync(fixture.HttpContext, "https://other.example.test/signed-out"))
            .Should().Be("https://other.example.test/signed-out");
    }

    [TestMethod]
    public async Task ResolvePostLogoutRedirectAsync_ScopesExternalOriginsToNamedClient()
    {
        var fixture = await CreateLogoutFixtureAsync();

        (await fixture.Service.ResolvePostLogoutRedirectAsync(fixture.HttpContext, "https://app.example.test/signed-out", "example-web"))
            .Should().Be("https://app.example.test/signed-out");
        (await fixture.Service.ResolvePostLogoutRedirectAsync(fixture.HttpContext, "https://other.example.test/signed-out", "example-web"))
            .Should().BeNull();
        (await fixture.Service.ResolvePostLogoutRedirectAsync(fixture.HttpContext, "https://other.example.test/signed-out", "other-web"))
            .Should().Be("https://other.example.test/signed-out");
        (await fixture.Service.ResolvePostLogoutRedirectAsync(fixture.HttpContext, "https://auth.example.test/home", "example-web"))
            .Should().Be("https://auth.example.test/home");
    }

    [TestMethod]
    public async Task ResolvePostLogoutRedirectAsync_UnknownClientId_AllowsNoExternalOrigin()
    {
        var fixture = await CreateLogoutFixtureAsync();

        (await fixture.Service.ResolvePostLogoutRedirectAsync(fixture.HttpContext, "https://app.example.test/signed-out", "missing-client"))
            .Should().BeNull();
        (await fixture.Service.ResolvePostLogoutRedirectAsync(fixture.HttpContext, "/settings", "missing-client"))
            .Should().Be("/settings");
    }

    [TestMethod]
    [DataRow("/")]
    [DataRow("/settings")]
    [DataRow("/settings?tab=sessions")]
    [DataRow("/settings?tab=sessions#done")]
    [DataRow("/sqlos/auth/logged-out")]
    public async Task ResolvePostLogoutRedirectAsync_AllowsSafeLocalAbsolutePaths(string requestedUrl)
    {
        var (service, httpContext) = await CreateAuthorizationServerAsync();

        var redirect = await service.ResolvePostLogoutRedirectAsync(httpContext, requestedUrl);

        redirect.Should().Be(requestedUrl);
    }

    [TestMethod]
    public async Task ResolvePostLogoutRedirectAsync_TrimsSafeLocalAbsolutePaths()
    {
        var (service, httpContext) = await CreateAuthorizationServerAsync();

        var redirect = await service.ResolvePostLogoutRedirectAsync(httpContext, "  /settings  ");

        redirect.Should().Be("/settings");
    }

    [TestMethod]
    [DataRow("//example.invalid/path")]
    [DataRow("///example.invalid/path")]
    [DataRow("/\\example.invalid/path")]
    [DataRow("\\example.invalid/path")]
    [DataRow("\\\\example.invalid/path")]
    [DataRow("/\\/example.invalid/path")]
    [DataRow("/%2F%2Fexample.invalid/path")]
    [DataRow("/%2f%2fexample.invalid/path")]
    [DataRow("/%5C%5Cexample.invalid/path")]
    [DataRow("/%5cexample.invalid/path")]
    [DataRow("/%252F%252Fexample.invalid/path")]
    [DataRow("settings")]
    [DataRow("./settings")]
    [DataRow("../settings")]
    [DataRow("https://evil.example.test/signed-out")]
    [DataRow("http://evil.example.test/signed-out")]
    [DataRow("javascript:alert(1)")]
    [DataRow("data:text/html,phishing")]
    public async Task ResolvePostLogoutRedirectAsync_RejectsUnsafeLocalDestinations(string requestedUrl)
    {
        var (service, httpContext) = await CreateAuthorizationServerAsync();

        var redirect = await service.ResolvePostLogoutRedirectAsync(httpContext, requestedUrl);

        redirect.Should().BeNull();
    }

    [TestMethod]
    public async Task ResolvePostLogoutRedirectAsync_RejectsControlCharactersInLocalDestinations()
    {
        var (service, httpContext) = await CreateAuthorizationServerAsync();

        (await service.ResolvePostLogoutRedirectAsync(httpContext, "/settings\0")).Should().BeNull();
        (await service.ResolvePostLogoutRedirectAsync(httpContext, "/settings\n")).Should().BeNull();
        (await service.ResolvePostLogoutRedirectAsync(httpContext, "/settings\r\nLocation: https://evil.example.test"))
            .Should().BeNull();
        (await service.ResolvePostLogoutRedirectAsync(httpContext, "/settings\t")).Should().BeNull();
        (await service.ResolvePostLogoutRedirectAsync(httpContext, "/settings\u2028")).Should().BeNull();
        (await service.ResolvePostLogoutRedirectAsync(httpContext, "/%0d%0aLocation:%20https://evil.example.test"))
            .Should().BeNull();
    }

    [TestMethod]
    public async Task SignOutAsync_ConsumesIssuerSessionToken_AndDeletesCookie()
    {
        await using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var issuerSessionService = new SqlOSIssuerSessionService(context, crypto, settings);

        await crypto.EnsureActiveSigningKeyAsync();

        var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Alice", "alice@example.com", "P@ssword123!"));
        var rawToken = await crypto.CreateTemporaryTokenAsync(
            "auth_page_session",
            user.Id,
            null,
            null,
            new { AuthenticationMethod = "password" },
            TimeSpan.FromMinutes(30));

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Cookie = $"sqlos_auth_page={rawToken}";

        await issuerSessionService.SignOutAsync(httpContext);

        var storedToken = await crypto.FindTemporaryTokenAsync("auth_page_session", rawToken);
        storedToken.Should().BeNull();
        httpContext.Response.Headers.SetCookie.ToString().Should().Contain("sqlos_auth_page=");
    }

    [TestMethod]
    public async Task SignOutAsync_RevokesFamilySoPredecessorCookieCannotBeReused()
    {
        await using var context = CreateContext();
        var (issuerSession, crypto, user) = await CreateSessionStackAsync(context);

        var first = new DefaultHttpContext();
        first.Request.Scheme = "https";
        await issuerSession.SignInAsync(first, user, organizationId: null, "password");
        var cookieA = ReadIssuerSessionCookie(first);

        var renewal = new DefaultHttpContext();
        renewal.Request.Scheme = "https";
        renewal.Request.Headers.Cookie = $"sqlos_auth_page={cookieA}";
        await issuerSession.SignInAsync(renewal, user, organizationId: null, "password");
        var cookieB = ReadIssuerSessionCookie(renewal);

        var logout = new DefaultHttpContext();
        logout.Request.Scheme = "https";
        logout.Request.Headers.Cookie = $"sqlos_auth_page={cookieB}";
        await issuerSession.SignOutAsync(logout);

        (await crypto.FindTemporaryTokenAsync("auth_page_session", cookieA)).Should().BeNull();
        (await crypto.FindTemporaryTokenAsync("auth_page_session", cookieB)).Should().BeNull();

        var replayA = new DefaultHttpContext();
        replayA.Request.Headers.Cookie = $"sqlos_auth_page={cookieA}";
        (await issuerSession.TryGetSessionAsync(replayA)).Should().BeNull();

        var continueExisting = async () =>
        {
            var blocked = new DefaultHttpContext();
            blocked.Request.Scheme = "https";
            blocked.Request.Headers.Cookie = $"sqlos_auth_page={cookieA}";
            await issuerSession.SignInAsync(blocked, user, organizationId: null, "password", authenticatedAt: null, continueExistingSession: true);
        };
        await continueExisting.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(SqlOSIssuerSessionService.SessionNoLongerActiveMessage);

        var family = await context.Set<SqlOS.AuthServer.Models.SqlOSIssuerSessionFamily>().SingleAsync();
        family.RevokedAt.Should().NotBeNull();
        family.RevocationReason.Should().Be(SqlOSIssuerSessionService.LogoutReason);
    }

    [TestMethod]
    public async Task SignOutAsync_LeavesAnIndependentIssuerSessionUsable()
    {
        await using var context = CreateContext();
        var (issuerSession, crypto, user) = await CreateSessionStackAsync(context);

        var first = new DefaultHttpContext();
        first.Request.Scheme = "https";
        await issuerSession.SignInAsync(first, user, organizationId: null, "password");
        var cookieA = ReadIssuerSessionCookie(first);

        var second = new DefaultHttpContext();
        second.Request.Scheme = "https";
        await issuerSession.SignInAsync(second, user, organizationId: null, "password");
        var cookieB = ReadIssuerSessionCookie(second);

        var logout = new DefaultHttpContext();
        logout.Request.Scheme = "https";
        logout.Request.Headers.Cookie = $"sqlos_auth_page={cookieA}";
        await issuerSession.SignOutAsync(logout);

        (await crypto.FindTemporaryTokenAsync("auth_page_session", cookieA)).Should().BeNull();
        (await crypto.FindTemporaryTokenAsync("auth_page_session", cookieB)).Should().NotBeNull();

        var stillSignedIn = new DefaultHttpContext();
        stillSignedIn.Request.Headers.Cookie = $"sqlos_auth_page={cookieB}";
        (await issuerSession.TryGetSessionAsync(stillSignedIn)).Should().NotBeNull();
    }

    [TestMethod]
    public async Task SignOutAsync_RepeatedAndAbsentCookies_AreSafe()
    {
        await using var context = CreateContext();
        var (issuerSession, _, user) = await CreateSessionStackAsync(context);

        var signedIn = new DefaultHttpContext();
        signedIn.Request.Scheme = "https";
        await issuerSession.SignInAsync(signedIn, user, organizationId: null, "password");
        var cookie = ReadIssuerSessionCookie(signedIn);

        var firstLogout = new DefaultHttpContext();
        firstLogout.Request.Scheme = "https";
        firstLogout.Request.Headers.Cookie = $"sqlos_auth_page={cookie}";
        await issuerSession.SignOutAsync(firstLogout);

        var secondLogout = new DefaultHttpContext();
        secondLogout.Request.Scheme = "https";
        secondLogout.Request.Headers.Cookie = $"sqlos_auth_page={cookie}";
        await issuerSession.SignOutAsync(secondLogout);
        secondLogout.Response.Headers.SetCookie.ToString().Should().Contain("sqlos_auth_page=");

        var missing = new DefaultHttpContext();
        missing.Request.Scheme = "https";
        await issuerSession.SignOutAsync(missing);
        missing.Response.Headers.SetCookie.ToString().Should().Contain("sqlos_auth_page=");

        var invalid = new DefaultHttpContext();
        invalid.Request.Scheme = "https";
        invalid.Request.Headers.Cookie = "sqlos_auth_page=not-a-real-token";
        await issuerSession.SignOutAsync(invalid);
        invalid.Response.Headers.SetCookie.ToString().Should().Contain("sqlos_auth_page=");
    }

    [TestMethod]
    public async Task TryGetSessionAsync_RejectsLegacyUnlinkedIssuerSessionCookies()
    {
        await using var context = CreateContext();
        var (issuerSession, crypto, user) = await CreateSessionStackAsync(context);
        var rawToken = await crypto.CreateTemporaryTokenAsync(
            "auth_page_session",
            user.Id,
            null,
            null,
            new { AuthenticationMethod = "password" },
            TimeSpan.FromMinutes(30));

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Cookie = $"sqlos_auth_page={rawToken}";

        (await issuerSession.TryGetSessionAsync(httpContext)).Should().BeNull();
        (await crypto.FindTemporaryTokenAsync("auth_page_session", rawToken)).Should().BeNull();
    }

    [TestMethod]
    public async Task LogoutEndpoint_RedirectsSafeLocalReturnTo_BeforeEmittingLocation()
    {
        using var host = await StartLogoutHostAsync();
        var response = await host.GetTestClient().GetAsync(
            "/sqlos/auth/logout?returnTo=" + Uri.EscapeDataString("/settings?tab=sessions#done"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri("/settings?tab=sessions#done", UriKind.Relative));
        response.Headers.Location!.ToString().Should().NotContain("example.invalid");
    }

    [TestMethod]
    public async Task LogoutEndpoint_UsesPostLogoutRedirectUri_WhenReturnToIsAbsent()
    {
        using var host = await StartLogoutHostAsync();
        var response = await host.GetTestClient().GetAsync(
            "/sqlos/auth/logout?post_logout_redirect_uri=/signed-out");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri("/signed-out", UriKind.Relative));
    }

    [TestMethod]
    [DataRow("returnTo", "//example.invalid/path")]
    [DataRow("returnTo", "/\\example.invalid/path")]
    [DataRow("returnTo", "/%2F%2Fexample.invalid/path")]
    [DataRow("returnTo", "/%5Cexample.invalid/path")]
    [DataRow("returnTo", "https://evil.example.test/signed-out")]
    [DataRow("returnTo", "javascript:alert(1)")]
    [DataRow("post_logout_redirect_uri", "//example.invalid/path")]
    [DataRow("post_logout_redirect_uri", "/%2f%2fexample.invalid/path")]
    public async Task LogoutEndpoint_RejectsUnsafeDestinations_AndFallsBackToLoggedOut(
        string parameterName,
        string destination)
    {
        using var host = await StartLogoutHostAsync();
        var response = await host.GetTestClient().GetAsync(
            $"/sqlos/auth/logout?{parameterName}={Uri.EscapeDataString(destination)}");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri("/sqlos/auth/logged-out", UriKind.Relative));
        response.Headers.Location!.ToString().Should().NotContain("example.invalid");
        response.Headers.Location!.ToString().Should().NotContain("evil.example.test");
        response.Headers.Location!.ToString().Should().NotContain("javascript");
    }

    [TestMethod]
    public async Task LogoutEndpoint_AllowsConfiguredClientAbsoluteOrigin()
    {
        using var host = await StartLogoutHostAsync();
        var response = await host.GetTestClient().GetAsync(
            "/sqlos/auth/logout?returnTo=" + Uri.EscapeDataString("https://app.example.test/signed-out"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri("https://app.example.test/signed-out"));
    }

    [TestMethod]
    public async Task LogoutEndpoint_ClientIdScopesExternalOrigin()
    {
        using var host = await StartLogoutHostAsync();
        var client = host.GetTestClient();

        using var allowed = await client.GetAsync(
            "/sqlos/auth/logout?client_id=example-web&returnTo=" + Uri.EscapeDataString("https://app.example.test/signed-out"));
        allowed.Headers.Location.Should().Be(new Uri("https://app.example.test/signed-out"));

        using var unknown = await client.GetAsync(
            "/sqlos/auth/logout?client_id=missing-client&returnTo=" + Uri.EscapeDataString("https://app.example.test/signed-out"));
        unknown.Headers.Location.Should().Be(new Uri("/sqlos/auth/logged-out", UriKind.Relative));

        using var local = await client.GetAsync("/sqlos/auth/logout?client_id=missing-client&returnTo=/settings");
        local.Headers.Location.Should().Be(new Uri("/settings", UriKind.Relative));
    }

    [TestMethod]
    public async Task LogoutEndpoint_PrefersReturnTo_OverPostLogoutRedirectUri()
    {
        using var host = await StartLogoutHostAsync();
        var response = await host.GetTestClient().GetAsync(
            "/sqlos/auth/logout?returnTo=/safe&post_logout_redirect_uri=" +
            Uri.EscapeDataString("//example.invalid/path"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri("/safe", UriKind.Relative));
    }

    private static async Task<(SqlOSIssuerSessionService IssuerSession, SqlOSCryptoService Crypto, SqlOSUser User)> CreateSessionStackAsync(
        TestSqlOSInMemoryDbContext context)
    {
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var settings = new SqlOSSettingsService(context, options, new TestAuthEmailSender());
        var issuerSession = new SqlOSIssuerSessionService(context, crypto, settings);
        await crypto.EnsureActiveSigningKeyAsync();
        await settings.EnsureDefaultSettingsAsync();
        var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Alice", "alice@example.com", "P@ssword123!"));
        return (issuerSession, crypto, user);
    }

    private static string ReadIssuerSessionCookie(HttpContext httpContext)
    {
        var pair = httpContext.Response.Headers.SetCookie.ToString().Split(';', 2)[0];
        const string prefix = "sqlos_auth_page=";
        return pair.StartsWith(prefix, StringComparison.Ordinal)
            ? pair[prefix.Length..]
            : throw new InvalidOperationException($"Issuer-session sign-in did not set a cookie: {pair}");
    }

    private static async Task<(SqlOSAuthorizationServerService Service, DefaultHttpContext HttpContext)> CreateAuthorizationServerAsync()
    {
        var fixture = await CreateLogoutFixtureAsync();
        return (fixture.Service, fixture.HttpContext);
    }

    private sealed record LogoutFixture(
        TestSqlOSInMemoryDbContext Context,
        SqlOSAdminService Admin,
        SqlOSAuthorizationServerService Service,
        DefaultHttpContext HttpContext);

    private static async Task<LogoutFixture> CreateLogoutFixtureAsync()
    {
        var context = CreateContext();
        var authOptions = new SqlOSAuthServerOptions
        {
            Issuer = "https://auth.example.test/sqlos/auth",
            PublicOrigin = "https://auth.example.test"
        };
        authOptions.SeedBrowserClient("example-web", "Example Web", "https://app.example.test/auth/callback");
        authOptions.SeedBrowserClient("other-web", "Other Web", "https://other.example.test/auth/callback");
        var options = Options.Create(authOptions);
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var issuerSessionService = new SqlOSIssuerSessionService(context, crypto, settings);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
        var authService = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
        var authorizationServerService = new SqlOSAuthorizationServerService(
            context,
            admin,
            authService,
            crypto,
            settings,
            issuerSessionService,
            options);

        await crypto.EnsureActiveSigningKeyAsync();
        await admin.UpsertSeededClientsAsync();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("auth.example.test");
        return new LogoutFixture(context, admin, authorizationServerService, httpContext);
    }

    private static async Task<SqlOSClientApplication> AddDynamicClientAsync(
        TestSqlOSInMemoryDbContext context,
        string clientId,
        string registrationSource,
        string redirectUri)
    {
        var client = new SqlOSClientApplication
        {
            Id = $"cli_{Guid.NewGuid():N}",
            ClientId = clientId,
            Name = clientId,
            RegistrationSource = registrationSource,
            ConfigurationOwner = "dynamic",
            MetadataDocumentUrl = registrationSource == "cimd" ? clientId : null,
            RedirectUrisJson = $"[\"{redirectUri}\"]",
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        context.Set<SqlOSClientApplication>().Add(client);
        await context.SaveChangesAsync();
        return client;
    }

    private static async Task<IHost> StartLogoutHostAsync()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddLogging();
                    services.AddDbContext<TestSqlOSInMemoryDbContext>(db =>
                        db.UseInMemoryDatabase(databaseName));
                    services.AddSqlOS<TestSqlOSInMemoryDbContext>(sqlos =>
                    {
                        sqlos.AuthServer.Issuer = "https://auth.example.test/sqlos/auth";
                        sqlos.AuthServer.PublicOrigin = "https://auth.example.test";
                        sqlos.AuthServer.BasePath = "/sqlos/auth";
                        sqlos.AuthServer.SeedBrowserClient(
                            "example-web",
                            "Example Web",
                            "https://app.example.test/auth/callback");
                    });
                    foreach (var hostedService in services
                        .Where(x => x.ServiceType == typeof(IHostedService))
                        .ToList())
                    {
                        services.Remove(hostedService);
                    }

                    services.AddSingleton<ISqlOSAuthEmailSender>(new TestAuthEmailSender { IsConfigured = true });
                })
                .Configure(app =>
                {
                    app.UseRouting();
                }))
            .StartAsync();

        using (var scope = host.Services.CreateScope())
        {
            var crypto = scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>();
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            await crypto.EnsureActiveSigningKeyAsync();
            await admin.UpsertSeededClientsAsync();
        }

        return host;
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }
}

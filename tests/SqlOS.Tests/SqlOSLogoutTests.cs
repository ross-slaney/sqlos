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
    public async Task SignOutAsync_ConsumesAuthPageSessionToken_AndDeletesCookie()
    {
        await using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var authPageSessionService = new SqlOSAuthPageSessionService(context, crypto, settings);

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

        await authPageSessionService.SignOutAsync(httpContext);

        var storedToken = await crypto.FindTemporaryTokenAsync("auth_page_session", rawToken);
        storedToken.Should().BeNull();
        httpContext.Response.Headers.SetCookie.ToString().Should().Contain("sqlos_auth_page=");
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
    public async Task LogoutEndpoint_PrefersReturnTo_OverPostLogoutRedirectUri()
    {
        using var host = await StartLogoutHostAsync();
        var response = await host.GetTestClient().GetAsync(
            "/sqlos/auth/logout?returnTo=/safe&post_logout_redirect_uri=" +
            Uri.EscapeDataString("//example.invalid/path"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri("/safe", UriKind.Relative));
    }

    private static async Task<(SqlOSAuthorizationServerService Service, DefaultHttpContext HttpContext)> CreateAuthorizationServerAsync()
    {
        var context = CreateContext();
        var authOptions = new SqlOSAuthServerOptions
        {
            Issuer = "https://auth.example.test/sqlos/auth",
            PublicOrigin = "https://auth.example.test"
        };
        authOptions.SeedBrowserClient("example-web", "Example Web", "https://app.example.test/auth/callback");
        var options = Options.Create(authOptions);
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var authPageSessionService = new SqlOSAuthPageSessionService(context, crypto, settings);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
        var authService = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
        var authorizationServerService = new SqlOSAuthorizationServerService(
            context,
            admin,
            authService,
            crypto,
            settings,
            authPageSessionService,
            options);

        await crypto.EnsureActiveSigningKeyAsync();
        await admin.UpsertSeededClientsAsync();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("auth.example.test");
        return (authorizationServerService, httpContext);
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
                    app.UseEndpoints(endpoints => endpoints.MapAuthServer("/sqlos/auth"));
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

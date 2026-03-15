using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSLogoutTests
{
    [TestMethod]
    public async Task ResolvePostLogoutRedirectAsync_AllowsConfiguredClientOrigin()
    {
        await using var context = CreateContext();
        var authOptions = new SqlOSAuthServerOptions();
        authOptions.SeedBrowserClient("example-web", "Example Web", "https://app.example.test/auth/callback");
        var options = Options.Create(authOptions);
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var settings = new SqlOSSettingsService(context, options);
        var authPageSessionService = new SqlOSAuthPageSessionService(context, crypto, settings);
        var authService = new SqlOSAuthService(context, options, admin, crypto, settings);
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

        var redirect = await authorizationServerService.ResolvePostLogoutRedirectAsync(
            httpContext,
            "https://app.example.test/signed-out");

        redirect.Should().Be("https://app.example.test/signed-out");
    }

    [TestMethod]
    public async Task ResolvePostLogoutRedirectAsync_RejectsUnknownExternalOrigin()
    {
        await using var context = CreateContext();
        var authOptions = new SqlOSAuthServerOptions();
        authOptions.SeedBrowserClient("example-web", "Example Web", "https://app.example.test/auth/callback");
        var options = Options.Create(authOptions);
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var settings = new SqlOSSettingsService(context, options);
        var authPageSessionService = new SqlOSAuthPageSessionService(context, crypto, settings);
        var authService = new SqlOSAuthService(context, options, admin, crypto, settings);
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

        var redirect = await authorizationServerService.ResolvePostLogoutRedirectAsync(
            httpContext,
            "https://evil.example.test/signed-out");

        redirect.Should().BeNull();
    }

    [TestMethod]
    public async Task SignOutAsync_ConsumesAuthPageSessionToken_AndDeletesCookie()
    {
        await using var context = CreateContext();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var settings = new SqlOSSettingsService(context, options);
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

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }
}

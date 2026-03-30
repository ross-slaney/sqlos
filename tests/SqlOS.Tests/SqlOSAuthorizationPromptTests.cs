using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSAuthorizationPromptTests
{
    [TestMethod]
    public async Task BuildAuthorizationErrorRedirectAsync_CancelsRequest_AndPreservesState()
    {
        await using var context = CreateContext();
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedBrowserClient("example-web", "Example Web", "https://app.example.test/auth/callback");
        var options = Options.Create(optionsValue);
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

        var authorizationRequest = await authorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                "example-web",
                "https://app.example.test/auth/callback",
                "state-123",
                "openid profile email",
                "challenge-123",
                "S256",
                null,
                "alice@example.com",
                "none",
                null,
                "hosted",
                null));

        var redirectUrl = await authorizationServerService.BuildAuthorizationErrorRedirectAsync(
            authorizationRequest,
            "login_required",
            "The user is not signed in.");

        redirectUrl.Should().StartWith("https://app.example.test/auth/callback?");
        redirectUrl.Should().Contain("error=login_required");
        redirectUrl.Should().Contain("error_description=The%20user%20is%20not%20signed%20in.");
        redirectUrl.Should().Contain("state=state-123");

        authorizationRequest.CancelledAt.Should().NotBeNull();
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }
}

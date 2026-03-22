using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FluentAssertions;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSResourceBindingTests
{
    [TestMethod]
    public async Task CreateSessionTokensForUserAsync_UsesResourceAsAudience()
    {
        using var context = CreateContext();
        var (options, _, auth) = CreateAuthHarness(context);
        var user = await SeedUserAsync(context);
        var client = await SeedClientAsync(context, options.Value, "resource-client", "https://client.example.test/callback", "sqlos-api");

        var tokens = await auth.CreateSessionTokensForUserAsync(
            user,
            client,
            null,
            "password",
            "test-agent",
            "127.0.0.1",
            "https://api.example.test/resource");

        var session = await context.Set<SqlOSSession>().SingleAsync();
        session.Resource.Should().Be("https://api.example.test/resource");
        session.EffectiveAudience.Should().Be("https://api.example.test/resource");

        var validated = await auth.ValidateAccessTokenAsync(tokens.AccessToken, "https://api.example.test/resource");
        validated.Should().NotBeNull();

        var wrongAudience = await auth.ValidateAccessTokenAsync(tokens.AccessToken, "sqlos-api");
        wrongAudience.Should().BeNull();
    }

    [TestMethod]
    public async Task CreateSessionTokensForUserAsync_FallsBackToClientAudience_WhenResourceMissing()
    {
        using var context = CreateContext();
        var (options, _, auth) = CreateAuthHarness(context);
        var user = await SeedUserAsync(context);
        var client = await SeedClientAsync(context, options.Value, "audience-client", "https://client.example.test/callback", "sqlos-api");

        var tokens = await auth.CreateSessionTokensForUserAsync(
            user,
            client,
            null,
            "password",
            "test-agent",
            "127.0.0.1");

        var session = await context.Set<SqlOSSession>().SingleAsync();
        session.Resource.Should().BeNull();
        session.EffectiveAudience.Should().Be("sqlos-api");

        var validated = await auth.ValidateAccessTokenAsync(tokens.AccessToken, "sqlos-api");
        validated.Should().NotBeNull();
    }

    [TestMethod]
    public async Task RefreshAsync_PreservesOriginalResourceBinding()
    {
        using var context = CreateContext();
        var (options, _, auth) = CreateAuthHarness(context);
        var user = await SeedUserAsync(context);
        var client = await SeedClientAsync(context, options.Value, "refresh-client", "https://client.example.test/callback", "sqlos-api");

        var initial = await auth.CreateSessionTokensForUserAsync(
            user,
            client,
            null,
            "password",
            "test-agent",
            "127.0.0.1",
            "https://api.example.test/resource");

        var refreshed = await auth.RefreshAsync(new SqlOSRefreshRequest(initial.RefreshToken, null, "https://api.example.test/resource"));

        var validated = await auth.ValidateAccessTokenAsync(refreshed.AccessToken, "https://api.example.test/resource");
        validated.Should().NotBeNull();
    }

    [TestMethod]
    public async Task RefreshAsync_RejectsMismatchedResource()
    {
        using var context = CreateContext();
        var (options, _, auth) = CreateAuthHarness(context);
        var user = await SeedUserAsync(context);
        var client = await SeedClientAsync(context, options.Value, "refresh-client", "https://client.example.test/callback", "sqlos-api");

        var initial = await auth.CreateSessionTokensForUserAsync(
            user,
            client,
            null,
            "password",
            "test-agent",
            "127.0.0.1",
            "https://api.example.test/resource");

        var act = async () => await auth.RefreshAsync(new SqlOSRefreshRequest(initial.RefreshToken, null, "https://api.example.test/other"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Resource does not match*");
    }

    [TestMethod]
    public async Task ExchangeAuthorizationCodeAsync_RejectsMismatchedResource()
    {
        using var context = CreateContext();
        var (options, admin, auth, authorizationServer, crypto) = CreateAuthorizationHarness(context);
        var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Alice", "alice@example.com", "P@ssword123!"));
        await admin.UpsertSeededClientsAsync();

        var codeVerifier = crypto.GenerateOpaqueToken();
        var redirectUri = "https://client.example.test/callback";
        var authorizationRequest = await authorizationServer.CreateAuthorizationRequestAsync(new SqlOSAuthorizeRequestInput(
            "code",
            "resource-client",
            redirectUri,
            "state-123",
            "openid profile",
            crypto.CreatePkceCodeChallenge(codeVerifier),
            "S256",
            "https://api.example.test/resource",
            null,
            null,
            null,
            "hosted",
            null));

        var redirect = await authorizationServer.IssueAuthorizationRedirectAsync(
            authorizationRequest,
            user,
            null,
            "password",
            new DefaultHttpContext());
        var code = QueryHelpers.ParseQuery(new Uri(redirect).Query)["code"].ToString();

        var act = async () => await authorizationServer.ExchangeAuthorizationCodeAsync(
            new SqlOSTokenRequest(
                "authorization_code",
                code,
                redirectUri,
                "resource-client",
                codeVerifier,
                null,
                "https://api.example.test/other"),
            new DefaultHttpContext());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Resource does not match*");
    }

    [TestMethod]
    public async Task ExchangeAuthorizationCodeAsync_MintsAccessTokenForRequestedResource()
    {
        using var context = CreateContext();
        var (options, admin, auth, authorizationServer, crypto) = CreateAuthorizationHarness(context);
        var user = await admin.CreateUserAsync(new SqlOSCreateUserRequest("Alice", "alice@example.com", "P@ssword123!"));
        await admin.UpsertSeededClientsAsync();

        var codeVerifier = crypto.GenerateOpaqueToken();
        var redirectUri = "https://client.example.test/callback";
        var authorizationRequest = await authorizationServer.CreateAuthorizationRequestAsync(new SqlOSAuthorizeRequestInput(
            "code",
            "resource-client",
            redirectUri,
            "state-123",
            "openid profile",
            crypto.CreatePkceCodeChallenge(codeVerifier),
            "S256",
            "https://api.example.test/resource",
            null,
            null,
            null,
            "hosted",
            null));

        var redirect = await authorizationServer.IssueAuthorizationRedirectAsync(
            authorizationRequest,
            user,
            null,
            "password",
            new DefaultHttpContext());
        var code = QueryHelpers.ParseQuery(new Uri(redirect).Query)["code"].ToString();

        var result = await authorizationServer.ExchangeAuthorizationCodeAsync(
            new SqlOSTokenRequest(
                "authorization_code",
                code,
                redirectUri,
                "resource-client",
                codeVerifier,
                null,
                "https://api.example.test/resource"),
            new DefaultHttpContext());

        var validated = await auth.ValidateAccessTokenAsync(result.Tokens.AccessToken, "https://api.example.test/resource");
        validated.Should().NotBeNull();
        var session = await context.Set<SqlOSSession>().SingleAsync(x => x.Id == result.Tokens.SessionId);
        session.Resource.Should().Be("https://api.example.test/resource");
        session.EffectiveAudience.Should().Be("https://api.example.test/resource");
    }

    private static async Task<SqlOSUser> SeedUserAsync(TestSqlOSInMemoryDbContext context)
    {
        var user = new SqlOSUser
        {
            Id = "usr_test",
            DisplayName = "Alice",
            DefaultEmail = "alice@example.com",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.Set<SqlOSUser>().Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private static async Task<SqlOSClientApplication> SeedClientAsync(
        TestSqlOSInMemoryDbContext context,
        SqlOSAuthServerOptions optionsValue,
        string clientId,
        string redirectUri,
        string audience)
    {
        var client = new SqlOSClientApplication
        {
            Id = $"cli_{clientId}",
            ClientId = clientId,
            Name = clientId,
            Audience = audience,
            RedirectUrisJson = $"[\"{redirectUri}\"]",
            CreatedAt = DateTime.UtcNow,
            IsActive = true,
            RegistrationSource = "manual",
            TokenEndpointAuthMethod = "none",
            GrantTypesJson = "[\"authorization_code\",\"refresh_token\"]",
            ResponseTypesJson = "[\"code\"]"
        };
        context.Set<SqlOSClientApplication>().Add(client);
        await context.SaveChangesAsync();
        return client;
    }

    private static (IOptions<SqlOSAuthServerOptions> Options, SqlOSAdminService Admin, SqlOSAuthService Auth) CreateAuthHarness(TestSqlOSInMemoryDbContext context)
    {
        var optionsValue = new SqlOSAuthServerOptions
        {
            Issuer = "https://app.example.com/sqlos/auth",
            PublicOrigin = "https://app.example.com"
        };
        var options = Options.Create(optionsValue);
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var settings = new SqlOSSettingsService(context, options);
        var auth = new SqlOSAuthService(context, options, admin, crypto, settings);
        return (options, admin, auth);
    }

    private static (
        IOptions<SqlOSAuthServerOptions> Options,
        SqlOSAdminService Admin,
        SqlOSAuthService Auth,
        SqlOSAuthorizationServerService AuthorizationServer,
        SqlOSCryptoService Crypto) CreateAuthorizationHarness(TestSqlOSInMemoryDbContext context)
    {
        var optionsValue = new SqlOSAuthServerOptions
        {
            Issuer = "https://app.example.com/sqlos/auth",
            PublicOrigin = "https://app.example.com"
        };
        optionsValue.SeedBrowserClient("resource-client", "Resource Client", "https://client.example.test/callback");
        optionsValue.ClientSeeds[0].Audience = "sqlos-api";

        var options = Options.Create(optionsValue);
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var settings = new SqlOSSettingsService(context, options);
        var auth = new SqlOSAuthService(context, options, admin, crypto, settings);
        var authPageSession = new SqlOSAuthPageSessionService(context, crypto, settings);
        var authorizationServer = new SqlOSAuthorizationServerService(context, admin, auth, crypto, settings, authPageSession, options);
        return (options, admin, auth, authorizationServer, crypto);
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }
}

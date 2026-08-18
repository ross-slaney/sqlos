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
public sealed class SqlOSAuthorizationScopeIntersectionTests
{
    private const string ValidCodeChallenge = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string RedirectUri = "https://app.example.test/callback";
    private const string ClientId = "scope-web";

    [TestMethod]
    public async Task CreateAuthorizationRequest_AllowedScopes_GrantsRequestedScopes()
    {
        await using var harness = await CreateHarnessAsync(["openid", "profile", "email"]);

        var request = await harness.Authorization.CreateAuthorizationRequestAsync(
            Authorize("openid profile email"));

        request.Scope.Should().Be("openid profile email");
    }

    [TestMethod]
    public async Task CreateAuthorizationRequest_PartialAllowedScopes_DropsDisallowedScopes()
    {
        await using var harness = await CreateHarnessAsync(["openid", "profile"]);

        var request = await harness.Authorization.CreateAuthorizationRequestAsync(
            Authorize("openid profile email"));

        request.Scope.Should().Be("openid profile");
    }

    [TestMethod]
    public async Task CreateAuthorizationRequest_EmptyAllowedScopes_GrantsNoRequestedScopes()
    {
        await using var harness = await CreateHarnessAsync([]);

        var request = await harness.Authorization.CreateAuthorizationRequestAsync(
            Authorize("openid profile email custom.read"));

        request.Scope.Should().BeEmpty();
    }

    [TestMethod]
    public async Task CreateAuthorizationRequest_DuplicateScopeEntries_DeduplicatesGrantedScopes()
    {
        await using var harness = await CreateHarnessAsync(["openid", "profile", "email"]);

        var request = await harness.Authorization.CreateAuthorizationRequestAsync(
            Authorize("openid openid profile profile email"));

        request.Scope.Should().Be("openid profile email");
    }

    [TestMethod]
    public async Task CreateAuthorizationRequest_ThousandCharacterScope_PersistsAtColumnLimit()
    {
        var thousandCharacterScope = new string('a', 1000);
        await using var harness = await CreateHarnessAsync([thousandCharacterScope]);

        var request = await harness.Authorization.CreateAuthorizationRequestAsync(
            Authorize(thousandCharacterScope));

        request.Scope.Should().Be(thousandCharacterScope);
        request.Scope.Length.Should().Be(1000);
        (await harness.Context.Set<SqlOS.AuthServer.Models.SqlOSAuthorizationRequest>().SingleAsync())
            .Scope.Should().Be(thousandCharacterScope);
    }

    private static SqlOSAuthorizeRequestInput Authorize(string scope)
        => new(
            "code",
            ClientId,
            RedirectUri,
            "state-scope",
            scope,
            ValidCodeChallenge,
            "S256",
            null,
            null,
            null,
            null,
            "hosted",
            null);

    private static async Task<Harness> CreateHarnessAsync(string[] allowedScopes)
    {
        var context = new TestSqlOSInMemoryDbContext(
            new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedClient(client =>
        {
            client.ClientId = ClientId;
            client.Name = "Scope Web";
            client.RedirectUris = [RedirectUri];
            client.ClientType = "public_pkce";
            client.RequirePkce = true;
            client.IsFirstParty = true;
            client.AllowedScopes = [.. allowedScopes];
        });
        var options = Options.Create(optionsValue);
        var crypto = TestCryptoService.Create(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var emailSender = new TestAuthEmailSender();
        var settings = new SqlOSSettingsService(context, options, emailSender);
        var authPageSessionService = new SqlOSAuthPageSessionService(context, crypto, settings);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, emailSender, options);
        var authService = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp);
        var authorization = new SqlOSAuthorizationServerService(
            context,
            admin,
            authService,
            crypto,
            settings,
            authPageSessionService,
            options);
        await admin.UpsertSeededClientsAsync();
        return new Harness(context, authorization);
    }

    private sealed record Harness(
        TestSqlOSInMemoryDbContext Context,
        SqlOSAuthorizationServerService Authorization) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }
}

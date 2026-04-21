using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class HeadlessAuthIntegrationTests
{
    [TestMethod]
    public async Task CreateAuthorizationRequestAsync_PersistsHeadlessPresentationAndUiContext()
    {
        await using var fixture = await CreateFixtureAsync();

        var request = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "state-123",
                "openid profile email",
                "challenge-123",
                "S256",
                null,
                "alice@example.com",
                null,
                null,
                "headless",
                """{"lng":"en","template":"starter-pack"}"""));

        request.PresentationMode.Should().Be("headless");
        request.UiContextJson.Should().Contain("\"lng\":\"en\"");
        request.UiContextJson.Should().Contain("\"template\":\"starter-pack\"");
    }

    [TestMethod]
    public async Task GetRequestAsync_ReturnsProviders_AndConfiguredHeadlessApiBasePath()
    {
        await using var fixture = await CreateFixtureAsync(headless =>
        {
            headless.HeadlessApiBasePath = "/sqlos/auth/custom-headless";
        });

        await fixture.AdminService.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.Custom,
            $"Acme OIDC {Guid.NewGuid():N}",
            "custom-client",
            "custom-secret",
            ["https://app.example.local/callback/custom"],
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
            true,
            null,
            null,
            null));

        var authorizationRequest = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "state-456",
                "openid",
                "challenge-456",
                "S256",
                null,
                null,
                null,
                null,
                "headless",
                """{"lng":"en"}"""));

        var viewModel = await fixture.HeadlessAuthService.GetRequestAsync(
            authorizationRequest.Id,
            "signup",
            error: null,
            pendingToken: null,
            email: null,
            displayName: null);

        viewModel.View.Should().Be("signup");
        viewModel.HeadlessApiBasePath.Should().Be("/sqlos/auth/custom-headless");
        viewModel.ClientId.Should().Be(fixture.ClientId);
        viewModel.Providers.Should().ContainSingle(x => x.ProviderType == "Custom");
        viewModel.UiContext?["lng"]?.GetValue<string>().Should().Be("en");
    }

    [TestMethod]
    public async Task SignUpAsync_InvokesHook_AndReturnsAuthorizationRedirect()
    {
        JsonObject? capturedFields = null;

        await using var fixture = await CreateFixtureAsync(headless =>
        {
            headless.OnHeadlessSignupAsync = (ctx, _) =>
            {
                capturedFields = JsonNode.Parse(ctx.CustomFields.ToJsonString())?.AsObject();
                return Task.CompletedTask;
            };
        });

        var authorizationRequest = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "state-signup",
                "openid profile email",
                "challenge-signup",
                "S256",
                null,
                null,
                null,
                null,
                "headless",
                """{"lng":"en"}"""));

        var email = $"alice-{Guid.NewGuid():N}@example.com";
        var organizationName = $"Acme-{Guid.NewGuid():N}";
        var result = await fixture.HeadlessAuthService.SignUpAsync(
            CreateHttpContext(),
            new SqlOSHeadlessSignupRequest(
                authorizationRequest.Id,
                "Alice Example",
                email,
                "P@ssword123!",
                organizationName,
                new JsonObject
                {
                    ["firstName"] = "Alice",
                    ["lastName"] = "Example",
                    ["companyName"] = organizationName
                }));

        result.Type.Should().Be("redirect");
        result.RedirectUrl.Should().StartWith($"{fixture.RedirectUri}?");
        result.RedirectUrl.Should().Contain("code=");
        result.RedirectUrl.Should().Contain("state=state-signup");
        capturedFields?["companyName"]?.GetValue<string>().Should().Be(organizationName);

        (await fixture.Context.Set<SqlOSUserEmail>().CountAsync(x => x.Email == email)).Should().Be(1);
        (await fixture.Context.Set<SqlOSOrganization>().CountAsync(x => x.Name == organizationName)).Should().Be(1);
    }

    [TestMethod]
    public async Task SignUpAsync_WhenHookThrowsValidation_DoesNotPersistPartialSignup()
    {
        await using var fixture = await CreateFixtureAsync(headless =>
        {
            headless.OnHeadlessSignupAsync = (_, _) =>
                throw new SqlOSHeadlessValidationException(
                    "Validation failed.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["companyName"] = "Company name is already in use."
                    },
                    ["Please review the highlighted fields."]);
        });

        var authorizationRequest = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "state-validation",
                "openid profile email",
                "challenge-validation",
                "S256",
                null,
                null,
                null,
                null,
                "headless",
                """{"lng":"en"}"""));

        var email = $"alice-{Guid.NewGuid():N}@example.com";
        var organizationName = $"Acme-{Guid.NewGuid():N}";
        var result = await fixture.HeadlessAuthService.SignUpAsync(
            CreateHttpContext(),
            new SqlOSHeadlessSignupRequest(
                authorizationRequest.Id,
                "Alice Example",
                email,
                "P@ssword123!",
                organizationName,
                new JsonObject
                {
                    ["companyName"] = organizationName
                }));

        result.Type.Should().Be("view");
        result.ViewModel.Should().NotBeNull();
        result.ViewModel!.View.Should().Be("signup");
        result.ViewModel.FieldErrors.Should().ContainKey("companyName");
        result.ViewModel.Error.Should().Be("Please review the highlighted fields.");
        (await fixture.Context.Set<SqlOSUserEmail>().CountAsync(x => x.Email == email)).Should().Be(0);
        (await fixture.Context.Set<SqlOSOrganization>().CountAsync(x => x.Name == organizationName)).Should().Be(0);
    }

    [TestMethod]
    public async Task SignUpAsync_EstablishesReusableAuthPageSession()
    {
        await using var fixture = await CreateFixtureAsync();

        var authorizationRequest = await fixture.AuthorizationServerService.CreateAuthorizationRequestAsync(
            new SqlOSAuthorizeRequestInput(
                "code",
                fixture.ClientId,
                fixture.RedirectUri,
                "state-session",
                "openid profile email",
                "challenge-session",
                "S256",
                null,
                null,
                null,
                null,
                "headless",
                """{"lng":"en"}"""));

        var email = $"session-{Guid.NewGuid():N}@example.com";
        var httpContext = CreateHttpContext();
        var result = await fixture.HeadlessAuthService.SignUpAsync(
            httpContext,
            new SqlOSHeadlessSignupRequest(
                authorizationRequest.Id,
                "Session User",
                email,
                "P@ssword123!",
                "Session Org",
                new JsonObject()));

        result.Type.Should().Be("redirect");
        var authPageCookie = ExtractCookieValue(httpContext.Response.Headers.SetCookie.ToString(), "sqlos_auth_page");
        authPageCookie.Should().NotBeNullOrWhiteSpace();

        var followOnContext = CreateHttpContext();
        followOnContext.Request.Headers.Cookie = $"sqlos_auth_page={authPageCookie}";

        var session = await fixture.AuthPageSessionService.TryGetSessionAsync(followOnContext);
        session.Should().NotBeNull();
        session!.User.Id.Should().NotBeNullOrWhiteSpace();
        session.AuthenticationMethod.Should().Be("password");
    }

    [TestMethod]
    public async Task EnsureNativeHeadlessClientAllowedAsync_RejectsClientWithoutOptIn()
    {
        await using var fixture = await CreateFixtureAsync();

        var act = async () => await fixture.HeadlessAuthService.EnsureNativeHeadlessClientAllowedAsync(
            fixture.ClientId,
            fixture.RedirectUri);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("This client is not allowed to start native headless auth.");
    }

    [TestMethod]
    public async Task EnsureNativeHeadlessClientAllowedAsync_AllowsFirstPartyOptedInClient()
    {
        await using var fixture = await CreateFixtureAsync(allowNativeHeadlessAuth: true);

        var act = async () => await fixture.HeadlessAuthService.EnsureNativeHeadlessClientAllowedAsync(
            fixture.ClientId,
            fixture.RedirectUri);

        await act.Should().NotThrowAsync();
    }

    private static async Task<HeadlessFixture> CreateFixtureAsync(
        Action<SqlOSHeadlessAuthOptions>? configureHeadless = null,
        bool allowNativeHeadlessAuth = false)
    {
        var context = CreateContext();
        var clientId = $"headless-{Guid.NewGuid():N}";
        var redirectUri = $"https://client.example.test/{clientId}/callback";

        var optionsValue = new SqlOSAuthServerOptions
        {
            Issuer = AspireFixture.Options.Issuer,
            BasePath = AspireFixture.Options.BasePath
        };
        optionsValue.SeedBrowserClient(clientId, $"Headless Test {clientId}", redirectUri);
        optionsValue.ClientSeeds[0].AllowNativeHeadlessAuth = allowNativeHeadlessAuth;
        optionsValue.UseHeadlessAuthPage(headless =>
        {
            headless.BuildUiUrl = ctx =>
                $"https://app.example.test/authorize?request={Uri.EscapeDataString(ctx.RequestId ?? string.Empty)}&view={Uri.EscapeDataString(ctx.View)}";
        });
        configureHeadless?.Invoke(optionsValue.Headless);

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
        var discovery = new SqlOSHomeRealmDiscoveryService(context);
        var oidcAuthService = new SqlOSOidcAuthService(
            context,
            admin,
            crypto,
            new FakeOidcProviderHttpClientFactory(),
            NullLogger<SqlOSOidcAuthService>.Instance);
        var samlService = new SqlOSSamlService(context, options, admin, crypto);
        var oidcBrowserAuthService = new SqlOSOidcBrowserAuthService(
            context,
            admin,
            authService,
            authorizationServerService,
            crypto,
            oidcAuthService,
            options);
        var headlessAuthService = new SqlOSHeadlessAuthService(
            context,
            admin,
            authorizationServerService,
            discovery,
            oidcBrowserAuthService,
            samlService,
            settings,
            options);

        await crypto.EnsureActiveSigningKeyAsync();
        await admin.UpsertSeededClientsAsync();
        await settings.EnsureDefaultAuthPageSettingsAsync();

        return new HeadlessFixture(context, clientId, redirectUri, admin, authorizationServerService, headlessAuthService, authPageSessionService);
    }

    private static TestSqlOSDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSDbContext>()
            .UseSqlServer(AspireFixture.SqlConnectionString)
            .Options;
        return new TestSqlOSDbContext(options);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("tests");
        return context;
    }

    private static string? ExtractCookieValue(string setCookieHeader, string cookieName)
    {
        var marker = $"{cookieName}=";
        var start = setCookieHeader.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = setCookieHeader.IndexOf(';', start);
        if (end < 0)
        {
            end = setCookieHeader.Length;
        }

        return setCookieHeader[start..end];
    }

    private sealed record HeadlessFixture(
        TestSqlOSDbContext Context,
        string ClientId,
        string RedirectUri,
        SqlOSAdminService AdminService,
        SqlOSAuthorizationServerService AuthorizationServerService,
        SqlOSHeadlessAuthService HeadlessAuthService,
        SqlOSAuthPageSessionService AuthPageSessionService) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
        }
    }
}

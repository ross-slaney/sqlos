using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Services;

namespace SqlOS.IntegrationTests;

/// <summary>
/// Wire coverage for reloading a browser-headless consent view through
/// GET /headless/requests/{id}. Custom BuildUiUrl delegates may forward only the request id
/// and view — dropping the ConsentToken route field — so the server must re-mint a usable
/// consent token from what the same browser actually carries (auth-page session cookie or
/// the per-request sqlos_auth_continue_{hash} continuation cookie), while an anonymous
/// reload gets the consent view with no token at all.
/// </summary>
[TestClass]
public sealed class HeadlessConsentReloadIntegrationTests
{
    private const string TrustedOrigin = "https://auth.example.test";
    private const string FirstPartyClientId = "headless-first-party";
    private const string FirstPartyRedirect = "https://app.example.test/callback";
    private const string ThirdPartyClientId = "headless-third-party";
    private const string ThirdPartyRedirect = "https://third.example.test/callback";
    private const string Password = "P@ssword123!";

    [TestMethod]
    public async Task ConsentReload_WithLiveAuthPageSession_ReturnsUsableToken_AnonymousReloadGetsNone()
    {
        await using var host = await HeadlessHost.CreateAsync();

        // A first-party headless login establishes the auth-page session cookie.
        var firstPartyRequestId = await StartHeadlessAuthorizeAsync(host, FirstPartyClientId, FirstPartyRedirect, "openid");
        using var login = await host.Client.PostAsJsonAsync(
            "/sqlos/auth/headless/password/login",
            new { requestId = firstPartyRequestId, email = host.Email, password = Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        loginBody.RootElement.GetProperty("type").GetString().Should().Be("redirect");
        loginBody.RootElement.GetProperty("redirectUrl").GetString().Should().Contain("code=");
        var authPageCookie = ExtractCookie(login, "sqlos_auth_page=");

        // Silent SSO into the third-party client rides the session straight to consent; the
        // custom BuildUiUrl delegate drops the ConsentToken route field.
        using var authorize = await SendWithCookieAsync(
            host,
            BuildAuthorizeUrl(ThirdPartyClientId, ThirdPartyRedirect, "openid todo:read"),
            authPageCookie);
        authorize.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var uiLocation = authorize.Headers.Location!;
        uiLocation.AbsoluteUri.Should().StartWith("https://app.example.test/auth-ui");
        var uiQuery = QueryHelpers.ParseQuery(uiLocation.Query);
        uiQuery["view"].ToString().Should().Be("consent");
        uiQuery.ContainsKey("consentToken").Should().BeFalse("the delegate under test forwards only requestId and view");
        var requestId = uiQuery["requestId"].ToString();
        requestId.Should().NotBeNullOrWhiteSpace();

        // Reload in the SAME browser: the consent view comes back with scopes and a token.
        using var reload = await SendWithCookieAsync(
            host,
            $"/sqlos/auth/headless/requests/{requestId}?view=consent",
            authPageCookie);
        reload.StatusCode.Should().Be(HttpStatusCode.OK);
        using var reloadBody = JsonDocument.Parse(await reload.Content.ReadAsStringAsync());
        reloadBody.RootElement.GetProperty("view").GetString().Should().Be("consent");
        reloadBody.RootElement.GetProperty("consentScopes").EnumerateArray()
            .Select(x => x.GetProperty("scope").GetString())
            .Should().BeEquivalentTo("openid", "todo:read");
        reloadBody.RootElement.GetProperty("consentScopes").EnumerateArray()
            .Single(x => x.GetProperty("scope").GetString() == "todo:read")
            .GetProperty("displayName").GetString().Should().Be("Read your tasks");
        var consentToken = reloadBody.RootElement.GetProperty("consentToken").GetString();
        consentToken.Should().NotBeNullOrWhiteSpace();

        // A different (anonymous) browser gets the consent view WITHOUT a token.
        using var anonymousReload = await host.Client.GetAsync(
            $"/sqlos/auth/headless/requests/{requestId}?view=consent");
        anonymousReload.StatusCode.Should().Be(HttpStatusCode.OK);
        using var anonymousBody = JsonDocument.Parse(await anonymousReload.Content.ReadAsStringAsync());
        anonymousBody.RootElement.GetProperty("view").GetString().Should().Be("consent");
        anonymousBody.RootElement.GetProperty("consentToken").ValueKind.Should().Be(
            JsonValueKind.Null,
            "an anonymous reload must fail closed with no consent token");

        // The re-minted token is usable: approval completes the request with a code.
        using var approve = await host.Client.PostAsJsonAsync(
            "/sqlos/auth/headless/consent/approve",
            new { requestId, consentToken });
        approve.StatusCode.Should().Be(HttpStatusCode.OK);
        using var approveBody = JsonDocument.Parse(await approve.Content.ReadAsStringAsync());
        approveBody.RootElement.GetProperty("type").GetString().Should().Be("redirect");
        var redirectUrl = approveBody.RootElement.GetProperty("redirectUrl").GetString();
        redirectUrl.Should().StartWith(ThirdPartyRedirect);
        redirectUrl.Should().Contain("code=");

        await using var scope = host.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await db.Set<SqlOSConsentGrant>().SingleAsync(x => x.UserId == host.UserId && x.RevokedAt == null))
            .Scope.Should().Contain("todo:read");
    }

    [TestMethod]
    public async Task ConsentReload_WithContinuationCookie_ReturnsFreshUsableToken()
    {
        await using var host = await HeadlessHost.CreateAsync();

        // Password login for the third-party client stops at consent before any auth-page
        // session exists (consent is the first interstitial).
        var requestId = await StartHeadlessAuthorizeAsync(host, ThirdPartyClientId, ThirdPartyRedirect, "openid todo:read");
        using var login = await host.Client.PostAsJsonAsync(
            "/sqlos/auth/headless/password/login",
            new { requestId, email = host.Email, password = Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        loginBody.RootElement.GetProperty("type").GetString().Should().Be("view");
        var viewModel = loginBody.RootElement.GetProperty("viewModel");
        viewModel.GetProperty("view").GetString().Should().Be("consent");
        var originalConsentToken = viewModel.GetProperty("consentToken").GetString();
        originalConsentToken.Should().NotBeNullOrWhiteSpace();
        TryExtractCookie(login, "sqlos_auth_page=").Should().BeNull(
            "consent runs before the auth-page session is signed in");

        // Social-login callbacks persist the pending interaction in the per-request
        // sqlos_auth_continue_{hash} cookie; mint that continuation for this consent token
        // to model the redirected browser's state.
        string continuationCookie;
        await using (var scope = host.App.Services.CreateAsyncScope())
        {
            var authorizationServer = scope.ServiceProvider.GetRequiredService<SqlOSAuthorizationServerService>();
            var continuationContext = new DefaultHttpContext();
            continuationContext.Request.Scheme = "https";
            continuationContext.Request.Host = new HostString("auth.example.test");
            await authorizationServer.CreateAuthorizationContinuationRedirectAsync(
                new SqlOSAuthorizationRequestLoginResult(
                    null,
                    false,
                    null,
                    Array.Empty<SqlOSOrganizationOption>(),
                    AuthorizationRequestId: requestId,
                    RequiresConsent: true,
                    ConsentToken: originalConsentToken),
                continuationContext);
            var setCookie = continuationContext.Response.Headers.SetCookie.ToString();
            setCookie.Should().Contain(
                "path=/sqlos/auth;",
                "the continuation cookie must be scoped so the browser also sends it to " +
                "GET /sqlos/auth/headless/requests/{id}, not just /sqlos/auth/continue");
            continuationCookie = continuationContext.Response.Headers.SetCookie
                .Select(value => value!.Split(';', 2)[0])
                .Single(value => value.StartsWith(
                    SqlOSAuthorizationServerService.BuildContinuationCookieName(requestId) + "=",
                    StringComparison.Ordinal));
        }

        // Reloading with only the continuation cookie re-mints a FRESH token bound to the
        // same user.
        using var reload = await SendWithCookieAsync(
            host,
            $"/sqlos/auth/headless/requests/{requestId}?view=consent",
            continuationCookie);
        reload.StatusCode.Should().Be(HttpStatusCode.OK);
        using var reloadBody = JsonDocument.Parse(await reload.Content.ReadAsStringAsync());
        reloadBody.RootElement.GetProperty("view").GetString().Should().Be("consent");
        var freshConsentToken = reloadBody.RootElement.GetProperty("consentToken").GetString();
        freshConsentToken.Should().NotBeNullOrWhiteSpace();
        freshConsentToken.Should().NotBe(originalConsentToken, "the reload mints a fresh consent token");
        reloadBody.RootElement.GetProperty("consentScopes").EnumerateArray()
            .Select(x => x.GetProperty("scope").GetString())
            .Should().BeEquivalentTo("openid", "todo:read");

        // Anonymous reload of the same request still gets no token.
        using var anonymousReload = await host.Client.GetAsync(
            $"/sqlos/auth/headless/requests/{requestId}?view=consent");
        using var anonymousBody = JsonDocument.Parse(await anonymousReload.Content.ReadAsStringAsync());
        anonymousBody.RootElement.GetProperty("consentToken").ValueKind.Should().Be(JsonValueKind.Null);

        // The fresh token approves the request.
        using var approve = await host.Client.PostAsJsonAsync(
            "/sqlos/auth/headless/consent/approve",
            new { requestId, consentToken = freshConsentToken });
        approve.StatusCode.Should().Be(HttpStatusCode.OK);
        using var approveBody = JsonDocument.Parse(await approve.Content.ReadAsStringAsync());
        approveBody.RootElement.GetProperty("type").GetString().Should().Be("redirect");
        var redirectUrl = approveBody.RootElement.GetProperty("redirectUrl").GetString();
        redirectUrl.Should().StartWith(ThirdPartyRedirect);
        redirectUrl.Should().Contain("code=");
    }

    [TestMethod]
    public async Task Continue_WithTwoInterleavedFlows_EachResolvesItsOwnContinuation()
    {
        await using var host = await HeadlessHost.CreateAsync();

        // Two authorization flows racing in separate tabs of ONE browser: each stops at
        // consent and each callback writes its own continuation cookie. The per-request
        // cookie name means the second Set-Cookie cannot clobber the first tab's handle.
        var firstRequestId = await StartHeadlessAuthorizeAsync(host, ThirdPartyClientId, ThirdPartyRedirect, "openid todo:read");
        var secondRequestId = await StartHeadlessAuthorizeAsync(host, ThirdPartyClientId, ThirdPartyRedirect, "openid");
        var firstConsentToken = await LoginToConsentAsync(host, firstRequestId);
        var secondConsentToken = await LoginToConsentAsync(host, secondRequestId);

        var firstCookie = await MintContinuationCookieAsync(host, firstRequestId, firstConsentToken);
        // Interleave: the second flow's callback writes its cookie AFTER the first.
        var secondCookie = await MintContinuationCookieAsync(host, secondRequestId, secondConsentToken);
        firstCookie.Split('=', 2)[0].Should().NotBe(
            secondCookie.Split('=', 2)[0],
            "each authorization request must own its own continuation cookie slot");

        // The browser now carries BOTH cookies; each /continue must resolve its own flow.
        using var firstContinue = await SendWithCookieAsync(
            host,
            $"/sqlos/auth/continue?request={Uri.EscapeDataString(firstRequestId)}",
            $"{firstCookie}; {secondCookie}");
        firstContinue.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var firstUiQuery = QueryHelpers.ParseQuery(firstContinue.Headers.Location!.Query);
        firstUiQuery["requestId"].ToString().Should().Be(firstRequestId);
        firstUiQuery["view"].ToString().Should().Be("consent");

        using var secondContinue = await SendWithCookieAsync(
            host,
            $"/sqlos/auth/continue?request={Uri.EscapeDataString(secondRequestId)}",
            $"{firstCookie}; {secondCookie}");
        secondContinue.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var secondUiQuery = QueryHelpers.ParseQuery(secondContinue.Headers.Location!.Query);
        secondUiQuery["requestId"].ToString().Should().Be(
            secondRequestId,
            "the second tab's flow must not be lost to the first tab's continuation handle");
        secondUiQuery["view"].ToString().Should().Be("consent");

        // Both flows stay completable: each consent token still approves its own request.
        using var approveFirst = await host.Client.PostAsJsonAsync(
            "/sqlos/auth/headless/consent/approve",
            new { requestId = firstRequestId, consentToken = firstConsentToken });
        approveFirst.StatusCode.Should().Be(HttpStatusCode.OK);
        using var approveFirstBody = JsonDocument.Parse(await approveFirst.Content.ReadAsStringAsync());
        approveFirstBody.RootElement.GetProperty("redirectUrl").GetString().Should().Contain("code=");

        using var approveSecond = await host.Client.PostAsJsonAsync(
            "/sqlos/auth/headless/consent/approve",
            new { requestId = secondRequestId, consentToken = secondConsentToken });
        approveSecond.StatusCode.Should().Be(HttpStatusCode.OK);
        using var approveSecondBody = JsonDocument.Parse(await approveSecond.Content.ReadAsStringAsync());
        approveSecondBody.RootElement.GetProperty("redirectUrl").GetString().Should().Contain("code=");
    }

    private static async Task<string> LoginToConsentAsync(HeadlessHost host, string requestId)
    {
        using var login = await host.Client.PostAsJsonAsync(
            "/sqlos/auth/headless/password/login",
            new { requestId, email = host.Email, password = Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        loginBody.RootElement.GetProperty("type").GetString().Should().Be("view");
        var viewModel = loginBody.RootElement.GetProperty("viewModel");
        viewModel.GetProperty("view").GetString().Should().Be("consent");
        var consentToken = viewModel.GetProperty("consentToken").GetString();
        consentToken.Should().NotBeNullOrWhiteSpace();
        return consentToken!;
    }

    private static async Task<string> MintContinuationCookieAsync(
        HeadlessHost host,
        string requestId,
        string consentToken)
    {
        await using var scope = host.App.Services.CreateAsyncScope();
        var authorizationServer = scope.ServiceProvider.GetRequiredService<SqlOSAuthorizationServerService>();
        var continuationContext = new DefaultHttpContext();
        continuationContext.Request.Scheme = "https";
        continuationContext.Request.Host = new HostString("auth.example.test");
        await authorizationServer.CreateAuthorizationContinuationRedirectAsync(
            new SqlOSAuthorizationRequestLoginResult(
                null,
                false,
                null,
                Array.Empty<SqlOSOrganizationOption>(),
                AuthorizationRequestId: requestId,
                RequiresConsent: true,
                ConsentToken: consentToken),
            continuationContext);
        return continuationContext.Response.Headers.SetCookie
            .Select(value => value!.Split(';', 2)[0])
            .Single(value => value.StartsWith(
                SqlOSAuthorizationServerService.BuildContinuationCookieName(requestId) + "=",
                StringComparison.Ordinal));
    }

    private static async Task<string> StartHeadlessAuthorizeAsync(
        HeadlessHost host,
        string clientId,
        string redirectUri,
        string scope)
    {
        using var authorize = await host.Client.GetAsync(BuildAuthorizeUrl(clientId, redirectUri, scope));
        authorize.StatusCode.Should().Be(HttpStatusCode.Redirect, "headless mode redirects /authorize to the UI");
        var location = authorize.Headers.Location!;
        var requestId = QueryHelpers.ParseQuery(location.Query)["requestId"].ToString();
        requestId.Should().NotBeNullOrWhiteSpace($"the UI URL must carry the request id: {location}");
        return requestId;
    }

    private static string BuildAuthorizeUrl(string clientId, string redirectUri, string scope)
    {
        var codeVerifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');
        return QueryHelpers.AddQueryString("/sqlos/auth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = scope,
            ["state"] = $"state-{Guid.NewGuid():N}",
            ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier))),
            ["code_challenge_method"] = "S256"
        });
    }

    private static async Task<HttpResponseMessage> SendWithCookieAsync(HeadlessHost host, string url, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return await host.Client.SendAsync(request);
    }

    private static string ExtractCookie(HttpResponseMessage response, string prefix)
        => TryExtractCookie(response, prefix)
            ?? throw new InvalidOperationException($"Response did not set a '{prefix}' cookie.");

    private static string? TryExtractCookie(HttpResponseMessage response, string prefix)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        return values.Select(value => value.Split(';', 2)[0])
            .FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
    }

    private sealed class HeadlessHost : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required string Email { get; init; }
        public required string UserId { get; init; }

        public static async Task<HeadlessHost> CreateAsync()
        {
            await using var bootstrapContext = await AspireFixture.CreateIsolatedAuthContextAsync("HeadlessConsentReload");
            var connectionString = bootstrapContext.Database.GetConnectionString()!;

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<TestSqlOSDbContext>(database => database.UseTestProvider(connectionString));
            builder.Services.AddSqlOS<TestSqlOSDbContext>(options =>
            {
                options.AuthServer.Issuer = $"{TrustedOrigin}/sqlos/auth";
                options.AuthServer.PublicOrigin = TrustedOrigin;
                options.AuthServer.BasePath = "/sqlos/auth";
                options.AuthServer.SeedBrowserClient(FirstPartyClientId, "Headless First Party", FirstPartyRedirect);
                options.AuthServer.SeedClient(client =>
                {
                    client.ClientId = ThirdPartyClientId;
                    client.Name = "Headless Third Party";
                    client.RedirectUris = [ThirdPartyRedirect];
                    client.ClientType = "public_pkce";
                    client.RequirePkce = true;
                    client.IsFirstParty = false;
                    client.AllowedScopes = ["openid", "profile", "todo:read"];
                });
                options.AuthServer.SeedScopeDisplayName(
                    "todo:read",
                    "Read your tasks",
                    "See every task on your boards.");
                options.AuthServer.SeedAuthPage(page =>
                {
                    page.EnabledCredentialTypes = ["password"];
                    page.EnablePasswordSignup = true;
                });
                // Models a documented custom UI: only the request id and view are forwarded,
                // which is exactly how the ConsentToken route field gets lost.
                options.AuthServer.UseHeadlessAuthPage(headless =>
                    headless.BuildUiUrl = context =>
                        $"https://app.example.test/auth-ui?requestId={Uri.EscapeDataString(context.RequestId ?? string.Empty)}&view={Uri.EscapeDataString(context.View)}");
            });
            builder.Services.RemoveAll<IHostedService>();

            var app = builder.Build();
            app.MapAuthServer("/sqlos/auth");
            await using (var scope = app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<SqlOSBootstrapper>().InitializeAsync();
                var email = $"consent-reload-{Guid.NewGuid():N}@example.test";
                var user = await scope.ServiceProvider.GetRequiredService<SqlOSAdminService>()
                    .CreateUserAsync(new SqlOSCreateUserRequest("Consent Reload User", email, Password));
                await app.StartAsync();

                var client = app.GetTestClient();
                client.BaseAddress = new Uri(TrustedOrigin);
                return new HeadlessHost
                {
                    App = app,
                    Client = client,
                    Email = email,
                    UserId = user.Id
                };
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await using (var scope = App.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>().Database.EnsureDeletedAsync();
            }

            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}

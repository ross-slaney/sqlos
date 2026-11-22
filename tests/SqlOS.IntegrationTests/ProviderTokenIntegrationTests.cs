using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// A connection that keeps the provider's tokens stores them on every sign-in through the hosted
/// page and a headless UI, for GitHub's OAuth profile path and for OIDC (#557). A person in two
/// organizations who signs in through a provider in a headless UI can read the organizations to
/// choose from, with or without the pending token in the UI's address.
/// </summary>
[TestClass]
public sealed class ProviderTokenIntegrationTests
{
    private const string Origin = HostedAuthorizeTokenFixture.TrustedOrigin;
    private const string ProviderCallback = Origin + "/sqlos/auth/oidc/callback";

    [TestMethod]
    public async Task Hosted_GitHub_NewAndReturningPerson_KeepTheirToken()
    {
        await using var host = await TokenHost.CreateAsync(headlessUi: null);
        var email = $"hosted-github-{Guid.NewGuid():N}@example.test";

        var userId = await host.SignInHostedAsync("github", email, "first");
        (await host.ReadTokenAsync(userId, "github"))!.AccessToken.Should().Be($"github|success:{email}:first");

        var returning = await host.SignInHostedAsync("github", email, "second");
        returning.Should().Be(userId);
        var token = await host.ReadTokenAsync(userId, "github");
        token!.AccessToken.Should().Be($"github|success:{email}:second");
        token.ExpiresAt.Should().BeNull("a GitHub OAuth App token has no expiry");
        token.Scopes.Should().BeEquivalentTo("read:user", "user:email");
        (await host.ProviderScopesRequestedAsync("github")).Should().Contain("repo", "the seeded scopes reach GitHub's authorize URL");

        await host.ExecuteAsync(db => new SqlOSIdentityDeletionService(db, []).DeleteUserAsync(userId, "operator", confirm: true));
        (await host.QueryAsync(db => db.Set<SqlOSExternalIdentity>().AsNoTracking().SingleAsync(x => x.UserId == userId)))
            .ProviderAccessTokenEncrypted.Should().BeNull("deleting the user clears the tokens kept on their identities");
        (await host.ReadTokenAsync(userId, "github")).Should().BeNull();
    }

    [TestMethod]
    public async Task Hosted_Google_NewPerson_KeepsAccessTokenRefreshTokenAndExpiry()
    {
        await using var host = await TokenHost.CreateAsync(headlessUi: null);
        var email = $"hosted-google-{Guid.NewGuid():N}@example.test";

        var userId = await host.SignInHostedAsync("google", email, label: null);

        var identity = await host.QueryAsync(db => db.Set<SqlOSExternalIdentity>().AsNoTracking().SingleAsync(x => x.UserId == userId));
        identity.ProviderRefreshTokenEncrypted.Should().NotBeNull();
        identity.ProviderAccessTokenExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddHours(1), TimeSpan.FromMinutes(2));
        (await host.ReadTokenAsync(userId, "google"))!.AccessToken.Should().StartWith("google|success:" + email);

        await host.ExecuteAsync(async db =>
        {
            var stored = await db.Set<SqlOSExternalIdentity>().SingleAsync(x => x.UserId == userId);
            stored.ProviderAccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        });
        (await host.ReadTokenAsync(userId, "google"))!.AccessToken.Should().StartWith("google-refreshed|");
    }

    [TestMethod]
    public async Task Headless_GitHubAndGoogle_NewAndReturningPerson_KeepTheirTokens()
    {
        await using var host = await TokenHost.CreateAsync(headlessUi: HeadlessUi.WithPendingToken);
        var email = $"headless-{Guid.NewGuid():N}@example.test";

        var first = await host.SignInHeadlessAsync("github", email, "first");
        first.ClientRedirect.Should().NotBeNull("a person with no organization finishes at once");
        (await host.ReadTokenAsync(first.UserId, "github"))!.AccessToken.Should().Be($"github|success:{email}:first");

        var returning = await host.SignInHeadlessAsync("github", email, "second");
        returning.UserId.Should().Be(first.UserId);
        (await host.ReadTokenAsync(first.UserId, "github"))!.AccessToken.Should().Be($"github|success:{email}:second");

        var google = await host.SignInHeadlessAsync("google", email, label: null);
        google.UserId.Should().Be(first.UserId, "the verified email links the Google identity to the same person");
        (await host.ReadTokenAsync(first.UserId, "google"))!.AccessToken.Should().StartWith("google|success:" + email);
        (await host.ReadTokenAsync(first.UserId, "github"))!.AccessToken.Should().Be($"github|success:{email}:second");
    }

    [TestMethod]
    public async Task Headless_ProviderSignInWithTwoOrganizations_ListsThemFromThePendingToken()
    {
        await using var host = await TokenHost.CreateAsync(headlessUi: HeadlessUi.WithPendingToken);
        var email = $"two-orgs-{Guid.NewGuid():N}@example.test";
        var userId = (await host.SignInHeadlessAsync("github", email, "join")).UserId;
        var first = await host.AddMembershipAsync(userId, "First");
        var second = await host.AddMembershipAsync(userId, "Second");

        var waiting = await host.SignInHeadlessAsync("github", email, "choose");

        waiting.ClientRedirect.Should().BeNull();
        waiting.UiUrl.Should().NotBeNull();
        var uiQuery = QueryHelpers.ParseQuery(waiting.UiUrl!.Query);
        uiQuery["view"].ToString().Should().Be("organization");
        var pendingToken = uiQuery["pendingToken"].ToString();
        pendingToken.Should().NotBeNullOrWhiteSpace();

        // The UI reloads the request without the browser's cookie, from its own address alone.
        var view = await host.ReadRequestAsync(waiting.RequestId, pendingToken, cookie: null);
        view.OrganizationIds.Should().BeEquivalentTo([first, second]);

        var selected = await host.SelectOrganizationAsync(view.PendingToken!, second);
        selected.Should().StartWith(HostedAuthorizeTokenFixture.RedirectUri);
        (await host.ReadTokenAsync(userId, "github"))!.AccessToken.Should().Be($"github|success:{email}:choose");
    }

    [TestMethod]
    public async Task Headless_ProviderSignInWithTwoOrganizations_ListsThemFromTheCookie_WhenTheUiUrlDropsTheToken()
    {
        await using var host = await TokenHost.CreateAsync(headlessUi: HeadlessUi.WithoutPendingToken);
        var email = $"two-orgs-cookie-{Guid.NewGuid():N}@example.test";
        var userId = (await host.SignInHeadlessAsync("google", email, label: null)).UserId;
        var first = await host.AddMembershipAsync(userId, "Alpha");
        var second = await host.AddMembershipAsync(userId, "Beta");

        var waiting = await host.SignInHeadlessAsync("google", email, label: null);

        QueryHelpers.ParseQuery(waiting.UiUrl!.Query).ContainsKey("pendingToken").Should().BeFalse();
        var view = await host.ReadRequestAsync(waiting.RequestId, pendingToken: null, waiting.ContinuationCookie);
        view.OrganizationIds.Should().BeEquivalentTo([first, second]);
        view.PendingToken.Should().NotBeNullOrWhiteSpace();

        var selected = await host.SelectOrganizationAsync(view.PendingToken!, first);
        selected.Should().StartWith(HostedAuthorizeTokenFixture.RedirectUri);
        (await host.ReadTokenAsync(userId, "google"))!.AccessToken.Should().StartWith("google|success:" + email);
    }

    private enum HeadlessUi
    {
        WithPendingToken,
        WithoutPendingToken
    }

    private sealed record HeadlessSignIn(string RequestId, string UserId, Uri? ClientRedirect, Uri? UiUrl, string? ContinuationCookie);

    private sealed record RequestView(IReadOnlyList<string> OrganizationIds, string? PendingToken);

    private sealed class TokenHost : IAsyncDisposable
    {
        private TokenHost(HostedAuthorizeTokenFixture fixture)
        {
            Fixture = fixture;
        }

        private HostedAuthorizeTokenFixture Fixture { get; }

        private HttpClient Client => Fixture.Client;

        public static async Task<TokenHost> CreateAsync(HeadlessUi? headlessUi)
            => new(await HostedAuthorizeTokenFixture.CreateAsync(
                headlessUi == null ? "ProviderTokensHosted" : "ProviderTokensHeadless",
                configure: options =>
                {
                    options.AuthServer.SeedGitHubConnection(
                        "github-client",
                        "github-secret",
                        [ProviderCallback],
                        scopes: ["read:user", "user:email", "repo"],
                        storeProviderTokens: true);
                    options.AuthServer.SeedGoogleConnection(
                        "google-client",
                        "google-secret",
                        [ProviderCallback],
                        storeProviderTokens: true);
                    if (headlessUi is { } ui)
                    {
                        options.AuthServer.UseHeadlessAuthPage(headless => headless.BuildUiUrl = context =>
                            QueryHelpers.AddQueryString("https://app.example.test/auth", new Dictionary<string, string?>
                            {
                                ["request"] = context.RequestId,
                                ["view"] = context.View,
                                ["pendingToken"] = ui == HeadlessUi.WithPendingToken ? context.PendingToken : null
                            }.Where(x => x.Value != null)));
                    }
                },
                configureServices: services =>
                {
                    services.RemoveAll<IHttpClientFactory>();
                    services.AddSingleton<IHttpClientFactory>(new FakeOidcProviderHttpClientFactory());
                }));

        public async Task<string> SignInHostedAsync(string connectionKey, string email, string? label)
        {
            var started = await Fixture.StartAuthorizeAsync("openid profile email");
            var connectionId = await ConnectionIdAsync(connectionKey);
            using var login = await Client.GetAsync($"/sqlos/auth/login/oidc/{connectionId}?request={Uri.EscapeDataString(started.RequestId)}&email=");
            login.StatusCode.Should().Be(HttpStatusCode.Redirect);
            using var callback = await CallbackAsync(login.Headers.Location!, email, label);
            callback.StatusCode.Should().Be(HttpStatusCode.Redirect, await callback.Content.ReadAsStringAsync());
            callback.Headers.Location!.ToString().Should().StartWith(HostedAuthorizeTokenFixture.RedirectUri);
            return await UserIdAsync(email);
        }

        public async Task<HeadlessSignIn> SignInHeadlessAsync(string connectionKey, string email, string? label)
        {
            var requestId = await StartHeadlessAsync();
            using var start = await Client.PostAsJsonAsync("/sqlos/auth/headless/provider/start", new
            {
                requestId,
                connectionId = await ConnectionIdAsync(connectionKey),
                email = (string?)null
            });
            var started = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement;
            start.StatusCode.Should().Be(HttpStatusCode.OK, started.ToString());
            var providerUrl = new Uri(started.GetProperty("redirectUrl").GetString()!);

            using var callback = await CallbackAsync(providerUrl, email, label);
            callback.StatusCode.Should().Be(HttpStatusCode.Redirect, await callback.Content.ReadAsStringAsync());
            var location = callback.Headers.Location!;
            var userId = await UserIdAsync(email);
            if (location.IsAbsoluteUri && location.ToString().StartsWith(HostedAuthorizeTokenFixture.RedirectUri, StringComparison.Ordinal))
            {
                return new HeadlessSignIn(requestId, userId, location, null, null);
            }

            location.ToString().Should().Contain("/sqlos/auth/continue");
            var cookie = HostedAuthorizeTokenFixture.TryExtractCookie(callback, "sqlos_auth_continue_");
            cookie.Should().NotBeNull();
            using var continueRequest = new HttpRequestMessage(HttpMethod.Get, location);
            continueRequest.Headers.Add("Cookie", cookie);
            using var continued = await Client.SendAsync(continueRequest);
            continued.StatusCode.Should().Be(HttpStatusCode.Redirect, await continued.Content.ReadAsStringAsync());
            return new HeadlessSignIn(requestId, userId, null, continued.Headers.Location, cookie);
        }

        public async Task<RequestView> ReadRequestAsync(string requestId, string? pendingToken, string? cookie)
        {
            var path = QueryHelpers.AddQueryString($"/sqlos/auth/headless/requests/{Uri.EscapeDataString(requestId)}", new Dictionary<string, string?>
            {
                ["view"] = "organization",
                ["pendingToken"] = pendingToken
            }.Where(x => x.Value != null));
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (cookie != null)
            {
                request.Headers.Add("Cookie", cookie);
            }

            using var response = await Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            var root = JsonDocument.Parse(body).RootElement;
            return new RequestView(
                root.GetProperty("organizationSelection").EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToList(),
                root.TryGetProperty("pendingToken", out var token) && token.ValueKind == JsonValueKind.String ? token.GetString() : null);
        }

        public async Task<string> SelectOrganizationAsync(string pendingToken, string organizationId)
        {
            using var response = await Client.PostAsJsonAsync("/sqlos/auth/headless/organization/select", new { pendingToken, organizationId });
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            return JsonDocument.Parse(body).RootElement.GetProperty("redirectUrl").GetString()!;
        }

        public async Task<string> AddMembershipAsync(string userId, string name)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"{name} {Guid.NewGuid():N}", null));
            await admin.CreateMembershipAsync(organization.Id, new SqlOSCreateMembershipRequest(userId, "member"));
            return organization.Id;
        }

        public async Task<SqlOSProviderAccessToken?> ReadTokenAsync(string userId, string connectionKey)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<SqlOSOidcAuthService>()
                .GetProviderAccessTokenAsync(userId, connectionKey);
        }

        public async Task<string[]> ProviderScopesRequestedAsync(string connectionKey)
        {
            var started = await Fixture.StartAuthorizeAsync("openid profile email");
            using var login = await Client.GetAsync($"/sqlos/auth/login/oidc/{await ConnectionIdAsync(connectionKey)}?request={Uri.EscapeDataString(started.RequestId)}&email=");
            return QueryHelpers.ParseQuery(login.Headers.Location!.Query)["scope"].ToString().Split(' ');
        }

        public async Task<T> QueryAsync<T>(Func<TestSqlOSDbContext, Task<T>> query)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            return await query(scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>());
        }

        public async Task ExecuteAsync(Func<TestSqlOSDbContext, Task> action)
        {
            await using var scope = Fixture.App.Services.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>());
        }

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();

        private async Task<string> StartHeadlessAsync()
        {
            var verifier = HostedAuthorizeTokenFixture.CreateCodeVerifier();
            var path = QueryHelpers.AddQueryString("/sqlos/auth/authorize", new Dictionary<string, string?>
            {
                ["response_type"] = "code",
                ["client_id"] = HostedAuthorizeTokenFixture.ClientId,
                ["redirect_uri"] = HostedAuthorizeTokenFixture.RedirectUri,
                ["scope"] = "openid profile email",
                ["state"] = $"state-{Guid.NewGuid():N}",
                ["code_challenge"] = HostedAuthorizeTokenFixture.CreateCodeChallenge(verifier),
                ["code_challenge_method"] = "S256"
            });
            using var authorize = await Client.GetAsync(path);
            authorize.StatusCode.Should().Be(HttpStatusCode.Redirect, "a headless application renders sign-in in its own UI");
            var requestId = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["request"].ToString();
            requestId.Should().NotBeNullOrWhiteSpace();
            return requestId;
        }

        private Task<HttpResponseMessage> CallbackAsync(Uri providerUrl, string email, string? label)
        {
            var providerQuery = QueryHelpers.ParseQuery(providerUrl.Query);
            var nonce = providerQuery.TryGetValue("nonce", out var value) ? value.ToString() : label ?? "nonce";
            return Client.GetAsync(QueryHelpers.AddQueryString(ProviderCallbackPath, new Dictionary<string, string?>
            {
                ["state"] = providerQuery["state"].ToString(),
                ["code"] = $"success:{email}:{nonce}"
            }));
        }

        private const string ProviderCallbackPath = "/sqlos/auth/oidc/callback";

        private Task<string> ConnectionIdAsync(string key)
            => QueryAsync(db => db.Set<SqlOSOidcConnection>()
                .Where(x => x.ConfigurationSourceKey == key)
                .Select(x => x.Id)
                .SingleAsync());

        private Task<string> UserIdAsync(string email)
        {
            var normalized = SqlOSAdminService.NormalizeEmail(email);
            return QueryAsync(db => db.Set<SqlOSUserEmail>()
                .Where(x => x.NormalizedEmail == normalized)
                .Select(x => x.UserId)
                .SingleAsync());
        }
    }
}

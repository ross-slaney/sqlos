using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// Wire-level coverage for silent SSO reuse combined with the OIDC
/// max_age / prompt=select_account semantics, and for the nonce and
/// auth_time plumbing from the authorize request through the code to
/// the session.
/// </summary>
[TestClass]
public sealed class HostedAuthorizePromptIntegrationTests
{
    [TestMethod]
    public async Task SilentSso_WithFreshMaxAge_IssuesCodePreservingOriginalAuthTime()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("PromptMaxAge");
        await fixture.SetClientAllowedScopesAsync("openid", "profile");

        var beforeLogin = DateTime.UtcNow.AddSeconds(-1);
        var started = await fixture.StartAuthorizeAsync("openid profile");
        var login = await fixture.SubmitPasswordLoginWithSessionAsync(started);
        var afterLogin = DateTime.UtcNow.AddSeconds(1);

        using var silent = await fixture.AuthorizeWithSessionAsync("openid profile", login.AuthPageCookie, maxAge: "3600");

        silent.Response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = silent.Response.Headers.Location!;
        var query = QueryHelpers.ParseQuery(location.Query);
        var silentCode = query["code"].ToString();
        silentCode.Should().NotBeNullOrWhiteSpace();

        var codeRow = await LoadCodeRowAsync(fixture, silentCode);
        codeRow.AuthTime.Should().NotBeNull();
        codeRow.AuthTime!.Value.Should().BeOnOrAfter(beforeLogin).And.BeOnOrBefore(afterLogin);
    }

    [TestMethod]
    public async Task MaxAgeZero_WithLiveSession_RechallengesInsteadOfSilentSso()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("PromptMaxAge");
        await fixture.SetClientAllowedScopesAsync("openid");
        var started = await fixture.StartAuthorizeAsync("openid");
        var login = await fixture.SubmitPasswordLoginWithSessionAsync(started);

        using var challenged = await fixture.AuthorizeWithSessionAsync("openid", login.AuthPageCookie, maxAge: "0");

        challenged.Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await challenged.Response.Content.ReadAsStringAsync();
        html.Should().Contain("__RequestVerificationToken");
    }

    [TestMethod]
    public async Task PromptNone_WithStaleMaxAge_ReturnsLoginRequiredRedirect()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("PromptMaxAge");
        await fixture.SetClientAllowedScopesAsync("openid");
        var started = await fixture.StartAuthorizeAsync("openid");
        var login = await fixture.SubmitPasswordLoginWithSessionAsync(started);

        using var denied = await fixture.AuthorizeWithSessionAsync("openid", login.AuthPageCookie, prompt: "none", maxAge: "0");

        denied.Response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = denied.Response.Headers.Location!;
        location.AbsoluteUri.Should().StartWith(HostedAuthorizeTokenFixture.RedirectUri);
        var query = QueryHelpers.ParseQuery(location.Query);
        query["error"].ToString().Should().Be("login_required");
    }

    [TestMethod]
    public async Task PromptNone_WithLiveSessionAndNoMaxAge_StillSilentlyIssuesCode()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("PromptMaxAge");
        await fixture.SetClientAllowedScopesAsync("openid");
        var started = await fixture.StartAuthorizeAsync("openid");
        var login = await fixture.SubmitPasswordLoginWithSessionAsync(started);

        using var silent = await fixture.AuthorizeWithSessionAsync("openid", login.AuthPageCookie, prompt: "none");

        silent.Response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var query = QueryHelpers.ParseQuery(silent.Response.Headers.Location!.Query);
        query["code"].ToString().Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task MaxAge_LongMaxValue_WithLiveSession_SilentSsoSucceeds()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("PromptMaxAge");
        await fixture.SetClientAllowedScopesAsync("openid");
        var started = await fixture.StartAuthorizeAsync("openid");
        var login = await fixture.SubmitPasswordLoginWithSessionAsync(started);

        // long.MaxValue seconds must not 500 (TimeSpan.FromSeconds would throw
        // OverflowException); the session age is never >= that, so silent SSO
        // issues a code.
        using var silent = await fixture.AuthorizeWithSessionAsync(
            "openid",
            login.AuthPageCookie,
            maxAge: "9223372036854775807");

        silent.Response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var query = QueryHelpers.ParseQuery(silent.Response.Headers.Location!.Query);
        query["code"].ToString().Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task PromptSelectAccountConsentList_WithLiveSession_ForcesFreshSignIn()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("PromptMaxAge");
        await fixture.SetClientAllowedScopesAsync("openid");
        var started = await fixture.StartAuthorizeAsync("openid");
        var login = await fixture.SubmitPasswordLoginWithSessionAsync(started);

        // prompt is a space-delimited list; select_account must be honored even
        // when combined with other values.
        using var challenged = await fixture.AuthorizeWithSessionAsync(
            "openid",
            login.AuthPageCookie,
            prompt: "select_account consent");

        challenged.Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await challenged.Response.Content.ReadAsStringAsync();
        html.Should().Contain("__RequestVerificationToken");
    }

    [TestMethod]
    public async Task PromptSelectAccount_WithLiveSession_ForcesFreshSignIn()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("PromptMaxAge");
        await fixture.SetClientAllowedScopesAsync("openid");
        var started = await fixture.StartAuthorizeAsync("openid");
        var login = await fixture.SubmitPasswordLoginWithSessionAsync(started);

        using var challenged = await fixture.AuthorizeWithSessionAsync("openid", login.AuthPageCookie, prompt: "select_account");

        challenged.Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await challenged.Response.Content.ReadAsStringAsync();
        html.Should().Contain("__RequestVerificationToken");
    }

    [TestMethod]
    public async Task MaxAge_NonNumeric_IsRejectedAsInvalidRequest()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("PromptMaxAge");
        await fixture.SetClientAllowedScopesAsync("openid");
        var started = await fixture.StartAuthorizeAsync("openid");
        var login = await fixture.SubmitPasswordLoginWithSessionAsync(started);

        using var rejected = await fixture.AuthorizeWithSessionAsync("openid", login.AuthPageCookie, maxAge: "not-a-number");

        rejected.Response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task MaxAge_WhitespaceOnly_IsRejectedAsInvalidRequest()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("PromptMaxAge");
        await fixture.SetClientAllowedScopesAsync("openid");
        var started = await fixture.StartAuthorizeAsync("openid");
        var login = await fixture.SubmitPasswordLoginWithSessionAsync(started);

        // max_age=%20 is present but malformed; it must be rejected instead of
        // silently reading as absent and dropping the freshness constraint
        // (which would let silent SSO issue a code here).
        using var rejected = await fixture.AuthorizeWithSessionAsync("openid", login.AuthPageCookie, maxAge: " ");

        rejected.Response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task PromptNone_CombinedWithLogin_IsRejectedAsInvalidRequest()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("PromptMaxAge");
        await fixture.SetClientAllowedScopesAsync("openid");
        var started = await fixture.StartAuthorizeAsync("openid");
        var login = await fixture.SubmitPasswordLoginWithSessionAsync(started);

        // OIDC Core 3.1.2.1: none MUST NOT be combined with any other value.
        using var rejected = await fixture.AuthorizeWithSessionAsync(
            "openid",
            login.AuthPageCookie,
            prompt: "none login");

        rejected.Response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var html = await rejected.Response.Content.ReadAsStringAsync();
        html.Should().Contain("prompt cannot combine none with other values.");
    }

    [TestMethod]
    public async Task PromptNone_CombinedWithConsent_DoesNotSilentlyIssueCode()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("PromptMaxAge");
        await fixture.SetClientAllowedScopesAsync("openid");
        var started = await fixture.StartAuthorizeAsync("openid");
        var login = await fixture.SubmitPasswordLoginWithSessionAsync(started);

        using var rejected = await fixture.AuthorizeWithSessionAsync(
            "openid",
            login.AuthPageCookie,
            prompt: "none consent");

        rejected.Response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        rejected.Response.Headers.Location.Should().BeNull();
    }

    [TestMethod]
    public async Task NonceAndAuthTime_RoundTripThroughCodeToSession()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("PromptMaxAge");
        await fixture.SetClientAllowedScopesAsync("openid", "profile");
        var nonce = $"nonce-{Guid.NewGuid():N}";

        var started = await fixture.StartAuthorizeAsync("openid profile", nonce: nonce);
        var login = await fixture.SubmitPasswordLoginWithSessionAsync(started);

        var codeRow = await LoadCodeRowAsync(fixture, login.Code);
        codeRow.Nonce.Should().Be(nonce);
        codeRow.AuthTime.Should().NotBeNull();

        using var tokens = await fixture.ExchangeAuthorizationCodeAsync(login.Code, started.CodeVerifier);
        var sessionId = await ReadSessionIdAsync(fixture, tokens.RootElement.GetProperty("access_token").GetString()!);

        await using var scope = fixture.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        var session = await db.Set<SqlOSSession>().AsNoTracking().SingleAsync(x => x.Id == sessionId);
        session.Scope.Should().Be("openid profile");
        session.AuthenticatedAt.Should().Be(codeRow.AuthTime);
    }

    [TestMethod]
    public async Task ReplayedCode_DoesNotIssueSecondSessionForItsNonce()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("PromptMaxAge");
        await fixture.SetClientAllowedScopesAsync("openid");
        var started = await fixture.StartAuthorizeAsync("openid", nonce: $"nonce-{Guid.NewGuid():N}");
        var login = await fixture.SubmitPasswordLoginWithSessionAsync(started);
        using var first = await fixture.ExchangeAuthorizationCodeAsync(login.Code, started.CodeVerifier);

        var replay = async () => await fixture.ExchangeAuthorizationCodeAsync(login.Code, started.CodeVerifier);

        await replay.Should().ThrowAsync<InvalidOperationException>();
    }

    [TestMethod]
    public async Task PostAuthorize_WithFormParameters_RendersLoginPageAndCompletesFlow()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("AuthorizePost");
        await fixture.SetClientAllowedScopesAsync("openid");

        // OIDC Core 3.1.2.1: POST /authorize with the same form fields the GET
        // flow sends via query must start the same hosted flow.
        var started = await fixture.StartAuthorizeAsync("openid", usePost: true);
        started.RequestId.Should().NotBeNullOrWhiteSpace();

        var code = await fixture.SubmitPasswordLoginAsync(started);
        using var tokens = await fixture.ExchangeAuthorizationCodeAsync(code, started.CodeVerifier);
        tokens.RootElement.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task PostAuthorize_PromptNoneWithoutSession_RedirectsLoginRequired()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("AuthorizePost");
        await fixture.SetClientAllowedScopesAsync("openid");

        using var denied = await fixture.AuthorizeRawAsync(
            HttpMethod.Post,
            "openid",
            state: "post-none-state",
            extraParameters: new Dictionary<string, string> { ["prompt"] = "none" });

        denied.Response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = denied.Response.Headers.Location!;
        location.AbsoluteUri.Should().StartWith(HostedAuthorizeTokenFixture.RedirectUri);
        var query = QueryHelpers.ParseQuery(location.Query);
        query["error"].ToString().Should().Be("login_required");
        query["state"].ToString().Should().Be("post-none-state");
    }

    [TestMethod]
    public async Task Authorize_WithRequestObject_RedirectsRequestNotSupportedEchoingOuterState()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("AuthorizePost");
        await fixture.SetClientAllowedScopesAsync("openid");

        // OIDC Core 6.1: an OP without request-object support must return
        // request_not_supported. The outer state must be echoed even when the RP
        // put its real state only inside the request object.
        using var rejected = await fixture.AuthorizeRawAsync(
            HttpMethod.Get,
            "openid",
            state: "outer-state",
            extraParameters: new Dictionary<string, string>
            {
                ["request"] = "eyJhbGciOiJub25lIn0.eyJzdGF0ZSI6ImlubmVyLXN0YXRlIn0."
            });

        rejected.Response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = rejected.Response.Headers.Location!;
        location.AbsoluteUri.Should().StartWith(HostedAuthorizeTokenFixture.RedirectUri);
        var query = QueryHelpers.ParseQuery(location.Query);
        query["error"].ToString().Should().Be("request_not_supported");
        query["state"].ToString().Should().Be("outer-state");
        query.ContainsKey("code").Should().BeFalse();
    }

    [TestMethod]
    public async Task Authorize_WithRequestUri_RedirectsRequestUriNotSupported()
    {
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync("AuthorizePost");
        await fixture.SetClientAllowedScopesAsync("openid");

        using var rejected = await fixture.AuthorizeRawAsync(
            HttpMethod.Post,
            "openid",
            state: "outer-state",
            extraParameters: new Dictionary<string, string>
            {
                ["request_uri"] = "https://client.example.test/request.jwt"
            });

        rejected.Response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var query = QueryHelpers.ParseQuery(rejected.Response.Headers.Location!.Query);
        query["error"].ToString().Should().Be("request_uri_not_supported");
        query["state"].ToString().Should().Be("outer-state");
    }

    [TestMethod]
    public async Task ClientSecretPost_FullAuthorizeLoginExchange_SucceedsWithBodySecret()
    {
        const string postClientId = "hosted-post-client";
        const string postSecret = "hosted-post-client-secret-with-256-bits-of-entropy-1234567";
        await using var fixture = await HostedAuthorizeTokenFixture.CreateAsync(
            "AuthorizePost",
            options => options.AuthServer.SeedClient(client =>
            {
                client.ClientId = postClientId;
                client.Name = "Hosted client_secret_post client";
                client.RedirectUris = [HostedAuthorizeTokenFixture.RedirectUri];
                client.AllowedScopes = ["openid"];
                client.ClientType = "confidential";
                client.TokenEndpointAuthMethod = "client_secret_post";
                client.RequirePkce = false;
                client.IsFirstParty = true;
                client.ClientSecretResolver = () => postSecret;
            }));

        var started = await fixture.StartAuthorizeAsync("openid", clientId: postClientId);
        var code = await fixture.SubmitPasswordLoginAsync(started);

        using var tokens = await fixture.ExchangeAuthorizationCodeWithBodySecretAsync(
            code,
            started.CodeVerifier,
            postClientId,
            postSecret);

        tokens.RootElement.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
        tokens.RootElement.GetProperty("token_type").GetString().Should().Be("Bearer");
    }

    private static async Task<SqlOSAuthorizationCode> LoadCodeRowAsync(HostedAuthorizeTokenFixture fixture, string rawCode)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var crypto = scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>();
        var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        var codeHash = crypto.HashToken(rawCode);
        return await db.Set<SqlOSAuthorizationCode>().AsNoTracking().SingleAsync(x => x.CodeHash == codeHash);
    }

    private static async Task<string> ReadSessionIdAsync(HostedAuthorizeTokenFixture fixture, string accessToken)
    {
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        var sessionId = jwt.Claims.Single(claim => claim.Type == "sid").Value;
        sessionId.Should().NotBeNullOrWhiteSpace();
        return await Task.FromResult(sessionId);
    }
}

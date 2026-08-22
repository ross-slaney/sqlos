using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// Wire-level coverage for the non-first-party consent interstitial: the hosted consent page
/// after password login, remembered grants, scope escalation, deny, and the
/// prompt=none / prompt=consent semantics. First-party clients must never see consent.
/// </summary>
[TestClass]
public sealed class HostedConsentIntegrationTests
{
    private const string ThirdPartyClientId = "third-party-consent-client";
    private const string ThirdPartyRedirect = "https://third.example.test/callback";

    [TestMethod]
    public async Task ThirdParty_FirstAuthorize_ShowsConsentPage_AndApproveIssuesWorkingCode()
    {
        await using var fixture = await CreateFixtureAsync();

        var started = await fixture.StartAuthorizeAsync(
            "openid todo:read",
            clientId: ThirdPartyClientId,
            redirectUri: ThirdPartyRedirect);
        var consentPage = await fixture.SubmitPasswordLoginExpectingConsentAsync(started);

        consentPage.Html.Should().Contain("/sqlos/auth/consent/approve");
        consentPage.Html.Should().Contain("/sqlos/auth/consent/deny");
        consentPage.Html.Should().Contain("Third Party Consent Client");
        consentPage.Html.Should().Contain("Read your tasks", "the catalog display name replaces the raw scope");
        consentPage.Html.Should().Contain("openid", "uncataloged scopes fall back to the raw scope string");
        consentPage.ConsentToken.Should().NotBeNullOrWhiteSpace();

        using var approved = await fixture.SubmitConsentDecisionAsync(consentPage, approve: true);
        var location = await HostedAuthorizeTokenFixture.ReadClientRedirectAsync(approved);
        location.AbsoluteUri.Should().StartWith(ThirdPartyRedirect);
        var query = QueryHelpers.ParseQuery(location.Query);
        var code = query["code"].ToString();
        code.Should().NotBeNullOrWhiteSpace();

        using var tokens = await fixture.ExchangeAuthorizationCodeAsync(
            code,
            started.CodeVerifier,
            ThirdPartyClientId,
            ThirdPartyRedirect);
        tokens.RootElement.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
        tokens.RootElement.GetProperty("scope").GetString().Should().Contain("todo:read");

        await using var scope = fixture.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        var grant = await db.Set<SqlOSConsentGrant>()
            .SingleAsync(x => x.UserId == fixture.UserId && x.RevokedAt == null);
        grant.Scope.Should().Contain("todo:read");
        (await db.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "oauth.consent.granted"))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task ThirdParty_SecondAuthorizeWithSessionAndCoveringGrant_IssuesCodeSilently()
    {
        await using var fixture = await CreateFixtureAsync();
        var authPageCookie = await ApproveInitialConsentAsync(fixture, "openid todo:read");

        using var silent = await fixture.AuthorizeWithSessionAsync(
            "openid todo:read",
            authPageCookie,
            clientId: ThirdPartyClientId,
            redirectUri: ThirdPartyRedirect);

        silent.Response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = silent.Response.Headers.Location!;
        location.AbsoluteUri.Should().StartWith(ThirdPartyRedirect);
        QueryHelpers.ParseQuery(location.Query)["code"].ToString().Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task ThirdParty_ScopeEscalationWithSession_RePromptsForConsent()
    {
        await using var fixture = await CreateFixtureAsync();
        var authPageCookie = await ApproveInitialConsentAsync(fixture, "openid");

        using var escalated = await fixture.AuthorizeWithSessionAsync(
            "openid todo:read",
            authPageCookie,
            clientId: ThirdPartyClientId,
            redirectUri: ThirdPartyRedirect);

        escalated.Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await escalated.Response.Content.ReadAsStringAsync();
        html.Should().Contain("/sqlos/auth/consent/approve");
        html.Should().Contain("Read your tasks");
    }

    [TestMethod]
    public async Task ThirdParty_Deny_RedirectsWithAccessDenied_AndCancelsTheRequest()
    {
        await using var fixture = await CreateFixtureAsync();
        var started = await fixture.StartAuthorizeAsync(
            "openid todo:read",
            clientId: ThirdPartyClientId,
            redirectUri: ThirdPartyRedirect);
        var consentPage = await fixture.SubmitPasswordLoginExpectingConsentAsync(started);

        using var denied = await fixture.SubmitConsentDecisionAsync(consentPage, approve: false);

        var location = await HostedAuthorizeTokenFixture.ReadClientRedirectAsync(denied);
        location.AbsoluteUri.Should().StartWith(ThirdPartyRedirect);
        var query = QueryHelpers.ParseQuery(location.Query);
        query["error"].ToString().Should().Be("access_denied");
        query.ContainsKey("code").Should().BeFalse();

        await using var scope = fixture.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        var request = await db.Set<SqlOSAuthorizationRequest>().SingleAsync(x => x.Id == consentPage.RequestId);
        request.CancelledAt.Should().NotBeNull();
        (await db.Set<SqlOSConsentGrant>().CountAsync(x => x.UserId == fixture.UserId)).Should().Be(0);
        (await db.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "oauth.consent.denied"))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task ThirdParty_PromptConsent_RePromptsDespiteCoveringGrant()
    {
        await using var fixture = await CreateFixtureAsync();
        var authPageCookie = await ApproveInitialConsentAsync(fixture, "openid todo:read");

        using var forced = await fixture.AuthorizeWithSessionAsync(
            "openid todo:read",
            authPageCookie,
            prompt: "consent",
            clientId: ThirdPartyClientId,
            redirectUri: ThirdPartyRedirect);

        forced.Response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await forced.Response.Content.ReadAsStringAsync();
        html.Should().Contain("/sqlos/auth/consent/approve");
    }

    [TestMethod]
    public async Task ThirdParty_PromptNone_WithoutCoveringGrant_ReturnsConsentRequired()
    {
        await using var fixture = await CreateFixtureAsync();
        // Establish a live auth-page session through the first-party client so the
        // third-party prompt=none request fails on consent, not on login.
        var firstPartyStart = await fixture.StartAuthorizeAsync("openid");
        var firstPartyLogin = await fixture.SubmitPasswordLoginWithSessionAsync(firstPartyStart);

        using var denied = await fixture.AuthorizeWithSessionAsync(
            "openid todo:read",
            firstPartyLogin.AuthPageCookie,
            prompt: "none",
            clientId: ThirdPartyClientId,
            redirectUri: ThirdPartyRedirect);

        denied.Response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = denied.Response.Headers.Location!;
        location.AbsoluteUri.Should().StartWith(ThirdPartyRedirect);
        QueryHelpers.ParseQuery(location.Query)["error"].ToString().Should().Be("consent_required");
    }

    [TestMethod]
    public async Task ThirdParty_PromptNone_WithCoveringGrantAndSession_IssuesCodeSilently()
    {
        await using var fixture = await CreateFixtureAsync();
        var authPageCookie = await ApproveInitialConsentAsync(fixture, "openid todo:read");

        using var silent = await fixture.AuthorizeWithSessionAsync(
            "openid todo:read",
            authPageCookie,
            prompt: "none",
            clientId: ThirdPartyClientId,
            redirectUri: ThirdPartyRedirect);

        silent.Response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = silent.Response.Headers.Location!;
        location.AbsoluteUri.Should().StartWith(ThirdPartyRedirect);
        QueryHelpers.ParseQuery(location.Query)["code"].ToString().Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task FirstParty_FlowIsUnchanged_NoConsentPageEver()
    {
        await using var fixture = await CreateFixtureAsync();

        // Password login redirects straight to the app with a code (the helper throws if
        // anything other than a code redirect comes back, pinning "no consent page").
        var started = await fixture.StartAuthorizeAsync("openid");
        var login = await fixture.SubmitPasswordLoginWithSessionAsync(started);
        login.Code.Should().NotBeNullOrWhiteSpace();
        login.Location.Should().NotContain("consent");

        // Silent SSO reuse also stays consent-free.
        using var silent = await fixture.AuthorizeWithSessionAsync("openid", login.AuthPageCookie);
        silent.Response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        QueryHelpers.ParseQuery(silent.Response.Headers.Location!.Query)["code"].ToString()
            .Should().NotBeNullOrWhiteSpace();

        await using var scope = fixture.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await db.Set<SqlOSConsentGrant>().CountAsync()).Should().Be(0, "first-party flows never write consent grants");
    }

    [TestMethod]
    public async Task AccountGrantSurfaces_RejectThirdPartyRefreshTokens_AndStillServeFirstPartySessions()
    {
        await using var fixture = await CreateFixtureAsync();

        // Third-party session: approve consent for the third-party client and exchange its code.
        var thirdPartyStart = await fixture.StartAuthorizeAsync(
            "openid todo:read",
            clientId: ThirdPartyClientId,
            redirectUri: ThirdPartyRedirect);
        var consentPage = await fixture.SubmitPasswordLoginExpectingConsentAsync(thirdPartyStart);
        using var approved = await fixture.SubmitConsentDecisionAsync(consentPage, approve: true);
        var thirdPartyCode = QueryHelpers.ParseQuery((await HostedAuthorizeTokenFixture.ReadClientRedirectAsync(approved)).Query)["code"].ToString();
        using var thirdPartyTokens = await fixture.ExchangeAuthorizationCodeAsync(
            thirdPartyCode,
            thirdPartyStart.CodeVerifier,
            ThirdPartyClientId,
            ThirdPartyRedirect);
        var thirdPartyRefreshToken = thirdPartyTokens.RootElement.GetProperty("refresh_token").GetString();
        thirdPartyRefreshToken.Should().NotBeNullOrWhiteSpace();

        // First-party session for the same user.
        var firstPartyStart = await fixture.StartAuthorizeAsync("openid");
        var firstPartyCode = await fixture.SubmitPasswordLoginAsync(firstPartyStart);
        using var firstPartyTokens = await fixture.ExchangeAuthorizationCodeAsync(
            firstPartyCode,
            firstPartyStart.CodeVerifier);
        var firstPartyRefreshToken = firstPartyTokens.RootElement.GetProperty("refresh_token").GetString();
        firstPartyRefreshToken.Should().NotBeNullOrWhiteSpace();

        // The third-party refresh token gets the same generic 401 as an invalid token for
        // both surfaces: it must not enumerate or revoke the user's grants for other clients.
        using var thirdPartyList = await fixture.Client.PostAsJsonAsync(
            "/sqlos/auth/account/grants",
            new { refreshToken = thirdPartyRefreshToken });
        thirdPartyList.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        string grantId;
        await using (var scope = fixture.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            grantId = (await db.Set<SqlOSConsentGrant>()
                .SingleAsync(x => x.UserId == fixture.UserId && x.RevokedAt == null)).Id;
        }

        using var thirdPartyRevoke = await fixture.Client.PostAsJsonAsync(
            "/sqlos/auth/account/grants/revoke",
            new { refreshToken = thirdPartyRefreshToken, grantId });
        thirdPartyRevoke.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await using (var scope = fixture.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            (await db.Set<SqlOSConsentGrant>().SingleAsync(x => x.Id == grantId)).RevokedAt
                .Should().BeNull("a third-party session must not revoke grants");
        }

        // The first-party session still lists and revokes the user's grants.
        using var firstPartyList = await fixture.Client.PostAsJsonAsync(
            "/sqlos/auth/account/grants",
            new { refreshToken = firstPartyRefreshToken });
        firstPartyList.StatusCode.Should().Be(HttpStatusCode.OK);
        using var listBody = JsonDocument.Parse(await firstPartyList.Content.ReadAsStringAsync());
        listBody.RootElement.GetProperty("data").EnumerateArray()
            .Select(x => x.GetProperty("id").GetString())
            .Should().Contain(grantId);

        using var firstPartyRevoke = await fixture.Client.PostAsJsonAsync(
            "/sqlos/auth/account/grants/revoke",
            new { refreshToken = firstPartyRefreshToken, grantId });
        firstPartyRevoke.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var scope = fixture.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            (await db.Set<SqlOSConsentGrant>().SingleAsync(x => x.Id == grantId)).RevokedAt
                .Should().NotBeNull();
        }
    }

    private static Task<HostedAuthorizeTokenFixture> CreateFixtureAsync()
        => HostedAuthorizeTokenFixture.CreateAsync("HostedConsent", options =>
        {
            options.AuthServer.SeedClient(client =>
            {
                client.ClientId = ThirdPartyClientId;
                client.Name = "Third Party Consent Client";
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
        });

    /// <summary>
    /// Runs the full first-visit consent approval for the third-party client and returns the
    /// auth-page session cookie set alongside the issued code.
    /// </summary>
    private static async Task<string> ApproveInitialConsentAsync(
        HostedAuthorizeTokenFixture fixture,
        string scope)
    {
        var started = await fixture.StartAuthorizeAsync(
            scope,
            clientId: ThirdPartyClientId,
            redirectUri: ThirdPartyRedirect);
        var consentPage = await fixture.SubmitPasswordLoginExpectingConsentAsync(started);
        using var approved = await fixture.SubmitConsentDecisionAsync(consentPage, approve: true);
        QueryHelpers.ParseQuery((await HostedAuthorizeTokenFixture.ReadClientRedirectAsync(approved)).Query)["code"].ToString()
            .Should().NotBeNullOrWhiteSpace();
        return HostedAuthorizeTokenFixture.TryExtractCookie(approved, "sqlos_auth_page=")
            ?? throw new InvalidOperationException("Consent approval did not establish an auth-page session.");
    }
}

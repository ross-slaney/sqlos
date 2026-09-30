using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Protocol.ProtocolForms;

namespace SqlOS.BehaviorLock.Scenarios.Protocol;

/// <summary>
/// Browser sign-out: <c>GET /sqlos/auth/logout</c> ends the issuer (AuthPage) session and
/// redirects, and <c>GET /sqlos/auth/logged-out</c> renders the signed-out page.
/// </summary>
[TestClass]
public sealed class BrowserSessionScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/logout")]
    [Covers("GET /sqlos/auth/logged-out")]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("GET /api/me")]
    public async Task Signing_out_of_the_browser_ends_the_issuer_session_but_not_the_issued_tokens()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var session = await t.Setup.SignInWithPasswordAsync(alice);

        var loggedOut = t.Observe(await t.GetAsync("/sqlos/auth/logout"), "Alice signs out: the issuer cookie is deleted");
        t.ObservePage(await t.GetAsync(loggedOut.Location!), "the signed-out page, which deletes the cookie again");
        t.Observe(await t.GetAsync(t.Urls.Authorize().Url), "the next authorization request asks her to sign in");
        t.ObserveTokens(
            await t.Api.PostFormAsync("/sqlos/auth/token", Refresh(BehaviorLockConstants.AppClientId, session.RefreshToken)),
            "the refresh token issued before the sign-out still rotates");
        t.ObserveResource(await t.Api.GetAsync("/api/me", options => options.Bearer(session.AccessToken)), "and the access token still opens the API");
        t.Observe(await t.NewBrowser("never-signed-in").GetAsync("/sqlos/auth/logout"), "signing out without a session still deletes the cookie");

        await t.ObserveAuditAsync("browser sign-out writes no audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/logout")]
    public async Task Post_logout_redirects_follow_local_paths_and_known_origins_only()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var browser = t.NewBrowser("browser");
        async Task SignOutAsync(string query, string caption)
            => t.Observe(await browser.GetAsync("/sqlos/auth/logout" + query), caption);

        await SignOutAsync("?returnTo=%2Fdashboard%3Ftab%3D1%23section", "a local path, with its query and fragment");
        await SignOutAsync("?returnTo=https%3A%2F%2Fsqlos.example.test%2Fsigned-out", "an absolute URL on the public origin");
        await SignOutAsync("?post_logout_redirect_uri=https%3A%2F%2Fsqlos.example.test%2Fafter-logout", "post_logout_redirect_uri when returnTo is absent");
        await SignOutAsync("?returnTo=https%3A%2F%2Fsqlos.example.test%2Fnext%3Fq%3Da%2526b", "an encoded ampersand in the query of an absolute URL");
        await SignOutAsync("?returnTo=https%3A%2F%2Fattacker.example%2Fphish", "a foreign origin falls back to the signed-out page");
        await SignOutAsync("?returnTo=%2F%2Fattacker.example%2Fphish", "a protocol-relative URL");
        await SignOutAsync("?returnTo=%2F%252F%252Fattacker.example", "an encoded protocol-relative path");
        await SignOutAsync("?returnTo=javascript%3Aalert(1)", "a javascript: URL");
        await SignOutAsync("?returnTo=settings", "a relative path without a leading slash");
        await SignOutAsync("?returnTo=https%3A%2F%2Fattacker.example%2Fphish&post_logout_redirect_uri=%2Fhome", "a rejected returnTo does not fall back to post_logout_redirect_uri");
        await SignOutAsync("?post_logout_redirect_uri=%2Fhome&id_token_hint=ignored&state=ignored-state", "id_token_hint and state are ignored, and state is not echoed");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/logout")]
    [Covers("POST /sqlos/auth/register")]
    public async Task A_dynamically_registered_redirect_origin_becomes_an_allowed_post_logout_target()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Dcr);
        var browser = t.NewBrowser("victim-browser");
        const string phishing = "https://attacker.example/phish";

        t.Observe(await browser.GetAsync("/sqlos/auth/logout?returnTo=" + Uri.EscapeDataString(phishing)), "before any registration the foreign origin is refused");
        await t.ObserveWithAuditAsync(
            await t.NewClient("attacker").PostJsonAsync("/sqlos/auth/register", PublicRegistration("Totally Legit", "https://attacker.example/callback")),
            "an unauthenticated caller registers a client whose redirect URI is on the attacker's origin");
        t.Observe(
            await browser.GetAsync("/sqlos/auth/logout?returnTo=" + Uri.EscapeDataString(phishing)),
            "now SqlOS redirects a signed-out user to any path on that origin");

        await t.ApproveAsync();
    }
}

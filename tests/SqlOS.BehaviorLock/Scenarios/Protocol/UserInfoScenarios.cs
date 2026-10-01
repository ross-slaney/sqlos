using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Protocol;

/// <summary>
/// The OpenID Connect UserInfo endpoint: claim release by granted scope, both RFC 6750 token
/// transports, and every Bearer challenge it answers with.
/// </summary>
[TestClass]
public sealed class UserInfoScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/userinfo")]
    [Covers("POST /sqlos/auth/userinfo")]
    public async Task UserInfo_releases_claims_by_granted_scope_through_either_token_transport()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var full = await t.Setup.SignInWithPasswordAsync(alice);
        var client = t.NewClient("relying-party");

        t.Observe(await client.GetAsync("/sqlos/auth/userinfo", options => options.Bearer(full.AccessToken)), "openid profile email: identity, profile, and email claims");
        t.Observe(
            await client.PostFormAsync("/sqlos/auth/userinfo", ProtocolForms.Form(("access_token", full.AccessToken))),
            "the same token as the access_token form parameter (RFC 6750 §2.2)");

        var openIdOnly = await t.Setup.SignInWithPasswordAsync(
            alice,
            t.Urls.Authorize(scope: "openid", extra: new Dictionary<string, string?> { ["view"] = "password" }),
            t.NewBrowser("openid-only-browser"));
        t.Observe(await client.GetAsync("/sqlos/auth/userinfo", options => options.Bearer(openIdOnly.AccessToken)), "openid alone releases the subject and amr only");

        var noOpenId = await t.Setup.SignInWithPasswordAsync(
            alice,
            t.Urls.Authorize(scope: "profile email", extra: new Dictionary<string, string?> { ["view"] = "password" }),
            t.NewBrowser("oauth-browser"));
        t.Observe(await client.GetAsync("/sqlos/auth/userinfo", options => options.Bearer(noOpenId.AccessToken)), "a token granted without openid gets insufficient_scope");

        await t.ObserveAuditAsync("userinfo writes no audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/userinfo")]
    [Covers("POST /sqlos/auth/userinfo")]
    public async Task UserInfo_challenges_missing_ambiguous_invalid_and_revoked_tokens()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var session = await t.Setup.SignInWithPasswordAsync(alice);
        var client = t.NewClient("relying-party");
        t.Scrub(session.IdToken!, "id-token");

        t.Observe(await client.GetAsync("/sqlos/auth/userinfo"), "no token: a bare Bearer challenge");
        t.Observe(await client.PostFormAsync("/sqlos/auth/userinfo", ProtocolForms.Form(("access_token", ""))), "an empty access_token form field counts as no token");
        t.Observe(await client.GetAsync("/sqlos/auth/userinfo", options => options.Bearer("not-a-jwt")), "a malformed token is invalid_token");
        t.Observe(await client.GetAsync("/sqlos/auth/userinfo", options => options.Bearer(session.IdToken!)), "an ID token is not an access token");
        t.Observe(
            await client.PostFormAsync(
                "/sqlos/auth/userinfo",
                ProtocolForms.Form(("access_token", session.AccessToken)),
                options => options.Bearer(session.AccessToken)),
            "the header and the form parameter together are ambiguous (RFC 6750 §2)");

        t.Discard(await t.Api.PostJsonAsync("/sqlos/auth/logout", new { refreshToken = session.RefreshToken }));
        await t.SkipAuditAsync();
        t.Note("Alice signs out with her refresh token; the access token has not expired.");
        t.Observe(await client.GetAsync("/sqlos/auth/userinfo", options => options.Bearer(session.AccessToken)), "UserInfo refuses the token of a revoked session");

        await t.ApproveAsync();
    }
}

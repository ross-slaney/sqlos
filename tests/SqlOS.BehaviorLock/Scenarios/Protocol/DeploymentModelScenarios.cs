using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Protocol.ProtocolForms;

namespace SqlOS.BehaviorLock.Scenarios.Protocol;

/// <summary>
/// The authorization-code journey in a deployment model that changes it: an OAuth-only server
/// (no OpenID Provider role). The explicitly wired legacy host is locked by the admin surface's
/// <c>LegacyWiringScenarios</c>.
/// </summary>
[TestClass]
public sealed class DeploymentModelScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("GET /api/me")]
    public async Task An_oauth_only_server_issues_no_id_token_even_when_openid_is_requested()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.OAuthOnly);
        var alice = await t.Setup.CreateUserAsync("alice");
        var request = t.Urls.Authorize(extra: new Dictionary<string, string?> { ["view"] = "password" });

        var page = t.Observe(await t.GetAsync(request.Url), "the password page");
        var login = t.Observe(await t.SubmitAsync(page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)), "Alice signs in");
        var tokens = t.ObserveTokens(await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(login.NextUrlParameter("code"))), "the code redeems without an ID token");
        t.ObserveTokens(
            await t.Api.PostFormAsync("/sqlos/auth/token", Refresh(BehaviorLockConstants.AppClientId, tokens.JsonString("refresh_token"))),
            "and so does the refresh");
        t.Observe(await t.Api.GetAsync("/api/me", options => options.Bearer(tokens.JsonString("access_token"))), "the access token opens the API");

        await t.ObserveAuditAsync("events");
        await t.ApproveAsync();
    }
}

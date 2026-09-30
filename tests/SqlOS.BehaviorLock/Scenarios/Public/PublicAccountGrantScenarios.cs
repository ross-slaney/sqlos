using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Public;

/// <summary>
/// Account self-service over remembered consent: <c>POST /sqlos/auth/account/grants</c> lists the
/// user's grants and <c>POST /sqlos/auth/account/grants/revoke</c> revokes one. The credential is a
/// refresh token of a first-party session; any other token gets the same bare 401, so the answer
/// does not reveal how SqlOS classifies the client.
/// </summary>
[TestClass]
public sealed class PublicAccountGrantScenarios
{
    private const string PartnerClientId = "partner-app";
    private const string PartnerRedirectUri = "https://partner.example.test/callback";

    [Scenario]
    [Covers("POST /sqlos/auth/account/grants")]
    [Covers("POST /sqlos/auth/account/grants/revoke")]
    public async Task A_first_party_session_lists_and_revokes_remembered_consent()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var bob = await t.Setup.CreateUserAsync("bob");
        await t.CreateClientAsync(PartnerClientId, "Partner App", isFirstParty: false, PartnerRedirectUri);
        var partnerRefresh = await t.ConsentThroughHostedSignInAsync(alice, PartnerClientId, PartnerRedirectUri);
        var aliceRefresh = (await t.PasswordLoginAsync(alice))["tokens"]!["refreshToken"]!.GetValue<string>();
        var bobRefresh = (await t.PasswordLoginAsync(bob))["tokens"]!["refreshToken"]!.GetValue<string>();

        var listed = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/account/grants", new { refreshToken = aliceRefresh }),
            "Alice's first-party session lists her remembered consent");
        var grantId = listed.JsonString("data.0.id");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/account/grants", new { refreshToken = partnerRefresh }),
            "the partner's own refresh token is refused like an invalid one");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/account/grants", new { refreshToken = "not-a-refresh-token" }),
            "an unknown refresh token");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/account/grants", new { }),
            "no refresh token");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/account/grants/revoke", new { refreshToken = partnerRefresh, grantId }),
            "the partner cannot revoke its own grant");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/account/grants/revoke", new { refreshToken = bobRefresh, grantId }),
            "Bob's session cannot revoke Alice's grant: it is not found for him");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/account/grants/revoke", new { refreshToken = aliceRefresh, grantId }),
            "Alice revokes the grant");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/account/grants/revoke", new { refreshToken = aliceRefresh, grantId }),
            "revoking it again");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/account/grants/revoke", new { refreshToken = aliceRefresh }),
            "revoking without a grant id");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/account/grants", new { refreshToken = aliceRefresh }),
            "the list is empty");

        await t.ObserveAuditAsync("the revocation and the mapped failures");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}/grants", "the operator view of Alice's grants");
        await t.ApproveAsync();
    }
}

using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Headless.HeadlessJourney;

namespace SqlOS.BehaviorLock.Scenarios.Headless;

/// <summary>
/// The consent interstitial a third-party client's authorization reaches in the headless UI:
/// approve, deny, and the remembered grant.
/// </summary>
[TestClass]
public sealed class HeadlessConsentScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/headless/password/login")]
    [Covers("POST /sqlos/auth/headless/consent/approve")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/grants")]
    public async Task A_third_party_client_gets_a_code_after_the_user_approves_consent_and_the_grant_is_remembered()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await CreatePartnerClientAsync(t);
        var alice = await t.Setup.CreateUserAsync("alice");
        var request = t.Urls.Authorize(PartnerClientId, PartnerRedirectUri);
        var requestId = await OpenAuthorizeAsync(t, request, "a third-party client's authorization also lands in the app's UI");

        var login = t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = alice.Email, password = alice.Password }),
            "sign in: the client is not first party, so the UI must ask for consent");
        var approved = t.Observe(
            await t.PostJsonAsync($"{Api}/consent/approve", new { requestId, consentToken = login.JsonString("viewModel.consentToken") }),
            "approve: a redirect to the partner with a code");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(approved))),
            "the partner redeems the authorization code");
        t.Observe(
            await t.PostJsonAsync($"{Api}/consent/approve", new { requestId, consentToken = login.JsonString("viewModel.consentToken") }),
            "the consent token cannot be replayed");

        var again = t.Urls.Authorize(PartnerClientId, PartnerRedirectUri);
        t.Observe(await t.GetAsync(again.Url), "authorizing the partner again in the same browser skips the UI: the grant is remembered");

        await t.ObserveAuditAsync("consent events");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}/grants", "the remembered grant");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/password/login")]
    [Covers("POST /sqlos/auth/headless/consent/deny")]
    [Covers("POST /sqlos/auth/headless/consent/approve")]
    public async Task Denying_consent_sends_the_partner_access_denied_and_issues_nothing()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await CreatePartnerClientAsync(t);
        var alice = await t.Setup.CreateUserAsync("alice");
        var request = t.Urls.Authorize(PartnerClientId, PartnerRedirectUri);
        var requestId = await OpenAuthorizeAsync(t, request);

        var login = t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = alice.Email, password = alice.Password }),
            "sign in: the UI asks for consent");
        var consentToken = login.JsonString("viewModel.consentToken");
        t.Observe(
            await t.PostJsonAsync($"{Api}/consent/deny", new { requestId, consentToken }),
            "deny: a redirect to the partner with access_denied");
        t.Observe(
            await t.PostJsonAsync($"{Api}/consent/approve", new { requestId, consentToken }),
            "the denied request cannot be approved afterwards");

        await t.ObserveAuditAsync("consent denial events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/consent/approve")]
    [Covers("POST /sqlos/auth/headless/consent/deny")]
    public async Task Consent_decisions_reject_a_token_minted_for_another_request_without_burning_it()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        await CreatePartnerClientAsync(t);
        var alice = await t.Setup.CreateUserAsync("alice");
        var bob = await t.Setup.CreateUserAsync("bob");
        var bobsBrowser = t.NewBrowser("bobs-browser");
        var aliceRequestId = await OpenAuthorizeAsync(t, t.Urls.Authorize(PartnerClientId, PartnerRedirectUri));
        var bobRequestId = await OpenAuthorizeAsync(t, t.Urls.Authorize(PartnerClientId, PartnerRedirectUri), browser: bobsBrowser);
        var aliceLogin = t.Discard(await t.PostJsonAsync($"{Api}/password/login", new { requestId = aliceRequestId, email = alice.Email, password = alice.Password }));
        var bobLogin = t.Discard(await bobsBrowser.PostJsonAsync($"{Api}/password/login", new { requestId = bobRequestId, email = bob.Email, password = bob.Password }));
        var aliceToken = aliceLogin.JsonString("viewModel.consentToken");
        var bobToken = bobLogin.JsonString("viewModel.consentToken");

        t.Observe(
            await t.PostJsonAsync($"{Api}/consent/approve", new { requestId = aliceRequestId, consentToken = bobToken }),
            "approve alice's request with the consent token minted for bob's request: rejected");
        t.Observe(
            await t.PostJsonAsync($"{Api}/consent/deny", new { requestId = aliceRequestId, consentToken = bobToken }),
            "deny alice's request with bob's token: rejected the same way");
        t.Observe(
            await t.PostJsonAsync($"{Api}/consent/approve", new { requestId = aliceRequestId, consentToken = "not-a-consent-token" }),
            "an unknown consent token");
        t.Observe(
            await t.PostJsonAsync($"{Api}/consent/approve", new { requestId = aliceRequestId, consentToken = aliceToken }),
            "alice's own token still approves: a redirect with a code");
        t.Observe(
            await bobsBrowser.PostJsonAsync($"{Api}/consent/deny", new { requestId = bobRequestId, consentToken = bobToken }),
            "bob's token was not burned by the rejected attempts: bob can still deny");

        await t.ObserveAuditAsync("consent binding events");
        await t.ApproveAsync();
    }
}

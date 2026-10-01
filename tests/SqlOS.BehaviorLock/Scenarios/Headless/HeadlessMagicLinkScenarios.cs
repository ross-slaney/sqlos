using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Headless.HeadlessJourney;

namespace SqlOS.BehaviorLock.Scenarios.Headless;

/// <summary>Magic-link sign-in through the headless API.</summary>
[TestClass]
public sealed class HeadlessMagicLinkScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/headless/magic-link/start")]
    [Covers("POST /sqlos/auth/headless/magic-link/complete")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Magic_link_sign_in_completes_on_the_requesting_ui()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var dana = await t.Setup.CreateUserAsync("dana");
        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request);

        t.Observe(
            await t.PostJsonAsync($"{Api}/magic-link/start", new { requestId, email = dana.Email }),
            "request a sign-in link");
        var token = EmailLinkToken(t, dana.Email);
        var completed = t.Observe(
            await t.PostJsonAsync($"{Api}/magic-link/complete", new { token, requestId }),
            "the UI posts the link token back with its request: a redirect with a code");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(completed))),
            "redeem the authorization code");
        t.Observe(
            await t.PostJsonAsync($"{Api}/magic-link/complete", new { token, requestId }),
            "the link cannot be used twice");

        await t.ObserveAuditAsync("magic-link events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/magic-link/complete")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_link_opened_without_the_request_id_still_completes_the_request_it_was_sent_for()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var dana = await t.Setup.CreateUserAsync("dana");
        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request);
        t.Discard(await t.PostJsonAsync($"{Api}/magic-link/start", new { requestId, email = dana.Email }));
        var token = EmailLinkToken(t, dana.Email);

        var completed = t.Observe(
            await t.NewBrowser("mail-app").PostJsonAsync($"{Api}/magic-link/complete", new { token }),
            "the link is opened in another browser that knows no request: the link's own request completes there");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(completed))),
            "the original app redeems the code with its PKCE verifier");

        await t.ObserveAuditAsync("cross-browser magic-link events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/magic-link/complete")]
    public async Task A_link_presented_with_another_request_is_rejected_without_spending_it()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var dana = await t.Setup.CreateUserAsync("dana");
        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize());
        var otherRequestId = await OpenAuthorizeAsync(t, t.Urls.Authorize(), browser: t.NewBrowser("attacker"));
        t.Discard(await t.PostJsonAsync($"{Api}/magic-link/start", new { requestId, email = dana.Email }));
        var token = EmailLinkToken(t, dana.Email);

        t.Observe(
            await t.PostJsonAsync($"{Api}/magic-link/complete", new { token, requestId = otherRequestId }),
            "the link with another authorization request: rejected");
        t.Observe(
            await t.PostJsonAsync($"{Api}/magic-link/complete", new { token = "not-a-magic-link-token" }),
            "an unknown link token");
        t.Observe(
            await t.PostJsonAsync($"{Api}/magic-link/complete", new { token, requestId }),
            "the link still completes its own request");

        await t.ObserveAuditAsync("magic-link rejection events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/magic-link/start")]
    public async Task Links_are_not_sent_to_unknown_addresses_and_resends_wait_for_the_cooldown()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var dana = await t.Setup.CreateUserAsync("dana");
        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize());
        var nobody = t.Unique.Email("nobody");

        t.Observe(
            await t.PostJsonAsync($"{Api}/magic-link/start", new { requestId, email = nobody }),
            "an address with no account: the same sent view, and no email");
        t.Observe(
            await t.PostJsonAsync($"{Api}/magic-link/start", new { requestId, email = dana.Email }),
            "a real account: the link is sent");
        t.Observe(
            await t.PostJsonAsync($"{Api}/magic-link/start", new { requestId, email = dana.Email }),
            "a resend inside the cooldown: the magic-link view with the wait message");
        t.Observe(
            await t.PostJsonAsync($"{Api}/magic-link/start", new { requestId, email = "" }),
            "no address");

        await t.ObserveAuditAsync("magic-link request events");
        await t.ApproveAsync();
    }
}

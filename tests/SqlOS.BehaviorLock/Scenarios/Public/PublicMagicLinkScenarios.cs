using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Public;

/// <summary>
/// Sign-in links through the public account API: <c>POST /sqlos/auth/magic-link/start</c> emails a
/// one-time link, and <c>POST /sqlos/auth/magic-link/complete</c> trades its token for tokens. The
/// emailed link points at the hosted completion page; an application that renders its own UI
/// posts the token to the public route instead.
/// </summary>
[TestClass]
public sealed class PublicMagicLinkScenarios
{
    private const string Client = BehaviorLockConstants.AppClientId;

    [Scenario]
    [Covers("POST /sqlos/auth/magic-link/start")]
    [Covers("POST /sqlos/auth/magic-link/complete")]
    public async Task Sign_in_link_verifies_the_address_and_returns_tokens()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/magic-link/start", new { email = alice.Email, clientId = Client }),
            "request a sign-in link; it is emailed to the account's address");
        var token = t.LatestEmailLinkToken(alice.Email);
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/magic-link/complete", new { token }),
            "complete with the link's token: tokens come back in the body");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/magic-link/complete", new { token }),
            "the link cannot be used twice");

        await t.ObserveAuditAsync("sign-in link events");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}", "the link proved the mailbox: the email is now verified");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/magic-link/start")]
    [Covers("POST /sqlos/auth/magic-link/complete")]
    public async Task An_unknown_address_gets_the_same_answer_and_forged_links_are_rejected()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/magic-link/start", new { email = t.Unique.Email("nobody"), clientId = Client }),
            "an unknown address: the usual answer, and nothing is sent");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/magic-link/start", new { email = "not an address", clientId = Client }),
            "an invalid address escapes the endpoint");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/magic-link/complete", new { token = "forged-link-token-that-was-never-issued" }),
            "a token SqlOS never issued is rejected and audited");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/magic-link/complete", new { token = "   " }),
            "a blank token is rejected before any lookup");

        await t.ObserveAuditAsync("the unsent link and the rejected completion");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/magic-link/start")]
    public async Task Link_requests_are_throttled_per_context_and_per_address()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/magic-link/start", new { email = alice.Email, clientId = Client }),
            "first request");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/magic-link/start", new { email = alice.Email, clientId = Client }),
            "the same request inside the 30-second resend cooldown escapes the endpoint");
        for (var context = 2; context <= 5; context++)
        {
            t.Observe(
                await t.Api.PostJsonAsync("/sqlos/auth/magic-link/start", new { email = alice.Email, clientId = Client, organizationId = $"workspace-{context.ToString(CultureInfo.InvariantCulture)}" }),
                $"another requested organization is another context (link {context.ToString(CultureInfo.InvariantCulture)} this hour)");
        }

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/magic-link/start", new { email = alice.Email, clientId = Client, organizationId = "workspace-6" }),
            "the sixth link for the address this hour is refused");

        await t.ObserveAuditAsync("five links and the rate-limit rejection");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("GET /sqlos/auth/login/magic-link")]
    [Covers("POST /sqlos/auth/login/magic-link/start")]
    [Covers("POST /sqlos/auth/magic-link/complete")]
    [Covers("GET /sqlos/auth/login/magic-link/complete")]
    [Covers("POST /sqlos/auth/login/magic-link/complete")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_link_started_on_the_hosted_page_cannot_be_redeemed_through_the_public_api()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var request = t.Urls.Authorize();

        var authorize = t.Observe(await t.GetAsync(request.Url), "the relying party starts an authorization request");
        var requestId = authorize.Form("/login/identify")["requestId"];
        var linkPage = t.Observe(await t.GetAsync($"/sqlos/auth/login/magic-link?request={requestId}"), "open the hosted sign-in link page");
        t.Observe(
            await t.SubmitAsync(linkPage.Form("/login/magic-link/start").With("email", alice.Email)),
            "the hosted page emails a link bound to the authorization request");
        var token = t.LatestEmailLinkToken(alice.Email);

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/magic-link/complete", new { token }),
            "the public API refuses a link bound to an authorization request");

        var confirm = t.Observe(await t.GetAsync($"/sqlos/auth/login/magic-link/complete?token={Uri.EscapeDataString(token)}"), "the emailed link opens the hosted confirmation");
        var completed = t.Observe(await t.SubmitAsync(confirm.Form("/login/magic-link/complete")), "the refused attempt did not burn the link: the hosted flow completes");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(completed.NextUrlParameter("code"))),
            "the relying party redeems its code");

        await t.ObserveAuditAsync("hosted link events");
        await t.ApproveAsync();
    }
}

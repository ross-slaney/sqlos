using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Hosted;

/// <summary>
/// Hosted magic-link sign-in: the request page, the start form, the confirmation interstitial
/// (<c>GET /login/magic-link/complete</c>, which never consumes the link), and the completion
/// form, with the branches they handle distinctly and the 7.2.1 email-ownership attacks.
/// </summary>
[TestClass]
public sealed class HostedMagicLinkScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/login/magic-link")]
    [Covers("POST /sqlos/auth/login/magic-link/start")]
    [Covers("GET /sqlos/auth/login/magic-link/complete")]
    [Covers("POST /sqlos/auth/login/magic-link/complete")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_magic_link_asks_for_confirmation_then_completes_the_authorization_request()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await HostedFlows.CreateVerifiedEmailUserAsync(t, "alice");
        var begun = await HostedFlows.BeginAsync(t);

        var page = t.Observe(
            await t.GetAsync($"/sqlos/auth/login/magic-link?request={begun.RequestId}&email={Uri.EscapeDataString(alice.Email)}"),
            "follow 'Email me a sign-in link'");
        t.Observe(await t.SubmitAsync(page.Form("/login/magic-link/start")), "send the sign-in link");
        var token = HostedFlows.LinkToken(t, alice.Email);

        var confirm = t.Observe(
            await t.GetAsync($"/sqlos/auth/login/magic-link/complete?token={Uri.EscapeDataString(token)}"),
            "open the link: a confirmation page that does not consume it (mail scanners follow links)");
        var completed = t.Observe(
            await t.SubmitAsync(confirm.Form("/login/magic-link/complete")),
            "confirm: the interstitial returns to the application");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", begun.Request.TokenRequest(completed.NextUrlParameter("code"))),
            "redeem the authorization code");

        await t.ObserveAuditAsync("magic-link sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login/magic-link")]
    [Covers("POST /sqlos/auth/login/magic-link/start")]
    [Covers("GET /sqlos/auth/login/magic-link/complete")]
    [Covers("POST /sqlos/auth/login/magic-link/complete")]
    public async Task A_magic_link_without_an_authorization_request_starts_an_issuer_session()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await HostedFlows.CreateVerifiedEmailUserAsync(t, "alice");

        var page = t.Observe(await t.GetAsync("/sqlos/auth/login/magic-link"), "open the magic-link page directly");
        t.Observe(await t.SubmitAsync(page.Form("/login/magic-link/start").With("email", alice.Email)), "send the sign-in link");
        var token = HostedFlows.LinkToken(t, alice.Email);
        var confirm = t.Observe(
            await t.GetAsync($"/sqlos/auth/login/magic-link/complete?token={Uri.EscapeDataString(token)}"),
            "open the link");
        t.Observe(await t.SubmitAsync(confirm.Form("/login/magic-link/complete")), "confirm: signed in to SqlOS itself");

        await t.ObserveAuditAsync("standalone magic-link events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login/magic-link/complete")]
    public async Task Opening_the_completion_page_without_a_link_token_shows_the_sign_in_page()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);

        t.Observe(await t.GetAsync("/sqlos/auth/login/magic-link/complete"), "no token at all");
        t.Observe(await t.GetAsync("/sqlos/auth/login/magic-link/complete?token=%20"), "a blank token");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/magic-link/complete")]
    public async Task A_used_or_unknown_link_is_refused_on_confirmation()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await HostedFlows.CreateVerifiedEmailUserAsync(t, "alice");
        var page = t.Discard(await t.GetAsync("/sqlos/auth/login/magic-link"));
        t.Discard(await t.SubmitAsync(page.Form("/login/magic-link/start").With("email", alice.Email)));
        var token = HostedFlows.LinkToken(t, alice.Email);
        var confirm = t.Discard(await t.GetAsync($"/sqlos/auth/login/magic-link/complete?token={Uri.EscapeDataString(token)}"));
        HostedFlows.EnsureStatus(t.Discard(await t.SubmitAsync(confirm.Form("/login/magic-link/complete"))), 302);
        await t.SkipAuditAsync();

        t.Observe(await t.SubmitAsync(confirm.Form("/login/magic-link/complete")), "confirm the same link a second time");
        t.Observe(
            await t.SubmitAsync(confirm.Form("/login/magic-link/complete").With("token", "not-a-real-sign-in-link-token-value")),
            "confirm a token SqlOS never issued");

        await t.ObserveAuditAsync("both rejections are audited the same way");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/magic-link/complete")]
    public async Task A_link_whose_authorization_request_already_finished_is_consumed_and_refused()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var begun = await HostedFlows.BeginAsync(t, extra: new Dictionary<string, string?> { ["view"] = "password" });
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/magic-link?request={begun.RequestId}"));
        t.Discard(await t.SubmitAsync(page.Form("/login/magic-link/start").With("email", alice.Email)));
        var token = HostedFlows.LinkToken(t, alice.Email);
        // Meanwhile the same authorization request completes with the password.
        HostedFlows.EnsureStatus(
            t.Discard(await t.SubmitAsync(begun.Page.Form("/login/password").With("email", alice.Email).With("password", alice.Password))),
            200);
        await t.SkipAuditAsync();
        var confirm = t.Discard(await t.GetAsync($"/sqlos/auth/login/magic-link/complete?token={Uri.EscapeDataString(token)}"));

        t.Observe(
            await t.SubmitAsync(confirm.Form("/login/magic-link/complete")),
            "confirm the link after its authorization request completed");
        t.Observe(
            await t.SubmitAsync(confirm.Form("/login/magic-link/complete")),
            "the link was consumed by the failed attempt");

        await t.ObserveAuditAsync("the first attempt proved the mailbox before the request check failed");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/magic-link/start")]
    public async Task An_unknown_address_gets_the_same_answer_and_no_link()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var page = t.Discard(await t.GetAsync("/sqlos/auth/login/magic-link"));

        t.Observe(
            await t.SubmitAsync(page.Form("/login/magic-link/start").With("email", t.Unique.Email("nobody"))),
            "request a link for an address with no account");

        await t.ObserveAuditAsync("the request records that nothing was sent");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/magic-link/start")]
    public async Task A_second_link_within_the_cooldown_is_refused()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var page = t.Discard(await t.GetAsync("/sqlos/auth/login/magic-link"));
        var start = page.Form("/login/magic-link/start").With("email", alice.Email);
        t.Discard(await t.SubmitAsync(start));
        await t.SkipAuditAsync();

        t.Observe(await t.SubmitAsync(start), "ask for another link straight away");

        await t.ObserveAuditAsync("the cooldown writes no audit event");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/magic-link/start")]
    public async Task The_sixth_link_for_one_address_within_an_hour_is_rate_limited()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        for (var sent = 1; sent <= 5; sent++)
        {
            var begun = await HostedFlows.BeginAsync(t);
            var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/magic-link?request={begun.RequestId}"));
            HostedFlows.EnsureStatus(t.Discard(await t.SubmitAsync(page.Form("/login/magic-link/start").With("email", alice.Email))), 200);
        }

        await t.SkipAuditAsync();
        var sixth = await HostedFlows.BeginAsync(t);
        var sixthPage = t.Discard(await t.GetAsync($"/sqlos/auth/login/magic-link?request={sixth.RequestId}"));
        t.Observe(
            await t.SubmitAsync(sixthPage.Form("/login/magic-link/start").With("email", alice.Email)),
            "a sixth link for the same address in the hour");

        await t.ObserveAuditAsync("the per-address limit rejects it");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/magic-link/start")]
    public async Task The_sixty_first_link_from_one_ip_address_within_an_hour_is_rate_limited()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var page = t.Discard(await t.GetAsync("/sqlos/auth/login/magic-link"));
        var start = page.Form("/login/magic-link/start");
        for (var sent = 1; sent <= 60; sent++)
        {
            HostedFlows.EnsureStatus(
                t.Discard(await t.SubmitAsync(start.With("email", $"visitor-{sent}@unknown.example.test"))),
                200);
        }

        await t.SkipAuditAsync();
        t.Observe(
            await t.SubmitAsync(start.With("email", "visitor-61@unknown.example.test")),
            "a sixty-first link from the same IP address in the hour");

        await t.ObserveAuditAsync("the per-IP limit rejects it");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/magic-link/start")]
    public async Task Concurrent_link_requests_never_exceed_the_per_address_limit()
    {
        // #424, fixed: start admits the send with one atomic reservation of the address's, the IP
        // address's and the client's buckets before it writes anything. Of seven requests sent
        // together, five are admitted and reach their insert (the barrier holds them there) and
        // two are refused before writing, so five links go out, the limit. 7.2.1 loaded the recent
        // links, counted them in memory, then inserted its own, with no lock between: all seven
        // counted before any inserted, and seven links went out.
        var barrier = new HostedRaceBarrier("/sqlos/auth/login/magic-link/start", "INSERT INTO", "SqlOSTemporaryTokens", participants: 7);
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted, barrier.Install);
        var alice = await HostedFlows.CreateVerifiedEmailUserAsync(t, "alice");
        var forms = new List<HtmlForm>();
        for (var request = 1; request <= 7; request++)
        {
            var begun = await HostedFlows.BeginAsync(t);
            var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/magic-link?request={begun.RequestId}"));
            forms.Add(page.Form("/login/magic-link/start").With("email", alice.Email));
        }

        await t.SkipAuditAsync();
        var emailsBefore = t.Emails.Count;
        barrier.Arm();
        var (shown, others) = await barrier.PostTogetherAsync(t, forms);
        t.Observe(shown, "seven link requests at once for one address, one per tab (one shown, admitted)");
        t.Note($"The other six tabs were answered {string.Join(", ", others)}.");
        t.Note(
            $"{barrier.Held} start requests were admitted and held at their insert; the others were refused before writing anything.",
            baselineText: $"{barrier.Held} start requests were held at their insert until all had counted the recent links.");
        t.Note($"{t.Emails.Count - emailsBefore} sign-in links were emailed to the address; the limit is 5 per hour.");
        // The audit readback is left out on purpose: the fake sender numbers deliveries in the
        // order the concurrent requests reach it, so email.send.queued rows would not render
        // deterministically. The answers and the count above are the evidence.
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/magic-link/start")]
    public async Task Missing_and_malformed_addresses_are_rejected()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var page = t.Discard(await t.GetAsync("/sqlos/auth/login/magic-link"));
        var start = page.Form("/login/magic-link/start");

        t.Observe(await t.SubmitAsync(start.With("email", "")), "a blank address");
        t.Observe(await t.SubmitAsync(start.With("email", "not-an-address")), "a malformed address");

        await t.ObserveAuditAsync("only the malformed address is audited, as an opaque error");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login/magic-link")]
    [Covers("POST /sqlos/auth/login/magic-link/start")]
    [Covers("POST /sqlos/auth/login/magic-link/complete")]
    public async Task Magic_links_are_unavailable_when_the_application_does_not_enable_them()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");

        var page = t.Observe(await t.GetAsync("/sqlos/auth/login/magic-link"), "the magic-link page still renders");
        t.Observe(
            await t.SubmitAsync(page.Form("/login/magic-link/start").With("email", alice.Email)),
            "sending a link is refused");
        t.Observe(
            await t.PostFormAsync("/sqlos/auth/login/magic-link/complete", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = page.Form("/login/magic-link/start")["__RequestVerificationToken"],
                ["token"] = "not-a-real-sign-in-link-token-value"
            }),
            "completing one is refused too");

        await t.ObserveAuditAsync("nothing is audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/magic-link/start")]
    public async Task A_link_asked_for_with_another_spelling_goes_only_to_the_stored_address()
    {
        // #422: the link for an existing account goes to the stored address, not the typed spelling.
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await HostedFlows.CreateVerifiedEmailUserAsync(t, "alice");
        var page = t.Discard(await t.GetAsync("/sqlos/auth/login/magic-link"));

        var spelling = $" {alice.Email.ToUpperInvariant()} ";
        t.Scrub(spelling, "email", "ALICE-padded");
        t.Observe(
            await t.SubmitAsync(page.Form("/login/magic-link/start").With("email", spelling)),
            "ask for a link with an upper-case, padded spelling of the address");

        await t.ObserveAuditAsync("delivery events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/magic-link/start")]
    public async Task A_look_alike_address_never_receives_another_persons_link()
    {
        // #422: 'giþub' collated equal to 'github' on SQL Server's default collation.
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        await HostedFlows.CreateUserWithEmailAsync(t, "Bob", "bob@github.example", "Lock-Bob-2468!");
        var page = t.Discard(await t.GetAsync("/sqlos/auth/login/magic-link"));

        t.Observe(
            await t.SubmitAsync(page.Form("/login/magic-link/start").With("email", "bob@giþub.example")),
            "the attacker asks for a link for bob@giþub.example: no account, no link");

        await t.ObserveAuditAsync("the link request is not bound to Bob and nothing was sent");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login/magic-link/complete")]
    [Covers("POST /sqlos/auth/login/magic-link/complete")]
    [Covers("POST /sqlos/auth/login/password")]
    public async Task Completing_a_magic_link_claims_an_address_a_squatter_signed_up_with()
    {
        // #423: the owner's magic link proves the mailbox, so the squatter's password is revoked.
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var victimEmail = t.Unique.Email("victim");
        var squatterPassword = t.Unique.Password("squatter");
        var squatter = t.NewBrowser("squatter");
        var squatterSignup = t.Discard(await squatter.GetAsync("/sqlos/auth/signup"));
        HostedFlows.EnsureStatus(
            t.Discard(await squatter.SubmitAsync(squatterSignup.Form("/signup/submit")
                .With("displayName", "Victim")
                .With("email", victimEmail)
                .With("password", squatterPassword))),
            302);
        await t.SkipAuditAsync();

        var page = t.Discard(await t.GetAsync("/sqlos/auth/login/magic-link"));
        t.Discard(await t.SubmitAsync(page.Form("/login/magic-link/start").With("email", victimEmail)));
        var token = HostedFlows.LinkToken(t, victimEmail);
        var confirm = t.Observe(
            await t.GetAsync($"/sqlos/auth/login/magic-link/complete?token={Uri.EscapeDataString(token)}"),
            "the owner opens the link from their mailbox");
        t.Observe(await t.SubmitAsync(confirm.Form("/login/magic-link/complete")), "and confirms it");
        await t.ObserveAuditAsync("the link claims the address and revokes the squatter's password");

        var squatterAgain = await HostedFlows.BeginAsync(t, squatter, new Dictionary<string, string?> { ["view"] = "password" });
        t.Observe(
            await squatter.SubmitAsync(squatterAgain.Page.Form("/login/password").With("email", victimEmail).With("password", squatterPassword)),
            "the squatter's password no longer signs in");

        await t.ApproveAsync();
    }
}

using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Hosted;

/// <summary>
/// Hosted email-code sign-in: <c>/login/email-otp</c>, its start and verify forms, and the
/// branches those forms handle distinctly (unknown addresses, the resend cooldown, the per-address
/// and per-IP send limits, wrong, replayed, and cross-request codes, invalid addresses), plus the
/// 7.2.1 email-ownership attacks on this surface (#422 delivery and look-alikes, #423 claims).
/// </summary>
[TestClass]
public sealed class HostedEmailOtpScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/login/identify")]
    [Covers("POST /sqlos/auth/login/email-otp/start")]
    [Covers("POST /sqlos/auth/login/email-otp/verify")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Identify_then_email_code_signs_in_and_redeems_the_code()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await HostedFlows.CreateVerifiedEmailUserAsync(t, "alice");
        var request = t.Urls.Authorize();

        var authorize = t.Observe(await t.GetAsync(request.Url), "open the hosted sign-in page");
        var identified = t.Observe(
            await t.SubmitAsync(authorize.Form("/login/identify").With("email", alice.Email)),
            "identify: the email code is the preferred local factor");
        var started = t.Observe(
            await t.SubmitAsync(identified.Form("/login/email-otp/start")),
            "send the sign-in code");
        var verified = t.Observe(
            await t.SubmitAsync(started.Form("/login/email-otp/verify").With("code", HostedFlows.EmailCode(t, alice.Email))),
            "enter the code: a plain 302 to the application, not the interstitial other hosted forms use");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(verified.NextUrlParameter("code"))),
            "redeem the authorization code");

        await t.ObserveAuditAsync("email-code sign-in events (the address is already verified, so nothing is claimed)");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}/sessions", "the session the code redemption created");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login/email-otp")]
    [Covers("POST /sqlos/auth/login/email-otp/start")]
    [Covers("POST /sqlos/auth/login/email-otp/verify")]
    [Covers("GET /sqlos/auth/login")]
    public async Task Email_code_without_an_authorization_request_starts_an_issuer_session()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await HostedFlows.CreateVerifiedEmailUserAsync(t, "alice");

        var page = t.Observe(await t.GetAsync("/sqlos/auth/login/email-otp"), "open the email-code page directly");
        var started = t.Observe(
            await t.SubmitAsync(page.Form("/login/email-otp/start").With("email", alice.Email)),
            "send the sign-in code");
        var verified = t.Observe(
            await t.SubmitAsync(started.Form("/login/email-otp/verify").With("code", HostedFlows.EmailCode(t, alice.Email))),
            "enter the code: signed in to SqlOS itself");
        t.Observe(await t.GetAsync(verified.Location!), "the signed-in status page");

        await t.ObserveAuditAsync("standalone email-code sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login/email-otp")]
    [Covers("POST /sqlos/auth/login/email-otp/start")]
    [Covers("POST /sqlos/auth/login/email-otp/verify")]
    public async Task The_email_code_link_carries_the_authorization_request_and_the_address()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await HostedFlows.CreateVerifiedEmailUserAsync(t, "alice");
        var begun = await HostedFlows.BeginAsync(t);

        var page = t.Observe(
            await t.GetAsync($"/sqlos/auth/login/email-otp?request={begun.RequestId}&email={Uri.EscapeDataString(alice.Email)}"),
            "follow 'Use an email code instead' with the address prefilled");
        var started = t.Observe(await t.SubmitAsync(page.Form("/login/email-otp/start")), "send the sign-in code");
        t.Observe(
            await t.SubmitAsync(started.Form("/login/email-otp/verify").With("code", HostedFlows.EmailCode(t, alice.Email))),
            "enter the code");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/email-otp/start")]
    [Covers("POST /sqlos/auth/login/email-otp/verify")]
    public async Task An_unknown_address_gets_the_same_answer_and_no_email()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var begun = await HostedFlows.BeginAsync(t);
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/email-otp?request={begun.RequestId}"));

        var started = t.Observe(
            await t.SubmitAsync(page.Form("/login/email-otp/start").With("email", t.Unique.Email("nobody"))),
            "request a code for an address with no account: the same answer, and no email");
        t.Observe(
            await t.SubmitAsync(started.Form("/login/email-otp/verify").With("code", "123456")),
            "a guessed code is rejected like any wrong code");

        await t.ObserveAuditAsync("the challenge records that nothing was sent");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/email-otp/verify")]
    public async Task Five_wrong_codes_invalidate_the_challenge_and_the_right_code_then_fails()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await HostedFlows.CreateVerifiedEmailUserAsync(t, "alice");
        var begun = await HostedFlows.BeginAsync(t);
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/email-otp?request={begun.RequestId}"));
        var started = t.Discard(await t.SubmitAsync(page.Form("/login/email-otp/start").With("email", alice.Email)));
        await t.SkipAuditAsync();
        var code = HostedFlows.EmailCode(t, alice.Email);
        var wrong = HostedFlows.WrongCode(t, code);
        var verify = started.Form("/login/email-otp/verify");

        t.Observe(await t.SubmitAsync(verify.With("code", wrong)), "first wrong code");
        for (var attempt = 2; attempt <= 4; attempt++)
        {
            t.Discard(await t.SubmitAsync(verify.With("code", wrong)));
        }

        t.Observe(await t.SubmitAsync(verify.With("code", wrong)), "fifth wrong code: the challenge is invalidated");
        t.Observe(await t.SubmitAsync(verify.With("code", code)), "the right code no longer works");

        await t.ObserveAuditAsync("four wrong codes, then max_attempts");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/email-otp/verify")]
    public async Task A_code_without_digits_is_rejected_without_counting_an_attempt()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await HostedFlows.CreateVerifiedEmailUserAsync(t, "alice");
        var begun = await HostedFlows.BeginAsync(t);
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/email-otp?request={begun.RequestId}"));
        var started = t.Discard(await t.SubmitAsync(page.Form("/login/email-otp/start").With("email", alice.Email)));
        await t.SkipAuditAsync();
        var verify = started.Form("/login/email-otp/verify");

        t.Observe(await t.SubmitAsync(verify.With("code", "abc-def")), "a code with no digits");
        t.Observe(await t.SubmitAsync(verify.With("code", HostedFlows.EmailCode(t, alice.Email))), "the real code still works");

        await t.ObserveAuditAsync("no failed attempt was recorded");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/email-otp/verify")]
    public async Task A_verified_code_cannot_be_replayed()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await HostedFlows.CreateVerifiedEmailUserAsync(t, "alice");
        var begun = await HostedFlows.BeginAsync(t);
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/email-otp?request={begun.RequestId}"));
        var started = t.Discard(await t.SubmitAsync(page.Form("/login/email-otp/start").With("email", alice.Email)));
        var verify = started.Form("/login/email-otp/verify").With("code", HostedFlows.EmailCode(t, alice.Email));
        t.Discard(await t.SubmitAsync(verify));
        await t.SkipAuditAsync();

        t.Observe(await t.SubmitAsync(verify), "post the same verified code again");

        await t.ObserveAuditAsync("the replay writes nothing");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/email-otp/verify")]
    public async Task A_code_started_for_one_authorization_request_cannot_complete_another()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await HostedFlows.CreateVerifiedEmailUserAsync(t, "alice");
        var first = await HostedFlows.BeginAsync(t);
        var second = await HostedFlows.BeginAsync(t);
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/email-otp?request={first.RequestId}"));
        var started = t.Discard(await t.SubmitAsync(page.Form("/login/email-otp/start").With("email", alice.Email)));
        await t.SkipAuditAsync();
        var code = HostedFlows.EmailCode(t, alice.Email);

        t.Observe(
            await t.SubmitAsync(started.Form("/login/email-otp/verify").With("requestId", second.RequestId).With("code", code)),
            "present the code with another authorization request");
        t.Observe(
            await t.SubmitAsync(started.Form("/login/email-otp/verify").With("code", code)),
            "the code still completes its own request");

        await t.ObserveAuditAsync("the mismatch is not counted as a wrong code");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/email-otp/start")]
    public async Task A_second_code_within_the_cooldown_is_refused()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await HostedFlows.CreateVerifiedEmailUserAsync(t, "alice");
        var begun = await HostedFlows.BeginAsync(t);
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/email-otp?request={begun.RequestId}"));
        var start = page.Form("/login/email-otp/start").With("email", alice.Email);
        t.Discard(await t.SubmitAsync(start));
        await t.SkipAuditAsync();

        t.Observe(await t.SubmitAsync(start), "ask for another code straight away");

        await t.ObserveAuditAsync("the cooldown writes no audit event");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/email-otp/start")]
    public async Task The_sixth_code_for_one_address_within_an_hour_is_rate_limited()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        for (var sent = 1; sent <= 5; sent++)
        {
            // Each code belongs to a new authorization request, so the resend cooldown never applies.
            var begun = await HostedFlows.BeginAsync(t);
            var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/email-otp?request={begun.RequestId}"));
            HostedFlows.EnsureStatus(t.Discard(await t.SubmitAsync(page.Form("/login/email-otp/start").With("email", alice.Email))), 200);
        }

        await t.SkipAuditAsync();
        var sixth = await HostedFlows.BeginAsync(t);
        var sixthPage = t.Discard(await t.GetAsync($"/sqlos/auth/login/email-otp?request={sixth.RequestId}"));
        t.Observe(
            await t.SubmitAsync(sixthPage.Form("/login/email-otp/start").With("email", alice.Email)),
            "a sixth code for the same address in the hour");

        await t.ObserveAuditAsync("the per-address limit rejects it");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/email-otp/start")]
    public async Task The_sixty_first_code_from_one_ip_address_within_an_hour_is_rate_limited()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var page = t.Discard(await t.GetAsync("/sqlos/auth/login/email-otp"));
        var start = page.Form("/login/email-otp/start");
        for (var sent = 1; sent <= 60; sent++)
        {
            // Sixty different addresses from one client IP address; none of them has an account.
            HostedFlows.EnsureStatus(
                t.Discard(await t.SubmitAsync(start.With("email", $"visitor-{sent}@unknown.example.test"))),
                200);
        }

        await t.SkipAuditAsync();
        t.Observe(
            await t.SubmitAsync(start.With("email", "visitor-61@unknown.example.test")),
            "a sixty-first code from the same IP address in the hour");
        t.Observe(
            await t.SubmitAsync(start.With("email", "visitor-62@unknown.example.test"), options => options.FromAddress("198.51.100.7")),
            "another IP address is not limited");

        await t.ObserveAuditAsync("the per-IP limit rejects the first");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/email-otp/start")]
    public async Task Missing_and_malformed_addresses_are_rejected()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var begun = await HostedFlows.BeginAsync(t);
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/email-otp?request={begun.RequestId}"));
        var start = page.Form("/login/email-otp/start");

        t.Observe(await t.SubmitAsync(start.With("email", "   ")), "a blank address");
        t.Observe(
            await t.SubmitAsync(start.With("email", "not-an-address")),
            "a malformed address: its message is not on the public allow-list, so the page shows the opaque one");

        await t.ObserveAuditAsync("only the malformed address is audited, as an opaque error");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login/email-otp")]
    [Covers("POST /sqlos/auth/login/email-otp/start")]
    [Covers("POST /sqlos/auth/login/email-otp/verify")]
    public async Task Email_codes_are_unavailable_when_the_application_does_not_enable_them()
    {
        // The default single-application setup enables only passwords.
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var begun = await HostedFlows.BeginAsync(t);

        var page = t.Observe(
            await t.GetAsync($"/sqlos/auth/login/email-otp?request={begun.RequestId}"),
            "the email-code page still renders");
        t.Observe(
            await t.SubmitAsync(page.Form("/login/email-otp/start").With("email", alice.Email)),
            "sending a code is refused");
        t.Observe(
            await t.PostFormAsync("/sqlos/auth/login/email-otp/verify", new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = page.Form("/login/email-otp/start")["__RequestVerificationToken"],
                ["requestId"] = begun.RequestId,
                ["challengeToken"] = "unused",
                ["code"] = "123456"
            }),
            "verifying is refused too");

        await t.ObserveAuditAsync("nothing is audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/email-otp/verify")]
    public async Task Concurrent_wrong_codes_lose_their_attempt_counts_CurrentBehavior_KnownDefect_424()
    {
        // #424: verify loads the challenge, compares, and saves AttemptCount + 1 with no lock or
        // conditional update. Ten wrong codes that all load the challenge before any saves (the
        // barrier forces that interleaving) each write AttemptCount = 1, so the five-attempt limit
        // never trips and the right code still signs in afterwards.
        var barrier = new HostedRaceBarrier("/sqlos/auth/login/email-otp/verify", "UPDATE", "SqlOSEmailOtpChallenges", participants: 10);
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted, barrier.Install);
        var alice = await HostedFlows.CreateVerifiedEmailUserAsync(t, "alice");
        var begun = await HostedFlows.BeginAsync(t);
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/email-otp?request={begun.RequestId}"));
        var started = t.Discard(await t.SubmitAsync(page.Form("/login/email-otp/start").With("email", alice.Email)));
        await t.SkipAuditAsync();
        var code = HostedFlows.EmailCode(t, alice.Email);
        var verify = started.Form("/login/email-otp/verify");
        var wrong = verify.With("code", HostedFlows.WrongCode(t, code));

        barrier.Arm();
        var (shown, others) = await HostedRaceBarrier.PostTogetherAsync(t, Enumerable.Repeat(wrong, 10).ToList());
        t.Observe(shown, "ten wrong codes at once from ten tabs: each is answered 'invalid or expired' (one shown)");
        t.Note($"The other nine tabs were answered {string.Join(", ", others)}.");
        t.Note($"{barrier.Held} verify requests were held at their write until all had loaded the challenge.");

        await t.ObserveAuditAsync("ten failures, every one counted as the first: no max_attempts");
        t.Observe(await t.SubmitAsync(verify.With("code", code)), "the right code still signs in after ten wrong ones");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/email-otp/start")]
    public async Task Concurrent_code_requests_exceed_the_per_address_limit_CurrentBehavior_KnownDefect_424()
    {
        // #424: start counts the address's recent challenges, then inserts its own, with no lock
        // between. Seven requests that all count before any inserts are all admitted, so seven
        // codes go out although the limit is five per hour.
        var barrier = new HostedRaceBarrier("/sqlos/auth/login/email-otp/start", "INSERT INTO", "SqlOSEmailOtpChallenges", participants: 7);
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted, barrier.Install);
        var alice = await HostedFlows.CreateVerifiedEmailUserAsync(t, "alice");
        var forms = new List<HtmlForm>();
        for (var request = 1; request <= 7; request++)
        {
            // One authorization request each, so the resend cooldown never applies.
            var begun = await HostedFlows.BeginAsync(t);
            var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/email-otp?request={begun.RequestId}"));
            forms.Add(page.Form("/login/email-otp/start").With("email", alice.Email));
        }

        await t.SkipAuditAsync();
        var emailsBefore = t.Emails.Count;
        barrier.Arm();
        var (shown, others) = await HostedRaceBarrier.PostTogetherAsync(t, forms);
        t.Observe(shown, "seven code requests at once for one address, one per tab (one shown)");
        t.Note($"The other six tabs were answered {string.Join(", ", others)}.");
        t.Note($"{barrier.Held} start requests were held at their insert until all had counted the recent challenges.");
        t.Note($"{t.Emails.Count - emailsBefore} sign-in codes were emailed to the address; the limit is 5 per hour.");
        // The audit readback is left out on purpose: the fake sender numbers deliveries in the
        // order the concurrent requests reach it, so email.send.queued rows would not render
        // deterministically. The answers and the count above are the evidence.
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/email-otp/start")]
    [Covers("POST /sqlos/auth/login/email-otp/verify")]
    public async Task A_code_asked_for_with_another_spelling_goes_only_to_the_stored_address()
    {
        // #422: the code for an existing account is delivered to the address stored on it, never to
        // the spelling typed into the form.
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await HostedFlows.CreateVerifiedEmailUserAsync(t, "alice");
        var begun = await HostedFlows.BeginAsync(t);
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/email-otp?request={begun.RequestId}"));

        // The padded spelling comes back URL-encoded in the page's links (%20%20ALICE...), where the
        // plain address placeholder cannot match after "%20", so it gets a name of its own.
        var spelling = $"  {alice.Email.ToUpperInvariant()}  ";
        t.Scrub(spelling, "email", "ALICE-padded");
        var started = t.Observe(
            await t.SubmitAsync(page.Form("/login/email-otp/start").With("email", spelling)),
            "ask for a code with an upper-case, padded spelling of the address");
        t.Observe(
            await t.SubmitAsync(started.Form("/login/email-otp/verify").With("code", HostedFlows.EmailCode(t, alice.Email))),
            "the code from the stored mailbox signs in");

        await t.ObserveAuditAsync("delivery events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/email-otp/start")]
    public async Task A_look_alike_address_never_selects_another_persons_account()
    {
        // #422: on SQL Server's default collation 'busineß' equalled 'business', so the victim's code
        // went to the attacker's look-alike mailbox. A look-alike is now a different, unknown address,
        // and ASCII look-alike characters such as the long s are refused as invalid.
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        await HostedFlows.CreateUserWithEmailAsync(t, "Bob", "bob@business.example", "Lock-Bob-2468!");
        await HostedFlows.CreateUserWithEmailAsync(t, "Bos", "bos@example.test", "Lock-Bos-2468!");
        var begun = await HostedFlows.BeginAsync(t);
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/email-otp?request={begun.RequestId}"));
        var start = page.Form("/login/email-otp/start");

        t.Observe(
            await t.SubmitAsync(start.With("email", "bob@busineß.example")),
            "the attacker asks for a code for bob@busineß.example: no account, no email");
        t.Observe(
            await t.SubmitAsync(start.With("email", "boſ@example.test")),
            "a long s that .NET upper-cases to S is refused outright");

        await t.ObserveAuditAsync("no challenge was bound to Bob, and nothing was sent");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/signup")]
    [Covers("POST /sqlos/auth/signup/submit")]
    [Covers("POST /sqlos/auth/login/email-otp/verify")]
    [Covers("POST /sqlos/auth/login/password")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_squatters_password_stops_working_once_the_owner_signs_in_with_an_email_code()
    {
        // #423: anyone could sign up with a password for an address they do not own and keep using it
        // after the owner started using the account. The owner's first email code now claims the
        // unverified address and revokes everything attached before it.
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var victimEmail = t.Unique.Email("victim");
        var squatterPassword = t.Unique.Password("squatter");
        var squatter = t.NewBrowser("squatter");
        var squatterRequest = t.Urls.Authorize();
        var squatterStart = t.Discard(await squatter.GetAsync(squatterRequest.Url));

        var signupPage = t.Observe(
            await squatter.GetAsync($"/sqlos/auth/signup?request={HostedFlows.RequestId(squatterStart)}"),
            "the squatter opens the sign-up page");
        var signedUp = t.Observe(
            await squatter.SubmitAsync(signupPage.Form("/signup/submit")
                .With("displayName", "Victim")
                .With("email", victimEmail)
                .With("password", squatterPassword)),
            "and signs up with the victim's address and a password of their choosing");
        var squatterTokens = t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", squatterRequest.TokenRequest(signedUp.NextUrlParameter("code"))),
            "the squatter holds tokens for the unverified account");
        await t.SkipAuditAsync();

        var owner = await HostedFlows.BeginAsync(t);
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/email-otp?request={owner.RequestId}"));
        var started = t.Discard(await t.SubmitAsync(page.Form("/login/email-otp/start").With("email", victimEmail)));
        t.Observe(
            await t.SubmitAsync(started.Form("/login/email-otp/verify").With("code", HostedFlows.EmailCode(t, victimEmail))),
            "the owner signs in with the code from their own mailbox");
        await t.ObserveAuditAsync("the code claims the address and revokes the squatter's password and sessions");

        var squatterAgain = await HostedFlows.BeginAsync(t, squatter, new Dictionary<string, string?> { ["view"] = "password" });
        t.Observe(
            await squatter.SubmitAsync(squatterAgain.Page.Form("/login/password").With("email", victimEmail).With("password", squatterPassword)),
            "the squatter's password no longer signs in");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = squatterRequest.ClientId,
                ["refresh_token"] = squatterTokens.JsonString("refresh_token")
            }),
            "and the squatter's refresh token was revoked with the claim");

        await t.ObserveAuditAsync("the squatter's failed attempts");
        await t.ApproveAsync();
    }
}

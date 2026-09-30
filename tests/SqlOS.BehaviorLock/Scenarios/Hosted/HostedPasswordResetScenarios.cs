using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Hosted;

/// <summary>
/// Hosted password recovery: the forgot-password page and form, the emailed link, and the hosted
/// reset page and form it opens (served as HTML from the public account endpoints), with the
/// anti-enumeration answer, the per-address limit, reset failures, and the #423 claim.
/// </summary>
[TestClass]
public sealed class HostedPasswordResetScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/password/forgot")]
    [Covers("POST /sqlos/auth/password/forgot/submit")]
    [Covers("GET /sqlos/auth/password/reset")]
    [Covers("POST /sqlos/auth/password/reset/submit")]
    [Covers("POST /sqlos/auth/login/password")]
    public async Task A_forgotten_password_is_reset_from_the_emailed_link()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var newPassword = t.Unique.Password("alice-new");
        var begun = await HostedFlows.BeginAsync(t, extra: new Dictionary<string, string?> { ["view"] = "password" });

        var forgot = t.Observe(
            await t.GetAsync($"/sqlos/auth/password/forgot?request={begun.RequestId}&email={Uri.EscapeDataString(alice.Email)}"),
            "follow 'Forgot password?'");
        t.Observe(await t.SubmitAsync(forgot.Form("/password/forgot/submit")), "ask for a reset link");
        var token = HostedFlows.LinkToken(t, alice.Email, "reset-token");

        var reset = t.Observe(
            await t.GetAsync($"/sqlos/auth/password/reset?token={Uri.EscapeDataString(token)}"),
            "open the link: the reset form");
        t.Observe(
            await t.SubmitAsync(HostedFlows.ResetForm(reset).With("newPassword", newPassword).With("confirmPassword", newPassword)),
            "choose a new password");
        await t.ObserveAuditAsync("reset events: the link also proves and claims the unverified address");

        var signIn = await HostedFlows.BeginAsync(t, extra: new Dictionary<string, string?> { ["view"] = "password" });
        t.Observe(
            await t.SubmitAsync(signIn.Page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)),
            "the old password no longer works");
        t.Observe(
            await t.SubmitAsync(signIn.Page.Form("/login/password").With("email", alice.Email).With("password", newPassword)),
            "the new password signs in");

        await t.ObserveAuditAsync("sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/password/forgot")]
    [Covers("POST /sqlos/auth/password/forgot/submit")]
    public async Task An_unknown_address_gets_the_same_answer_and_no_email()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);

        var forgot = t.Observe(await t.GetAsync("/sqlos/auth/password/forgot"), "open the forgot-password page directly");
        t.Observe(
            await t.SubmitAsync(forgot.Form("/password/forgot/submit").With("email", t.Unique.Email("nobody"))),
            "ask for a reset link for an address with no account");
        t.Observe(await t.SubmitAsync(forgot.Form("/password/forgot/submit").With("email", " ")), "a blank address");

        await t.ObserveAuditAsync("the request is recorded as not eligible");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/password/forgot/submit")]
    public async Task The_sixth_reset_request_for_one_address_within_an_hour_is_silently_limited()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var forgot = t.Discard(await t.GetAsync("/sqlos/auth/password/forgot"));
        var submit = forgot.Form("/password/forgot/submit").With("email", alice.Email);
        for (var sent = 1; sent <= 5; sent++)
        {
            HostedFlows.EnsureStatus(t.Discard(await t.SubmitAsync(submit)), 200);
        }

        await t.SkipAuditAsync();
        t.Observe(await t.SubmitAsync(submit), "a sixth request: the same answer, and no email");

        await t.ObserveAuditAsync("the limit is only visible in the audit log");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/password/reset")]
    [Covers("POST /sqlos/auth/password/reset/submit")]
    public async Task Mismatched_passwords_and_used_or_unknown_links_are_refused()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var newPassword = t.Unique.Password("alice-new");
        var forgot = t.Discard(await t.GetAsync("/sqlos/auth/password/forgot"));
        t.Discard(await t.SubmitAsync(forgot.Form("/password/forgot/submit").With("email", alice.Email)));
        var token = HostedFlows.LinkToken(t, alice.Email, "reset-token");
        var reset = t.Discard(await t.GetAsync($"/sqlos/auth/password/reset?token={Uri.EscapeDataString(token)}"));
        await t.SkipAuditAsync();
        var form = HostedFlows.ResetForm(reset);

        t.Observe(
            await t.SubmitAsync(form.With("newPassword", newPassword).With("confirmPassword", newPassword + "?")),
            "the two passwords differ");
        HostedFlows.EnsureStatus(
            t.Discard(await t.SubmitAsync(form.With("newPassword", newPassword).With("confirmPassword", newPassword))),
            200);
        t.Observe(
            await t.SubmitAsync(form.With("newPassword", newPassword).With("confirmPassword", newPassword)),
            "the same link a second time");
        var blank = t.Observe(await t.GetAsync("/sqlos/auth/password/reset"), "the reset page without a token");
        t.Observe(
            await t.SubmitAsync(HostedFlows.ResetForm(blank).With("newPassword", newPassword).With("confirmPassword", newPassword)),
            "and its form");

        await t.ObserveAuditAsync("reset events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/password/reset")]
    [Covers("POST /sqlos/auth/password/reset/submit")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Resetting_the_password_claims_an_address_a_squatter_signed_up_with()
    {
        // #423: the reset link proves the mailbox; the claim revokes the squatter's sessions and
        // keeps only the password being set now.
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var victimEmail = t.Unique.Email("victim");
        var squatter = t.NewBrowser("squatter");
        var squatterRequest = t.Urls.Authorize();
        var squatterStart = t.Discard(await squatter.GetAsync(squatterRequest.Url));
        var squatterSignup = t.Discard(await squatter.GetAsync($"/sqlos/auth/signup?request={HostedFlows.RequestId(squatterStart)}"));
        var signedUp = t.Discard(await squatter.SubmitAsync(squatterSignup.Form("/signup/submit")
            .With("displayName", "Victim")
            .With("email", victimEmail)
            .With("password", t.Unique.Password("squatter"))));
        var squatterTokens = t.Discard(await t.Api.PostFormAsync(
            "/sqlos/auth/token",
            squatterRequest.TokenRequest(signedUp.NextUrlParameter("code"))));
        HostedFlows.EnsureStatus(squatterTokens, 200);
        var forgot = t.Discard(await t.GetAsync("/sqlos/auth/password/forgot"));
        t.Discard(await t.SubmitAsync(forgot.Form("/password/forgot/submit").With("email", victimEmail)));
        var token = HostedFlows.LinkToken(t, victimEmail, "reset-token");
        await t.SkipAuditAsync();
        var newPassword = t.Unique.Password("owner");

        var reset = t.Observe(
            await t.GetAsync($"/sqlos/auth/password/reset?token={Uri.EscapeDataString(token)}"),
            "the owner opens the reset link from their mailbox");
        t.Observe(
            await t.SubmitAsync(HostedFlows.ResetForm(reset).With("newPassword", newPassword).With("confirmPassword", newPassword)),
            "and sets their own password");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = squatterRequest.ClientId,
                ["refresh_token"] = squatterTokens.JsonString("refresh_token")
            }),
            "the squatter's refresh token was revoked by the claim");

        await t.ObserveAuditAsync("the reset claims the address");
        await t.ApproveAsync();
    }
}

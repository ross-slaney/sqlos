using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Hosted;

/// <summary>
/// Hosted two-step verification once an operator requires a TOTP authenticator for every user:
/// enrollment during sign-in (<c>/mfa/totp/enroll/verify</c>), the challenge on later sign-ins
/// (<c>/mfa/verify</c>), and their refusals, including the per-challenge failure limit.
/// </summary>
[TestClass]
public sealed class HostedMfaScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/login/password")]
    [Covers("POST /sqlos/auth/mfa/totp/enroll/verify")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("POST /__probe/auth/mfa-status")]
    public async Task A_required_authenticator_is_enrolled_during_sign_in_CurrentBehavior_KnownDefect_415()
    {
        // #415: enrolling the authenticator writes no enrollment audit event; only user.login.mfa.
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await HostedFlows.RequireTotpForAllUsersAsync(t);
        var begun = await HostedFlows.BeginAsync(t, extra: new Dictionary<string, string?> { ["view"] = "password" });

        var enroll = t.Observe(
            await t.SubmitAsync(begun.Page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)),
            "sign in: the authenticator setup page");
        var secret = HostedFlows.TotpSecret(t, enroll);
        var (code, _) = await HostedFlows.TotpCodeAsync(t, secret);
        var enrolled = t.Observe(
            await t.SubmitAsync(enroll.Form("/mfa/totp/enroll/verify").With("code", code)),
            "enter the first authenticator code");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", begun.Request.TokenRequest(enrolled.NextUrlParameter("code"))),
            "redeem the authorization code");

        await t.ObserveAuditAsync("enrollment events: no enrollment event");
        t.Observe(
            await t.Api.PostJsonAsync("/__probe/auth/mfa-status", new { userId = alice.Id }),
            "the documented MFA status API: Alice now has an authenticator (the admin user view shows none)");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/mfa/totp/enroll/verify")]
    public async Task A_wrong_enrollment_code_starts_the_setup_again_with_a_new_key()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await HostedFlows.RequireTotpForAllUsersAsync(t);
        var begun = await HostedFlows.BeginAsync(t, extra: new Dictionary<string, string?> { ["view"] = "password" });
        var enroll = t.Discard(await t.SubmitAsync(begun.Page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)));
        var firstSecret = HostedFlows.TotpSecret(t, enroll);
        var (firstCode, _) = await HostedFlows.TotpCodeAsync(t, firstSecret);
        await t.SkipAuditAsync();

        var retry = t.Observe(
            await t.SubmitAsync(enroll.Form("/mfa/totp/enroll/verify").With("code", HostedFlows.WrongCode(t, firstCode))),
            "a wrong code: the page comes back with an error and a new setup key");
        var secondSecret = HostedFlows.TotpSecret(t, retry);
        var (secondCode, _) = await HostedFlows.TotpCodeAsync(t, secondSecret);
        t.Observe(
            await t.SubmitAsync(retry.Form("/mfa/totp/enroll/verify").With("code", secondCode)),
            "a code for the new key completes the sign-in");

        await t.ObserveAuditAsync("enrollment events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/mfa/totp/enroll/verify")]
    public async Task The_enrollment_form_answers_bare_json_when_the_challenge_itself_is_unknown()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await HostedFlows.RequireTotpForAllUsersAsync(t);
        var begun = await HostedFlows.BeginAsync(t, extra: new Dictionary<string, string?> { ["view"] = "password" });
        var enroll = t.Discard(await t.SubmitAsync(begun.Page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)));
        HostedFlows.TotpSecret(t, enroll);
        await t.SkipAuditAsync();
        var form = enroll.Form("/mfa/totp/enroll/verify");

        var again = t.Observe(
            await t.SubmitAsync(form.With("enrollmentToken", "not-an-enrollment-token").With("code", "123456")),
            "an unknown enrollment token with a live challenge: the setup page again, with a new key");
        HostedFlows.TotpSecret(t, again);
        t.Observe(
            await t.SubmitAsync(form.With("mfaToken", "not-an-mfa-token").With("enrollmentToken", "not-an-enrollment-token").With("code", "123456")),
            "an unknown challenge as well: a 400 whose body is a bare JSON string, not a page");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/password")]
    [Covers("POST /sqlos/auth/mfa/verify")]
    [Covers("POST /sqlos/auth/token")]
    public async Task An_enrolled_user_enters_an_authenticator_code_at_sign_in()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await HostedFlows.RequireTotpForAllUsersAsync(t);
        var (secret, lastStep) = await EnrollAsync(t, alice);
        var begun = await HostedFlows.BeginAsync(t, extra: new Dictionary<string, string?> { ["view"] = "password" });

        var challenge = t.Observe(
            await t.SubmitAsync(begun.Page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)),
            "sign in: the two-step verification page");
        var (code, _) = await HostedFlows.TotpCodeAsync(t, secret, lastStep);
        var verified = t.Observe(
            await t.SubmitAsync(challenge.Form("/mfa/verify").With("code", code)),
            "enter a fresh authenticator code");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", begun.Request.TokenRequest(verified.NextUrlParameter("code"))),
            "redeem the authorization code");

        await t.ObserveAuditAsync("two-step sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/email-otp/verify")]
    [Covers("POST /__probe/auth/mfa-status")]
    public async Task An_email_code_that_claims_the_address_revokes_the_authenticator_enrolled_before_it()
    {
        // Alice enrolled her authenticator while her operator-created address was still
        // unverified. Her first email code proves the address and claims it (#423), which evicts
        // everything attached before the proof, the authenticator included, so the sign-in asks
        // her to enroll again.
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await HostedFlows.RequireTotpForAllUsersAsync(t);
        await EnrollAsync(t, alice);
        var begun = await HostedFlows.BeginAsync(t);
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/login/email-otp?request={begun.RequestId}"));
        var started = t.Discard(await t.SubmitAsync(page.Form("/login/email-otp/start").With("email", alice.Email)));

        var enroll = t.Observe(
            await t.SubmitAsync(started.Form("/login/email-otp/verify").With("code", HostedFlows.EmailCode(t, alice.Email))),
            "enter the email code: the authenticator setup page, not the challenge");
        HostedFlows.TotpSecret(t, enroll);

        await t.ObserveAuditAsync("the claim revokes the password and the authenticator");
        t.Observe(
            await t.Api.PostJsonAsync("/__probe/auth/mfa-status", new { userId = alice.Id }),
            "the documented MFA status API: no authenticator left");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/mfa/verify")]
    public async Task Five_wrong_authenticator_codes_lock_the_challenge()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        await HostedFlows.RequireTotpForAllUsersAsync(t);
        var (secret, lastStep) = await EnrollAsync(t, alice);
        var begun = await HostedFlows.BeginAsync(t, extra: new Dictionary<string, string?> { ["view"] = "password" });
        var challenge = t.Discard(await t.SubmitAsync(begun.Page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)));
        await t.SkipAuditAsync();
        var verify = challenge.Form("/mfa/verify");
        var (code, _) = await HostedFlows.TotpCodeAsync(t, secret, lastStep);
        var wrong = HostedFlows.WrongCode(t, code);

        t.Observe(await t.SubmitAsync(verify.With("code", wrong)), "a wrong code");
        for (var attempt = 2; attempt <= 4; attempt++)
        {
            HostedFlows.EnsureStatus(t.Discard(await t.SubmitAsync(verify.With("code", wrong))), 400);
        }

        t.Observe(await t.SubmitAsync(verify.With("code", wrong)), "the fifth wrong code locks the challenge");
        t.Observe(await t.SubmitAsync(verify.With("code", code)), "the right code is refused afterwards");
        t.Observe(
            await t.SubmitAsync(verify.With("mfaToken", "not-an-mfa-token").With("code", code)),
            "an unknown challenge token");

        await t.ObserveAuditAsync("failed attempts and the lock");
        await t.ApproveAsync();
    }

    /// <summary>Enrolls <paramref name="user"/>'s authenticator through a hosted sign-in in a browser of its own.</summary>
    private static async Task<(string Secret, long LastStep)> EnrollAsync(Transcript t, ScenarioUser user)
    {
        var browser = t.NewBrowser("enrollment");
        var begun = await HostedFlows.BeginAsync(t, browser, new Dictionary<string, string?> { ["view"] = "password" });
        var enroll = t.Discard(await browser.SubmitAsync(begun.Page.Form("/login/password").With("email", user.Email).With("password", user.Password)));
        var secret = HostedFlows.TotpSecret(t, enroll);
        var (code, step) = await HostedFlows.TotpCodeAsync(t, secret);
        HostedFlows.EnsureStatus(t.Discard(await browser.SubmitAsync(enroll.Form("/mfa/totp/enroll/verify").With("code", code))), 200);
        await t.SkipAuditAsync();
        return (secret, step);
    }
}

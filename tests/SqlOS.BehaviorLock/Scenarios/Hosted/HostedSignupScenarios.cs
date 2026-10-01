using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Hosted;

/// <summary>
/// Hosted sign-up: the password form, the email-code form an application gets when it turns
/// password sign-up off, and the phone form's failure branches, with the audit gaps #415 records.
/// </summary>
[TestClass]
public sealed class HostedSignupScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/signup")]
    [Covers("POST /sqlos/auth/signup/submit")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Password_sign_up_completes_the_authorization_request_CurrentBehavior_KnownDefect_415()
    {
        // #415: hosted password sign-up creates the user without any sign-up or user-created audit
        // event; only the public JSON sign-up route records user.signup.
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var begun = await HostedFlows.BeginAsync(t);

        var page = t.Observe(await t.GetAsync($"/sqlos/auth/signup?request={begun.RequestId}"), "follow 'Get started'");
        var signedUp = t.Observe(
            await t.SubmitAsync(page.Form("/signup/submit")
                .With("displayName", "Dana")
                .With("email", t.Unique.Email("dana"))
                .With("password", t.Unique.Password("dana"))),
            "create the account: the interstitial returns to the application");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", begun.Request.TokenRequest(signedUp.NextUrlParameter("code"))),
            "redeem the authorization code");

        await t.ObserveAuditAsync("sign-up events: nothing records the new user");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/signup")]
    [Covers("POST /sqlos/auth/signup/submit")]
    [Covers("GET /sqlos/auth/login")]
    public async Task Password_sign_up_without_an_authorization_request_starts_an_issuer_session()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);

        var page = t.Observe(await t.GetAsync("/sqlos/auth/signup"), "open the sign-up page directly");
        var signedUp = t.Observe(
            await t.SubmitAsync(page.Form("/signup/submit")
                .With("displayName", "Dana")
                .With("email", t.Unique.Email("dana"))
                .With("password", t.Unique.Password("dana"))),
            "create the account: signed in to SqlOS itself");
        t.Observe(await t.GetAsync(signedUp.Location!), "the signed-up status page");

        await t.ObserveAuditAsync("sign-up events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/signup/submit")]
    public async Task Signing_up_with_an_address_that_has_an_account_gets_the_opaque_error()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var begun = await HostedFlows.BeginAsync(t);
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/signup?request={begun.RequestId}"));

        t.Observe(
            await t.SubmitAsync(page.Form("/signup/submit")
                .With("displayName", "Alice Again")
                .With("email", alice.Email)
                .With("password", t.Unique.Password("again"))),
            "sign up with an address that already has an account");

        await t.ObserveAuditAsync("the duplicate is audited as an opaque error that names the address");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/signup/submit")]
    public async Task Missing_or_oversized_sign_up_fields_are_refused()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var begun = await HostedFlows.BeginAsync(t);
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/signup?request={begun.RequestId}"));
        var form = page.Form("/signup/submit")
            .With("displayName", "Dana")
            .With("email", t.Unique.Email("dana"))
            .With("password", t.Unique.Password("dana"));

        t.Observe(await t.SubmitAsync(form.With("displayName", " ")), "no display name");
        t.Observe(await t.SubmitAsync(form.With("email", "")), "no email address");
        t.Observe(await t.SubmitAsync(form.With("password", "")), "no password");
        t.Observe(await t.SubmitAsync(form.With("displayName", new string('D', 201))), "a 201-character display name");
        t.Observe(await t.SubmitAsync(form.With("password", "x")), "a one-character password is accepted: there is no strength policy");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/signup")]
    [Covers("POST /sqlos/auth/signup/submit")]
    [Covers("POST /sqlos/auth/signup/email-otp/start")]
    [Covers("POST /sqlos/auth/signup/email-otp/verify")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Email_code_sign_up_replaces_the_password_form_CurrentBehavior_KnownDefect_415()
    {
        // With password sign-up off, the sign-up page asks for an email code instead. #415: the
        // hosted email-code sign-up records no sign-up event (its headless twin does).
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted, options =>
            options.ConfigureSqlOS = sqlos => sqlos.AuthServer.SeedAuthPage(page => page.EnablePasswordSignup = false));
        var email = t.Unique.Email("erin");
        var begun = await HostedFlows.BeginAsync(t);

        var page = t.Observe(await t.GetAsync($"/sqlos/auth/signup?request={begun.RequestId}"), "the sign-up page offers an email code");
        var started = t.Observe(
            await t.SubmitAsync(page.Form("/signup/email-otp/start").With("displayName", "Erin").With("email", email)),
            "send the sign-up code");
        var verified = t.Observe(
            await t.SubmitAsync(started.Form("/signup/email-otp/verify").With("code", HostedFlows.EmailCode(t, email))),
            "enter the code: the account exists and the interstitial returns to the application");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", begun.Request.TokenRequest(verified.NextUrlParameter("code"))),
            "redeem the authorization code");
        var crafted = new HtmlForm("/sqlos/auth/signup/submit", page.Form("/signup/email-otp/start").Fields)
            .With("displayName", "Frank")
            .With("email", t.Unique.Email("frank"))
            .With("password", t.Unique.Password("frank"));
        t.Observe(await t.SubmitAsync(crafted), "a crafted password sign-up post is refused");

        await t.ObserveAuditAsync("email-code sign-up events: no sign-up event");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/signup")]
    [Covers("POST /sqlos/auth/signup/email-otp/start")]
    [Covers("POST /sqlos/auth/signup/email-otp/verify")]
    public async Task Email_code_sign_up_without_an_authorization_request_starts_an_issuer_session()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted, options =>
            options.ConfigureSqlOS = sqlos => sqlos.AuthServer.SeedAuthPage(page => page.EnablePasswordSignup = false));
        var email = t.Unique.Email("erin");

        var page = t.Observe(await t.GetAsync("/sqlos/auth/signup"), "open the sign-up page directly");
        var started = t.Observe(
            await t.SubmitAsync(page.Form("/signup/email-otp/start")
                .With("displayName", "Erin")
                .With("email", email)
                .With("organizationName", "Erin's Studio")),
            "send the sign-up code, naming a new organization");
        t.Observe(
            await t.SubmitAsync(started.Form("/signup/email-otp/verify").With("code", HostedFlows.EmailCode(t, email))),
            "enter the code: signed in to SqlOS itself");

        await t.ObserveAuditAsync("sign-up events");
        var userId = await HostedFlows.FindUserIdAsync(t, email);
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{userId}/memberships", "the new user owns the new organization");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/signup/email-otp/start")]
    [Covers("POST /sqlos/auth/signup/email-otp/verify")]
    public async Task Email_code_sign_up_for_an_address_with_an_account_is_refused_at_the_code()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted, options =>
            options.ConfigureSqlOS = sqlos => sqlos.AuthServer.SeedAuthPage(page => page.EnablePasswordSignup = false));
        var alice = await t.Setup.CreateUserAsync("alice");
        var begun = await HostedFlows.BeginAsync(t);
        var page = t.Discard(await t.GetAsync($"/sqlos/auth/signup?request={begun.RequestId}"));

        var started = t.Observe(
            await t.SubmitAsync(page.Form("/signup/email-otp/start").With("displayName", "Alice Again").With("email", alice.Email)),
            "start a sign-up for an address that has an account: the same answer, and the code is sent");
        t.Observe(
            await t.SubmitAsync(started.Form("/signup/email-otp/verify").With("code", HostedFlows.EmailCode(t, alice.Email))),
            "the code does not create a second account");

        await t.ObserveAuditAsync("sign-up events (the verify transaction rolls back)");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}", "the existing account is unchanged");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/signup/email-otp/verify")]
    public async Task An_unknown_sign_up_token_is_refused_before_the_code_is_checked()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted, options =>
            options.ConfigureSqlOS = sqlos => sqlos.AuthServer.SeedAuthPage(page => page.EnablePasswordSignup = false));
        var email = t.Unique.Email("erin");
        var page = t.Discard(await t.GetAsync("/sqlos/auth/signup"));
        var started = t.Discard(await t.SubmitAsync(page.Form("/signup/email-otp/start").With("displayName", "Erin").With("email", email)));
        var code = HostedFlows.EmailCode(t, email);
        await t.SkipAuditAsync();

        t.Observe(
            await t.SubmitAsync(started.Form("/signup/email-otp/verify")
                .With("signupToken", "not-the-sign-up-token-for-this-code")
                .With("code", code)),
            "the right code with a sign-up token SqlOS never issued");

        await t.ObserveAuditAsync("nothing is recorded");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/signup/email-otp/verify")]
    public async Task Wrong_sign_up_codes_are_never_counted_because_the_failed_verify_rolls_back()
    {
        // Looks like a defect (not filed as of 7.2.1): the hosted sign-up verify form runs in a
        // transaction and rolls it back on any failure, which also undoes the challenge's attempt
        // count and the email_otp.verify_failed audit event. Sign-up codes therefore have no attempt
        // limit: six wrong codes, and the seventh (right) code still creates the account.
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted, options =>
            options.ConfigureSqlOS = sqlos => sqlos.AuthServer.SeedAuthPage(page => page.EnablePasswordSignup = false));
        var email = t.Unique.Email("erin");
        var page = t.Discard(await t.GetAsync("/sqlos/auth/signup"));
        var started = t.Discard(await t.SubmitAsync(page.Form("/signup/email-otp/start").With("displayName", "Erin").With("email", email)));
        var code = HostedFlows.EmailCode(t, email);
        await t.SkipAuditAsync();
        var verify = started.Form("/signup/email-otp/verify");
        var wrong = HostedFlows.WrongCode(t, code);

        t.Observe(await t.SubmitAsync(verify.With("code", wrong)), "a wrong code");
        for (var attempt = 2; attempt <= 5; attempt++)
        {
            HostedFlows.EnsureStatus(t.Discard(await t.SubmitAsync(verify.With("code", wrong))), 400);
        }

        t.Observe(await t.SubmitAsync(verify.With("code", wrong)), "a sixth wrong code: still only 'invalid or expired'");
        t.Observe(await t.SubmitAsync(verify.With("code", code)), "the right code still creates the account");

        await t.ObserveAuditAsync("no failed attempt survived; only the success is recorded");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/signup/phone-otp")]
    [Covers("POST /sqlos/auth/signup/phone-otp/start")]
    [Covers("POST /sqlos/auth/signup/phone-otp/verify")]
    public async Task Phone_sign_up_refuses_a_taken_number_and_a_wrong_code_does_not_invalidate_the_challenge()
    {
        // Unlike phone sign-in, where one failed provider check invalidates the challenge, the
        // hosted sign-up verify form rolls its transaction back on failure, so the invalidation and
        // its phone_otp.verify_failed audit event are undone and the right code still works (the
        // provider's own check limit is the only one left).
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        await HostedFlows.SignUpWithPhoneAsync(t, "Heidi");
        const string otherNumber = "+12025550199";

        var page = t.Observe(
            await t.GetAsync($"/sqlos/auth/signup/phone-otp?displayName=Ivan&phoneNumber={Uri.EscapeDataString(HostedFlows.PhoneNumber)}"),
            "open phone sign-up with the name and number prefilled");
        t.Observe(await t.SubmitAsync(page.Form("/signup/phone-otp/start")), "a number that already has an account");
        var started = t.Observe(
            await t.SubmitAsync(page.Form("/signup/phone-otp/start").With("phoneNumber", otherNumber)),
            "a new number");
        var code = HostedFlows.SmsCode(t, otherNumber);
        var verify = started.Form("/signup/phone-otp/verify");
        t.Observe(await t.SubmitAsync(verify.With("code", HostedFlows.WrongCode(t, code))), "a wrong code");
        t.Observe(await t.SubmitAsync(verify.With("code", code)), "the right code still creates the account");

        await t.ObserveAuditAsync("phone sign-up events: the failed check left no trace");
        await t.ApproveAsync();
    }
}

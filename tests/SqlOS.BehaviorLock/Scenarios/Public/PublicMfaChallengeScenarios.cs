using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Host.Support;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Public;

/// <summary>
/// The multi-factor step of direct login. When the MFA policy applies, a direct login answers with
/// an MFA challenge token instead of tokens: <c>POST /sqlos/auth/mfa/challenge/verify</c> completes
/// it with an authenticator or recovery code, and a user without an authenticator enrolls one first
/// through <c>POST /sqlos/auth/mfa/challenge/totp/enroll/start</c> and <c>/enroll/verify</c>, bound
/// to that challenge.
/// </summary>
[TestClass]
public sealed class PublicMfaChallengeScenarios
{
    private const string Client = BehaviorLockConstants.AppClientId;

    [Scenario]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/mfa/challenge/verify")]
    [Covers("POST /sqlos/auth/mfa/challenge/totp/enroll/start")]
    [Covers("POST /sqlos/auth/mfa/challenge/totp/enroll/verify")]
    public async Task Forced_enrollment_issues_tokens_but_audits_no_enrollment_CurrentBehavior_KnownDefect_415()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        await t.Setup.AddMembershipAsync(acme, alice);
        await t.RequireOrganizationMfaAsync(acme);

        var login = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = Client }),
            "Acme requires MFA and Alice has no authenticator: the login answers with an enrollment challenge");
        var mfaToken = login.JsonString("mfaToken");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/verify", new { mfaToken, code = "000000" }),
            "an enrollment challenge cannot be answered with a code");

        var started = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/totp/enroll/start", new { mfaToken, displayName = "Alice's phone" }),
            "start enrolling an authenticator app, bound to the challenge");
        var secret = started.JsonString("secret");
        var enrollmentToken = started.JsonString("enrollmentToken");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/totp/enroll/verify", new { enrollmentToken, code = PublicSetup.WrongTotpCode(secret), mfaToken }),
            "a wrong authenticator code: the raw message comes back as a JSON string");
        var step = await Totp.StableCurrentStepAsync() - 1;
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/totp/enroll/verify", new { enrollmentToken, code = Totp.Code(secret, step), mfaToken }),
            "the right code confirms the authenticator, returns recovery codes, and issues tokens");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/totp/enroll/verify", new { enrollmentToken, code = Totp.Code(secret, step + 1), mfaToken }),
            "the enrollment and its challenge cannot be used again");

        await t.ObserveAuditAsync("the sign-in is audited; the new authenticator is not (#415)");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/mfa/challenge/totp/enroll/start")]
    [Covers("POST /sqlos/auth/mfa/challenge/totp/enroll/verify")]
    public async Task An_enrollment_only_completes_with_the_challenge_that_started_it()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        await t.Setup.AddMembershipAsync(acme, alice);
        await t.RequireOrganizationMfaAsync(acme);

        var first = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = Client }),
            "sign in: an enrollment challenge");
        var started = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/totp/enroll/start", new { mfaToken = first.JsonString("mfaToken") }),
            "start enrolling under the first challenge");
        var second = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = Client }),
            "sign in again: a second enrollment challenge");
        var secret = started.JsonString("secret");
        // The current step's code stays acceptable for this step and the next (one step of skew).
        var step = await Totp.StableCurrentStepAsync();
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/totp/enroll/verify", new
            {
                enrollmentToken = started.JsonString("enrollmentToken"),
                code = Totp.Code(secret, step),
                mfaToken = second.JsonString("mfaToken")
            }),
            "the first enrollment with the second challenge is refused, even with the right code");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/totp/enroll/verify", new
            {
                enrollmentToken = started.JsonString("enrollmentToken"),
                code = Totp.Code(secret, step)
            }),
            "without its challenge, a challenge-bound enrollment is refused too");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/totp/enroll/verify", new
            {
                enrollmentToken = started.JsonString("enrollmentToken"),
                code = Totp.Code(secret, step),
                mfaToken = first.JsonString("mfaToken")
            }),
            "with the challenge that started it, the enrollment completes and tokens are issued");

        await t.ObserveAuditAsync("enrollment events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/mfa/challenge/verify")]
    public async Task A_later_sign_in_passes_the_challenge_with_an_authenticator_or_a_recovery_code()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        await t.Setup.AddMembershipAsync(acme, alice);
        await t.RequireOrganizationMfaAsync(acme);
        var authenticator = await t.EnrollAuthenticatorAsync(alice, acme);

        var login = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = Client }),
            "Alice has an authenticator: the login answers with a challenge");
        var mfaToken = login.JsonString("mfaToken");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/verify", new { mfaToken, code = PublicSetup.WrongTotpCode(authenticator.Secret) }),
            "a wrong code is a public error and is audited");
        var step = await Totp.NextUnusedStepAsync(authenticator.EnrolledStep);
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/verify", new { mfaToken, code = Totp.Code(authenticator.Secret, step) }),
            "the authenticator code completes the sign-in");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/verify", new { mfaToken, code = Totp.Code(authenticator.Secret, step) }),
            "the completed challenge cannot be used again");

        var second = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = Client }),
            "sign in again");
        var recoveryCode = authenticator.RecoveryCodes[0];
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/verify", new { mfaToken = second.JsonString("mfaToken"), code = recoveryCode }),
            "a recovery code completes the sign-in instead");

        var third = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = Client }),
            "sign in a third time");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/verify", new { mfaToken = third.JsonString("mfaToken"), code = recoveryCode }),
            "a used recovery code is refused");

        await t.ObserveAuditAsync("challenge events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/mfa/challenge/verify")]
    public async Task Wrong_codes_lock_the_challenge_and_then_the_accounts_mfa_budget()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        await t.Setup.AddMembershipAsync(acme, alice);
        await t.RequireOrganizationMfaAsync(acme);
        var authenticator = await t.EnrollAuthenticatorAsync(alice, acme);
        var wrong = PublicSetup.WrongTotpCode(authenticator.Secret);
        // One code for both refused attempts: a locked challenge is refused before any code is
        // checked, and one value keeps the transcript the same whether or not a step boundary passes.
        var right = Totp.Code(authenticator.Secret, await Totp.NextUnusedStepAsync(authenticator.EnrolledStep));

        for (var challenge = 1; challenge <= 2; challenge++)
        {
            var login = t.Observe(
                await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = Client }),
                $"challenge {challenge.ToString(CultureInfo.InvariantCulture)}");
            var mfaToken = login.JsonString("mfaToken");
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                t.Observe(
                    await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/verify", new { mfaToken, code = wrong }),
                    $"challenge {challenge.ToString(CultureInfo.InvariantCulture)}: wrong code {attempt.ToString(CultureInfo.InvariantCulture)}");
            }

            t.Observe(
                await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/verify", new { mfaToken, code = right }),
                $"challenge {challenge.ToString(CultureInfo.InvariantCulture)} is locked: the right code is refused");
        }

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = Client }),
            "ten failures exhausted Alice's MFA budget: no new challenge is issued");

        await t.ObserveAuditAsync("challenge failures, locks, and the refused challenge");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("POST /sqlos/auth/mfa/challenge/verify")]
    [Covers("POST /sqlos/auth/mfa/challenge/totp/enroll/start")]
    [Covers("POST /sqlos/auth/mfa/challenge/totp/enroll/verify")]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/login/password")]
    public async Task Challenge_routes_refuse_tokens_that_do_not_prove_the_right_challenge()
    {
        await using var t = await PublicHost.StartAsync(HostProfiles.Hosted);
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        await t.Setup.AddMembershipAsync(acme, alice);
        await t.RequireOrganizationMfaAsync(acme);
        await t.EnrollAuthenticatorAsync(alice, acme);

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/totp/enroll/start", new { mfaToken = "not-an-mfa-challenge-token" }),
            "enrollment with an unknown challenge");
        var login = t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = Client }),
            "Alice already has an authenticator: her challenge does not require enrollment");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/totp/enroll/start", new { mfaToken = login.JsonString("mfaToken") }),
            "so it cannot start an enrollment (a second authenticator)");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/totp/enroll/verify", new { enrollmentToken = "not-an-enrollment-token", code = "000000", mfaToken = login.JsonString("mfaToken") }),
            "enrollment verification with an unknown enrollment token");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/verify", new { mfaToken = "not-an-mfa-challenge-token", code = "000000" }),
            "a code for an unknown challenge");

        var request = t.Urls.Authorize(extra: new Dictionary<string, string?> { ["view"] = "password" });
        var page = t.Observe(await t.GetAsync(request.Url), "the hosted sign-in page");
        var hosted = t.Observe(
            await t.SubmitAsync(page.Form("/login/password").With("email", alice.Email).With("password", alice.Password)),
            "a hosted sign-in stops at its own MFA challenge, bound to the authorization request");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/mfa/challenge/verify", new { mfaToken = hosted.Form("/mfa/verify")["mfaToken"], code = "000000" }),
            "that authorization challenge cannot finish as a direct login");

        await t.ObserveAuditAsync("rejected challenge uses");
        await t.ApproveAsync();
    }
}

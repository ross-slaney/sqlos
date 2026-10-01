using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Headless.HeadlessJourney;

namespace SqlOS.BehaviorLock.Scenarios.Headless;

/// <summary>Account creation through the headless API: password signup and email-code signup.</summary>
[TestClass]
public sealed class HeadlessSignupScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/headless/signup")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}")]
    public async Task Password_sign_up_creates_the_account_and_signs_in_CurrentBehavior_KnownDefect_415()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var email = t.Unique.Email("bruno");
        var password = t.Unique.Password("bruno");
        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request);

        var signup = t.Observe(
            await t.PostJsonAsync($"{Api}/signup", new
            {
                requestId,
                displayName = "Bruno",
                email,
                password,
                customFields = new { }
            }),
            "sign up with a password: the account is created and signed in");
        var token = t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(signup))),
            "redeem the authorization code");

        t.Note("Known defect #415: headless password signup writes no signup audit event (the public /signup route records user.signup).");
        await t.ObserveAuditAsync("signup events");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{TokenSubject(token.JsonString("access_token"))}", "the new user, with an unverified email");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/signup/email-otp/start")]
    [Covers("POST /sqlos/auth/headless/signup/email-otp/verify")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/memberships")]
    public async Task Email_code_sign_up_creates_a_verified_account_and_its_organization()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var email = t.Unique.Email("carmen");
        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request);

        var started = t.Observe(
            await t.PostJsonAsync($"{Api}/signup/email-otp/start", new
            {
                requestId,
                displayName = "Carmen",
                email,
                organizationName = "Carmen Studio",
                customFields = new { plan = "team" }
            }),
            "start email-code signup: SqlOS emails a sign-up code");
        var verified = t.Observe(
            await t.PostJsonAsync($"{Api}/signup/email-otp/verify", new
            {
                requestId,
                signupToken = started.JsonString("viewModel.signupToken"),
                challengeToken = started.JsonString("viewModel.challengeToken"),
                code = EmailCode(t, email)
            }),
            "verify the code: the account and its organization are created and signed in");
        var token = t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(verified))),
            "redeem the authorization code");

        await t.ObserveAuditAsync("email-code signup events");
        var userId = TokenSubject(token.JsonString("access_token"));
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{userId}", "the new user, with a verified email");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{userId}/memberships", "the user owns the new organization");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/signup")]
    public async Task Password_signup_rejects_missing_fields_and_an_address_that_already_has_an_account()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var alice = await t.Setup.CreateUserAsync("alice");
        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize());
        var bruno = t.Unique.Email("bruno");

        t.Observe(
            await t.PostJsonAsync($"{Api}/signup", new { requestId, displayName = " ", email = bruno, password = t.Unique.Password("bruno") }),
            "no display name: the signup view with the error");
        t.Observe(
            await t.PostJsonAsync($"{Api}/signup", new { requestId, displayName = "Bruno", email = bruno, password = "" }),
            "no password");
        t.Observe(
            await t.PostJsonAsync($"{Api}/signup", new { requestId, displayName = "Alice Again", email = alice.Email, password = t.Unique.Password("alice-again") }),
            "an address that already has an account: a generic error, the reason only in the audit");

        await t.ObserveAuditAsync("signup rejection events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/signup/email-otp/start")]
    [Covers("POST /sqlos/auth/headless/signup/email-otp/verify")]
    public async Task Email_code_signup_for_an_existing_address_mails_the_account_and_refuses_the_code()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var alice = await t.Setup.CreateUserAsync("alice");
        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize());

        var started = t.Observe(
            await t.PostJsonAsync($"{Api}/signup/email-otp/start", new { requestId, displayName = "Alice Again", email = alice.Email }),
            "email-code signup for an address that already has an account: a code still goes to that account");
        t.Observe(
            await t.PostJsonAsync($"{Api}/signup/email-otp/verify", new
            {
                requestId,
                signupToken = started.JsonString("viewModel.signupToken"),
                challengeToken = started.JsonString("viewModel.challengeToken"),
                code = EmailCode(t, alice.Email)
            }),
            "the right code is refused because the challenge belongs to an existing account");

        await t.ObserveAuditAsync("existing-address signup events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/signup")]
    [Covers("GET /sqlos/admin/auth/api/users")]
    public async Task The_signup_hook_sees_the_new_account_and_can_reject_it_with_field_errors()
    {
        await using var t = await Transcript.StartAsync(
            HostProfiles.Headless,
            options => options.ConfigureSqlOS = sqlos => sqlos.AuthServer.Headless.OnHeadlessSignupAsync = (context, _) =>
            {
                var plan = context.CustomFields["plan"]?.GetValue<string>();
                context.HttpContext.Response.Headers["X-BehaviorLock-Signup-Hook"] =
                    $"user={context.User.Id}; organization={context.Organization?.Name ?? "(none)"}; plan={plan ?? "(none)"}";
                if (plan == "unsupported")
                {
                    throw new SqlOS.AuthServer.Contracts.SqlOSHeadlessValidationException(
                        "The plan is not available.",
                        new Dictionary<string, string> { ["customFields.plan"] = "Choose a supported plan." },
                        ["That plan is not available."]);
                }

                return Task.CompletedTask;
            });
        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize());
        var bruno = t.Unique.Email("bruno");
        var carmen = t.Unique.Email("carmen");

        t.Observe(
            await t.PostJsonAsync($"{Api}/signup", new
            {
                requestId,
                displayName = "Carmen",
                email = carmen,
                password = t.Unique.Password("carmen"),
                organizationName = "Carmen Studio",
                customFields = new { plan = "unsupported" }
            }),
            "the host's hook rejects the plan: the signup view with the hook's field and global errors");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users?search={Uri.EscapeDataString(carmen)}", "the rejected signup left no account");
        t.Observe(
            await t.PostJsonAsync($"{Api}/signup", new
            {
                requestId,
                displayName = "Bruno",
                email = bruno,
                password = t.Unique.Password("bruno"),
                organizationName = "Bruno Works",
                customFields = new { plan = "team" }
            }),
            "the hook accepts: it ran with the new user and organization, and the signup redirects with a code");

        await t.ObserveAuditAsync("hooked signup events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/signup/email-otp/start")]
    [Covers("POST /sqlos/auth/headless/signup/email-otp/verify")]
    [Covers("GET /sqlos/admin/auth/api/users")]
    public async Task A_signup_hook_rejection_rolls_back_an_email_code_signup_and_keeps_the_code_usable()
    {
        await using var t = await Transcript.StartAsync(
            HostProfiles.Headless,
            options => options.ConfigureSqlOS = sqlos => sqlos.AuthServer.Headless.OnHeadlessSignupAsync = (context, _) =>
                context.CustomFields["plan"]?.GetValue<string>() == "unsupported"
                    ? throw new SqlOS.AuthServer.Contracts.SqlOSHeadlessValidationException(
                        "The plan is not available.",
                        new Dictionary<string, string> { ["customFields.plan"] = "Choose a supported plan." })
                    : Task.CompletedTask);
        var email = t.Unique.Email("carmen");
        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize());

        var started = t.Observe(
            await t.PostJsonAsync($"{Api}/signup/email-otp/start", new
            {
                requestId,
                displayName = "Carmen",
                email,
                organizationName = "Carmen Studio",
                customFields = new { plan = "unsupported" }
            }),
            "start email-code signup with a plan the host rejects");
        var verify = new
        {
            requestId,
            signupToken = started.JsonString("viewModel.signupToken"),
            challengeToken = started.JsonString("viewModel.challengeToken"),
            code = EmailCode(t, email)
        };
        t.Observe(
            await t.PostJsonAsync($"{Api}/signup/email-otp/verify", verify),
            "verify: the hook rejects, the account and organization are rolled back");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users?search={Uri.EscapeDataString(email)}", "no account was kept");
        t.Observe(
            await t.PostJsonAsync($"{Api}/signup/email-otp/verify", verify),
            "the same code again: the rollback left the challenge unconsumed, so the hook runs and rejects again");

        await t.ObserveAuditAsync("rolled-back signup events");
        await t.ApproveAsync();
    }
}

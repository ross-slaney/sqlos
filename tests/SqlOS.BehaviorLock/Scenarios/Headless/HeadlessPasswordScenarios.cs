using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Headless.HeadlessJourney;

namespace SqlOS.BehaviorLock.Scenarios.Headless;

/// <summary>
/// Password sign-in, identification, and password reset through the headless API, as an app's
/// own sign-in UI drives them (<c>app.Headless("/auth/authorize")</c>).
/// </summary>
[TestClass]
public sealed class HeadlessPasswordScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/authorize")]
    [Covers("POST /sqlos/auth/headless/identify")]
    [Covers("POST /sqlos/auth/headless/password/login")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Identify_then_password_sign_in_redeems_the_code_for_tokens()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var alice = await t.Setup.CreateUserAsync("alice");
        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request, "authorize redirects to the app's own sign-in UI");

        t.Observe(
            await t.PostJsonAsync($"{Api}/identify", new { requestId, email = alice.Email }),
            "identify: no SSO domain matches, so the UI gets the preferred local credential view");
        var login = t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = alice.Email, password = alice.Password }),
            "sign in with the password: a redirect to the app callback with a code and the issuer session cookie");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(login))),
            "redeem the authorization code");

        await t.ObserveAuditAsync("password sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/password/forgot")]
    [Covers("POST /sqlos/auth/headless/password/reset")]
    [Covers("POST /sqlos/auth/headless/password/login")]
    [Covers("POST /sqlos/auth/token")]
    public async Task Forgot_password_emails_a_reset_link_and_the_reset_ends_every_session()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var alice = await t.Setup.CreateUserAsync("alice");
        var earlier = await SignInAsync(t, alice, t.NewBrowser("phone"));
        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request);

        t.Observe(
            await t.PostJsonAsync($"{Api}/password/forgot", new { email = alice.Email, requestId }),
            "request a reset email from the sign-in UI");
        var resetToken = EmailLinkToken(t, alice.Email);
        var newPassword = t.Unique.Password("alice-new");
        t.Observe(
            await t.PostJsonAsync($"{Api}/password/reset", new { token = resetToken, newPassword }),
            "reset the password with the emailed token");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = earlier.RefreshToken,
                ["client_id"] = AppClientId
            }),
            "a refresh token issued before the reset no longer works");
        t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = alice.Email, password = alice.Password }),
            "the old password is rejected");
        var login = t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = alice.Email, password = newPassword }),
            "the new password signs in");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(login))),
            "redeem the authorization code");
        t.Observe(
            await t.PostJsonAsync($"{Api}/password/reset", new { token = resetToken, newPassword = t.Unique.Password("alice-again") }),
            "the reset token cannot be replayed");

        await t.ObserveAuditAsync("password reset events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/password/login")]
    public async Task A_wrong_password_and_an_unknown_email_get_the_same_password_view_error()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var alice = await t.Setup.CreateUserAsync("alice");
        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize());
        var nobody = t.Unique.Email("nobody");

        t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = alice.Email, password = "not-alices-password" }),
            "a wrong password: the password view with a generic error");
        t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = nobody, password = "whatever-password" }),
            "an address with no account: the same view and error");

        await t.ObserveAuditAsync("failed sign-in events: the audit tells the two apart");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/password/login")]
    public async Task Five_wrong_passwords_lock_the_account_even_against_the_right_one()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var alice = await t.Setup.CreateUserAsync("alice");
        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize());
        await t.SkipAuditAsync();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            t.Observe(
                await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = alice.Email, password = $"wrong-password-{attempt}" }),
                $"wrong password {attempt} of 5");
        }

        t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = alice.Email, password = alice.Password }),
            "the right password while the account is locked: the same generic error");

        // SqlOS writes one lock event per locked bucket in database order (see AuditOrder.Content).
        await t.ObserveAuditAsync("failure and lockout events", AuditOrder.Content);
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/password/forgot")]
    public async Task Reset_requests_answer_the_same_for_unknown_addresses_and_throttle_per_address()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var alice = await t.Setup.CreateUserAsync("alice");
        var nobody = t.Unique.Email("nobody");

        t.Observe(
            await t.PostJsonAsync($"{Api}/password/forgot", new { email = nobody }),
            "an address with no account and no request: the same answer, and no email");
        for (var request = 1; request <= 6; request++)
        {
            t.Observe(
                await t.PostJsonAsync($"{Api}/password/forgot", new { email = alice.Email }),
                $"reset request {request} for a real account");
        }

        await t.ObserveAuditAsync("reset request events, including the throttled ones");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/password/login")]
    [Covers("POST /sqlos/auth/token")]
    public async Task A_host_that_requires_verified_emails_refuses_password_sign_in_until_an_invitation_proves_the_address()
    {
        await using var t = await Transcript.StartAsync(
            HostProfiles.Headless,
            options => options.ConfigureSqlOS = sqlos => sqlos.AuthServer.RequireVerifiedEmailForPasswordLogin = true);
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var invitationToken = await HeadlessInvitationScenarios.InviteAsync(t, acme, alice.Email);
        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request);

        t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = alice.Email, password = alice.Password }),
            "alice's address is unverified: the password view with the verification error");
        var login = t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = alice.Email, password = alice.Password, invitationToken }),
            "the same sign-in carrying an invitation to that address is allowed");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(login))),
            "redeem the authorization code");

        await t.ObserveAuditAsync("verified-email events");
        await t.ApproveAsync();
    }
}

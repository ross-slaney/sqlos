using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Headless.HeadlessJourney;

namespace SqlOS.BehaviorLock.Scenarios.Headless;

/// <summary>
/// The headless API honors the AuthPage credential settings. A minimal single application keeps
/// the default <c>EnabledCredentialTypes = ["password"]</c> and serves the hosted AuthPage, so the
/// request comes from the hosted page and every other credential route refuses.
/// </summary>
[TestClass]
public sealed class HeadlessCredentialSettingsScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/headless/identify")]
    [Covers("POST /sqlos/auth/headless/email-otp/start")]
    [Covers("POST /sqlos/auth/headless/magic-link/start")]
    [Covers("POST /sqlos/auth/headless/magic-link/complete")]
    [Covers("POST /sqlos/auth/headless/phone-otp/start")]
    [Covers("POST /sqlos/auth/headless/signup/email-otp/start")]
    [Covers("POST /sqlos/auth/headless/invitations/signup")]
    public async Task Credential_routes_refuse_the_methods_the_auth_page_does_not_enable()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var invitationToken = await HeadlessInvitationScenarios.InviteAsync(t, acme, t.Unique.Email("erin"));
        var page = t.Discard(await t.GetAsync(t.Urls.Authorize().Url));
        var requestId = page.Form("/login/identify")["requestId"];

        t.Observe(
            await t.PostJsonAsync($"{Api}/identify", new { requestId, email = alice.Email }),
            "identify: password is the only local method, so it is the preferred view");
        t.Observe(
            await t.PostJsonAsync($"{Api}/email-otp/start", new { requestId, email = alice.Email }),
            "email code: unavailable");
        t.Observe(
            await t.PostJsonAsync($"{Api}/magic-link/start", new { requestId, email = alice.Email }),
            "magic link: unavailable");
        t.Observe(
            await t.PostJsonAsync($"{Api}/magic-link/complete", new { token = "any-magic-link-token", requestId }),
            "magic-link completion: unavailable");
        t.Observe(
            await t.PostJsonAsync($"{Api}/phone-otp/start", new { requestId, phoneNumber = PhoneNumber }),
            "phone code: unavailable");
        t.Observe(
            await t.PostJsonAsync($"{Api}/signup/email-otp/start", new { requestId, displayName = "Bruno", email = t.Unique.Email("bruno") }),
            "email-code signup: unavailable");
        t.Observe(
            await t.PostJsonAsync($"{Api}/invitations/signup", new { requestId, displayName = "Erin", email = t.Unique.Email("erin"), invitationToken }),
            "passwordless invitation signup needs email codes");

        await t.ObserveAuditAsync("refusal events");
        await t.ApproveAsync();
    }
}

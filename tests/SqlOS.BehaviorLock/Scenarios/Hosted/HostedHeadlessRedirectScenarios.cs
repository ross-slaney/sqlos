using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Hosted;

/// <summary>
/// When the application renders its own sign-in UI (<c>app.Headless(...)</c>), every hosted
/// AuthPage GET route answers with a redirect into that UI, carrying the view, the request, the
/// address, and a <c>ui_context</c> (invitation token, device code, phone number).
/// </summary>
[TestClass]
public sealed class HostedHeadlessRedirectScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/login")]
    [Covers("GET /sqlos/auth/password/forgot")]
    [Covers("GET /sqlos/auth/login/email-otp")]
    [Covers("GET /sqlos/auth/login/magic-link")]
    [Covers("GET /sqlos/auth/login/phone-otp")]
    [Covers("GET /sqlos/auth/signup")]
    [Covers("GET /sqlos/auth/signup/phone-otp")]
    public async Task Hosted_pages_redirect_into_the_applications_own_sign_in_ui()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var email = Uri.EscapeDataString(t.Unique.Email("alice"));

        t.Observe(await t.GetAsync("/sqlos/auth/login"), "the sign-in page");
        t.Observe(
            await t.GetAsync($"/sqlos/auth/login?request=req_0123456789abcdef0123456789abcdef&email={email}&ui_context=%7B%22theme%22%3A%22dark%22%7D"),
            "with a request, an address, and the application's own ui_context");
        t.Observe(await t.GetAsync("/sqlos/auth/login?status=signed-in"), "a status page is a plain sign-in view there");
        t.Observe(await t.GetAsync($"/sqlos/auth/password/forgot?email={email}"), "forgot password");
        t.Observe(await t.GetAsync("/sqlos/auth/login/email-otp?user_code=WDJB-MJHT"), "the email-code page with a device code");
        t.Observe(await t.GetAsync("/sqlos/auth/login/magic-link"), "the magic-link page");
        t.Observe(await t.GetAsync("/sqlos/auth/login/phone-otp?deviceUserCode=WDJB-MJHT"), "the phone-code page");
        t.Observe(await t.GetAsync("/sqlos/auth/signup"), "the sign-up page");
        t.Observe(
            await t.GetAsync("/sqlos/auth/signup/phone-otp?phoneNumber=%2B12025550173&ui_context=not-json"),
            "phone sign-up keeps the number and drops a ui_context that is not JSON");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/invitations/accept")]
    [Covers("GET /sqlos/auth/login")]
    [Covers("GET /sqlos/auth/signup")]
    public async Task An_invitation_link_redirects_into_the_applications_invite_view()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var token = Uri.EscapeDataString(await HostedFlows.InviteAsync(t, acme, t.Unique.Email("carol")));

        t.Observe(await t.GetAsync($"/sqlos/auth/invitations/accept?token={token}"), "the invitation link");
        t.Observe(await t.GetAsync($"/sqlos/auth/login?invitationToken={token}"), "the sign-in page with the invitation");
        t.Observe(await t.GetAsync($"/sqlos/auth/signup?invitationToken={token}"), "the sign-up page with the invitation");
        t.Observe(await t.GetAsync("/sqlos/auth/invitations/accept?token=not-an-invitation"), "an unknown invitation still gets the hosted error page");

        await t.ApproveAsync();
    }
}

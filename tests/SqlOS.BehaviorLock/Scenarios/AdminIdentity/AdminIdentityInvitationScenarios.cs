using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminIdentity;

/// <summary>
/// Organization email invitations from the operator's side: creating them (with and without the
/// email), listing them, superseding, resending, and revoking, the rate limit per address, and
/// whether the emailed link still opens the invitation.
/// </summary>
[TestClass]
public sealed class AdminIdentityInvitationScenarios
{
    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/invitations")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/invitations")]
    [Covers("GET /sqlos/auth/invitations/accept")]
    public async Task Operator_invites_by_email_and_the_link_opens_the_invitation()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var invitations = $"{AdminIdentity.Api}/organizations/{acme.Id}/invitations";

        var dana = t.Observe(
            await t.Operator.PostJsonAsync(invitations, new { email = t.Unique.Email("dana"), role = "admin" }),
            "invite dana as an admin; the email carries the link");
        t.Observe(
            await t.Operator.PostJsonAsync(invitations, new
            {
                email = t.Unique.Email("erin"),
                sendEmail = false,
                expiresAt = DateTime.UtcNow.AddDays(2),
                customFields = new { team = "blue", seats = 3 }
            }),
            "invite erin without sending an email, with a custom expiry and custom fields; the role defaults to member");
        await t.ObserveAuditAsync("invitation events");
        t.Observe(await t.Operator.GetAsync(invitations), "invitations, newest first, without their links");
        var page = t.Observe(await t.Operator.GetAsync(invitations + "?pageSize=1"), "one to a page");
        t.Observe(
            await t.Operator.GetAsync(invitations + "?pageSize=1&cursor=" + Uri.EscapeDataString(page.JsonString("nextCursor"))),
            "the next page");
        t.Observe(
            await t.NewBrowser("dana").GetAsync(new Uri(dana.JsonString("inviteUrl")).PathAndQuery),
            "dana's link opens the invitation on the hosted page");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/invitations")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/invitations")]
    public async Task Invitation_requests_are_validated_before_anything_is_sent()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback, options => options.AnswerUnhandledExceptionsAsServerErrors = true);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var dormant = await t.Setup.CreateOrganizationAsync("dormant");
        t.Discard(await t.Operator.PutJsonAsync($"{AdminIdentity.Api}/organizations/{dormant.Id}", new { name = dormant.Name, slug = dormant.Slug, isActive = false }));
        await t.SkipAuditAsync();
        var invitations = $"{AdminIdentity.Api}/organizations/{acme.Id}/invitations";
        var email = t.Unique.Email("dana");

        t.Observe(
            await t.Operator.PostJsonAsync(invitations, new { email, expiresAt = DateTime.UtcNow.AddMinutes(-1) }),
            "an expiry in the past is refused");
        t.Observe(await t.Operator.PostJsonAsync(invitations, new { email = "not-an-email" }), "an invalid address is refused");
        t.Observe(await t.Operator.PostJsonAsync(invitations, new { email = "\u017Fam@example.test" }), "an ASCII look-alike address is refused");
        t.Observe(await t.Operator.PostJsonAsync(invitations, new { email, role = new string('r', 51) }), "a role over fifty characters is refused");
        t.Observe(
            await t.Operator.PostJsonAsync($"{AdminIdentity.Api}/organizations/org_missing/invitations", new { email }),
            "an unknown organization is refused");
        t.Observe(
            await t.Operator.PostJsonAsync($"{AdminIdentity.Api}/organizations/{dormant.Id}/invitations", new { email }),
            "an inactive organization is refused the same way");
        t.Observe(
            await t.Operator.PostJsonAsync(invitations, new { email, clientId = "no-such-client", redirectUri = BehaviorLockConstants.AppRedirectUri }),
            "an unknown client");
        t.Observe(
            await t.Operator.PostJsonAsync(invitations, new { email, clientId = BehaviorLockConstants.AppClientId, redirectUri = "https://attacker.example/callback" }),
            "a redirect URI the client does not register");
        t.Observe(
            await t.Operator.PostJsonAsync(invitations, new { email, clientId = BehaviorLockConstants.AppClientId, redirectUri = BehaviorLockConstants.AppRedirectUri, scope = "openid profile" }),
            "an invitation bound to the application's client and redirect URI");
        await t.ObserveAuditAsync("only the invitation that was created is audited");
        t.Observe(await t.Operator.GetAsync($"{AdminIdentity.Api}/organizations/org_missing/invitations"), "listing an unknown organization's invitations is refused");
        t.Observe(await t.Operator.GetAsync($"{AdminIdentity.Api}/organizations/{dormant.Id}/invitations"), "and an inactive one's");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/invitations")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/invitations")]
    [Covers("POST /sqlos/admin/auth/api/invitations/{invitationId}/resend")]
    [Covers("POST /sqlos/admin/auth/api/invitations/{invitationId}/revoke")]
    [Covers("GET /sqlos/auth/invitations/accept")]
    public async Task A_new_invitation_supersedes_the_pending_one_and_resend_and_revoke_retire_links()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var invitations = $"{AdminIdentity.Api}/organizations/{acme.Id}/invitations";
        var email = t.Unique.Email("dana");
        var browser = t.NewBrowser("dana");
        string Accept(HttpExchange created) => new Uri(created.JsonString("inviteUrl")).PathAndQuery;

        var first = t.Observe(await t.Operator.PostJsonAsync(invitations, new { email, role = "member" }), "invite dana");
        var second = t.Observe(await t.Operator.PostJsonAsync(invitations, new { email, role = "admin" }), "invite dana again as an admin");
        t.Observe(await t.Operator.GetAsync(invitations), "the first invitation is revoked as superseded");
        t.Observe(await browser.GetAsync(Accept(first)), "the superseded link no longer opens");

        var resent = t.Observe(
            await t.Operator.PostJsonAsync($"{AdminIdentity.Api}/invitations/{second.JsonString("id")}/resend", new { }),
            "resend the pending invitation with a fresh link");
        t.Observe(await browser.GetAsync(Accept(second)), "the replaced link no longer opens");
        t.Observe(await browser.GetAsync(Accept(resent)), "the resent link opens");
        t.Observe(
            await t.Operator.PostJsonAsync($"{AdminIdentity.Api}/invitations/{first.JsonString("id")}/resend", new { }),
            "a superseded invitation cannot be resent");
        t.Observe(await t.Operator.PostJsonAsync($"{AdminIdentity.Api}/invitations/inv_missing/resend", new { }), "nor can an unknown one");
        await t.ObserveAuditAsync("creation, supersession, and resend events");

        t.Observe(
            await t.Operator.PostJsonAsync($"{AdminIdentity.Api}/invitations/{second.JsonString("id")}/revoke", new { reason = "  Sent to the wrong team  " }),
            "revoke the pending invitation with a reason");
        t.Observe(
            await t.Operator.PostJsonAsync($"{AdminIdentity.Api}/invitations/{second.JsonString("id")}/revoke", new { reason = "again" }),
            "revoking it again changes nothing");
        t.Observe(
            await t.Operator.PostJsonAsync($"{AdminIdentity.Api}/invitations/{first.JsonString("id")}/revoke", new { }),
            "revoking the superseded one keeps its original reason");
        t.Observe(await t.Operator.PostJsonAsync($"{AdminIdentity.Api}/invitations/inv_missing/revoke", new { }), "an unknown invitation is not found");
        t.Observe(await browser.GetAsync(Accept(resent)), "the revoked invitation's link no longer opens");
        t.Observe(
            await t.Operator.PostJsonAsync($"{AdminIdentity.Api}/invitations/{second.JsonString("id")}/resend", new { }),
            "and it cannot be resent");
        await t.ObserveAuditAsync("the first revocation is audited once");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/invitations")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/memberships")]
    [Covers("POST /sqlos/admin/auth/api/invitations/{invitationId}/revoke")]
    [Covers("POST /sqlos/admin/auth/api/invitations/{invitationId}/resend")]
    public async Task An_accepted_invitation_can_be_neither_revoked_nor_resent()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var dana = await t.Setup.CreateUserAsync("dana");
        var invitation = await t.Setup.OperatorPostAsync(
            $"{AdminIdentity.Api}/organizations/{acme.Id}/invitations",
            new { email = dana.Email, role = "admin" });
        var browser = t.NewBrowser("dana");
        var page = t.Discard(await browser.GetAsync(new Uri(invitation.JsonString("inviteUrl")).PathAndQuery));
        var accepted = t.Discard(await browser.SubmitAsync(page.Form("/login/password").With("password", dana.Password)));
        if (accepted.StatusCode >= 400)
        {
            throw new InvalidOperationException($"Accepting the invitation failed: {accepted.Describe()}");
        }

        await t.SkipAuditAsync();
        t.Note("Setup: dana, an existing user, accepted the invitation by signing in on the hosted invitation page.");
        t.Observe(await t.Operator.GetAsync($"{AdminIdentity.Api}/organizations/{acme.Id}/invitations"), "the invitation is accepted by dana");
        t.Observe(await t.Operator.GetAsync($"{AdminIdentity.Api}/organizations/{acme.Id}/memberships"), "who is an admin of acme");
        t.Observe(
            await t.Operator.PostJsonAsync($"{AdminIdentity.Api}/invitations/{invitation.JsonString("id")}/revoke", new { }),
            "an accepted invitation cannot be revoked");
        t.Observe(
            await t.Operator.PostJsonAsync($"{AdminIdentity.Api}/invitations/{invitation.JsonString("id")}/resend", new { }),
            "or resent");
        await t.ObserveAuditAsync("the refusals are not audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/invitations")]
    public async Task Invitations_to_one_address_are_limited_per_hour()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var invitations = $"{AdminIdentity.Api}/organizations/{acme.Id}/invitations";
        var email = t.Unique.Email("dana");
        for (var sent = 0; sent < 10; sent++)
        {
            await t.Setup.OperatorPostAsync(invitations, new { email, sendEmail = false });
        }

        t.Note("Ten invitations to the same address were created in setup, the default hourly limit per address.");
        t.Observe(await t.Operator.PostJsonAsync(invitations, new { email, sendEmail = false }), "the eleventh is refused");
        t.Observe(
            await t.Operator.PostJsonAsync(invitations, new { email = email.ToUpperInvariant(), sendEmail = false }),
            "the limit is per mailbox, not per spelling");
        await t.ObserveAuditAsync("the rejections are audited with the normalized address");
        t.Observe(
            await t.Operator.PostJsonAsync(invitations, new { email = t.Unique.Email("erin"), sendEmail = false }),
            "another address is still accepted");
        await t.ApproveAsync();
    }
}

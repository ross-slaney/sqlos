using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Hosted;

/// <summary>
/// Hosted invitation acceptance: the accept link, accepting by signing in (email code, password),
/// creating an account from an invitation inside an authorization request, the server errors that
/// creating one from the bare invitation link runs into, and the refusals for unusable links.
/// </summary>
[TestClass]
public sealed class HostedInvitationScenarios
{
    [Scenario]
    [Covers("GET /sqlos/auth/invitations/accept")]
    [Covers("POST /sqlos/auth/login/email-otp/start")]
    [Covers("POST /sqlos/auth/login/email-otp/verify")]
    [Covers("GET /sqlos/auth/login")]
    public async Task An_existing_user_accepts_an_invitation_with_an_email_code()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var bob = await t.Setup.CreateUserAsync("bob");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var token = await HostedFlows.InviteAsync(t, acme, bob.Email, "admin");

        var invite = t.Observe(
            await t.GetAsync($"/sqlos/auth/invitations/accept?token={Uri.EscapeDataString(token)}"),
            "open the invitation link");
        var started = t.Observe(
            await t.SubmitAsync(invite.Form("/login/email-otp/start")),
            "'Email me a sign-in code' for the invited address");
        var verified = t.Observe(
            await t.SubmitAsync(started.Form("/login/email-otp/verify").With("code", HostedFlows.EmailCode(t, bob.Email))),
            "enter the code: signed in and the invitation is accepted");
        t.Observe(await t.GetAsync(verified.Location!), "the invitation-accepted status page");

        await t.ObserveAuditAsync("acceptance events");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{bob.Id}/memberships", "Bob is an admin of Acme");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/invitations/accept")]
    [Covers("POST /sqlos/auth/login/password")]
    public async Task In_a_password_only_application_an_existing_user_accepts_with_the_password()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var bob = await t.Setup.CreateUserAsync("bob");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var token = await HostedFlows.InviteAsync(t, acme, bob.Email);

        var invite = t.Observe(
            await t.GetAsync($"/sqlos/auth/invitations/accept?token={Uri.EscapeDataString(token)}"),
            "open the invitation link: password sign-in and password sign-up forms");
        t.Observe(
            await t.SubmitAsync(invite.Form("/login/password").With("password", bob.Password)),
            "sign in with the password: the invitation token also proves the unverified address");

        await t.ObserveAuditAsync("acceptance events: the claim keeps the password that was just used");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{bob.Id}/memberships", "Bob's new membership");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/invitations/accept")]
    [Covers("POST /sqlos/auth/signup/invitation/submit")]
    public async Task Creating_an_account_from_the_invitation_link_fails_with_a_server_error()
    {
        // Looks like a defect (not filed as of 7.2.1). Without an authorization request, the form
        // stages the invitation's membership without saving it and then signs the new user in to
        // the issuer session; the session's lifecycle check reads the database, does not see the
        // membership, and refuses. The error page then tries to show the invitation, which the
        // change tracker still holds as accepted, and throws: Kestrel answers a bare 500. The
        // transaction rolled back, so no account exists and the invitation is still pending.
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted, options => options.AnswerUnhandledExceptionsAsServerErrors = true);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var email = t.Unique.Email("carol");
        var token = await HostedFlows.InviteAsync(t, acme, email);

        var invite = t.Observe(
            await t.GetAsync($"/sqlos/auth/invitations/accept?token={Uri.EscapeDataString(token)}"),
            "open the invitation link");
        t.Observe(
            await t.SubmitAsync(invite.Form("/signup/invitation/submit").With("displayName", "Carol")),
            "'Create account': the server fails");

        await t.ObserveAuditAsync("nothing survives the rollback");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/invitations", "the invitation is still pending");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/signup/submit")]
    public async Task In_a_password_only_application_a_password_sign_up_from_the_invitation_link_fails_with_a_server_error()
    {
        // The same defect through the password sign-up form the invitation page shows when email
        // codes are off.
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback, options => options.AnswerUnhandledExceptionsAsServerErrors = true);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var email = t.Unique.Email("carol");
        var token = await HostedFlows.InviteAsync(t, acme, email);

        var invite = t.Discard(await t.GetAsync($"/sqlos/auth/invitations/accept?token={Uri.EscapeDataString(token)}"));
        t.Observe(
            await t.SubmitAsync(invite.Form("/signup/submit").With("displayName", "Carol").With("password", t.Unique.Password("carol"))),
            "'Create account with password' from the invitation: the server fails");

        await t.ObserveAuditAsync("nothing survives the rollback");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/login")]
    [Covers("POST /sqlos/auth/signup/invitation/submit")]
    [Covers("POST /sqlos/auth/token")]
    public async Task An_invitee_creates_an_account_inside_an_authorization_request_CurrentBehavior_KnownDefect_415()
    {
        // Inside an authorization request the invitation is bound to the request and accepted
        // (and saved) as part of the sign-in, so account creation works. #415: nothing records
        // the new user; only invitation.accepted is audited.
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var email = t.Unique.Email("carol");
        var token = await HostedFlows.InviteAsync(t, acme, email);
        var begun = await HostedFlows.BeginAsync(t);

        var invite = t.Observe(
            await t.GetAsync($"/sqlos/auth/login?request={begun.RequestId}&invitationToken={Uri.EscapeDataString(token)}"),
            "the sign-in page for a pending authorization request, with the invitation");
        var created = t.Observe(
            await t.SubmitAsync(invite.Form("/signup/invitation/submit").With("displayName", "Carol")),
            "'Create account': the interstitial returns to the application");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", begun.Request.TokenRequest(created.NextUrlParameter("code"))),
            "redeem the authorization code: the tokens carry Acme");

        await t.ObserveAuditAsync("acceptance events: nothing records the new user");
        var carolId = await HostedFlows.FindUserIdAsync(t, email);
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{carolId}", "Carol's account and verified address");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/login/email-otp/start")]
    [Covers("POST /sqlos/auth/login/email-otp/verify")]
    [Covers("POST /sqlos/auth/token")]
    public async Task An_existing_user_accepts_an_invitation_inside_an_authorization_request()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var bob = await HostedFlows.CreateVerifiedEmailUserAsync(t, "bob");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var token = await HostedFlows.InviteAsync(t, acme, bob.Email);
        var begun = await HostedFlows.BeginAsync(t);

        var invite = t.Discard(await t.GetAsync($"/sqlos/auth/login?request={begun.RequestId}&invitationToken={Uri.EscapeDataString(token)}"));
        var started = t.Observe(
            await t.SubmitAsync(invite.Form("/login/email-otp/start")),
            "'Email me a sign-in code' binds the invitation to the authorization request");
        var verified = t.Observe(
            await t.SubmitAsync(started.Form("/login/email-otp/verify").With("code", HostedFlows.EmailCode(t, bob.Email))),
            "enter the code: the invitation is accepted as part of the sign-in");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", begun.Request.TokenRequest(verified.NextUrlParameter("code"))),
            "redeem the authorization code: the tokens carry Acme");

        await t.ObserveAuditAsync("acceptance and sign-in events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/auth/invitations/accept")]
    public async Task Missing_unknown_used_and_revoked_invitation_links_show_the_sign_in_page_with_an_error()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var bob = await t.Setup.CreateUserAsync("bob");
        var usedToken = await HostedFlows.InviteAsync(t, acme, bob.Email);
        var usedInvite = t.Discard(await t.GetAsync($"/sqlos/auth/invitations/accept?token={Uri.EscapeDataString(usedToken)}"));
        var usedStart = t.Discard(await t.SubmitAsync(usedInvite.Form("/login/email-otp/start")));
        HostedFlows.EnsureStatus(
            t.Discard(await t.SubmitAsync(usedStart.Form("/login/email-otp/verify").With("code", HostedFlows.EmailCode(t, bob.Email)))),
            302);
        var revoked = await t.Setup.OperatorPostAsync(
            $"/sqlos/admin/auth/api/organizations/{acme.Id}/invitations",
            new { email = t.Unique.Email("revoked"), role = "member", sendEmail = false });
        var revokedToken = Uri.UnescapeDataString(revoked.JsonString("inviteUrl").Split("token=")[1]);
        t.Scrub(revokedToken, "invitation-token");
        await t.Setup.OperatorPostAsync($"/sqlos/admin/auth/api/invitations/{revoked.JsonString("id")}/revoke", new { reason = "sent by mistake" });
        await t.SkipAuditAsync();

        t.Observe(await t.GetAsync("/sqlos/auth/invitations/accept"), "no token");
        t.Observe(await t.GetAsync("/sqlos/auth/invitations/accept?token=not-an-invitation-token"), "a token SqlOS never issued");
        t.Observe(await t.GetAsync($"/sqlos/auth/invitations/accept?token={Uri.EscapeDataString(usedToken)}"), "an invitation already accepted");
        t.Observe(await t.GetAsync($"/sqlos/auth/invitations/accept?token={Uri.EscapeDataString(revokedToken)}"), "a revoked invitation");

        await t.ObserveAuditAsync("refusals write nothing");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/signup/invitation/submit")]
    public async Task Invitation_sign_up_refuses_another_address_and_a_missing_invitation()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Hosted);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var email = t.Unique.Email("carol");
        var token = await HostedFlows.InviteAsync(t, acme, email);
        var invite = t.Discard(await t.GetAsync($"/sqlos/auth/invitations/accept?token={Uri.EscapeDataString(token)}"));
        var form = invite.Form("/signup/invitation/submit").With("displayName", "Carol");

        t.Observe(
            await t.SubmitAsync(form.With("email", t.Unique.Email("mallory"))),
            "create the account under another address");
        t.Observe(
            await t.SubmitAsync(new HtmlForm("/sqlos/auth/signup/invitation/submit", form.Without("invitationToken").Fields)),
            "no invitation token in the form or the query");

        await t.ObserveAuditAsync("refusals write nothing");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/signup/invitation/submit")]
    [Covers("POST /sqlos/auth/signup/phone-otp/start")]
    public async Task Invitation_sign_up_needs_email_codes_and_phone_sign_up_refuses_invitations()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var email = t.Unique.Email("carol");
        var token = await HostedFlows.InviteAsync(t, acme, email);
        var invite = t.Discard(await t.GetAsync($"/sqlos/auth/invitations/accept?token={Uri.EscapeDataString(token)}"));
        var fields = invite.Form("/signup/submit");

        t.Observe(
            await t.SubmitAsync(new HtmlForm("/sqlos/auth/signup/invitation/submit", fields.Fields).With("displayName", "Carol")),
            "a crafted invitation sign-up where email codes are off");
        t.Observe(
            await t.SubmitAsync(new HtmlForm("/sqlos/auth/signup/phone-otp/start", fields.Fields)
                .With("displayName", "Carol")
                .With("phoneNumber", HostedFlows.PhoneNumber)),
            "a crafted phone sign-up that carries the invitation");

        await t.ApproveAsync();
    }
}

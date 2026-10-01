using Microsoft.AspNetCore.WebUtilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using static SqlOS.BehaviorLock.Scenarios.Headless.HeadlessJourney;

namespace SqlOS.BehaviorLock.Scenarios.Headless;

/// <summary>Organization invitations accepted through the headless API.</summary>
[TestClass]
public sealed class HeadlessInvitationScenarios
{
    [Scenario]
    [Covers("POST /sqlos/auth/headless/invitations/resolve")]
    [Covers("POST /sqlos/auth/headless/invitations/signup")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/invitations")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/memberships")]
    public async Task An_invitee_resolves_the_invitation_and_signs_up_without_a_password()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var email = t.Unique.Email("erin");
        var invitationToken = await InviteAsync(t, acme, email);

        t.Observe(
            await t.PostJsonAsync($"{Api}/invitations/resolve", new { invitationToken }),
            "the UI resolves the invitation link before any authorization request");
        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request);
        var signup = t.Observe(
            await t.PostJsonAsync($"{Api}/invitations/signup", new
            {
                requestId,
                displayName = "Erin",
                email,
                invitationToken,
                customFields = new { team = "design" }
            }),
            "sign up from the invitation: no password, the invitation proves the mailbox");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(signup))),
            "redeem the authorization code: the token is bound to the inviting organization");

        await t.ObserveAuditAsync("invitation signup events");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/invitations", "the invitation is accepted");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/organizations/{acme.Id}/memberships", "the invitee is a member");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/password/login")]
    [Covers("POST /sqlos/auth/token")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/memberships")]
    public async Task An_existing_account_accepts_an_invitation_by_signing_in_with_its_password()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var invitationToken = await InviteAsync(t, acme, alice.Email, "admin");
        var request = t.Urls.Authorize();
        var requestId = await OpenAuthorizeAsync(t, request);

        var login = t.Observe(
            await t.PostJsonAsync($"{Api}/password/login", new { requestId, email = alice.Email, password = alice.Password, invitationToken }),
            "sign in with the invitation: the invitation is accepted and proves the address");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", request.TokenRequest(RedirectCode(login))),
            "redeem the authorization code: bound to the inviting organization");

        await t.ObserveAuditAsync("invitation acceptance events");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{alice.Id}/memberships", "alice is now an admin of the organization");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/auth/headless/invitations/resolve")]
    [Covers("POST /sqlos/auth/headless/invitations/signup")]
    [Covers("POST /sqlos/auth/headless/signup")]
    public async Task Unknown_used_and_mismatched_invitations_are_refused()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.Headless);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var erin = t.Unique.Email("erin");
        var invitationToken = await InviteAsync(t, acme, erin);
        var requestId = await OpenAuthorizeAsync(t, t.Urls.Authorize());

        t.Observe(
            await t.PostJsonAsync($"{Api}/invitations/resolve", new { invitationToken = "not-an-invitation-token" }),
            "resolve an unknown invitation token");
        t.Observe(
            await t.PostJsonAsync($"{Api}/invitations/signup", new { requestId, displayName = "Erin", email = erin }),
            "invitation signup without an invitation");
        t.Observe(
            await t.PostJsonAsync($"{Api}/signup", new
            {
                requestId,
                displayName = "Mallory",
                email = t.Unique.Email("mallory"),
                password = t.Unique.Password("mallory"),
                invitationToken
            }),
            "password signup carrying the invitation with another address");
        t.Observe(
            await t.PostJsonAsync($"{Api}/invitations/signup", new { requestId, displayName = "Erin", email = erin, invitationToken }),
            "the invitee signs up with the invitation");
        t.Observe(
            await t.PostJsonAsync($"{Api}/invitations/resolve", new { invitationToken }),
            "the accepted invitation no longer resolves");

        await t.ObserveAuditAsync("invitation refusal events");
        await t.ApproveAsync();
    }

    /// <summary>Creates an email invitation as the operator (unrecorded) and returns its token.</summary>
    internal static async Task<string> InviteAsync(Transcript t, ScenarioOrganization organization, string email, string role = "member")
    {
        var created = await t.Setup.OperatorPostAsync(
            $"/sqlos/admin/auth/api/organizations/{organization.Id}/invitations",
            new { email, role });
        var token = QueryHelpers.ParseQuery(new Uri(created.JsonString("inviteUrl")).Query)["token"].ToString();
        t.Scrub(token, "invitation-token");
        return token;
    }
}

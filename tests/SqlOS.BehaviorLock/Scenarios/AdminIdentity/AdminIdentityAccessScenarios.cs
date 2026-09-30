using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminIdentity;

/// <summary>
/// Who may call the admin identity API in each operator-access deployment model: the host's
/// authorization callback, Password mode's session cookie and same-origin proof, the open
/// Development environment, and a production host without operator access. Every route the
/// admin identity endpoints map is swept, so a route that loses its operator check shows up here.
/// </summary>
[TestClass]
public sealed class AdminIdentityAccessScenarios
{
    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/users")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/memberships")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/sessions")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/applications")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/grants")]
    [Covers("POST /sqlos/admin/auth/api/users/{userId}/grants/{grantId}/revoke")]
    [Covers("POST /sqlos/admin/auth/api/users/{userId}/password-reset-email")]
    [Covers("POST /sqlos/admin/auth/api/users")]
    [Covers("GET /sqlos/admin/auth/api/organizations")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/applications")]
    [Covers("POST /sqlos/admin/auth/api/organizations")]
    [Covers("PUT /sqlos/admin/auth/api/organizations/{organizationId}")]
    [Covers("GET /sqlos/admin/auth/api/memberships")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/memberships")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/invitations")]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/invitations")]
    [Covers("POST /sqlos/admin/auth/api/invitations/{invitationId}/resend")]
    [Covers("POST /sqlos/admin/auth/api/invitations/{invitationId}/revoke")]
    [Covers("POST /sqlos/admin/auth/api/memberships")]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/memberships")]
    [Covers("GET /sqlos/admin/auth/api/clients")]
    [Covers("GET /sqlos/admin/auth/api/clients/{clientId}")]
    [Covers("GET /sqlos/admin/auth/api/clients/{clientId}/credentials")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/credentials")]
    [Covers("DELETE /sqlos/admin/auth/api/clients/{clientId}/credentials/{credentialId}")]
    [Covers("GET /sqlos/admin/auth/api/applications/{applicationId}/assignments")]
    [Covers("POST /sqlos/admin/auth/api/applications/{applicationId}/access-mode")]
    [Covers("POST /sqlos/admin/auth/api/applications/{applicationId}/assignments")]
    [Covers("DELETE /sqlos/admin/auth/api/applications/{applicationId}/assignments/{assignmentId}")]
    [Covers("GET /sqlos/admin/auth/api/applications/{applicationId}/access/check")]
    [Covers("POST /sqlos/admin/auth/api/clients")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/disable")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/enable")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/emergency-disable")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/emergency-enable")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/revoke")]
    [Covers("GET /sqlos/admin/auth/api/machine-clients")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/rotate")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/validate")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/revoke")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/emergency-disable")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/emergency-enable")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/grants")]
    [Covers("DELETE /sqlos/admin/auth/api/machine-clients/{clientId}/grants/{grantId}")]
    public async Task Every_admin_identity_route_is_hidden_without_the_operator_callback()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);

        foreach (var route in AdminIdentity.Routes)
        {
            t.Observe(
                await AdminIdentity.SendAsync(t.Operator, route, options => options.WithoutCredentials()),
                $"{route.Method} {route.Template} without the operator header is not found");
        }

        t.Observe(
            await t.Operator.GetAsync(AdminIdentity.Api + "/users", options => options
                .WithoutCredentials()
                .Header(BehaviorLockConstants.OperatorHeader, "not-the-operator-secret")),
            "a wrong operator header value is not found either");
        await t.ObserveAuditAsync("rejected operator requests write no audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/users")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/memberships")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/sessions")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/applications")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/grants")]
    [Covers("POST /sqlos/admin/auth/api/users/{userId}/grants/{grantId}/revoke")]
    [Covers("POST /sqlos/admin/auth/api/users/{userId}/password-reset-email")]
    [Covers("POST /sqlos/admin/auth/api/users")]
    [Covers("GET /sqlos/admin/auth/api/organizations")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/applications")]
    [Covers("POST /sqlos/admin/auth/api/organizations")]
    [Covers("PUT /sqlos/admin/auth/api/organizations/{organizationId}")]
    [Covers("GET /sqlos/admin/auth/api/memberships")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/memberships")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/invitations")]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/invitations")]
    [Covers("POST /sqlos/admin/auth/api/invitations/{invitationId}/resend")]
    [Covers("POST /sqlos/admin/auth/api/invitations/{invitationId}/revoke")]
    [Covers("POST /sqlos/admin/auth/api/memberships")]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/memberships")]
    [Covers("GET /sqlos/admin/auth/api/clients")]
    [Covers("GET /sqlos/admin/auth/api/clients/{clientId}")]
    [Covers("GET /sqlos/admin/auth/api/clients/{clientId}/credentials")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/credentials")]
    [Covers("DELETE /sqlos/admin/auth/api/clients/{clientId}/credentials/{credentialId}")]
    [Covers("GET /sqlos/admin/auth/api/applications/{applicationId}/assignments")]
    [Covers("POST /sqlos/admin/auth/api/applications/{applicationId}/access-mode")]
    [Covers("POST /sqlos/admin/auth/api/applications/{applicationId}/assignments")]
    [Covers("DELETE /sqlos/admin/auth/api/applications/{applicationId}/assignments/{assignmentId}")]
    [Covers("GET /sqlos/admin/auth/api/applications/{applicationId}/access/check")]
    [Covers("POST /sqlos/admin/auth/api/clients")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/disable")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/enable")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/emergency-disable")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/emergency-enable")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/revoke")]
    [Covers("GET /sqlos/admin/auth/api/machine-clients")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/rotate")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/validate")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/revoke")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/emergency-disable")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/emergency-enable")]
    [Covers("POST /sqlos/admin/auth/api/machine-clients/{clientId}/grants")]
    [Covers("DELETE /sqlos/admin/auth/api/machine-clients/{clientId}/grants/{grantId}")]
    public async Task Without_operator_access_every_admin_identity_route_is_hidden()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardOff);
        var alice = await t.Setup.CreateUserAsync("alice");

        foreach (var route in AdminIdentity.Routes)
        {
            t.Observe(
                await AdminIdentity.SendAsync(t.Operator, route),
                $"{route.Method} {route.Template} is not found in production without operator access");
        }

        t.Observe(
            await t.Operator.GetAsync($"{AdminIdentity.Api}/users/{alice.Id}"),
            "an existing user is not found either");
        t.Observe(
            await t.Operator.GetAsync($"{AdminIdentity.Api}/users/{alice.Id}", options => options
                .Header(BehaviorLockConstants.OperatorHeader, BehaviorLockConstants.OperatorSecret)),
            "the operator header means nothing without an authorization callback");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/users")]
    [Covers("POST /sqlos/admin/auth/api/users")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}")]
    [Covers("POST /sqlos/admin/auth/api/organizations")]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/memberships")]
    [Covers("DELETE /sqlos/admin/auth/api/applications/{applicationId}/assignments/{assignmentId}")]
    public async Task The_development_environment_opens_the_admin_identity_api_to_anyone()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardDevelopment);
        var anyone = t.NewClient("anyone");
        var email = t.Unique.Email("dana");

        t.Observe(await anyone.GetAsync(AdminIdentity.Api + "/users"), "list users with no credentials");
        var created = t.Observe(
            await anyone.PostJsonAsync(AdminIdentity.Api + "/users", new { displayName = "Dana", email, password = t.Unique.Password("dana") }),
            "create a user with no credentials and no same-origin proof");
        t.Observe(await anyone.GetAsync($"{AdminIdentity.Api}/users/{created.JsonString("id")}"), "read the user back");
        var organization = t.Observe(
            await anyone.PostJsonAsync(AdminIdentity.Api + "/organizations", new { name = "Open Org" }),
            "create an organization");
        t.Observe(
            await anyone.PostJsonAsync(
                $"{AdminIdentity.Api}/organizations/{organization.JsonString("id")}/memberships",
                new { userId = created.JsonString("id"), role = "admin" }),
            "add the user to it");
        t.Observe(
            await anyone.DeleteAsync($"{AdminIdentity.Api}/applications/{BehaviorLockConstants.AppClientId}/assignments/asa_missing"),
            "a DELETE is admitted too and fails only on the missing assignment");
        await t.ObserveAuditAsync("audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/users")]
    [Covers("POST /sqlos/admin/auth/api/users")]
    [Covers("DELETE /sqlos/admin/auth/api/applications/{applicationId}/assignments/{assignmentId}")]
    public async Task Password_mode_admits_the_operator_session_and_requires_the_same_origin_proof_on_mutations()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardPassword);
        var stranger = t.NewClient("stranger");
        var users = AdminIdentity.Api + "/users";
        object NewUser(string name) => new { displayName = char.ToUpperInvariant(name[0]) + name[1..], email = t.Unique.Email(name) };

        t.Observe(await t.Operator.GetAsync(users), "the signed-in operator lists users with the session cookie");
        t.Observe(await stranger.GetAsync(users), "without the session cookie the admin API is not found");
        t.Observe(
            await stranger.PostJsonAsync(users, NewUser("stranger"), options => options
                .Header("X-SqlOS-Request", "1")
                .WithOrigin(BehaviorLockConstants.PublicOrigin)),
            "a proof without the session cookie is still not found");
        t.Observe(
            await t.Operator.PostJsonAsync(users, NewUser("missing"), options => options.WithoutCredentials()),
            "a cookie-authenticated mutation without X-SqlOS-Request is rejected");
        t.Observe(
            await t.Operator.PostJsonAsync(users, NewUser("crosssite"), options => options
                .WithoutCredentials()
                .Header("X-SqlOS-Request", "1")
                .WithOrigin("https://attacker.example")),
            "a cross-site Origin is rejected even with the header");
        t.Observe(
            await t.Operator.PostJsonAsync(users, NewUser("opaque"), options => options
                .WithoutCredentials()
                .Header("X-SqlOS-Request", "1")
                .WithOrigin("null")),
            "an opaque Origin is rejected");
        t.Observe(
            await t.Operator.PostJsonAsync(users, NewUser("twice"), options => options
                .WithoutCredentials()
                .Header("X-SqlOS-Request", "1")
                .Header("X-SqlOS-Request", "1")
                .WithOrigin(BehaviorLockConstants.PublicOrigin)),
            "a repeated X-SqlOS-Request header is rejected as ambiguous");
        t.Observe(
            await t.Operator.PostJsonAsync(users, NewUser("nosource"), options => options
                .WithoutCredentials()
                .Header("X-SqlOS-Request", "1")),
            "the header alone, with no Origin, Referer, or Sec-Fetch-Site, is rejected");
        t.Observe(
            await t.Operator.PostJsonAsync(users, NewUser("referer"), options => options
                .WithoutCredentials()
                .Header("X-SqlOS-Request", "1")
                .Header("Referer", BehaviorLockConstants.PublicOrigin + "/sqlos/admin/auth/users")),
            "a same-origin Referer stands in for a missing Origin");
        t.Observe(
            await t.Operator.PostJsonAsync(users, NewUser("fetchsite"), options => options
                .WithoutCredentials()
                .Header("X-SqlOS-Request", "1")
                .Header("Sec-Fetch-Site", "same-origin")),
            "Sec-Fetch-Site: same-origin stands in when neither Origin nor Referer is sent");
        t.Observe(
            await t.Operator.PostJsonAsync(users, NewUser("proven")),
            "the operator's mutation with the header and its own Origin succeeds");
        t.Observe(
            await t.Operator.DeleteAsync($"{AdminIdentity.Api}/applications/{BehaviorLockConstants.AppClientId}/assignments/asa_missing", options => options.WithoutCredentials()),
            "DELETE needs the proof too");
        await t.ObserveAuditAsync("each rejected mutation is audited as security.csrf_rejected");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/users")]
    [Covers("POST /sqlos/admin/auth/api/organizations")]
    public async Task A_callback_authorized_mutation_needs_no_same_origin_proof()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);

        t.Note("Recorded as it behaves today: #426 proposes requiring the same-origin proof on every unsafe admin request, not only cookie-authenticated ones.");
        t.Observe(
            await t.Operator.PostJsonAsync(
                AdminIdentity.Api + "/users",
                new { displayName = "Cross", email = t.Unique.Email("cross") },
                options => options.WithOrigin("https://attacker.example")),
            "the callback admits a cross-site mutation with no X-SqlOS-Request header");
        t.Observe(
            await t.Operator.PostJsonAsync(
                AdminIdentity.Api + "/organizations",
                new { name = "Cross Site" },
                options => options.WithOrigin("null").Header("Sec-Fetch-Site", "cross-site")),
            "an opaque Origin and Sec-Fetch-Site: cross-site change nothing");
        await t.ObserveAuditAsync("no csrf rejection is audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/users")]
    [Covers("POST /sqlos/admin/auth/api/organizations")]
    [Covers("PUT /sqlos/admin/auth/api/organizations/{organizationId}")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/disable")]
    public async Task Request_bodies_are_bound_before_the_operator_check()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var stranger = t.NewClient("stranger");
        var users = AdminIdentity.Api + "/users";

        t.Observe(
            await stranger.SendAsync(HttpMethod.Post, users, Json("{\"displayName\": ")),
            "malformed JSON from an unauthenticated caller fails binding before the operator check");
        t.Observe(
            await stranger.SendAsync(HttpMethod.Post, users, content: null),
            "a missing body from the same caller reaches the operator check and is not found");
        t.Observe(
            await stranger.SendAsync(HttpMethod.Post, users, new StringContent("displayName=Eve", Encoding.UTF8, "application/x-www-form-urlencoded")),
            "a form body is an unsupported media type");
        t.Observe(
            await stranger.PostJsonAsync(users, new { displayName = "Eve", email = t.Unique.Email("eve") }),
            "a well-formed body from the same caller is not found");
        t.Observe(
            await t.Operator.SendAsync(HttpMethod.Post, users, Json("null")),
            "the operator's JSON null body fails binding");
        t.Observe(
            await t.Operator.SendAsync(HttpMethod.Post, AdminIdentity.Api + "/organizations", Json("{\"name\": 42}")),
            "a number where a string belongs fails binding");
        t.Observe(
            await t.Operator.SendAsync(HttpMethod.Put, AdminIdentity.Api + "/organizations/org_missing", Json("{\"name\": \"Acme\", \"isActive\": \"yes\"}")),
            "a string where a boolean belongs fails binding");
        t.Observe(
            await t.Operator.SendAsync(HttpMethod.Post, $"{AdminIdentity.Api}/clients/{BehaviorLockConstants.AppClientId}/disable", content: null),
            "a lifecycle route that binds a reason body needs one");
        await t.ApproveAsync();
    }

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");
}

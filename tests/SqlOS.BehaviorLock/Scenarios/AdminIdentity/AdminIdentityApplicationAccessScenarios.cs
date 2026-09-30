using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminIdentity;

/// <summary>
/// Application access policy: an application's access mode, its organization, user, role, and
/// deny assignments, the access check operators use to explain a decision, the per-organization and
/// per-user application views, and what the policy does to sign-in.
/// </summary>
[TestClass]
public sealed class AdminIdentityApplicationAccessScenarios
{
    private static readonly object Portal = new
    {
        clientId = "portal",
        name = "Portal",
        audience = "https://portal.example.test/api",
        redirectUris = new[] { "https://portal.example.test/callback" },
        allowedScopes = new[] { "openid", "profile", "email", "offline_access" },
        isFirstParty = true
    };

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/applications/{applicationId}/access-mode")]
    [Covers("POST /sqlos/admin/auth/api/applications/{applicationId}/assignments")]
    [Covers("GET /sqlos/admin/auth/api/applications/{applicationId}/assignments")]
    [Covers("DELETE /sqlos/admin/auth/api/applications/{applicationId}/assignments/{assignmentId}")]
    [Covers("GET /sqlos/admin/auth/api/applications/{applicationId}/access/check")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/applications")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/applications")]
    [Covers("POST /sqlos/auth/password/login")]
    public async Task Operator_restricts_an_application_to_assigned_organizations()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback, AdminIdentity.AnswerUnhandledExceptionsAsServerErrors);
        var alice = await t.Setup.CreateUserAsync("alice");
        var bob = await t.Setup.CreateUserAsync("bob");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        await t.Setup.AddMembershipAsync(acme, alice);
        await t.Setup.AddMembershipAsync(globex, bob);
        await AdminIdentity.CreateClientAsync(t, Portal);
        var application = AdminIdentity.Api + "/applications/portal";

        t.Observe(await t.Operator.PostJsonAsync(application + "/access-mode", new { accessMode = "  Selected_Organizations  " }), "restrict portal to selected organizations");
        var assignment = t.Observe(
            await t.Operator.PostJsonAsync(application + "/assignments", new { principalType = "organization", principalId = acme.Id, reason = "Pilot customer" }),
            "assign acme, naming it as the principal");
        await t.ObserveAuditAsync("the mode change and the assignment are audited");
        t.Observe(await t.Operator.GetAsync(application + "/assignments"), "portal's active assignments");
        t.Observe(await t.Operator.GetAsync($"{application}/access/check?userId={alice.Id}&organizationId={acme.Id}"), "alice in acme is allowed by the organization assignment");
        t.Observe(await t.Operator.GetAsync($"{application}/access/check?userId={bob.Id}&organizationId={globex.Id}"), "bob in globex is not");
        t.Observe(await t.Operator.GetAsync($"{application}/access/check?userId={alice.Id}"), "without an organization nothing matches");
        t.Observe(await t.Operator.GetAsync($"{AdminIdentity.Api}/organizations/{acme.Id}/applications"), "acme's applications include portal");
        t.Observe(await t.Operator.GetAsync($"{AdminIdentity.Api}/organizations/{globex.Id}/applications"), "globex's do not");
        t.Observe(await t.Operator.GetAsync($"{AdminIdentity.Api}/users/{bob.Id}/applications"), "bob's application view");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = alice.Email, password = alice.Password, clientId = "portal", organizationId = acme.Id }),
            "alice signs in to portal in acme");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = bob.Email, password = bob.Password, clientId = "portal", organizationId = globex.Id }),
            "bob is refused in globex (the public login lets the refusal escape as a 500)");
        await t.ObserveAuditAsync("the refusal is audited as a denied access check");

        var assignmentId = assignment.JsonString("id");
        t.Observe(await t.Operator.DeleteAsync($"{application}/assignments/{assignmentId}"), "revoke the acme assignment");
        t.Observe(await t.Operator.DeleteAsync($"{application}/assignments/{assignmentId}"), "revoking it again returns it unchanged");
        t.Observe(await t.Operator.GetAsync(application + "/assignments"), "no active assignments remain");
        t.Observe(await t.Operator.GetAsync(application + "/assignments?includeRevoked=true"), "the revoked assignment is still listed on request");
        t.Observe(await t.Operator.GetAsync($"{application}/access/check?userId={alice.Id}&organizationId={acme.Id}"), "alice in acme is now denied");
        await t.ObserveAuditAsync("the revocation is audited once");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/applications/{applicationId}/access-mode")]
    [Covers("POST /sqlos/admin/auth/api/applications/{applicationId}/assignments")]
    [Covers("GET /sqlos/admin/auth/api/applications/{applicationId}/access/check")]
    [Covers("GET /sqlos/admin/auth/api/applications/{applicationId}/assignments")]
    public async Task User_role_and_deny_assignments_decide_access_to_a_selected_principals_application()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var bob = await t.Setup.CreateUserAsync("bob");
        var carol = await t.Setup.CreateUserAsync("carol");
        var dave = await t.Setup.CreateUserAsync("dave");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        await t.Setup.AddMembershipAsync(acme, alice, "member");
        await t.Setup.AddMembershipAsync(acme, bob, "admin");
        await t.Setup.AddMembershipAsync(acme, carol, "admin");
        await t.Setup.AddMembershipAsync(acme, dave, "member");
        await AdminIdentity.CreateClientAsync(t, Portal);
        var application = AdminIdentity.Api + "/applications/portal";
        string Check(ScenarioUser user) => $"{application}/access/check?userId={user.Id}&organizationId={acme.Id}";

        t.Observe(await t.Operator.PostJsonAsync(application + "/access-mode", new { accessMode = "selected_users_groups_roles" }), "restrict portal to selected principals");
        t.Observe(await t.Operator.PostJsonAsync(application + "/assignments", new { principalType = "user", principalId = alice.Id }), "allow alice");
        t.Observe(
            await t.Operator.PostJsonAsync(application + "/assignments", new { principalType = "role", organizationId = acme.Id, roleKey = "admin", access = "ALLOWED" }),
            "allow acme's admins");
        t.Observe(
            await t.Operator.PostJsonAsync(application + "/assignments", new { principalType = "user", principalId = carol.Id, access = "denied", reason = "Contractor" }),
            "deny carol explicitly");
        t.Observe(await t.Operator.GetAsync(Check(alice)), "alice matches her user assignment");
        t.Observe(await t.Operator.GetAsync(Check(bob)), "bob matches the admin role assignment");
        t.Observe(await t.Operator.GetAsync(Check(carol)), "carol is an admin, but the deny wins");
        t.Observe(await t.Operator.GetAsync(Check(dave)), "dave matches nothing");
        t.Observe(await t.Operator.PostJsonAsync(application + "/access-mode", new { accessMode = "internal_only" }), "internal-only uses the same principal assignments");
        t.Observe(await t.Operator.GetAsync(Check(bob)), "bob is still allowed");
        t.Observe(await t.Operator.PostJsonAsync(application + "/access-mode", new { accessMode = "all_organizations" }), "open portal to every organization");
        t.Observe(await t.Operator.GetAsync(Check(dave)), "dave is allowed by the mode");
        t.Observe(await t.Operator.GetAsync(Check(carol)), "a deny assignment still wins over an open mode");
        var page = t.Observe(await t.Operator.GetAsync(application + "/assignments?pageSize=2"), "assignments, newest first, two to a page");
        t.Observe(
            await t.Operator.GetAsync(application + "/assignments?pageSize=2&cursor=" + Uri.EscapeDataString(page.JsonString("nextCursor"))),
            "the oldest assignment");
        t.Observe(
            await t.Operator.GetAsync(application + "/assignments?includeRevoked=true&cursor=" + Uri.EscapeDataString(page.JsonString("nextCursor"))),
            "the cursor is bound to the includeRevoked filter");
        await t.ObserveAuditAsync("every mode change and assignment is audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/applications/{applicationId}/assignments")]
    [Covers("GET /sqlos/admin/auth/api/applications/{applicationId}/access/check")]
    [Covers("POST /sqlos/admin/auth/api/applications/{applicationId}/access-mode")]
    public async Task A_machine_client_service_account_can_be_assigned_within_its_organization()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        await AdminIdentity.CreateClientAsync(t, Portal);
        await t.Setup.OperatorPostAsync(AdminIdentity.Api + "/machine-clients", new
        {
            clientId = "reporting-worker",
            displayName = "Reporting Worker",
            audience = "https://portal.example.test/api",
            scopes = new[] { "reports.read" },
            organizationId = acme.Id,
            grants = Array.Empty<object>()
        });
        var serviceAccountId = t.Discard(await t.Operator.GetAsync("/sqlos/admin/fga/api/service-accounts"))
            .Json!["data"]!.AsArray().First(item => item!["clientId"]!.GetValue<string>() == "reporting-worker")!["id"]!.GetValue<string>();
        var application = AdminIdentity.Api + "/applications/portal";

        t.Observe(await t.Operator.PostJsonAsync(application + "/access-mode", new { accessMode = "internal_only" }), "make portal internal-only");
        t.Observe(
            await t.Operator.PostJsonAsync(application + "/assignments", new { principalType = "service_account", principalId = serviceAccountId, organizationId = globex.Id }),
            "the service account belongs to acme, so it cannot be assigned in globex");
        t.Observe(
            await t.Operator.PostJsonAsync(application + "/assignments", new { principalType = "service_account", principalId = serviceAccountId, organizationId = acme.Id, reason = "Report export" }),
            "assign it in acme");
        t.Observe(
            await t.Operator.GetAsync($"{application}/access/check?userId={serviceAccountId}&organizationId={acme.Id}"),
            "the service account is allowed by its assignment");
        await t.ObserveAuditAsync("audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/applications/{applicationId}/access-mode")]
    [Covers("GET /sqlos/admin/auth/api/applications/{applicationId}/access/check")]
    [Covers("POST /sqlos/admin/auth/api/clients/{clientId}/enable")]
    [Covers("POST /sqlos/auth/token")]
    public async Task The_disabled_access_mode_disables_the_client_until_it_is_enabled()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        await AdminIdentity.CreateClientAsync(t, Portal);
        var session = await AdminIdentity.PasswordLoginAsync(t, alice, clientId: "portal");
        var application = AdminIdentity.Api + "/applications/portal";

        t.Observe(await t.Operator.PostJsonAsync(application + "/access-mode", new { accessMode = "disabled" }), "disable access to portal");
        t.Observe(
            await t.Api.PostFormAsync("/sqlos/auth/token", AdminIdentity.Refresh(session.RefreshToken, "portal")),
            "portal's sessions were revoked");
        t.Observe(await t.Operator.GetAsync($"{application}/access/check?userId={alice.Id}"), "every check is denied");
        t.Observe(await t.Operator.PostJsonAsync(application + "/access-mode", new { accessMode = "all_organizations" }), "switching the mode back leaves the client disabled");
        t.Observe(await t.Operator.GetAsync($"{application}/access/check?userId={alice.Id}"), "so checks are still denied");
        t.Observe(await t.Operator.PostJsonAsync(AdminIdentity.Api + "/clients/portal/enable", new { }), "enable the client");
        t.Observe(await t.Operator.GetAsync($"{application}/access/check?userId={alice.Id}"), "access is allowed again");
        await t.ObserveAuditAsync("mode changes and the enable");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/applications/{applicationId}/access-mode")]
    [Covers("POST /sqlos/admin/auth/api/applications/{applicationId}/assignments")]
    [Covers("GET /sqlos/admin/auth/api/applications/{applicationId}/assignments")]
    [Covers("DELETE /sqlos/admin/auth/api/applications/{applicationId}/assignments/{assignmentId}")]
    [Covers("GET /sqlos/admin/auth/api/applications/{applicationId}/access/check")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/applications")]
    public async Task Access_modes_and_assignments_are_validated()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var dormant = await t.Setup.CreateOrganizationAsync("dormant");
        t.Discard(await t.Operator.PutJsonAsync($"{AdminIdentity.Api}/organizations/{dormant.Id}", new { name = dormant.Name, slug = dormant.Slug, isActive = false }));
        await AdminIdentity.CreateClientAsync(t, Portal);
        await t.SkipAuditAsync();
        var application = AdminIdentity.Api + "/applications/portal";
        var assignments = application + "/assignments";

        t.Observe(await t.Operator.PostJsonAsync(application + "/access-mode", new { accessMode = "everyone" }), "an unknown access mode is refused");
        t.Observe(
            await t.Operator.PostJsonAsync($"{AdminIdentity.Api}/applications/{BehaviorLockConstants.AppClientId}/access-mode", new { accessMode = "disabled" }),
            "the dashboard cannot change a code-owned client's access mode");
        t.Observe(await t.Operator.PostJsonAsync(assignments, new { principalType = "team", principalId = "x" }), "an unknown principal type is refused");
        t.Observe(await t.Operator.PostJsonAsync(assignments, new { principalType = "user", principalId = alice.Id, access = "maybe" }), "access must be allowed or denied");
        t.Observe(await t.Operator.PostJsonAsync(assignments, new { principalType = "organization" }), "an organization assignment needs an organization");
        t.Observe(await t.Operator.PostJsonAsync(assignments, new { principalType = "user" }), "a user assignment needs a principal");
        t.Observe(await t.Operator.PostJsonAsync(assignments, new { principalType = "role", roleKey = "admin" }), "a role assignment needs an organization");
        t.Observe(await t.Operator.PostJsonAsync(assignments, new { principalType = "role", organizationId = acme.Id }), "and a role key");
        t.Observe(
            await t.Operator.PostJsonAsync(assignments, new { principalType = "user", principalId = alice.Id, reason = new string('r', 501) }),
            "a reason over five hundred characters is refused");
        t.Observe(await t.Operator.PostJsonAsync(assignments, new { principalType = "organization", organizationId = dormant.Id }), "an inactive organization is refused");
        t.Observe(await t.Operator.PostJsonAsync(assignments, new { principalType = "user", principalId = "usr_missing" }), "an unknown user is refused");
        t.Observe(
            await t.Operator.PostJsonAsync(assignments, new { principalType = "user", principalId = alice.Id, organizationId = acme.Id }),
            "a user scoped to an organization she is not a member of is refused");
        t.Observe(await t.Operator.PostJsonAsync(assignments, new { principalType = "user_group", principalId = "grp_missing" }), "user_group is read as group, and an unknown group is refused");
        t.Observe(await t.Operator.PostJsonAsync(assignments, new { principalType = "service_account", principalId = "sa_missing" }), "an unknown service account is refused");
        t.Observe(await t.Operator.PostJsonAsync(assignments, new { principalType = "agent", principalId = "agent_missing" }), "an unknown agent is refused");
        t.Observe(
            await t.Operator.PostJsonAsync(assignments, new { principalType = "user", principalId = alice.Id }),
            "a user assignment on an application that allows every organization is accepted");
        t.Observe(await t.Operator.DeleteAsync(assignments + "/asa_missing"), "an unknown assignment cannot be revoked");
        t.Observe(await t.Operator.GetAsync($"{AdminIdentity.Api}/applications/cli_missing/assignments"), "listing an unknown application's assignments is refused");
        t.Observe(await t.Operator.GetAsync($"{AdminIdentity.Api}/applications/cli_missing/access/check"), "and so is checking access to it");

        t.Observe(await t.Operator.PostJsonAsync($"{AdminIdentity.Api}/applications/cli_missing/access-mode", new { accessMode = "disabled" }), "nor can its mode change");
        t.Observe(await t.Operator.PostJsonAsync($"{AdminIdentity.Api}/applications/cli_missing/assignments", new { principalType = "user", principalId = alice.Id }), "nor can it be assigned");
        t.Observe(await t.Operator.DeleteAsync($"{AdminIdentity.Api}/applications/cli_missing/assignments/asa_missing"), "nor can its assignments be revoked");
        t.Observe(await t.Operator.GetAsync($"{AdminIdentity.Api}/organizations/org_missing/applications"), "an unknown organization's applications are not found");
        await t.ObserveAuditAsync("only the accepted assignment is audited");
        await t.ApproveAsync();
    }
}

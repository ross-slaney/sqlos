using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminIdentity;

/// <summary>
/// Organization memberships through both admin routes (the organization-scoped route and the flat
/// one that takes the organization in the body), the all-organization and per-organization lists,
/// and what a membership changes at sign-in.
/// </summary>
[TestClass]
public sealed class AdminIdentityMembershipScenarios
{
    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/memberships")]
    [Covers("POST /sqlos/admin/auth/api/memberships")]
    [Covers("GET /sqlos/admin/auth/api/memberships")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/memberships")]
    [Covers("POST /sqlos/auth/password/login")]
    [Covers("GET /sqlos/admin/auth/api/users/{userId}/memberships")]
    public async Task Operator_adds_members_through_both_routes_and_the_member_signs_in_to_the_organization()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var bob = await t.Setup.CreateUserAsync("bob");
        var carol = await t.Setup.CreateUserAsync("carol");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        var memberships = AdminIdentity.Api + "/memberships";
        var acmeMemberships = $"{AdminIdentity.Api}/organizations/{acme.Id}/memberships";

        t.Observe(
            await t.Operator.PostJsonAsync(acmeMemberships, new { userId = alice.Id, role = "admin" }),
            "add alice to acme through the organization route");
        t.Observe(
            await t.Operator.PostJsonAsync(memberships, new { organizationId = globex.Id, userId = alice.Id, role = "member" }),
            "add alice to globex through the flat route");
        t.Observe(
            await t.Operator.PostJsonAsync(acmeMemberships, new { userId = bob.Id, role = "member" }),
            "add bob to acme");
        t.Observe(
            await t.Operator.PostJsonAsync(memberships, new { organizationId = acme.Id, userId = carol.Id, role = "billing" }),
            "a role is free text");
        await t.ObserveAuditAsync("membership creation writes no audit event");

        t.Observe(await t.Operator.GetAsync(memberships), "all memberships, by organization name then user name");
        t.Observe(await t.Operator.GetAsync(memberships + "?search=billing"), "search matches the role");
        t.Observe(await t.Operator.GetAsync(memberships + "?search=Globex"), "and the organization name");
        var page = t.Observe(await t.Operator.GetAsync(memberships + "?pageSize=2"), "two to a page");
        t.Observe(
            await t.Operator.GetAsync(memberships + "?pageSize=2&cursor=" + Uri.EscapeDataString(page.JsonString("nextCursor"))),
            "the next page");
        t.Observe(await t.Operator.GetAsync(acmeMemberships), "acme's members, by user name");
        t.Observe(await t.Operator.GetAsync(acmeMemberships + "?search=Bob"), "search within the organization");
        var acmePage = t.Observe(await t.Operator.GetAsync(acmeMemberships + "?pageSize=1"), "one member to a page");
        t.Observe(
            await t.Operator.GetAsync(acmeMemberships + "?pageSize=1&cursor=" + Uri.EscapeDataString(acmePage.JsonString("nextCursor"))),
            "the next member");
        t.Observe(
            await t.Operator.GetAsync(memberships + "?cursor=" + Uri.EscapeDataString(acmePage.JsonString("nextCursor"))),
            "an organization list's cursor does not page the flat list");
        t.Observe(await t.Operator.GetAsync($"{AdminIdentity.Api}/organizations/org_missing/memberships"), "an unknown organization has no members");

        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/login", new { email = carol.Email, password = carol.Password, clientId = BehaviorLockConstants.AppClientId }),
            "carol, a member of one organization, signs in to it");
        // Not observed: alice's direct login, which asks a member of two organizations to choose
        // one, lists the organizations from a query without ORDER BY, so their order varies.
        t.Observe(await t.Operator.GetAsync($"{AdminIdentity.Api}/users/{alice.Id}/memberships"), "alice belongs to both organizations");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/memberships")]
    [Covers("POST /sqlos/admin/auth/api/memberships")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/memberships")]
    public async Task Memberships_for_unknown_users_or_organizations_fail_in_the_database()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback, options => options.AnswerUnhandledExceptionsAsServerErrors = true);
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var acmeMemberships = $"{AdminIdentity.Api}/organizations/{acme.Id}/memberships";

        t.Observe(
            await t.Operator.PostJsonAsync(acmeMemberships, new { userId = "usr_missing", role = "member" }),
            "an unknown user breaks the foreign key: 500");
        t.Observe(
            await t.Operator.PostJsonAsync(AdminIdentity.Api + "/memberships", new { organizationId = "org_missing", userId = alice.Id, role = "member" }),
            "an unknown organization breaks the foreign key: 500");
        t.Observe(
            await t.Operator.PostJsonAsync(acmeMemberships, new { userId = alice.Id }),
            "a missing role reaches the database: 500");
        t.Observe(
            await t.Operator.PostJsonAsync(acmeMemberships, new { userId = alice.Id, role = new string('r', 51) }),
            "a role longer than its fifty-character column: 500");
        t.Observe(
            await t.Operator.PostJsonAsync(acmeMemberships, new { userId = alice.Id, role = "" }),
            "an empty role is accepted");
        t.Observe(await t.Operator.GetAsync(acmeMemberships), "the only membership has the empty role");
        await t.ApproveAsync();
    }
}

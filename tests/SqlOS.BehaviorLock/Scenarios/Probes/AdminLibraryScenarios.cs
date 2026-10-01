using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Probes;

/// <summary>
/// The <c>SqlOSAdminService</c> members hosts call from their own code (startup seeding, their own
/// admin screens), through the <c>/__probe/admin</c> routes.
/// </summary>
[TestClass]
public sealed class AdminLibraryScenarios
{
    /// <summary>
    /// Known defect #415 (7.2.1): creating a user, an organization, or a membership through
    /// <c>SqlOSAdminService</c> writes no audit event, and adding an existing membership again
    /// silently reactivates it and changes its role, also without an event.
    /// </summary>
    [Scenario]
    [Covers("POST /__probe/admin/users")]
    [Covers("POST /__probe/admin/organizations")]
    [Covers("POST /__probe/admin/organizations/{organizationId}/memberships")]
    [Covers("GET /__probe/admin/users/{userId}/organizations")]
    [Covers("GET /__probe/admin/users/{userId}/memberships/{organizationId}")]
    public async Task The_admin_service_creates_identities_without_audit_events_CurrentBehavior_KnownDefect_415()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var probe = t.NewClient("probe");
        var aliceEmail = t.Unique.Email("alice");

        var alice = t.Observe(
            await probe.PostJsonAsync("/__probe/admin/users", new { displayName = "Alice", email = aliceEmail, password = t.Unique.Password("alice") }),
            "CreateUserAsync with a password");
        t.Observe(
            await probe.PostJsonAsync("/__probe/admin/users", new { displayName = "Bob", email = t.Unique.Email("bob") }),
            "and without one");
        var acme = t.Observe(
            await probe.PostJsonAsync("/__probe/admin/organizations", new { name = "Acme", slug = "acme", primaryDomain = "Acme.Example.TEST" }),
            "CreateOrganizationAsync with a slug and a primary domain");
        t.Observe(
            await probe.PostJsonAsync("/__probe/admin/organizations", new { name = "Beta Industries" }),
            "without a slug, the slug comes from the name");
        var aliceId = alice.JsonString("id");
        var acmeId = acme.JsonString("id");
        t.Observe(
            await probe.PostJsonAsync($"/__probe/admin/organizations/{acmeId}/memberships", new { userId = aliceId, role = "member" }),
            "CreateMembershipAsync");
        t.Observe(
            await probe.PostJsonAsync($"/__probe/admin/organizations/{acmeId}/memberships", new { userId = aliceId, role = "admin" }),
            "adding the same membership again changes its role");
        t.Observe(await probe.GetAsync($"/__probe/admin/users/{aliceId}/organizations"), "the membership as the user's organizations show it");
        t.Observe(await probe.GetAsync($"/__probe/admin/users/{aliceId}/memberships/{acmeId}"), "UserHasMembershipAsync");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/users/{aliceId}", "the user as the admin API reads it back");

        await t.ObserveAuditAsync("no audit event for any of it");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /__probe/admin/users")]
    [Covers("POST /__probe/admin/organizations")]
    public async Task The_admin_service_rejects_invalid_and_duplicate_identities()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var probe = t.NewClient("probe");

        t.Observe(
            await probe.PostJsonAsync("/__probe/admin/users", new { displayName = "Nobody", email = "not-an-email" }),
            "an invalid email address is refused");
        t.Observe(
            await probe.PostJsonAsync("/__probe/admin/users", new { displayName = "Alice Again", email = alice.Email.ToUpperInvariant() }),
            "an address that differs only in case already exists");
        var duplicate = t.Observe(
            await probe.PostJsonAsync("/__probe/admin/organizations", new { name = "Acme Two", slug = acme.Slug }),
            "a slug that is taken gets a random suffix");
        t.Scrub(duplicate.JsonString("slug"), "slug", "acme-with-suffix");
        t.Observe(
            await probe.PostJsonAsync("/__probe/admin/organizations", new { name = "Gamma", slug = "  Gamma Corp! " }),
            "a slug is normalized");
        await ProbeCalls.ObserveOrUnhandledAsync(
            t,
            () => probe.PostJsonAsync("/__probe/admin/organizations/org_00000000000000000000000000000000/memberships", new { userId = alice.Id, role = "member" }),
            "CreateMembershipAsync does not look the organization up, so the save fails in the database");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /__probe/admin/applications/{clientId}/access-check")]
    [Covers("POST /__probe/admin/applications/{clientId}/assignments")]
    [Covers("POST /__probe/admin/applications/{clientId}/access-mode")]
    public async Task Application_access_follows_the_access_mode_and_assignments()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var alice = await t.Setup.CreateUserAsync("alice");
        var bob = await t.Setup.CreateUserAsync("bob");
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        await t.Setup.AddMembershipAsync(acme, alice);
        var probe = t.NewClient("probe");
        const string console = "/__probe/admin/applications/atlas-ops-console";

        t.Observe(await probe.GetAsync($"{console}/access-check?userId={alice.Id}"), "selected users only: no assignment, no access");
        t.Observe(
            await probe.PostJsonAsync($"{console}/assignments", new { principalType = "user", principalId = alice.Id, reason = "On call" }),
            "AssignApplicationAsync allows one user");
        t.Observe(await probe.GetAsync($"{console}/access-check?userId={alice.Id}"), "the assignment allows her");
        t.Observe(await probe.GetAsync($"{console}/access-check?userId={bob.Id}"), "but nobody else");
        t.Observe(
            await probe.PostJsonAsync($"{console}/assignments", new { principalType = "user", principalId = bob.Id, access = "denied", reason = "Contractor" }),
            "an explicit deny assignment");
        t.Observe(
            await probe.PostJsonAsync($"{console}/access-mode", new { accessMode = "all_organizations" }),
            "SetApplicationAccessModeAsync opens the application to everyone");
        t.Observe(await probe.GetAsync($"{console}/access-check?userId={alice.Id}"), "everyone is allowed by the mode");
        t.Observe(await probe.GetAsync($"{console}/access-check?userId={bob.Id}"), "except a denied principal");
        t.Observe(
            await probe.PostJsonAsync($"{console}/access-mode", new { accessMode = "selected_organizations" }),
            "restrict it to selected organizations");
        t.Observe(await probe.GetAsync($"{console}/access-check?userId={alice.Id}&organizationId={acme.Id}"), "Acme is not selected");
        t.Observe(
            await probe.PostJsonAsync($"{console}/assignments", new { principalType = "organization", organizationId = acme.Id }),
            "select Acme");
        t.Observe(await probe.GetAsync($"{console}/access-check?userId={alice.Id}&organizationId={acme.Id}"), "now Acme is allowed");
        t.Observe(
            await probe.PostJsonAsync($"{console}/assignments", new { principalType = "user", principalId = "usr_00000000000000000000000000000000" }),
            "a user that does not exist cannot be assigned");
        t.Observe(
            await probe.PostJsonAsync($"{console}/assignments", new { principalType = "organization" }),
            "an organization assignment needs the organization");
        t.Observe(
            await probe.PostJsonAsync($"{console}/assignments", new { principalType = "robot", principalId = alice.Id }),
            "an unknown principal type is refused");
        t.Observe(
            await probe.PostJsonAsync($"{console}/access-mode", new { accessMode = "everyone" }),
            "an unknown access mode is refused");
        t.Observe(
            await probe.PostJsonAsync($"{console}/access-mode", new { accessMode = "disabled" }),
            "disabling the application");
        t.Observe(await probe.GetAsync($"{console}/access-check?userId={alice.Id}&organizationId={acme.Id}"), "denies everyone");
        t.Observe(await probe.GetAsync($"/__probe/admin/applications/no-such-client/access-check?userId={alice.Id}"), "an unknown client is refused");

        await t.ObserveAuditAsync("access mode and assignment events");
        await t.ApproveAsync();
    }
}

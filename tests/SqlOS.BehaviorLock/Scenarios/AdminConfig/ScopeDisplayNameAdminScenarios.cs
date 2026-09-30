using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Database;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminConfig;

/// <summary>
/// Scope display names (<c>/sqlos/admin/auth/api/scope-display-names</c>): dashboard-owned entries
/// beside a code-owned seed, the validation branches, and the reconciliation rule that a seed
/// removed from code leaves an orphaned row the dashboard may delete but not edit. The code-owned
/// entry comes from <c>SeedScopeDisplayName</c>, the capability under test, on the dashboard
/// callback profile.
/// </summary>
[TestClass]
public sealed class ScopeDisplayNameAdminScenarios
{
    private const string Route = "/sqlos/admin/auth/api/scope-display-names";

    private static void SeedWorkspaceRead(ScenarioOptions options)
        => options.ConfigureSqlOS = sqlos => sqlos.AuthServer.SeedScopeDisplayName(
            "workspace.read",
            "Read workspaces",
            "View workspaces you can access.");

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/scope-display-names")]
    [Covers("POST /sqlos/admin/auth/api/scope-display-names")]
    [Covers("PUT /sqlos/admin/auth/api/scope-display-names/{id}")]
    [Covers("DELETE /sqlos/admin/auth/api/scope-display-names/{id}")]
    public async Task Operator_manages_scope_display_names_beside_a_code_owned_one()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardCallback, SeedWorkspaceRead);

        t.Observe(
            await t.Operator.GetAsync(Route, options => options.WithoutCredentials()),
            "without operator credentials the list is not found");
        var seeded = t.Observe(await t.Operator.GetAsync(Route), "SeedScopeDisplayName owns the workspace.read entry");
        var seededId = seeded.JsonString("data.0.id");

        var created = t.Observe(
            await t.Operator.PostJsonAsync(Route, new
            {
                scope = " workspace.write ",
                displayName = " Edit workspaces ",
                description = " Create and change workspaces you can access. "
            }),
            "add a dashboard-owned display name; values are trimmed");
        var createdId = created.JsonString("id");
        t.Observe(
            await t.Operator.PutJsonAsync($"{Route}/{createdId}", new { displayName = "Change workspaces", description = " " }),
            "rename it and clear its description");
        t.Observe(
            await t.Operator.PutJsonAsync($"{Route}/{seededId}", new { displayName = "Look at workspaces" }),
            "the code-owned entry cannot be edited here");
        t.Observe(
            await t.Operator.DeleteAsync($"{Route}/{seededId}"),
            "nor deleted while its seed exists");
        t.Observe(await t.Operator.GetAsync(Route), "both entries, ordered by scope");
        t.Observe(await t.Operator.DeleteAsync($"{Route}/{createdId}"), "delete the dashboard-owned entry");
        t.Observe(await t.Operator.GetAsync(Route), "only the code-owned entry remains");

        await t.ObserveAuditAsync("dashboard changes are audited; rejected ones are not");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/scope-display-names")]
    [Covers("PUT /sqlos/admin/auth/api/scope-display-names/{id}")]
    [Covers("DELETE /sqlos/admin/auth/api/scope-display-names/{id}")]
    public async Task Invalid_scope_display_name_changes_are_rejected()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardCallback, SeedWorkspaceRead);
        var created = await t.Setup.OperatorPostAsync(Route, new { scope = "profile", displayName = "Your profile" });
        var createdId = created.JsonString("id");
        const string unknownId = "sdn_00000000000000000000000000000000";

        t.Observe(await t.Operator.PostJsonAsync(Route, new { scope = " ", displayName = "Nothing" }), "a blank scope");
        t.Observe(await t.Operator.PostJsonAsync(Route, new { scope = "email", displayName = new string('d', 201) }), "a display name over 200 characters");
        t.Observe(
            await t.Operator.PostJsonAsync(Route, new { scope = "email", displayName = "Email", description = new string('x', 1001) }),
            "a description over 1000 characters");
        t.Observe(
            await t.Operator.PostJsonAsync(Route, new { scope = "workspace.read", displayName = "Read" }),
            "a scope that already has a display name, even a code-owned one");
        t.Observe(await t.Operator.PutJsonAsync($"{Route}/{createdId}", new { displayName = "" }), "an edit that blanks the display name");
        t.Observe(await t.Operator.PutJsonAsync($"{Route}/{unknownId}", new { displayName = "Ghost" }), "editing an unknown entry");
        t.Observe(await t.Operator.DeleteAsync($"{Route}/{unknownId}"), "deleting an unknown entry");

        await t.ObserveStateAsync(Route, "the entries are unchanged");
        await t.ObserveAuditAsync("rejected changes write no audit events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/auth/api/scope-display-names")]
    [Covers("PUT /sqlos/admin/auth/api/scope-display-names/{id}")]
    [Covers("DELETE /sqlos/admin/auth/api/scope-display-names/{id}")]
    [Covers("GET /sqlos/admin/audit/api/events")]
    public async Task A_seed_removed_from_code_leaves_an_orphan_the_dashboard_may_delete()
    {
        var database = await BehaviorLockDatabase.CreateDatabaseAsync("sdnorphan");
        var keys = Directory.CreateTempSubdirectory("sqlos-bl-keys-").FullName;
        try
        {
            // The first deployment seeds workspace.read; the second no longer declares it.
            await using (await Transcript.StartAsync(HostProfiles.DashboardCallback, options =>
                         {
                             SeedWorkspaceRead(options);
                             options.ExistingDatabase = database;
                             options.DataProtectionKeysDirectory = keys;
                         }))
            {
            }

            await using var t = await AdminConfigHost.StartAsync(HostProfiles.DashboardCallback, options =>
            {
                options.ExistingDatabase = database;
                options.DataProtectionKeysDirectory = keys;
            });
            t.Note("The database was first started with SeedScopeDisplayName(\"workspace.read\", ...); this host no longer declares that seed.");

            var orphan = t.Observe(await t.Operator.GetAsync(Route), "the entry is still code-owned but marked orphaned");
            var orphanId = orphan.JsonString("data.0.id");
            t.Observe(
                await t.Operator.GetAsync("/sqlos/admin/audit/api/events?action=configuration.reconciled&search=scope_display_name"),
                "startup reconciliation recorded the seed's creation and then its orphaning");
            t.Observe(
                await t.Operator.PutJsonAsync($"{Route}/{orphanId}", new { displayName = "Adopted by the dashboard" }),
                "an orphan still cannot be edited from the dashboard");
            t.Observe(await t.Operator.DeleteAsync($"{Route}/{orphanId}"), "but it can be deleted, which is the supported cleanup");
            t.Observe(await t.Operator.GetAsync(Route), "no entries remain");

            await t.ObserveAuditAsync("the cleanup is audited");
            await t.ApproveAsync();
        }
        finally
        {
            await BehaviorLockDatabase.DropDatabaseAsync(database);
            Directory.Delete(keys, recursive: true);
        }
    }
}

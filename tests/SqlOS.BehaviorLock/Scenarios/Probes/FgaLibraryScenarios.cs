using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using SqlOS.BehaviorLock.Scenarios.Dashboard;

namespace SqlOS.BehaviorLock.Scenarios.Probes;

/// <summary>
/// The FGA library APIs a host calls in-process, through the <c>/__probe/fga</c> routes:
/// <c>ISqlOSFgaAuthService</c> checks, traces, and list filters; the resource helpers
/// (<c>CreateResource</c>, <c>CreateResourceAsync</c>, <c>CreateResourceWithIdAsync</c>,
/// <c>ProvisionResourceWithIdAsync</c>, <c>DeleteResourceAsync</c>); <c>ISqlOSResourceEntity</c>
/// synchronization on save; and the subject and grant helpers.
/// </summary>
[TestClass]
public sealed class FgaLibraryScenarios
{
    private const string Read = BehaviorLockAuthorization.ReadPermission;
    private const string Write = BehaviorLockAuthorization.WritePermission;

    [Scenario]
    [Covers("POST /__probe/fga/check")]
    [Covers("POST /__probe/fga/allows")]
    [Covers("POST /__probe/fga/capability")]
    [Covers("POST /__probe/fga/trace")]
    [Covers("PUT /__probe/fga/workspaces/{id}")]
    public async Task Access_checks_follow_the_hierarchy_and_explain_each_denial()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var fga = new FgaFixture(t);
        await fga.UserSubjectAsync(alice);
        await fga.AgentSubjectAsync("agent-indexer", "Indexer");
        var alpha = await fga.WorkspaceAsync("alpha", "Alpha");
        var docs = await fga.WorkspaceAsync("docs", "Docs", alpha);
        var beta = await fga.WorkspaceAsync("beta", "Beta");
        var ledger = await fga.RootChildAsync("workspace::ledger", "Ledger");
        await fga.GrantAsync(alice.Id, alpha, BehaviorLockAuthorization.ReaderRole);
        await fga.GrantAsync("agent-indexer", "root", BehaviorLockAuthorization.AdminRole);
        var probe = t.NewClient("probe");

        t.Observe(await Check(probe, alice.Id, Read, docs), "read on Docs is inherited from the grant on Alpha");
        t.Observe(await probe.PostJsonAsync("/__probe/fga/allows", new { subjectId = alice.Id, permissionKey = Read, resourceId = docs }), "Allows returns only the decision");
        t.Observe(await probe.PostJsonAsync("/__probe/fga/allows", new { subjectId = alice.Id, permissionKey = Write, resourceId = docs }), "the reader role does not write");
        t.Observe(await Check(probe, alice.Id, Read, beta), "no grant anywhere on Beta's path");
        t.Observe(await Check(probe, "agent-indexer", Write, ledger), "a grant on the root resource reaches a workspace under it");
        t.Observe(await Check(probe, "agent-indexer", Write, alpha), "but not a top-level workspace, which is not under the root");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/capability", new { subjectId = "agent-indexer", permissionKey = Write }),
            "HasCapabilityAsync checks the root resource, where a workspace permission never applies");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/capability", new { subjectId = alice.Id, permissionKey = Read }),
            "so it is false for a user without a root grant too");
        t.Observe(await Check(probe, alice.Id, Read, "workspace::missing"), "an unknown resource");
        t.Observe(await Check(probe, alice.Id, "workspace.delete", alpha), "an unknown permission");
        t.Observe(await Check(probe, alice.Id, Read, "root"), "a permission scoped to another resource type");
        t.Observe(await Check(probe, "no-such-subject", Read, alpha), "an unknown subject resolves to no subjects");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/trace", new { subjectId = alice.Id, permissionKey = Read, resourceId = docs }),
            "the structured trace of the inherited decision");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/trace", new { subjectId = "no-such-subject", permissionKey = Read, resourceId = docs }),
            "the trace of an unknown subject");

        t.Observe(
            await probe.PutJsonAsync("/__probe/fga/workspaces/docs", new { name = "Docs", parentResourceId = beta }),
            "saving a moved workspace moves its resource");
        t.Observe(await Check(probe, alice.Id, Read, docs), "Docs no longer inherits from Alpha");
        t.Observe(
            await probe.PutJsonAsync("/__probe/fga/workspaces/alpha", new { name = "Alpha", isActive = false }),
            "saving an inactive workspace deactivates its resource");
        t.Observe(await Check(probe, alice.Id, Read, alpha), "an inactive resource is not checked at all");
        t.Observe(
            await probe.PutJsonAsync("/__probe/fga/workspaces/alpha", new { name = "Alpha Prime", isActive = true }),
            "renaming and reactivating it");
        t.Observe(await Check(probe, alice.Id, Read, alpha), "the grant applies again");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /__probe/fga/resources")]
    [Covers("DELETE /__probe/fga/resources/{resourceId}")]
    [Covers("POST /__probe/fga/check")]
    public async Task Resource_helpers_create_provision_and_delete_resources()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var fga = new FgaFixture(t);
        await fga.AgentSubjectAsync("agent-indexer", "Indexer");
        var probe = t.NewClient("probe");
        const string type = BehaviorLockAuthorization.WorkspaceType;

        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/resources", new { mode = "create", resourceTypeId = type, name = "Ledger", resourceId = "workspace::ledger" }),
            "CreateResource adds a resource under the given parent (the probe passes the root)");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/resources", new { mode = "create", resourceTypeId = type, name = "Unnamed" }),
            "without an ID CreateResource generates a GUID");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/resources", new { mode = "create-async", resourceTypeId = type, name = "Generated", parentResourceId = "workspace::ledger" }),
            "CreateResourceAsync generates a type-prefixed ID");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/resources", new { mode = "create-with-id", resourceTypeId = type, name = "Reports", resourceId = "workspace::reports", parentResourceId = "workspace::ledger", description = "Quarterly reports" }),
            "CreateResourceWithIdAsync uses the given ID");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/resources", new { mode = "create-with-id", resourceTypeId = type, name = "Reports", resourceId = "workspace::reports" }),
            "the same ID again is refused");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/resources", new { mode = "create-with-id", resourceTypeId = "folder", name = "Folder", resourceId = "folder::one" }),
            "an unknown resource type is refused");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/resources", new { mode = "create-with-id", resourceTypeId = type, name = "Orphan", resourceId = "workspace::orphan", parentResourceId = "workspace::missing" }),
            "an unknown parent is refused");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/resources", new { mode = "create-with-id", resourceTypeId = type, name = "Loop", resourceId = "workspace::loop", parentResourceId = "workspace::loop" }),
            "a resource cannot be its own parent");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/resources", new { mode = "create-with-id", resourceTypeId = type, name = " ", resourceId = "workspace::blank" }),
            "a blank name is refused");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/resources", new { mode = "provision-with-id", resourceTypeId = type, name = "Archive", resourceId = "workspace::archive", parentResourceId = "workspace::ledger" }),
            "ProvisionResourceWithIdAsync creates a missing resource");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/resources", new { mode = "provision-with-id", resourceTypeId = type, name = "Archive 2026", resourceId = "workspace::archive" }),
            "and updates an existing one, keeping its parent when none is given");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/resources", new { mode = "provision-with-id", resourceTypeId = type, name = "Ledger", resourceId = "workspace::ledger", parentResourceId = "workspace::archive" }),
            "a parent that is already a descendant would make a cycle");
        await ProbeCalls.ObserveOrUnhandledAsync(
            t,
            () => probe.PostJsonAsync("/__probe/fga/resources", new { mode = "create", resourceTypeId = type, name = "Stray", resourceId = "workspace::stray", parentResourceId = "workspace::missing" }),
            "CreateResource does not look the parent up, so the save fails in the database");

        await fga.GrantAsync("agent-indexer", "workspace::reports", BehaviorLockAuthorization.AdminRole);
        t.Observe(await probe.DeleteAsync("/__probe/fga/resources/workspace::ledger"), "a resource with children cannot be deleted");
        t.Observe(await probe.DeleteAsync("/__probe/fga/resources/workspace::reports"), "DeleteResourceAsync removes a leaf resource and its grants");
        t.Observe(await Check(probe, "agent-indexer", Read, "workspace::reports"), "the deleted resource is gone");
        t.Observe(await probe.DeleteAsync("/__probe/fga/resources/workspace::reports"), "deleting it again is refused");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /__probe/fga/workspaces")]
    [Covers("PUT /__probe/fga/workspaces/{id}")]
    [Covers("POST /__probe/fga/check")]
    public async Task Saving_resource_entities_validates_the_resource_hierarchy()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var fga = new FgaFixture(t);
        await fga.UserSubjectAsync(alice);
        await fga.RootChildAsync("workspace::taken", "Taken");
        var probe = t.NewClient("probe");

        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/workspaces", new { id = "orphan", name = "Orphan", parentResourceId = "workspace::missing" }),
            "a workspace whose parent resource does not exist is not saved");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/workspaces", new { id = "loop", name = "Loop", parentResourceId = "workspace::loop" }),
            "nor one that is its own parent");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/workspaces", new { id = "taken", name = "Taken" }),
            "nor a new workspace whose resource already exists");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/workspaces", new { id = "blank", name = " " }),
            "nor one without a resource name");

        var parent = await fga.WorkspaceAsync("level-0", "Level 0");
        var top = parent;
        for (var level = 1; level <= 10; level++)
        {
            parent = await fga.WorkspaceAsync($"level-{level}", $"Level {level}", parent);
        }

        t.Note("Workspaces level-0 through level-10 form a chain eleven resources deep (not recorded).");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/workspaces", new { id = "level-11", name = "Level 11", parentResourceId = parent }),
            "a twelfth level exceeds the default maximum hierarchy depth of 10");
        await fga.GrantAsync(alice.Id, top, BehaviorLockAuthorization.ReaderRole);
        t.Observe(await Check(probe, alice.Id, Read, parent), "a grant at the top of the deepest allowed chain reaches its bottom");
        t.Observe(
            await probe.PutJsonAsync("/__probe/fga/workspaces/level-0", new { name = "Level 0", parentResourceId = parent }),
            "moving the top of the chain under its own descendant is a cycle");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /__probe/fga/subjects")]
    [Covers("POST /__probe/fga/grants")]
    [Covers("POST /__probe/fga/grants/revoke")]
    [Covers("POST /__probe/fga/check")]
    public async Task Subject_and_grant_helpers_are_idempotent_and_validate_their_targets()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var fga = new FgaFixture(t);
        var alpha = await fga.WorkspaceAsync("alpha", "Alpha");
        var probe = t.NewClient("probe");
        var reader = BehaviorLockAuthorization.ReaderRole;

        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/subjects", new { type = "user", subjectId = alice.Id, displayName = alice.DisplayName, email = alice.Email }),
            "ProvisionUserSubjectAsync");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/subjects", new { type = "user", subjectId = alice.Id, displayName = "Alice Smith" }),
            "provisioning the same subject again updates it");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/subjects", new { type = "agent", subjectId = "agent-indexer", displayName = "Indexer" }),
            "ProvisionAgentSubjectAsync");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/subjects", new { type = "service_account", subjectId = "sa-reporter", displayName = "Reporter", clientId = "reporter-client", clientSecretHash = "fixture-secret-hash" }),
            "ProvisionServiceAccountSubjectAsync");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/subjects", new { type = "agent", subjectId = alice.Id, displayName = "Alice" }),
            "a subject ID cannot change type");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/subjects", new { type = "agent", subjectId = "agent-blank", displayName = " " }),
            "a display name is required");

        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/grants", new { subjectId = alice.Id, resourceId = alpha, role = reader }),
            "GrantRoleAsync derives the grant ID from subject, resource, and role");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/grants", new { subjectId = alice.Id, resourceId = alpha, role = reader }),
            "granting it again returns the same grant");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/grants", new { subjectId = "no-such-subject", resourceId = alpha, role = reader }),
            "the subject must be provisioned first");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/grants", new { subjectId = alice.Id, resourceId = alpha, role = "no-such-role" }),
            "the role must exist");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/grants", new { subjectId = alice.Id, resourceId = "workspace::missing", role = reader }),
            "the resource must exist");
        t.Observe(await Check(probe, alice.Id, Read, alpha), "the grant allows read");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/grants/revoke", new { subjectId = alice.Id, resourceId = alpha, role = reader }),
            "RevokeRoleAsync removes the grant");
        t.Observe(await Check(probe, alice.Id, Read, alpha), "read is denied again");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/grants/revoke", new { subjectId = alice.Id, resourceId = alpha, role = reader }),
            "revoking a grant that does not exist is a no-op");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/grants/revoke", new { subjectId = "no-such-subject", resourceId = alpha, role = reader }),
            "but the subject must exist");

        await t.ObserveAuditAsync("FGA helper writes are not audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /__probe/fga/filter")]
    [Covers("PUT /__probe/fga/workspaces/{id}")]
    public async Task List_filters_include_only_accessible_active_resources()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var fga = new FgaFixture(t);
        await fga.UserSubjectAsync(alice);
        await fga.AgentSubjectAsync("agent-indexer", "Indexer");
        var alpha = await fga.WorkspaceAsync("alpha", "Alpha");
        await fga.WorkspaceAsync("docs", "Docs", alpha);
        await fga.WorkspaceAsync("beta", "Beta");
        var gamma = await fga.WorkspaceAsync("gamma", "Gamma");
        await fga.GrantAsync(alice.Id, alpha, BehaviorLockAuthorization.ReaderRole);
        await fga.GrantAsync(alice.Id, gamma, BehaviorLockAuthorization.AdminRole);
        var probe = t.NewClient("probe");

        t.Observe(await Filter(probe, alice.Id, Read), "read covers Alpha, its child Docs, and Gamma");
        t.Observe(await Filter(probe, alice.Id, Write), "write only Gamma, where the role grants it");
        t.Observe(await Filter(probe, "agent-indexer", Read), "a subject without grants sees nothing");
        t.Observe(await Filter(probe, alice.Id, "workspace.delete"), "an unknown permission matches nothing");
        t.Observe(await Filter(probe, "no-such-subject", Read), "neither does an unknown subject");
        t.Observe(
            await probe.PutJsonAsync("/__probe/fga/workspaces/alpha", new { name = "Alpha", isActive = false }),
            "deactivate Alpha");
        t.Observe(await Filter(probe, alice.Id, Read), "an inactive resource and the children it passed access to drop out");

        await t.ApproveAsync();
    }

    /// <summary>
    /// A host whose context implements the SqlOS interfaces itself (no <c>SqlOSDbContext</c>) gets
    /// no resource synchronization: saving an <c>ISqlOSResourceEntity</c> writes only the
    /// application row, and the host creates the FGA resource with the manual helpers.
    /// </summary>
    [Scenario]
    [Covers("POST /__probe/fga/workspaces")]
    [Covers("POST /__probe/fga/resources")]
    [Covers("POST /__probe/fga/filter")]
    [Covers("POST /__probe/fga/check")]
    public async Task A_host_without_resource_sync_creates_resources_itself()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.LegacyHost);
        var probe = t.NewClient("probe");
        t.Discard(await probe.PostJsonAsync("/__probe/fga/subjects", new { type = "agent", subjectId = "agent-indexer", displayName = "Indexer" }));

        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/workspaces", new { id = "alpha", name = "Alpha" }),
            "saving the workspace creates no FGA resource");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/grants", new { subjectId = "agent-indexer", resourceId = "workspace::alpha", role = BehaviorLockAuthorization.ReaderRole }),
            "so its resource cannot be granted on");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/resources", new { mode = "create-with-id", resourceTypeId = BehaviorLockAuthorization.WorkspaceType, name = "Alpha", resourceId = "workspace::alpha" }),
            "the host creates the resource with the manual helper");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/grants", new { subjectId = "agent-indexer", resourceId = "workspace::alpha", role = BehaviorLockAuthorization.ReaderRole }),
            "now the grant succeeds");
        t.Observe(await Check(probe, "agent-indexer", Read, "workspace::alpha"), "and checks see it");
        t.Observe(await Filter(probe, "agent-indexer", Read), "the list filter finds the workspace through its resource");

        await t.ApproveAsync();
    }

    private static Task<HttpExchange> Check(HttpActor probe, string subjectId, string permissionKey, string resourceId)
        => probe.PostJsonAsync("/__probe/fga/check", new { subjectId, permissionKey, resourceId });

    private static Task<HttpExchange> Filter(HttpActor probe, string subjectId, string permissionKey)
        => probe.PostJsonAsync("/__probe/fga/filter", new { subjectId, permissionKey });
}

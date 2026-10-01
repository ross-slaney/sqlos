using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// Group mappings turn a directory group into an FGA role grant. Every mapped grant must land on
/// the connection's grant boundary resource or one of its descendants (#421); a mapping that
/// cannot be proven inside the boundary, or whose role or resource is missing, fails closed and
/// says why in the sync events and the audit log.
/// </summary>
[TestClass]
public sealed class ScimGroupMappingScenarios
{
    private const string AcmeRoot = "workspace::acme";
    private const string AcmeProjects = "workspace::acme-projects";

    [Scenario]
    [Covers("POST /scim/v2/Groups")]
    [Covers("PATCH /scim/v2/Groups/{id}")]
    [Covers("DELETE /scim/v2/Groups/{id}")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}/mappings")]
    [Covers("GET /sqlos/admin/fga/api/users")]
    [Covers("POST /__probe/fga/check")]
    public async Task A_mapped_group_grants_its_members_a_role_inside_the_grant_boundary()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        await t.CreateWorkspaceResourceAsync(AcmeRoot, "Acme");
        await t.CreateWorkspaceResourceAsync(AcmeProjects, "Acme projects", AcmeRoot);
        var connection = await t.CreateBoundedScimConnectionAsync(acme, AcmeRoot);
        var mappingId = await t.CreateScimMappingAsync(connection, new
        {
            matchType = "display_name",
            groupDisplayName = "Engineering",
            roleKey = BehaviorLockAuthorization.ReaderRole,
            resourceId = AcmeProjects,
            description = "Engineering reads projects"
        });
        t.Scrub(mappingId, "scmap", "engineering");
        var directory = t.NewClient("directory");
        var ann = await ScimGroupScenarios.ProvisionAsync(t, directory, connection, domain, "ann", "Ann", "Archer");
        t.Note("The connection's grant boundary is workspace::acme; the mapping grants workspace_reader on workspace::acme-projects to Engineering.");

        var group = t.Observe(
            await directory.PostAsync($"{Scim.Root}/Groups", Scim.Group("Engineering", "directory-engineering", ann), connection.Token),
            "the directory pushes Engineering with Ann");
        var groupId = group.JsonString("id");
        t.Scrub(groupId, "grp", "engineering");
        var subject = t.Observe(
            await t.Operator.GetAsync("/sqlos/admin/fga/api/users?search=Ann"),
            "dashboard: SCIM mirrored Ann into an FGA user subject");
        var annSubject = subject.JsonString("data.0.subjectId");
        t.Scrub(annSubject, "subj", "ann");

        var probe = t.NewClient("probe");
        async Task CheckAsync(string permission, string resourceId, string caption)
            => t.Observe(await probe.PostJsonAsync("/__probe/fga/check", new { subjectId = annSubject, permissionKey = permission, resourceId }), caption);
        await CheckAsync(BehaviorLockAuthorization.ReadPermission, AcmeProjects, "Ann reads the projects workspace through the group");
        await CheckAsync(BehaviorLockAuthorization.WritePermission, AcmeProjects, "the reader role does not write");
        await CheckAsync(BehaviorLockAuthorization.ReadPermission, AcmeRoot, "the grant does not reach the parent");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/scim-connections/{connection.Id}/mappings", "dashboard: the mapping has one active grant, inside the boundary");

        t.Observe(
            await directory.PatchAsync($"{Scim.Root}/Groups/{groupId}", Scim.Patch(("remove", $"members[value eq \"{ann}\"]", null)), connection.Token),
            "the directory removes Ann from Engineering");
        await CheckAsync(BehaviorLockAuthorization.ReadPermission, AcmeProjects, "Ann no longer reads the projects workspace");
        t.Observe(await directory.DeleteAsync($"{Scim.Root}/Groups/{groupId}", connection.Token), "the directory deletes Engineering");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/scim-connections/{connection.Id}/mappings", "dashboard: deleting the group revoked the managed grant");

        await t.ObserveAuditAsync("grant mapping and revocation events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /scim/v2/Groups")]
    [Covers("PATCH /scim/v2/Groups/{id}")]
    [Covers("DELETE /scim/v2/Groups/{id}")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}/sync-events")]
    [Covers("POST /__probe/fga/check")]
    public async Task A_group_name_cannot_steer_a_pattern_mapping_onto_another_tenants_resource()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        await t.CreateWorkspaceResourceAsync(AcmeRoot, "Acme");
        await t.CreateWorkspaceResourceAsync("workspace::store-42", "Acme store 42", AcmeRoot);
        await t.CreateWorkspaceResourceAsync("workspace::globex", "Globex");
        await t.CreateWorkspaceResourceAsync("workspace::store-9001", "Globex store 9001", "workspace::globex");
        var connection = await t.CreateBoundedScimConnectionAsync(acme, AcmeRoot);
        var mappingId = await t.CreateScimMappingAsync(connection, new
        {
            matchType = "pattern",
            groupPattern = "^Store-(?<storeId>[^-]+)-Managers$",
            roleKey = BehaviorLockAuthorization.AdminRole,
            resourceIdTemplate = "workspace::store-{storeId}"
        });
        t.Scrub(mappingId, "scmap", "stores");
        var directory = t.NewClient("directory");
        var ann = await ScimGroupScenarios.ProvisionAsync(t, directory, connection, domain, "ann", "Ann", "Archer");
        t.Note("Acme's boundary is workspace::acme. The documented pattern mapping turns Store-{storeId}-Managers into workspace_admin on workspace::store-{storeId}; store 9001 belongs to Globex, under workspace::globex.");

        var own = t.Observe(
            await directory.PostAsync($"{Scim.Root}/Groups", Scim.Group("Store-42-Managers", "store-42-managers", ann), connection.Token),
            "a store inside Acme's boundary: the grant is created");
        var ownId = own.JsonString("id");
        t.Scrub(ownId, "grp", "store-42");
        var foreign = t.Observe(
            await directory.PostAsync($"{Scim.Root}/Groups", Scim.Group("Store-9001-Managers", "store-9001-managers", ann), connection.Token),
            "a group named for Globex's store: no grant, the group itself still syncs");
        var foreignId = foreign.JsonString("id");
        t.Scrub(foreignId, "grp", "store-9001");

        var annSubject = t.Discard(await t.Operator.GetAsync("/sqlos/admin/fga/api/users?search=Ann")).JsonString("data.0.subjectId");
        t.Scrub(annSubject, "subj", "ann");
        var probe = t.NewClient("probe");
        async Task CheckAsync(string resourceId, string caption)
            => t.Observe(await probe.PostJsonAsync("/__probe/fga/check", new { subjectId = annSubject, permissionKey = BehaviorLockAuthorization.WritePermission, resourceId }), caption);
        await CheckAsync("workspace::store-42", "Ann manages Acme's store");
        await CheckAsync("workspace::store-9001", "Ann cannot manage Globex's store");

        t.Observe(await directory.DeleteAsync($"{Scim.Root}/Groups/{foreignId}", connection.Token), "the directory deletes the foreign-named group");
        t.Observe(
            await directory.PatchAsync($"{Scim.Root}/Groups/{ownId}", Scim.Patch(("replace", "displayName", JsonValue.Create("Store-9001-Managers"))), connection.Token),
            "renaming the granted group toward Globex's store revokes the old grant and creates none");
        await CheckAsync("workspace::store-42", "Ann lost the Acme grant");
        await CheckAsync("workspace::store-9001", "and gained nothing at Globex");

        await t.ObserveStateAsync($"/sqlos/admin/auth/api/scim-connections/{connection.Id}/sync-events?pageSize=50", "dashboard: outside_boundary is recorded as a failed sync event");
        await t.ObserveAuditAsync("boundary decisions are audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /scim/v2/Groups")]
    [Covers("PATCH /scim/v2/Groups/{id}")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}/mappings")]
    public async Task A_mapping_whose_role_or_resource_does_not_exist_grants_nothing_until_it_does()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        await t.CreateWorkspaceResourceAsync(AcmeRoot, "Acme");
        await t.CreateWorkspaceResourceAsync(AcmeProjects, "Acme projects", AcmeRoot);
        var connection = await t.CreateBoundedScimConnectionAsync(acme, AcmeRoot);
        t.Scrub(await t.CreateScimMappingAsync(connection, new
        {
            matchType = "display_name",
            groupDisplayName = "Auditors",
            roleKey = "workspace_auditor",
            resourceId = AcmeProjects
        }), "scmap", "auditors");
        t.Scrub(await t.CreateScimMappingAsync(connection, new
        {
            matchType = "external_id",
            groupExternalId = "directory-archivists",
            roleKey = BehaviorLockAuthorization.ReaderRole,
            resourceId = "workspace::acme-archive"
        }), "scmap", "archivists");
        var directory = t.NewClient("directory");
        var ann = await ScimGroupScenarios.ProvisionAsync(t, directory, connection, domain, "ann", "Ann", "Archer");
        t.Note("Auditors maps to a role the FGA model does not define; Archivists maps by externalId to workspace::acme-archive, which does not exist yet. Saving both was allowed.");

        t.Observe(
            await directory.PostAsync($"{Scim.Root}/Groups", Scim.Group("Auditors", "directory-auditors", ann), connection.Token),
            "push Auditors: the role is missing");
        var archivists = t.Observe(
            await directory.PostAsync($"{Scim.Root}/Groups", Scim.Group("Archivists", "directory-archivists", ann), connection.Token),
            "push Archivists: the resource is missing");
        var archivistsId = archivists.JsonString("id");
        t.Scrub(archivistsId, "grp", "archivists");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/scim-connections/{connection.Id}/mappings", "dashboard: neither mapping holds a grant");

        await t.CreateWorkspaceResourceAsync("workspace::acme-archive", "Acme archive", AcmeRoot);
        t.Note("The host application creates workspace::acme-archive under workspace::acme.");
        t.Observe(
            await directory.PatchAsync($"{Scim.Root}/Groups/{archivistsId}", Scim.Patch(("add", "members", Scim.Members(ann))), connection.Token),
            "the next push of Archivists creates the grant");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/scim-connections/{connection.Id}/mappings", "dashboard: Archivists now holds one grant");

        await t.ObserveAuditAsync("role_missing, resource_missing, then mapped");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /scim/v2/Groups")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}/mappings")]
    public async Task Mapped_grants_fail_closed_when_the_grant_boundary_resource_is_deleted()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        await t.CreateWorkspaceResourceAsync(AcmeRoot, "Acme");
        await t.CreateWorkspaceResourceAsync(AcmeProjects, "Acme projects", AcmeRoot);
        await t.CreateWorkspaceResourceAsync("workspace::acme-temporary", "Temporary boundary", AcmeRoot);
        var connection = await t.CreateBoundedScimConnectionAsync(acme, "workspace::acme-temporary");
        t.Scrub(await t.CreateScimMappingAsync(connection, new
        {
            matchType = "pattern",
            groupPattern = "^Team-(?<team>[a-z]+)$",
            roleKey = BehaviorLockAuthorization.ReaderRole,
            resourceIdTemplate = "workspace::acme-{team}"
        }), "scmap", "teams");
        var directory = t.NewClient("directory");
        var ann = await ScimGroupScenarios.ProvisionAsync(t, directory, connection, domain, "ann", "Ann", "Archer");
        var deleted = t.Discard(await t.NewClient("probe").DeleteAsync("/__probe/fga/resources/workspace::acme-temporary"));
        EnterpriseSetup.EnsureSucceeded(deleted);
        t.Note("The boundary was workspace::acme-temporary; the host application then deleted that resource.");

        await t.ObserveStateAsync($"/sqlos/admin/auth/api/scim-connections/{connection.Id}", "dashboard: the boundary is reported as not found");
        await t.ObserveStateAsync($"/sqlos/admin/auth/api/scim-connections/{connection.Id}/mappings", "dashboard: the mapping reports the missing boundary");
        t.Observe(
            await directory.PostAsync($"{Scim.Root}/Groups", Scim.Group("Team-projects", "directory-team-projects", ann), connection.Token),
            "push Team-projects: its resource exists, but no boundary can contain it");

        await t.ObserveAuditAsync("boundary_missing");
        await t.ApproveAsync();
    }

    /// <summary>
    /// #448: SCIM keys a user's FGA subject by the user ID, the subject the FGA guides provision, and
    /// reuses it when the host provisions it too, so grants a SCIM group mapping creates reach an
    /// access check by user ID. (7.2.1 created a second subject, <c>subj_…</c> with
    /// <c>ExternalRef</c> set to the user ID, which a check by user ID never saw.)
    /// </summary>
    [Scenario]
    [Covers("POST /scim/v2/Groups")]
    [Covers("POST /__probe/fga/subjects")]
    [Covers("GET /sqlos/admin/fga/api/users")]
    [Covers("POST /__probe/fga/check")]
    public async Task A_directory_user_is_granted_through_the_subject_keyed_by_their_user_id()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        await t.CreateWorkspaceResourceAsync(AcmeRoot, "Acme");
        await t.CreateWorkspaceResourceAsync(AcmeProjects, "Acme projects", AcmeRoot);
        var connection = await t.CreateBoundedScimConnectionAsync(acme, AcmeRoot);
        t.Scrub(await t.CreateScimMappingAsync(connection, new
        {
            matchType = "display_name",
            groupDisplayName = "Engineering",
            roleKey = BehaviorLockAuthorization.ReaderRole,
            resourceId = AcmeProjects
        }), "scmap", "engineering");
        var directory = t.NewClient("directory");
        var ann = await ScimGroupScenarios.ProvisionAsync(t, directory, connection, domain, "ann", "Ann", "Archer");
        var probe = t.NewClient("probe");

        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/subjects", new { type = "user", subjectId = ann, displayName = "Ann Archer (app)", organizationId = acme.Id }),
            "the host provisions Ann's FGA subject by her user ID, as the FGA guides teach: the subject SCIM keyed by it");
        t.Observe(
            await directory.PostAsync($"{Scim.Root}/Groups", Scim.Group("Engineering", "directory-engineering", ann), connection.Token),
            "the directory pushes Engineering with Ann");
        var subjects = t.Observe(
            await t.Operator.GetAsync("/sqlos/admin/fga/api/users?search=Ann"),
            "dashboard: Ann has one FGA user subject");
        var scimSubject = subjects.JsonString("data.0.subjectId");
        t.Scrub(scimSubject, "subj", "ann-scim");

        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/check", new { subjectId = ann, permissionKey = BehaviorLockAuthorization.ReadPermission, resourceId = AcmeProjects }),
            "a check by Ann's user ID sees the mapped grant");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/check", new { subjectId = scimSubject, permissionKey = BehaviorLockAuthorization.ReadPermission, resourceId = AcmeProjects }),
            "as does a check by the subject the dashboard lists, the same one");

        await t.ObserveAuditAsync("group and grant events");
        await t.ApproveAsync();
    }
}

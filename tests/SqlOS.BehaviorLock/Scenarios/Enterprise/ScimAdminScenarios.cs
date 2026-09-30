using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// The SCIM administration API the dashboard uses: connections (one-time token reveal, rotation,
/// enablement, grant boundary), group mappings, and sync events, with every validation failure
/// the admin service distinguishes.
/// </summary>
[TestClass]
public sealed class ScimAdminScenarios
{
    private const string AcmeRoot = "workspace::acme";
    private const string AcmeProjects = "workspace::acme-projects";

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/scim-connections")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/scim-connections")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}")]
    [Covers("PUT /sqlos/admin/auth/api/scim-connections/{connectionId}")]
    [Covers("POST /sqlos/admin/auth/api/scim-connections/{connectionId}/token/rotate")]
    [Covers("POST /sqlos/admin/auth/api/scim-connections/{connectionId}/disable")]
    [Covers("POST /sqlos/admin/auth/api/scim-connections/{connectionId}/enable")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}/sync-events")]
    [Covers("GET /scim/v2/ServiceProviderConfig")]
    public async Task An_operator_creates_rotates_renames_and_toggles_a_scim_connection()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        await t.CreateWorkspaceResourceAsync(AcmeRoot, "Acme");
        var directory = t.NewClient("directory");
        var connections = $"/sqlos/admin/auth/api/organizations/{acme.Id}/scim-connections";

        var created = t.Observe(
            await t.Operator.PostJsonAsync(connections, new { displayName = "Acme Entra", enabled = true, grantBoundaryResourceId = AcmeRoot }),
            "create a connection: the bearer token is shown once, never cached");
        var id = created.JsonString("connectionId");
        var firstToken = created.JsonString("token");
        t.ScrubScimToken(firstToken);
        var connection = $"/sqlos/admin/auth/api/scim-connections/{id}";
        t.Observe(await directory.GetAsync($"{Scim.Root}/ServiceProviderConfig", firstToken), "the directory connects with it");

        t.Observe(await t.Operator.GetAsync(connections), "list the organization's connections");
        t.Observe(await t.Operator.GetAsync(connection), "read the connection: no token, only its prefix");
        var rotated = t.Observe(await t.Operator.PostJsonAsync($"{connection}/token/rotate", new { }), "rotate the token");
        var secondToken = rotated.JsonString("token");
        t.ScrubScimToken(secondToken);
        t.Observe(await directory.GetAsync($"{Scim.Root}/ServiceProviderConfig", firstToken), "the old token stops working at once");
        t.Observe(await directory.GetAsync($"{Scim.Root}/ServiceProviderConfig", secondToken), "the new one works");

        t.Observe(await t.Operator.PutJsonAsync(connection, new { displayName = "Acme Entra ID", enabled = true }), "rename it; omitting the boundary keeps it");
        t.Observe(await t.Operator.PutJsonAsync(connection, new { displayName = "Acme Entra ID", enabled = false }), "disable it through the edit form");
        t.Observe(await directory.GetAsync($"{Scim.Root}/ServiceProviderConfig", secondToken), "a disabled connection refuses its token");
        t.Observe(await t.Operator.PostJsonAsync($"{connection}/enable", new { }), "enable it");
        t.Observe(await t.Operator.PostJsonAsync($"{connection}/enable", new { }), "enabling again changes nothing but is audited again");
        t.Observe(await t.Operator.PostJsonAsync($"{connection}/disable", new { }), "disable it with the emergency control");
        t.Observe(await t.Operator.GetAsync($"{connection}/sync-events"), "no directory writes yet, so no sync events");
        t.Observe(await t.Operator.GetAsync(connections), "the list reflects the disabled connection");

        await t.ObserveAuditAsync("connection administration events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/organizations/{organizationId}/scim-connections")]
    [Covers("GET /sqlos/admin/auth/api/organizations/{organizationId}/scim-connections")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}")]
    [Covers("PUT /sqlos/admin/auth/api/scim-connections/{connectionId}")]
    [Covers("POST /sqlos/admin/auth/api/scim-connections/{connectionId}/token/rotate")]
    [Covers("POST /sqlos/admin/auth/api/scim-connections/{connectionId}/enable")]
    [Covers("POST /sqlos/admin/auth/api/scim-connections/{connectionId}/disable")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}/mappings")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}/sync-events")]
    public async Task Scim_connection_administration_refuses_invalid_requests()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        await t.CreateWorkspaceResourceAsync(AcmeRoot, "Acme");
        var live = await t.CreateDirectoryAsync(acme, "Live directory");
        var connections = $"/sqlos/admin/auth/api/organizations/{acme.Id}/scim-connections";
        const string Unknown = "/sqlos/admin/auth/api/scim-connections/scim_ffffffffffffffffffffffffffffffff";

        t.Observe(await t.Operator.PostJsonAsync("/sqlos/admin/auth/api/organizations/org_ffffffffffffffffffffffffffffffff/scim-connections", new { displayName = "Nowhere", enabled = false }), "an unknown organization");
        t.Observe(await t.Operator.PostJsonAsync(connections, new { displayName = " ", enabled = false }), "a blank display name");
        t.Observe(await t.Operator.PostJsonAsync(connections, new { displayName = "Second directory", enabled = true }), "a second enabled connection for the same organization");
        t.Observe(await t.Operator.PostJsonAsync(connections, new { displayName = "Bounded", enabled = false, grantBoundaryResourceId = "workspace::nowhere" }), "a grant boundary resource that does not exist");
        t.Observe(await t.Operator.PostJsonAsync(connections, new { displayName = "Bounded", enabled = false, grantBoundaryResourceId = "workspace::" + new string('x', 260) }), "a grant boundary ID longer than 256 characters");
        var staged = t.Observe(await t.Operator.PostJsonAsync(connections, new { displayName = "Staged directory", enabled = false }), "a disabled second connection is allowed");
        var stagedId = staged.JsonString("connectionId");
        t.ScrubScimToken(staged.JsonString("token"));
        t.Observe(await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/scim-connections/{stagedId}/enable", new { }), "enabling it while the first is enabled");
        t.Observe(await t.Operator.PutJsonAsync($"/sqlos/admin/auth/api/scim-connections/{stagedId}", new { displayName = "Staged directory", enabled = true }), "enabling it through the edit form");
        t.Observe(await t.Operator.PutJsonAsync($"/sqlos/admin/auth/api/scim-connections/{live.Id}", new { displayName = "", enabled = true }), "renaming to a blank display name");
        t.Observe(await t.Operator.PutJsonAsync($"/sqlos/admin/auth/api/scim-connections/{live.Id}", new { displayName = "Live directory", enabled = true, grantBoundaryResourceId = "workspace::nowhere" }), "moving the boundary to a resource that does not exist");

        t.Observe(await t.Operator.GetAsync(Unknown), "reading an unknown connection");
        t.Observe(await t.Operator.PutJsonAsync(Unknown, new { displayName = "Nobody", enabled = true }), "editing an unknown connection");
        t.Observe(await t.Operator.PostJsonAsync($"{Unknown}/token/rotate", new { }), "rotating an unknown connection's token");
        t.Observe(await t.Operator.PostJsonAsync($"{Unknown}/enable", new { }), "enabling an unknown connection");
        t.Observe(await t.Operator.PostJsonAsync($"{Unknown}/disable", new { }), "disabling an unknown connection");
        t.Observe(await t.Operator.GetAsync($"{Unknown}/mappings"), "an unknown connection has an empty mapping list");
        t.Observe(await t.Operator.GetAsync($"{Unknown}/sync-events"), "and an empty sync-event list");

        t.Observe(await t.Operator.GetAsync($"{connections}?pageSize=1"), "a one-connection page with a next cursor");
        t.Observe(await t.Operator.GetAsync($"{connections}?page=2"), "offset pagination is refused");
        t.Observe(await t.Operator.GetAsync($"{connections}?cursor=not-a-cursor"), "a cursor SqlOS did not issue");
        t.Observe(await t.Operator.GetAsync($"{connections}", options => options.WithoutCredentials()), "without the operator credential the API does not exist");
        t.Observe(await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/scim-connections/{live.Id}/token/rotate", new { }, options => options.WithoutCredentials()), "rotation without the operator credential");

        await t.ObserveAuditAsync("only the staged connection was created");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/scim-connections/{connectionId}/mappings")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}/mappings")]
    [Covers("PUT /sqlos/admin/auth/api/scim-mappings/{mappingId}")]
    [Covers("POST /sqlos/admin/auth/api/scim-mappings/{mappingId}/disable")]
    [Covers("POST /sqlos/admin/auth/api/scim-mappings/{mappingId}/enable")]
    [Covers("POST /scim/v2/Groups")]
    [Covers("PATCH /scim/v2/Groups/{id}")]
    public async Task An_operator_maps_directory_groups_to_roles_and_toggles_the_mappings()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        await t.CreateWorkspaceResourceAsync(AcmeRoot, "Acme");
        await t.CreateWorkspaceResourceAsync(AcmeProjects, "Acme projects", AcmeRoot);
        var connection = await t.CreateBoundedScimConnectionAsync(acme, AcmeRoot);
        var directory = t.NewClient("directory");
        var ann = await ScimGroupScenarios.ProvisionAsync(t, directory, connection, domain, "ann", "Ann", "Archer");
        var mappings = $"/sqlos/admin/auth/api/scim-connections/{connection.Id}/mappings";

        var byName = t.Observe(
            await t.Operator.PostJsonAsync(mappings, new
            {
                matchType = "display_name",
                groupDisplayName = "Engineering",
                roleKey = BehaviorLockAuthorization.ReaderRole,
                resourceId = AcmeProjects,
                description = "Engineering reads projects"
            }),
            "map the Engineering group to workspace_reader on the projects workspace");
        var byNameId = byName.JsonString("id");
        t.Scrub(byNameId, "scmap", "engineering");
        t.Scrub(t.Observe(
            await t.Operator.PostJsonAsync(mappings, new
            {
                matchType = "externalId",
                groupExternalId = "directory-admins",
                roleKey = BehaviorLockAuthorization.AdminRole,
                resourceId = AcmeRoot
            }),
            "map a group by externalId (the legacy spelling of the match type)").JsonString("id"), "scmap", "admins");
        t.Scrub(t.Observe(
            await t.Operator.PostJsonAsync(mappings, new
            {
                matchType = "regex",
                groupPattern = "^Team-(?<team>[a-z]+)$",
                roleKey = BehaviorLockAuthorization.ReaderRole,
                resourceIdTemplate = "workspace::acme-{team}"
            }),
            "map a pattern whose template is checked when a group is pushed").JsonString("id"), "scmap", "teams");
        t.Observe(await t.Operator.GetAsync(mappings), "list mappings, newest first, with each one's boundary status");

        var group = t.Observe(
            await directory.PostAsync($"{Scim.Root}/Groups", Scim.Group("Engineering", "directory-engineering", ann), connection.Token),
            "the directory pushes Engineering: the mapping grants");
        var groupId = group.JsonString("id");
        t.Scrub(groupId, "grp", "engineering");
        t.Observe(
            await t.Operator.PutJsonAsync($"/sqlos/admin/auth/api/scim-mappings/{byNameId}", new
            {
                matchType = "display_name",
                groupDisplayName = "Engineering",
                roleKey = BehaviorLockAuthorization.AdminRole,
                resourceId = AcmeProjects,
                description = "Engineering administers projects",
                enabled = true
            }),
            "change the mapped role: the existing grant is revoked");
        t.Observe(
            await directory.PatchAsync($"{Scim.Root}/Groups/{groupId}", Scim.Patch(("replace", "externalId", JsonValue.Create("directory-engineering-2"))), connection.Token),
            "the next push grants the new role");
        t.Observe(await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/scim-mappings/{byNameId}/disable", new { }), "disable the mapping: its grant is revoked");
        t.Observe(await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/scim-mappings/{byNameId}/enable", new { }), "enable it again: grants return on the next push");
        t.Observe(await t.Operator.GetAsync(mappings), "the mapping list after the changes");

        await t.ObserveAuditAsync("mapping and grant events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/auth/api/scim-connections/{connectionId}/mappings")]
    [Covers("PUT /sqlos/admin/auth/api/scim-mappings/{mappingId}")]
    [Covers("POST /sqlos/admin/auth/api/scim-mappings/{mappingId}/enable")]
    [Covers("POST /sqlos/admin/auth/api/scim-mappings/{mappingId}/disable")]
    public async Task Group_mapping_administration_refuses_mappings_that_could_escape_the_boundary_or_are_malformed()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var globex = await t.Setup.CreateOrganizationAsync("globex");
        await t.CreateWorkspaceResourceAsync(AcmeRoot, "Acme");
        await t.CreateWorkspaceResourceAsync(AcmeProjects, "Acme projects", AcmeRoot);
        await t.CreateWorkspaceResourceAsync("workspace::globex", "Globex");
        var bounded = await t.CreateBoundedScimConnectionAsync(acme, AcmeRoot, "Acme directory");
        var unbounded = await t.CreateDirectoryAsync(globex, "Globex directory");
        var mappings = $"/sqlos/admin/auth/api/scim-connections/{bounded.Id}/mappings";
        object Mapping(string matchType = "display_name", string? displayName = "Engineering", string? externalId = null, string? pattern = null, string? roleKey = BehaviorLockAuthorization.ReaderRole, string? resourceId = AcmeProjects, string? template = null, bool enabled = true)
            => new { matchType, groupDisplayName = displayName, groupExternalId = externalId, groupPattern = pattern, roleKey, resourceId, resourceIdTemplate = template, enabled };

        t.Observe(await t.Operator.PostJsonAsync(mappings, Mapping(roleKey: " ")), "no role key");
        t.Observe(await t.Operator.PostJsonAsync(mappings, Mapping(resourceId: null)), "neither a resource ID nor a template");
        t.Observe(await t.Operator.PostJsonAsync(mappings, Mapping(matchType: "prefix")), "an unknown match type");
        t.Observe(await t.Operator.PostJsonAsync(mappings, Mapping(displayName: null)), "a display-name mapping without a display name");
        t.Observe(await t.Operator.PostJsonAsync(mappings, Mapping(matchType: "external_id", displayName: null)), "an externalId mapping without an externalId");
        t.Observe(await t.Operator.PostJsonAsync(mappings, Mapping(matchType: "pattern", displayName: null)), "a pattern mapping without a pattern");
        t.Observe(await t.Operator.PostJsonAsync(mappings, Mapping(matchType: "pattern", displayName: null, pattern: "^Team-(?<team>[a-z+$")), "a pattern that is not a valid regular expression");
        t.Observe(await t.Operator.PostJsonAsync(mappings, Mapping(resourceId: "workspace::globex")), "a fixed resource outside the boundary");
        t.Observe(await t.Operator.PostJsonAsync(mappings, Mapping(resourceId: null, template: "workspace::globex")), "a template with no captures is a fixed resource, checked the same way");
        t.Observe(await t.Operator.PostJsonAsync(mappings, Mapping(resourceId: "workspace::acme-later")), "a fixed resource that does not exist yet is accepted");
        t.Observe(await t.Operator.PostJsonAsync("/sqlos/admin/auth/api/scim-connections/scim_ffffffffffffffffffffffffffffffff/mappings", Mapping()), "an unknown connection");

        var globexMappings = $"/sqlos/admin/auth/api/scim-connections/{unbounded.Id}/mappings";
        t.Observe(await t.Operator.PostJsonAsync(globexMappings, Mapping(resourceId: "workspace::globex")), "an enabled mapping on a connection without a boundary");
        var staged = t.Observe(await t.Operator.PostJsonAsync(globexMappings, Mapping(resourceId: "workspace::globex", enabled: false)), "a disabled mapping may be staged without a boundary");
        var stagedId = staged.JsonString("id");
        t.Scrub(stagedId, "scmap", "staged");
        t.Observe(await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/scim-mappings/{stagedId}/enable", new { }), "enabling it needs the boundary");
        t.Observe(await t.Operator.PutJsonAsync($"/sqlos/admin/auth/api/scim-mappings/{stagedId}", Mapping(resourceId: "workspace::globex", enabled: true)), "so does enabling it through an edit");
        t.Observe(await t.Operator.PutJsonAsync($"/sqlos/admin/auth/api/scim-mappings/{stagedId}", Mapping(resourceId: null, enabled: false)), "an edit still validates the shape");
        t.Observe(await t.Operator.PostJsonAsync($"/sqlos/admin/auth/api/scim-mappings/{stagedId}/disable", new { }), "disabling a disabled mapping");

        const string UnknownMapping = "/sqlos/admin/auth/api/scim-mappings/scmap_ffffffffffffffffffffffffffffffff";
        t.Observe(await t.Operator.PutJsonAsync(UnknownMapping, Mapping()), "editing an unknown mapping");
        t.Observe(await t.Operator.PostJsonAsync($"{UnknownMapping}/enable", new { }), "enabling an unknown mapping");
        t.Observe(await t.Operator.PostJsonAsync($"{UnknownMapping}/disable", new { }), "disabling an unknown mapping");

        await t.ObserveAuditAsync("only the accepted mappings were recorded");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("PUT /sqlos/admin/auth/api/scim-connections/{connectionId}")]
    [Covers("POST /sqlos/admin/auth/api/scim-connections/{connectionId}/disable")]
    [Covers("GET /sqlos/admin/auth/api/scim-connections/{connectionId}/mappings")]
    public async Task Moving_or_disabling_a_connection_revokes_the_grants_its_mappings_made()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        await t.CreateWorkspaceResourceAsync(AcmeRoot, "Acme");
        await t.CreateWorkspaceResourceAsync(AcmeProjects, "Acme projects", AcmeRoot);
        await t.CreateWorkspaceResourceAsync("workspace::acme-finance", "Acme finance", AcmeRoot);
        var connection = await t.CreateBoundedScimConnectionAsync(acme, AcmeRoot);
        t.Scrub(await t.CreateScimMappingAsync(connection, new
        {
            matchType = "pattern",
            groupPattern = "^Team-(?<team>[a-z]+)$",
            roleKey = BehaviorLockAuthorization.ReaderRole,
            resourceIdTemplate = "workspace::acme-{team}"
        }), "scmap", "teams");
        var directory = t.NewClient("directory");
        var ann = await ScimGroupScenarios.ProvisionAsync(t, directory, connection, domain, "ann", "Ann", "Archer");
        t.Discard(await directory.PostAsync($"{Scim.Root}/Groups", Scim.Group("Team-projects", "directory-team-projects", ann), connection.Token));
        t.Discard(await directory.PostAsync($"{Scim.Root}/Groups", Scim.Group("Team-finance", "directory-team-finance", ann), connection.Token));
        await t.SkipAuditAsync();
        t.Note("Team-projects and Team-finance were pushed; each holds a workspace_reader grant under workspace::acme.");
        var connectionPath = $"/sqlos/admin/auth/api/scim-connections/{connection.Id}";

        await t.ObserveStateAsync($"{connectionPath}/mappings", "dashboard: two active grants");
        t.Observe(
            await t.Operator.PutJsonAsync(connectionPath, new { displayName = "Directory", enabled = true, grantBoundaryResourceId = AcmeProjects }),
            "narrow the boundary to the projects workspace: the finance grant falls outside and is revoked");
        await t.ObserveStateAsync($"{connectionPath}/mappings", "dashboard: one active grant");
        t.Observe(await t.Operator.PostJsonAsync($"{connectionPath}/disable", new { }), "disable the connection: every managed grant is revoked");
        await t.ObserveStateAsync($"{connectionPath}/mappings", "dashboard: no active grants");

        await t.ObserveAuditAsync("boundary change and revocation events");
        await t.ApproveAsync();
    }
}

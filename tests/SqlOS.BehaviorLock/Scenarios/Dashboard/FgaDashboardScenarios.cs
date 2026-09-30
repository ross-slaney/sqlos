using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;
using SqlOS.BehaviorLock.Scenarios.Probes;

namespace SqlOS.BehaviorLock.Scenarios.Dashboard;

/// <summary>
/// The FGA dashboard API (<c>/sqlos/admin/fga/api/*</c>, string-routed by
/// <c>SqlOSFgaDashboardMiddleware</c>): model reads, the rejected schema writes, resource and
/// subject browsing with cursor pagination, grant writes, and the access trace.
/// </summary>
[TestClass]
public sealed class FgaDashboardScenarios
{
    private const string Api = "/sqlos/admin/fga/api";

    [Scenario]
    [Covers("GET /sqlos/admin/fga/api/stats")]
    [Covers("GET /sqlos/admin/fga/api/roles")]
    [Covers("GET /sqlos/admin/fga/api/roles/{roleId}")]
    [Covers("GET /sqlos/admin/fga/api/roles/{roleId}/permissions")]
    [Covers("GET /sqlos/admin/fga/api/permissions")]
    [Covers("GET /sqlos/admin/fga/api/resource-types")]
    public async Task The_operator_reads_the_seeded_authorization_model()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var op = t.Operator;

        t.Observe(await op.GetAsync($"{Api}/stats"), "summary counts of the seeded model");
        t.Observe(await op.GetAsync($"{Api}/resource-types"), "resource types, by name");
        t.Observe(await op.GetAsync($"{Api}/roles"), "roles, by name");
        t.Observe(await op.GetAsync($"{Api}/roles?search=reader"), "roles filtered by key or name");
        t.Observe(await op.GetAsync($"{Api}/roles/{BehaviorLockAuthorization.AdminRole}"), "a role's detail");
        t.Observe(await op.GetAsync($"{Api}/roles/no-such-role"), "an unknown role is 404");
        t.Observe(await op.GetAsync($"{Api}/roles/{BehaviorLockAuthorization.AdminRole}/permissions"), "the permissions a role grants");
        t.Observe(await op.GetAsync($"{Api}/roles/no-such-role/permissions"), "an unknown role has no permissions, not a 404");
        t.Observe(await op.GetAsync($"{Api}/permissions"), "permissions, by name");
        t.Observe(await op.GetAsync($"{Api}/permissions?search=write"), "permissions filtered by key or name");
        t.Observe(await op.GetAsync($"{Api}/no-such-endpoint"), "an unknown API path is a JSON 404");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/fga/api/roles")]
    [Covers("GET /sqlos/admin/fga/api/permissions")]
    public async Task List_cursors_are_bound_to_their_list_and_filters()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var op = t.Operator;

        var first = t.Observe(await op.GetAsync($"{Api}/roles?pageSize=1"), "the first page of one role carries a cursor");
        var cursor = first.JsonString("nextCursor");
        t.Observe(await op.GetAsync($"{Api}/roles?pageSize=1&cursor={Uri.EscapeDataString(cursor)}"), "the cursor reads the next page");
        t.Observe(
            await op.GetAsync($"{Api}/roles?pageSize=1&search=Workspace&cursor={Uri.EscapeDataString(cursor)}"),
            "the cursor is refused under a different filter");
        t.Observe(
            await op.GetAsync($"{Api}/permissions?pageSize=1&cursor={Uri.EscapeDataString(cursor)}"),
            "and on a different list");
        t.Observe(await op.GetAsync($"{Api}/roles?cursor=not-a-cursor"), "a malformed cursor is refused");
        t.Observe(await op.GetAsync($"{Api}/roles?page=2"), "offset pagination beyond the first page is refused");
        t.Observe(await op.GetAsync($"{Api}/roles?page=1&pageSize=500"), "page=1 is accepted and the page size is capped");
        t.Observe(await op.GetAsync($"{Api}/roles?pageSize=0"), "and raised to at least one");

        await t.ApproveAsync();
    }

    /// <summary>
    /// Roles, permissions, and role permissions are schema: SqlOS seeds them from
    /// <c>options.Fga.Seed</c> and the dashboard refuses to change them.
    /// </summary>
    [Scenario]
    [Covers("POST /sqlos/admin/fga/api/roles")]
    [Covers("PUT /sqlos/admin/fga/api/roles/{roleId}")]
    [Covers("DELETE /sqlos/admin/fga/api/roles/{roleId}")]
    [Covers("POST /sqlos/admin/fga/api/roles/{roleId}/permissions")]
    [Covers("DELETE /sqlos/admin/fga/api/roles/{roleId}/permissions/{permissionId}")]
    [Covers("POST /sqlos/admin/fga/api/permissions")]
    [Covers("GET /sqlos/admin/fga/api/roles/{roleId}")]
    public async Task Schema_writes_are_refused_because_the_model_is_seeded_in_code()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var op = t.Operator;
        var reader = BehaviorLockAuthorization.ReaderRole;

        t.Observe(await op.PostJsonAsync($"{Api}/roles", new { key = "auditor", name = "Auditor" }), "create a role");
        t.Observe(await op.PutJsonAsync($"{Api}/roles/{reader}", new { name = "Renamed" }), "rename a role");
        t.Observe(await op.DeleteAsync($"{Api}/roles/{reader}"), "delete a role");
        t.Observe(
            await op.PostJsonAsync($"{Api}/roles/{reader}/permissions", new { permissionId = BehaviorLockAuthorization.WritePermission }),
            "add a permission to a role");
        t.Observe(
            await op.DeleteAsync($"{Api}/roles/{reader}/permissions/{BehaviorLockAuthorization.ReadPermission}"),
            "remove a permission from a role");
        t.Observe(await op.PostJsonAsync($"{Api}/permissions", new { key = "workspace.delete", name = "Delete workspace" }), "create a permission");
        t.Observe(await op.GetAsync($"{Api}/roles/{reader}"), "the role is unchanged");

        await t.ObserveAuditAsync("refused schema writes are not audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/fga/api/stats")]
    [Covers("GET /sqlos/admin/fga/api/resources/tree")]
    [Covers("GET /sqlos/admin/fga/api/resources")]
    [Covers("GET /sqlos/admin/fga/api/resources/{resourceId}")]
    [Covers("GET /sqlos/admin/fga/api/resources/{resourceId}/children")]
    [Covers("GET /sqlos/admin/fga/api/resources/{resourceId}/grants")]
    [Covers("GET /sqlos/admin/fga/api/resources/{resourceId}/access")]
    public async Task The_operator_browses_the_resource_tree_and_its_access()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var fga = new FgaFixture(t);
        await fga.UserSubjectAsync(alice);
        var alpha = await fga.WorkspaceAsync("alpha", "Alpha");
        var docs = await fga.WorkspaceAsync("docs", "Docs", alpha);
        await fga.WorkspaceAsync("beta", "Beta");
        var ledger = await fga.RootChildAsync("workspace::ledger", "Ledger");
        await fga.GrantAsync(alice.Id, alpha, BehaviorLockAuthorization.ReaderRole);
        var op = t.Operator;

        t.Observe(await op.GetAsync($"{Api}/stats"), "counts after the setup");
        t.Observe(await op.GetAsync($"{Api}/resources/tree"), "top-level resources with child and grant counts");
        t.Observe(await op.GetAsync($"{Api}/resources/tree?search=Al"), "top-level resources filtered by name");
        t.Observe(await op.GetAsync($"{Api}/resources?search=Doc"), "the flat list finds resources at any depth");
        t.Observe(await op.GetAsync($"{Api}/resources?search=workspace::l"), "the flat list also matches resource IDs");
        var page = t.Observe(await op.GetAsync($"{Api}/resources?pageSize=2"), "the flat list pages by name");
        t.Observe(
            await op.GetAsync($"{Api}/resources?pageSize=2&cursor={Uri.EscapeDataString(page.JsonString("nextCursor"))}"),
            "the next page");
        t.Observe(await op.GetAsync($"{Api}/resources/{docs}"), "a nested resource's detail and breadcrumbs");
        t.Observe(await op.GetAsync($"{Api}/resources/{ledger}"), "a resource under the root resource");
        t.Observe(await op.GetAsync($"{Api}/resources/root/children"), "the root resource's children");
        t.Observe(await op.PostJsonAsync($"{Api}/resources/root/children", new { }), "the children route answers any method");
        t.Observe(await op.GetAsync($"{Api}/resources/{alpha}/children"), "a workspace's children");
        t.Observe(await op.GetAsync($"{Api}/resources/{alpha}/children?search=Nothing"), "children filtered by name");
        t.Observe(await op.GetAsync($"{Api}/resources/{alpha}/grants"), "the grants made directly on a resource");
        t.Observe(await op.GetAsync($"{Api}/resources/{alpha}/access"), "access on a resource with a direct grant");
        t.Observe(await op.GetAsync($"{Api}/resources/{docs}/access"), "access inherited from the parent");
        t.Observe(await op.GetAsync($"{Api}/resources/workspace::missing"), "an unknown resource is 404");
        t.Observe(await op.GetAsync($"{Api}/resources/workspace::missing/access"), "so is its access");
        t.Observe(await op.GetAsync($"{Api}/resources/workspace::missing/children"), "but its children are an empty page");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/fga/api/subjects")]
    [Covers("GET /sqlos/admin/fga/api/subjects/{subjectId}")]
    [Covers("GET /sqlos/admin/fga/api/subjects/{subjectId}/grants")]
    [Covers("GET /sqlos/admin/fga/api/users")]
    [Covers("GET /sqlos/admin/fga/api/agents")]
    [Covers("GET /sqlos/admin/fga/api/service-accounts")]
    [Covers("GET /sqlos/admin/fga/api/user-groups")]
    [Covers("GET /sqlos/admin/fga/api/grants")]
    public async Task The_operator_browses_subjects_and_grants()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var fga = new FgaFixture(t);
        await fga.UserSubjectAsync(alice);
        await fga.AgentSubjectAsync("agent-indexer", "Indexer");
        await fga.ServiceAccountSubjectAsync("sa-reporter", "Reporter", "reporter-client");
        var alpha = await fga.WorkspaceAsync("alpha", "Alpha");
        var beta = await fga.WorkspaceAsync("beta", "Beta");
        await fga.GrantAsync(alice.Id, alpha, BehaviorLockAuthorization.ReaderRole);
        await fga.GrantAsync("agent-indexer", beta, BehaviorLockAuthorization.AdminRole);
        var op = t.Operator;

        t.Observe(await op.GetAsync($"{Api}/subjects"), "every subject, by display name");
        t.Observe(await op.GetAsync($"{Api}/subjects?type=agent"), "subjects of one type");
        t.Observe(await op.GetAsync($"{Api}/subjects?search=Rep"), "subjects matched by name");
        t.Observe(await op.GetAsync($"{Api}/subjects/{alice.Id}"), "a subject's detail, groups, and members");
        t.Observe(await op.GetAsync($"{Api}/subjects/{alice.Id}/grants"), "a subject's grants");
        t.Observe(await op.GetAsync($"{Api}/subjects/no-such-subject"), "an unknown subject is 404");
        t.Observe(await op.DeleteAsync($"{Api}/subjects/{alice.Id}"), "DELETE on a subject answers its detail and deletes nothing");
        t.Observe(await op.GetAsync($"{Api}/subjects?search=Alice"), "the subject is still there");
        t.Observe(await op.GetAsync($"{Api}/subjects/no-such-subject/grants"), "but its grants are an empty page");
        t.Observe(await op.GetAsync($"{Api}/users"), "user subjects");
        t.Observe(await op.GetAsync($"{Api}/users?search={Uri.EscapeDataString(alice.Email)}"), "user subjects matched by email");
        t.Observe(await op.GetAsync($"{Api}/agents"), "agent subjects");
        t.Observe(await op.GetAsync($"{Api}/service-accounts"), "service-account subjects");
        t.Observe(await op.GetAsync($"{Api}/user-groups"), "no user groups");
        t.Observe(await op.GetAsync($"{Api}/grants"), "every grant, newest first");
        t.Observe(await op.GetAsync($"{Api}/grants?search=Beta"), "grants matched by resource, subject, or role name");

        await t.ApproveAsync();
    }

    /// <summary>
    /// The dashboard writes grants directly: it validates that the subject, role, and resource
    /// exist, but not that the role applies to the resource's type, and it creates a new grant
    /// (with its own random ID) for an identical request instead of reusing the existing one.
    /// Grant changes write no audit events.
    /// </summary>
    [Scenario]
    [Covers("POST /sqlos/admin/fga/api/grants")]
    [Covers("DELETE /sqlos/admin/fga/api/grants/{grantId}")]
    [Covers("GET /sqlos/admin/fga/api/grants")]
    [Covers("POST /__probe/fga/check")]
    [Covers("POST /__probe/fga/allows")]
    public async Task The_operator_grants_and_revokes_roles_and_checks_follow()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var fga = new FgaFixture(t);
        await fga.UserSubjectAsync(alice);
        var alpha = await fga.WorkspaceAsync("alpha", "Alpha");
        var ledger = await fga.RootChildAsync("workspace::ledger", "Ledger");
        var op = t.Operator;
        var probe = t.NewClient("probe");
        var read = BehaviorLockAuthorization.ReadPermission;

        t.Observe(await op.PostJsonAsync($"{Api}/grants", new { }), "subject, role, and resource are required");
        await ProbeCalls.ObserveOrUnhandledAsync(
            t,
            () => op.PostJsonAsync($"{Api}/grants", "{\"subjectId\":"),
            "a body that is not JSON");
        await ProbeCalls.ObserveOrUnhandledAsync(
            t,
            () => op.PostJsonAsync($"{Api}/grants", new { subjectId = alice.Id, roleId = BehaviorLockAuthorization.ReaderRole, resourceId = alpha, effectiveFrom = "next tuesday" }),
            "a window that is not a date");
        t.Observe(
            await op.PostJsonAsync($"{Api}/grants", new { subjectId = "no-such-subject", roleId = BehaviorLockAuthorization.ReaderRole, resourceId = alpha }),
            "an unknown subject is refused");
        t.Observe(
            await op.PostJsonAsync($"{Api}/grants", new { subjectId = alice.Id, roleId = "no-such-role", resourceId = alpha }),
            "an unknown role is refused");
        t.Observe(
            await op.PostJsonAsync($"{Api}/grants", new { subjectId = alice.Id, roleId = BehaviorLockAuthorization.ReaderRole, resourceId = "workspace::missing" }),
            "an unknown resource is refused");
        t.Observe(
            await op.PostJsonAsync($"{Api}/grants", new { subjectId = alice.Id, roleId = BehaviorLockAuthorization.ReaderRole, resourceId = BehaviorLockAuthorization.ReaderRole }),
            "the role key is not accepted as a resource ID");

        var first = t.Observe(
            await op.PostJsonAsync($"{Api}/grants", new { subjectId = alice.Id, roleId = BehaviorLockAuthorization.ReaderRole, resourceId = alpha }),
            "grant the reader role on Alpha");
        var second = t.Observe(
            await op.PostJsonAsync($"{Api}/grants", new { subjectId = alice.Id, roleId = BehaviorLockAuthorization.ReaderRole, resourceId = alpha }),
            "the identical request creates a second grant");
        t.Observe(await op.GetAsync($"{Api}/grants"), "both grants are listed");
        // With two identical grants, which one a check's trace names depends on the order the
        // database returns them in (no ORDER BY), so this step records only the decision.
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/allows", new { subjectId = alice.Id, permissionKey = read, resourceId = alpha }),
            "read is allowed on Alpha");

        t.Observe(await op.DeleteAsync($"{Api}/grants/{first.JsonString("id")}"), "delete the first grant");
        t.Observe(await op.DeleteAsync($"{Api}/grants/{first.JsonString("id")}"), "deleting it again is 404");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/check", new { subjectId = alice.Id, permissionKey = read, resourceId = alpha }),
            "the duplicate still allows read");
        t.Observe(await op.DeleteAsync($"{Api}/grants/{second.JsonString("id")}"), "delete the duplicate");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/check", new { subjectId = alice.Id, permissionKey = read, resourceId = alpha }),
            "now read is denied");

        t.Observe(
            await op.PostJsonAsync($"{Api}/grants", new { subjectId = alice.Id, roleId = BehaviorLockAuthorization.ReaderRole, resourceId = "root" }),
            "a workspace role can be granted on the root resource");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/check", new { subjectId = alice.Id, permissionKey = read, resourceId = "root" }),
            "the root is not a workspace, so the permission does not apply there");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/check", new { subjectId = alice.Id, permissionKey = read, resourceId = ledger }),
            "but a workspace under the root inherits the grant");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/check", new { subjectId = alice.Id, permissionKey = read, resourceId = alpha }),
            "a top-level workspace is not under the root resource and does not");

        await t.ObserveAuditAsync("grant writes are not audited");
        await t.ApproveAsync();
    }

    /// <summary>
    /// Known defect #325 (7.2.1): timestamps SqlOS reads back through EF have no UTC marker. A grant
    /// window sent in UTC with a trailing <c>Z</c> is stored correctly and enforced correctly, but
    /// the dashboard returns it (and <c>createdAt</c>) without the <c>Z</c>, so browsers read it as
    /// local time. The fix changes the <c>{datetime:unspecified}</c> placeholders to <c>{datetime:utc-z}</c>.
    /// </summary>
    [Scenario]
    [Covers("POST /sqlos/admin/fga/api/grants")]
    [Covers("GET /sqlos/admin/fga/api/resources/{resourceId}/grants")]
    [Covers("GET /sqlos/admin/fga/api/subjects/{subjectId}/grants")]
    [Covers("POST /__probe/fga/check")]
    public async Task Grant_windows_round_trip_without_a_utc_marker_CurrentBehavior_KnownDefect_325()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var fga = new FgaFixture(t);
        await fga.UserSubjectAsync(alice);
        var alpha = await fga.WorkspaceAsync("alpha", "Alpha");
        var beta = await fga.WorkspaceAsync("beta", "Beta");
        var gamma = await fga.WorkspaceAsync("gamma", "Gamma");
        var op = t.Operator;
        var probe = t.NewClient("probe");
        var read = BehaviorLockAuthorization.ReadPermission;
        var reader = BehaviorLockAuthorization.ReaderRole;

        t.Observe(
            await op.PostJsonAsync($"{Api}/grants", new { subjectId = alice.Id, roleId = reader, resourceId = alpha, effectiveFrom = "2000-01-01T00:00:00Z", effectiveTo = "2099-12-31T23:59:59Z" }),
            "a grant whose UTC window is open now comes back without the UTC marker");
        t.Observe(
            await op.PostJsonAsync($"{Api}/grants", new { subjectId = alice.Id, roleId = reader, resourceId = beta, effectiveFrom = "2099-01-01T00:00:00Z" }),
            "a grant that starts in the future");
        t.Observe(
            await op.PostJsonAsync($"{Api}/grants", new { subjectId = alice.Id, roleId = reader, resourceId = gamma, effectiveTo = "2000-01-01T00:00:00Z" }),
            "a grant that ended in the past");
        t.Observe(await op.GetAsync($"{Api}/resources/{alpha}/grants"), "the resource's grants carry the window without the marker");
        t.Observe(await op.GetAsync($"{Api}/subjects/{alice.Id}/grants"), "so do the subject's grants");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/check", new { subjectId = alice.Id, permissionKey = read, resourceId = alpha }),
            "inside its window the grant allows read");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/check", new { subjectId = alice.Id, permissionKey = read, resourceId = beta }),
            "before its window it does not");
        t.Observe(
            await probe.PostJsonAsync("/__probe/fga/check", new { subjectId = alice.Id, permissionKey = read, resourceId = gamma }),
            "after its window it does not");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/fga/api/trace")]
    public async Task The_trace_explains_each_allow_and_deny_decision()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardCallback);
        var alice = await t.Setup.CreateUserAsync("alice");
        var fga = new FgaFixture(t);
        await fga.UserSubjectAsync(alice);
        var alpha = await fga.WorkspaceAsync("alpha", "Alpha");
        var docs = await fga.WorkspaceAsync("docs", "Docs", alpha);
        var beta = await fga.WorkspaceAsync("beta", "Beta");
        await fga.GrantAsync(alice.Id, alpha, BehaviorLockAuthorization.ReaderRole);
        var op = t.Operator;
        var read = BehaviorLockAuthorization.ReadPermission;
        var write = BehaviorLockAuthorization.WritePermission;

        t.Observe(await op.PostJsonAsync($"{Api}/trace", new { subjectId = alice.Id }), "subject, resource, and permission are required");
        t.Observe(await op.PostJsonAsync($"{Api}/trace", new { subjectId = alice.Id, resourceId = alpha, permissionKey = read }), "a direct grant");
        t.Observe(await op.PostJsonAsync($"{Api}/trace", new { subjectId = alice.Id, resourceId = docs, permissionKey = read }), "a grant inherited from the parent");
        t.Observe(await op.PostJsonAsync($"{Api}/trace", new { subjectId = alice.Id, resourceId = alpha, permissionKey = write }), "a grant whose role lacks the permission");
        t.Observe(await op.PostJsonAsync($"{Api}/trace", new { subjectId = alice.Id, resourceId = beta, permissionKey = read }), "no grant anywhere on the path");
        t.Observe(await op.PostJsonAsync($"{Api}/trace", new { subjectId = alice.Id, resourceId = "root", permissionKey = read }), "a permission scoped to another resource type");
        t.Observe(await op.PostJsonAsync($"{Api}/trace", new { subjectId = alice.Id, resourceId = alpha, permissionKey = "workspace.delete" }), "an unknown permission");
        t.Observe(await op.PostJsonAsync($"{Api}/trace", new { subjectId = "no-such-subject", resourceId = alpha, permissionKey = read }), "an unknown subject");
        t.Observe(await op.PostJsonAsync($"{Api}/trace", new { subjectId = alice.Id, resourceId = "workspace::missing", permissionKey = read }), "an unknown resource");

        await t.ApproveAsync();
    }

    /// <summary>
    /// In Password mode the FGA API authenticates with the operator's session cookie, so every
    /// mutation must carry <c>X-SqlOS-Request: 1</c> and come from the dashboard's own origin
    /// (Origin, else Referer, else <c>Sec-Fetch-Site: same-origin</c>). Each refusal is audited.
    /// </summary>
    [Scenario]
    [Covers("POST /sqlos/admin/fga/api/grants")]
    [Covers("DELETE /sqlos/admin/fga/api/grants/{grantId}")]
    [Covers("POST /sqlos/admin/fga/api/trace")]
    [Covers("GET /sqlos/admin/fga/api/grants")]
    public async Task Cookie_authenticated_fga_mutations_need_the_same_origin_proof()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.DashboardPassword);
        var alice = await t.Setup.CreateUserAsync("alice");
        var fga = new FgaFixture(t);
        await fga.UserSubjectAsync(alice);
        var alpha = await fga.WorkspaceAsync("alpha", "Alpha");
        var op = t.Operator;
        var grant = new { subjectId = alice.Id, roleId = BehaviorLockAuthorization.ReaderRole, resourceId = alpha };

        t.Observe(
            await t.NewBrowser("visitor").PostJsonAsync($"{Api}/grants", grant),
            "without a session the API is 401");
        t.Observe(
            await op.PostJsonAsync($"{Api}/grants", grant, options => options.WithoutCredentials()),
            "the session cookie alone is refused");
        t.Observe(
            await op.PostJsonAsync($"{Api}/grants", grant, options => options.WithoutCredentials().Header("X-SqlOS-Request", "1").Header("X-SqlOS-Request", "1")),
            "a repeated proof header is ambiguous");
        t.Observe(
            await op.PostJsonAsync($"{Api}/grants", grant, options => options.WithoutCredentials().Header("X-SqlOS-Request", "1").WithOrigin("https://evil.example")),
            "a foreign Origin is refused");
        t.Observe(
            await op.PostJsonAsync($"{Api}/grants", grant, options => options.WithoutCredentials().Header("X-SqlOS-Request", "1").WithOrigin("null")),
            "an opaque Origin is refused");
        t.Observe(
            await op.PostJsonAsync($"{Api}/grants", grant, options => options.WithoutCredentials().Header("X-SqlOS-Request", "1").WithOrigin("https://sqlos.example.test, https://evil.example")),
            "a list of origins is ambiguous");
        t.Observe(
            await op.PostJsonAsync($"{Api}/grants", grant, options => options.WithoutCredentials().Header("X-SqlOS-Request", "1")),
            "no Origin, Referer, or Sec-Fetch-Site is refused");
        t.Observe(
            await op.PostJsonAsync($"{Api}/grants", grant, options => options.WithoutCredentials().Header("X-SqlOS-Request", "1").Header("Referer", "https://evil.example/page")),
            "a foreign Referer is refused");
        t.Observe(
            await op.PostJsonAsync($"{Api}/grants", grant, options => options.WithoutCredentials().Header("X-SqlOS-Request", "1").Header("Referer", "https://sqlos.example.test/sqlos/admin/fga/grants")),
            "a same-origin Referer is accepted");
        var viaFetchMetadata = t.Observe(
            await op.PostJsonAsync($"{Api}/grants", grant, options => options.WithoutCredentials().Header("X-SqlOS-Request", "1").Header("Sec-Fetch-Site", "same-origin")),
            "so is Sec-Fetch-Site: same-origin");
        t.Observe(
            await op.DeleteAsync($"{Api}/grants/{viaFetchMetadata.JsonString("id")}", options => options.WithoutCredentials()),
            "a delete without the proof is refused");
        t.Observe(
            await op.DeleteAsync($"{Api}/grants/{viaFetchMetadata.JsonString("id")}"),
            "a delete with the proof and the dashboard's Origin");
        t.Observe(
            await op.PostJsonAsync($"{Api}/trace", new { subjectId = alice.Id, resourceId = alpha, permissionKey = BehaviorLockAuthorization.ReadPermission }, options => options.WithoutCredentials()),
            "a read-only POST such as the trace needs the proof too");
        t.Observe(await op.GetAsync($"{Api}/grants"), "reads need no proof");

        await t.ObserveAuditAsync("each refusal is audited");
        await t.ApproveAsync();
    }

    /// <summary>
    /// The machine client the standalone identity server seeds in code is an FGA service account
    /// that the dashboard lists with its code ownership.
    /// </summary>
    [Scenario]
    [Covers("GET /sqlos/admin/fga/api/service-accounts")]
    [Covers("GET /sqlos/admin/fga/api/subjects")]
    public async Task Code_owned_machine_clients_are_listed_as_service_accounts()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.MultiApp);
        var op = t.Operator;

        t.Observe(await op.GetAsync($"{Api}/service-accounts"), "the seeded machine client's service account");
        t.Observe(await op.GetAsync($"{Api}/subjects?type=service_account"), "and its subject");

        await t.ApproveAsync();
    }
}

using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Fga.Infrastructure;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga;

/// <summary>
/// <c>ListVisibleAsync</c> against three oracles on a random tree: the paper's Definition 1 written in C#,
/// <c>fn_IsResourceAccessible</c> applied to every resource, and <c>BuildFilterAsync</c> over the entity
/// table. All four must agree for every subject, with inactive nodes and subjects, time-windowed grants,
/// group grants, grants nested inside other grants, and grants on leaves.
/// </summary>
[TestClass]
public class SqlOSFgaVisibleListIntegrationTests : FgaIntegrationTestBase
{
    private const int MaxDepth = 10;
    private static readonly string Suffix = Guid.NewGuid().ToString("N")[..8];
    private static readonly string TreeRoot = $"vis_root_{Suffix}";
    private static readonly List<SqlOSFgaResource> Resources = [];
    private static readonly List<SqlOSFgaSubject> Subjects = [];
    private static readonly List<SqlOSFgaUser> Users = [];
    private static readonly List<SqlOSFgaUserGroup> Groups = [];
    private static readonly List<SqlOSFgaUserGroupMembership> Memberships = [];
    private static readonly List<SqlOSFgaGrant> Grants = [];
    private static readonly List<LifecycleProtectedEntity> Entities = [];
    private static SqlOSFgaPermission TeamOnlyPermission = null!;
    private static SqlOSFgaRole TeamViewerRole = null!;

    private SqlOSFgaAuthService _authService = null!;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        var random = new Random(20260930);
        string[] roles = [FgaTestDataSeeder.SystemAdminRoleId, FgaTestDataSeeder.AgencyAdminRoleId, FgaTestDataSeeder.AgencyMemberRoleId];

        // The tree: root → agencies → teams → projects, with some projects nested under projects, down to
        // the configured depth (a node may have at most MaxDepth ancestors; the tree root has one).
        var depths = new Dictionary<string, int>(StringComparer.Ordinal) { [TreeRoot] = 1 };
        Resources.Add(new SqlOSFgaResource { Id = TreeRoot, ParentId = "root", Name = "Visible list root", ResourceTypeId = "agency" });
        for (var a = 0; a < 5; a++)
        {
            var agency = Add($"agency_{a}", "agency", TreeRoot, random, depths);
            for (var t = 0; t < 3 + random.Next(3); t++)
            {
                var team = Add($"team_{a}_{t}", "team", agency, random, depths);
                var projects = new List<string>();
                for (var p = 0; p < 2 + random.Next(6); p++)
                {
                    var parent = team;
                    if (projects.Count > 0 && random.Next(4) == 0)
                    {
                        var candidate = projects[random.Next(projects.Count)];
                        parent = depths[candidate] < MaxDepth ? candidate : team;
                    }

                    projects.Add(Add($"project_{a}_{t}_{p}", "project", parent, random, depths));
                }
            }
        }

        // People: users and groups, some inactive.
        for (var u = 0; u < 8; u++)
        {
            var subjectId = $"vis_user_{u}_{Suffix}";
            Subjects.Add(new SqlOSFgaSubject { Id = subjectId, SubjectTypeId = "user", DisplayName = subjectId });
            Users.Add(new SqlOSFgaUser { Id = $"vis_usr_{u}_{Suffix}", SubjectId = subjectId, IsActive = u != 7 });
        }

        for (var g = 0; g < 3; g++)
        {
            var subjectId = $"vis_group_{g}_{Suffix}";
            Subjects.Add(new SqlOSFgaSubject { Id = subjectId, SubjectTypeId = "group", DisplayName = subjectId });
            Groups.Add(new SqlOSFgaUserGroup { Id = $"vis_ug_{g}_{Suffix}", SubjectId = subjectId, Name = subjectId, IsActive = g != 2 });
            foreach (var user in Users.Where(_ => random.Next(2) == 0))
            {
                Memberships.Add(new SqlOSFgaUserGroupMembership { SubjectId = user.SubjectId, UserGroupId = Groups[^1].Id });
            }
        }

        // Grants at random nodes, including the tree root, leaves, nested ones, and expired or future windows.
        var grantees = Users.Select(u => u.SubjectId).Concat(Groups.Select(g => g.SubjectId)).ToList();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 40; i++)
        {
            var resource = Resources[random.Next(Resources.Count)];
            var window = random.Next(10);
            Grants.Add(new SqlOSFgaGrant
            {
                Id = $"vis_grant_{i}_{Suffix}",
                SubjectId = grantees[random.Next(grantees.Count)],
                ResourceId = resource.Id,
                RoleId = roles[random.Next(roles.Length)],
                EffectiveFrom = window == 0 ? now.AddDays(1) : window == 1 ? now.AddDays(-1) : null,
                EffectiveTo = window == 2 ? now.AddDays(-1) : window == 3 ? now.AddDays(1) : null,
            });
        }

        // A permission scoped to teams, with its own role and a grant for user 0 at the tree root.
        TeamOnlyPermission = new SqlOSFgaPermission { Id = $"vis_perm_team_{Suffix}", Key = $"VIS_TEAM_VIEW_{Suffix}", Name = "View teams", ResourceTypeId = "team" };
        TeamViewerRole = new SqlOSFgaRole { Id = $"vis_role_team_{Suffix}", Key = $"TeamViewer_{Suffix}", Name = "Team viewer" };
        Grants.Add(new SqlOSFgaGrant { Id = $"vis_grant_team_{Suffix}", SubjectId = Users[0].SubjectId, ResourceId = TreeRoot, RoleId = TeamViewerRole.Id });

        // One entity row per resource.
        Entities.AddRange(Resources.Select(r => new LifecycleProtectedEntity { Id = $"ent_{r.Id}", ResourceId = r.Id }));

        Context.Set<SqlOSFgaResource>().AddRange(Resources);
        Context.Set<SqlOSFgaSubject>().AddRange(Subjects);
        Context.Set<SqlOSFgaUser>().AddRange(Users);
        Context.Set<SqlOSFgaUserGroup>().AddRange(Groups);
        Context.Set<SqlOSFgaPermission>().Add(TeamOnlyPermission);
        Context.Set<SqlOSFgaRole>().Add(TeamViewerRole);
        await Context.SaveChangesAsync();
        Context.Set<SqlOSFgaRolePermission>().Add(new SqlOSFgaRolePermission { RoleId = TeamViewerRole.Id, PermissionId = TeamOnlyPermission.Id });
        Context.Set<SqlOSFgaUserGroupMembership>().AddRange(Memberships);
        Context.Set<SqlOSFgaGrant>().AddRange(Grants);
        Context.Set<LifecycleProtectedEntity>().AddRange(Entities);
        await Context.SaveChangesAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        // Untracked deletes: the shared context has fixed up navigations across these graphs.
        Context.ChangeTracker.Clear();
        var resourceIds = Resources.Select(r => r.Id).ToList();
        var grantIds = Grants.Select(g => g.Id).ToList();
        var groupIds = Groups.Select(g => g.Id).ToList();
        var subjectIds = Subjects.Select(s => s.Id).ToList();
        await Context.Set<LifecycleProtectedEntity>().Where(e => resourceIds.Contains(e.ResourceId)).ExecuteDeleteAsync();
        await Context.Set<SqlOSFgaGrant>().Where(g => grantIds.Contains(g.Id)).ExecuteDeleteAsync();
        await Context.Set<SqlOSFgaUserGroupMembership>().Where(m => groupIds.Contains(m.UserGroupId)).ExecuteDeleteAsync();
        await Context.Set<SqlOSFgaRolePermission>().Where(rp => rp.RoleId == TeamViewerRole.Id).ExecuteDeleteAsync();
        await Context.Set<SqlOSFgaRole>().Where(r => r.Id == TeamViewerRole.Id).ExecuteDeleteAsync();
        await Context.Set<SqlOSFgaPermission>().Where(p => p.Id == TeamOnlyPermission.Id).ExecuteDeleteAsync();
        await Context.Set<SqlOSFgaUserGroup>().Where(g => groupIds.Contains(g.Id)).ExecuteDeleteAsync();
        await Context.Set<SqlOSFgaUser>().Where(u => subjectIds.Contains(u.SubjectId)).ExecuteDeleteAsync();
        await Context.Set<SqlOSFgaSubject>().Where(s => subjectIds.Contains(s.Id)).ExecuteDeleteAsync();
        foreach (var resource in Enumerable.Reverse(Resources))
        {
            await Context.Set<SqlOSFgaResource>().Where(r => r.Id == resource.Id).ExecuteDeleteAsync();
        }
    }

    [TestInitialize]
    public void TestInit()
    {
        _authService = new SqlOSFgaAuthService(
            Context,
            Options.Create(new SqlOSFgaOptions()),
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqlOSFgaAuthService>());
    }

    [TestMethod]
    public async Task EverySubject_SeesExactlyWhatTheDefinitionTheFunctionAndTheFilterAllow()
    {
        var seqs = await ReadSeqsAsync();
        foreach (var type in new[] { "project", "team", "agency" })
        {
            foreach (var user in Users)
            {
                var definition = Resources
                    .Where(r => r.ResourceTypeId == type && Allowed(user.SubjectId, "TEST_VIEW", r))
                    .Select(r => r.Id)
                    .OrderBy(id => seqs[id])
                    .ToList();

                var function = new List<string>();
                var subjects = JsonSerializer.Serialize(await ResolvedSubjectsAsync(user.SubjectId));
                foreach (var resource in Resources.Where(r => r.ResourceTypeId == type))
                {
                    if (await Context.IsResourceAccessible(resource.Id, subjects, FgaTestDataSeeder.ViewPermissionId).AnyAsync())
                    {
                        function.Add(resource.Id);
                    }
                }

                var filter = await _authService.BuildFilterAsync<LifecycleProtectedEntity>(user.SubjectId, "TEST_VIEW");
                var filtered = await Context.Set<LifecycleProtectedEntity>()
                    .Where(filter)
                    .Select(e => e.ResourceId)
                    .ToListAsync();
                var typeIds = Resources.Where(r => r.ResourceTypeId == type).Select(r => r.Id).ToHashSet();

                var pages = await WalkPagesAsync(user.SubjectId, "TEST_VIEW", type, pageSize: 7);

                function.OrderBy(id => seqs[id]).Should().Equal(definition, "fn_IsResourceAccessible must implement Definition 1 for {0}/{1}", user.SubjectId, type);
                filtered.Where(typeIds.Contains).OrderBy(id => seqs[id]).Should().Equal(definition, "BuildFilterAsync must agree for {0}/{1}", user.SubjectId, type);
                pages.Should().Equal(definition, "ListVisibleAsync must return the same rows, in creation order, for {0}/{1}", user.SubjectId, type);
            }
        }
    }

    [TestMethod]
    public async Task Pages_AreDisjoint_AndDoNotDependOnThePageSize()
    {
        var user = Users[0].SubjectId;
        var byOne = await WalkPagesAsync(user, "TEST_VIEW", "project", pageSize: 1);
        var byThree = await WalkPagesAsync(user, "TEST_VIEW", "project", pageSize: 3);
        var byHundred = await WalkPagesAsync(user, "TEST_VIEW", "project", pageSize: 100);

        byOne.Should().OnlyHaveUniqueItems();
        byThree.Should().Equal(byOne);
        byHundred.Should().Equal(byOne);
    }

    [TestMethod]
    public async Task ATypedPermission_ListsOnlyItsType()
    {
        var user = Users[0].SubjectId;
        var teams = await WalkPagesAsync(user, TeamOnlyPermission.Key, "team", pageSize: 5);
        var projects = await WalkPagesAsync(user, TeamOnlyPermission.Key, "project", pageSize: 5);

        teams.Should().NotBeEmpty();
        teams.Should().OnlyContain(id => Resources.Single(r => r.Id == id).ResourceTypeId == "team");
        projects.Should().BeEmpty("the permission is scoped to teams");
    }

    [TestMethod]
    public async Task UnknownSubjectPermissionOrType_YieldEmptyPages()
    {
        (await _authService.ListVisibleAsync<LifecycleProtectedEntity>("nobody", "TEST_VIEW", "project", 10)).Items.Should().BeEmpty();
        (await _authService.ListVisibleAsync<LifecycleProtectedEntity>(Users[0].SubjectId, "NO_SUCH_PERMISSION", "project", 10)).Items.Should().BeEmpty();
        (await _authService.ListVisibleAsync<LifecycleProtectedEntity>(Users[0].SubjectId, "TEST_VIEW", "no_such_type", 10)).Items.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AnInactiveCaller_SeesNothing()
    {
        var inactive = Users.Single(u => !u.IsActive).SubjectId;
        (await WalkPagesAsync(inactive, "TEST_VIEW", "project", 10)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task InvalidArguments_AreRejected()
    {
        var user = Users[0].SubjectId;
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() => _authService.ListVisibleAsync<LifecycleProtectedEntity>(user, "TEST_VIEW", "project", 0));
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() => _authService.ListVisibleAsync<LifecycleProtectedEntity>(user, "TEST_VIEW", "project", SqlOSFgaAuthService.MaxVisiblePageSize + 1));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => _authService.ListVisibleAsync<LifecycleProtectedEntity>(user, "TEST_VIEW", "project", 10, "not-a-cursor"));
    }

    private async Task<List<string>> WalkPagesAsync(string subjectId, string permissionKey, string resourceTypeId, int pageSize)
    {
        var ids = new List<string>();
        string? cursor = null;
        for (var guard = 0; guard < 1000; guard++)
        {
            var page = await _authService.ListVisibleAsync<LifecycleProtectedEntity>(subjectId, permissionKey, resourceTypeId, pageSize, cursor);
            page.Items.Count.Should().BeLessThanOrEqualTo(pageSize);
            ids.AddRange(page.Items.Select(e => e.ResourceId));
            if (!page.HasMore)
            {
                return ids;
            }

            page.Items.Count.Should().Be(pageSize, "a page with a next cursor is full");
            cursor = page.NextCursor;
        }

        throw new AssertFailedException("The page walk did not terminate.");
    }

    /// <summary>Definition 1 of the paper: some active-path ancestor (or the resource) carries a matching, active grant.</summary>
    private static bool Allowed(string subjectId, string permissionKey, SqlOSFgaResource target)
    {
        var permission = permissionKey == "TEST_VIEW" ? FgaTestDataSeeder.ViewPermissionId : TeamOnlyPermission.Id;
        var permissionType = permissionKey == "TEST_VIEW" ? null : TeamOnlyPermission.ResourceTypeId;
        if (permissionType != null && permissionType != target.ResourceTypeId)
        {
            return false;
        }

        var user = Users.Single(u => u.SubjectId == subjectId);
        if (!user.IsActive)
        {
            return false;
        }

        var principals = new HashSet<string>(StringComparer.Ordinal) { subjectId };
        foreach (var membership in Memberships.Where(m => m.SubjectId == subjectId))
        {
            var group = Groups.Single(g => g.Id == membership.UserGroupId);
            if (group.IsActive)
            {
                principals.Add(group.SubjectId);
            }
        }

        var now = DateTime.UtcNow;
        var byId = Resources.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var current = target;
        var depth = 0;
        while (current != null && current.IsActive && depth <= MaxDepth)
        {
            foreach (var grant in Grants.Where(g => g.ResourceId == current.Id && principals.Contains(g.SubjectId)))
            {
                var inWindow = (grant.EffectiveFrom == null || grant.EffectiveFrom <= now)
                    && (grant.EffectiveTo == null || grant.EffectiveTo >= now);
                if (inWindow && RoleHas(grant.RoleId, permission))
                {
                    return true;
                }
            }

            current = current.ParentId != null && byId.TryGetValue(current.ParentId, out var parent) ? parent : null;
            depth++;
            if (current != null && current.Id == "root")
            {
                // The SqlOS root is outside the generated tree and carries other suites' grants; stop here.
                break;
            }
        }

        return false;
    }

    private static bool RoleHas(string roleId, string permissionId)
    {
        if (roleId == TeamViewerRole.Id)
        {
            return permissionId == TeamOnlyPermission.Id;
        }

        if (permissionId != FgaTestDataSeeder.ViewPermissionId)
        {
            return false;
        }

        return roleId is FgaTestDataSeeder.SystemAdminRoleId or FgaTestDataSeeder.AgencyAdminRoleId or FgaTestDataSeeder.AgencyMemberRoleId;
    }

    private static async Task<List<string>> ResolvedSubjectsAsync(string subjectId)
    {
        var groups = await Context.Set<SqlOSFgaUserGroupMembership>()
            .Where(m => m.SubjectId == subjectId)
            .Join(Context.Set<SqlOSFgaUserGroup>(), m => m.UserGroupId, g => g.Id, (m, g) => g)
            .Where(g => g.IsActive)
            .Select(g => g.SubjectId)
            .ToListAsync();
        return [subjectId, .. groups];
    }

    private static string Add(string name, string type, string parentId, Random random, Dictionary<string, int> depths)
    {
        var id = $"vis_{name}_{Suffix}";
        Resources.Add(new SqlOSFgaResource
        {
            Id = id,
            ParentId = parentId,
            Name = name,
            ResourceTypeId = type,
            IsActive = random.Next(12) != 0,
        });
        depths[id] = depths[parentId] + 1;
        return id;
    }

    private static async Task<Dictionary<string, long>> ReadSeqsAsync()
    {
        var connection = Context.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = TestDatabase.Rewrite("SELECT [Id], [Seq] FROM [dbo].[SqlOSFgaResources]");
            await using DbDataReader reader = await command.ExecuteReaderAsync();
            var seqs = new Dictionary<string, long>(StringComparer.Ordinal);
            while (await reader.ReadAsync())
            {
                seqs[reader.GetString(0)] = reader.GetInt64(1);
            }

            return seqs;
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
    }
}

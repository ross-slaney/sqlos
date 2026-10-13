using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Models;
using SqlOS.IntegrationTests.Fga.Infrastructure;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga;

[TestClass]
public class SqlOSFgaAuthServiceIntegrationTests : FgaIntegrationTestBase
{
    private SqlOSFgaAuthService _authService = null!;
    private readonly List<int> _counters = [];

    [TestInitialize]
    public async Task TestInit()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        _authService = new SqlOSFgaAuthService(
            Context,
            Options.Create(new SqlOSFgaOptions()),
            loggerFactory.CreateLogger<SqlOSFgaAuthService>());

        // The list half of each rule pages over rows of a SqlOSDbContext that point at the test's resources.
        await PageOrderFixture.EnsureSeededAsync();
    }

    [TestCleanup]
    public async Task RemoveCounters()
    {
        // Resource edits made through the hand-rolled context leave these rows' page-order entries
        // stale; deleting the rows deletes their entries.
        await using var context = PageOrderFixture.CreateContext();
        context.Counters.RemoveRange(await context.Counters.Where(counter => _counters.Contains(counter.Id)).ToListAsync());
        await context.SaveChangesAsync();
    }

    [TestMethod]
    public async Task CheckAccess_SystemAdmin_HasAccessToEverything()
    {
        var result = await _authService.CheckAccessAsync(
            FgaTestDataSeeder.SystemAdminSubjectId, "TEST_VIEW", FgaTestDataSeeder.TestTeamResourceId);
        Assert.IsTrue(result.Allowed);
    }

    [TestMethod]
    public async Task CheckAccess_AgencyAdmin_HasAccessToChildResources()
    {
        var result = await _authService.CheckAccessAsync(
            FgaTestDataSeeder.AgencyAdminSubjectId, "TEST_VIEW", FgaTestDataSeeder.TestProjectResourceId);
        Assert.IsTrue(result.Allowed);
    }

    [TestMethod]
    public async Task CheckAccess_AgencyMember_DeniedEditPermission()
    {
        var result = await _authService.CheckAccessAsync(
            FgaTestDataSeeder.AgencyMemberSubjectId, "TEST_EDIT", FgaTestDataSeeder.TestProjectResourceId);
        Assert.IsFalse(result.Allowed);
    }

    [TestMethod]
    public async Task CheckAccess_GroupMember_InheritsGroupGrant()
    {
        var result = await _authService.CheckAccessAsync(
            FgaTestDataSeeder.GroupMemberSubjectId, "TEST_VIEW", FgaTestDataSeeder.TestTeamResourceId);
        Assert.IsTrue(result.Allowed);
    }

    [TestMethod]
    public async Task CheckAccess_Unauthorized_DeniedAccess()
    {
        var result = await _authService.CheckAccessAsync(
            FgaTestDataSeeder.UnauthorizedSubjectId, "TEST_VIEW", FgaTestDataSeeder.TestTeamResourceId);
        Assert.IsFalse(result.Allowed);
    }

    [TestMethod]
    public async Task CheckAccess_CrossAgency_DeniedAccess()
    {
        var result = await _authService.CheckAccessAsync(
            FgaTestDataSeeder.AgencyAdminSubjectId, "TEST_VIEW", FgaTestDataSeeder.OtherAgencyResourceId);
        Assert.IsFalse(result.Allowed);
    }

    [TestMethod]
    public async Task HasCapability_SystemAdmin_HasAdminCapability()
    {
        var result = await _authService.HasCapabilityAsync(
            FgaTestDataSeeder.SystemAdminSubjectId, "TEST_ADMIN");
        Assert.IsTrue(result);
    }

    [TestMethod]
    public async Task HasCapability_AgencyAdmin_NoAdminCapability()
    {
        var result = await _authService.HasCapabilityAsync(
            FgaTestDataSeeder.AgencyAdminSubjectId, "TEST_ADMIN");
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task TraceAccess_ProvidesDetailedTrace()
    {
        var trace = await _authService.TraceResourceAccessAsync(
            FgaTestDataSeeder.SystemAdminSubjectId, FgaTestDataSeeder.TestTeamResourceId, "TEST_VIEW");

        Assert.IsTrue(trace.AccessGranted);
        Assert.IsTrue(trace.PathNodes.Count > 0);
        Assert.IsFalse(string.IsNullOrEmpty(trace.DecisionSummary));
    }

    [TestMethod]
    public async Task TypeScopedPermission_DeniesDifferentTargetType_InPointTraceAndPage()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var permission = new SqlOSFgaPermission
        {
            Id = $"perm_typed_deny_{suffix}",
            Key = $"TYPED_DENY_{suffix}",
            Name = "Team-only permission",
            ResourceTypeId = "team"
        };
        Context.Set<SqlOSFgaPermission>().Add(permission);
        Context.Set<SqlOSFgaRolePermission>().Add(new SqlOSFgaRolePermission
        {
            RoleId = FgaTestDataSeeder.SystemAdminRoleId,
            PermissionId = permission.Id
        });
        await Context.SaveChangesAsync();

        var point = await _authService.CheckAccessAsync(
            FgaTestDataSeeder.SystemAdminSubjectId,
            permission.Key,
            FgaTestDataSeeder.TestProjectResourceId);
        var trace = await _authService.TraceResourceAccessAsync(
            FgaTestDataSeeder.SystemAdminSubjectId,
            FgaTestDataSeeder.TestProjectResourceId,
            permission.Key);
        await AddCounterAsync(FgaTestDataSeeder.TestProjectResourceId);

        Assert.IsFalse(point.Allowed);
        Assert.IsTrue(point.Error?.Contains("does not apply to resource type project", StringComparison.Ordinal));
        Assert.IsFalse(trace.AccessGranted);
        Assert.IsTrue(trace.DenialReason?.Contains("applies to resource type 'team'", StringComparison.Ordinal));
        await AssertPointAndPageAsync(FgaTestDataSeeder.SystemAdminSubjectId, FgaTestDataSeeder.TestProjectResourceId, expected: false, permission.Key);
    }

    [TestMethod]
    public async Task TypeScopedPermission_AllowsTargetTypeViaAncestorGrant()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var permission = new SqlOSFgaPermission
        {
            Id = $"perm_typed_allow_{suffix}",
            Key = $"TYPED_ALLOW_{suffix}",
            Name = "Project permission",
            ResourceTypeId = "project"
        };
        Context.Set<SqlOSFgaPermission>().Add(permission);
        Context.Set<SqlOSFgaRolePermission>().Add(new SqlOSFgaRolePermission
        {
            RoleId = FgaTestDataSeeder.AgencyAdminRoleId,
            PermissionId = permission.Id
        });
        await Context.SaveChangesAsync();

        var result = await _authService.CheckAccessAsync(
            FgaTestDataSeeder.AgencyAdminSubjectId,
            permission.Key,
            FgaTestDataSeeder.TestProjectResourceId);

        Assert.IsTrue(result.Allowed, "a permission scoped to the target type should still inherit through an ancestor grant");
    }

    [TestMethod]
    public async Task InactiveUser_IsDeniedByPointCheckAndPageDespiteExistingGrant()
    {
        var subjectService = CreateSubjectService();
        var user = await subjectService.CreateUserAsync("Lifecycle User", $"lifecycle-{Guid.NewGuid():N}@example.com");
        var resourceId = await CreateProtectedResourceWithGrantAsync(user.SubjectId);

        await AssertPointAndPageAsync(user.SubjectId, resourceId, expected: true);

        user.IsActive = false;
        await Context.SaveChangesAsync();

        await AssertPointAndPageAsync(user.SubjectId, resourceId, expected: false);
    }

    [TestMethod]
    public async Task InactiveResourceOrAncestor_IsDeniedByPointCheckAndPage()
    {
        var subjectService = CreateSubjectService();
        var user = await subjectService.CreateUserAsync("Resource Lifecycle User", $"resource-lifecycle-{Guid.NewGuid():N}@example.com");
        var suffix = Guid.NewGuid().ToString("N");
        var parent = new SqlOSFgaResource
        {
            Id = $"res_lifecycle_parent_{suffix}",
            ParentId = "root",
            Name = "Lifecycle Parent",
            ResourceTypeId = "agency"
        };
        var child = new SqlOSFgaResource
        {
            Id = $"res_lifecycle_child_{suffix}",
            ParentId = parent.Id,
            Name = "Lifecycle Child",
            ResourceTypeId = "project"
        };
        Context.Set<SqlOSFgaResource>().AddRange(parent, child);
        Context.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant
        {
            Id = $"grant_lifecycle_{suffix}",
            SubjectId = user.SubjectId,
            ResourceId = parent.Id,
            RoleId = FgaTestDataSeeder.AgencyMemberRoleId
        });
        await Context.SaveChangesAsync();
        await AddCounterAsync(child.Id);

        await AssertPointAndPageAsync(user.SubjectId, child.Id, expected: true);

        parent.IsActive = false;
        await Context.SaveChangesAsync();
        await AssertPointAndPageAsync(user.SubjectId, child.Id, expected: false);

        parent.IsActive = true;
        child.IsActive = false;
        await Context.SaveChangesAsync();
        await AssertPointAndPageAsync(user.SubjectId, child.Id, expected: false);
    }

    [TestMethod]
    public async Task ExpiredServiceAccount_IsDeniedByPointCheckAndPage()
    {
        var subjectService = CreateSubjectService();
        var serviceAccount = await subjectService.CreateServiceAccountAsync(
            "Lifecycle Worker",
            $"client-{Guid.NewGuid():N}",
            "test-only-hash",
            expiresAt: DateTime.UtcNow.AddMinutes(5));
        var resourceId = await CreateProtectedResourceWithGrantAsync(serviceAccount.SubjectId);

        await AssertPointAndPageAsync(serviceAccount.SubjectId, resourceId, expected: true);

        serviceAccount.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await Context.SaveChangesAsync();

        await AssertPointAndPageAsync(serviceAccount.SubjectId, resourceId, expected: false);
    }

    [TestMethod]
    public async Task InactiveGroupGrant_IsDeniedByPointCheckAndPage()
    {
        var subjectService = CreateSubjectService();
        var user = await subjectService.CreateUserAsync("Group Lifecycle User", $"group-lifecycle-{Guid.NewGuid():N}@example.com");
        var group = await subjectService.CreateGroupAsync("Lifecycle Group");
        await subjectService.AddToGroupAsync(user.SubjectId, group.Id);
        var resourceId = await CreateProtectedResourceWithGrantAsync(group.SubjectId);

        await AssertPointAndPageAsync(user.SubjectId, resourceId, expected: true);

        group.IsActive = false;
        await Context.SaveChangesAsync();

        await AssertPointAndPageAsync(user.SubjectId, resourceId, expected: false);
    }

    [TestMethod]
    public async Task Page_RechecksTheCallersLifecycle_WhenItsStatementsRun()
    {
        var subjectService = CreateSubjectService();
        var user = await subjectService.CreateUserAsync("Racing Lifecycle User", $"racing-lifecycle-{Guid.NewGuid():N}@example.com");
        var group = await subjectService.CreateGroupAsync("Racing Lifecycle Group");
        await subjectService.AddToGroupAsync(user.SubjectId, group.Id);
        var resourceId = await CreateProtectedResourceWithGrantAsync(group.SubjectId);
        await AssertPointAndPageAsync(user.SubjectId, resourceId, expected: true);

        // ToPageAsync resolves the user as active; the user is deactivated before the page's first statement.
        var deactivate = new DeactivateBeforeFirstPageStatement(user.SubjectId);
        await using var pages = PageOrderFixture.CreateContext(null, deactivate);
        var page = await pages.Counters.Where(counter => counter.ResourceId == resourceId).OrderBy(counter => counter.Rank)
            .ToPageAsync(user.SubjectId, "TEST_VIEW", 10);

        Assert.IsTrue(deactivate.Fired, "The user must be deactivated between resolution and the page's statements.");
        Assert.AreEqual(0, page.Data.Count, "The page's SQL must recheck the caller when it runs.");
    }

    [TestMethod]
    public async Task CraftedSubjectIdentifier_CannotInjectAnotherGrantSubjectIntoAPage()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var victimSubjectId = $"subj_victim_{suffix}";
        var attackerSubjectId = $"subj_attacker_{suffix},{victimSubjectId}";
        Context.Set<SqlOSFgaSubject>().AddRange(
            new SqlOSFgaSubject { Id = victimSubjectId, SubjectTypeId = "user", DisplayName = "Victim" },
            new SqlOSFgaSubject { Id = attackerSubjectId, SubjectTypeId = "user", DisplayName = "Attacker" });
        Context.Set<SqlOSFgaUser>().AddRange(
            new SqlOSFgaUser { Id = $"usr_victim_{suffix}", SubjectId = victimSubjectId, IsActive = true },
            new SqlOSFgaUser { Id = $"usr_attacker_{suffix}", SubjectId = attackerSubjectId, IsActive = true });
        await Context.SaveChangesAsync();
        var resourceId = await CreateProtectedResourceWithGrantAsync(victimSubjectId);

        await AssertPointAndPageAsync(attackerSubjectId, resourceId, expected: false);
    }

    private SqlOSFgaSubjectService CreateSubjectService()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        return new SqlOSFgaSubjectService(
            Context,
            loggerFactory.CreateLogger<SqlOSFgaSubjectService>());
    }

    private async Task<string> CreateProtectedResourceWithGrantAsync(string grantSubjectId)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var resourceId = $"res_lifecycle_{suffix}";
        Context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
        {
            Id = resourceId,
            ParentId = "root",
            Name = "Lifecycle Protected Resource",
            ResourceTypeId = "project"
        });
        Context.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant
        {
            Id = $"grant_lifecycle_{suffix}",
            SubjectId = grantSubjectId,
            ResourceId = resourceId,
            RoleId = FgaTestDataSeeder.AgencyMemberRoleId
        });
        await Context.SaveChangesAsync();
        await AddCounterAsync(resourceId);
        return resourceId;
    }

    private async Task AddCounterAsync(string resourceId)
    {
        await using var context = PageOrderFixture.CreateContext();
        var counter = new PagedCounter { ResourceId = resourceId, Rank = 1 };
        context.Counters.Add(counter);
        await context.SaveChangesAsync();
        _counters.Add(counter.Id);
    }

    // The point check, a page scoped to the resource (read 1 decides), and a walk over every row
    // (read 2 under the caller's nodes, then the final check) agree.
    private async Task AssertPointAndPageAsync(string subjectId, string resourceId, bool expected, string permissionKey = "TEST_VIEW")
    {
        var pointCheck = await _authService.CheckAccessAsync(subjectId, permissionKey, resourceId);
        Assert.AreEqual(expected, pointCheck.Allowed, "Point authorization result did not match lifecycle policy.");

        await using var pages = PageOrderFixture.CreateContext();
        var scoped = await pages.Counters.AsNoTracking()
            .Where(counter => counter.ResourceId == resourceId)
            .OrderBy(counter => counter.Rank)
            .ToPageAsync(subjectId, permissionKey, 10);
        Assert.AreEqual(expected, scoped.Data.Count > 0, "A page scoped to the resource did not match the point authorization result.");

        var listed = new List<PagedCounter>();
        string? cursor = null;
        do
        {
            var page = await pages.Counters.AsNoTracking().OrderBy(counter => counter.Rank).ToPageAsync(subjectId, permissionKey, 50, cursor);
            listed.AddRange(page.Data);
            cursor = page.NextCursor;
        }
        while (cursor != null);

        Assert.AreEqual(expected, listed.Any(counter => counter.ResourceId == resourceId), "A walk over every row did not match the point authorization result.");
    }

    /// <summary>Deactivates a user, on its own connection, just before the first statement that reads the page's rows.</summary>
    private sealed class DeactivateBeforeFirstPageStatement(string subjectId) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("PagedCounters", StringComparison.Ordinal))
            {
                Fired = true;
                await PageOrderFixture.ExecuteAsync(
                    $"UPDATE {TestDatabase.Qualify("SqlOSFgaUsers")} SET {TestDatabase.Quote("IsActive")} = {(TestDatabase.IsPostgreSql ? "FALSE" : "0")} WHERE {TestDatabase.Quote("SubjectId")} = '{subjectId}'");
            }

            return result;
        }
    }
}

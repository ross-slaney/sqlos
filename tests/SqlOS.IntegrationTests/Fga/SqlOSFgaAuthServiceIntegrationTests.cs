using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;
using SqlOS.IntegrationTests.Fga.Infrastructure;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga;

[TestClass]
public class SqlOSFgaAuthServiceIntegrationTests : FgaIntegrationTestBase
{
    private SqlOSFgaAuthService _authService = null!;

    [TestInitialize]
    public void TestInit()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        _authService = new SqlOSFgaAuthService(
            Context,
            Options.Create(new SqlOSFgaOptions()),
            loggerFactory.CreateLogger<SqlOSFgaAuthService>());
    }

    [TestMethod]
    public async Task BuildFilterAsync_ComposesIntoASingleSqlQuery_OverTheScopeColumn()
    {
        var filter = await _authService.BuildFilterAsync<LifecycleProtectedEntity>(
            FgaTestDataSeeder.AgencyAdminSubjectId,
            "TEST_VIEW");
        var sql = Context.Set<LifecycleProtectedEntity>().Where(filter).ToQueryString();

        // The predicate reads the row's own scope column at the agency admin's level (1), with the root as a
        // parameter, joins nothing, and checks the caller's liveness once per query. On SQL Server the level is
        // The same SQL on both engines: the level's eight bytes compared with a parameter, under the depth-byte
        // filter of the level's index.
        StringAssert.Contains(sql, $"{SqlOSFgaLineage.ScopeAncestorOffset(1)}, 8)");
        StringAssert.Contains(sql, "SUBSTRING(");
        StringAssert.Contains(sql, "FgaScope");
        StringAssert.Contains(sql, "fn_ActiveSubjects");
        Assert.IsFalse(sql.Contains("fn_IsResourceAccessible", StringComparison.OrdinalIgnoreCase), sql);
        Assert.IsFalse(sql.Contains("SqlOSFgaResources", StringComparison.OrdinalIgnoreCase), sql);
        Assert.AreEqual(1, Regex.Matches(sql, "LifecycleProtectedEntities", RegexOptions.IgnoreCase).Count, $"One query over the table. SQL:{Environment.NewLine}{sql}");
    }

    [TestMethod]
    public async Task TheFilter_AgreesWithThePointCheck()
    {
        var suffix = Guid.NewGuid().ToString("N");
        foreach (var (resource, i) in new[] { FgaTestDataSeeder.TestAgencyResourceId, FgaTestDataSeeder.TestTeamResourceId, FgaTestDataSeeder.TestProjectResourceId, FgaTestDataSeeder.OtherAgencyResourceId, "root" }.Select((r, i) => (r, i)))
        {
            Context.Set<LifecycleProtectedEntity>().Add(new LifecycleProtectedEntity { Id = $"agree_{i}_{suffix}", ResourceId = resource, Rank = i });
        }

        await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();

        var subjects = new[]
        {
            FgaTestDataSeeder.SystemAdminSubjectId, FgaTestDataSeeder.AgencyAdminSubjectId, FgaTestDataSeeder.AgencyMemberSubjectId,
            FgaTestDataSeeder.GroupMemberSubjectId, FgaTestDataSeeder.UnauthorizedSubjectId,
        };
        var rows = await Context.Set<LifecycleProtectedEntity>().AsNoTracking().Where(e => e.Id.EndsWith(suffix)).ToListAsync();
        foreach (var subject in subjects)
        {
            foreach (var permission in new[] { "TEST_VIEW", "TEST_EDIT" })
            {
                var expected = new List<string>();
                foreach (var row in rows)
                {
                    if ((await _authService.CheckAccessAsync(subject, permission, row.ResourceId)).Allowed)
                    {
                        expected.Add(row.Id);
                    }
                }

                var visible = await Context.Set<LifecycleProtectedEntity>().AsNoTracking()
                    .Where(e => e.Id.EndsWith(suffix))
                    .Where(await _authService.BuildFilterAsync<LifecycleProtectedEntity>(subject, permission))
                    .OrderBy(e => e.Rank)
                    .Select(e => e.Id)
                    .ToListAsync();

                CollectionAssert.AreEqual(expected.OrderBy(id => id).ToList(), visible.OrderBy(id => id).ToList(), $"{subject} / {permission}");
            }
        }
    }

    [TestMethod]
    public async Task ACallerWithMoreRootsThanTheListLimit_IsCheckedRowByRow()
    {
        // 1,001 grants on single projects: the predicate checks each row with fn_IsResourceAccessible instead
        // of listing the roots, and still returns exactly the granted rows.
        var subjectService = CreateSubjectService();
        var user = await subjectService.CreateUserAsync("Many Grants User", $"many-grants-{Guid.NewGuid():N}@example.com");
        var suffix = Guid.NewGuid().ToString("N");
        var granted = Enumerable.Range(0, 1_001).Select(i => $"many_{i}_{suffix}").ToArray();
        var ungranted = $"many_none_{suffix}";
        Context.ChangeTracker.Clear();
        foreach (var id in granted.Append(ungranted))
        {
            Context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource { Id = id, ParentId = FgaTestDataSeeder.TestAgencyResourceId, Name = id, ResourceTypeId = "project" });
        }

        await Context.SaveChangesAsync();
        foreach (var id in granted)
        {
            Context.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant { Id = $"grant_{id}", SubjectId = user.SubjectId, ResourceId = id, RoleId = FgaTestDataSeeder.AgencyMemberRoleId });
        }

        Context.Set<LifecycleProtectedEntity>().AddRange(
            new LifecycleProtectedEntity { Id = $"manyrow_a_{suffix}", ResourceId = granted[0] },
            new LifecycleProtectedEntity { Id = $"manyrow_b_{suffix}", ResourceId = granted[^1] },
            new LifecycleProtectedEntity { Id = $"manyrow_c_{suffix}", ResourceId = ungranted });
        await Context.SaveChangesAsync();
        Context.ChangeTracker.Clear();

        var filter = await _authService.BuildFilterAsync<LifecycleProtectedEntity>(user.SubjectId, "TEST_VIEW");
        var query = Context.Set<LifecycleProtectedEntity>().AsNoTracking().Where(e => e.Id.EndsWith(suffix)).Where(filter);
        StringAssert.Contains(query.ToQueryString(), "fn_IsResourceAccessible");
        var visible = await query.Select(e => e.Id).ToListAsync();
        CollectionAssert.AreEquivalent(new[] { $"manyrow_a_{suffix}", $"manyrow_b_{suffix}" }, visible);
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
    public async Task TypeScopedPermission_DeniesDifferentTargetType_InPointTraceAndEfFilter()
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
        Context.Set<LifecycleProtectedEntity>().Add(new LifecycleProtectedEntity
        {
            Id = $"typed_{Guid.NewGuid():N}",
            ResourceId = FgaTestDataSeeder.TestProjectResourceId
        });
        await Context.SaveChangesAsync();
        var filter = await _authService.BuildFilterAsync<LifecycleProtectedEntity>(
            FgaTestDataSeeder.SystemAdminSubjectId,
            permission.Key);

        Assert.IsFalse(point.Allowed);
        Assert.IsTrue(point.Error?.Contains("does not apply to resource type project", StringComparison.Ordinal));
        Assert.IsFalse(trace.AccessGranted);
        Assert.IsTrue(trace.DenialReason?.Contains("applies to resource type 'team'", StringComparison.Ordinal));
        Assert.IsFalse(await Context.Set<LifecycleProtectedEntity>()
            .Where(x => x.ResourceId == FgaTestDataSeeder.TestProjectResourceId)
            .Where(filter)
            .AnyAsync());
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
    public async Task InactiveUser_IsDeniedByPointCheckAndEfFilterDespiteExistingGrant()
    {
        var subjectService = CreateSubjectService();
        var user = await subjectService.CreateUserAsync("Lifecycle User", $"lifecycle-{Guid.NewGuid():N}@example.com");
        var resourceId = await CreateProtectedResourceWithGrantAsync(user.SubjectId);

        await AssertPointAndFilterAsync(user.SubjectId, resourceId, expected: true);

        user.IsActive = false;
        await Context.SaveChangesAsync();

        await AssertPointAndFilterAsync(user.SubjectId, resourceId, expected: false);
    }

    [TestMethod]
    public async Task InactiveResourceOrAncestor_IsDeniedByPointCheckAndEfFilter()
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
        Context.Set<LifecycleProtectedEntity>().Add(new LifecycleProtectedEntity { Id = suffix, ResourceId = child.Id });
        await Context.SaveChangesAsync();

        await AssertPointAndFilterAsync(user.SubjectId, child.Id, expected: true);

        parent.IsActive = false;
        await Context.SaveChangesAsync();
        await AssertPointAndFilterAsync(user.SubjectId, child.Id, expected: false);

        parent.IsActive = true;
        child.IsActive = false;
        await Context.SaveChangesAsync();
        await AssertPointAndFilterAsync(user.SubjectId, child.Id, expected: false);
    }

    [TestMethod]
    public async Task ExpiredServiceAccount_IsDeniedByPointCheckAndEfFilter()
    {
        var subjectService = CreateSubjectService();
        var serviceAccount = await subjectService.CreateServiceAccountAsync(
            "Lifecycle Worker",
            $"client-{Guid.NewGuid():N}",
            "test-only-hash",
            expiresAt: DateTime.UtcNow.AddMinutes(5));
        var resourceId = await CreateProtectedResourceWithGrantAsync(serviceAccount.SubjectId);

        await AssertPointAndFilterAsync(serviceAccount.SubjectId, resourceId, expected: true);

        serviceAccount.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await Context.SaveChangesAsync();

        await AssertPointAndFilterAsync(serviceAccount.SubjectId, resourceId, expected: false);
    }

    [TestMethod]
    public async Task InactiveGroupGrant_IsDeniedByPointCheckAndEfFilter()
    {
        var subjectService = CreateSubjectService();
        var user = await subjectService.CreateUserAsync("Group Lifecycle User", $"group-lifecycle-{Guid.NewGuid():N}@example.com");
        var group = await subjectService.CreateGroupAsync("Lifecycle Group");
        await subjectService.AddToGroupAsync(user.SubjectId, group.Id);
        var resourceId = await CreateProtectedResourceWithGrantAsync(group.SubjectId);

        await AssertPointAndFilterAsync(user.SubjectId, resourceId, expected: true);

        group.IsActive = false;
        await Context.SaveChangesAsync();

        await AssertPointAndFilterAsync(user.SubjectId, resourceId, expected: false);
    }

    [TestMethod]
    public async Task EfFilter_RechecksDirectGroupMemberLifecycleWhenQueryExecutes()
    {
        var subjectService = CreateSubjectService();
        var user = await subjectService.CreateUserAsync("Racing Lifecycle User", $"racing-lifecycle-{Guid.NewGuid():N}@example.com");
        var group = await subjectService.CreateGroupAsync("Racing Lifecycle Group");
        await subjectService.AddToGroupAsync(user.SubjectId, group.Id);
        var resourceId = await CreateProtectedResourceWithGrantAsync(group.SubjectId);
        var filter = await _authService.BuildFilterAsync<LifecycleProtectedEntity>(user.SubjectId, "TEST_VIEW");

        user.IsActive = false;
        await Context.SaveChangesAsync();

        var listed = await Context.Set<LifecycleProtectedEntity>()
            .Where(item => item.ResourceId == resourceId)
            .Where(filter)
            .AnyAsync();
        Assert.IsFalse(listed, "The SQL query must recheck the direct member after the filter has been constructed.");
    }

    [TestMethod]
    public async Task CraftedSubjectIdentifier_CannotInjectAnotherGrantSubjectIntoEfFilter()
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

        await AssertPointAndFilterAsync(attackerSubjectId, resourceId, expected: false);
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
        Context.Set<LifecycleProtectedEntity>().Add(new LifecycleProtectedEntity { Id = suffix, ResourceId = resourceId });
        await Context.SaveChangesAsync();
        return resourceId;
    }

    private async Task AssertPointAndFilterAsync(string subjectId, string resourceId, bool expected)
    {
        var pointCheck = await _authService.CheckAccessAsync(subjectId, "TEST_VIEW", resourceId);
        Assert.AreEqual(expected, pointCheck.Allowed, "Point authorization result did not match lifecycle policy.");

        var filter = await _authService.BuildFilterAsync<LifecycleProtectedEntity>(subjectId, "TEST_VIEW");
        var listed = await Context.Set<LifecycleProtectedEntity>()
            .Where(item => item.ResourceId == resourceId)
            .Where(filter)
            .AnyAsync();
        Assert.AreEqual(expected, listed, "EF authorization filter did not match the point authorization result.");
    }
}

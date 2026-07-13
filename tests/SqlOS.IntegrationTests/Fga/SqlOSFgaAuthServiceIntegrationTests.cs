using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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

        var filter = await _authService.GetAuthorizationFilterAsync<LifecycleProtectedEntity>(subjectId, "TEST_VIEW");
        var listed = await Context.Set<LifecycleProtectedEntity>()
            .Where(item => item.ResourceId == resourceId)
            .Where(filter)
            .AnyAsync();
        Assert.AreEqual(expected, listed, "EF authorization filter did not match the point authorization result.");
    }
}

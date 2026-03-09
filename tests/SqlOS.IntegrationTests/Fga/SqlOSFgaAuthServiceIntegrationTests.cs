using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Configuration;
using SqlOS.IntegrationTests.Fga.Infrastructure;
using SqlOS.Fga.Services;

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
}

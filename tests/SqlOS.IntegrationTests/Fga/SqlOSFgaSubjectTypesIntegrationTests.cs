using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Configuration;
using SqlOS.IntegrationTests.Fga.Infrastructure;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;

namespace SqlOS.IntegrationTests.Fga;

[TestClass]
public class SqlOSFgaSubjectTypesIntegrationTests : FgaIntegrationTestBase
{
    private SqlOSFgaSubjectService _subjectService = null!;

    [TestInitialize]
    public void TestInit()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        _subjectService = new SqlOSFgaSubjectService(
            Context,
            loggerFactory.CreateLogger<SqlOSFgaSubjectService>());
    }

    [TestMethod]
    public async Task User_CanBeCreatedAndQueried()
    {
        var user = await _subjectService.CreateUserAsync("Integration Test User", "integration@test.com");
        Assert.IsNotNull(user);
        Assert.IsTrue(user.Id.StartsWith("usr_"));
        Assert.AreEqual("integration@test.com", user.Email);

        var subject = await Context.Set<SqlOSFgaSubject>()
            .FirstOrDefaultAsync(p => p.Id == user.SubjectId);
        Assert.IsNotNull(subject);
        Assert.AreEqual("user", subject.SubjectTypeId);
    }

    [TestMethod]
    public async Task Agent_CanBeAddedToGroup()
    {
        var agent = await _subjectService.CreateAgentAsync("New Agent", "worker");
        var group = await _subjectService.CreateGroupAsync("New Group");

        await _subjectService.AddToGroupAsync(agent.SubjectId, group.Id);

        var groups = await _subjectService.GetGroupsForSubjectAsync(agent.SubjectId);
        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual(group.Id, groups[0].Id);
    }

    [TestMethod]
    public async Task Agent_InheritsGrantsViaGroup()
    {
        // TestAgent is in TestGroup (seeded). TestGroup has AgencyMemberRole at TestAgencyResourceId.
        // AgencyMember has TEST_VIEW permission. So agent should have TEST_VIEW on TestTeamResourceId.
        var authService = new SqlOSFgaAuthService(
            Context,
            Options.Create(new SqlOSFgaOptions()),
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<SqlOSFgaAuthService>());

        var result = await authService.CheckAccessAsync(
            FgaTestDataSeeder.TestAgentSubjectId, "TEST_VIEW", FgaTestDataSeeder.TestTeamResourceId);
        Assert.IsTrue(result.Allowed, "Agent should inherit TEST_VIEW from group membership");
    }

    [TestMethod]
    public async Task ServiceAccount_CanBeCreatedWithCredentials()
    {
        var sa = await _subjectService.CreateServiceAccountAsync(
            "Integration SA", "int_client", "int_hash", "Integration test service account");
        Assert.IsNotNull(sa);
        Assert.IsTrue(sa.Id.StartsWith("sa_"));
        Assert.AreEqual("int_client", sa.ClientId);
        Assert.AreEqual("int_hash", sa.ClientSecretHash);

        var subject = await Context.Set<SqlOSFgaSubject>()
            .FirstOrDefaultAsync(p => p.Id == sa.SubjectId);
        Assert.IsNotNull(subject);
        Assert.AreEqual("service_account", subject.SubjectTypeId);
    }

    [TestMethod]
    public async Task AllSubjectTypes_ResolveCorrectly()
    {
        var userIds = await _subjectService.ResolveSubjectIdsAsync(FgaTestDataSeeder.TestUserSubjectId);
        Assert.AreEqual(1, userIds.Count);
        Assert.AreEqual(FgaTestDataSeeder.TestUserSubjectId, userIds[0]);

        var agentIds = await _subjectService.ResolveSubjectIdsAsync(FgaTestDataSeeder.TestAgentSubjectId);
        Assert.IsTrue(agentIds.Count >= 2);
        Assert.IsTrue(agentIds.Contains(FgaTestDataSeeder.TestAgentSubjectId));
        Assert.IsTrue(agentIds.Contains(FgaTestDataSeeder.TestGroupSubjectId));

        var saIds = await _subjectService.ResolveSubjectIdsAsync(FgaTestDataSeeder.TestServiceAccountSubjectId);
        Assert.AreEqual(1, saIds.Count);
        Assert.AreEqual(FgaTestDataSeeder.TestServiceAccountSubjectId, saIds[0]);
    }
}

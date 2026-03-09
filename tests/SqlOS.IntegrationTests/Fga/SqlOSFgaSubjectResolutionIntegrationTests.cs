using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.IntegrationTests.Fga.Infrastructure;
using SqlOS.Fga.Services;

namespace SqlOS.IntegrationTests.Fga;

[TestClass]
public class SqlOSFgaSubjectResolutionIntegrationTests : FgaIntegrationTestBase
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
    public async Task ResolveSubjectIds_UserWithGroups_ReturnsUserAndGroupSubjects()
    {
        var ids = await _subjectService.ResolveSubjectIdsAsync(FgaTestDataSeeder.GroupMemberSubjectId);
        Assert.IsTrue(ids.Count >= 2);
        Assert.IsTrue(ids.Contains(FgaTestDataSeeder.GroupMemberSubjectId));
        Assert.IsTrue(ids.Contains(FgaTestDataSeeder.TestGroupSubjectId));
    }

    [TestMethod]
    public async Task ResolveSubjectIds_UserWithoutGroups_ReturnsOnlyUser()
    {
        var ids = await _subjectService.ResolveSubjectIdsAsync(FgaTestDataSeeder.UnauthorizedSubjectId);
        Assert.AreEqual(1, ids.Count);
        Assert.AreEqual(FgaTestDataSeeder.UnauthorizedSubjectId, ids[0]);
    }

    [TestMethod]
    public async Task AddToGroup_GroupSubject_ThrowsInvalidOperation()
    {
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await _subjectService.AddToGroupAsync(
                FgaTestDataSeeder.TestGroupSubjectId, FgaTestDataSeeder.TestGroupId);
        });
    }

    [TestMethod]
    public async Task GetGroupsForSubject_ReturnsCorrectGroups()
    {
        var groups = await _subjectService.GetGroupsForSubjectAsync(FgaTestDataSeeder.GroupMemberSubjectId);
        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual(FgaTestDataSeeder.TestGroupId, groups[0].Id);
    }
}

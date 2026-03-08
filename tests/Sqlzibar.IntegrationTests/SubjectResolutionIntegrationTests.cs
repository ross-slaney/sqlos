using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sqlzibar.IntegrationTests.Infrastructure;
using Sqlzibar.Services;

namespace Sqlzibar.IntegrationTests;

[TestClass]
public class SubjectResolutionIntegrationTests : IntegrationTestBase
{
    private SqlzibarSubjectService _subjectService = null!;

    [TestInitialize]
    public void TestInit()
    {
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        _subjectService = new SqlzibarSubjectService(
            Context,
            loggerFactory.CreateLogger<SqlzibarSubjectService>());
    }

    [TestMethod]
    public async Task ResolveSubjectIds_UserWithGroups_ReturnsUserAndGroupSubjects()
    {
        var ids = await _subjectService.ResolveSubjectIdsAsync(TestDataSeeder.GroupMemberSubjectId);
        Assert.IsTrue(ids.Count >= 2);
        Assert.IsTrue(ids.Contains(TestDataSeeder.GroupMemberSubjectId));
        Assert.IsTrue(ids.Contains(TestDataSeeder.TestGroupSubjectId));
    }

    [TestMethod]
    public async Task ResolveSubjectIds_UserWithoutGroups_ReturnsOnlyUser()
    {
        var ids = await _subjectService.ResolveSubjectIdsAsync(TestDataSeeder.UnauthorizedSubjectId);
        Assert.AreEqual(1, ids.Count);
        Assert.AreEqual(TestDataSeeder.UnauthorizedSubjectId, ids[0]);
    }

    [TestMethod]
    public async Task AddToGroup_GroupSubject_ThrowsInvalidOperation()
    {
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await _subjectService.AddToGroupAsync(
                TestDataSeeder.TestGroupSubjectId, TestDataSeeder.TestGroupId);
        });
    }

    [TestMethod]
    public async Task GetGroupsForSubject_ReturnsCorrectGroups()
    {
        var groups = await _subjectService.GetGroupsForSubjectAsync(TestDataSeeder.GroupMemberSubjectId);
        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual(TestDataSeeder.TestGroupId, groups[0].Id);
    }
}

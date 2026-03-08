using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Sqlzibar.Extensions;
using Sqlzibar.Interfaces;
using Sqlzibar.Models;
using Sqlzibar.Services;

namespace Sqlzibar.Tests;

public class TestInMemoryDbContext : DbContext, ISqlzibarDbContext
{
    public TestInMemoryDbContext(DbContextOptions<TestInMemoryDbContext> options) : base(options) { }

    public IQueryable<SqlzibarAccessibleResource> IsResourceAccessible(
        string resourceId, string subjectIds, string permissionId)
        => throw new NotSupportedException("TVF not supported with InMemory provider");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Apply model config WITHOUT TVF registration (InMemory doesn't support it)
        Sqlzibar.Configuration.SqlzibarModelConfiguration.Configure(modelBuilder, new Sqlzibar.Configuration.SqlzibarOptions());
    }
}

[TestClass]
public class SqlzibarSubjectServiceTests
{
    private TestInMemoryDbContext _context = null!;
    private SqlzibarSubjectService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<TestInMemoryDbContext>()
            .UseInMemoryDatabase(databaseName: $"Test_{Guid.NewGuid()}")
            .Options;

        _context = new TestInMemoryDbContext(options);

        // Seed subject types
        _context.Set<SqlzibarSubjectType>().AddRange(
            new SqlzibarSubjectType { Id = "user", Name = "User" },
            new SqlzibarSubjectType { Id = "group", Name = "Group" },
            new SqlzibarSubjectType { Id = "service_account", Name = "Service Account" },
            new SqlzibarSubjectType { Id = "agent", Name = "Agent" }
        );
        _context.SaveChanges();

        _service = new SqlzibarSubjectService(
            _context,
            Mock.Of<ILogger<SqlzibarSubjectService>>());
    }

    [TestCleanup]
    public void Cleanup()
    {
        _context.Dispose();
    }

    [TestMethod]
    public async Task CreateSubject_CreatesWithGeneratedId()
    {
        var result = await _service.CreateSubjectAsync("Test User", "user");
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Id.StartsWith("subj_"));
        Assert.AreEqual("Test User", result.DisplayName);
        Assert.AreEqual("user", result.SubjectTypeId);
    }

    [TestMethod]
    public async Task CreateGroup_CreatesGroupAndSubject()
    {
        var group = await _service.CreateGroupAsync("Test Group", "A test group");
        Assert.IsNotNull(group);
        Assert.IsTrue(group.Id.StartsWith("grp_"));
        Assert.AreEqual("Test Group", group.Name);

        var subject = await _context.Set<SqlzibarSubject>()
            .FirstOrDefaultAsync(p => p.Id == group.SubjectId);
        Assert.IsNotNull(subject);
        Assert.AreEqual("group", subject.SubjectTypeId);
    }

    [TestMethod]
    public async Task AddToGroup_ValidUser_Succeeds()
    {
        var user = await _service.CreateSubjectAsync("User", "user");
        var group = await _service.CreateGroupAsync("Group");

        await _service.AddToGroupAsync(user.Id, group.Id);

        var groups = await _service.GetGroupsForSubjectAsync(user.Id);
        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual(group.Id, groups[0].Id);
    }

    [TestMethod]
    public async Task AddToGroup_GroupSubject_ThrowsInvalidOperation()
    {
        var group1 = await _service.CreateGroupAsync("Group 1");
        var group2 = await _service.CreateGroupAsync("Group 2");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await _service.AddToGroupAsync(group1.SubjectId, group2.Id);
        });
    }

    [TestMethod]
    public async Task AddToGroup_Idempotent_DoesNotDuplicate()
    {
        var user = await _service.CreateSubjectAsync("User", "user");
        var group = await _service.CreateGroupAsync("Group");

        await _service.AddToGroupAsync(user.Id, group.Id);
        await _service.AddToGroupAsync(user.Id, group.Id); // second call

        var memberships = await _context.Set<SqlzibarUserGroupMembership>()
            .Where(m => m.SubjectId == user.Id)
            .ToListAsync();
        Assert.AreEqual(1, memberships.Count);
    }

    [TestMethod]
    public async Task RemoveFromGroup_RemovesMembership()
    {
        var user = await _service.CreateSubjectAsync("User", "user");
        var group = await _service.CreateGroupAsync("Group");

        await _service.AddToGroupAsync(user.Id, group.Id);
        await _service.RemoveFromGroupAsync(user.Id, group.Id);

        var groups = await _service.GetGroupsForSubjectAsync(user.Id);
        Assert.AreEqual(0, groups.Count);
    }

    [TestMethod]
    public async Task ResolveSubjectIds_ReturnsUserAndGroupSubjects()
    {
        var user = await _service.CreateSubjectAsync("User", "user");
        var group = await _service.CreateGroupAsync("Group");

        await _service.AddToGroupAsync(user.Id, group.Id);

        var ids = await _service.ResolveSubjectIdsAsync(user.Id);
        Assert.AreEqual(2, ids.Count);
        Assert.IsTrue(ids.Contains(user.Id));
        Assert.IsTrue(ids.Contains(group.SubjectId));
    }

    [TestMethod]
    public async Task CreateUser_CreatesUserAndSubject()
    {
        var user = await _service.CreateUserAsync("Test User", "test@example.com");
        Assert.IsNotNull(user);
        Assert.IsTrue(user.Id.StartsWith("usr_"));
        Assert.AreEqual("test@example.com", user.Email);
        Assert.IsTrue(user.IsActive);

        var subject = await _context.Set<SqlzibarSubject>()
            .FirstOrDefaultAsync(p => p.Id == user.SubjectId);
        Assert.IsNotNull(subject);
        Assert.AreEqual("user", subject.SubjectTypeId);
        Assert.AreEqual("Test User", subject.DisplayName);
    }

    [TestMethod]
    public async Task CreateAgent_CreatesAgentAndSubject()
    {
        var agent = await _service.CreateAgentAsync("Test Agent", "background_job", "Nightly sync");
        Assert.IsNotNull(agent);
        Assert.IsTrue(agent.Id.StartsWith("agt_"));
        Assert.AreEqual("background_job", agent.AgentType);
        Assert.AreEqual("Nightly sync", agent.Description);

        var subject = await _context.Set<SqlzibarSubject>()
            .FirstOrDefaultAsync(p => p.Id == agent.SubjectId);
        Assert.IsNotNull(subject);
        Assert.AreEqual("agent", subject.SubjectTypeId);
        Assert.AreEqual("Test Agent", subject.DisplayName);
    }

    [TestMethod]
    public async Task CreateServiceAccount_CreatesServiceAccountAndSubject()
    {
        var sa = await _service.CreateServiceAccountAsync("API Client", "client_123", "hash_abc");
        Assert.IsNotNull(sa);
        Assert.IsTrue(sa.Id.StartsWith("sa_"));
        Assert.AreEqual("client_123", sa.ClientId);
        Assert.AreEqual("hash_abc", sa.ClientSecretHash);

        var subject = await _context.Set<SqlzibarSubject>()
            .FirstOrDefaultAsync(p => p.Id == sa.SubjectId);
        Assert.IsNotNull(subject);
        Assert.AreEqual("service_account", subject.SubjectTypeId);
        Assert.AreEqual("API Client", subject.DisplayName);
    }

    [TestMethod]
    public async Task AddToGroup_AgentSubject_Succeeds()
    {
        var agent = await _service.CreateAgentAsync("Agent", "worker");
        var group = await _service.CreateGroupAsync("Group");

        await _service.AddToGroupAsync(agent.SubjectId, group.Id);

        var groups = await _service.GetGroupsForSubjectAsync(agent.SubjectId);
        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual(group.Id, groups[0].Id);
    }
}

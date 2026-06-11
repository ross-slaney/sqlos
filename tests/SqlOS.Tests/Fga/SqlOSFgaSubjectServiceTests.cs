using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;

namespace SqlOS.Tests.Fga;

public class TestInMemoryDbContext : DbContext, ISqlOSFgaDbContext
{
    public TestInMemoryDbContext(DbContextOptions<TestInMemoryDbContext> options) : base(options) { }

    public IQueryable<SqlOSFgaAccessibleResource> IsResourceAccessible(
        string resourceId, string subjectIds, string permissionId)
        => throw new NotSupportedException("TVF not supported with InMemory provider");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplySqlOSFgaModel();
    }
}

[TestClass]
public class SqlOSFgaSubjectServiceTests
{
    private TestInMemoryDbContext _context = null!;
    private SqlOSFgaSubjectService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<TestInMemoryDbContext>()
            .UseInMemoryDatabase(databaseName: $"Test_{Guid.NewGuid()}")
            .Options;

        _context = new TestInMemoryDbContext(options);

        // Seed subject types
        _context.Set<SqlOSFgaSubjectType>().AddRange(
            new SqlOSFgaSubjectType { Id = "user", Name = "User" },
            new SqlOSFgaSubjectType { Id = "group", Name = "Group" },
            new SqlOSFgaSubjectType { Id = "service_account", Name = "Service Account" },
            new SqlOSFgaSubjectType { Id = "agent", Name = "Agent" }
        );
        _context.SaveChanges();

        _service = new SqlOSFgaSubjectService(
            _context,
            NullLogger<SqlOSFgaSubjectService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _context.Dispose();
    }

    [TestMethod]
    public void CreateResource_WhenParentIsSelf_Throws()
    {
        Assert.ThrowsException<InvalidOperationException>(() =>
            _context.CreateResource("res_self", "Self", "root", id: "res_self"));
    }

    [TestMethod]
    public void CreateResource_WhenExistingParentChainHasCycle_Throws()
    {
        _context.Set<SqlOSFgaResource>().AddRange(
            new SqlOSFgaResource { Id = "res_a", ParentId = "res_b", Name = "A", ResourceTypeId = "root" },
            new SqlOSFgaResource { Id = "res_b", ParentId = "res_a", Name = "B", ResourceTypeId = "root" });
        _context.SaveChanges();

        Assert.ThrowsException<InvalidOperationException>(() =>
            _context.CreateResource("res_a", "Child", "root", id: "res_child"));
    }

    [TestMethod]
    public async Task GetAuthorizationFilterAsync_UsesCapturedParametersInsteadOfLiteralConstants()
    {
        _context.Set<SqlOSFgaPermission>().Add(new SqlOSFgaPermission
        {
            Id = "perm_read",
            Key = "READ",
            Name = "Read"
        });
        _context.SaveChanges();
        var authService = new SqlOSFgaAuthService(
            _context,
            Options.Create(new SqlOSFgaOptions()),
            NullLogger<SqlOSFgaAuthService>.Instance);

        var filter = await authService.GetAuthorizationFilterAsync<TestProtectedEntity>("subj_user", "READ");
        var constants = ConstantCollector.Collect(filter);

        Assert.IsFalse(constants.Contains("subj_user"));
        Assert.IsFalse(constants.Contains("perm_read"));
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

        var subject = await _context.Set<SqlOSFgaSubject>()
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

        var memberships = await _context.Set<SqlOSFgaUserGroupMembership>()
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

        var subject = await _context.Set<SqlOSFgaSubject>()
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

        var subject = await _context.Set<SqlOSFgaSubject>()
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

        var subject = await _context.Set<SqlOSFgaSubject>()
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

    private sealed class TestProtectedEntity : IHasResourceId
    {
        public string ResourceId { get; set; } = string.Empty;
    }

    private sealed class ConstantCollector : ExpressionVisitor
    {
        private readonly List<object?> _values = new();

        public static List<object?> Collect(Expression expression)
        {
            var collector = new ConstantCollector();
            collector.Visit(expression);
            return collector._values;
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            _values.Add(node.Value);
            return base.VisitConstant(node);
        }
    }
}

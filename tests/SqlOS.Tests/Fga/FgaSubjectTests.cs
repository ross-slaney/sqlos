using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;
using SqlOS.Domain.Events;
using SqlOS.Extensions;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests.Fga;

/// <summary>The subject aggregate: kinds, lifecycle, group membership and the host API over it.</summary>
[TestClass]
public sealed class FgaSubjectTests
{
    private static readonly DateTime Now = FgaTestModel.Now;

    [TestMethod]
    public void A_subject_is_created_with_its_kind_and_typed_record()
    {
        var subject = SqlOSFgaSubject.CreateUser("usr_alice", "Alice", "org_1", "usr_alice", "usr::alice", "alice@example.com", isActive: true, FgaActor.Host, Now);

        subject.SubjectTypeId.Should().Be("user");
        subject.User.Should().BeEquivalentTo(new { Id = "usr::alice", SubjectId = "usr_alice", Email = "alice@example.com", IsActive = true });
        Events(subject).Should().Equal(new FgaSubjectCreated("usr_alice", "user", "org_1", FgaActor.Host));
    }

    [TestMethod]
    public void A_subject_s_kind_cannot_change()
    {
        var agent = FgaTestModel.Subject("worker", "agent");

        var attachUser = () => agent.AttachUser("usr::worker", null, isActive: true, Now);

        attachUser.Should().Throw<InvalidOperationException>().WithMessage("FGA subject 'worker' is a 'agent', not a 'user'.");
        agent.User.Should().BeNull();
    }

    [TestMethod]
    public void Only_users_and_groups_have_an_active_state_and_a_change_is_recorded_once()
    {
        var user = FgaTestModel.Existing(SqlOSFgaSubject.CreateUser("usr_alice", "Alice", null, null, "usr::alice", null, isActive: true, FgaActor.Host, Now));
        var agent = FgaTestModel.Existing(SqlOSFgaSubject.CreateAgent("indexer", "Indexer", null, null, "agt::indexer", null, null, FgaActor.Host, Now));

        user.ChangeActivity(false, FgaActor.Directory("scim_1"), Now);
        user.ChangeActivity(false, FgaActor.Directory("scim_1"), Now);
        var deactivateAgent = () => agent.ChangeActivity(false, FgaActor.Host, Now);

        user.User!.IsActive.Should().BeFalse();
        Events(user).Should().Equal(new FgaSubjectActivationChanged("usr_alice", false, FgaActor.Directory("scim_1")));
        deactivateAgent.Should().Throw<InvalidOperationException>().WithMessage("FGA subject 'indexer' of type 'agent' has no active state.");
    }

    [TestMethod]
    public void A_group_takes_users_and_agents_but_never_another_group()
    {
        var group = Group("support");
        var user = FgaTestModel.Subject("usr_alice", "user");
        var otherGroup = Group("billing");

        var membership = group.AddMember(user, FgaActor.Host, Now);
        var nest = () => group.AddMember(otherGroup, FgaActor.Host, Now);

        membership.Should().BeEquivalentTo(new { SubjectId = "usr_alice", UserGroupId = "grp::support" });
        nest.Should().Throw<InvalidOperationException>().WithMessage("Groups cannot be members of other groups");
        Events(group).Should().Equal(new FgaGroupMemberAdded("support", "grp::support", "usr_alice", FgaActor.Host));
    }

    [TestMethod]
    public void A_group_removes_only_its_own_memberships()
    {
        var support = Group("support");
        var foreign = FgaTestModel.Membership("usr_alice", "grp::billing");

        var remove = () => support.RemoveMember(foreign, FgaActor.Host);

        remove.Should().Throw<InvalidOperationException>().WithMessage("Subject 'usr_alice' is not a member of group 'grp::support'.");
        Events(support).Should().BeEmpty();
    }

    [TestMethod]
    public void A_service_account_s_expiry_change_is_recorded_and_its_use_is_stamped()
    {
        var account = FgaTestModel.Existing(SqlOSFgaSubject.CreateServiceAccount(
            "sa_reporter", "Reporter", null, null, "sa::reporter", "reporter", "hash", null, null, "dashboard", null, FgaActor.Operator, Now));
        var later = Now.AddHours(1);

        account.ChangeServiceAccountExpiry(later, FgaActor.Operator, later);
        account.ChangeServiceAccountExpiry(later, FgaActor.Operator, later);
        account.RecordActivity(later);

        account.ServiceAccount!.ExpiresAt.Should().Be(later);
        account.ServiceAccount.LastUsedAt.Should().Be(later);
        Events(account).Should().Equal(new FgaServiceAccountExpiryChanged("sa_reporter", later, FgaActor.Operator));
    }

    [TestMethod]
    public async Task Host_provisioning_is_idempotent_and_audits_only_the_creation_and_the_lifecycle_change()
    {
        await using var context = CreateContext();
        await context.ProvisionUserSubjectAsync("usr_alice", "Alice", "alice@example.com");
        await context.SaveChangesAsync();

        await context.ProvisionUserSubjectAsync("usr_alice", "Alice Smith");
        await context.SaveChangesAsync();
        await context.ProvisionUserSubjectAsync("usr_alice", "Alice Smith", isActive: false);
        await context.SaveChangesAsync();

        var user = await context.Set<SqlOSFgaUser>().SingleAsync();
        user.Should().BeEquivalentTo(new { SubjectId = "usr_alice", Email = "alice@example.com", IsActive = false });
        (await context.Set<SqlOSFgaSubject>().SingleAsync()).DisplayName.Should().Be("Alice Smith");
        (await AuditAsync(context)).Should().Equal("fga.subject.created", "fga.subject.deactivated");
    }

    [TestMethod]
    public async Task Provisioning_repairs_a_bare_subject_with_its_typed_record()
    {
        await using var context = CreateContext();
        context.Set<SqlOSFgaSubject>().Add(FgaTestModel.Subject("usr_bare", "user", "Bare"));
        await context.SaveChangesAsync();

        var user = await context.ProvisionUserSubjectAsync("usr_bare", "Bare");
        await context.SaveChangesAsync();

        user.SubjectId.Should().Be("usr_bare");
        (await context.Set<SqlOSFgaUser>().CountAsync()).Should().Be(1);
    }

    [TestMethod]
    public async Task A_tenant_owned_group_is_provisioned_by_its_stable_subject_id()
    {
        await using var context = CreateContext();

        var group = await context.ProvisionGroupSubjectAsync("grp-support", "Support", groupType: "team", organizationId: "org_acme");
        await context.SaveChangesAsync();
        var again = await context.ProvisionGroupSubjectAsync("grp-support", "Support desk", description: "Customer support");
        await context.SaveChangesAsync();

        again.Id.Should().Be(group.Id);
        var subject = await context.Set<SqlOSFgaSubject>().Include(s => s.UserGroup).SingleAsync();
        subject.Should().BeEquivalentTo(new { Id = "grp-support", SubjectTypeId = "group", OrganizationId = "org_acme", DisplayName = "Support desk" });
        subject.UserGroup.Should().BeEquivalentTo(new { Name = "Support desk", Description = "Customer support", GroupType = "team", IsActive = true });
    }

    [TestMethod]
    public async Task Recording_activity_stamps_the_kind_s_own_column()
    {
        await using var context = CreateContext();
        await context.ProvisionUserSubjectAsync("usr_alice", "Alice");
        await context.ProvisionAgentSubjectAsync("indexer", "Indexer");
        await context.SaveChangesAsync();

        await context.RecordSubjectActivityAsync("usr_alice");
        await context.RecordSubjectActivityAsync("indexer");
        await context.SaveChangesAsync();
        var unknown = () => context.RecordSubjectActivityAsync("nobody");

        (await context.Set<SqlOSFgaUser>().SingleAsync()).LastLoginAt.Should().NotBeNull();
        (await context.Set<SqlOSFgaAgent>().SingleAsync()).LastRunAt.Should().NotBeNull();
        await unknown.Should().ThrowAsync<InvalidOperationException>().WithMessage("FGA subject 'nobody' was not found.");
    }

    [TestMethod]
    public async Task The_subject_service_saves_group_membership_changes_with_their_audit_rows()
    {
        await using var context = CreateContext();
        var service = new SqlOSFgaSubjectService(context, NullLogger<SqlOSFgaSubjectService>.Instance);
        var group = await service.CreateGroupAsync("Support");
        var user = await service.CreateUserAsync("Alice");

        await service.AddToGroupAsync(user.SubjectId, group.Id);
        await service.AddToGroupAsync(user.SubjectId, group.Id);
        await service.RemoveFromGroupAsync(user.SubjectId, group.Id);
        var missing = () => service.AddToGroupAsync(user.SubjectId, "grp_missing");

        (await context.Set<SqlOSFgaUserGroupMembership>().CountAsync()).Should().Be(0);
        (await AuditAsync(context)).Should().Equal(
            "fga.subject.created",
            "fga.subject.created",
            "fga.group.member_added",
            "fga.group.member_removed");
        await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("Group 'grp_missing' not found");
    }

    [TestMethod]
    public async Task An_access_check_trace_names_an_unknown_or_inactive_subject()
    {
        await using var context = CreateContext();
        context.Set<SqlOSFgaResourceType>().Add(FgaTestModel.ResourceType("document"));
        context.Set<SqlOSFgaResource>().Add(FgaTestModel.Resource("doc", "Doc", "document"));
        context.Set<SqlOSFgaPermission>().Add(FgaTestModel.Permission("perm_read", "doc.read", resourceTypeId: "document"));
        context.Set<SqlOSFgaSubject>().Add(FgaTestModel.Subject("usr_gone", "user", "Gone"));
        context.Set<SqlOSFgaUser>().Add(FgaTestModel.User("usr::gone", "usr_gone", isActive: false));
        await context.SaveChangesAsync();
        var fga = new SqlOSFgaAuthService(context, Options.Create(new SqlOSFgaOptions()), NullLogger<SqlOSFgaAuthService>.Instance);

        var unknown = await fga.CheckAccessAsync("usr_nobody", "doc.read", "doc");
        var inactive = await fga.CheckAccessAsync("usr_gone", "doc.read", "doc");

        unknown.Trace.Single(step => step.Step == "Subject Resolution").Detail.Should().Be("Subject \"usr_nobody\" was not found");
        inactive.Trace.Single(step => step.Step == "Subject Resolution").Detail.Should().Be("Subject \"Gone\" is not active");
        new[] { unknown, inactive }.Should().AllSatisfy(result => result.Should().BeEquivalentTo(new { Allowed = false, Error = "No subjects found" }));
    }

    private static SqlOSFgaSubject Group(string id)
        => FgaTestModel.Existing(SqlOSFgaSubject.CreateGroup(id, id, null, null, $"grp::{id}", null, null, FgaActor.Host, Now));

    private static IReadOnlyList<ISqlOSDomainEvent> Events(ISqlOSAggregate aggregate) => aggregate.Events.Pending;

    private static async Task<List<string>> AuditAsync(TestSqlOSInMemoryDbContext context)
        => await context.Set<SqlOSAuditEvent>().OrderBy(row => row.IngestedAt).Select(row => row.EventType).ToListAsync();

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var context = new TestSqlOSInMemoryDbContext(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase($"fga-subjects-{Guid.NewGuid():N}")
            .Options);
        context.Set<SqlOSFgaSubjectType>().AddRange(FgaTestModel.BuiltInSubjectTypes());
        context.SaveChanges();
        return context;
    }
}

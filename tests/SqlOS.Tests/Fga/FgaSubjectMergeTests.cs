using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;
using SqlOS.Fga.Processes;
using SqlOS.Fga.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests.Fga;

/// <summary>
/// #448: the upgrade merges the subjects 7.x SCIM created for SqlOS users (<c>subj_…</c>) into the
/// subject keyed by the user ID, moving memberships and grants without duplicates.
/// </summary>
[TestClass]
public sealed class FgaSubjectMergeTests
{
    private static readonly DateTime Now = FgaTestModel.Now;

    [TestMethod]
    public async Task A_directory_user_s_subject_becomes_the_user_id_subject_with_its_groups_and_grants()
    {
        await using var context = CreateContext();
        var bob = await SeedAsync(context, users: ["Bob"]);
        var bobId = bob["Bob"];
        context.AddRange(
            FgaTestModel.Subject("subj_bob", "user", "Bob Builder", "org_acme", externalRef: bobId),
            FgaTestModel.User("fusr_bob", "subj_bob", "bob@acme.test"),
            FgaTestModel.Membership("subj_bob", "fgrp_eng"),
            FgaTestModel.Grant("grant_bob_root", "subj_bob", "root", "reader"),
            Link("link_bob", "conn_acme", bobId, "subj_bob"));
        await context.SaveChangesAsync();

        await new MergeDirectoryUserSubjects(context).ExecuteAsync(CancellationToken.None);

        var subject = await context.Set<SqlOSFgaSubject>().Include(s => s.User).SingleAsync(s => s.Id == bobId);
        subject.Should().BeEquivalentTo(new { DisplayName = "Bob Builder", OrganizationId = "org_acme", ExternalRef = bobId });
        subject.User.Should().BeEquivalentTo(new { Id = SqlOSFgaWrites.TypedRecordId("usr", bobId), Email = "bob@acme.test", IsActive = true });
        (await context.Set<SqlOSFgaUserGroupMembership>().Select(m => new { m.SubjectId, m.UserGroupId }).ToListAsync())
            .Should().Equal(new { SubjectId = bobId, UserGroupId = "fgrp_eng" });
        (await context.Set<SqlOSFgaGrant>().SingleAsync(g => g.Id == "grant_bob_root")).SubjectId.Should().Be(bobId);
        (await context.Set<SqlOSFgaSubject>().AnyAsync(s => s.Id == "subj_bob")).Should().BeFalse();
        (await context.Set<SqlOSFgaUser>().AnyAsync(u => u.Id == "fusr_bob")).Should().BeFalse();
        (await context.Set<SqlOSScimExternalId>().SingleAsync()).FgaSubjectId.Should().Be(bobId);
        (await Fga(context).CheckAccessAsync(bobId, "doc.read", "child")).Allowed.Should().BeTrue("the group's grant reaches the user ID");
        (await AuditAsync(context)).Should().Equal(
            "fga.subject.created system:upgrade",
            "fga.group.member_removed system:upgrade",
            "fga.group.member_added system:upgrade",
            "fga.subject.merged system:upgrade");
    }

    [TestMethod]
    public async Task Merging_into_a_host_provisioned_subject_keeps_it_and_drops_what_it_already_holds()
    {
        await using var context = CreateContext();
        var ann = (await SeedAsync(context, users: ["Ann"]))["Ann"];
        context.AddRange(
            FgaTestModel.Subject(ann, "user", "Ann (app)", "org_acme", externalRef: ann),
            FgaTestModel.User("usr::ann", ann, "ann@acme.test"),
            FgaTestModel.Membership(ann, "fgrp_eng"),
            FgaTestModel.Grant("grant_host_write", ann, "child", "writer"),
            FgaTestModel.Set(FgaTestModel.Subject("subj_ann_acme", "user", "Ann Archer", "org_acme", externalRef: ann), nameof(SqlOSFgaSubject.CreatedAt), Now.AddDays(-2)),
            FgaTestModel.User("fusr_ann_acme", "subj_ann_acme", "ann@acme.test"),
            FgaTestModel.Membership("subj_ann_acme", "fgrp_eng"),
            FgaTestModel.Grant("grant_acme_write", "subj_ann_acme", "child", "writer"),
            FgaTestModel.Grant("grant_acme_read", "subj_ann_acme", "root", "reader"),
            FgaTestModel.Set(FgaTestModel.Subject("subj_ann_beta", "user", "Ann Archer", "org_beta", externalRef: ann), nameof(SqlOSFgaSubject.CreatedAt), Now.AddDays(-1)),
            FgaTestModel.User("fusr_ann_beta", "subj_ann_beta", "ann@beta.test"),
            FgaTestModel.Membership("subj_ann_beta", "fgrp_beta"),
            FgaTestModel.Grant("grant_beta_read", "subj_ann_beta", "root", "reader"),
            Link("link_ann_acme", "conn_acme", ann, "subj_ann_acme"),
            Link("link_ann_beta", "conn_beta", ann, "subj_ann_beta"));
        await context.SaveChangesAsync();

        await new MergeDirectoryUserSubjects(context).ExecuteAsync(CancellationToken.None);

        (await context.Set<SqlOSFgaSubject>().Where(s => s.SubjectTypeId == "user").Select(s => s.Id).ToListAsync()).Should().Equal(ann);
        (await context.Set<SqlOSFgaSubject>().SingleAsync(s => s.Id == ann)).DisplayName.Should().Be("Ann (app)");
        (await context.Set<SqlOSFgaUserGroupMembership>().Where(m => m.SubjectId == ann).Select(m => m.UserGroupId).OrderBy(id => id).ToListAsync())
            .Should().Equal("fgrp_beta", "fgrp_eng");
        (await context.Set<SqlOSFgaGrant>().Where(g => g.SubjectId != "grp_eng").Select(g => new { g.Id, g.SubjectId }).OrderBy(g => g.Id).ToListAsync())
            .Should().Equal(new { Id = "grant_acme_read", SubjectId = ann }, new { Id = "grant_host_write", SubjectId = ann });
        (await context.Set<SqlOSScimExternalId>().Select(l => l.FgaSubjectId).ToListAsync()).Should().Equal(ann, ann);
        var revoked = await context.Set<SqlOSAuditEvent>().Where(row => row.EventType == "fga.grant.revoked").ToListAsync();
        revoked.Should().HaveCount(2).And.AllSatisfy(row => row.MetadataJson.Should().Contain("\"reason\":\"subject_merged\""));
        revoked.Should().ContainSingle(row => row.MetadataJson!.Contains("\"grantId\":\"grant_acme_write\""));
        revoked.Should().ContainSingle(row => row.MetadataJson!.Contains("\"grantId\":\"grant_beta_read\""));
        var merged = await context.Set<SqlOSAuditEvent>().Where(row => row.EventType == "fga.subject.merged").OrderBy(row => row.IngestedAt).ToListAsync();
        merged.Should().HaveCount(2).And.AllSatisfy(row => row.MetadataJson.Should().Contain($"\"subjectId\":\"{ann}\""));
        merged[0].MetadataJson.Should().Contain("\"mergedSubjectId\":\"subj_ann_acme\"")
            .And.Contain("\"movedGrantIds\":[\"grant_acme_read\"]")
            .And.Contain("\"droppedGrantIds\":[\"grant_acme_write\"]");
        merged[1].MetadataJson.Should().Contain("\"mergedSubjectId\":\"subj_ann_beta\"")
            .And.Contain("\"droppedGrantIds\":[\"grant_beta_read\"]");
    }

    [TestMethod]
    public async Task Only_subjects_a_scim_link_points_at_merge_and_a_second_run_changes_nothing()
    {
        await using var context = CreateContext();
        var users = await SeedAsync(context, users: ["Bob", "Carol"]);
        context.AddRange(
            FgaTestModel.Subject("subj_bob", "user", "Bob", "org_acme", externalRef: users["Bob"]),
            FgaTestModel.User("fusr_bob", "subj_bob"),
            Link("link_bob", "conn_acme", users["Bob"], "subj_bob"),
            // The host created Carol's subject with an ID of its own and checks access with it.
            FgaTestModel.Subject("subj_carol", "user", "Carol", "org_acme", externalRef: users["Carol"]),
            FgaTestModel.User("usr_carol_record", "subj_carol"));
        await context.SaveChangesAsync();
        var merge = new MergeDirectoryUserSubjects(context);

        await merge.ExecuteAsync(CancellationToken.None);
        var audited = await context.Set<SqlOSAuditEvent>().CountAsync();
        var needed = await merge.IsNeededAsync(CancellationToken.None);
        await merge.ExecuteAsync(CancellationToken.None);

        (await context.Set<SqlOSFgaSubject>().Where(s => s.SubjectTypeId == "user").Select(s => s.Id).OrderBy(id => id).ToListAsync())
            .Should().BeEquivalentTo([users["Bob"], "subj_carol"]);
        needed.Should().BeFalse();
        (await context.Set<SqlOSAuditEvent>().CountAsync()).Should().Be(audited);
    }

    [TestMethod]
    public async Task A_user_id_that_names_another_kind_of_subject_merges_nothing()
    {
        await using var context = CreateContext();
        var bob = (await SeedAsync(context, users: ["Bob"]))["Bob"];
        context.AddRange(
            FgaTestModel.Subject(bob, "agent", "An agent named like Bob"),
            FgaTestModel.Agent("agt_bob", bob),
            FgaTestModel.Subject("subj_bob", "user", "Bob", "org_acme", externalRef: bob),
            FgaTestModel.User("fusr_bob", "subj_bob"),
            Link("link_bob", "conn_acme", bob, "subj_bob"));
        await context.SaveChangesAsync();

        await new MergeDirectoryUserSubjects(context).ExecuteAsync(CancellationToken.None);

        (await context.Set<SqlOSFgaSubject>().AnyAsync(s => s.Id == "subj_bob")).Should().BeTrue();
        (await context.Set<SqlOSScimExternalId>().SingleAsync()).FgaSubjectId.Should().Be("subj_bob");
        (await context.Set<SqlOSAuditEvent>().CountAsync()).Should().Be(0);
    }

    private static SqlOSScimExternalId Link(string id, string connectionId, string userId, string subjectId)
        => new()
        {
            Id = id,
            ConnectionId = connectionId,
            ResourceType = "User",
            ExternalId = $"directory-{id}",
            EntityId = userId,
            FgaSubjectId = subjectId,
            CreatedAt = Now,
            UpdatedAt = Now,
            LastSyncedAt = Now
        };

    /// <summary>
    /// The workspace model (root and child, reader and writer), an Engineering group that reads the
    /// child, a Beta group, and a SqlOS user per name; returns the user IDs by name.
    /// </summary>
    private static async Task<Dictionary<string, string>> SeedAsync(TestSqlOSInMemoryDbContext context, string[] users)
    {
        var read = FgaTestModel.Permission("perm_read", "doc.read", resourceTypeId: "workspace");
        var write = FgaTestModel.Permission("perm_write", "doc.write", resourceTypeId: "workspace");
        context.AddRange(FgaTestModel.BuiltInSubjectTypes());
        context.AddRange(
            FgaTestModel.ResourceType("workspace"),
            FgaTestModel.Resource("root", "Root", "workspace"),
            FgaTestModel.Resource("child", "Child", "workspace", parentId: "root"),
            read,
            write,
            FgaTestModel.Role("reader", null, null, read),
            FgaTestModel.Role("writer", null, null, read, write),
            FgaTestModel.Subject("grp_eng", "group", "Engineering", "org_acme"),
            FgaTestModel.Group("fgrp_eng", "grp_eng", "Engineering"),
            FgaTestModel.Grant("grant_eng_read", "grp_eng", "child", "reader"),
            FgaTestModel.Subject("grp_beta", "group", "Beta", "org_beta"),
            FgaTestModel.Group("fgrp_beta", "grp_beta", "Beta"));
        var ids = new Dictionary<string, string>();
        foreach (var name in users)
        {
            var user = SqlOSUser.Register(name, Now);
            context.Add(user);
            ids[name] = user.Id;
        }

        await context.SaveChangesAsync();
        return ids;
    }

    private static SqlOSFgaAuthService Fga(TestSqlOSInMemoryDbContext context)
        => new(context, Options.Create(new SqlOSFgaOptions()), NullLogger<SqlOSFgaAuthService>.Instance);

    private static async Task<List<string>> AuditAsync(TestSqlOSInMemoryDbContext context)
        => await context.Set<SqlOSAuditEvent>()
            .OrderBy(row => row.IngestedAt)
            .Select(row => row.EventType + " " + row.ActorType + ":" + row.ActorId)
            .ToListAsync();

    private static TestSqlOSInMemoryDbContext CreateContext()
        => new(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase($"fga-merge-{Guid.NewGuid():N}")
            .Options);
}

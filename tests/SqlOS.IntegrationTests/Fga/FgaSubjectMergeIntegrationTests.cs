using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;
using SqlOS.Fga.Processes;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga;

/// <summary>
/// #448's upgrade on a real database: the startup merge moves 7.x SCIM subjects' memberships and
/// grants onto the user-ID subject under its foreign keys, and instances that start together merge
/// once.
/// </summary>
[TestClass]
public sealed class FgaSubjectMergeIntegrationTests
{
    [TestMethod]
    public async Task Startup_merges_directory_user_subjects_once_even_when_instances_start_together()
    {
        await using var context = await AspireFixture.CreateIsolatedAuthContextAsync("FgaMerge");
        await new SqlOSFgaSchemaInitializer(context, Options.Create(new SqlOSFgaOptions()), NullLogger<SqlOSFgaSchemaInitializer>.Instance)
            .EnsureSchemaAsync();
        var (bob, ann) = await SeedSevenXDirectoryAsync(context);
        var connectionString = context.Database.GetConnectionString()!;

        await using var first = CreateContext(connectionString);
        await using var second = CreateContext(connectionString);
        await Task.WhenAll(
            new MergeDirectoryUserSubjects(first).ExecuteAtStartupAsync(CancellationToken.None),
            new MergeDirectoryUserSubjects(second).ExecuteAtStartupAsync(CancellationToken.None));

        await using var read = CreateContext(connectionString);
        var userSubjects = await read.Set<SqlOSFgaSubject>().Where(s => s.SubjectTypeId == "user").Select(s => s.Id).ToListAsync();
        CollectionAssert.AreEquivalent(new[] { bob, ann }, userSubjects);
        var memberships = await read.Set<SqlOSFgaUserGroupMembership>().Select(m => m.SubjectId + "@" + m.UserGroupId).ToListAsync();
        CollectionAssert.AreEquivalent(new[] { $"{bob}@fgrp_eng", $"{ann}@fgrp_eng" }, memberships);
        var grants = await read.Set<SqlOSFgaGrant>().Where(g => g.SubjectId != "grp_eng").Select(g => g.Id + "@" + g.SubjectId).ToListAsync();
        CollectionAssert.AreEquivalent(new[] { $"grant_bob_root@{bob}", $"grant_ann_host@{ann}", $"grant_ann_root@{ann}" }, grants);
        var links = await read.Set<SqlOSScimExternalId>().Select(l => l.FgaSubjectId).ToListAsync();
        CollectionAssert.AreEquivalent(new[] { bob, ann }, links);
        Assert.AreEqual(2, await read.Set<SqlOSAuditEvent>().CountAsync(row => row.EventType == "fga.subject.merged"), "two instances started together merge once");
        Assert.AreEqual(1, await read.Set<SqlOSAuditEvent>().CountAsync(row => row.EventType == "fga.grant.revoked"));
        Assert.IsFalse(await new MergeDirectoryUserSubjects(read).IsNeededAsync(CancellationToken.None));
    }

    /// <summary>
    /// What 7.2.1 left behind: Bob only has the subject SCIM created; Ann has the host's user-ID
    /// subject too, which already holds the admin grant her SCIM subject holds and belongs to the
    /// same group.
    /// </summary>
    private static async Task<(string Bob, string Ann)> SeedSevenXDirectoryAsync(TestSqlOSDbContext context)
    {
        var crypto = new SqlOSCryptoService(context, Options.Create(AspireFixture.Options));
        var admin = new SqlOSAdminService(context, Options.Create(AspireFixture.Options), crypto);
        var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Acme", null));
        var connection = await admin.CreateScimConnectionAsync(new SqlOSCreateScimConnectionRequest(organization.Id, "Acme Directory"));
        var bob = SqlOSUser.Register("Bob", DateTime.UtcNow);
        var ann = SqlOSUser.Register("Ann", DateTime.UtcNow);
        context.AddRange(bob, ann);

        var read = FgaTestModel.Permission("perm_merge_read", "MERGE_READ", resourceTypeId: "merge_ws");
        var write = FgaTestModel.Permission("perm_merge_write", "MERGE_WRITE", resourceTypeId: "merge_ws");
        context.AddRange(FgaTestModel.BuiltInSubjectTypes());
        context.AddRange(
            FgaTestModel.ResourceType("merge_ws"),
            FgaTestModel.Resource("merge::root", "Root", "merge_ws"),
            FgaTestModel.Resource("merge::child", "Child", "merge_ws", parentId: "merge::root"),
            read,
            write,
            FgaTestModel.Role("merge_reader", null, null, read),
            FgaTestModel.Role("merge_admin", null, null, read, write),
            FgaTestModel.Subject("grp_eng", "group", "Engineering", organization.Id),
            FgaTestModel.Group("fgrp_eng", "grp_eng", "Engineering"),
            FgaTestModel.Grant("grant_eng_read", "grp_eng", "merge::child", "merge_reader"),
            FgaTestModel.Subject("subj_bob", "user", "Bob Builder", organization.Id, externalRef: bob.Id),
            FgaTestModel.User("fusr_bob", "subj_bob", "bob@acme.test"),
            FgaTestModel.Membership("subj_bob", "fgrp_eng"),
            FgaTestModel.Grant("grant_bob_root", "subj_bob", "merge::root", "merge_reader"),
            FgaTestModel.Subject(ann.Id, "user", "Ann (app)", organization.Id, externalRef: ann.Id),
            FgaTestModel.User("usr_ann_record", ann.Id, "ann@acme.test"),
            FgaTestModel.Membership(ann.Id, "fgrp_eng"),
            FgaTestModel.Grant("grant_ann_host", ann.Id, "merge::child", "merge_admin"),
            FgaTestModel.Subject("subj_ann", "user", "Ann Archer", organization.Id, externalRef: ann.Id),
            FgaTestModel.User("fusr_ann", "subj_ann", "ann@acme.test"),
            FgaTestModel.Membership("subj_ann", "fgrp_eng"),
            FgaTestModel.Grant("grant_ann_dup", "subj_ann", "merge::child", "merge_admin"),
            FgaTestModel.Grant("grant_ann_root", "subj_ann", "merge::root", "merge_reader"),
            Link(connection.ConnectionId, "directory-bob", bob.Id, "subj_bob"),
            Link(connection.ConnectionId, "directory-ann", ann.Id, "subj_ann"));
        await context.SaveChangesAsync();
        return (bob.Id, ann.Id);
    }

    private static SqlOSScimExternalId Link(string connectionId, string externalId, string userId, string subjectId)
        => new()
        {
            Id = $"scim_link_{externalId}",
            ConnectionId = connectionId,
            ResourceType = "User",
            ExternalId = externalId,
            EntityId = userId,
            FgaSubjectId = subjectId,
            OwnsUserLifecycle = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LastSyncedAt = DateTime.UtcNow
        };

    private static TestSqlOSDbContext CreateContext(string connectionString)
        => new(new DbContextOptionsBuilder<TestSqlOSDbContext>().UseTestProvider(connectionString).Options);
}

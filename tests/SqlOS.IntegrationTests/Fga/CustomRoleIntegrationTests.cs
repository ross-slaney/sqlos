using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests.Fga;

[TestClass]
public class CustomRoleIntegrationTests
{
    [TestMethod]
    public async Task Real_database_checks_and_page_nodes_follow_grants_edits_moves_and_retirement()
    {
        await using var db = await AspireFixture.CreateIsolatedAuthContextAsync("CustomRole");
        try
        {
            var options = new SqlOSFgaOptions();
            options.CustomRoles.ManagePermission = "MANAGE_ROLES";
            options.CustomRoles.OwnerResourceTypes.Add("organization");
            options.CustomRoles.IsEligibleRecipientAsync = (_, owner, subject, _) => Task.FromResult(owner == "acme" && subject == "jamie");
            var configured = Options.Create(options);
            var schema = new SqlOSFgaSchemaInitializer(db, configured, NullLogger<SqlOSFgaSchemaInitializer>.Instance);
            await schema.EnsureSchemaAsync();
            await schema.EnsureSchemaAsync(); // restart is a no-op
            await new SqlOSFgaFunctionInitializer(db, configured, NullLogger<SqlOSFgaFunctionInitializer>.Instance).EnsureFunctionsExistAsync();
            var seed = new SqlOSFgaSeedService(db, configured, NullLogger<SqlOSFgaSeedService>.Instance);
            await seed.SeedCoreAsync();
            options.Seed(s =>
            {
                s.ResourceType("organization", "Organization").ResourceType("checklist", "Checklist");
                s.Permission("MANAGE_ROLES", "Manage roles", "organization");
                s.Permission("ASSIGN", "Assign", "checklist").AllowInCustomRoles("ASSIGN");
                s.Permission("FINISH", "Finish", "checklist").AllowInCustomRoles("FINISH");
                s.Role("admin", "Administrator").Can("MANAGE_ROLES");
            });
            await seed.SeedAuthorizationDataAsync(options.StartupSeedData!);
            db.Set<SqlOSFgaResource>().AddRange(
                new() { Id = "acme", ParentId = "root", Name = "Acme", ResourceTypeId = "organization" },
                new() { Id = "other", ParentId = "root", Name = "Other", ResourceTypeId = "organization" },
                new() { Id = "work", ParentId = "acme", Name = "Work", ResourceTypeId = "checklist" });
            await db.ProvisionUserSubjectAsync("admin", "Administrator");
            await db.ProvisionUserSubjectAsync("jamie", "Jamie");
            await db.SaveChangesAsync();
            await db.GrantRoleAsync("admin", "acme", "admin");
            await db.SaveChangesAsync();
            var auth = new SqlOSFgaAuthService(db, configured, NullLogger<SqlOSFgaAuthService>.Instance);
            var crypto = new SqlOSCryptoService(db, Options.Create(AspireFixture.Options), AspireFixture.DataProtectionProvider);
            var service = new SqlOSCustomRoleService(db, auth, new SqlOSAuditLogService(db, crypto), configured);
            var role = await service.CreateAsync("admin", "acme", new("Relief", null, ["ASSIGN"]));
            role = await service.GrantAsync("admin", "acme", role.Id, role.Revision, "jamie", "work");
            var subjects = JsonSerializer.Serialize(new[] { "jamie" });
            (await auth.CheckAccessAsync("jamie", "ASSIGN", "work")).Allowed.Should().BeTrue();
            (await db.IsResourceAccessible("work", subjects, "ASSIGN").AnyAsync()).Should().BeTrue();
            (await db.PageNodes(subjects, "ASSIGN").Select(x => x.NodeId).ToListAsync()).Should().Equal("work");
            (await db.PageNodes(subjects, "FINISH").AnyAsync()).Should().BeFalse();
            role = await service.UpdateAsync("admin", "acme", role.Id, role.Revision, new("Relief", null, ["FINISH"]));
            (await db.IsResourceAccessible("work", subjects, "ASSIGN").AnyAsync()).Should().BeFalse();
            (await db.PageNodes(subjects, "FINISH").AnyAsync()).Should().BeTrue();
            (await db.Set<SqlOSFgaResource>().SingleAsync(x => x.Id == "work")).ParentId = "other";
            await db.SaveChangesAsync();
            (await db.IsResourceAccessible("work", subjects, "FINISH").AnyAsync()).Should().BeFalse();
            (await db.PageNodes(subjects, "FINISH").AnyAsync()).Should().BeFalse();
            // Both independent requests read the same revision. Exactly one may commit.
            var concurrent = await service.CreateAsync("admin", "acme", new("Concurrent", null, ["ASSIGN"]));
            async Task<string> TryUpdate(string permission)
            {
                await using var isolated = new TestSqlOSDbContext(new DbContextOptionsBuilder<TestSqlOSDbContext>()
                    .UseTestProvider(db.Database.GetConnectionString()!).Options);
                var isolatedAuth = new SqlOSFgaAuthService(isolated, configured, NullLogger<SqlOSFgaAuthService>.Instance);
                var isolatedCrypto = new SqlOSCryptoService(isolated, Options.Create(AspireFixture.Options), AspireFixture.DataProtectionProvider);
                var roles = new SqlOSCustomRoleService(isolated, isolatedAuth, new SqlOSAuditLogService(isolated, isolatedCrypto), configured);
                try
                {
                    await roles.UpdateAsync("admin", "acme", concurrent.Id, concurrent.Revision, new("Concurrent", null, [permission]));
                    return "committed";
                }
                catch (SqlOSCustomRoleException error) { return error.Code; }
            }
            var outcomes = await Task.WhenAll(TryUpdate("ASSIGN"), TryUpdate("FINISH"));
            outcomes.Should().BeEquivalentTo("committed", "stale_revision");
            (await db.Set<SqlOSAuditEvent>().CountAsync(x => x.Action == "fga.custom_role.updated")).Should().Be(2);
            var beforeFailure = await service.GetAsync("admin", "acme", concurrent.Id);
            var failing = new SqlOSCustomRoleService(db, auth, new RejectingAudit(), configured);
            await FluentActions.Invoking(() => failing.UpdateAsync("admin", "acme", concurrent.Id,
                beforeFailure.Revision, new("Must roll back", null, []))).Should().ThrowAsync<InvalidOperationException>();
            var afterFailure = await service.GetAsync("admin", "acme", concurrent.Id);
            afterFailure.Should().BeEquivalentTo(beforeFailure);
            (await db.Set<SqlOSAuditEvent>().CountAsync(x => x.Action == "fga.custom_role.updated")).Should().Be(2);
            // Host state and role revocation can participate in one owner-serialized unit of work.
            var composed = await service.CreateAsync("admin", "acme", new("Offboarding", null, ["ASSIGN"]));
            composed = await service.GrantAsync("admin", "acme", composed.Id, composed.Revision, "jamie", "acme");
            await FluentActions.Invoking(() => service.WithOwnerTransactionAsync<int>("admin", "acme", async () =>
            {
                (await db.Set<SqlOSFgaResource>().SingleAsync(x => x.Id == "acme")).Name = "Uncommitted host change";
                await db.SaveChangesAsync();
                await service.RevokeAsync("admin", "acme", composed.Id, composed.Revision, "jamie", "acme");
                throw new InvalidOperationException("Synthetic host failure");
            })).Should().ThrowAsync<InvalidOperationException>();
            (await db.Set<SqlOSFgaResource>().Where(x => x.Id == "acme").Select(x => x.Name).SingleAsync()).Should().Be("Acme");
            (await service.GetAsync("admin", "acme", composed.Id)).Revision.Should().Be(composed.Revision);
            (await service.ListGrantsAsync("admin", "acme", composed.Id)).Data.Should().ContainSingle();
            (await db.Set<SqlOSAuditEvent>().AnyAsync(x => x.Action == "fga.custom_role.revoked")).Should().BeFalse();
            await service.WithOwnerTransactionAsync("admin", "acme", () =>
                service.RevokeAsync("admin", "acme", composed.Id, composed.Revision, "jamie", "acme"));
            (await service.ListGrantsAsync("admin", "acme", composed.Id)).Data.Should().BeEmpty();
            // A host SaveChanges hook has already staged membership changes. It must share the
            // owner's lock before reading grants to reconcile, even though it cannot call the
            // interactive service's pending-change guard.
            await FluentActions.Invoking(() => SqlOSCustomRoleService.LockOwnerAsync(db, "acme"))
                .Should().ThrowAsync<SqlOSCustomRoleException>().Where(x => x.Code == "transaction_required");
            db.Set<SqlOSFgaSubject>().Add(new() { Id = "members", SubjectTypeId = "group", DisplayName = "Members" });
            db.Set<SqlOSFgaUserGroup>().Add(new() { Id = "members", SubjectId = "members", Name = "Members" });
            db.Set<SqlOSFgaUserGroupMembership>().Add(new() { UserGroupId = "members", SubjectId = "jamie" });
            await db.SaveChangesAsync();
            options.CustomRoles.IsEligibleRecipientAsync = (context, owner, subject, token) =>
                context.Set<SqlOSFgaUserGroupMembership>().AnyAsync(x => owner == "acme" && x.UserGroupId == "members" && x.SubjectId == subject, token);
            var lifecycleRole = await service.CreateAsync("admin", "acme", new("Lifecycle", null, ["ASSIGN"]));
            lifecycleRole = await service.GrantAsync("admin", "acme", lifecycleRole.Id, lifecycleRole.Revision, "jamie", "acme");
            await using (var lifecycle = await db.Database.BeginTransactionAsync())
            {
                db.Set<SqlOSFgaUserGroupMembership>().Remove(await db.Set<SqlOSFgaUserGroupMembership>().SingleAsync(x => x.UserGroupId == "members"));
                await SqlOSCustomRoleService.LockOwnerAsync(db, "acme"); // accepts staged host changes
                db.Set<SqlOSFgaGrant>().RemoveRange(await db.Set<SqlOSFgaGrant>()
                    .Where(x => x.SubjectId == "jamie" && x.Role!.OwnerResourceId == "acme").ToListAsync());
                var attempting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var concurrentGrant = Task.Run(async () =>
                {
                    await using var isolated = new TestSqlOSDbContext(new DbContextOptionsBuilder<TestSqlOSDbContext>()
                        .UseTestProvider(db.Database.GetConnectionString()!).Options);
                    var isolatedAuth = new SqlOSFgaAuthService(isolated, configured, NullLogger<SqlOSFgaAuthService>.Instance);
                    var isolatedCrypto = new SqlOSCryptoService(isolated, Options.Create(AspireFixture.Options), AspireFixture.DataProtectionProvider);
                    var roles = new SqlOSCustomRoleService(isolated, isolatedAuth, new SqlOSAuditLogService(isolated, isolatedCrypto), configured);
                    attempting.SetResult();
                    try
                    {
                        await roles.GrantAsync("admin", "acme", lifecycleRole.Id, lifecycleRole.Revision, "jamie", "acme");
                        return "committed";
                    }
                    catch (SqlOSCustomRoleException error) { return error.Code; }
                });
                await attempting.Task;
                (await Task.WhenAny(concurrentGrant, Task.Delay(300))).Should().NotBe(concurrentGrant);
                await db.SaveChangesAsync();
                await lifecycle.CommitAsync();
                (await concurrentGrant).Should().Be("recipient_denied");
            }
            (await db.Set<SqlOSFgaGrant>().AnyAsync(x => x.SubjectId == "jamie" && x.Role!.OwnerResourceId == "acme")).Should().BeFalse();
            await service.RetireAsync("admin", "acme", role.Id, role.Revision);
            (await db.Set<SqlOSFgaGrant>().AnyAsync(x => x.RoleId == role.Id)).Should().BeFalse();
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }
    private sealed class RejectingAudit : ISqlOSAuditLogService
    {
        public Task<SqlOSAuditLogRecordResult> RecordAsync(SqlOSAuditLogRecordRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Synthetic audit failure");
        public Task<SqlOSAuditLogListResult> ListAsync(SqlOSAuditLogListRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SqlOSAuditLogEvent?> GetAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SqlOSAuditLogCsvExportResult> ExportCsvAsync(SqlOSAuditLogListRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

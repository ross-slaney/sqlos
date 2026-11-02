using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;
namespace SqlOS.IntegrationTests;
[TestClass]
public sealed class IdentityDeletionIntegrationTests
{
    [TestMethod]
    public async Task RealSqlSchemaAndHostCleanup_AreAtomic_AndTerminalDeletionSurvivesReload()
    {
        await using var db = await AspireFixture.CreateIsolatedAuthContextAsync("IdentityDelete");
        try
        {
            db.Add(new SqlOSUser { Id = "u", DisplayName = "Alice", DefaultEmail = "alice@example.test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.Add(new SqlOSUserEmail { Id = "e", UserId = "u", Email = "alice@example.test", NormalizedEmail = "ALICE@EXAMPLE.TEST", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var veto = new SqlOSIdentityDeletionService(db, [new SaveThenVeto(db)]);
            await FluentActions.Invoking(() => veto.DeleteUserAsync("u", "u", true)).Should().ThrowAsync<InvalidOperationException>();
            db.ChangeTracker.Clear();
            (await db.Set<SqlOSUser>().SingleAsync()).DeletedAt.Should().BeNull();
            (await db.Set<SqlOSOrganization>().AnyAsync(x => x.Id == "marker")).Should().BeFalse();
            var service = new SqlOSIdentityDeletionService(db, []);
            await service.DeleteUserAsync("u", "u", true);
            db.ChangeTracker.Clear();
            var user = await db.Set<SqlOSUser>().SingleAsync();
            user.DeletedAt.Should().NotBeNull(); user.IsActive.Should().BeFalse(); user.DefaultEmail.Should().BeNull();
            db.Set<SqlOSUserEmail>().Should().BeEmpty();
            (await service.DeleteUserAsync("u", "u", true)).AlreadyDeleted.Should().BeTrue();
            db.Add(new SqlOSOrganization { Id = "org", Slug = "org", Name = "Example", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            await service.DeleteOrganizationAsync("org", "u", true);
            db.ChangeTracker.Clear();
            (await db.Set<SqlOSOrganization>().SingleAsync(x => x.Id == "org")).DeletedAt.Should().NotBeNull();
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }
    sealed class SaveThenVeto(TestSqlOSDbContext db) : ISqlOSIdentityDeletionParticipant
    {
        public async Task BeforeUserDeletionAsync(string id, CancellationToken ct)
        {
            db.Add(new SqlOSOrganization { Id = "marker", Name = "Host cleanup", Slug = "marker", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException("Host policy veto.");
        }
        public Task BeforeOrganizationDeletionAsync(string id, CancellationToken ct) => Task.CompletedTask;
    }
}

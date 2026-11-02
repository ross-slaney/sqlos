using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Tests.Infrastructure;
namespace SqlOS.Tests;
[TestClass]
public sealed class SqlOSIdentityDeletionTests
{
    static TestSqlOSInMemoryDbContext Context() => new(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    static async Task Seed(TestSqlOSInMemoryDbContext db)
    {
        db.AddRange(new SqlOSUser { Id = "u", DisplayName = "Alice", DefaultEmail = "alice@example.com" }, new SqlOSUser { Id = "other", DisplayName = "Bob" },
            new SqlOSOrganization { Id = "o", Name = "Acme", Slug = "acme" }, new SqlOSOrganization { Id = "other-org", Name = "Other", Slug = "other" },
            new SqlOSMembership { OrganizationId = "o", UserId = "u" }, new SqlOSMembership { OrganizationId = "other-org", UserId = "u" },
            new SqlOSUserEmail { Id = "email", UserId = "u", Email = "alice@example.com", NormalizedEmail = "ALICE@EXAMPLE.COM" },
            new SqlOSCredential { Id = "password", UserId = "u", SecretHash = "secret" });
        await db.SaveChangesAsync();
    }
    [TestMethod]
    public async Task AccountDeletion_PreservesPrincipalData_DisablesAdmission_AndIsIdempotent()
    {
        await using var db = Context(); await Seed(db);
        var service = new SqlOSIdentityDeletionService(db, []);
        await FluentActions.Invoking(() => service.DeleteUserAsync("u", "u", false)).Should().ThrowAsync<ArgumentException>();
        var result = await service.DeleteUserAsync("u", "u", true);
        result.AlreadyDeleted.Should().BeFalse();
        var user = await db.Set<SqlOSUser>().FindAsync("u");
        user!.IsActive.Should().BeFalse(); user.DisplayName.Should().Be("Alice"); user.DefaultEmail.Should().Be("alice@example.com"); user.DeletedAt.Should().NotBeNull();
        db.Set<SqlOSCredential>().Should().ContainSingle(); db.Set<SqlOSUserEmail>().Should().ContainSingle();
        db.Set<SqlOSMembership>().Count().Should().Be(2); db.Set<SqlOSMembership>().Any(x => x.IsActive).Should().BeFalse();
        (await db.Set<SqlOSUser>().FindAsync("other"))!.IsActive.Should().BeTrue();
        (await service.DeleteUserAsync("u", "u", true)).AlreadyDeleted.Should().BeTrue();
        db.Set<SqlOSAuditEvent>().Count(x => x.EventType == "identity.user.deleted").Should().Be(1);
    }
    [TestMethod]
    public async Task OrganizationDeletion_IsScoped_AndMembershipRemovalIsScoped()
    {
        await using var db = Context(); await Seed(db); var service = new SqlOSIdentityDeletionService(db, []);
        db.Add(new SqlOSInvitation { Id = "invite", OrganizationId = "o", InvitedEmail = "new@example.test", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();
        await service.RemoveMembershipAsync("o", "u", "u", true);
        (await db.Set<SqlOSMembership>().FindAsync("o", "u"))!.IsActive.Should().BeFalse();
        (await db.Set<SqlOSMembership>().FindAsync("other-org", "u"))!.IsActive.Should().BeTrue();
        await service.RemoveMembershipAsync("o", "u", "u", true);
        await service.RemoveMembershipAsync("missing", "u", "u", true);
        await service.DeleteOrganizationAsync("o", "operator", true);
        (await db.Set<SqlOSOrganization>().FindAsync("o"))!.Name.Should().Be("Acme");
        (await db.Set<SqlOSOrganization>().FindAsync("o"))!.Slug.Should().Be("acme");
        (await db.Set<SqlOSOrganization>().FindAsync("other-org"))!.IsActive.Should().BeTrue();
        db.Set<SqlOSMembership>().Count().Should().Be(2);
        (await service.DeleteOrganizationAsync("o", "operator", true)).AlreadyDeleted.Should().BeTrue();
        (await db.Set<SqlOSInvitation>().FindAsync("invite"))!.RevokedAt.Should().NotBeNull();
    }
    sealed class Veto : ISqlOSIdentityDeletionParticipant
    {
        public Task BeforeUserDeletionAsync(string id, CancellationToken ct) => throw new InvalidOperationException("Resolve shared ownership first.");
        public Task BeforeOrganizationDeletionAsync(string id, CancellationToken ct) => throw new InvalidOperationException("Cancel billing first.");
    }
    [TestMethod]
    public async Task HostPolicyVeto_ChangesNothing()
    {
        await using var db = Context(); await Seed(db); var service = new SqlOSIdentityDeletionService(db, [new Veto()]);
        await FluentActions.Invoking(() => service.DeleteUserAsync("u", "u", true)).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Invoking(() => service.DeleteOrganizationAsync("o", "u", true)).Should().ThrowAsync<InvalidOperationException>();
        (await db.Set<SqlOSUser>().FindAsync("u"))!.IsActive.Should().BeTrue();
        (await db.Set<SqlOSOrganization>().FindAsync("o"))!.IsActive.Should().BeTrue();
        await FluentActions.Invoking(() => service.DeleteUserAsync("missing", "u", true)).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Invoking(() => service.DeleteOrganizationAsync("missing", "u", true)).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Invoking(() => service.DeleteUserAsync("u", "", true)).Should().ThrowAsync<ArgumentException>();
    }
}

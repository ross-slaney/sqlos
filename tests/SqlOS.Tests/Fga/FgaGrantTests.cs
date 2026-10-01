using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Domain;
using SqlOS.Domain.Events;
using SqlOS.Extensions;
using SqlOS.Fga;
using SqlOS.Fga.Models;
using SqlOS.Fga.Processes;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests.Fga;

/// <summary>
/// Grants: created only with a <see cref="GrantAuthority"/>, unique per subject, role, resource and
/// window, and never across a tenant boundary through a tenant-controlled authority.
/// </summary>
[TestClass]
public sealed class FgaGrantTests
{
    private static readonly DateTime Now = FgaTestModel.Now;

    [TestMethod]
    public void A_grant_records_who_granted_it_and_its_window()
    {
        var subject = SqlOSFgaSubject.CreateUser("usr_alice", "Alice", "org_acme", null, "usr::alice", null, true, FgaActor.Host, Now);
        var window = TimeWindow.Between(Now, Now.AddDays(30));

        var grant = SqlOSFgaGrant.Create("grant_1", subject, FgaTestModel.Role("reader"), "doc", window, "Contractor", new GrantAuthority(FgaActor.Operator), Now);

        grant.Should().BeEquivalentTo(new { SubjectId = "usr_alice", RoleId = "reader", ResourceId = "doc", EffectiveFrom = Now, EffectiveTo = Now.AddDays(30), Description = "Contractor" });
        Events(grant).Should().Equal(new FgaGrantCreated("grant_1", "usr_alice", "reader", "doc", Now, Now.AddDays(30), "org_acme", FgaActor.Operator));
    }

    [TestMethod]
    public void A_tenant_controlled_authority_grants_only_the_subject_and_resource_it_was_proven_for()
    {
        var group = FgaTestModel.Subject("grp_support", "group");
        var otherGroup = FgaTestModel.Subject("grp_other", "group");
        var role = FgaTestModel.Role("store_manager");
        var authority = new GrantAuthority(FgaActor.Directory("scim_acme"), "grp_support", "org::a::store::42", "org::a");

        var inside = SqlOSFgaGrant.Create("grant_ok", group, role, "org::a::store::42", TimeWindow.Always, null, authority, Now);
        var otherTenant = () => SqlOSFgaGrant.Create("grant_b", group, role, "org::b::store::9001", TimeWindow.Always, null, authority, Now);
        var otherSubject = () => SqlOSFgaGrant.Create("grant_c", otherGroup, role, "org::a::store::42", TimeWindow.Always, null, authority, Now);

        inside.ResourceId.Should().Be("org::a::store::42");
        otherTenant.Should().Throw<InvalidOperationException>().WithMessage("*not subject 'grp_support' on resource 'org::b::store::9001'*");
        otherSubject.Should().Throw<InvalidOperationException>().WithMessage("*not subject 'grp_other' on resource 'org::a::store::42'*");
    }

    [TestMethod]
    public async Task The_scim_boundary_policy_produces_no_authority_for_another_tenant_s_resource()
    {
        await using var context = CreateContext();
        context.Set<SqlOSFgaResourceType>().Add(FgaTestModel.ResourceType("org"));
        context.Set<SqlOSFgaResource>().AddRange(
            FgaTestModel.Resource("org::a", "Acme", "org"),
            FgaTestModel.Resource("org::a::store::42", "Store 42", "org", parentId: "org::a"),
            FgaTestModel.Resource("org::b", "Beta", "org"),
            // Named like Acme's, but in Beta's subtree: the tree decides, not the name.
            FgaTestModel.Resource("org::a::store::9001", "Store 9001", "org", parentId: "org::b"));
        await context.SaveChangesAsync();
        var connection = ScimConnection("scim_acme", boundary: "org::a");
        var resolver = SqlOSScimGrantBoundaryPolicy.CreateResolver(context);

        var (inside, _) = await SqlOSScimGrantBoundaryPolicy.AuthorizeMappedGrantAsync(resolver, connection, "grp_support", "org::a::store::42", CancellationToken.None);
        var (lookalike, membership) = await SqlOSScimGrantBoundaryPolicy.AuthorizeMappedGrantAsync(resolver, connection, "grp_support", "org::a::store::9001", CancellationToken.None);
        var (unbounded, _) = await SqlOSScimGrantBoundaryPolicy.AuthorizeMappedGrantAsync(resolver, ScimConnection("scim_none", boundary: null), "grp_support", "org::a::store::42", CancellationToken.None);

        inside.Should().BeEquivalentTo(new { SubjectId = "grp_support", ResourceId = "org::a::store::42", BoundaryResourceId = "org::a", IsTenantControlled = true });
        inside!.Grantor.Should().Be(FgaActor.Directory("scim_acme"));
        lookalike.Should().BeNull();
        membership.Should().Be(SqlOSFgaSubtreeMembership.Outside);
        unbounded.Should().BeNull("a connection without a boundary can prove nothing inside one");
    }

    [TestMethod]
    public async Task The_operator_cannot_create_an_identical_grant_twice_but_may_add_another_window()
    {
        await using var context = await SeedAsync();
        var process = new GrantFgaRole(context);
        var operatorAuthority = new GrantAuthority(FgaActor.Operator);

        var first = await process.ExecuteAsync(new GrantFgaRoleCommand("usr_alice", "reader", "doc", null, null), operatorAuthority, CancellationToken.None);
        var duplicate = await process.ExecuteAsync(new GrantFgaRoleCommand("usr_alice", "reader", "doc", null, null), operatorAuthority, CancellationToken.None);
        var windowed = await process.ExecuteAsync(new GrantFgaRoleCommand("usr_alice", "reader", "doc", Now, null), operatorAuthority, CancellationToken.None);

        var granted = first.Should().BeOfType<GrantFgaRoleOutcome.Granted>().Subject;
        duplicate.Should().Be(new GrantFgaRoleOutcome.Refused(GrantRefusal.Duplicate, granted.Grant.Id));
        windowed.Should().BeOfType<GrantFgaRoleOutcome.Granted>();
        (await context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(2);
        (await context.Set<SqlOSAuditEvent>().Where(row => row.EventType == "fga.grant.created").Select(row => row.ActorType).ToListAsync())
            .Should().Equal("admin", "admin");
    }

    [TestMethod]
    public async Task The_operator_is_refused_an_unknown_subject_role_or_resource_without_a_write()
    {
        await using var context = await SeedAsync();
        var process = new GrantFgaRole(context);
        var authority = new GrantAuthority(FgaActor.Operator);

        (await process.ExecuteAsync(new("nobody", "reader", "doc", null, null), authority, CancellationToken.None))
            .Should().Be(new GrantFgaRoleOutcome.Refused(GrantRefusal.SubjectNotFound));
        (await process.ExecuteAsync(new("usr_alice", "no-role", "doc", null, null), authority, CancellationToken.None))
            .Should().Be(new GrantFgaRoleOutcome.Refused(GrantRefusal.RoleNotFound));
        (await process.ExecuteAsync(new("usr_alice", "reader", "no-doc", null, null), authority, CancellationToken.None))
            .Should().Be(new GrantFgaRoleOutcome.Refused(GrantRefusal.ResourceNotFound));
        (await context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task Host_grants_converge_per_window_and_keep_the_7_x_identifier_without_one()
    {
        await using var context = await SeedAsync();

        var permanent = await context.GrantRoleAsync("usr_alice", "doc", "reader");
        var again = await context.GrantRoleAsync("usr_alice", "doc", "reader", new SqlOSFgaGrantOptions { Description = "Owner" });
        var temporary = await context.GrantRoleAsync("usr_alice", "doc", "reader", new SqlOSFgaGrantOptions { EffectiveFrom = Now, EffectiveTo = Now.AddMonths(3) });
        await context.SaveChangesAsync();

        again.Should().BeSameAs(permanent);
        permanent.Description.Should().Be("Owner");
        permanent.Id.Should().Be(SevenXGrantId("usr_alice", "doc", "reader"));
        temporary.Id.Should().NotBe(permanent.Id);
        temporary.Window.Should().Be(TimeWindow.Between(Now, Now.AddMonths(3)));
        (await context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(2);
    }

    [TestMethod]
    public async Task Revoking_a_role_removes_it_in_every_window()
    {
        await using var context = await SeedAsync();
        await context.GrantRoleAsync("usr_alice", "doc", "reader");
        await context.GrantRoleAsync("usr_alice", "doc", "reader", new SqlOSFgaGrantOptions { EffectiveTo = Now.AddDays(1) });
        await context.SaveChangesAsync();

        await context.RevokeRoleAsync("usr_alice", "doc", "reader");
        await context.SaveChangesAsync();

        (await context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSAuditEvent>().CountAsync(row => row.EventType == "fga.grant.revoked" && row.ActorType == "application")).Should().Be(2);
    }

    [TestMethod]
    public async Task Deleting_a_resource_revokes_its_grants_with_the_reason()
    {
        await using var context = await SeedAsync();
        await context.GrantRoleAsync("usr_alice", "doc", "reader");
        await context.SaveChangesAsync();

        await context.DeleteResourceAsync("doc");
        await context.SaveChangesAsync();

        var revoked = await context.Set<SqlOSAuditEvent>().SingleAsync(row => row.EventType == "fga.grant.revoked");
        revoked.MetadataJson.Should().Contain("\"reason\":\"resource_deleted\"");
        (await context.Set<SqlOSAuditEvent>().CountAsync(row => row.EventType == "fga.resource.deleted")).Should().Be(1);
    }

    private static string SevenXGrantId(string subjectId, string resourceId, string roleId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{subjectId}\n{resourceId}\n{roleId}"));
        return $"grant::{Convert.ToHexString(bytes).ToLowerInvariant()[..32]}";
    }

    private static SqlOSScimConnection ScimConnection(string id, string? boundary)
        => new() { Id = id, OrganizationId = "org_acme", GrantBoundaryResourceId = boundary };

    private static IReadOnlyList<ISqlOSDomainEvent> Events(ISqlOSAggregate aggregate) => aggregate.Events.Pending;

    private static async Task<TestSqlOSInMemoryDbContext> SeedAsync()
    {
        var context = CreateContext();
        context.Set<SqlOSFgaSubjectType>().AddRange(FgaTestModel.BuiltInSubjectTypes());
        context.Set<SqlOSFgaResourceType>().Add(FgaTestModel.ResourceType("document"));
        context.Set<SqlOSFgaResource>().Add(FgaTestModel.Resource("doc", "Doc", "document"));
        context.Set<SqlOSFgaRole>().Add(FgaTestModel.Role("reader"));
        context.Set<SqlOSFgaSubject>().Add(FgaTestModel.Subject("usr_alice", "user", "Alice"));
        await context.SaveChangesAsync();
        return context;
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
        => new(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase($"fga-grants-{Guid.NewGuid():N}")
            .Options);
}

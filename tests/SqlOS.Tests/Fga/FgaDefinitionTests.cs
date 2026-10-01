using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;
using SqlOS.Domain.Events;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests.Fga;

/// <summary>The FGA definitions: resource types, permissions and roles, and their reconciliation.</summary>
[TestClass]
public sealed class FgaDefinitionTests
{
    [TestMethod]
    public void A_role_allows_a_permission_once_and_records_who_added_it()
    {
        var role = SqlOSFgaRole.Define("role_reader", "reader", "Reader", null, isVirtual: false, FgaActor.Startup);
        var read = FgaTestModel.Permission("perm_read", "doc.read");

        role.Allow(read, FgaActor.Startup);
        role.Allow(read, FgaActor.Startup);

        role.RolePermissions.Should().ContainSingle(assignment => assignment.PermissionId == "perm_read" && assignment.RoleId == "role_reader");
        Events(role).Should().Equal(
            new FgaRoleDefined("role_reader", "reader", false, FgaActor.Startup),
            new FgaRolePermissionAdded("role_reader", "perm_read", FgaActor.Startup));
    }

    [TestMethod]
    public void A_role_s_permissions_are_changed_only_through_the_role()
    {
        var role = FgaTestModel.Role("role_reader", "reader");

        var add = () => role.RolePermissions.Add(new SqlOSFgaRolePermission("role_reader", "perm_read"));

        add.Should().Throw<NotSupportedException>();
    }

    [TestMethod]
    public void Redefining_a_role_without_a_change_raises_nothing()
    {
        var role = FgaTestModel.Role("role_reader", "reader", "Reader");

        role.Redefine("reader", "Reader", null, isVirtual: false, FgaActor.Startup);
        role.Redefine("reader", "Readers", null, isVirtual: false, FgaActor.Startup);

        Events(role).Should().Equal(new FgaRoleRedefined("role_reader", "reader", false, FgaActor.Startup));
        role.Name.Should().Be("Readers");
    }

    [TestMethod]
    public void A_permission_key_longer_than_the_index_allows_is_refused()
    {
        var define = () => SqlOSFgaPermission.Define("perm", new string('k', SqlOSFgaPermission.MaxKeyLength + 1), "Too long", null, null);

        define.Should().Throw<InvalidOperationException>().WithMessage("FGA permission keys cannot exceed 450 characters.");
    }

    [TestMethod]
    public async Task Reconciling_a_seed_defines_the_model_in_one_save_and_audits_the_roles()
    {
        await using var context = CreateContext();
        var seed = new SqlOSFgaSeedBuilder()
            .ResourceType("workspace", "Workspace")
            .Permission("workspace.read", "Read workspace", "workspace")
            .Permission("workspace.write", "Write workspace", "workspace")
            .Role("workspace_reader", "Workspace reader").Can("workspace.read")
            .Role("workspace_admin", "Workspace admin").Can("workspace.read", "workspace.write")
            .Build();

        await Service(context).SeedStartupDataAsync(seed);

        context.SaveChangesAsyncCallCount.Should().Be(1);
        var roles = await context.Set<SqlOSFgaRole>().Include(role => role.RolePermissions).OrderBy(role => role.Id).ToListAsync();
        roles.Select(role => (role.Id, role.RolePermissions.Count)).Should().Equal(("workspace_admin", 2), ("workspace_reader", 1));
        var audit = await context.Set<SqlOSAuditEvent>().OrderBy(row => row.IngestedAt).ToListAsync();
        audit.Select(row => row.EventType).Should().Equal(
            "fga.role.created",
            "fga.role.created",
            "fga.role.permission_added",
            "fga.role.permission_added",
            "fga.role.permission_added");
        audit.Should().OnlyContain(row => row.Source == "fga" && row.ActorType == "system" && row.ActorId == "startup");
    }

    [TestMethod]
    public async Task Reconciling_the_same_seed_again_changes_and_audits_nothing()
    {
        await using var context = CreateContext();
        var seed = new SqlOSFgaSeedBuilder()
            .ResourceType("workspace", "Workspace")
            .Permission("workspace.read", "Read workspace", "workspace")
            .Role("workspace_reader", "Workspace reader").Can("workspace.read")
            .Build();
        await Service(context).SeedStartupDataAsync(seed);
        var rows = await context.Set<SqlOSAuditEvent>().CountAsync();

        await Service(context).SeedStartupDataAsync(seed);

        (await context.Set<SqlOSAuditEvent>().CountAsync()).Should().Be(rows);
        (await context.Set<SqlOSFgaRolePermission>().CountAsync()).Should().Be(1);
    }

    [TestMethod]
    public async Task Host_code_seeding_records_the_application_as_the_actor()
    {
        await using var context = CreateContext();

        await Service(context).SeedAuthorizationDataAsync(new SqlOSFgaSeedData
        {
            ResourceTypes = [new() { Id = "chain", Name = "Chain" }],
            Roles = [new() { Id = "role_admin", Key = "admin", Name = "Admin" }],
            Permissions = [new() { Id = "perm_view", Key = "CHAIN_VIEW", Name = "View chains" }],
            RolePermissions = [("admin", ["CHAIN_VIEW"])]
        });

        var actors = await context.Set<SqlOSAuditEvent>().Select(row => row.ActorType).Distinct().ToListAsync();
        actors.Should().Equal("application");
        (await context.Set<SqlOSFgaPermission>().SingleAsync()).ResourceTypeId.Should().BeNull("a permission without a type applies to every type");
    }

    [TestMethod]
    public async Task A_permission_for_an_undefined_resource_type_is_refused_before_anything_is_saved()
    {
        await using var context = CreateContext();

        var seed = () => Service(context).SeedAuthorizationDataAsync(new SqlOSFgaSeedData
        {
            Permissions = [new() { Id = "perm_view", Key = "CHAIN_VIEW", Name = "View chains", ResourceTypeId = "chain" }]
        });

        await seed.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("FGA permission 'CHAIN_VIEW' applies to resource type 'chain', which is not defined. Seed the resource type first.");
        context.SaveChangesAsyncCallCount.Should().Be(0);
    }

    [TestMethod]
    public async Task A_role_permission_naming_an_unknown_role_or_permission_is_skipped()
    {
        await using var context = CreateContext();

        await Service(context).SeedAuthorizationDataAsync(new SqlOSFgaSeedData
        {
            Roles = [new() { Id = "role_admin", Key = "admin", Name = "Admin" }],
            RolePermissions = [("admin", ["NO_SUCH_PERMISSION"]), ("no_such_role", ["NO_SUCH_PERMISSION"])]
        });

        (await context.Set<SqlOSFgaRolePermission>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSFgaRole>().SingleAsync()).Key.Should().Be("admin");
    }

    private static IReadOnlyList<ISqlOSDomainEvent> Events(ISqlOSAggregate aggregate)
        => aggregate.Events.Pending;

    private static SqlOSFgaSeedService Service(TestSqlOSInMemoryDbContext context)
        => new(context, Options.Create(new SqlOSFgaOptions()), NullLogger<SqlOSFgaSeedService>.Instance);

    private static TestSqlOSInMemoryDbContext CreateContext()
        => new(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase($"fga-definitions-{Guid.NewGuid():N}")
            .Options);
}

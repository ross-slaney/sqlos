using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSCustomRoleTests
{
    [TestMethod]
    public async Task The_three_roles_give_exact_authority_through_squads_and_direct_grants()
    {
        await using var f = await Fixture.Create();
        var supervisor = await f.Create("Cleaning supervisor", "assign", "due", "finish");
        supervisor = await f.Service.GrantAsync("admin", "acme", supervisor.Id, supervisor.Revision, "supervisors", "acme");
        var procedures = await f.Create("Procedure manager", "create_template", "edit_template", "repeat", "weekday");
        procedures = await f.Service.GrantAsync("admin", "acme", procedures.Id, procedures.Revision, "procedures", "acme");
        var relief = await f.Create("Relief supervisor", "assign", "due");
        await f.Service.GrantAsync("admin", "acme", relief.Id, relief.Revision, "jamie", "acme");
        foreach (var permission in new[] { "assign", "due", "finish", "execute", "edit", "create_template", "edit_template", "repeat", "weekday" })
        {
            var target = permission is "create_template" ? "acme" : permission is "edit_template" or "repeat" or "weekday" ? "template" : "work";
            (await f.Auth.CheckAccessAsync("sam", permission, target)).Allowed.Should().Be(new[] { "assign", "due", "finish" }.Contains(permission), $"supervisor / {permission}");
            (await f.Auth.CheckAccessAsync("pat", permission, target)).Allowed.Should().Be(new[] { "create_template", "edit_template", "repeat", "weekday" }.Contains(permission), $"procedure manager / {permission}");
            (await f.Auth.CheckAccessAsync("jamie", permission, target)).Allowed.Should().Be(new[] { "assign", "due" }.Contains(permission), $"relief / {permission}");
            (await f.Auth.CheckAccessAsync("cleaner", permission, target)).Allowed.Should().BeFalse($"ordinary cleaner / {permission}");
        }
        (await f.Db.Set<SqlOSAuditEvent>().CountAsync()).Should().Be(6);
        (await f.Db.Set<SqlOSFgaGrant>().CountAsync(x => x.RoleId == "owner")).Should().Be(0, "capability roles do not create responsibility grants");
    }

    [TestMethod]
    public async Task Editing_a_role_and_removing_group_membership_preserve_independent_authority()
    {
        await using var f = await Fixture.Create();
        var role = await f.Create("Supervisor", "assign", "finish");
        role = await f.Service.GrantAsync("admin", "acme", role.Id, role.Revision, "supervisors", "acme");
        f.Db.Set<SqlOSFgaGrant>().Add(new() { Id = "independent", SubjectId = "sam", ResourceId = "work", RoleId = "owner" });
        await f.Db.SaveChangesAsync();
        role = await f.Service.UpdateAsync("admin", "acme", role.Id, role.Revision, new("Supervisor", null, ["assign"]));
        (await f.Auth.CheckAccessAsync("sam", "finish", "work")).Allowed.Should().BeTrue("independent ownership survives role editing");
        f.Db.Set<SqlOSFgaUserGroupMembership>().Remove(await f.Db.Set<SqlOSFgaUserGroupMembership>().SingleAsync(x => x.SubjectId == "sam"));
        await f.Db.SaveChangesAsync();
        (await f.Auth.CheckAccessAsync("sam", "assign", "work")).Allowed.Should().BeFalse();
        (await f.Auth.CheckAccessAsync("sam", "finish", "work")).Allowed.Should().BeTrue();
    }

    [TestMethod]
    public async Task Foreign_actors_recipients_targets_and_role_ids_are_refused()
    {
        await using var f = await Fixture.Create();
        var role = await f.Create("Supervisor", "assign");
        await Refused(() => f.Service.CreateAsync("stranger", "acme", new("Forged", null, ["assign"])), "forbidden");
        await Refused(() => f.Service.GrantAsync("admin", "acme", role.Id, role.Revision, "stranger", "acme"), "recipient_denied");
        await Refused(() => f.Service.GrantAsync("admin", "acme", role.Id, role.Revision, "jamie", "other"), "outside_owner");
        await Refused(() => f.Service.GrantAsync("admin", "acme", role.Id, role.Revision, "jamie", "root"), "outside_owner");
        await Refused(() => f.Service.GetAsync("stranger", "other", role.Id), "not_found");
        (await f.Db.Set<SqlOSFgaGrant>().CountAsync(x => x.RoleId == role.Id)).Should().Be(0);
    }

    [TestMethod]
    public async Task Built_ins_reserved_permissions_and_management_privilege_cannot_be_edited_into_customer_roles()
    {
        await using var f = await Fixture.Create();
        await Refused(() => f.Service.CreateAsync("admin", "acme", new("Escalate", null, ["manage"])), "permission_denied");
        await Refused(() => f.Service.CreateAsync("admin", "acme", new("Escalate", null, ["unknown"])), "permission_denied");
        await Refused(() => f.Service.UpdateAsync("admin", "acme", "admin-role", 1, new("Forged", null, ["assign"])), "not_found");
        (await f.Db.Set<SqlOSAuditEvent>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task Stale_edits_fail_and_retired_roles_cannot_be_resurrected()
    {
        await using var f = await Fixture.Create();
        var role = await f.Create("Supervisor", "assign");
        role = await f.Service.GrantAsync("admin", "acme", role.Id, role.Revision, "jamie", "work");
        await Refused(() => f.Service.UpdateAsync("admin", "acme", role.Id, 1, new("Outdated", null, ["finish"])), "stale_revision");
        role = await f.Service.RetireAsync("admin", "acme", role.Id, role.Revision);
        (await f.Db.Set<SqlOSFgaGrant>().AnyAsync(x => x.RoleId == role.Id)).Should().BeFalse();
        await Refused(() => f.Service.GrantAsync("admin", "acme", role.Id, role.Revision, "jamie", "work"), "archived");
        // Even a raw/legacy writer cannot make an archived definition effective.
        f.Db.Set<SqlOSFgaGrant>().Add(new() { Id = "rogue", RoleId = role.Id, ResourceId = "acme", SubjectId = "jamie" });
        await f.Db.SaveChangesAsync();
        (await f.Auth.CheckAccessAsync("jamie", "assign", "work")).Allowed.Should().BeFalse();
    }

    [TestMethod]
    public async Task A_raw_foreign_grant_and_a_moved_resource_fail_closed_in_checks_and_traces()
    {
        await using var f = await Fixture.Create();
        var role = await f.Create("Supervisor", "assign");
        role = await f.Service.GrantAsync("admin", "acme", role.Id, role.Revision, "jamie", "work");
        f.Db.Set<SqlOSFgaGrant>().Add(new() { Id = "foreign", RoleId = role.Id, ResourceId = "other", SubjectId = "jamie" });
        (await f.Db.Set<SqlOSFgaResource>().SingleAsync(x => x.Id == "work")).ParentId = "other";
        await f.Db.SaveChangesAsync();
        (await f.Auth.CheckAccessAsync("jamie", "assign", "work")).Allowed.Should().BeFalse();
        (await f.Auth.TraceResourceAccessAsync("jamie", "work", "assign")).AccessGranted.Should().BeFalse();
    }

    [TestMethod]
    public async Task Startup_leaves_customer_definitions_alone_and_rejects_ownership_collisions()
    {
        await using var f = await Fixture.Create();
        var role = await f.Create("Supervisor", "assign");
        await f.Seed.SeedAuthorizationDataAsync(new() { Roles = [new() { Id = "owner", Key = "owner", Name = "Updated built-in" }] });
        (await f.Service.GetAsync("admin", "acme", role.Id)).PermissionKeys.Should().Equal("assign");
        Func<Task> collision = () => f.Seed.SeedAuthorizationDataAsync(new() { Roles = [new() { Id = role.Id, Key = "overwrite", Name = "Overwrite" }] });
        await collision.Should().ThrowAsync<InvalidOperationException>();
    }

    [TestMethod]
    public async Task Existing_grant_helper_cannot_bypass_customer_recipient_policy()
    {
        await using var f = await Fixture.Create();
        var role = await f.Create("Supervisor", "assign");
        Func<Task> bypass = async () => await f.Db.GrantRoleAsync("stranger", "acme", role.Id);
        await bypass.Should().ThrowAsync<InvalidOperationException>().WithMessage("*SqlOSCustomRoleService*");
    }

    [TestMethod]
    public async Task Permission_opt_in_survives_builder_cloning_and_can_be_withdrawn()
    {
        await using var f = await Fixture.Create();
        var options = new SqlOSFgaOptions();
        options.Seed(s => s.Permission("assign", "Assign", "checklist").AllowInCustomRoles("assign"));
        options.Seed(s => s.Role("extra", "Extra"));
        options.StartupSeedData!.Permissions!.Single().AllowInCustomRoles.Should().BeTrue();
        var role = await f.Create("Supervisor", "assign");
        await f.Service.GrantAsync("admin", "acme", role.Id, role.Revision, "jamie", "acme");
        await f.Seed.SeedAuthorizationDataAsync(new() { Permissions = [new() { Id = "assign", Key = "assign", Name = "Assign", ResourceTypeId = "checklist" }] });
        (await f.Auth.CheckAccessAsync("jamie", "assign", "work")).Allowed.Should().BeFalse();
    }

    private static async Task Refused(Func<Task> operation, string code)
    {
        var thrown = await operation.Should().ThrowAsync<SqlOSCustomRoleException>();
        thrown.Which.Code.Should().Be(code);
    }

    [TestMethod]
    public async Task Dashboard_and_public_service_share_definition_grant_revision_and_retirement_rules()
    {
        await using var host = await ControlPlaneParityHarness.CreateAsync();
        await using var f = await Fixture.Create(host.Context, host.FgaOptions);
        var javascript = await host.Client.GetStringAsync("/sqlos/admin/fga/app.js");
        javascript.Should().Contain("apiPost('custom-roles', body)").And.Contain("revision: role.revision")
            .And.Contain("customRoleAttribute").And.Contain("openCustomRoleRetirement");
        const string route = "/sqlos/admin/fga/api/custom-roles";
        var created = await host.PostDashboardAsync(route, new { action = "create", ownerResourceId = "acme", name = "Relief", permissionKeys = new[] { "assign", "due" } });
        var id = created.GetProperty("id").GetString()!;
        var role = await f.Service.GetAsync("admin", "acme", id);
        role.PermissionKeys.Should().BeEquivalentTo("assign", "due");
        role = await f.Service.GrantAsync("admin", "acme", id, role.Revision, "jamie", "acme");
        (await f.Auth.CheckAccessAsync("jamie", "assign", "work")).Allowed.Should().BeTrue();
        using var stale = await host.Client.PostAsJsonAsync(route, new { action = "retire", ownerResourceId = "acme", id, revision = 1 });
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var denied = await host.Client.PostAsJsonAsync(route, new { action = "grant", ownerResourceId = "acme", id, revision = role.Revision, subjectId = "jamie", resourceId = "other" });
        denied.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var bypass = await host.Client.PostAsJsonAsync("/sqlos/admin/fga/api/grants", new { subjectId = "jamie", resourceId = "acme", roleId = id });
        bypass.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var retired = await host.PostDashboardAsync(route, new { action = "retire", ownerResourceId = "acme", id, revision = role.Revision });
        retired.GetProperty("archivedAt").ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Null);
        f.Db.ChangeTracker.Clear();
        (await f.Auth.CheckAccessAsync("jamie", "assign", "work")).Allowed.Should().BeFalse();
        (await f.Db.Set<SqlOSAuditEvent>().CountAsync()).Should().Be(3);
    }

    [TestMethod]
    public async Task Customer_role_list_is_bounded_owner_scoped_and_preserves_duplicate_display_names()
    {
        await using var f = await Fixture.Create();
        await f.Create("Relief", "assign");
        await f.Create("Relief", "due");
        var first = await f.Service.ListAsync("admin", "acme", pageSize: 1);
        first.Data.Should().HaveCount(1);
        first.HasNextPage.Should().BeTrue();
        var second = await f.Service.ListAsync("admin", "acme", first.NextCursor, 1);
        second.Data.Should().HaveCount(1);
        second.Data[0].Id.Should().NotBe(first.Data[0].Id);
        second.HasNextPage.Should().BeFalse();
        (await f.Service.ListAsync("stranger", "other")).Data.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Grant_listing_and_revocation_require_the_owner_and_preserve_independent_grants()
    {
        await using var f = await Fixture.Create();
        var role = await f.Create("Relief", "assign");
        role = await f.Service.GrantAsync("admin", "acme", role.Id, role.Revision, "jamie", "acme");
        await f.Db.GrantRoleAsync("jamie", "work", "owner");
        await f.Db.SaveChangesAsync();
        var grants = await f.Service.ListGrantsAsync("admin", "acme", role.Id, pageSize: 1);
        grants.Data.Should().ContainSingle().Which.SubjectId.Should().Be("jamie");
        await FluentActions.Invoking(() => f.Service.ListGrantsAsync("stranger", "other", role.Id))
            .Should().ThrowAsync<SqlOSCustomRoleException>();
        await FluentActions.Invoking(() => f.Db.RevokeRoleAsync("jamie", "acme", role.Id))
            .Should().ThrowAsync<InvalidOperationException>();
        role = await f.Service.RevokeAsync("admin", "acme", role.Id, role.Revision, "jamie", "acme");
        (await f.Auth.CheckAccessAsync("jamie", "assign", "work")).Allowed.Should().BeFalse();
        (await f.Auth.CheckAccessAsync("jamie", "finish", "work")).Allowed.Should().BeTrue();
        (await f.Service.ListGrantsAsync("admin", "acme", role.Id)).Data.Should().BeEmpty();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestSqlOSInMemoryDbContext Db { get; init; }
        public required SqlOSCustomRoleService Service { get; init; }
        public required SqlOSFgaAuthService Auth { get; init; }
        public required SqlOSFgaSeedService Seed { get; init; }
        public Task<SqlOSCustomRoleView> Create(string name, params string[] permissions) => Service.CreateAsync("admin", "acme", new(name, null, permissions));
        public ValueTask DisposeAsync() => Db.DisposeAsync();

        public static async Task<Fixture> Create(TestSqlOSInMemoryDbContext? database = null, SqlOSFgaOptions? configured = null)
        {
            var db = database ?? new TestSqlOSInMemoryDbContext(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var options = configured ?? new SqlOSFgaOptions();
            options.CustomRoles.ManagePermission = "manage";
            options.CustomRoles.OwnerResourceTypes.Add("organization");
            options.CustomRoles.IsEligibleRecipientAsync = (_, owner, subject, _) => Task.FromResult(owner == "acme" && new[] { "jamie", "supervisors", "procedures" }.Contains(subject));
            var seed = new SqlOSFgaSeedService(db, Options.Create(options), NullLogger<SqlOSFgaSeedService>.Instance);
            await seed.SeedCoreAsync();
            options.Seed(s =>
            {
                s.ResourceType("organization", "Organization").ResourceType("checklist", "Checklist").ResourceType("template", "Template");
                foreach (var key in new[] { "assign", "due", "finish", "execute", "edit" }) s.Permission(key, key, "checklist").AllowInCustomRoles(key);
                foreach (var key in new[] { "edit_template", "repeat", "weekday" }) s.Permission(key, key, "template").AllowInCustomRoles(key);
                s.Permission("create_template", "Create template", "organization").AllowInCustomRoles("create_template");
                s.Permission("manage", "Manage roles", "organization");
                s.Role("admin-role", "Admin").Can("manage");
                s.Role("owner", "Owner").Can("finish");
            });
            await seed.SeedAuthorizationDataAsync(options.StartupSeedData!);
            foreach (var (id, parent, type) in new[] { ("acme", "root", "organization"), ("other", "root", "organization"), ("work", "acme", "checklist"), ("template", "acme", "template") })
                db.Set<SqlOSFgaResource>().Add(new() { Id = id, ParentId = parent, Name = id, ResourceTypeId = type });
            foreach (var user in new[] { "admin", "stranger", "sam", "pat", "jamie", "cleaner" }) await db.ProvisionUserSubjectAsync(user, user);
            foreach (var (group, user) in new[] { ("supervisors", "sam"), ("procedures", "pat") })
            {
                db.Set<SqlOSFgaSubject>().Add(new() { Id = group, SubjectTypeId = "group", DisplayName = group });
                db.Set<SqlOSFgaUserGroup>().Add(new() { Id = group, SubjectId = group, Name = group });
                db.Set<SqlOSFgaUserGroupMembership>().Add(new() { UserGroupId = group, SubjectId = user });
            }
            db.Set<SqlOSFgaGrant>().AddRange(new SqlOSFgaGrant { Id = "admin", RoleId = "admin-role", SubjectId = "admin", ResourceId = "acme" }, new SqlOSFgaGrant { Id = "other-admin", RoleId = "admin-role", SubjectId = "stranger", ResourceId = "other" });
            await db.SaveChangesAsync();
            var auth = new SqlOSFgaAuthService(db, Options.Create(options), NullLogger<SqlOSFgaAuthService>.Instance);
            var crypto = new SqlOSCryptoService(db, Options.Create(new SqlOSAuthServerOptions()), new EphemeralDataProtectionProvider());
            return new() { Db = db, Auth = auth, Seed = seed, Service = new(db, auth, new SqlOSAuditLogService(db, crypto), Options.Create(options)) };
        }
    }
}

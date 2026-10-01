using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.Extensions;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

/// <summary>
/// FGA roles and grants through each control plane that writes them, judged by a real access check.
/// </summary>
/// <remarks>
/// The authorization model (resource types, permissions, roles) is application code: the code seed
/// (<c>options.Fga.Seed</c>, reconciled at startup) and the host service
/// (<c>SqlOSFgaSeedService.SeedAuthorizationDataAsync</c>) write it, and the dashboard only reads
/// it, refusing schema writes (<c>SqlOSFgaDashboardSchemaWriteTests</c>). Grants to users are
/// application data, not configuration, so they have no code seed: host code grants through
/// <c>GrantRoleAsync</c> and an operator through the dashboard route. Machine-client grants, which
/// are configuration, have all three planes and are covered by
/// <c>MachineClient_CodeServiceAndDashboard_ShareOwnershipAndEmergencyDisableSemantics</c>.
/// </remarks>
[TestClass]
public sealed class SqlOSFgaControlPlaneParityTests
{
    private const string FgaApi = "/sqlos/admin/fga/api";
    private const string Root = "workspace::root";
    private const string Child = "workspace::child";
    private const string Ada = "usr_ada";

    [TestMethod]
    public async Task FgaRoles_CodeSeedAndHostService_DefineTheSameModel_ThatTheDashboardShowsAndChecksEnforce()
    {
        await using var code = await ControlPlaneParityHarness.CreateAsync();
        await using var service = await ControlPlaneParityHarness.CreateAsync();

        await Seeds(code).SeedStartupDataAsync(Model());
        await Seeds(service).SeedAuthorizationDataAsync(Model());
        foreach (var harness in new[] { code, service })
        {
            await SeedTreeAndAdaAsync(harness);
            await harness.Context.GrantRoleAsync(Ada, Root, "reader");
            await harness.Context.SaveChangesAsync();
        }

        var projections = new[] { await ProjectModelAsync(code), await ProjectModelAsync(service) };
        projections[1].Should().Equal(projections[0]);
        projections[0].Should().Contain("reader: Reader [workspace.read]").And.Contain("editor: Editor [workspace.read, workspace.write]");
        foreach (var harness in new[] { code, service })
        {
            (await harness.Fga.CheckAccessAsync(Ada, "workspace.read", Child)).Allowed.Should().BeTrue("the reader role allows read and inherits down the tree");
            (await harness.Fga.CheckAccessAsync(Ada, "workspace.write", Child)).Allowed.Should().BeFalse();
        }

        // The writer differs, and the audit says which: startup reconciliation or host code.
        (await RoleAuditActorsAsync(code)).Should().Equal("system:startup", "system:startup");
        (await RoleAuditActorsAsync(service)).Should().Equal("application:", "application:");

        // Reconciliation is idempotent: the same model again changes nothing.
        var audited = await code.Context.Set<SqlOSAuditEvent>().CountAsync();
        await Seeds(code).SeedStartupDataAsync(Model());
        (await code.Context.Set<SqlOSAuditEvent>().CountAsync()).Should().Be(audited);

        // Invalid input fails the same way on both planes.
        var invalid = Model();
        invalid.Permissions!.Add(new SqlOSFgaPermissionSeed { Id = "perm_ship", Key = "workspace.ship", Name = "Ship", ResourceTypeId = "rocket" });
        var codeInvalid = () => Seeds(code).SeedStartupDataAsync(invalid);
        var serviceInvalid = () => Seeds(service).SeedAuthorizationDataAsync(invalid);
        var expected = "FGA permission 'workspace.ship' applies to resource type 'rocket', which is not defined. Seed the resource type first.";
        await codeInvalid.Should().ThrowAsync<InvalidOperationException>().WithMessage(expected);
        await serviceInvalid.Should().ThrowAsync<InvalidOperationException>().WithMessage(expected);

        // The dashboard reads the model but does not write it.
        using var refused = await code.Client.PostAsJsonAsync($"{FgaApi}/roles", new { key = "auditor", name = "Auditor" });
        refused.IsSuccessStatusCode.Should().BeFalse();
        (await code.Context.Set<SqlOSFgaRole>().CountAsync()).Should().Be(2);
    }

    [TestMethod]
    public async Task FgaGrants_HostApiAndDashboardRoute_GrantRefuseAndRevokeEquivalently()
    {
        await using var host = await ControlPlaneParityHarness.CreateAsync();
        await using var dashboard = await ControlPlaneParityHarness.CreateAsync();
        foreach (var harness in new[] { host, dashboard })
        {
            await Seeds(harness).SeedAuthorizationDataAsync(Model());
            await SeedTreeAndAdaAsync(harness);
        }

        // The same intent: Ada reads the root workspace.
        await host.Context.GrantRoleAsync(Ada, Root, "reader");
        await host.Context.SaveChangesAsync();
        using var created = await PostGrantAsync(dashboard, "role_reader");
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var grants = new[] { await ProjectGrantsAsync(host), await ProjectGrantsAsync(dashboard) };
        grants[1].Should().Equal(grants[0]);
        grants[0].Should().Equal($"Reader on {Root} (always)");
        foreach (var harness in new[] { host, dashboard })
        {
            (await harness.Fga.CheckAccessAsync(Ada, "workspace.read", Child)).Allowed.Should().BeTrue();
            (await harness.Fga.CheckAccessAsync(Ada, "workspace.write", Child)).Allowed.Should().BeFalse();
        }

        // An identical grant is not created twice: host code converges on the existing one, the
        // dashboard refuses with it.
        var again = await host.Context.GrantRoleAsync(Ada, Root, "reader");
        await host.Context.SaveChangesAsync();
        using var duplicate = await PostGrantAsync(dashboard, "role_reader");
        again.Id.Should().Be((await host.Context.Set<SqlOSFgaGrant>().SingleAsync()).Id);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("grantId").GetString()
            .Should().Be((await dashboard.Context.Set<SqlOSFgaGrant>().AsNoTracking().SingleAsync()).Id);

        // An unknown role is refused without a write on both planes.
        var hostUnknown = () => host.Context.GrantRoleAsync(Ada, Root, "role_nope");
        await hostUnknown.Should().ThrowAsync<InvalidOperationException>().WithMessage("FGA role 'role_nope' was not found.");
        using var dashboardUnknown = await PostGrantAsync(dashboard, "role_nope");
        dashboardUnknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await dashboardUnknown.Content.ReadAsStringAsync()).Should().Be("""{"error":"Role not found"}""");

        // Revoking takes the access away on both planes.
        await host.Context.RevokeRoleAsync(Ada, Root, "reader");
        await host.Context.SaveChangesAsync();
        var dashboardGrantId = (await dashboard.Context.Set<SqlOSFgaGrant>().AsNoTracking().SingleAsync()).Id;
        using var revoked = await dashboard.Client.DeleteAsync($"{FgaApi}/grants/{dashboardGrantId}");
        revoked.StatusCode.Should().Be(HttpStatusCode.NoContent);
        foreach (var harness in new[] { host, dashboard })
        {
            (await harness.Context.Set<SqlOSFgaGrant>().AsNoTracking().CountAsync()).Should().Be(0);
            (await harness.Fga.CheckAccessAsync(Ada, "workspace.read", Child)).Allowed.Should().BeFalse();
        }

        // Both are audited alike, naming who granted: host code or the operator.
        (await GrantAuditAsync(host)).Should().Equal("fga.grant.created application:", "fga.grant.revoked application:");
        (await GrantAuditAsync(dashboard)).Should().Equal("fga.grant.created admin:", "fga.grant.revoked admin:");
    }

    [TestMethod]
    public void FgaDashboardJavascript_PostsTheGrantRoutePayloadAndShowsItsError()
    {
        var javascript = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "SqlOS", "Fga", "Dashboard", "wwwroot", "app.js"));

        javascript.Should().Contain("apiPost('grants', { subjectId: subj, roleId: role, resourceId: res })");
        javascript.Should().Contain("throw new Error(err.error || 'Failed to create grant');");
        javascript.Should().Contain("apiDelete(`grants/${grantId}`)");
        javascript.Should().Contain("`${basePath}/api/trace`");
    }

    private static SqlOSFgaSeedService Seeds(ControlPlaneParityHarness harness)
        => new(harness.Context, Microsoft.Extensions.Options.Options.Create(new SqlOS.Fga.Configuration.SqlOSFgaOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<SqlOSFgaSeedService>.Instance);

    private static SqlOSFgaSeedData Model()
        => new()
        {
            ResourceTypes = [new SqlOSFgaResourceTypeSeed { Id = "workspace", Name = "Workspace" }],
            Permissions =
            [
                new SqlOSFgaPermissionSeed { Id = "perm_read", Key = "workspace.read", Name = "Read", ResourceTypeId = "workspace" },
                new SqlOSFgaPermissionSeed { Id = "perm_write", Key = "workspace.write", Name = "Write", ResourceTypeId = "workspace" }
            ],
            Roles =
            [
                new SqlOSFgaRoleSeed { Id = "role_reader", Key = "reader", Name = "Reader" },
                new SqlOSFgaRoleSeed { Id = "role_editor", Key = "editor", Name = "Editor" }
            ],
            RolePermissions = [("reader", ["workspace.read"]), ("editor", ["workspace.read", "workspace.write"])]
        };

    private static async Task SeedTreeAndAdaAsync(ControlPlaneParityHarness harness)
    {
        await harness.Context.CreateResourceWithIdAsync(Root, "workspace", "Root");
        await harness.Context.CreateResourceWithIdAsync(Child, "workspace", "Child", Root);
        await harness.Context.ProvisionUserSubjectAsync(Ada, "Ada", "ada@example.test");
        await harness.Context.SaveChangesAsync();
    }

    /// <summary>The model as the dashboard lists it: each role with its permission keys.</summary>
    private static async Task<List<string>> ProjectModelAsync(ControlPlaneParityHarness harness)
    {
        var roles = await harness.Client.GetFromJsonAsync<JsonElement>($"{FgaApi}/roles");
        var projection = new List<string>();
        foreach (var role in roles.GetProperty("data").EnumerateArray())
        {
            var permissions = await harness.Client.GetFromJsonAsync<JsonElement>($"{FgaApi}/roles/{role.GetProperty("id").GetString()}/permissions");
            var keys = permissions.EnumerateArray().Select(permission => permission.GetProperty("key").GetString()).Order(StringComparer.Ordinal);
            projection.Add($"{role.GetProperty("key").GetString()}: {role.GetProperty("name").GetString()} [{string.Join(", ", keys)}]");
        }

        return projection;
    }

    /// <summary>Ada's grants as the dashboard lists them, without generated IDs or timestamps.</summary>
    private static async Task<List<string>> ProjectGrantsAsync(ControlPlaneParityHarness harness)
    {
        var grants = await harness.Client.GetFromJsonAsync<JsonElement>($"{FgaApi}/subjects/{Ada}/grants");
        return grants.GetProperty("data").EnumerateArray()
            .Select(grant => $"{grant.GetProperty("roleName").GetString()} on {grant.GetProperty("resourceId").GetString()} "
                + (grant.GetProperty("effectiveFrom").ValueKind == JsonValueKind.Null && grant.GetProperty("effectiveTo").ValueKind == JsonValueKind.Null ? "(always)" : "(windowed)"))
            .ToList();
    }

    private static Task<HttpResponseMessage> PostGrantAsync(ControlPlaneParityHarness harness, string roleId)
        => harness.Client.PostAsJsonAsync($"{FgaApi}/grants", new { subjectId = Ada, roleId, resourceId = Root });

    private static async Task<List<string>> RoleAuditActorsAsync(ControlPlaneParityHarness harness)
        => await harness.Context.Set<SqlOSAuditEvent>().AsNoTracking()
            .Where(row => row.EventType == "fga.role.created")
            .Select(row => row.ActorType + ":" + row.ActorId)
            .ToListAsync();

    private static async Task<List<string>> GrantAuditAsync(ControlPlaneParityHarness harness)
        => await harness.Context.Set<SqlOSAuditEvent>().AsNoTracking()
            .Where(row => row.Source == "fga" && row.EventType.StartsWith("fga.grant."))
            .OrderBy(row => row.IngestedAt)
            .Select(row => row.EventType + " " + row.ActorType + ":" + row.ActorId)
            .ToListAsync();

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SqlOS.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found.");
    }
}

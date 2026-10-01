using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using SqlOS.IntegrationTests.Infrastructure;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// Issue #421 on a real database: a tenant's IdP chooses SCIM group names, so a mapped grant
/// must stay inside the connection's grant boundary subtree of the FGA resource tree.
/// </summary>
public sealed partial class ScimProtocolIntegrationTests
{
    private const string BoundaryTenantRoot = "tenant_a_root";
    private const string OtherTenantRoot = "tenant_b_root";
    private const string BoundaryStore42 = "store::42";
    private const string OtherTenantStore9001 = "store::9001";
    private const string StoreManagersPattern = "^Store-(?<storeId>[^-]+)-Managers$";

    [TestMethod]
    public async Task CrossTenantGroupName_OverScimHttp_CreatesNoGrantAndTheUserIsDenied()
    {
        await using var server = await ScimSqlServer.CreateAsync("ScimBoundaryAttack");
        await SeedTwoTenantStoresAsync(server);
        await BoundConnectionToTenantRootAsync(server, BoundaryTenantRoot);
        await CreateMappingAsync(server, "store::{storeId}");
        var ada = await server.CreateUserAsync("boundary-ada");

        var attackGroupId = await server.CreateGroupAsync("Store-9001-Managers", ada.Id);
        var legitimateGroupId = await server.CreateGroupAsync("Store-42-Managers", ada.Id);

        await using var verify = server.Services.CreateAsyncScope();
        var context = verify.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await context.Set<SqlOSFgaGrant>().Select(x => x.ResourceId).ToListAsync())
            .Should().Equal([BoundaryStore42], "only the store inside org A's subtree may be granted");
        var violation = await context.Set<SqlOSScimSyncEvent>().SingleAsync(x => x.Action == "scim.grant.outside_boundary");
        violation.Result.Should().Be("failed");
        violation.ResourceId.Should().Be(attackGroupId);
        var data = JsonNode.Parse(violation.DataJson!)!.AsObject();
        data["resourceId"]!.GetValue<string>().Should().Be(OtherTenantStore9001);
        data["grantBoundaryResourceId"]!.GetValue<string>().Should().Be(BoundaryTenantRoot);
        (await context.Set<SqlOSAuditEvent>().CountAsync(x => x.Action == "scim.grant.outside_boundary" && x.Source == "scim"))
            .Should().Be(1);

        var subjectId = await FgaSubjectIdAsync(context, ada.Id);
        var fga = verify.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>();
        (await fga.CheckAccessAsync(subjectId, "STORE_MANAGE", OtherTenantStore9001)).Allowed.Should().BeFalse();
        (await fga.CheckAccessAsync(subjectId, "STORE_MANAGE", BoundaryStore42)).Allowed.Should().BeTrue();
        var explanation = await fga.TraceResourceAccessAsync(subjectId, BoundaryStore42, "STORE_MANAGE");
        explanation.GrantsUsed.Should().Contain(grant =>
            grant.ViaGroupName == "Store-42-Managers" && grant.RoleKey == "store_manager");
        legitimateGroupId.Should().NotBe(attackGroupId);
    }

    [TestMethod]
    public async Task GroupRenamedToAnotherTenantsStore_OverScimHttp_RevokesTheExistingGrant()
    {
        await using var server = await ScimSqlServer.CreateAsync("ScimBoundaryRename");
        await SeedTwoTenantStoresAsync(server);
        await BoundConnectionToTenantRootAsync(server, BoundaryTenantRoot);
        await CreateMappingAsync(server, "store::{storeId}");
        var ada = await server.CreateUserAsync("rename-ada");
        var groupId = await server.CreateGroupAsync("Store-42-Managers", ada.Id);

        using var rename = await server.SendAsync(HttpMethod.Patch, $"/Groups/{groupId}", new JsonObject
        {
            ["schemas"] = new JsonArray(PatchSchema),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "replace",
                ["path"] = "displayName",
                ["value"] = "Store-9001-Managers"
            })
        });
        rename.StatusCode.Should().Be(HttpStatusCode.NoContent, await rename.Content.ReadAsStringAsync());

        await using var verify = server.Services.CreateAsyncScope();
        var context = verify.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSScimManagedGrant>().SingleAsync()).RevokedAt.Should().NotBeNull();
        (await context.Set<SqlOSScimSyncEvent>().CountAsync(x => x.Action == "scim.grant.outside_boundary")).Should().Be(1);
        var fga = verify.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>();
        (await fga.CheckAccessAsync(await FgaSubjectIdAsync(context, ada.Id), "STORE_MANAGE", OtherTenantStore9001)).Allowed.Should().BeFalse();
    }

    [TestMethod]
    public async Task UpgradedConnectionWithoutBoundary_RevokesAPlantedGrantOnTheNextPush()
    {
        await using var server = await ScimSqlServer.CreateAsync("ScimBoundaryUpgrade");
        await SeedTwoTenantStoresAsync(server);
        var ada = await server.CreateUserAsync("upgrade-ada");
        var groupId = await server.CreateGroupAsync("Store-9001-Managers", ada.Id);
        string subjectId;
        await using (var setup = server.Services.CreateAsyncScope())
        {
            // Rows exactly as SqlOS 7.2.0 stored them before connections had a boundary: an
            // unbounded template mapping and the cross-tenant grant it created.
            var context = setup.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            var group = await context.Set<SqlOSFgaUserGroup>().SingleAsync(x => x.Id == groupId);
            var link = await context.Set<SqlOSScimExternalId>().SingleAsync(x => x.EntityId == groupId);
            context.Set<SqlOSScimGroupMapping>().Add(new SqlOSScimGroupMapping
            {
                Id = "scmap_legacy",
                ConnectionId = server.ConnectionId,
                Source = SqlOSScimSources.Dashboard,
                MatchType = SqlOSScimGroupMappingMatchTypes.Pattern,
                GroupPattern = StoreManagersPattern,
                RoleKey = "store_manager",
                ResourceIdTemplate = "store::{storeId}",
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            context.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant
            {
                Id = "grant_planted",
                SubjectId = group.SubjectId,
                ResourceId = OtherTenantStore9001,
                RoleId = "role_store_manager",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
            context.Set<SqlOSScimManagedGrant>().Add(new SqlOSScimManagedGrant
            {
                Id = "scgrant_planted",
                ConnectionId = server.ConnectionId,
                MappingId = "scmap_legacy",
                GroupExternalId = link.ExternalId!,
                FgaGroupId = groupId,
                FgaGroupSubjectId = group.SubjectId,
                GrantId = "grant_planted",
                RoleId = "role_store_manager",
                ResourceId = OtherTenantStore9001,
                CreatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
            subjectId = await FgaSubjectIdAsync(context, ada.Id);
            (await setup.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>()
                    .CheckAccessAsync(subjectId, "STORE_MANAGE", OtherTenantStore9001))
                .Allowed.Should().BeTrue("the fixture reproduces a grant planted before the upgrade");
        }

        using var push = await server.SendAsync(HttpMethod.Patch, $"/Groups/{groupId}", new JsonObject
        {
            ["schemas"] = new JsonArray(PatchSchema),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "add",
                ["path"] = "members",
                ["value"] = new JsonArray(new JsonObject { ["value"] = ada.Id })
            })
        });
        push.StatusCode.Should().Be(HttpStatusCode.NoContent, await push.Content.ReadAsStringAsync());

        await using var verify = server.Services.CreateAsyncScope();
        var verifyContext = verify.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await verifyContext.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
        (await verifyContext.Set<SqlOSScimManagedGrant>().SingleAsync()).RevokedAt.Should().NotBeNull();
        var missing = await verifyContext.Set<SqlOSScimSyncEvent>().SingleAsync(x => x.Action == "scim.grant.boundary_missing");
        JsonNode.Parse(missing.DataJson!)!["reason"]!.GetValue<string>().Should().Be("boundary_not_configured");
        (await verifyContext.Set<SqlOSAuditEvent>().CountAsync(x => x.Action == "scim.grant.boundary_missing")).Should().Be(1);
        (await verify.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>()
                .CheckAccessAsync(subjectId, "STORE_MANAGE", OtherTenantStore9001))
            .Allowed.Should().BeFalse();
    }

    [TestMethod]
    public async Task ChangingTheBoundary_RevokesOnlyManagedGrantsOutsideIt()
    {
        await using var server = await ScimSqlServer.CreateAsync("ScimBoundaryChange");
        await SeedTwoTenantStoresAsync(server);
        await using (var setup = server.Services.CreateAsyncScope())
        {
            var context = setup.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            AddResource(context, "tenant_a_west", BoundaryTenantRoot, "region");
            AddResource(context, "store::7", "tenant_a_west", "store");
            await context.SaveChangesAsync();
        }
        await BoundConnectionToTenantRootAsync(server, BoundaryTenantRoot);
        await CreateMappingAsync(server, "store::{storeId}");
        await server.CreateGroupAsync("Store-42-Managers");
        await server.CreateGroupAsync("Store-7-Managers");

        await using (var change = server.Services.CreateAsyncScope())
        {
            var admin = change.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var missing = () => admin.UpdateScimConnectionAsync(server.ConnectionId, new SqlOSUpdateScimConnectionRequest("Integration Directory", true)
            {
                GrantBoundaryResourceId = "tenant_missing"
            });
            (await missing.Should().ThrowAsync<SqlOSScimGrantBoundaryException>())
                .Which.Error.Should().Be(SqlOSScimGrantBoundaryErrors.BoundaryNotFound);
            await admin.UpdateScimConnectionAsync(server.ConnectionId, new SqlOSUpdateScimConnectionRequest("Integration Directory", true)
            {
                GrantBoundaryResourceId = "tenant_a_west"
            });
        }

        await using var verify = server.Services.CreateAsyncScope();
        var verifyContext = verify.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await verifyContext.Set<SqlOSFgaGrant>().Select(x => x.ResourceId).ToListAsync()).Should().Equal(["store::7"]);
        (await verifyContext.Set<SqlOSScimManagedGrant>().SingleAsync(x => x.RevokedAt != null)).ResourceId.Should().Be(BoundaryStore42);
        (await verifyContext.Set<SqlOSScimConnection>().SingleAsync(x => x.Id == server.ConnectionId)).GrantBoundaryResourceId.Should().Be("tenant_a_west");
        // The fixture's first boundary on an existing connection is also an audited change.
        var boundaryChanges = await verifyContext.Set<SqlOSAuditEvent>()
            .Where(x => x.Action == "scim.connection.grant_boundary_changed")
            .Select(x => x.MetadataJson!)
            .ToListAsync();
        boundaryChanges.Should().HaveCount(2);
        var moved = JsonNode.Parse(boundaryChanges.Single(json => json.Contains("tenant_a_west")))!;
        moved["previousGrantBoundaryResourceId"]!.GetValue<string>().Should().Be(BoundaryTenantRoot);
        moved["grantBoundaryResourceId"]!.GetValue<string>().Should().Be("tenant_a_west");
        moved["revokedManagedGrantCount"]!.GetValue<int>().Should().Be(1);
        var revoked = await verifyContext.Set<SqlOSScimSyncEvent>().SingleAsync(x => x.Action == "scim.grant.revoked");
        JsonNode.Parse(revoked.DataJson!)!["reason"]!.GetValue<string>().Should().Be("grant_boundary_changed");
    }

    [TestMethod]
    public async Task HierarchyCycleOrOverDeepChain_FailsClosedOnTheRealProvider()
    {
        await using var server = await ScimSqlServer.CreateAsync("ScimBoundaryHierarchy");
        await SeedTwoTenantStoresAsync(server);
        await using (var setup = server.Services.CreateAsyncScope())
        {
            var context = setup.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            AddResource(context, "loop_1", null, "region");
            AddResource(context, "loop_2", "loop_1", "region");
            AddResource(context, "store::cycle", "loop_2", "store");
            var parent = BoundaryTenantRoot;
            for (var level = 1; level <= 11; level++)
            {
                AddResource(context, $"deep_{level}", parent, "region");
                parent = $"deep_{level}";
            }
            AddResource(context, "store::deep", parent, "store");
            await context.SaveChangesAsync();
            // Foreign keys allow a cycle once both rows exist.
            (await context.Set<SqlOSFgaResource>().SingleAsync(x => x.Id == "loop_1")).ParentId = "loop_2";
            await context.SaveChangesAsync();
        }
        await BoundConnectionToTenantRootAsync(server, BoundaryTenantRoot);
        await CreateMappingAsync(server, "store::{storeId}");

        await server.CreateGroupAsync("Store-cycle-Managers");
        await server.CreateGroupAsync("Store-deep-Managers");

        await using var verify = server.Services.CreateAsyncScope();
        var verifyContext = verify.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await verifyContext.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
        var reasons = (await verifyContext.Set<SqlOSScimSyncEvent>()
                .Where(x => x.Action == "scim.grant.outside_boundary")
                .Select(x => x.DataJson)
                .ToListAsync())
            .Select(json => JsonNode.Parse(json!)!["reason"]!.GetValue<string>())
            .Order(StringComparer.Ordinal)
            .ToList();
        reasons.Should().Equal("hierarchy_cycle", "hierarchy_too_deep");
    }

    // Every mapped grant must stay inside the connection's grant boundary, so fixtures that
    // exercise mapped grants model the organization's root resource and bound the connection to it.
    private static async Task BoundConnectionToTenantRootAsync(ScimSqlServer server, string tenantRootId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        if (!await context.Set<SqlOSFgaResourceType>().AnyAsync(x => x.Id == "organization"))
        {
            context.Set<SqlOSFgaResourceType>().Add(FgaTestModel.ResourceType("organization", "Organization"));
        }
        if (!await context.Set<SqlOSFgaResource>().AnyAsync(x => x.Id == tenantRootId))
        {
            AddResource(context, tenantRootId, "root", "organization");
        }
        await context.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<SqlOSAdminService>().UpdateScimConnectionAsync(
            server.ConnectionId,
            new SqlOSUpdateScimConnectionRequest("Integration Directory", true) { GrantBoundaryResourceId = tenantRootId });
    }

    private static async Task SeedTwoTenantStoresAsync(ScimSqlServer server)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        context.Set<SqlOSFgaResourceType>().AddRange(
            FgaTestModel.ResourceType("organization", "Organization"),
            FgaTestModel.ResourceType("region", "Region"),
            FgaTestModel.ResourceType("store", "Store"));
        AddResource(context, BoundaryTenantRoot, "root", "organization");
        AddResource(context, BoundaryStore42, BoundaryTenantRoot, "store");
        AddResource(context, OtherTenantRoot, "root", "organization");
        AddResource(context, OtherTenantStore9001, OtherTenantRoot, "store");
        context.Set<SqlOSFgaPermission>().Add(FgaTestModel.Permission("perm_store_manage", "STORE_MANAGE", name: "Manage store", resourceTypeId: "store"));
        context.Set<SqlOSFgaRole>().Add(FgaTestModel.Role("role_store_manager", key: "store_manager", name: "Store manager"));
        await context.SaveChangesAsync();
        context.Set<SqlOSFgaRolePermission>().Add(new SqlOSFgaRolePermission("role_store_manager", "perm_store_manage"));
        await context.SaveChangesAsync();
    }

    private static async Task CreateMappingAsync(ScimSqlServer server, string resourceIdTemplate)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SqlOSAdminService>().CreateScimGroupMappingAsync(
            server.ConnectionId,
            new SqlOSCreateScimGroupMappingRequest(
                SqlOSScimGroupMappingMatchTypes.Pattern,
                GroupDisplayName: null,
                GroupExternalId: null,
                GroupPattern: StoreManagersPattern,
                RoleKey: "store_manager",
                ResourceId: null,
                ResourceIdTemplate: resourceIdTemplate));
    }

    private static async Task<string> FgaSubjectIdAsync(TestSqlOSDbContext context, string userId)
        => (await context.Set<SqlOSScimExternalId>().SingleAsync(x => x.ResourceType == "User" && x.EntityId == userId)).FgaSubjectId!;

    private static void AddResource(TestSqlOSDbContext context, string id, string? parentId, string resourceTypeId)
        => context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
        {
            Id = id,
            ParentId = parentId,
            ResourceTypeId = resourceTypeId,
            Name = id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
}

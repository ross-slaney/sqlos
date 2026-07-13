using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Fga.Models;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class ScimProtocolIntegrationTests
{
    private const string UserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";
    private const string GroupSchema = "urn:ietf:params:scim:schemas:core:2.0:Group";
    private const string PatchSchema = "urn:ietf:params:scim:api:messages:2.0:PatchOp";

    [TestMethod]
    public async Task UserDeprovisioning_IsTenantScoped_AndRevokesOnlyThatOrganizationsSessions()
    {
        await using var server = await ScimSqlServer.CreateAsync("ScimTenant");
        string sharedUserId;
        string otherOrganizationId;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            var shared = await admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Existing Shared User",
                "shared.user@example.test",
                Password: null));
            var other = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Other Tenant", null));
            await admin.CreateMembershipAsync(server.OrganizationId, new SqlOSCreateMembershipRequest(shared.Id, "member"));
            await admin.CreateMembershipAsync(other.Id, new SqlOSCreateMembershipRequest(shared.Id, "member"));
            context.Set<SqlOSSession>().AddRange(
                NewSession("sess_scim_tenant", shared.Id, server.OrganizationId),
                NewSession("sess_other_tenant", shared.Id, other.Id));
            await context.SaveChangesAsync();
            sharedUserId = shared.Id;
            otherOrganizationId = other.Id;
        }

        using var create = await server.SendAsync(HttpMethod.Post, "/Users", UserPayload(
            "entra-shared-user",
            "shared.user@entra.example",
            "shared.user@example.test",
            "Directory Display Name"));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadObjectAsync(create))["id"]!.GetValue<string>().Should().Be(sharedUserId);

        using var deactivate = await server.SendAsync(HttpMethod.Patch, $"/Users/{sharedUserId}", new JsonObject
        {
            ["schemas"] = new JsonArray(PatchSchema),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "replace",
                ["value"] = new JsonObject { ["active"] = false }
            })
        });
        deactivate.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var verify = server.Services.CreateAsyncScope();
        var verifyContext = verify.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await verifyContext.Set<SqlOSUser>().SingleAsync(x => x.Id == sharedUserId)).IsActive.Should().BeTrue();
        (await verifyContext.Set<SqlOSMembership>().SingleAsync(x => x.UserId == sharedUserId && x.OrganizationId == server.OrganizationId)).IsActive.Should().BeFalse();
        (await verifyContext.Set<SqlOSMembership>().SingleAsync(x => x.UserId == sharedUserId && x.OrganizationId == otherOrganizationId)).IsActive.Should().BeTrue();
        (await verifyContext.Set<SqlOSSession>().SingleAsync(x => x.Id == "sess_scim_tenant")).RevokedAt.Should().NotBeNull();
        (await verifyContext.Set<SqlOSSession>().SingleAsync(x => x.Id == "sess_other_tenant")).RevokedAt.Should().BeNull();
        (await verifyContext.Set<SqlOSScimExternalId>().SingleAsync(x => x.EntityId == sharedUserId)).UserName.Should().Be("shared.user@entra.example");
        (await verifyContext.Set<SqlOSUser>().SingleAsync(x => x.Id == sharedUserId)).DisplayName.Should().Be("Existing Shared User");
    }

    [TestMethod]
    public async Task EmailLinking_PreservesStandaloneUserProfileAndGlobalSuspension()
    {
        await using var server = await ScimSqlServer.CreateAsync("ScimStandaloneIsolation");
        string activeUserId;
        string suspendedUserId;
        await using (var setup = server.Services.CreateAsyncScope())
        {
            var admin = setup.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var context = setup.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            activeUserId = (await admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Locally Managed Active User",
                "standalone.active@example.test",
                Password: null))).Id;
            var suspended = await admin.CreateUserAsync(new SqlOSCreateUserRequest(
                "Security Suspended User",
                "standalone.suspended@example.test",
                Password: null));
            suspended.IsActive = false;
            suspendedUserId = suspended.Id;
            await context.SaveChangesAsync();
        }

        using var activeCreate = await server.SendAsync(HttpMethod.Post, "/Users", UserPayload(
            "entra-standalone-active",
            "entra.active@example.test",
            "standalone.active@example.test",
            "Provider Overwrite Attempt"));
        activeCreate.StatusCode.Should().Be(HttpStatusCode.Created, await activeCreate.Content.ReadAsStringAsync());
        (await ReadObjectAsync(activeCreate))["id"]!.GetValue<string>().Should().Be(activeUserId);

        using var suspendedCreate = await server.SendAsync(HttpMethod.Post, "/Users", UserPayload(
            "entra-standalone-suspended",
            "entra.suspended@example.test",
            "standalone.suspended@example.test",
            "Provider Reactivation Attempt"));
        suspendedCreate.StatusCode.Should().Be(HttpStatusCode.Created, await suspendedCreate.Content.ReadAsStringAsync());
        (await ReadObjectAsync(suspendedCreate))["id"]!.GetValue<string>().Should().Be(suspendedUserId);

        using var deactivate = await server.SendAsync(HttpMethod.Patch, $"/Users/{activeUserId}", new JsonObject
        {
            ["schemas"] = new JsonArray(PatchSchema),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "replace",
                ["path"] = "active",
                ["value"] = false
            })
        });
        deactivate.StatusCode.Should().Be(HttpStatusCode.OK, await deactivate.Content.ReadAsStringAsync());

        await using var verify = server.Services.CreateAsyncScope();
        var verifyContext = verify.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        var activeUser = await verifyContext.Set<SqlOSUser>().SingleAsync(item => item.Id == activeUserId);
        var suspendedUser = await verifyContext.Set<SqlOSUser>().SingleAsync(item => item.Id == suspendedUserId);
        activeUser.DisplayName.Should().Be("Locally Managed Active User");
        activeUser.IsActive.Should().BeTrue("an organization directory must not disable a pre-existing standalone account");
        suspendedUser.DisplayName.Should().Be("Security Suspended User");
        suspendedUser.IsActive.Should().BeFalse("SCIM must not override a global security suspension");
        (await verifyContext.Set<SqlOSScimExternalId>().Where(item => item.ResourceType == "User").ToListAsync())
            .Should().OnlyContain(item => !item.OwnsUserLifecycle);
        (await verifyContext.Set<SqlOSFgaSubject>().Where(item =>
                item.SubjectTypeId == "user" && (item.ExternalRef == activeUserId || item.ExternalRef == suspendedUserId)).ToListAsync())
            .Should().OnlyContain(item => item.OrganizationId == server.OrganizationId);
    }

    [TestMethod]
    public async Task ScimOwnedUserLifecycle_AggregatesAcrossOrganizationsRegardlessOfUpdateOrder()
    {
        await using var server = await ScimSqlServer.CreateAsync("ScimLifecycleAggregate");
        using var ownerCreate = await server.SendAsync(HttpMethod.Post, "/Users", UserPayload(
            "owner-directory-user",
            "owner-directory-user@example.test",
            "aggregate.user@example.test",
            "Aggregate User"));
        ownerCreate.StatusCode.Should().Be(HttpStatusCode.Created, await ownerCreate.Content.ReadAsStringAsync());
        var userId = (await ReadObjectAsync(ownerCreate))["id"]!.GetValue<string>();

        string secondToken;
        string secondConnectionId;
        await using (var setup = server.Services.CreateAsyncScope())
        {
            var admin = setup.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var secondOrganization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Second SCIM tenant", null));
            var secondConnection = await admin.CreateScimConnectionAsync(
                new SqlOSCreateScimConnectionRequest(secondOrganization.Id, "Second directory", true));
            secondToken = secondConnection.Token;
            secondConnectionId = secondConnection.ConnectionId;
        }

        await using var secondScope = server.Services.CreateAsyncScope();
        var secondScim = secondScope.ServiceProvider.GetRequiredService<SqlOSScimService>();
        var secondHttpContext = new DefaultHttpContext();
        secondHttpContext.Request.Headers.Authorization = $"Bearer {secondToken}";
        var secondConnectionEntity = await secondScim.AuthenticateAsync(secondHttpContext);
        var secondCreate = await secondScim.CreateUserAsync(secondConnectionEntity, new JsonObject
        {
            ["schemas"] = new JsonArray(UserSchema),
            ["externalId"] = "second-directory-user",
            ["userName"] = "second-directory-user@example.test",
            ["displayName"] = "Aggregate User from second tenant",
            ["active"] = true,
            ["emails"] = new JsonArray(new JsonObject
            {
                ["value"] = "aggregate.user@example.test",
                ["type"] = "work",
                ["primary"] = true
            })
        });
        secondCreate["id"]!.GetValue<string>().Should().Be(userId);

        using var ownerDeactivate = await server.SendAsync(HttpMethod.Patch, $"/Users/{userId}", new JsonObject
        {
            ["schemas"] = new JsonArray(PatchSchema),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "replace",
                ["path"] = "active",
                ["value"] = false
            })
        });
        ownerDeactivate.StatusCode.Should().Be(HttpStatusCode.OK, await ownerDeactivate.Content.ReadAsStringAsync());
        await using (var afterOwnerDeactivate = server.Services.CreateAsyncScope())
        {
            (await afterOwnerDeactivate.ServiceProvider.GetRequiredService<TestSqlOSDbContext>()
                .Set<SqlOSUser>().SingleAsync(item => item.Id == userId)).IsActive.Should().BeTrue();
        }

        await secondScim.PatchUserAsync(secondConnectionEntity, userId, new JsonObject
        {
            ["schemas"] = new JsonArray(PatchSchema),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "replace",
                ["path"] = "active",
                ["value"] = false
            })
        });
        await using var verify = server.Services.CreateAsyncScope();
        var verifyContext = verify.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await verifyContext.Set<SqlOSUser>().SingleAsync(item => item.Id == userId)).IsActive.Should().BeFalse();
        (await verifyContext.Set<SqlOSScimExternalId>().SingleAsync(item => item.ConnectionId == secondConnectionId)).OwnsUserLifecycle.Should().BeFalse();

        await secondScim.PatchUserAsync(secondConnectionEntity, userId, new JsonObject
        {
            ["schemas"] = new JsonArray(PatchSchema),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "replace",
                ["path"] = "active",
                ["value"] = true
            })
        });
        verifyContext.ChangeTracker.Clear();
        (await verifyContext.Set<SqlOSUser>().SingleAsync(item => item.Id == userId)).IsActive.Should().BeTrue(
            "a non-owner SCIM link must reactivate a lifecycle that is SCIM-managed by another connection");
    }

    [TestMethod]
    public async Task PatchWithUnknownMember_IsAtomicAgainstRealSql()
    {
        await using var server = await ScimSqlServer.CreateAsync("ScimAtomic");
        var user = await server.CreateUserAsync("atomic-user");
        var groupId = await server.CreateGroupAsync("Atomic Group", user.Id);

        using var response = await server.SendAsync(HttpMethod.Patch, $"/Groups/{groupId}", new JsonObject
        {
            ["schemas"] = new JsonArray(PatchSchema),
            ["Operations"] = new JsonArray(
                new JsonObject
                {
                    ["op"] = "remove",
                    ["path"] = $"members[value eq \"{user.Id}\"]"
                },
                new JsonObject
                {
                    ["op"] = "add",
                    ["path"] = "members",
                    ["value"] = new JsonArray(new JsonObject { ["value"] = "usr_missing" })
                })
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadObjectAsync(response))["scimType"]!.GetValue<string>().Should().Be("invalidValue");

        using var get = await server.Client.GetAsync($"{server.BasePath}/Groups/{groupId}");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var persisted = await ReadObjectAsync(get);
        persisted["members"]!.AsArray().Select(x => x!["value"]!.GetValue<string>()).Should().Equal(user.Id);

        await using var scope = server.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await context.Set<SqlOSFgaUserGroupMembership>().CountAsync()).Should().Be(1);
    }

    [TestMethod]
    public async Task ExternalId_IsCaseExactForSqlFilteringAndUniqueness()
    {
        await using var server = await ScimSqlServer.CreateAsync("ScimExternalCase");
        using var upperCreate = await server.SendAsync(HttpMethod.Post, "/Users", UserPayload(
            "CaseSensitiveProviderId",
            "case.upper@example.test",
            "case.upper@example.test",
            "Upper case identity"));
        upperCreate.StatusCode.Should().Be(HttpStatusCode.Created, await upperCreate.Content.ReadAsStringAsync());
        using var lowerCreate = await server.SendAsync(HttpMethod.Post, "/Users", UserPayload(
            "casesensitiveproviderid",
            "case.lower@example.test",
            "case.lower@example.test",
            "Lower case identity"));
        lowerCreate.StatusCode.Should().Be(HttpStatusCode.Created, await lowerCreate.Content.ReadAsStringAsync());

        var upperFilter = Uri.EscapeDataString("externalId eq \"CaseSensitiveProviderId\"");
        var mixedFilter = Uri.EscapeDataString("externalId eq \"CASESENSITIVEPROVIDERID\"");
        using var upperResponse = await server.Client.GetAsync($"{server.BasePath}/Users?filter={upperFilter}");
        using var mixedResponse = await server.Client.GetAsync($"{server.BasePath}/Users?filter={mixedFilter}");
        var upper = await ReadObjectAsync(upperResponse);
        var mixed = await ReadObjectAsync(mixedResponse);

        upper["totalResults"]!.GetValue<int>().Should().Be(1);
        upper["Resources"]![0]!["externalId"]!.GetValue<string>().Should().Be("CaseSensitiveProviderId");
        mixed["totalResults"]!.GetValue<int>().Should().Be(0);

        var upperUserId = (await ReadObjectAsync(upperCreate))["id"]!.GetValue<string>();
        var lowerUserId = (await ReadObjectAsync(lowerCreate))["id"]!.GetValue<string>();
        using var groupResponse = await server.SendAsync(HttpMethod.Post, "/Groups", new JsonObject
        {
            ["schemas"] = new JsonArray(GroupSchema),
            ["displayName"] = "Case-sensitive external members",
            ["members"] = new JsonArray(
                new JsonObject { ["value"] = "CaseSensitiveProviderId" },
                new JsonObject { ["value"] = "casesensitiveproviderid" })
        });
        groupResponse.StatusCode.Should().Be(HttpStatusCode.Created, await groupResponse.Content.ReadAsStringAsync());
        var group = await ReadObjectAsync(groupResponse);
        group["members"]!.AsArray().Select(member => member!["value"]!.GetValue<string>())
            .Should().BeEquivalentTo(upperUserId, lowerUserId);
    }

    [TestMethod]
    public async Task ProtocolTraffic_CleansOneDeterministicBoundedCommitMarkerBatchAgainstRealSql()
    {
        await using var server = await ScimSqlServer.CreateAsync("ScimProtocolMarkerCleanup");
        await using (var setup = server.Services.CreateAsyncScope())
        {
            var context = setup.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            var expiredAt = DateTime.UtcNow.AddDays(-2);
            context.Set<SqlOSScimOperationCommit>().AddRange(Enumerable.Range(0, 300).Select(index =>
                new SqlOSScimOperationCommit
                {
                    Id = $"protocol_expired_{index:D4}",
                    OccurredAt = expiredAt
                }));
            context.Set<SqlOSScimOperationCommit>().Add(new SqlOSScimOperationCommit
            {
                Id = "protocol_recent_marker",
                OccurredAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        using var response = await server.SendAsync(HttpMethod.Post, "/Users", UserPayload(
            "protocol-cleanup-user",
            "protocol.cleanup@example.test",
            "protocol.cleanup@example.test",
            "Protocol Cleanup User"));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        await using var verify = server.Services.CreateAsyncScope();
        var verifyContext = verify.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        var remainingExpiredIds = await verifyContext.Set<SqlOSScimOperationCommit>()
            .Where(marker => marker.Id.StartsWith("protocol_expired_"))
            .OrderBy(marker => marker.Id)
            .Select(marker => marker.Id)
            .ToListAsync();
        remainingExpiredIds.Should().Equal(
            Enumerable.Range(256, 44).Select(index => $"protocol_expired_{index:D4}"),
            "one protocol transaction should retire exactly the oldest 256 expired markers");
        (await verifyContext.Set<SqlOSScimOperationCommit>()
                .AnyAsync(marker => marker.Id == "protocol_recent_marker"))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task MappingDisable_ImmediatelyRevokesManagedAuthorization()
    {
        await using var server = await ScimSqlServer.CreateAsync("ScimMapping");
        var user = await server.CreateUserAsync("mapping-user");
        string mappingId;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            context.Set<SqlOSFgaResourceType>().Add(new SqlOSFgaResourceType { Id = "store", Name = "Store" });
            context.Set<SqlOSFgaRole>().Add(new SqlOSFgaRole { Id = "role_manager", Key = "manager", Name = "Manager" });
            context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
            {
                Id = "store_123",
                ResourceTypeId = "store",
                Name = "Store 123",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var mapping = await admin.CreateScimGroupMappingAsync(server.ConnectionId, new SqlOSCreateScimGroupMappingRequest(
                "display_name", "Store Managers", null, null, "manager", "store_123", null, Enabled: true));
            mappingId = mapping.Id;
        }

        await server.CreateGroupAsync("Store Managers", user.Id);
        await using (var before = server.Services.CreateAsyncScope())
        {
            var context = before.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            (await context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(1);
            (await context.Set<SqlOSScimManagedGrant>().SingleAsync()).RevokedAt.Should().BeNull();
        }

        await using (var disable = server.Services.CreateAsyncScope())
        {
            await disable.ServiceProvider.GetRequiredService<SqlOSAdminService>()
                .SetScimGroupMappingEnabledAsync(mappingId, false);
        }

        await using var after = server.Services.CreateAsyncScope();
        var afterContext = after.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await afterContext.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
        (await afterContext.Set<SqlOSScimManagedGrant>().SingleAsync()).RevokedAt.Should().NotBeNull();
    }

    [TestMethod]
    public async Task StartupReconciliation_RevokesGrantsForConnectionsDisabledBySchemaHardening()
    {
        await using var server = await ScimSqlServer.CreateAsync("ScimStartupReconcile");
        await using (var setup = server.Services.CreateAsyncScope())
        {
            var context = setup.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            context.Set<SqlOSFgaResourceType>().Add(new SqlOSFgaResourceType { Id = "site", Name = "Site" });
            context.Set<SqlOSFgaRole>().Add(new SqlOSFgaRole { Id = "role_site_admin", Key = "site_admin", Name = "Site admin" });
            context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
            {
                Id = "site_1",
                ResourceTypeId = "site",
                Name = "Site 1",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
            await setup.ServiceProvider.GetRequiredService<SqlOSAdminService>()
                .CreateScimGroupMappingAsync(server.ConnectionId, new SqlOSCreateScimGroupMappingRequest(
                    "display_name", "Site Admins", null, null, "site_admin", "site_1", null, Enabled: true));
        }
        await server.CreateGroupAsync("Site Admins");

        await using (var simulateMigration = server.Services.CreateAsyncScope())
        {
            var context = simulateMigration.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            var connection = await context.Set<SqlOSScimConnection>().SingleAsync(item => item.Id == server.ConnectionId);
            connection.IsEnabled = false;
            await context.SaveChangesAsync();
        }

        await using (var reconcile = server.Services.CreateAsyncScope())
        {
            await reconcile.ServiceProvider.GetRequiredService<SqlOSAdminService>()
                .ReconcileDisabledScimManagedGrantsAsync();
        }

        await using var verify = server.Services.CreateAsyncScope();
        var verifyContext = verify.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await verifyContext.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
        (await verifyContext.Set<SqlOSScimManagedGrant>().SingleAsync()).RevokedAt.Should().NotBeNull();
    }

    [TestMethod]
    public async Task GroupExternalIdChange_ReconcilesManagedAuthorizationByStableGroupIdentity()
    {
        await using var server = await ScimSqlServer.CreateAsync("ScimMappingIdentity");
        await using (var setup = server.Services.CreateAsyncScope())
        {
            var context = setup.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            context.Set<SqlOSFgaResourceType>().Add(new SqlOSFgaResourceType { Id = "warehouse", Name = "Warehouse" });
            context.Set<SqlOSFgaRole>().Add(new SqlOSFgaRole { Id = "role_operator", Key = "operator", Name = "Operator" });
            context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
            {
                Id = "warehouse_123",
                ResourceTypeId = "warehouse",
                Name = "Warehouse 123",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
            await setup.ServiceProvider.GetRequiredService<SqlOSAdminService>()
                .CreateScimGroupMappingAsync(server.ConnectionId, new SqlOSCreateScimGroupMappingRequest(
                    "external_id", null, "warehouse-operators", null, "operator", "warehouse_123", null, Enabled: true));
        }

        using var create = await server.SendAsync(HttpMethod.Post, "/Groups", new JsonObject
        {
            ["schemas"] = new JsonArray(GroupSchema),
            ["externalId"] = "warehouse-operators",
            ["displayName"] = "Warehouse Operators",
            ["members"] = new JsonArray()
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var groupId = (await ReadObjectAsync(create))["id"]!.GetValue<string>();

        await using (var before = server.Services.CreateAsyncScope())
        {
            var context = before.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            (await context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(1);
        }

        using var changeExternalId = await server.SendAsync(HttpMethod.Patch, $"/Groups/{groupId}", new JsonObject
        {
            ["schemas"] = new JsonArray(PatchSchema),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "replace",
                ["path"] = "externalId",
                ["value"] = "warehouse-operators-renamed"
            })
        });
        changeExternalId.StatusCode.Should().Be(HttpStatusCode.NoContent, await changeExternalId.Content.ReadAsStringAsync());

        await using (var afterChange = server.Services.CreateAsyncScope())
        {
            var context = afterChange.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            (await context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
            (await context.Set<SqlOSScimManagedGrant>().SingleAsync()).RevokedAt.Should().NotBeNull();
        }

        using var restoreExternalId = await server.SendAsync(HttpMethod.Patch, $"/Groups/{groupId}", new JsonObject
        {
            ["schemas"] = new JsonArray(PatchSchema),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "replace",
                ["path"] = "externalId",
                ["value"] = "warehouse-operators"
            })
        });
        restoreExternalId.StatusCode.Should().Be(HttpStatusCode.NoContent, await restoreExternalId.Content.ReadAsStringAsync());

        await using var restored = server.Services.CreateAsyncScope();
        var restoredContext = restored.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await restoredContext.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(1);
        (await restoredContext.Set<SqlOSScimManagedGrant>().CountAsync(item => item.RevokedAt == null)).Should().Be(1);
        (await restoredContext.Set<SqlOSScimManagedGrant>().CountAsync()).Should().Be(2);
    }

    [TestMethod]
    public async Task ConnectionDisable_BlocksARequestThatAuthenticatedBeforeDisableCommitted()
    {
        await using var server = await ScimSqlServer.CreateAsync("ScimDisableBarrier");
        await using var staleRequestScope = server.Services.CreateAsyncScope();
        var scim = staleRequestScope.ServiceProvider.GetRequiredService<SqlOSScimService>();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = $"Bearer {server.OriginalToken}";
        var authenticatedConnection = await scim.AuthenticateAsync(httpContext);

        await using (var disableScope = server.Services.CreateAsyncScope())
        {
            await disableScope.ServiceProvider.GetRequiredService<SqlOSAdminService>()
                .SetScimConnectionEnabledAsync(server.ConnectionId, false);
        }

        var write = async () => await scim.CreateUserAsync(
            authenticatedConnection,
            UserPayload("disabled-stale-request", "disabled@example.test", "disabled@example.test", "Disabled"));
        var error = await write.Should().ThrowAsync<SqlOSScimException>();
        error.Which.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);

        await using var verify = server.Services.CreateAsyncScope();
        var context = verify.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await context.Set<SqlOSScimExternalId>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task TokenRotation_BlocksAWriteThatAuthenticatedBeforeRotationCommitted()
    {
        await using var server = await ScimSqlServer.CreateAsync("ScimRotationBarrier");
        await using var staleRequestScope = server.Services.CreateAsyncScope();
        var scim = staleRequestScope.ServiceProvider.GetRequiredService<SqlOSScimService>();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = $"Bearer {server.OriginalToken}";
        var authenticatedConnection = await scim.AuthenticateAsync(httpContext);

        await using (var rotationScope = server.Services.CreateAsyncScope())
        {
            await rotationScope.ServiceProvider.GetRequiredService<SqlOSAdminService>()
                .RotateScimTokenAsync(server.ConnectionId);
        }

        var write = async () => await scim.CreateUserAsync(
            authenticatedConnection,
            UserPayload("rotated-stale-request", "rotated@example.test", "rotated@example.test", "Rotated"));
        var error = await write.Should().ThrowAsync<SqlOSScimException>();
        error.Which.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);

        await using var verify = server.Services.CreateAsyncScope();
        var context = verify.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        (await context.Set<SqlOSScimExternalId>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task ConcurrentCreates_AreDuplicateSafeAndDistinctRetrySafe_AndTokenRotationInvalidatesOldToken()
    {
        await using var server = await ScimSqlServer.CreateAsync("ScimRace");
        var payload = UserPayload("race-external", "race.user@example.test", "race.user@example.test", "Race User");
        var responses = await Task.WhenAll(
            server.SendAsync(HttpMethod.Post, "/Users", payload.DeepClone().AsObject()),
            server.SendAsync(HttpMethod.Post, "/Users", payload.DeepClone().AsObject()));
        responses.Select(x => x.StatusCode).Should().BeEquivalentTo([HttpStatusCode.Created, HttpStatusCode.Conflict]);
        foreach (var response in responses)
        {
            response.Dispose();
        }

        await using (var verify = server.Services.CreateAsyncScope())
        {
            var context = verify.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            (await context.Set<SqlOSScimExternalId>().CountAsync(x => x.ResourceType == "User")).Should().Be(1);
        }

        var distinctPayloads = new[]
        {
            UserPayload("race-distinct-one", "race.distinct.one@example.test", "race.distinct.one@example.test", "Race Distinct One"),
            UserPayload("race-distinct-two", "race.distinct.two@example.test", "race.distinct.two@example.test", "Race Distinct Two")
        };
        var distinctResponses = await Task.WhenAll(distinctPayloads.Select(item =>
            server.SendAsync(HttpMethod.Post, "/Users", item.DeepClone().AsObject())));
        for (var index = 0; index < distinctResponses.Length; index++)
        {
            using var distinctResponse = distinctResponses[index];
            if (distinctResponse.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                using var retryResponse = await server.SendAsync(
                    HttpMethod.Post,
                    "/Users",
                    distinctPayloads[index].DeepClone().AsObject());
                retryResponse.StatusCode.Should().Be(
                    HttpStatusCode.Created,
                    $"a distinct request that received a retryable SQL failure should be safe to retry: {await retryResponse.Content.ReadAsStringAsync()}");
            }
            else
            {
                distinctResponse.StatusCode.Should().Be(
                    HttpStatusCode.Created,
                    $"distinct concurrent creates must not fail permanently: {await distinctResponse.Content.ReadAsStringAsync()}");
            }
        }

        await using (var verifyDistinct = server.Services.CreateAsyncScope())
        {
            var context = verifyDistinct.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
            (await context.Set<SqlOSScimExternalId>().CountAsync(x => x.ResourceType == "User")).Should().Be(3);
        }

        string newToken;
        await using (var rotate = server.Services.CreateAsyncScope())
        {
            newToken = (await rotate.ServiceProvider.GetRequiredService<SqlOSAdminService>()
                .RotateScimTokenAsync(server.ConnectionId)).Token;
        }

        using var oldRequest = new HttpRequestMessage(HttpMethod.Get, $"{server.BasePath}/ServiceProviderConfig");
        oldRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.OriginalToken);
        using var oldResponse = await server.AnonymousClient.SendAsync(oldRequest);
        oldResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var newRequest = new HttpRequestMessage(HttpMethod.Get, $"{server.BasePath}/ServiceProviderConfig");
        newRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
        using var newResponse = await server.AnonymousClient.SendAsync(newRequest);
        newResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task DeletedUserNameWithDifferentExternalId_CannotResurrectTombstonedIdentity()
    {
        await using var server = await ScimSqlServer.CreateAsync("ScimTombstone");
        const string userName = "tombstoned.user@example.test";
        using var createResponse = await server.SendAsync(
            HttpMethod.Post,
            "/Users",
            UserPayload("tombstoned-external-original", userName, userName, "Tombstoned User"));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created, await createResponse.Content.ReadAsStringAsync());
        var createdId = (await ReadObjectAsync(createResponse))["id"]!.GetValue<string>();

        using var deleteResponse = await server.Client.DeleteAsync($"{server.BasePath}/Users/{createdId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var recreateResponse = await server.SendAsync(
            HttpMethod.Post,
            "/Users",
            UserPayload("tombstoned-external-different", userName, userName, "Different Directory Identity"));

        recreateResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadObjectAsync(recreateResponse))["scimType"]!.GetValue<string>().Should().Be("uniqueness");
        using var getDeletedResponse = await server.Client.GetAsync($"{server.BasePath}/Users/{createdId}");
        getDeletedResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var verify = server.Services.CreateAsyncScope();
        var context = verify.ServiceProvider.GetRequiredService<TestSqlOSDbContext>();
        var link = await context.Set<SqlOSScimExternalId>().SingleAsync(x => x.ResourceType == "User");
        link.EntityId.Should().Be(createdId);
        link.ExternalId.Should().Be("tombstoned-external-original");
        link.DeletedAt.Should().NotBeNull();
    }

    private static SqlOSSession NewSession(string id, string userId, string organizationId)
        => new()
        {
            Id = id,
            UserId = userId,
            OrganizationId = organizationId,
            AuthenticationMethod = "saml",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            IdleExpiresAt = DateTime.UtcNow.AddHours(1),
            AbsoluteExpiresAt = DateTime.UtcNow.AddHours(1)
        };

    private static JsonObject UserPayload(string externalId, string userName, string email, string displayName)
        => new()
        {
            ["schemas"] = new JsonArray(UserSchema),
            ["externalId"] = externalId,
            ["userName"] = userName,
            ["displayName"] = displayName,
            ["active"] = true,
            ["emails"] = new JsonArray(new JsonObject { ["value"] = email, ["type"] = "work", ["primary"] = true })
        };

    private static async Task<JsonObject> ReadObjectAsync(HttpResponseMessage response)
        => JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new AssertFailedException("Expected a SCIM JSON object response.");

    private sealed record CreatedUser(string Id, string UserName);

    private sealed class ScimSqlServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ScimSqlServer(WebApplication app, HttpClient client, HttpClient anonymousClient, string databaseName)
        {
            _app = app;
            Client = client;
            AnonymousClient = anonymousClient;
            DatabaseName = databaseName;
        }

        public string BasePath { get; } = "/sqlos/scim/v2";
        public string ConnectionId { get; private set; } = string.Empty;
        public string OrganizationId { get; private set; } = string.Empty;
        public string OriginalToken { get; private set; } = string.Empty;
        public string DatabaseName { get; }
        public HttpClient Client { get; }
        public HttpClient AnonymousClient { get; }
        public IServiceProvider Services => _app.Services;

        public static async Task<ScimSqlServer> CreateAsync(string databasePrefix)
        {
            await using var bootstrap = await AspireFixture.CreateIsolatedAuthContextAsync(databasePrefix);
            var fgaOptions = Options.Create(new SqlOSFgaOptions());
            var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());
            await new SqlOSFgaSchemaInitializer(
                bootstrap,
                fgaOptions,
                loggerFactory.CreateLogger<SqlOSFgaSchemaInitializer>())
                .EnsureSchemaAsync();
            await new SqlOSFgaSeedService(
                bootstrap,
                fgaOptions,
                loggerFactory.CreateLogger<SqlOSFgaSeedService>())
                .SeedCoreAsync();
            var connectionString = bootstrap.Database.GetConnectionString()!;
            var databaseName = bootstrap.Database.GetDbConnection().Database;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<TestSqlOSDbContext>(options =>
                options.UseSqlServer(connectionString, sqlServer => sqlServer.EnableRetryOnFailure()));
            builder.Services.AddSqlOS<TestSqlOSDbContext>(options =>
            {
                options.AuthServer.PublicOrigin = "https://scim.integration.test";
                options.AuthServer.Issuer = "https://scim.integration.test/sqlos/auth";
                options.AuthServer.BasePath = "/sqlos/auth";
                options.AuthServer.EnableScim = true;
                options.AuthServer.ScimBasePath = "/sqlos/scim/v2";
            });
            builder.Services.RemoveAll<IHostedService>();
            builder.Services.RemoveAll<IStartupFilter>();
            var app = builder.Build();
            app.MapAuthServer("/sqlos/auth");
            await app.StartAsync();

            var client = app.GetTestClient();
            client.BaseAddress = new Uri("https://scim.integration.test");
            var anonymousClient = app.GetTestClient();
            anonymousClient.BaseAddress = client.BaseAddress;
            var server = new ScimSqlServer(app, client, anonymousClient, databaseName);
            await using var scope = app.Services.CreateAsyncScope();
            var admin = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();
            var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest($"SCIM {Guid.NewGuid():N}", null));
            var connection = await admin.CreateScimConnectionAsync(new SqlOSCreateScimConnectionRequest(
                organization.Id,
                "Integration Directory",
                Enabled: true));
            server.OrganizationId = organization.Id;
            server.ConnectionId = connection.ConnectionId;
            server.OriginalToken = connection.Token;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
            return server;
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativePath, JsonObject body)
        {
            var request = new HttpRequestMessage(method, $"{BasePath}{relativePath}")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/scim+json")
            };
            return await Client.SendAsync(request);
        }

        public async Task<CreatedUser> CreateUserAsync(string key)
        {
            using var response = await SendAsync(HttpMethod.Post, "/Users", UserPayload(
                $"external-{key}",
                $"{key}@login.example.test",
                $"{key}@mail.example.test",
                key));
            response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            var user = await ReadObjectAsync(response);
            return new CreatedUser(user["id"]!.GetValue<string>(), user["userName"]!.GetValue<string>());
        }

        public async Task<string> CreateGroupAsync(string displayName, params string[] members)
        {
            using var response = await SendAsync(HttpMethod.Post, "/Groups", new JsonObject
            {
                ["schemas"] = new JsonArray(GroupSchema),
                ["externalId"] = $"external-{Guid.NewGuid():N}",
                ["displayName"] = displayName,
                ["members"] = new JsonArray(members.Select(x => (JsonNode)new JsonObject { ["value"] = x }).ToArray())
            });
            response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            return (await ReadObjectAsync(response))["id"]!.GetValue<string>();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            AnonymousClient.Dispose();
            await using (var scope = _app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<TestSqlOSDbContext>().Database.EnsureDeletedAsync();
            }
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

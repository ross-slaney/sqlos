using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;
using SqlOS.Fga.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

/// <summary>
/// Issue #421: a tenant's IdP chooses SCIM group names, so a mapping must never grant a role
/// outside the connection's grant boundary subtree in the FGA tree.
///
/// Resource tree used by every test:
///   org::a (Acme, org A's root)            org::b (Beta, org B's root)
///   ├── org::a::store::42                  ├── org::b::store::9001
///   ├── store::42 (legacy global ID)       ├── store::9001 (legacy global ID)
///   └── org::a::region::west               └── org::a::store::9001 (Acme-looking ID in Beta's subtree)
///       └── org::a::region::west::store::7
/// </summary>
[TestClass]
public sealed class SqlOSScimGrantBoundaryTests
{
    private const string OrgA = "org_acme";
    private const string OrgB = "org_beta";
    private const string BoundaryA = "org::a";
    private const string BoundaryB = "org::b";
    private const string StoreA42 = "org::a::store::42";
    private const string RegionAWest = "org::a::region::west";
    private const string StoreAWest7 = "org::a::region::west::store::7";
    private const string StoreB9001 = "org::b::store::9001";
    private const string LegacyStoreA42 = "store::42";
    private const string LegacyStoreB9001 = "store::9001";
    private const string AcmeLookingStoreInB = "org::a::store::9001";
    private const string DocumentedPattern = "^Store-(?<storeId>[^-]+)-Managers$";
    private const string StoreManager = "store_manager";
    private const string ManageStore = "STORE_MANAGE";
    private const string StrongSeedToken = "scim_boundary_seed_token_0123456789abcdef";

    [TestMethod]
    public async Task DocumentedTemplate_GroupNamingAnotherTenantsStore_CreatesNoGrantAndDeniesAccess()
    {
        using var f = await CreateFixtureAsync();
        var connection = await CreateConnectionAsync(f, OrgA, BoundaryA);
        await f.Admin.CreateScimGroupMappingAsync(connection.Id, PatternMapping("store::{storeId}"));
        var ada = await PushUserAsync(f, connection, "ada");

        var group = await PushGroupAsync(f, connection, "grp-attack", "Store-9001-Managers", ada);

        (await f.Context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0,
            "org A's IdP must not obtain store_manager on org B's store");
        (await f.Context.Set<SqlOSScimManagedGrant>().CountAsync()).Should().Be(0);
        var violation = await f.Context.Set<SqlOSScimSyncEvent>().SingleAsync(x => x.Action == "scim.grant.outside_boundary");
        violation.Result.Should().Be("failed");
        violation.ResourceId.Should().Be(group);
        var data = JsonNode.Parse(violation.DataJson!)!.AsObject();
        data["resourceId"]!.GetValue<string>().Should().Be(LegacyStoreB9001);
        data["grantBoundaryResourceId"]!.GetValue<string>().Should().Be(BoundaryA);
        data["reason"]!.GetValue<string>().Should().Be("outside_boundary");
        (await f.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.Action == "scim.grant.outside_boundary" && x.Source == "scim" && x.OrganizationId == OrgA))
            .Should().Be(1);
        (await f.Fga.CheckAccessAsync(await SubjectIdAsync(f, ada), ManageStore, LegacyStoreB9001)).Allowed.Should().BeFalse();
    }

    [TestMethod]
    public async Task IssueScenario_TemplateThatNamesOrgBsStore_CreatesNoGrant()
    {
        using var f = await CreateFixtureAsync();
        var connection = await CreateConnectionAsync(f, OrgA, BoundaryA);
        await f.Admin.CreateScimGroupMappingAsync(connection.Id, PatternMapping("org::b::store::{storeId}"));
        var ada = await PushUserAsync(f, connection, "ada");

        await PushGroupAsync(f, connection, "grp-issue", "Store-9001-Managers", ada);

        (await f.Context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
        var violation = await f.Context.Set<SqlOSScimSyncEvent>().SingleAsync(x => x.Action == "scim.grant.outside_boundary");
        JsonNode.Parse(violation.DataJson!)!["resourceId"]!.GetValue<string>().Should().Be(StoreB9001);
        (await f.Fga.CheckAccessAsync(await SubjectIdAsync(f, ada), ManageStore, StoreB9001)).Allowed.Should().BeFalse();
    }

    [TestMethod]
    public async Task MatchingIdPrefix_ForAResourceUnderAnotherTenant_IsOutsideTheBoundary()
    {
        using var f = await CreateFixtureAsync();
        var connection = await CreateConnectionAsync(f, OrgA, BoundaryA);
        await f.Admin.CreateScimGroupMappingAsync(connection.Id, PatternMapping("org::a::store::{storeId}"));
        var ada = await PushUserAsync(f, connection, "ada");

        await PushGroupAsync(f, connection, "grp-prefix", "Store-9001-Managers", ada);

        (await f.Context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0,
            "a resource ID that merely starts with org A's prefix is still in org B's subtree");
        (await f.Context.Set<SqlOSScimSyncEvent>().CountAsync(x => x.Action == "scim.grant.outside_boundary")).Should().Be(1);
        (await f.Fga.CheckAccessAsync(await SubjectIdAsync(f, ada), ManageStore, AcmeLookingStoreInB)).Allowed.Should().BeFalse();
    }

    [TestMethod]
    public async Task GroupRenamedToResolveOutsideTheBoundary_RevokesTheExistingGrantAndCreatesNone()
    {
        using var f = await CreateFixtureAsync();
        var connection = await CreateConnectionAsync(f, OrgA, BoundaryA);
        await f.Admin.CreateScimGroupMappingAsync(connection.Id, PatternMapping("store::{storeId}"));
        var ada = await PushUserAsync(f, connection, "ada");
        var subject = await SubjectIdAsync(f, ada);
        await PushGroupAsync(f, connection, "grp-rename", "Store-42-Managers", ada);
        (await f.Context.Set<SqlOSFgaGrant>().SingleAsync()).ResourceId.Should().Be(LegacyStoreA42);
        (await f.Fga.CheckAccessAsync(subject, ManageStore, LegacyStoreA42)).Allowed.Should().BeTrue();

        await PushGroupAsync(f, connection, "grp-rename", "Store-9001-Managers", ada);

        (await f.Context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
        (await f.Context.Set<SqlOSScimManagedGrant>().SingleAsync()).RevokedAt.Should().NotBeNull();
        (await f.Context.Set<SqlOSScimSyncEvent>().CountAsync(x => x.Action == "scim.grant.revoked")).Should().Be(1);
        (await f.Context.Set<SqlOSScimSyncEvent>().CountAsync(x => x.Action == "scim.grant.outside_boundary")).Should().Be(1);
        (await f.Fga.CheckAccessAsync(subject, ManageStore, LegacyStoreA42)).Allowed.Should().BeFalse();
        (await f.Fga.CheckAccessAsync(subject, ManageStore, LegacyStoreB9001)).Allowed.Should().BeFalse();
    }

    [TestMethod]
    public async Task HierarchyCycleAboveTheResolvedResource_FailsClosed()
    {
        using var f = await CreateFixtureAsync();
        AddResource(f.Context, "org::a::loop::1", "org::a::loop::2", "region", "Loop 1");
        AddResource(f.Context, "org::a::loop::2", "org::a::loop::1", "region", "Loop 2");
        AddResource(f.Context, "org::a::store::cyc", "org::a::loop::1", "store", "Store in a cycle");
        await f.Context.SaveChangesAsync();
        var connection = await CreateConnectionAsync(f, OrgA, BoundaryA);
        await f.Admin.CreateScimGroupMappingAsync(connection.Id, PatternMapping("org::a::store::{storeId}"));

        await PushGroupAsync(f, connection, "grp-cycle", "Store-cyc-Managers");

        (await f.Context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
        var violation = await f.Context.Set<SqlOSScimSyncEvent>().SingleAsync(x => x.Action == "scim.grant.outside_boundary");
        JsonNode.Parse(violation.DataJson!)!["reason"]!.GetValue<string>().Should().Be("hierarchy_cycle");
    }

    [TestMethod]
    public async Task HierarchyDeeperThanTheConfiguredMaximum_FailsClosed()
    {
        using var f = await CreateFixtureAsync();
        var parent = BoundaryA;
        for (var level = 1; level <= 11; level++)
        {
            var id = $"org::a::deep::{level}";
            AddResource(f.Context, id, parent, "region", $"Deep {level}");
            parent = id;
        }
        AddResource(f.Context, "org::a::store::deep", parent, "store", "Store below the depth limit");
        await f.Context.SaveChangesAsync();
        var connection = await CreateConnectionAsync(f, OrgA, BoundaryA);
        await f.Admin.CreateScimGroupMappingAsync(connection.Id, PatternMapping("org::a::store::{storeId}"));

        await PushGroupAsync(f, connection, "grp-deep", "Store-deep-Managers");

        (await f.Context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
        var violation = await f.Context.Set<SqlOSScimSyncEvent>().SingleAsync(x => x.Action == "scim.grant.outside_boundary");
        JsonNode.Parse(violation.DataJson!)!["reason"]!.GetValue<string>().Should().Be("hierarchy_too_deep");
    }

    [TestMethod]
    public async Task UpgradedConnectionWithoutBoundary_PreexistingTemplateMapping_GrantsNothing()
    {
        using var f = await CreateFixtureAsync();
        var connection = await CreateLegacyConnectionAsync(f, OrgA);
        await InsertLegacyPatternMappingAsync(f, connection.Id, "store::{storeId}");
        var ada = await PushUserAsync(f, connection, "ada");

        await PushGroupAsync(f, connection, "grp-upgrade", "Store-9001-Managers", ada);

        (await f.Context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0,
            "a connection without a grant boundary must fail closed after upgrade");
        (await f.Context.Set<SqlOSScimManagedGrant>().CountAsync()).Should().Be(0);
        (await f.Context.Set<SqlOSScimSyncEvent>().CountAsync(x => x.Action == "scim.grant.boundary_missing" && x.Result == "failed"))
            .Should().Be(1);
        (await f.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.Action == "scim.grant.boundary_missing" && x.Source == "scim"))
            .Should().Be(1);
        (await f.Fga.CheckAccessAsync(await SubjectIdAsync(f, ada), ManageStore, LegacyStoreB9001)).Allowed.Should().BeFalse();
    }

    [TestMethod]
    public async Task UpgradedConnectionWithoutBoundary_RevokesAPreexistingManagedGrantOnTheNextPush()
    {
        using var f = await CreateFixtureAsync();
        var connection = await CreateLegacyConnectionAsync(f, OrgA);
        var ada = await PushUserAsync(f, connection, "ada");
        var groupId = await PushGroupAsync(f, connection, "grp-planted", "Store-9001-Managers", ada);
        var mapping = await InsertLegacyPatternMappingAsync(f, connection.Id, "store::{storeId}");
        await InsertLegacyManagedGrantAsync(f, connection.Id, mapping.Id, groupId, "grp-planted", LegacyStoreB9001);
        var subject = await SubjectIdAsync(f, ada);
        (await f.Fga.CheckAccessAsync(subject, ManageStore, LegacyStoreB9001)).Allowed.Should().BeTrue(
            "the fixture reproduces a cross-tenant grant planted before the upgrade");

        await PushGroupAsync(f, connection, "grp-planted", "Store-9001-Managers", ada);

        (await f.Context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
        (await f.Context.Set<SqlOSScimManagedGrant>().SingleAsync()).RevokedAt.Should().NotBeNull();
        (await f.Context.Set<SqlOSScimSyncEvent>().CountAsync(x => x.Action == "scim.grant.boundary_missing")).Should().Be(1);
        (await f.Fga.CheckAccessAsync(subject, ManageStore, LegacyStoreB9001)).Allowed.Should().BeFalse();
    }

    [TestMethod]
    public async Task DeletedBoundaryResource_FailsClosedAndRevokesOnTheNextPush()
    {
        using var f = await CreateFixtureAsync();
        AddResource(f.Context, "org::gone", null, "org", "Soon removed");
        AddResource(f.Context, "org::gone::store::1", "org::gone", "store", "Store 1");
        await f.Context.SaveChangesAsync();
        var connection = await CreateConnectionAsync(f, OrgA, "org::gone");
        await f.Admin.CreateScimGroupMappingAsync(connection.Id, PatternMapping("org::gone::store::{storeId}"));
        await PushGroupAsync(f, connection, "grp-gone", "Store-1-Managers");
        (await f.Context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(1);
        var store = await f.Context.Set<SqlOSFgaResource>().SingleAsync(x => x.Id == "org::gone::store::1");
        store.ParentId = null;
        f.Context.Remove(await f.Context.Set<SqlOSFgaResource>().SingleAsync(x => x.Id == "org::gone"));
        await f.Context.SaveChangesAsync();

        await PushGroupAsync(f, connection, "grp-gone", "Store-1-Managers");

        (await f.Context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
        var missing = await f.Context.Set<SqlOSScimSyncEvent>().SingleAsync(x => x.Action == "scim.grant.boundary_missing");
        JsonNode.Parse(missing.DataJson!)!["reason"]!.GetValue<string>().Should().Be("boundary_resource_not_found");
        ToJson(await f.Admin.GetScimConnectionAsync(connection.Id))["grantBoundary"]!["status"]!.GetValue<string>().Should().Be("not_found");
        ToJson(await f.Admin.ListScimGroupMappingsAsync(connection.Id))["data"]![0]!["grantBoundaryStatus"]!.GetValue<string>()
            .Should().Be("boundary_not_found");

        // The dashboard re-sends the current boundary on every save; that is not a change and
        // must not block renaming or disabling a connection whose boundary resource is gone.
        var renamed = await f.Admin.UpdateScimConnectionAsync(connection.Id, new SqlOSUpdateScimConnectionRequest("Renamed", false)
        {
            GrantBoundaryResourceId = "org::gone"
        });
        renamed.DisplayName.Should().Be("Renamed");
        renamed.IsEnabled.Should().BeFalse();
        renamed.GrantBoundaryResourceId.Should().Be("org::gone");
    }

    [TestMethod]
    public async Task PatternInsideTheBoundary_GrantsAllowsAndExplains_ThenMembershipRemovalAndDisableRevoke()
    {
        using var f = await CreateFixtureAsync();
        var connection = await CreateConnectionAsync(f, OrgA, BoundaryA);
        var mapping = await f.Admin.CreateScimGroupMappingAsync(connection.Id, PatternMapping("org::a::store::{storeId}"));
        var ada = await PushUserAsync(f, connection, "ada");
        var subject = await SubjectIdAsync(f, ada);

        await PushGroupAsync(f, connection, "grp-42", "Store-42-Managers", ada);

        var grant = await f.Context.Set<SqlOSFgaGrant>().SingleAsync();
        grant.ResourceId.Should().Be(StoreA42);
        (await f.Context.Set<SqlOSScimSyncEvent>().CountAsync(x => x.Action == "scim.grant.mapped")).Should().Be(1);
        (await f.Fga.CheckAccessAsync(subject, ManageStore, StoreA42)).Allowed.Should().BeTrue();
        var explanation = await f.Fga.TraceResourceAccessAsync(subject, StoreA42, ManageStore);
        explanation.AccessGranted.Should().BeTrue();
        explanation.GrantsUsed.Should().Contain(item =>
            item.ViaGroupName == "Store-42-Managers" && item.RoleKey == StoreManager && item.ResourceId == StoreA42);
        (await f.Fga.CheckAccessAsync(subject, ManageStore, StoreB9001)).Allowed.Should().BeFalse();

        await PushGroupAsync(f, connection, "grp-42", "Store-42-Managers");
        (await f.Fga.CheckAccessAsync(subject, ManageStore, StoreA42)).Allowed.Should().BeFalse(
            "removing the member removes group-derived access without rewriting the group grant");
        (await f.Context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(1);

        await f.Admin.SetScimGroupMappingEnabledAsync(mapping.Id, false);
        (await f.Context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
        (await f.Context.Set<SqlOSScimManagedGrant>().SingleAsync()).RevokedAt.Should().NotBeNull();
    }

    [TestMethod]
    public async Task MappingOnTheBoundaryItself_InheritsDownOnlyInsideTheTenant()
    {
        using var f = await CreateFixtureAsync();
        var connection = await CreateConnectionAsync(f, OrgA, BoundaryA);
        await f.Admin.CreateScimGroupMappingAsync(connection.Id, FixedMapping("Acme-Admins", BoundaryA));
        var ada = await PushUserAsync(f, connection, "ada");
        var grace = await PushUserAsync(f, connection, "grace");

        await PushGroupAsync(f, connection, "grp-admins", "Acme-Admins", ada);

        var adaSubject = await SubjectIdAsync(f, ada);
        (await f.Context.Set<SqlOSFgaGrant>().SingleAsync()).ResourceId.Should().Be(BoundaryA);
        (await f.Fga.CheckAccessAsync(adaSubject, ManageStore, StoreA42)).Allowed.Should().BeTrue();
        (await f.Fga.CheckAccessAsync(adaSubject, ManageStore, StoreAWest7)).Allowed.Should().BeTrue();
        (await f.Fga.CheckAccessAsync(adaSubject, ManageStore, StoreB9001)).Allowed.Should().BeFalse();
        (await f.Fga.CheckAccessAsync(await SubjectIdAsync(f, grace), ManageStore, StoreA42)).Allowed.Should().BeFalse(
            "a directory user outside the mapped group has no grant");
    }

    [TestMethod]
    public async Task EnabledMappingOnAConnectionWithoutBoundary_IsRejectedWithATypedError()
    {
        using var f = await CreateFixtureAsync();
        var connection = await CreateLegacyConnectionAsync(f, OrgA);

        var create = () => f.Admin.CreateScimGroupMappingAsync(connection.Id, PatternMapping("org::a::store::{storeId}"));
        (await create.Should().ThrowAsync<SqlOSScimGrantBoundaryException>())
            .Which.Error.Should().Be(SqlOSScimGrantBoundaryErrors.BoundaryRequired);
        (await f.Context.Set<SqlOSScimGroupMapping>().CountAsync()).Should().Be(0);

        var staged = await f.Admin.CreateScimGroupMappingAsync(connection.Id, PatternMapping("org::a::store::{storeId}", enabled: false));
        var enable = () => f.Admin.SetScimGroupMappingEnabledAsync(staged.Id, true);
        (await enable.Should().ThrowAsync<SqlOSScimGrantBoundaryException>())
            .Which.Error.Should().Be(SqlOSScimGrantBoundaryErrors.BoundaryRequired);
        var update = () => f.Admin.UpdateScimGroupMappingAsync(staged.Id, ToUpdate(PatternMapping("org::a::store::{storeId}")));
        (await update.Should().ThrowAsync<SqlOSScimGrantBoundaryException>())
            .Which.Error.Should().Be(SqlOSScimGrantBoundaryErrors.BoundaryRequired);
        (await f.Context.Set<SqlOSScimGroupMapping>().SingleAsync()).IsEnabled.Should().BeFalse();
        (await f.Admin.SetScimGroupMappingEnabledAsync(staged.Id, false)).IsEnabled.Should().BeFalse(
            "disabling never needs a boundary");
    }

    [TestMethod]
    public async Task FixedTargetOutsideTheBoundary_IsRejectedOnCreateUpdateAndEnable()
    {
        using var f = await CreateFixtureAsync();
        var connection = await CreateConnectionAsync(f, OrgA, BoundaryA);

        foreach (var request in new[]
        {
            FixedMapping("Copied config", StoreB9001),
            FixedMapping("Acme-looking ID", AcmeLookingStoreInB),
            new SqlOSCreateScimGroupMappingRequest(SqlOSScimGroupMappingMatchTypes.DisplayName, "Literal template", null, null, StoreManager, null, StoreB9001),
            new SqlOSCreateScimGroupMappingRequest(SqlOSScimGroupMappingMatchTypes.Pattern, null, null, DocumentedPattern, StoreManager, null, "org::b::store::9001")
        })
        {
            var create = () => f.Admin.CreateScimGroupMappingAsync(connection.Id, request);
            var error = (await create.Should().ThrowAsync<SqlOSScimGrantBoundaryException>()).Which;
            error.Error.Should().Be(SqlOSScimGrantBoundaryErrors.ResourceOutsideBoundary);
            error.GrantBoundaryResourceId.Should().Be(BoundaryA);
        }
        (await f.Context.Set<SqlOSScimGroupMapping>().CountAsync()).Should().Be(0);

        var mapping = await f.Admin.CreateScimGroupMappingAsync(connection.Id, FixedMapping("Store 42 Managers", StoreA42));
        await PushGroupAsync(f, connection, "grp-fixed", "Store 42 Managers");
        var grantId = (await f.Context.Set<SqlOSFgaGrant>().SingleAsync()).Id;

        var update = () => f.Admin.UpdateScimGroupMappingAsync(mapping.Id, ToUpdate(FixedMapping("Store 42 Managers", StoreB9001)));
        (await update.Should().ThrowAsync<SqlOSScimGrantBoundaryException>())
            .Which.Error.Should().Be(SqlOSScimGrantBoundaryErrors.ResourceOutsideBoundary);
        (await f.Context.Set<SqlOSFgaGrant>().SingleAsync()).Id.Should().Be(grantId, "a rejected update must not revoke or mutate");
        (await f.Context.Set<SqlOSScimGroupMapping>().SingleAsync()).ResourceId.Should().Be(StoreA42);

        var staged = await f.Admin.CreateScimGroupMappingAsync(connection.Id, FixedMapping("Staged", StoreB9001, enabled: false));
        var enable = () => f.Admin.SetScimGroupMappingEnabledAsync(staged.Id, true);
        (await enable.Should().ThrowAsync<SqlOSScimGrantBoundaryException>())
            .Which.Error.Should().Be(SqlOSScimGrantBoundaryErrors.ResourceOutsideBoundary);
    }

    [TestMethod]
    public async Task FixedTargetWithACyclicHierarchy_IsRejected()
    {
        using var f = await CreateFixtureAsync();
        AddResource(f.Context, "org::a::loop::1", "org::a::loop::2", "region", "Loop 1");
        AddResource(f.Context, "org::a::loop::2", "org::a::loop::1", "region", "Loop 2");
        await f.Context.SaveChangesAsync();
        var connection = await CreateConnectionAsync(f, OrgA, BoundaryA);

        var create = () => f.Admin.CreateScimGroupMappingAsync(connection.Id, FixedMapping("Loop", "org::a::loop::1"));

        (await create.Should().ThrowAsync<SqlOSScimGrantBoundaryException>())
            .Which.Error.Should().Be(SqlOSScimGrantBoundaryErrors.HierarchyInvalid);

        // A row saved before the rule existed is reported, not trusted.
        f.Context.Set<SqlOSScimGroupMapping>().Add(new SqlOSScimGroupMapping
        {
            Id = "scmap_loop",
            ConnectionId = connection.Id,
            Source = SqlOSScimSources.Dashboard,
            MatchType = SqlOSScimGroupMappingMatchTypes.DisplayName,
            GroupDisplayName = "Loop",
            RoleKey = StoreManager,
            ResourceId = "org::a::loop::1",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await f.Context.SaveChangesAsync();
        StatusFor(ToJson(await f.Admin.ListScimGroupMappingsAsync(connection.Id))["data"]!.AsArray(), "Loop").Should().Be("hierarchy_invalid");
    }

    [TestMethod]
    public async Task DanglingParentLink_CannotProveMembership()
    {
        using var f = await CreateFixtureAsync();
        AddResource(f.Context, "org::a::store::orphan", "org::a::region::deleted", "store", "Store whose parent row is gone");
        await f.Context.SaveChangesAsync();
        var connection = await CreateConnectionAsync(f, OrgA, BoundaryA);

        var create = () => f.Admin.CreateScimGroupMappingAsync(connection.Id, FixedMapping("Orphan", "org::a::store::orphan"));

        (await create.Should().ThrowAsync<SqlOSScimGrantBoundaryException>())
            .Which.Error.Should().Be(SqlOSScimGrantBoundaryErrors.ResourceOutsideBoundary);
    }

    [TestMethod]
    public async Task FixedTargetThatDoesNotExistYet_IsAcceptedAndStillBoundedAtGrantTime()
    {
        using var f = await CreateFixtureAsync();
        var connection = await CreateConnectionAsync(f, OrgA, BoundaryA);
        await f.Admin.CreateScimGroupMappingAsync(connection.Id, FixedMapping("Future Store", "org::a::store::77"));

        await PushGroupAsync(f, connection, "grp-future", "Future Store");
        (await f.Context.Set<SqlOSScimSyncEvent>().CountAsync(x => x.Action == "scim.grant.resource_missing")).Should().Be(1);

        AddResource(f.Context, "org::a::store::77", BoundaryB, "store", "Created in the wrong tenant");
        await f.Context.SaveChangesAsync();
        await PushGroupAsync(f, connection, "grp-future", "Future Store");

        (await f.Context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
        (await f.Context.Set<SqlOSScimSyncEvent>().CountAsync(x => x.Action == "scim.grant.outside_boundary")).Should().Be(1);
    }

    [TestMethod]
    public async Task ChangingTheBoundary_RevokesOnlyManagedGrantsOutsideTheNewBoundary()
    {
        using var f = await CreateFixtureAsync();
        var connection = await CreateConnectionAsync(f, OrgA, BoundaryA);
        await f.Admin.CreateScimGroupMappingAsync(connection.Id, FixedMapping("Store 42 Managers", StoreA42));
        await f.Admin.CreateScimGroupMappingAsync(connection.Id, FixedMapping("West Store 7 Managers", StoreAWest7));
        await PushGroupAsync(f, connection, "grp-42", "Store 42 Managers");
        await PushGroupAsync(f, connection, "grp-7", "West Store 7 Managers");
        (await f.Context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(2);

        var updated = await f.Admin.UpdateScimConnectionAsync(connection.Id, new SqlOSUpdateScimConnectionRequest(connection.DisplayName, true)
        {
            GrantBoundaryResourceId = $"  {RegionAWest}  "
        });

        updated.GrantBoundaryResourceId.Should().Be(RegionAWest);
        (await f.Context.Set<SqlOSFgaGrant>().SingleAsync()).ResourceId.Should().Be(StoreAWest7);
        (await f.Context.Set<SqlOSScimManagedGrant>().SingleAsync(x => x.RevokedAt != null)).ResourceId.Should().Be(StoreA42);
        var revoked = await f.Context.Set<SqlOSScimSyncEvent>().SingleAsync(x => x.Action == "scim.grant.revoked");
        var evidence = JsonNode.Parse(revoked.DataJson!)!.AsObject();
        evidence["reason"]!.GetValue<string>().Should().Be("grant_boundary_changed");
        evidence["revokedManagedGrantCount"]!.GetValue<int>().Should().Be(1);
        var audit = await f.Context.Set<SqlOSAuditEvent>().SingleAsync(x => x.Action == "scim.connection.grant_boundary_changed");
        audit.OrganizationId.Should().Be(OrgA);
        audit.MetadataJson.Should().Contain(BoundaryA).And.Contain(RegionAWest);

        await f.Admin.UpdateScimConnectionAsync(connection.Id, new SqlOSUpdateScimConnectionRequest("Renamed directory", true));
        (await f.Context.Set<SqlOSScimConnection>().SingleAsync()).GrantBoundaryResourceId.Should().Be(RegionAWest,
            "omitting the boundary on update leaves it unchanged");
        (await f.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.Action == "scim.connection.grant_boundary_changed")).Should().Be(1);
    }

    [TestMethod]
    public async Task SettingTheBoundary_RequiresAnExistingResourceAndValidInput()
    {
        using var f = await CreateFixtureAsync();

        var missing = () => f.Admin.CreateScimConnectionAsync(new SqlOSCreateScimConnectionRequest(OrgA, "Acme SCIM") { GrantBoundaryResourceId = "org::missing" });
        (await missing.Should().ThrowAsync<SqlOSScimGrantBoundaryException>())
            .Which.Error.Should().Be(SqlOSScimGrantBoundaryErrors.BoundaryNotFound);
        var tooLong = () => f.Admin.CreateScimConnectionAsync(new SqlOSCreateScimConnectionRequest(OrgA, "Acme SCIM") { GrantBoundaryResourceId = new string('x', 257) });
        (await tooLong.Should().ThrowAsync<SqlOSScimGrantBoundaryException>())
            .Which.Error.Should().Be(SqlOSScimGrantBoundaryErrors.BoundaryInvalid);
        (await f.Context.Set<SqlOSScimConnection>().CountAsync()).Should().Be(0);

        var created = await f.Admin.CreateScimConnectionAsync(new SqlOSCreateScimConnectionRequest(OrgA, "Acme SCIM") { GrantBoundaryResourceId = BoundaryA });
        var change = () => f.Admin.UpdateScimConnectionAsync(created.ConnectionId, new SqlOSUpdateScimConnectionRequest("Acme SCIM", true) { GrantBoundaryResourceId = "org::missing" });
        (await change.Should().ThrowAsync<SqlOSScimGrantBoundaryException>())
            .Which.Error.Should().Be(SqlOSScimGrantBoundaryErrors.BoundaryNotFound);
        (await f.Context.Set<SqlOSScimConnection>().SingleAsync()).GrantBoundaryResourceId.Should().Be(BoundaryA);
    }

    [TestMethod]
    public async Task ListingConnectionsAndMappings_ProjectsTheBoundaryState()
    {
        using var f = await CreateFixtureAsync();
        var bounded = await CreateConnectionAsync(f, OrgA, BoundaryA);
        var legacy = await CreateLegacyConnectionAsync(f, OrgB);
        await f.Admin.CreateScimGroupMappingAsync(bounded.Id, FixedMapping("Store 42 Managers", StoreA42));
        await f.Admin.CreateScimGroupMappingAsync(bounded.Id, PatternMapping("org::a::store::{storeId}"));
        await f.Admin.CreateScimGroupMappingAsync(bounded.Id, FixedMapping("Future", "org::a::store::77"));
        await InsertLegacyPatternMappingAsync(f, legacy.Id, "store::{storeId}");
        f.Context.Set<SqlOSScimGroupMapping>().Add(new SqlOSScimGroupMapping
        {
            Id = "scmap_outside",
            ConnectionId = bounded.Id,
            Source = SqlOSScimSources.Dashboard,
            MatchType = SqlOSScimGroupMappingMatchTypes.DisplayName,
            GroupDisplayName = "Planted",
            RoleKey = StoreManager,
            ResourceId = StoreB9001,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await f.Context.SaveChangesAsync();

        var connection = ToJson(await f.Admin.GetScimConnectionAsync(bounded.Id));
        connection["grantBoundaryResourceId"]!.GetValue<string>().Should().Be(BoundaryA);
        connection["grantBoundary"]!["status"]!.GetValue<string>().Should().Be("configured");
        connection["grantBoundary"]!["resourceName"]!.GetValue<string>().Should().Be("Acme");
        var legacyView = ToJson(await f.Admin.ListOrganizationScimConnectionsAsync(OrgB));
        legacyView["data"]![0]!["grantBoundary"]!["status"]!.GetValue<string>().Should().Be("missing");

        var mappings = ToJson(await f.Admin.ListScimGroupMappingsAsync(bounded.Id, pageSize: 50))["data"]!.AsArray();
        StatusFor(mappings, "Store 42 Managers").Should().Be("within");
        StatusFor(mappings, "Future").Should().Be("resource_not_found");
        StatusFor(mappings, "Planted").Should().Be("outside");
        mappings.Single(item => item!["groupPattern"]?.GetValue<string>() == DocumentedPattern)!["grantBoundaryStatus"]!
            .GetValue<string>().Should().Be("checked_at_grant_time");
        var legacyMappings = ToJson(await f.Admin.ListScimGroupMappingsAsync(legacy.Id))["data"]!.AsArray();
        legacyMappings.Single()!["grantBoundaryStatus"]!.GetValue<string>().Should().Be("boundary_missing");
    }

    [TestMethod]
    public async Task SeededFixedMappingOutsideTheBoundary_FailsReconciliationWithATypedError()
    {
        using var f = await CreateFixtureAsync(SeedAcme(scim =>
        {
            scim.GrantBoundaryResourceId = BoundaryA;
            scim.MapGroup("Copied Beta Managers", mapping =>
            {
                mapping.RoleKey = StoreManager;
                mapping.ResourceId = StoreB9001;
            });
        }));

        var reconcile = () => f.Admin.UpsertSeededScimConnectionsAsync();

        (await reconcile.Should().ThrowAsync<SqlOSScimGrantBoundaryException>())
            .Which.Error.Should().Be(SqlOSScimGrantBoundaryErrors.ResourceOutsideBoundary);
        (await f.Context.Set<SqlOSScimConnection>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task SeededBoundaryThatDoesNotExist_FailsReconciliation()
    {
        using var f = await CreateFixtureAsync(SeedAcme(scim => scim.GrantBoundaryResourceId = "org::missing"));

        var reconcile = () => f.Admin.UpsertSeededScimConnectionsAsync();

        (await reconcile.Should().ThrowAsync<SqlOSScimGrantBoundaryException>())
            .Which.Error.Should().Be(SqlOSScimGrantBoundaryErrors.BoundaryNotFound);
        (await f.Context.Set<SqlOSScimConnection>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task SeededConnectionWithoutBoundary_StillStartsButItsMappingsGrantNothing()
    {
        using var f = await CreateFixtureAsync(SeedAcme(scim => scim.MapGroupPattern(DocumentedPattern, mapping =>
        {
            mapping.RoleKey = StoreManager;
            mapping.ResourceIdTemplate = "store::{storeId}";
        })));

        await f.Admin.UpsertSeededScimConnectionsAsync();
        var fingerprint = (await f.Context.Set<SqlOSScimConnection>().SingleAsync()).ConfigurationFingerprint;
        await f.Admin.UpsertSeededScimConnectionsAsync();
        var connection = await f.Context.Set<SqlOSScimConnection>().SingleAsync();
        connection.ConfigurationFingerprint.Should().Be(fingerprint, "reconciliation stays idempotent");
        connection.GrantBoundaryResourceId.Should().BeNull();
        await PushGroupAsync(f, connection, "grp-seed", "Store-42-Managers");

        (await f.Context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
        (await f.Context.Set<SqlOSScimSyncEvent>().CountAsync(x => x.Action == "scim.grant.boundary_missing")).Should().Be(1);
    }

    [TestMethod]
    public async Task SeededBoundary_ChangesAreReconciledAuthoritativelyAndRevokeOutsideGrants()
    {
        var options = SeedAcme(scim =>
        {
            scim.GrantBoundaryResourceId = BoundaryA;
            scim.MapGroup("Store 42 Managers", mapping =>
            {
                mapping.RoleKey = StoreManager;
                mapping.ResourceId = StoreA42;
            });
            scim.MapGroupPattern(DocumentedPattern, mapping =>
            {
                mapping.RoleKey = StoreManager;
                mapping.ResourceIdTemplate = "org::a::region::west::store::{storeId}";
            });
        });
        using var f = await CreateFixtureAsync(options);
        await f.Admin.UpsertSeededScimConnectionsAsync();
        await f.Admin.UpsertSeededScimConnectionsAsync();
        var connection = await f.Context.Set<SqlOSScimConnection>().SingleAsync();
        connection.GrantBoundaryResourceId.Should().Be(BoundaryA);
        await PushGroupAsync(f, connection, "grp-42", "Store 42 Managers");
        await PushGroupAsync(f, connection, "grp-7", "Store-7-Managers");
        (await f.Context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(2);
        (await f.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.Action == "scim.connection.grant_boundary_changed")).Should().Be(0,
            "creating a seeded connection with its boundary is not a boundary change");

        var seed = options.ScimConnectionSeeds.Single();
        seed.GroupMappings.RemoveAll(mapping => mapping.GroupDisplayName == "Store 42 Managers");
        seed.GrantBoundaryResourceId = RegionAWest;
        await f.Admin.UpsertSeededScimConnectionsAsync();

        (await f.Context.Set<SqlOSScimConnection>().SingleAsync()).GrantBoundaryResourceId.Should().Be(RegionAWest);
        (await f.Context.Set<SqlOSFgaGrant>().SingleAsync()).ResourceId.Should().Be(StoreAWest7);
        (await f.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.Action == "scim.connection.grant_boundary_changed")).Should().Be(1);

        seed.GrantBoundaryResourceId = null;
        await f.Admin.UpsertSeededScimConnectionsAsync();

        (await f.Context.Set<SqlOSScimConnection>().SingleAsync()).GrantBoundaryResourceId.Should().BeNull(
            "code-owned configuration is authoritative");
        (await f.Context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0, "no managed grant is inside a missing boundary");
        (await f.Context.Set<SqlOSScimManagedGrant>().CountAsync(x => x.RevokedAt == null)).Should().Be(0);
    }

    private static string StatusFor(JsonArray mappings, string groupDisplayName)
        => mappings.Single(item => item!["groupDisplayName"]?.GetValue<string>() == groupDisplayName)!["grantBoundaryStatus"]!.GetValue<string>();

    private static JsonNode ToJson(object value)
        => JsonNode.Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

    private static SqlOSAuthServerOptions SeedAcme(Action<SqlOSScimConnectionSeedOptions> configure)
    {
        var options = new SqlOSAuthServerOptions();
        options.SeedScimConnection("acme", scim =>
        {
            scim.OrganizationSlug = "acme";
            scim.DisplayName = "Acme directory";
            scim.Token = StrongSeedToken;
            configure(scim);
        });
        return options;
    }

    private static SqlOSCreateScimGroupMappingRequest PatternMapping(string template, bool enabled = true)
        => new(SqlOSScimGroupMappingMatchTypes.Pattern, null, null, DocumentedPattern, StoreManager, null, template, "Directory store managers", enabled);

    private static SqlOSCreateScimGroupMappingRequest FixedMapping(string groupDisplayName, string resourceId, bool enabled = true)
        => new(SqlOSScimGroupMappingMatchTypes.DisplayName, groupDisplayName, null, null, StoreManager, resourceId, null, null, enabled);

    private static SqlOSUpdateScimGroupMappingRequest ToUpdate(SqlOSCreateScimGroupMappingRequest request)
        => new(request.MatchType, request.GroupDisplayName, request.GroupExternalId, request.GroupPattern, request.RoleKey, request.ResourceId, request.ResourceIdTemplate, request.Description, request.Enabled);

    private static async Task<SqlOSScimConnection> CreateConnectionAsync(Fixture f, string organizationId, string boundary)
    {
        var draft = await f.Admin.CreateScimConnectionDraftAsync(
            new SqlOSCreateScimConnectionRequest(organizationId, $"{organizationId} directory", false)
            {
                GrantBoundaryResourceId = boundary
            });
        await f.Admin.RotateScimTokenAsync(draft.Id);
        return await f.Admin.SetScimConnectionEnabledAsync(draft.Id, true);
    }

    private static async Task<SqlOSScimConnection> CreateLegacyConnectionAsync(Fixture f, string organizationId)
    {
        var draft = await f.Admin.CreateScimConnectionDraftAsync(
            new SqlOSCreateScimConnectionRequest(organizationId, $"{organizationId} directory", false));
        await f.Admin.RotateScimTokenAsync(draft.Id);
        return await f.Admin.SetScimConnectionEnabledAsync(draft.Id, true);
    }

    // Mapping rows exactly as SqlOS 7.2.0 and earlier stored them, before a boundary existed.
    private static async Task<SqlOSScimGroupMapping> InsertLegacyPatternMappingAsync(Fixture f, string connectionId, string template)
    {
        var mapping = new SqlOSScimGroupMapping
        {
            Id = $"scmap_legacy_{connectionId}",
            ConnectionId = connectionId,
            SourceKey = $"pattern:{DocumentedPattern}",
            Source = SqlOSScimSources.Dashboard,
            MatchType = SqlOSScimGroupMappingMatchTypes.Pattern,
            GroupPattern = DocumentedPattern,
            RoleKey = StoreManager,
            ResourceIdTemplate = template,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        f.Context.Set<SqlOSScimGroupMapping>().Add(mapping);
        await f.Context.SaveChangesAsync();
        return mapping;
    }

    private static async Task InsertLegacyManagedGrantAsync(Fixture f, string connectionId, string mappingId, string groupId, string groupExternalId, string resourceId)
    {
        var group = await f.Context.Set<SqlOSFgaUserGroup>().SingleAsync(x => x.Id == groupId);
        f.Context.Set<SqlOSFgaGrant>().Add(new SqlOSFgaGrant
        {
            Id = "grant_planted",
            SubjectId = group.SubjectId,
            ResourceId = resourceId,
            RoleId = "role_store_manager",
            CreatedAt = DateTime.UtcNow
        });
        f.Context.Set<SqlOSScimManagedGrant>().Add(new SqlOSScimManagedGrant
        {
            Id = "scgrant_planted",
            ConnectionId = connectionId,
            MappingId = mappingId,
            GroupExternalId = groupExternalId,
            FgaGroupId = groupId,
            FgaGroupSubjectId = group.SubjectId,
            GrantId = "grant_planted",
            RoleId = "role_store_manager",
            ResourceId = resourceId,
            CreatedAt = DateTime.UtcNow
        });
        await f.Context.SaveChangesAsync();
    }

    private static async Task<string> PushUserAsync(Fixture f, SqlOSScimConnection connection, string name)
    {
        var user = await f.Scim.UpsertUserAsync(connection, new JsonObject
        {
            ["externalId"] = $"idp-{name}",
            ["userName"] = $"{name}@{connection.OrganizationId}.example.test",
            ["displayName"] = name,
            ["active"] = true
        }, replace: false);
        return user["id"]!.GetValue<string>();
    }

    private static async Task<string> PushGroupAsync(Fixture f, SqlOSScimConnection connection, string externalId, string displayName, params string[] memberIds)
    {
        var group = await f.Scim.UpsertGroupAsync(connection, new JsonObject
        {
            ["externalId"] = externalId,
            ["displayName"] = displayName,
            ["members"] = new JsonArray(memberIds.Select(id => (JsonNode)new JsonObject { ["value"] = id }).ToArray())
        }, replace: false);
        return group["id"]!.GetValue<string>();
    }

    private static async Task<string> SubjectIdAsync(Fixture f, string userId)
        => (await f.Context.Set<SqlOSScimExternalId>().SingleAsync(x => x.ResourceType == "User" && x.EntityId == userId)).FgaSubjectId!;

    private static void AddResource(TestSqlOSInMemoryDbContext context, string id, string? parentId, string type, string name)
        => context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
        {
            Id = id,
            ParentId = parentId,
            ResourceTypeId = type,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

    private static async Task<Fixture> CreateFixtureAsync(SqlOSAuthServerOptions? optionsValue = null)
    {
        var context = new TestSqlOSInMemoryDbContext(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        var now = DateTime.UtcNow;
        context.Set<SqlOSOrganization>().AddRange(
            new SqlOSOrganization { Id = OrgA, Slug = "acme", Name = "Acme", CreatedAt = now },
            new SqlOSOrganization { Id = OrgB, Slug = "beta", Name = "Beta", CreatedAt = now });
        context.Set<SqlOSFgaResourceType>().AddRange(
            new SqlOSFgaResourceType { Id = "org", Name = "Organization" },
            new SqlOSFgaResourceType { Id = "region", Name = "Region" },
            new SqlOSFgaResourceType { Id = "store", Name = "Store" });
        AddResource(context, BoundaryA, null, "org", "Acme");
        AddResource(context, StoreA42, BoundaryA, "store", "Acme store 42");
        AddResource(context, LegacyStoreA42, BoundaryA, "store", "Acme legacy store 42");
        AddResource(context, RegionAWest, BoundaryA, "region", "Acme west");
        AddResource(context, StoreAWest7, RegionAWest, "store", "Acme west store 7");
        AddResource(context, BoundaryB, null, "org", "Beta");
        AddResource(context, StoreB9001, BoundaryB, "store", "Beta store 9001");
        AddResource(context, LegacyStoreB9001, BoundaryB, "store", "Beta legacy store 9001");
        AddResource(context, AcmeLookingStoreInB, BoundaryB, "store", "Beta store with an Acme-looking ID");
        context.Set<SqlOSFgaPermission>().Add(new SqlOSFgaPermission { Id = "perm_store_manage", Key = ManageStore, Name = "Manage store", ResourceTypeId = "store" });
        context.Set<SqlOSFgaRole>().Add(new SqlOSFgaRole { Id = "role_store_manager", Key = StoreManager, Name = "Store manager" });
        context.Set<SqlOSFgaRolePermission>().Add(new SqlOSFgaRolePermission { RoleId = "role_store_manager", PermissionId = "perm_store_manage" });
        await context.SaveChangesAsync();

        var options = Options.Create(optionsValue ?? new SqlOSAuthServerOptions());
        var crypto = new SqlOSCryptoService(context, options);
        return new Fixture(
            context,
            new SqlOSAdminService(context, options, crypto),
            new SqlOSScimService(context, options, crypto),
            new SqlOSFgaAuthService(context, Options.Create(new SqlOSFgaOptions()), NullLogger<SqlOSFgaAuthService>.Instance));
    }

    private sealed record Fixture(
        TestSqlOSInMemoryDbContext Context,
        SqlOSAdminService Admin,
        SqlOSScimService Scim,
        SqlOSFgaAuthService Fga) : IDisposable
    {
        public void Dispose() => Context.Dispose();
    }
}

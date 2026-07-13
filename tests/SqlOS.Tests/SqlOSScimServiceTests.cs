using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Fga.Models;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSScimServiceTests
{
    private const string StrongSeedToken = "scim_seed_token_0123456789abcdef";

    [TestMethod]
    public async Task SeededScimConnections_ReconcileConnectionTokenAndMappings()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        var optionsValue = new SqlOSAuthServerOptions
        {
            PublicOrigin = "https://app.example.test"
        };
        optionsValue.SeedScimConnection("acme", seed =>
        {
            seed.OrganizationSlug = "acme";
            seed.DisplayName = "Acme Directory";
            seed.Token = StrongSeedToken;
            seed.MapGroup("Store 100 Managers", mapping =>
            {
                mapping.RoleKey = "store_manager";
                mapping.ResourceId = "store_100";
                mapping.Description = "Seeded store manager mapping";
            });
        });
        var harness = CreateHarness(context, optionsValue);

        await harness.Admin.UpsertSeededScimConnectionsAsync();

        var connection = await context.Set<SqlOSScimConnection>().SingleAsync();
        var mapping = await context.Set<SqlOSScimGroupMapping>().SingleAsync();
        connection.OrganizationId.Should().Be("org_acme");
        connection.DisplayName.Should().Be("Acme Directory");
        connection.Source.Should().Be(SqlOSScimSources.Seeded);
        connection.TokenPrefix.Should().Be("scim_seed_to");
        connection.TokenHash.Should().NotBe(StrongSeedToken);
        mapping.Source.Should().Be(SqlOSScimSources.Seeded);
        mapping.SourceKey.Should().Be("name:Store 100 Managers");
        mapping.RoleKey.Should().Be("store_manager");
        mapping.ResourceId.Should().Be("store_100");

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = $"Bearer {StrongSeedToken}";
        var authenticated = await harness.Scim.AuthenticateAsync(httpContext);
        authenticated.Id.Should().Be(connection.Id);
    }

    [TestMethod]
    public async Task SeededScimConnection_RejectsWeakBearerTokenBeforeTrackingConnection()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedScimConnection("acme", seed =>
        {
            seed.OrganizationSlug = "acme";
            seed.Token = "too-short";
        });
        var harness = CreateHarness(context, optionsValue);

        var act = async () => await harness.Admin.UpsertSeededScimConnectionsAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*at least 32 characters*");
        context.ChangeTracker.Entries<SqlOSScimConnection>().Should().BeEmpty();
        (await context.Set<SqlOSScimConnection>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task SeededScimConnection_RejectsBearerTokenContainingWhitespace()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedScimConnection("acme", seed =>
        {
            seed.OrganizationSlug = "acme";
            seed.Token = "scim_seed_token_with forbidden_space_0123456789";
        });
        var harness = CreateHarness(context, optionsValue);

        var act = async () => await harness.Admin.UpsertSeededScimConnectionsAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot contain whitespace*");
        (await context.Set<SqlOSScimConnection>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task SeededScimConnection_RejectsAmbiguousInlineAndSecretTokenSources()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedScimConnection("acme", seed =>
        {
            seed.OrganizationSlug = "acme";
            seed.Token = StrongSeedToken;
            seed.TokenSecretName = "ACME_SCIM_TOKEN";
        });
        var harness = CreateHarness(context, optionsValue);

        var act = async () => await harness.Admin.UpsertSeededScimConnectionsAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*either Token or TokenSecretName, not both*");
        (await context.Set<SqlOSScimConnection>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task SeededConnection_ConfigOwnsTokenAndDashboardLifecycle()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedScimConnection("acme", seed =>
        {
            seed.OrganizationSlug = "acme";
            seed.Token = StrongSeedToken;
        });
        var harness = CreateHarness(context, optionsValue);
        await harness.Admin.UpsertSeededScimConnectionsAsync();
        var connection = await context.Set<SqlOSScimConnection>().SingleAsync();

        var rotate = async () => await harness.Admin.RotateScimTokenAsync(connection.Id);
        var disable = async () => await harness.Admin.SetScimConnectionEnabledAsync(connection.Id, false);
        await rotate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*startup configuration*");
        await disable.Should().ThrowAsync<InvalidOperationException>().WithMessage("*startup configuration*");

        var configuredSeed = optionsValue.ScimConnectionSeeds.Single();
        configuredSeed.Token = null;
        var missingConfiguredToken = async () => await harness.Admin.UpsertSeededScimConnectionsAsync();
        await missingConfiguredToken.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*enabled*did not provide a token*");

        const string replacement = "scim_replacement_seed_token_0123456789abcdef";
        configuredSeed.Token = replacement;
        await harness.Admin.UpsertSeededScimConnectionsAsync();

        var oldContext = new DefaultHttpContext();
        oldContext.Request.Headers.Authorization = $"Bearer {StrongSeedToken}";
        var oldAuth = async () => await harness.Scim.AuthenticateAsync(oldContext);
        var error = await oldAuth.Should().ThrowAsync<SqlOSScimException>();
        error.Which.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        var replacementContext = new DefaultHttpContext();
        replacementContext.Request.Headers.Authorization = $"Bearer {replacement}";
        (await harness.Scim.AuthenticateAsync(replacementContext)).Id.Should().Be(connection.Id);

        optionsValue.ScimConnectionSeeds.Clear();
        optionsValue.SeedScimConnection("acme-renamed", seed =>
        {
            seed.OrganizationSlug = "acme";
            seed.Token = replacement;
        });
        await harness.Admin.UpsertSeededScimConnectionsAsync();
        var renamedConnections = await context.Set<SqlOSScimConnection>().OrderBy(item => item.CreatedAt).ToListAsync();
        renamedConnections.Should().HaveCount(2);
        renamedConnections[0].IsEnabled.Should().BeFalse();
        renamedConnections[0].TokenHash.Should().BeNull();
        renamedConnections[1].IsEnabled.Should().BeTrue();
        (await harness.Scim.AuthenticateAsync(replacementContext)).Id.Should().Be(renamedConnections[1].Id);

        optionsValue.ScimConnectionSeeds.Clear();
        await harness.Admin.UpsertSeededScimConnectionsAsync();
        var orphanedAuth = async () => await harness.Scim.AuthenticateAsync(replacementContext);
        var orphanedError = await orphanedAuth.Should().ThrowAsync<SqlOSScimException>();
        orphanedError.Which.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        (await context.Set<SqlOSScimConnection>().CountAsync(item => item.IsEnabled)).Should().Be(0);

        optionsValue.SeedScimConnection("acme-renamed", seed => seed.OrganizationSlug = "acme");
        var resurrectWithoutSecret = async () => await harness.Admin.UpsertSeededScimConnectionsAsync();
        await resurrectWithoutSecret.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*enabled*did not provide a token*");
    }

    [TestMethod]
    public async Task SeededScimConnection_EnabledWithoutToken_FailsBeforeCreatingConnection()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedScimConnection("acme", seed =>
        {
            seed.OrganizationSlug = "acme";
            seed.Enabled = true;
        });
        var harness = CreateHarness(context, optionsValue);

        var act = async () => await harness.Admin.UpsertSeededScimConnectionsAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*enabled*did not provide a token*");
        context.ChangeTracker.Entries<SqlOSScimConnection>().Should().BeEmpty();
        (await context.Set<SqlOSScimConnection>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task SeededScimConnection_DisabledWithoutToken_IsPersistedForLaterRotation()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedScimConnection("acme", seed =>
        {
            seed.OrganizationSlug = "acme";
            seed.Enabled = false;
        });
        var harness = CreateHarness(context, optionsValue);

        await harness.Admin.UpsertSeededScimConnectionsAsync();

        var connection = await context.Set<SqlOSScimConnection>().SingleAsync();
        connection.IsEnabled.Should().BeFalse();
        connection.TokenHash.Should().BeNull();
        connection.TokenPrefix.Should().BeNull();
    }

    [TestMethod]
    public async Task SeedReconciliation_RevokesAuthorizationForRemovedMappingsAndDisabledConnections()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        await SeedFgaRoleAndResourceAsync(context);
        var optionsValue = new SqlOSAuthServerOptions();
        optionsValue.SeedScimConnection("acme", seed =>
        {
            seed.OrganizationSlug = "acme";
            seed.Token = StrongSeedToken;
            seed.MapGroup("Store 100 Managers", mapping =>
            {
                mapping.RoleKey = "store_manager";
                mapping.ResourceId = "store_100";
            });
        });
        var harness = CreateHarness(context, optionsValue);
        await harness.Admin.UpsertSeededScimConnectionsAsync();
        var connection = await context.Set<SqlOSScimConnection>().SingleAsync();
        var user = await harness.Scim.UpsertUserAsync(connection, new JsonObject
        {
            ["externalId"] = "seed-user",
            ["userName"] = "seed.user@example.test",
            ["displayName"] = "Seed User",
            ["active"] = true
        }, replace: false);
        await harness.Scim.UpsertGroupAsync(connection, new JsonObject
        {
            ["externalId"] = "seed-group",
            ["displayName"] = "Store 100 Managers",
            ["members"] = new JsonArray(new JsonObject { ["value"] = user["id"]!.GetValue<string>() })
        }, replace: false);
        (await context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(1);

        var seed = optionsValue.ScimConnectionSeeds.Single();
        seed.GroupMappings.Clear();
        await harness.Admin.UpsertSeededScimConnectionsAsync();

        (await context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSScimManagedGrant>().CountAsync(item => item.RevokedAt != null)).Should().Be(1);
        (await context.Set<SqlOSScimGroupMapping>().SingleAsync()).IsEnabled.Should().BeFalse();

        seed.MapGroup("Store 100 Managers", mapping =>
        {
            mapping.RoleKey = "store_manager";
            mapping.ResourceId = "store_100";
        });
        await harness.Admin.UpsertSeededScimConnectionsAsync();
        await harness.Scim.UpsertGroupAsync(connection, new JsonObject
        {
            ["externalId"] = "seed-group",
            ["displayName"] = "Store 100 Managers",
            ["members"] = new JsonArray(new JsonObject { ["value"] = user["id"]!.GetValue<string>() })
        }, replace: true);
        (await context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(1);

        seed.Enabled = false;
        await harness.Admin.UpsertSeededScimConnectionsAsync();

        (await context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSScimConnection>().SingleAsync()).IsEnabled.Should().BeFalse();
        (await context.Set<SqlOSScimManagedGrant>().CountAsync(item => item.RevokedAt != null)).Should().Be(2);
    }

    [TestMethod]
    public async Task CreateScimConnection_ReturnsStrongOneTimeTokenAndPartialPrefix()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        var harness = CreateHarness(context);

        var result = await harness.Admin.CreateScimConnectionAsync(
            new SqlOSCreateScimConnectionRequest("org_acme", "Acme SCIM", true));

        result.Token.Length.Should().BeGreaterThanOrEqualTo(32);
        result.TokenPrefix.Length.Should().Be(12);
        result.TokenPrefix.Should().NotBe(result.Token);
        result.Token.Should().StartWith(result.TokenPrefix);
        var connection = await context.Set<SqlOSScimConnection>().SingleAsync();
        connection.TokenHash.Should().NotBe(result.Token);
        connection.TokenPrefix.Should().Be(result.TokenPrefix);
    }

    [TestMethod]
    public async Task LowLevelConnectionCreation_CannotEnableAConnectionWithoutIssuingAToken()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        var harness = CreateHarness(context);

        var act = async () => await harness.Admin.CreateScimConnectionDraftAsync(
            new SqlOSCreateScimConnectionRequest("org_acme", "Unsafe SCIM", true));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*CreateScimConnectionAsync*");
        (await context.Set<SqlOSScimConnection>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task DisabledConnection_CannotBeEnabledUntilATokenHasBeenRotated()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        var harness = CreateHarness(context);
        var connection = await harness.Admin.CreateScimConnectionDraftAsync(
            new SqlOSCreateScimConnectionRequest("org_acme", "Draft SCIM", false));

        var act = async () => await harness.Admin.SetScimConnectionEnabledAsync(connection.Id, true);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Rotate a bearer token*");
        (await context.Set<SqlOSScimConnection>().SingleAsync()).IsEnabled.Should().BeFalse();
    }

    [TestMethod]
    public async Task UpsertUserAsync_ProvisionsMembershipFgaSubjectSyncEventAndAudit()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        var harness = CreateHarness(context);
        var connection = await CreateConnectionAsync(harness.Admin);

        var result = await harness.Scim.UpsertUserAsync(connection, new JsonObject
        {
            ["externalId"] = "idp-user-1",
            ["userName"] = "ada@example.test",
            ["displayName"] = "Ada Lovelace",
            ["active"] = true,
            ["emails"] = new JsonArray(new JsonObject
            {
                ["value"] = "ada@example.test",
                ["primary"] = true,
                ["type"] = "work"
            })
        }, replace: false);

        var userId = result["id"]!.GetValue<string>();
        var user = await context.Set<SqlOSUser>().SingleAsync(x => x.Id == userId);
        var membership = await context.Set<SqlOSMembership>().SingleAsync(x => x.OrganizationId == "org_acme" && x.UserId == userId);
        var email = await context.Set<SqlOSUserEmail>().SingleAsync(x => x.UserId == userId);
        var externalLink = await context.Set<SqlOSScimExternalId>().SingleAsync(x => x.ConnectionId == connection.Id && x.ResourceType == "User");
        var fgaUser = await context.Set<SqlOSFgaUser>().SingleAsync();

        user.DisplayName.Should().Be("Ada Lovelace");
        user.DefaultEmail.Should().Be("ada@example.test");
        membership.IsActive.Should().BeTrue();
        email.IsPrimary.Should().BeTrue();
        email.IsVerified.Should().BeTrue();
        externalLink.ExternalId.Should().Be("idp-user-1");
        externalLink.EntityId.Should().Be(userId);
        externalLink.FgaSubjectId.Should().NotBeNullOrWhiteSpace();
        fgaUser.SubjectId.Should().Be(externalLink.FgaSubjectId);
        fgaUser.IsActive.Should().BeTrue();
        (await context.Set<SqlOSScimSyncEvent>().AnyAsync(x => x.Action == "scim.user.created" && x.Result == "success")).Should().BeTrue();
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.Action == "scim.user.created" && x.Source == "scim")).Should().BeTrue();

        await harness.Scim.PatchUserAsync(connection, userId, new JsonObject
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:api:messages:2.0:PatchOp"),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "replace",
                ["path"] = "displayName",
                ["value"] = "Ada Byron"
            })
        });

        (await context.Set<SqlOSScimExternalId>().CountAsync(x => x.ConnectionId == connection.Id && x.ResourceType == "User")).Should().Be(1);
        (await context.Set<SqlOSScimExternalId>().SingleAsync(x => x.ConnectionId == connection.Id && x.ResourceType == "User")).ExternalId.Should().Be("idp-user-1");
        (await context.Set<SqlOSUser>().SingleAsync(x => x.Id == userId)).DisplayName.Should().Be("Ada Byron");
    }

    [TestMethod]
    public async Task GroupSync_WithMapping_CreatesGroupMembershipAndManagedFgaGrant()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        await SeedFgaRoleAndResourceAsync(context);
        var harness = CreateHarness(context);
        var connection = await CreateConnectionAsync(harness.Admin);
        await harness.Admin.CreateScimGroupMappingAsync(connection.Id, new SqlOSCreateScimGroupMappingRequest(
            SqlOSScimGroupMappingMatchTypes.DisplayName,
            "Store 100 Managers",
            GroupExternalId: null,
            GroupPattern: null,
            RoleKey: "store_manager",
            ResourceId: "store_100",
            ResourceIdTemplate: null,
            Description: "SCIM store manager access",
            Enabled: true));
        var user = await harness.Scim.UpsertUserAsync(connection, new JsonObject
        {
            ["externalId"] = "idp-user-1",
            ["userName"] = "ada@example.test",
            ["displayName"] = "Ada Lovelace",
            ["active"] = true
        }, replace: false);

        var group = await harness.Scim.UpsertGroupAsync(connection, new JsonObject
        {
            ["externalId"] = "idp-group-1",
            ["displayName"] = "Store 100 Managers",
            ["members"] = new JsonArray(new JsonObject
            {
                ["value"] = "idp-user-1",
                ["display"] = "Ada Lovelace"
            })
        }, replace: false);

        var userId = user["id"]!.GetValue<string>();
        var groupId = group["id"]!.GetValue<string>();
        var groupEntity = await context.Set<SqlOSFgaUserGroup>().SingleAsync(x => x.Id == groupId);
        var userLink = await context.Set<SqlOSScimExternalId>().SingleAsync(x => x.EntityId == userId && x.ResourceType == "User");
        var membership = await context.Set<SqlOSFgaUserGroupMembership>().SingleAsync();
        var grant = await context.Set<SqlOSFgaGrant>().SingleAsync();
        var managedGrant = await context.Set<SqlOSScimManagedGrant>().SingleAsync();

        groupEntity.Name.Should().Be("Store 100 Managers");
        membership.UserGroupId.Should().Be(groupId);
        membership.SubjectId.Should().Be(userLink.FgaSubjectId);
        grant.SubjectId.Should().Be(groupEntity.SubjectId);
        grant.RoleId.Should().Be("role_store_manager");
        grant.ResourceId.Should().Be("store_100");
        managedGrant.GrantId.Should().Be(grant.Id);
        managedGrant.FgaGroupId.Should().Be(groupId);
        managedGrant.RevokedAt.Should().BeNull();
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.Action == "scim.group.member_added" && x.Source == "scim")).Should().BeTrue();
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.Action == "scim.grant.mapped" && x.Source == "scim")).Should().BeTrue();

        var mapping = await context.Set<SqlOSScimGroupMapping>().SingleAsync();
        await harness.Admin.SetScimGroupMappingEnabledAsync(mapping.Id, false);
        await harness.Scim.UpsertGroupAsync(connection, new JsonObject
        {
            ["externalId"] = "idp-group-1",
            ["displayName"] = "Store 100 Managers",
            ["members"] = new JsonArray(new JsonObject { ["value"] = "idp-user-1" })
        }, replace: false);

        (await context.Set<SqlOSFgaGrant>().AnyAsync()).Should().BeFalse();
        (await context.Set<SqlOSScimManagedGrant>().SingleAsync()).RevokedAt.Should().NotBeNull();
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.Action == "scim.grant.revoked" && x.Source == "scim")).Should().BeTrue();
    }

    [TestMethod]
    public async Task GroupSync_MediumMembershipBatch_UsesConstantPersistenceAndSummarizedAudit()
    {
        const int memberCount = 128;
        var saveCounter = new CountingSaveChangesInterceptor();
        using var context = CreateContext(saveCounter);
        await SeedOrganizationAsync(context);
        var harness = CreateHarness(context);
        var connection = await CreateConnectionAsync(harness.Admin);
        var members = new JsonArray();
        for (var index = 0; index < memberCount; index++)
        {
            var externalId = $"idp-user-{index:D3}";
            await harness.Scim.UpsertUserAsync(connection, new JsonObject
            {
                ["externalId"] = externalId,
                ["userName"] = $"person-{index:D3}@example.test",
                ["displayName"] = $"Person {index:D3}",
                ["active"] = true
            }, replace: false);
            members.Add(new JsonObject { ["value"] = externalId });
        }

        saveCounter.Reset();
        var group = await harness.Scim.UpsertGroupAsync(connection, new JsonObject
        {
            ["externalId"] = "idp-group-medium",
            ["displayName"] = "Medium Directory Group",
            ["members"] = members
        }, replace: false);

        saveCounter.Count.Should().Be(3, "membership persistence should not grow with the number of members");
        (await context.Set<SqlOSFgaUserGroupMembership>().CountAsync()).Should().Be(memberCount);

        var memberAudit = await context.Set<SqlOSAuditEvent>()
            .Where(item => item.Action == "scim.group.member_added" && item.Source == "scim")
            .SingleAsync();
        var memberEvent = await context.Set<SqlOSScimSyncEvent>()
            .Where(item => item.Action == "scim.group.member_added")
            .SingleAsync();
        memberAudit.MetadataJson.Should().NotBeNullOrWhiteSpace();
        var eventData = JsonNode.Parse(memberEvent.DataJson!).Should().BeOfType<JsonObject>().Subject;
        eventData["groupId"]!.GetValue<string>().Should().Be(group["id"]!.GetValue<string>());
        eventData["memberCount"]!.GetValue<int>().Should().Be(memberCount);
        eventData["subjectIds"]!.AsArray().Should().HaveCount(100);
        eventData["truncated"]!.GetValue<bool>().Should().BeTrue();
    }

    [TestMethod]
    public async Task ProtocolMutation_OpportunisticallyCleansOneDeterministicBoundedBatchOfExpiredCommitMarkers()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        var harness = CreateHarness(context);
        var connection = await CreateConnectionAsync(harness.Admin);
        var expiredAt = DateTime.UtcNow.AddDays(-2);
        context.Set<SqlOSScimOperationCommit>().AddRange(Enumerable.Range(0, 300).Select(index =>
            new SqlOSScimOperationCommit
            {
                Id = $"expired_{index:D4}",
                OccurredAt = expiredAt
            }));
        context.Set<SqlOSScimOperationCommit>().Add(new SqlOSScimOperationCommit
        {
            Id = "recent_marker",
            OccurredAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        await harness.Scim.UpsertUserAsync(connection, new JsonObject
        {
            ["externalId"] = "cleanup-user",
            ["userName"] = "cleanup@example.test",
            ["displayName"] = "Cleanup User",
            ["active"] = true
        }, replace: false);

        var remainingExpiredIds = await context.Set<SqlOSScimOperationCommit>()
            .Where(marker => marker.OccurredAt == expiredAt)
            .OrderBy(marker => marker.Id)
            .Select(marker => marker.Id)
            .ToListAsync();
        remainingExpiredIds.Should().Equal(
            Enumerable.Range(256, 44).Select(index => $"expired_{index:D4}"),
            "normal protocol traffic should retire only the oldest 256-row batch");
        (await context.Set<SqlOSScimOperationCommit>().AnyAsync(marker => marker.Id == "recent_marker"))
            .Should().BeTrue();

        await harness.Scim.UpsertUserAsync(connection, new JsonObject
        {
            ["externalId"] = "cleanup-user",
            ["userName"] = "cleanup@example.test",
            ["displayName"] = "Cleanup User Updated",
            ["active"] = true
        }, replace: false);

        (await context.Set<SqlOSScimOperationCommit>().AnyAsync(marker => marker.OccurredAt == expiredAt))
            .Should().BeFalse("later protocol operations should continue draining an old backlog");
        (await context.Set<SqlOSScimOperationCommit>().AnyAsync(marker => marker.Id == "recent_marker"))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task ExternalIdMapping_IsCaseExactForAuthorization()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        await SeedFgaRoleAndResourceAsync(context);
        var harness = CreateHarness(context);
        var connection = await CreateConnectionAsync(harness.Admin);
        await harness.Admin.CreateScimGroupMappingAsync(connection.Id, new SqlOSCreateScimGroupMappingRequest(
            SqlOSScimGroupMappingMatchTypes.ExternalId,
            GroupDisplayName: null,
            GroupExternalId: "Admin",
            GroupPattern: null,
            RoleKey: "store_manager",
            ResourceId: "store_100",
            ResourceIdTemplate: null,
            Enabled: true));

        await harness.Scim.UpsertGroupAsync(connection, new JsonObject
        {
            ["externalId"] = "admin",
            ["displayName"] = "Case-different group",
            ["members"] = new JsonArray()
        }, replace: false);

        (await context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSScimManagedGrant>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task PatternMapping_RenameToMissingResource_RevokesPreviousGrantAndRecordsFailure()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        await SeedFgaRoleAndResourceAsync(context);
        var harness = CreateHarness(context);
        var connection = await CreateConnectionAsync(harness.Admin);
        await harness.Admin.CreateScimGroupMappingAsync(connection.Id, new SqlOSCreateScimGroupMappingRequest(
            SqlOSScimGroupMappingMatchTypes.Pattern,
            GroupDisplayName: null,
            GroupExternalId: null,
            GroupPattern: "^Store (?<store>.+) Managers$",
            RoleKey: "store_manager",
            ResourceId: null,
            ResourceIdTemplate: "store_{store}",
            Enabled: true));

        var group = await harness.Scim.UpsertGroupAsync(connection, new JsonObject
        {
            ["externalId"] = "idp-group-pattern",
            ["displayName"] = "Store 100 Managers",
            ["members"] = new JsonArray()
        }, replace: false);
        (await context.Set<SqlOSFgaGrant>().SingleAsync()).ResourceId.Should().Be("store_100");

        await harness.Scim.UpsertGroupAsync(connection, new JsonObject
        {
            ["externalId"] = "idp-group-pattern",
            ["displayName"] = "Store 200 Managers",
            ["members"] = new JsonArray()
        }, replace: false);

        (await context.Set<SqlOSFgaGrant>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSScimManagedGrant>().SingleAsync()).RevokedAt.Should().NotBeNull();
        (await context.Set<SqlOSScimSyncEvent>().AnyAsync(x =>
            x.ResourceId == group["id"]!.GetValue<string>()
            && x.Action == "scim.grant.resource_missing"
            && x.Result == "failed")).Should().BeTrue();
    }

    [TestMethod]
    public async Task UpdateScimGroupMapping_InvalidRegex_DoesNotRevokeExistingGrantOrMutateMapping()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        await SeedFgaRoleAndResourceAsync(context);
        var harness = CreateHarness(context);
        var connection = await CreateConnectionAsync(harness.Admin);
        var mapping = await harness.Admin.CreateScimGroupMappingAsync(connection.Id, new SqlOSCreateScimGroupMappingRequest(
            SqlOSScimGroupMappingMatchTypes.DisplayName,
            "Store 100 Managers",
            GroupExternalId: null,
            GroupPattern: null,
            RoleKey: "store_manager",
            ResourceId: "store_100",
            ResourceIdTemplate: null,
            Enabled: true));
        var user = await harness.Scim.UpsertUserAsync(connection, new JsonObject
        {
            ["externalId"] = "idp-user-regex",
            ["userName"] = "regex.user@example.test",
            ["displayName"] = "Regex User",
            ["active"] = true
        }, replace: false);
        await harness.Scim.UpsertGroupAsync(connection, new JsonObject
        {
            ["externalId"] = "idp-group-regex",
            ["displayName"] = "Store 100 Managers",
            ["members"] = new JsonArray(new JsonObject { ["value"] = user["id"]!.GetValue<string>() })
        }, replace: false);
        var grantId = (await context.Set<SqlOSFgaGrant>().SingleAsync()).Id;

        var act = async () => await harness.Admin.UpdateScimGroupMappingAsync(mapping.Id, new SqlOSUpdateScimGroupMappingRequest(
            SqlOSScimGroupMappingMatchTypes.Pattern,
            GroupDisplayName: null,
            GroupExternalId: null,
            GroupPattern: "(",
            RoleKey: "store_manager",
            ResourceId: "store_100",
            ResourceIdTemplate: null,
            Description: "invalid replacement",
            Enabled: true));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not a valid regular expression*");
        (await context.Set<SqlOSFgaGrant>().SingleAsync()).Id.Should().Be(grantId);
        (await context.Set<SqlOSScimManagedGrant>().SingleAsync()).RevokedAt.Should().BeNull();
        var persisted = await context.Set<SqlOSScimGroupMapping>().SingleAsync(x => x.Id == mapping.Id);
        persisted.MatchType.Should().Be(SqlOSScimGroupMappingMatchTypes.DisplayName);
        persisted.GroupDisplayName.Should().Be("Store 100 Managers");
        persisted.GroupPattern.Should().BeNull();
        persisted.IsEnabled.Should().BeTrue();
    }

    [TestMethod]
    public async Task DeprovisionUserAsync_DisablesMembershipRevokesSessionAndRemovesScimGroupMembership()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        var harness = CreateHarness(context);
        var connection = await CreateConnectionAsync(harness.Admin);
        var user = await harness.Scim.UpsertUserAsync(connection, new JsonObject
        {
            ["externalId"] = "idp-user-1",
            ["userName"] = "ada@example.test",
            ["displayName"] = "Ada Lovelace",
            ["active"] = true
        }, replace: false);
        var group = await harness.Scim.UpsertGroupAsync(connection, new JsonObject
        {
            ["externalId"] = "idp-group-1",
            ["displayName"] = "Store 100 Managers",
            ["members"] = new JsonArray(new JsonObject { ["value"] = "idp-user-1" })
        }, replace: false);
        var userId = user["id"]!.GetValue<string>();
        context.Set<SqlOSSession>().Add(new SqlOSSession
        {
            Id = "sess_scim_user",
            UserId = userId,
            OrganizationId = "org_acme",
            AuthenticationMethod = "password",
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            IdleExpiresAt = DateTime.UtcNow.AddHours(1),
            AbsoluteExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        await context.SaveChangesAsync();

        await harness.Scim.UpsertUserAsync(connection, new JsonObject
        {
            ["externalId"] = "idp-user-1",
            ["userName"] = "ada@example.test",
            ["displayName"] = "Ada Lovelace",
            ["active"] = false
        }, replace: false);

        var membership = await context.Set<SqlOSMembership>().SingleAsync(x => x.UserId == userId && x.OrganizationId == "org_acme");
        var session = await context.Set<SqlOSSession>().SingleAsync();
        var groupId = group["id"]!.GetValue<string>();
        membership.IsActive.Should().BeFalse();
        session.RevokedAt.Should().NotBeNull();
        session.RevocationReason.Should().Be("scim_deprovisioned");
        (await context.Set<SqlOSFgaUserGroupMembership>().AnyAsync(x => x.UserGroupId == groupId)).Should().BeFalse();
        (await context.Set<SqlOSAuditEvent>().AnyAsync(x => x.Action == "scim.user.deactivated" && x.Source == "scim")).Should().BeTrue();
    }

    [TestMethod]
    public async Task AuthenticateAsync_ScimIsDisabledByDefault()
    {
        using var context = CreateContext();
        var harness = CreateHarness(context);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer any-token";

        var act = async () => await harness.Scim.AuthenticateAsync(httpContext);

        var error = await act.Should().ThrowAsync<SqlOSScimException>();
        error.Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [TestMethod]
    public async Task UserReadAndDelete_DoNotCrossOrganizationBoundary()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        context.Set<SqlOSOrganization>().Add(new SqlOSOrganization
        {
            Id = "org_other",
            Slug = "other",
            Name = "Other",
            CreatedAt = DateTime.UtcNow
        });
        context.Set<SqlOSUser>().Add(new SqlOSUser
        {
            Id = "usr_other",
            DisplayName = "Other User",
            DefaultEmail = "other@example.test",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        context.Set<SqlOSMembership>().Add(new SqlOSMembership
        {
            OrganizationId = "org_other",
            UserId = "usr_other",
            Role = "member",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var harness = CreateHarness(context);
        var connection = await CreateConnectionAsync(harness.Admin);

        var act = async () => await harness.Scim.GetUserAsync(connection, "usr_other");

        var error = await act.Should().ThrowAsync<SqlOSScimException>();
        error.Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);

        var delete = async () => await harness.Scim.DeleteUserAsync(connection, "usr_other");
        var deleteError = await delete.Should().ThrowAsync<SqlOSScimException>();
        deleteError.Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        (await context.Set<SqlOSUser>().SingleAsync(x => x.Id == "usr_other")).IsActive.Should().BeTrue();
    }

    [TestMethod]
    public async Task ListUsersAsync_RejectsUnsupportedFilterInsteadOfReturningTenantDirectory()
    {
        using var context = CreateContext();
        await SeedOrganizationAsync(context);
        var harness = CreateHarness(context);
        var connection = await CreateConnectionAsync(harness.Admin);

        var act = async () => await harness.Scim.ListUsersAsync(connection, 1, 100, "displayName co \"Admin\"");

        var error = await act.Should().ThrowAsync<SqlOSScimException>();
        error.Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        error.Which.ScimType.Should().Be("invalidFilter");
    }

    private static Harness CreateHarness(TestSqlOSInMemoryDbContext context, SqlOSAuthServerOptions? optionsValue = null)
    {
        var options = Options.Create(optionsValue ?? new SqlOSAuthServerOptions());
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var scim = new SqlOSScimService(context, options, crypto);
        return new Harness(admin, scim);
    }

    private static async Task<SqlOSScimConnection> CreateConnectionAsync(SqlOSAdminService admin)
    {
        var connection = await admin.CreateScimConnectionDraftAsync(new SqlOSCreateScimConnectionRequest("org_acme", "Acme SCIM", false));
        await admin.RotateScimTokenAsync(connection.Id);
        return await admin.SetScimConnectionEnabledAsync(connection.Id, true);
    }

    private static async Task SeedOrganizationAsync(TestSqlOSInMemoryDbContext context)
    {
        context.Set<SqlOSOrganization>().Add(new SqlOSOrganization
        {
            Id = "org_acme",
            Slug = "acme",
            Name = "Acme",
            PrimaryDomain = "acme.example",
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private static async Task SeedFgaRoleAndResourceAsync(TestSqlOSInMemoryDbContext context)
    {
        context.Set<SqlOSFgaResourceType>().Add(new SqlOSFgaResourceType
        {
            Id = "store",
            Name = "Store"
        });
        context.Set<SqlOSFgaRole>().Add(new SqlOSFgaRole
        {
            Id = "role_store_manager",
            Key = "store_manager",
            Name = "Store Manager"
        });
        context.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
        {
            Id = "store_100",
            ResourceTypeId = "store",
            Name = "Store 100",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private static TestSqlOSInMemoryDbContext CreateContext(CountingSaveChangesInterceptor? saveCounter = null)
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"));
        if (saveCounter != null)
        {
            options.AddInterceptors(saveCounter);
        }
        return new TestSqlOSInMemoryDbContext(options.Options);
    }

    private sealed class CountingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public int Count { get; private set; }

        public void Reset() => Count = 0;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed record Harness(SqlOSAdminService Admin, SqlOSScimService Scim);
}

using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
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
            seed.Token = "scim_seed_token";
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
        connection.TokenHash.Should().NotBe("scim_seed_token");
        mapping.Source.Should().Be(SqlOSScimSources.Seeded);
        mapping.SourceKey.Should().Be("name:Store 100 Managers");
        mapping.RoleKey.Should().Be("store_manager");
        mapping.ResourceId.Should().Be("store_100");

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer scim_seed_token";
        var authenticated = await harness.Scim.AuthenticateAsync(httpContext);
        authenticated.Id.Should().Be(connection.Id);
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
    public async Task GetUserAsync_DoesNotCrossOrganizationBoundary()
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
            Id = "mem_other",
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
        var connection = await admin.CreateScimConnectionAsync(new SqlOSCreateScimConnectionRequest("org_acme", "Acme SCIM", true));
        return connection;
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

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }

    private sealed record Harness(SqlOSAdminService Admin, SqlOSScimService Scim);
}

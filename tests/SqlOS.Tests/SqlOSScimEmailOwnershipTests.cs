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
using SqlOS.Domain;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

/// <summary>
/// #420: SCIM owns a person's global email and lifecycle only while it created them and they
/// have no independent anchor, and may only create or re-point verified emails inside the
/// organization's verified domains.
/// </summary>
[TestClass]
public sealed class SqlOSScimEmailOwnershipTests
{
    private const string OrganizationId = "org_acme";
    private const string VerifiedDomain = "acme.test";

    [DataTestMethod]
    [DataRow("organization_membership")]
    [DataRow("password")]
    [DataRow("mfa_factor")]
    [DataRow("phone_number")]
    [DataRow("oidc_identity")]
    [DataRow("saml_identity")]
    public async Task OwnedUser_WithIndependentAnchor_ReleasesOwnershipOnTheNextWrite(string anchor)
    {
        await using var context = CreateContext();
        var harness = await CreateHarnessAsync(context);
        var userId = await CreateOwnedUserAsync(harness, "ada");
        await AddAnchorAsync(context, userId, anchor);

        await harness.Scim.PatchUserAsync(harness.Connection, userId, Patch(new JsonObject
        {
            ["displayName"] = "Directory Rename",
            ["active"] = false,
            ["emails"] = new JsonArray(new JsonObject { ["value"] = "attacker@evil.test", ["primary"] = true })
        }));

        context.ChangeTracker.Clear();
        var user = await context.Set<SqlOSUser>().SingleAsync(x => x.Id == userId);
        user.DisplayName.Should().Be("ada");
        user.DefaultEmail.Should().Be("ada@acme.test");
        user.IsActive.Should().BeTrue();
        (await context.Set<SqlOSUserEmail>().Where(x => x.UserId == userId).Select(x => x.Email).ToListAsync())
            .Should().Equal("ada@acme.test");
        (await context.Set<SqlOSScimExternalId>().SingleAsync(x => x.EntityId == userId && x.ResourceType == "User"))
            .OwnsUserLifecycle.Should().BeFalse();
        (await context.Set<SqlOSMembership>().SingleAsync(x => x.UserId == userId && x.OrganizationId == OrganizationId))
            .IsActive.Should().BeFalse("the directory still controls its own organization's membership");
        var released = await context.Set<SqlOSAuditEvent>().SingleAsync(x => x.Action == "scim.user.lifecycle_released");
        released.MetadataJson.Should().Contain(anchor);
        (await context.Set<SqlOSScimSyncEvent>().AnyAsync(x => x.Action == "scim.user.lifecycle_released" && x.Result == "success"))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task OwnedUser_WithSamlIdentityFromTheSameOrganization_StaysOwned()
    {
        await using var context = CreateContext();
        var harness = await CreateHarnessAsync(context);
        var userId = await CreateOwnedUserAsync(harness, "grace");
        context.Set<SqlOSSsoConnection>().Add(new SqlOSSsoConnection { Id = "sso_same_org", OrganizationId = OrganizationId, DisplayName = "Same org", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        context.Set<SqlOSExternalIdentity>().Add(TestRows.Create<SqlOSExternalIdentity>(new { Id = "ext_same_org", UserId = userId, SsoConnectionId = "sso_same_org", Issuer = "urn:idp", Subject = "grace", CreatedAt = DateTime.UtcNow }));
        await context.SaveChangesAsync();

        await harness.Scim.PatchUserAsync(harness.Connection, userId, Patch(new JsonObject { ["displayName"] = "Grace Hopper" }));

        context.ChangeTracker.Clear();
        (await context.Set<SqlOSUser>().SingleAsync(x => x.Id == userId)).DisplayName.Should().Be("Grace Hopper");
        (await context.Set<SqlOSScimExternalId>().SingleAsync(x => x.EntityId == userId && x.ResourceType == "User"))
            .OwnsUserLifecycle.Should().BeTrue();
    }

    [TestMethod]
    public async Task CreateOutsideVerifiedDomains_IsRejectedAndRecordedAsFailedSyncEvent()
    {
        await using var context = CreateContext();
        var harness = await CreateHarnessAsync(context);

        var create = async () => await harness.Scim.CreateUserAsync(harness.Connection, User("victim-1", "victim@gmail.test"));

        var rejection = await create.Should().ThrowAsync<SqlOSScimRejectedWriteException>();
        rejection.Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        rejection.Which.ScimType.Should().Be("invalidValue");
        context.ChangeTracker.Clear();
        (await context.Set<SqlOSUser>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSUserEmail>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSMembership>().CountAsync()).Should().Be(0);
        (await context.Set<SqlOSScimExternalId>().CountAsync()).Should().Be(0);
        var failed = await context.Set<SqlOSScimSyncEvent>().SingleAsync();
        failed.Action.Should().Be("scim.user.rejected");
        failed.Result.Should().Be("failed");
        failed.DataJson.Should().Contain("untrusted_email_domain").And.Contain("create");
    }

    [TestMethod]
    public async Task PrimaryDomainAlone_NeverAuthorizesAnEmailWrite()
    {
        await using var context = CreateContext();
        var harness = await CreateHarnessAsync(context);

        var create = async () => await harness.Scim.CreateUserAsync(harness.Connection, User("primary-1", "someone@primary.test"));

        await create.Should().ThrowAsync<SqlOSScimRejectedWriteException>();
    }

    [TestMethod]
    public async Task OwnedUser_PatchToAnotherVerifiedAddress_MovesThePrimaryEmail_ButOutsideDomainIsRejected()
    {
        await using var context = CreateContext();
        var harness = await CreateHarnessAsync(context);
        var userId = await CreateOwnedUserAsync(harness, "linus");

        await harness.Scim.PatchUserAsync(harness.Connection, userId, Patch(new JsonObject
        {
            ["emails"] = new JsonArray(new JsonObject { ["value"] = "torvalds@acme.test", ["primary"] = true })
        }));
        var outside = async () => await harness.Scim.PatchUserAsync(harness.Connection, userId, Patch(new JsonObject
        {
            ["emails"] = new JsonArray(new JsonObject { ["value"] = "linus@outside.test", ["primary"] = true })
        }));

        await outside.Should().ThrowAsync<SqlOSScimRejectedWriteException>();
        context.ChangeTracker.Clear();
        (await context.Set<SqlOSUser>().SingleAsync(x => x.Id == userId)).DefaultEmail.Should().Be("torvalds@acme.test");
        (await context.Set<SqlOSUserEmail>().SingleAsync(x => x.UserId == userId && x.IsPrimary)).Email.Should().Be("torvalds@acme.test");
        (await context.Set<SqlOSUserEmail>().AnyAsync(x => x.Email == "linus@outside.test")).Should().BeFalse();
        (await context.Set<SqlOSScimSyncEvent>().SingleAsync(x => x.Result == "failed")).DataJson.Should().Contain("patch");
    }

    [TestMethod]
    public async Task OwnedUser_InvalidEmail_IsRejectedBeforeAnyWrite()
    {
        await using var context = CreateContext();
        var harness = await CreateHarnessAsync(context);

        var create = async () => await harness.Scim.CreateUserAsync(harness.Connection, User("invalid-1", "not-an-email"));

        var rejection = await create.Should().ThrowAsync<SqlOSScimException>();
        rejection.Which.ScimType.Should().Be("invalidValue");
        context.ChangeTracker.Clear();
        (await context.Set<SqlOSUser>().CountAsync()).Should().Be(0);
    }

    [TestMethod]
    public async Task ExistingUnverifiedAccount_IsNeverLinked()
    {
        await using var context = CreateContext();
        var harness = await CreateHarnessAsync(context);
        var squatter = await harness.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Squatter", "employee@acme.test", "P@ssword123!"));

        var create = async () => await harness.Scim.CreateUserAsync(harness.Connection, User("employee-1", "employee@acme.test"));

        var rejection = await create.Should().ThrowAsync<SqlOSScimRejectedWriteException>();
        rejection.Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        context.ChangeTracker.Clear();
        (await context.Set<SqlOSMembership>().AnyAsync(x => x.UserId == squatter.Id)).Should().BeFalse();
        (await context.Set<SqlOSScimSyncEvent>().SingleAsync()).DataJson.Should().Contain("unverified_email_match");
    }

    [TestMethod]
    public async Task NonOwningLink_NeverChangesGlobalActivity()
    {
        await using var context = CreateContext();
        var harness = await CreateHarnessAsync(context);
        var existing = await TestAccounts.RegisterAsync(
            context,
            new SqlOSCreateUserRequest("Contractor", "contractor@freelance.test", null),
            new OwnershipProof(EmailAddress.Parse("contractor@freelance.test"), OwnershipProofMethod.EmailOtp));
        await harness.Admin.DeactivateUserAsync(existing.Id);

        var created = await harness.Scim.CreateUserAsync(harness.Connection, User("contractor-1", "contractor@freelance.test", active: true));
        await harness.Scim.PatchUserAsync(harness.Connection, created["id"]!.GetValue<string>(), Patch(new JsonObject { ["displayName"] = "Renamed" }));

        context.ChangeTracker.Clear();
        var user = await context.Set<SqlOSUser>().SingleAsync(x => x.Id == existing.Id);
        user.IsActive.Should().BeFalse("only the owning link may change global activity");
        user.DisplayName.Should().Be("Contractor");
        (await context.Set<SqlOSMembership>().SingleAsync(x => x.UserId == existing.Id)).IsActive.Should().BeTrue();
    }

    [TestMethod]
    public async Task Delete_ReleasesAnchoredUsers_AndOffboardsUnanchoredOwnedUsers()
    {
        await using var context = CreateContext();
        var harness = await CreateHarnessAsync(context);
        var anchoredId = await CreateOwnedUserAsync(harness, "anchored");
        var soloId = await CreateOwnedUserAsync(harness, "solo");
        await AddAnchorAsync(context, anchoredId, "password");

        await harness.Scim.DeleteUserAsync(harness.Connection, anchoredId);
        await harness.Scim.DeleteUserAsync(harness.Connection, soloId);

        context.ChangeTracker.Clear();
        (await context.Set<SqlOSUser>().SingleAsync(x => x.Id == anchoredId)).IsActive.Should().BeTrue();
        (await context.Set<SqlOSUser>().SingleAsync(x => x.Id == soloId)).IsActive.Should().BeFalse();
        (await context.Set<SqlOSScimExternalId>().SingleAsync(x => x.EntityId == anchoredId && x.ResourceType == "User"))
            .OwnsUserLifecycle.Should().BeFalse();
        (await context.Set<SqlOSAuditEvent>().CountAsync(x => x.Action == "scim.user.lifecycle_released")).Should().Be(1);
    }

    private static async Task<string> CreateOwnedUserAsync(Harness harness, string name)
    {
        var created = await harness.Scim.CreateUserAsync(harness.Connection, User($"{name}-ext", $"{name}@acme.test", displayName: name));
        return created["id"]!.GetValue<string>();
    }

    private static async Task AddAnchorAsync(TestSqlOSInMemoryDbContext context, string userId, string anchor)
    {
        var now = DateTime.UtcNow;
        switch (anchor)
        {
            case "organization_membership":
                context.Set<SqlOSOrganization>().Add(new SqlOSOrganization { Id = "org_other", Slug = "other", Name = "Other", CreatedAt = now });
                context.Set<SqlOSMembership>().Add(new SqlOSMembership { OrganizationId = "org_other", UserId = userId, IsActive = true, CreatedAt = now });
                break;
            case "password":
                context.Set<SqlOSCredential>().Add(TestRows.Create<SqlOSCredential>(new { Id = $"cred_{userId}", UserId = userId, SecretHash = "hash", CreatedAt = now }));
                break;
            case "mfa_factor":
                context.Set<SqlOSUserAuthenticator>().Add(TestRows.Create<SqlOSUserAuthenticator>(new { Id = $"auth_{userId}", UserId = userId, SecretProtected = "secret", IsConfirmed = true, CreatedAt = now }));
                break;
            case "phone_number":
                context.Set<SqlOSUserPhoneNumber>().Add(TestRows.Create<SqlOSUserPhoneNumber>(new { Id = $"phn_{userId}", UserId = userId, PhoneNumber = "+15555550100", PhoneNumberHash = "hash", IsVerified = true, CreatedAt = now, UpdatedAt = now }));
                break;
            case "oidc_identity":
                context.Set<SqlOSExternalIdentity>().Add(TestRows.Create<SqlOSExternalIdentity>(new { Id = $"ext_{userId}", UserId = userId, OidcConnectionId = "oidc_google", Issuer = "https://accounts.google.com", Subject = userId, CreatedAt = now }));
                break;
            case "saml_identity":
                context.Set<SqlOSOrganization>().Add(new SqlOSOrganization { Id = "org_saml_other", Slug = "saml-other", Name = "SAML Other", CreatedAt = now });
                context.Set<SqlOSSsoConnection>().Add(new SqlOSSsoConnection { Id = "sso_other_org", OrganizationId = "org_saml_other", DisplayName = "Other", CreatedAt = now, UpdatedAt = now });
                context.Set<SqlOSExternalIdentity>().Add(TestRows.Create<SqlOSExternalIdentity>(new { Id = $"ext_{userId}", UserId = userId, SsoConnectionId = "sso_other_org", Issuer = "urn:other", Subject = userId, CreatedAt = now }));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(anchor), anchor, null);
        }

        await context.SaveChangesAsync();
    }

    private static JsonObject User(string externalId, string email, bool active = true, string? displayName = null)
        => new()
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:User"),
            ["externalId"] = externalId,
            ["userName"] = email,
            ["displayName"] = displayName ?? email,
            ["active"] = active,
            ["emails"] = new JsonArray(new JsonObject { ["value"] = email, ["type"] = "work", ["primary"] = true })
        };

    private static JsonObject Patch(JsonObject value)
        => new()
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:api:messages:2.0:PatchOp"),
            ["Operations"] = new JsonArray(new JsonObject { ["op"] = "replace", ["value"] = value })
        };

    private static async Task<Harness> CreateHarnessAsync(TestSqlOSInMemoryDbContext context)
    {
        context.Set<SqlOSOrganization>().Add(new SqlOSOrganization
        {
            Id = OrganizationId,
            Slug = "acme",
            Name = "Acme",
            PrimaryDomain = "primary.test",
            CreatedAt = DateTime.UtcNow
        });
        context.Set<SqlOSOrganizationDomain>().Add(new SqlOSOrganizationDomain
        {
            Id = "dom_acme",
            OrganizationId = OrganizationId,
            Domain = VerifiedDomain,
            Status = SqlOSOrganizationDomainStatuses.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            VerifiedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var options = Options.Create(new SqlOSAuthServerOptions());
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var scim = new SqlOSScimService(context, options, crypto);
        var draft = await admin.CreateScimConnectionDraftAsync(new SqlOSCreateScimConnectionRequest(OrganizationId, "Acme SCIM", false));
        await admin.RotateScimTokenAsync(draft.Id);
        var connection = await admin.SetScimConnectionEnabledAsync(draft.Id, true);
        return new Harness(admin, scim, connection);
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
        => new(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed record Harness(SqlOSAdminService Admin, SqlOSScimService Scim, SqlOSScimConnection Connection);
}

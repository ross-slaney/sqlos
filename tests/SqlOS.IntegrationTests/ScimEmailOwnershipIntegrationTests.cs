using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Fga.Models;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// #420: a tenant's SCIM directory may only create or re-point verified global emails inside its
/// organization's verified domains, loses lifecycle ownership once the person has an independent
/// anchor, and never changes global state through a link it does not own.
/// </summary>
[TestClass]
public sealed class ScimEmailOwnershipIntegrationTests
{
    private static EmailOwnershipServer _server = null!;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext _)
    {
        _server = await EmailOwnershipServer.CreateAsync("ScimEmailOwner");
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (_server != null)
        {
            await _server.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ScimCreate_WithEmailOutsideVerifiedDomains_IsRejectedWithoutSideEffects()
    {
        var tenant = await CreateTenantAsync();
        var victimEmail = $"victim{Unique()}@gmail.example";

        using var response = await _server.SendScimAsync(
            tenant.Token,
            HttpMethod.Post,
            "/Users",
            EmailOwnershipServer.ScimUser($"ext-{Unique()}", victimEmail, victimEmail, "Pre-hijack"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        error["scimType"]!.GetValue<string>().Should().Be("invalidValue");
        await using var verification = _server.CreateVerificationContext();
        (await verification.Set<SqlOSUserEmail>().AnyAsync(x => x.NormalizedEmail == SqlOSAdminService.NormalizeEmail(victimEmail)))
            .Should().BeFalse();
        (await verification.Set<SqlOSMembership>().AnyAsync(x => x.OrganizationId == tenant.OrganizationId)).Should().BeFalse();
        (await verification.Set<SqlOSScimExternalId>().AnyAsync(x => x.ConnectionId == tenant.ConnectionId)).Should().BeFalse();
        (await verification.Set<SqlOSFgaSubject>().AnyAsync(x => x.OrganizationId == tenant.OrganizationId)).Should().BeFalse();
        (await verification.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "scim.user.created" && x.OrganizationId == tenant.OrganizationId))
            .Should().BeFalse();
        var syncEvent = await verification.Set<SqlOSScimSyncEvent>().SingleAsync(x => x.ConnectionId == tenant.ConnectionId);
        syncEvent.Result.Should().Be("failed");
        syncEvent.DataJson.Should().Contain("untrusted_email_domain");
    }

    [TestMethod]
    public async Task ScimCreate_WithOnlyUnverifiedPrimaryDomain_IsRejected()
    {
        var unique = Unique();
        var domain = $"primary{unique}.example";
        var organization = await _server.CreateOrganizationAsync($"Primary {unique}", domain);
        var (_, token) = await _server.CreateScimConnectionAsync(organization.Id);

        using var response = await _server.SendScimAsync(
            token,
            HttpMethod.Post,
            "/Users",
            EmailOwnershipServer.ScimUser($"ext-{unique}", $"user@{domain}", $"user@{domain}", "Primary Only"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "an unverified PrimaryDomain alone never authorizes a directory email write");
    }

    [TestMethod]
    public async Task ScimOwnedUser_WithIndependentAnchors_CannotRepointEmailOrDeactivateGlobally()
    {
        var tenant = await CreateTenantAsync();
        var otherOrganization = await _server.CreateOrganizationAsync($"Other {Unique()}");
        var memberEmail = $"member{Unique()}@{tenant.Domain}";
        var attackerEmail = $"attacker{Unique()}@evil.example";
        var userId = await CreateScimUserAsync(tenant, memberEmail);
        (await _server.CompleteGoogleLoginAsync(memberEmail)).UserId.Should().Be(userId);
        await using (var scope = _server.CreateScope())
        {
            await scope.Admin.CreateMembershipAsync(otherOrganization.Id, new SqlOSCreateMembershipRequest(userId, "member"));
        }

        using var repoint = await _server.SendScimAsync(
            tenant.Token,
            HttpMethod.Patch,
            $"/Users/{userId}",
            EmailOwnershipServer.ScimPatch(new JsonObject
            {
                ["emails"] = new JsonArray(new JsonObject { ["value"] = attackerEmail, ["type"] = "work", ["primary"] = true })
            }));

        await using (var verification = _server.CreateVerificationContext())
        {
            (await verification.Set<SqlOSUserEmail>().AnyAsync(x => x.NormalizedEmail == SqlOSAdminService.NormalizeEmail(attackerEmail)))
                .Should().BeFalse("a directory must not re-point the login email of a person who has an independent login");
            (await verification.Set<SqlOSUserEmail>().SingleAsync(x => x.UserId == userId)).Email.Should().Be(memberEmail);
            (await verification.Set<SqlOSUser>().SingleAsync(x => x.Id == userId)).DefaultEmail.Should().Be(memberEmail);
            (await verification.Set<SqlOSScimExternalId>().SingleAsync(x => x.ConnectionId == tenant.ConnectionId && x.EntityId == userId))
                .OwnsUserLifecycle.Should().BeFalse();
        }

        await using (var scope = _server.CreateScope())
        {
            try
            {
                await scope.Auth.RequestMagicLinkAsync(
                    new SqlOSMagicLinkStartRequest(attackerEmail, EmailOwnershipServer.ClientId, null),
                    EmailOwnershipServer.HttpContext());
            }
            catch (InvalidOperationException)
            {
            }
        }

        _server.MessagesTo(attackerEmail).Should().BeEmpty("the attacker's address never became a login email for the victim");

        using var deactivate = await _server.SendScimAsync(
            tenant.Token,
            HttpMethod.Patch,
            $"/Users/{userId}",
            EmailOwnershipServer.ScimPatch(new JsonObject { ["active"] = false }));
        deactivate.StatusCode.Should().Be(HttpStatusCode.OK, await deactivate.Content.ReadAsStringAsync());
        await using var final = _server.CreateVerificationContext();
        (await final.Set<SqlOSUser>().SingleAsync(x => x.Id == userId)).IsActive.Should().BeTrue();
        (await final.Set<SqlOSMembership>().SingleAsync(x => x.UserId == userId && x.OrganizationId == tenant.OrganizationId))
            .IsActive.Should().BeFalse("the directory still controls its own organization's membership");
        (await final.Set<SqlOSAuditEvent>().AnyAsync(x => x.EventType == "scim.user.lifecycle_released" && x.OrganizationId == tenant.OrganizationId))
            .Should().BeTrue();
    }

    [TestMethod]
    public async Task ScimOwnedUser_WithOnlyAnUpstreamIdentity_IsNotGloballyDeactivated()
    {
        var tenant = await CreateTenantAsync();
        var memberEmail = $"solo{Unique()}@{tenant.Domain}";
        var userId = await CreateScimUserAsync(tenant, memberEmail);
        (await _server.CompleteGoogleLoginAsync(memberEmail)).UserId.Should().Be(userId);

        using var deactivate = await _server.SendScimAsync(
            tenant.Token,
            HttpMethod.Patch,
            $"/Users/{userId}",
            EmailOwnershipServer.ScimPatch(new JsonObject { ["active"] = false }));

        deactivate.StatusCode.Should().Be(HttpStatusCode.OK, await deactivate.Content.ReadAsStringAsync());
        await using var verification = _server.CreateVerificationContext();
        (await verification.Set<SqlOSUser>().SingleAsync(x => x.Id == userId)).IsActive
            .Should().BeTrue("a person with their own social login is not offboarded globally by one tenant's directory");
    }

    [TestMethod]
    public async Task NonOwningScimLink_CannotReactivateUserDeprovisionedByOwningDirectory()
    {
        var owner = await CreateTenantAsync();
        var other = await CreateTenantAsync();
        var email = $"offboarded{Unique()}@{owner.Domain}";
        var userId = await CreateScimUserAsync(owner, email);
        using (var deprovision = await _server.SendScimAsync(
                   owner.Token,
                   HttpMethod.Patch,
                   $"/Users/{userId}",
                   EmailOwnershipServer.ScimPatch(new JsonObject { ["active"] = false })))
        {
            deprovision.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await using (var verification = _server.CreateVerificationContext())
        {
            (await verification.Set<SqlOSUser>().SingleAsync(x => x.Id == userId)).IsActive.Should().BeFalse();
        }

        using var link = await _server.SendScimAsync(
            other.Token,
            HttpMethod.Post,
            "/Users",
            EmailOwnershipServer.ScimUser($"ext-{Unique()}", email, email, "Other Tenant View", active: true));

        link.StatusCode.Should().Be(HttpStatusCode.Created, await link.Content.ReadAsStringAsync());
        await using var final = _server.CreateVerificationContext();
        (await final.Set<SqlOSUser>().SingleAsync(x => x.Id == userId)).IsActive
            .Should().BeFalse("only the owning directory's link may change global activity");
        (await final.Set<SqlOSScimExternalId>().SingleAsync(x => x.ConnectionId == other.ConnectionId && x.EntityId == userId))
            .OwnsUserLifecycle.Should().BeFalse();
    }

    [TestMethod]
    public async Task ScimCreateAndPatch_InsideVerifiedDomain_StayDirectoryOwned()
    {
        var tenant = await CreateTenantAsync();
        var email = $"employee{Unique()}@{tenant.Domain}";
        var movedEmail = $"renamed{Unique()}@{tenant.Domain}";
        var userId = await CreateScimUserAsync(tenant, email);

        using var patch = await _server.SendScimAsync(
            tenant.Token,
            HttpMethod.Patch,
            $"/Users/{userId}",
            EmailOwnershipServer.ScimPatch(new JsonObject
            {
                ["displayName"] = "Renamed Employee",
                ["emails"] = new JsonArray(new JsonObject { ["value"] = movedEmail, ["type"] = "work", ["primary"] = true })
            }));

        patch.StatusCode.Should().Be(HttpStatusCode.OK, await patch.Content.ReadAsStringAsync());
        await using var verification = _server.CreateVerificationContext();
        var user = await verification.Set<SqlOSUser>().SingleAsync(x => x.Id == userId);
        user.DisplayName.Should().Be("Renamed Employee");
        user.DefaultEmail.Should().Be(movedEmail);
        var primary = await verification.Set<SqlOSUserEmail>().SingleAsync(x => x.UserId == userId && x.IsPrimary);
        primary.Email.Should().Be(movedEmail);
        primary.IsVerified.Should().BeTrue();
        (await verification.Set<SqlOSScimExternalId>().SingleAsync(x => x.ConnectionId == tenant.ConnectionId && x.EntityId == userId))
            .OwnsUserLifecycle.Should().BeTrue();
    }

    [TestMethod]
    public async Task ScimCreate_MatchingVerifiedContractor_LinksWithoutTouchingGlobalState()
    {
        var tenant = await CreateTenantAsync();
        var contractorEmail = $"contractor{Unique()}@freelance.example";
        var contractor = await _server.CreateUserAsync(contractorEmail, displayName: "Independent Contractor");

        using var response = await _server.SendScimAsync(
            tenant.Token,
            HttpMethod.Post,
            "/Users",
            EmailOwnershipServer.ScimUser($"ext-{Unique()}", contractorEmail, contractorEmail, "Directory Overwrite", active: false));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<string>().Should().Be(contractor.Id);
        await using (var verification = _server.CreateVerificationContext())
        {
            var user = await verification.Set<SqlOSUser>().SingleAsync(x => x.Id == contractor.Id);
            user.DisplayName.Should().Be("Independent Contractor");
            user.DefaultEmail.Should().Be(contractorEmail);
            user.IsActive.Should().BeTrue();
            (await verification.Set<SqlOSUserEmail>().CountAsync(x => x.UserId == contractor.Id)).Should().Be(1);
            (await verification.Set<SqlOSMembership>().SingleAsync(x => x.UserId == contractor.Id && x.OrganizationId == tenant.OrganizationId))
                .IsActive.Should().BeFalse();
            (await verification.Set<SqlOSScimExternalId>().SingleAsync(x => x.ConnectionId == tenant.ConnectionId && x.EntityId == contractor.Id))
                .OwnsUserLifecycle.Should().BeFalse();
        }

        await using var scope = _server.CreateScope();
        var login = await scope.Auth.LoginWithPasswordAsync(
            new SqlOSPasswordLoginRequest(contractorEmail, EmailOwnershipServer.Password, EmailOwnershipServer.ClientId, null),
            EmailOwnershipServer.HttpContext());
        (await _server.SessionUserIdAsync(login.Tokens!)).Should().Be(contractor.Id);
    }

    [TestMethod]
    public async Task ScimCreate_MatchingUnverifiedAccount_DoesNotLinkIt()
    {
        var tenant = await CreateTenantAsync();
        var email = $"squatted{Unique()}@{tenant.Domain}";
        var squatter = await _server.CreateUserAsync(email, verified: false, displayName: "Squatter");

        using var response = await _server.SendScimAsync(
            tenant.Token,
            HttpMethod.Post,
            "/Users",
            EmailOwnershipServer.ScimUser($"ext-{Unique()}", email, email, "Real Employee"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        await using var verification = _server.CreateVerificationContext();
        (await verification.Set<SqlOSMembership>().AnyAsync(x => x.UserId == squatter.Id && x.OrganizationId == tenant.OrganizationId))
            .Should().BeFalse("directory access must never be granted to an account whose email nobody has proven");
        (await verification.Set<SqlOSScimExternalId>().AnyAsync(x => x.EntityId == squatter.Id)).Should().BeFalse();
    }

    private static async Task<Tenant> CreateTenantAsync()
    {
        var unique = Unique();
        var domain = $"tenant{unique}.example";
        var organization = await _server.CreateOrganizationAsync($"Tenant {unique}", null, domain);
        var (connectionId, token) = await _server.CreateScimConnectionAsync(organization.Id);
        return new Tenant(organization.Id, connectionId, token, domain);
    }

    private static async Task<string> CreateScimUserAsync(Tenant tenant, string email)
    {
        using var response = await _server.SendScimAsync(
            tenant.Token,
            HttpMethod.Post,
            "/Users",
            EmailOwnershipServer.ScimUser($"ext-{Unique()}", email, email, "Directory Person"));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
    }

    private static string Unique() => Guid.NewGuid().ToString("N")[..12];

    private sealed record Tenant(string OrganizationId, string ConnectionId, string Token, string Domain);
}

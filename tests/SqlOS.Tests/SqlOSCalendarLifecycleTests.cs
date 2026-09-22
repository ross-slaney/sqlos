using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Calendar.Contracts;
using SqlOS.Calendar.Interfaces;
using SqlOS.Calendar.Models;
using SqlOS.Calendar.Services;
using SqlOS.Configuration;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

/// <summary>
/// Offboarding must revoke calendar credentials through the same central lifecycle policy
/// that revokes sessions and tokens (issue #241). These tests cover organization and user
/// deactivation, the scheduler recheck, races with refresh and sync, provider-independent
/// fail-closed behavior, reactivation, multi-connection scope, and idempotent retry.
/// </summary>
[TestClass]
public sealed class SqlOSCalendarLifecycleTests
{
    private const string ReturnUri = "https://app.example.local/settings/calendar";
    private const string Disconnected = "calendar.connection.disconnected";

    [TestMethod]
    public async Task OrganizationDeactivation_RevokesEveryOrganizationConnection_AndOnlyThose()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var organization = await services.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Acme", "acme"));
        var otherOrganization = await services.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Globex", "globex"));
        var user = await services.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Cal User", "cal@example.com", null));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);

        var first = await ConnectAsync(services, oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, organizationId: organization.Id);
        var second = await ConnectAsync(services, oidc.Id, SqlOSCalendarIntegrationMode.ConnectionOnly, organizationId: organization.Id);
        var otherOrg = await ConnectAsync(services, oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, organizationId: otherOrganization.Id);
        var personal = await ConnectAsync(services, oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, userId: user.Id);

        await services.Admin.UpdateOrganizationAsync(
            organization.Id,
            new SqlOSUpdateOrganizationRequest("Acme", "acme", IsActive: false));

        foreach (var id in new[] { first, second })
        {
            var revoked = context.Set<SqlOSCalendarConnection>().Single(x => x.Id == id);
            revoked.Status.Should().Be(SqlOSCalendarConnectionStatus.Revoked);
            revoked.RevokedAt.Should().NotBeNull();
            revoked.RevokedReason.Should().Be("organization_deactivated");
            revoked.AccessTokenEncrypted.Should().BeNull();
            revoked.RefreshTokenEncrypted.Should().BeNull();
            revoked.AccessTokenExpiresAt.Should().BeNull();

            var audit = context.Set<SqlOSAuditEvent>().Single(x => x.EventType == Disconnected && x.ActorId == id);
            audit.OrganizationId.Should().Be(organization.Id);
            audit.MetadataJson.Should().Contain("\"reason\":\"organization_deactivated\"");
            audit.MetadataJson.Should().NotContain("access");
        }

        foreach (var id in new[] { otherOrg, personal })
        {
            var untouched = context.Set<SqlOSCalendarConnection>().Single(x => x.Id == id);
            untouched.RevokedAt.Should().BeNull("only the deactivated organization's connections are revoked");
            untouched.RefreshTokenEncrypted.Should().NotBeNull();
        }

        var token = () => services.Calendar.GetAccessTokenAsync(first, forOrganizationId: organization.Id);
        await token.Should().ThrowAsync<InvalidOperationException>().WithMessage("*disconnected*");
        var sync = () => services.Sync.SyncConnectionAsync(first);
        await sync.Should().ThrowAsync<InvalidOperationException>().WithMessage("*disconnected*");
        var refresh = () => services.Calendar.ForceRefreshAsync(second);
        await refresh.Should().ThrowAsync<InvalidOperationException>().WithMessage("*disconnected*");

        (await services.Calendar.GetAccessTokenAsync(otherOrg, forOrganizationId: otherOrganization.Id))
            .AccessToken.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task LifecycleRevocation_IsIdempotentOnRetry()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var organization = await services.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Acme", "acme"));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);
        var connectionId = await ConnectAsync(services, oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, organizationId: organization.Id);

        var now = DateTime.UtcNow;
        await SqlOSAuthLifecyclePolicy.RevokeAsync(
            context, userId: null, organizationId: organization.Id, "organization_deactivated", now,
            scope: SqlOSAuthLifecycleRevocationScope.Offboarding);
        await context.SaveChangesAsync();
        var retry = await SqlOSAuthLifecyclePolicy.RevokeCalendarConnectionsAsync(
            context, userId: null, organizationId: organization.Id, "organization_deactivated", now.AddMinutes(1));
        await context.SaveChangesAsync();
        await services.Calendar.DisconnectAsync(connectionId, "operator_followup");

        retry.Should().BeEmpty();
        var connection = context.Set<SqlOSCalendarConnection>().Single();
        connection.RevokedAt.Should().Be(now);
        connection.RevokedReason.Should().Be("organization_deactivated");
        context.Set<SqlOSAuditEvent>().Count(x => x.EventType == Disconnected).Should().Be(1);
    }

    [TestMethod]
    public async Task OrganizationReactivation_DoesNotReviveCredentials_ExplicitReconnectIsAudited()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var organization = await services.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Acme", "acme"));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);
        var old = await ConnectAsync(services, oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, organizationId: organization.Id);

        await services.Admin.UpdateOrganizationAsync(organization.Id, new SqlOSUpdateOrganizationRequest("Acme", "acme", IsActive: false));
        await services.Admin.UpdateOrganizationAsync(organization.Id, new SqlOSUpdateOrganizationRequest("Acme", "acme", IsActive: true));

        var stale = context.Set<SqlOSCalendarConnection>().Single(x => x.Id == old);
        stale.Status.Should().Be(SqlOSCalendarConnectionStatus.Revoked);
        stale.RefreshTokenEncrypted.Should().BeNull();
        var token = () => services.Calendar.GetAccessTokenAsync(old, forOrganizationId: organization.Id);
        await token.Should().ThrowAsync<InvalidOperationException>().WithMessage("*disconnected*");
        (await services.Calendar.ListConnectionsAsync(organizationId: organization.Id)).Should().BeEmpty();

        var reconnected = await ConnectAsync(services, oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, organizationId: organization.Id);

        reconnected.Should().NotBe(old);
        (await services.Calendar.GetAccessTokenAsync(reconnected, forOrganizationId: organization.Id)).AccessToken.Should().NotBeNullOrEmpty();
        context.Set<SqlOSAuditEvent>().Count(x => x.EventType == "calendar.connection.created" && x.ActorId == reconnected).Should().Be(1);
        context.Set<SqlOSCalendarConnection>().Single(x => x.Id == old).RevokedAt.Should().NotBeNull();
    }

    [TestMethod]
    public async Task UserDeactivatedInDatabase_TokenAccessorRevokesAndFailsClosed()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var user = await services.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Cal User", "cal@example.com", null));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);
        var first = await ConnectAsync(services, oidc.Id, SqlOSCalendarIntegrationMode.ConnectionOnly, userId: user.Id);
        var second = await ConnectAsync(services, oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, userId: user.Id);

        // No admin API deactivates a user; operators flip IsActive through the shared DbContext.
        context.Set<SqlOSUser>().Single(x => x.Id == user.Id).IsActive = false;
        await context.SaveChangesAsync();

        var act = () => services.Calendar.GetAccessTokenAsync(first, forUserId: user.Id);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*disconnected*");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*disconnected*");

        foreach (var id in new[] { first, second })
        {
            var connection = context.Set<SqlOSCalendarConnection>().Single(x => x.Id == id);
            connection.Status.Should().Be(SqlOSCalendarConnectionStatus.Revoked);
            connection.RevokedReason.Should().Be("user_inactive");
            connection.AccessTokenEncrypted.Should().BeNull();
            connection.RefreshTokenEncrypted.Should().BeNull();
            context.Set<SqlOSAuditEvent>().Count(x => x.EventType == Disconnected && x.ActorId == id).Should().Be(1);
        }
    }

    [TestMethod]
    public async Task Scheduler_RechecksLifecycleAtExecution_AndStopsSyncingDeactivatedOwners()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var organization = await services.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Acme", "acme"));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);
        var connectionId = await ConnectAsync(services, oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, organizationId: organization.Id);

        context.Set<SqlOSOrganization>().Single(x => x.Id == organization.Id).IsActive = false;
        await context.SaveChangesAsync();

        var firstRun = await services.Sync.SyncDueConnectionsAsync();
        var secondRun = await services.Sync.SyncDueConnectionsAsync();

        firstRun.Should().Be(1, "the due query picked the connection up once before it was revoked");
        secondRun.Should().Be(0, "revoked connections are never scheduled again");
        var connection = context.Set<SqlOSCalendarConnection>().Single();
        connection.Status.Should().Be(SqlOSCalendarConnectionStatus.Revoked);
        connection.RevokedReason.Should().Be("organization_inactive");
        connection.RefreshTokenEncrypted.Should().BeNull();
        context.Set<SqlOSCalendarEvent>().Should().BeEmpty("no provider data is pulled for a deactivated owner");
        context.Set<SqlOSCalendarSyncState>().Should().BeEmpty();
        context.Set<SqlOSAuditEvent>().Any(x => x.EventType == "calendar.connection.synced").Should().BeFalse();
        context.Set<SqlOSAuditEvent>().Count(x => x.EventType == Disconnected).Should().Be(1);
    }

    [TestMethod]
    public async Task RefreshRacingOffboarding_DropsFreshProviderTokens()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        using var context = CreateContext(databaseName);
        var services = CreateServices(context);
        var organization = await services.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Acme", "acme"));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);
        var connectionId = await ConnectAsync(services, oidc.Id, SqlOSCalendarIntegrationMode.ConnectionOnly, organizationId: organization.Id);

        // The refresh path has already loaded a live connection when offboarding commits.
        var inFlight = await services.Calendar.RequireConnectionAsync(connectionId, null, null, includeRevoked: false, CancellationToken.None);
        using (var offboarding = CreateContext(databaseName))
        {
            await SqlOSAuthLifecyclePolicy.RevokeAsync(
                offboarding, userId: null, organizationId: organization.Id, "organization_deactivated", DateTime.UtcNow,
                scope: SqlOSAuthLifecycleRevocationScope.Offboarding);
            await offboarding.SaveChangesAsync();
        }

        // The provider accepts the refresh, but the local write loses the concurrency race.
        var act = () => services.Calendar.EnsureFreshAccessTokenAsync(inFlight, forceRefresh: true);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*disconnected*");

        using var verify = CreateContext(databaseName);
        var stored = verify.Set<SqlOSCalendarConnection>().Single();
        stored.Status.Should().Be(SqlOSCalendarConnectionStatus.Revoked);
        stored.AccessTokenEncrypted.Should().BeNull("a refresh must never put credentials back on a revoked row");
        stored.RefreshTokenEncrypted.Should().BeNull();
        stored.RevokedReason.Should().Be("organization_deactivated");
        inFlight.RefreshTokenEncrypted.Should().BeNull("the loser reloads the revoked state");
        verify.Set<SqlOSAuditEvent>().Count(x => x.EventType == Disconnected).Should().Be(1);
    }

    [TestMethod]
    public async Task SyncRacingOffboarding_DoesNotResurrectConnectionOrStoreEvents()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        using var context = CreateContext(databaseName);
        var services = CreateServices(context);
        var organization = await services.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Acme", "acme"));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);
        var connectionId = await ConnectAsync(services, oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, organizationId: organization.Id);

        // The worker's context already tracks the live row when offboarding commits elsewhere.
        _ = await services.Calendar.RequireConnectionAsync(connectionId, null, null, includeRevoked: false, CancellationToken.None);
        using (var offboarding = CreateContext(databaseName))
        {
            await SqlOSAuthLifecyclePolicy.RevokeAsync(
                offboarding, userId: null, organizationId: organization.Id, "organization_deactivated", DateTime.UtcNow,
                scope: SqlOSAuthLifecycleRevocationScope.Offboarding);
            await offboarding.SaveChangesAsync();
        }

        var result = await services.Sync.SyncConnectionAsync(connectionId);

        result.Errors.Should().ContainSingle(x => x.Contains("disconnected"));
        using var verify = CreateContext(databaseName);
        var stored = verify.Set<SqlOSCalendarConnection>().Single();
        stored.Status.Should().Be(SqlOSCalendarConnectionStatus.Revoked);
        stored.RevokedReason.Should().Be("organization_deactivated");
        stored.LastSyncAt.Should().BeNull();
        stored.AccessTokenEncrypted.Should().BeNull();
        verify.Set<SqlOSCalendarEvent>().Should().BeEmpty("events pulled for a revoked connection are discarded");
        // The in-memory provider is not transactional, so the sync-state row written in the same
        // batch as the conflicting connection update is asserted on SQL Server instead
        // (CalendarIntegrationTests.OrganizationDeactivation_RevokesConnectionsAtomically_AndRacingSyncFailsClosed).
        verify.Set<SqlOSAuditEvent>().Any(x => x.EventType == "calendar.sync.failed").Should().BeTrue();
    }

    [TestMethod]
    public async Task ProviderFailure_DuringOffboardedRefresh_StillFailsClosed()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var user = await services.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Cal User", "cal@example.com", null));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);
        var connectionId = await ConnectAsync(services, oidc.Id, SqlOSCalendarIntegrationMode.ConnectionOnly, userId: user.Id);

        var connection = context.Set<SqlOSCalendarConnection>().Single();
        connection.AccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        connection.RefreshTokenEncrypted = services.Crypto.ProtectSecret("revoked-by-provider");
        context.Set<SqlOSUser>().Single(x => x.Id == user.Id).IsActive = false;
        await context.SaveChangesAsync();

        var act = () => services.Calendar.GetAccessTokenAsync(connectionId, forUserId: user.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*disconnected*");
        connection.Status.Should().Be(SqlOSCalendarConnectionStatus.Revoked, "local revocation does not depend on the provider answering");
        connection.RefreshTokenEncrypted.Should().BeNull();
        context.Set<SqlOSAuditEvent>().Any(x => x.EventType == "calendar.connection.refresh_failed").Should().BeFalse("the provider was never called");
    }

    [TestMethod]
    public async Task SignOutShapedRevocation_KeepsCalendarConnections()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var user = await services.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Cal User", "cal@example.com", "OldPassword123!"));
        var organization = await services.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Acme", "acme"));
        await services.Admin.CreateMembershipAsync(organization.Id, new SqlOSCreateMembershipRequest(user.Id, "member"));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);
        var personal = await ConnectAsync(services, oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, userId: user.Id);
        var shared = await ConnectAsync(services, oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, organizationId: organization.Id);

        // Logout-all and password reset revoke sessions only.
        await SqlOSAuthLifecyclePolicy.RevokeAsync(context, user.Id, null, "logout_all", DateTime.UtcNow);
        await SqlOSAuthLifecyclePolicy.RevokeAsync(context, user.Id, null, "password_reset", DateTime.UtcNow);
        // Membership offboarding leaves the (still active) user's personal calendar and the
        // (still active) organization's calendar alone.
        await SqlOSAuthLifecyclePolicy.RevokeForDenialAsync(
            context, user.Id, organization.Id, SqlOSAuthLifecycleDecision.Denied("membership_inactive"), DateTime.UtcNow);
        await context.SaveChangesAsync();

        context.Set<SqlOSCalendarConnection>().Should().OnlyContain(x => x.RevokedAt == null && x.RefreshTokenEncrypted != null);
        (await services.Calendar.GetAccessTokenAsync(personal, forUserId: user.Id)).AccessToken.Should().NotBeNullOrEmpty();
        (await services.Calendar.GetAccessTokenAsync(shared, forOrganizationId: organization.Id)).AccessToken.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task InactiveOwner_CannotStartOrCompleteConnect()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var user = await services.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Cal User", "cal@example.com", null));
        var organization = await services.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Acme", "acme"));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);
        context.Set<SqlOSUser>().Single(x => x.Id == user.Id).IsActive = false;
        context.Set<SqlOSOrganization>().Single(x => x.Id == organization.Id).IsActive = false;
        await context.SaveChangesAsync();

        var startUser = () => services.Calendar.StartConnectAsync(new SqlOSStartCalendarConnectRequest(
            oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, ReturnUri, UserId: user.Id));
        var startOrganization = () => services.Calendar.StartConnectAsync(new SqlOSStartCalendarConnectRequest(
            oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, ReturnUri, OrganizationId: organization.Id));
        var complete = () => services.Calendar.CompleteConnectAsync(
            GooglePayload(oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, organizationId: organization.Id),
            "success:cal@example.com");

        await startUser.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not active*");
        await startOrganization.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not active*");
        await complete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not active*");
        context.Set<SqlOSCalendarConnection>().Should().BeEmpty();
        context.Set<SqlOSAuditEvent>().Any(x => x.EventType == "calendar.connect.error").Should().BeTrue();
    }

    [TestMethod]
    public async Task ScimDeprovisioning_OfLastMembership_RevokesUserConnections()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var organization = await services.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Acme", "acme"));
        var scimConnection = await CreateScimConnectionAsync(services.Admin, organization.Id);
        var scim = new SqlOSScimService(context, Options.Create(new SqlOSAuthServerOptions()), services.Crypto);
        var provisioned = await scim.UpsertUserAsync(scimConnection, ScimUser("idp-user-1", "ada@example.test", active: true), replace: false);
        var userId = provisioned["id"]!.GetValue<string>();
        var oidc = await CreateGoogleConnectionAsync(services.Admin);
        var connectionId = await ConnectAsync(services, oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, userId: userId);

        await scim.UpsertUserAsync(scimConnection, ScimUser("idp-user-1", "ada@example.test", active: false), replace: false);

        context.Set<SqlOSUser>().Single(x => x.Id == userId).IsActive.Should().BeFalse();
        var connection = context.Set<SqlOSCalendarConnection>().Single(x => x.Id == connectionId);
        connection.Status.Should().Be(SqlOSCalendarConnectionStatus.Revoked);
        connection.RevokedReason.Should().Be("scim_deprovisioned");
        connection.RefreshTokenEncrypted.Should().BeNull();
        context.Set<SqlOSAuditEvent>().Count(x => x.EventType == Disconnected && x.ActorId == connectionId).Should().Be(1);

        // Reactivation through SCIM restores membership but never the old credentials.
        await scim.UpsertUserAsync(scimConnection, ScimUser("idp-user-1", "ada@example.test", active: true), replace: false);
        context.Set<SqlOSUser>().Single(x => x.Id == userId).IsActive.Should().BeTrue();
        context.Set<SqlOSCalendarConnection>().Single(x => x.Id == connectionId).RevokedAt.Should().NotBeNull();
        var act = () => services.Calendar.GetAccessTokenAsync(connectionId, forUserId: userId);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*disconnected*");
    }

    [TestMethod]
    public async Task ScimDeprovisioning_WithAnotherActiveMembership_KeepsUserConnections()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var organization = await services.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Acme", "acme"));
        var other = await services.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Globex", "globex"));
        var scimConnection = await CreateScimConnectionAsync(services.Admin, organization.Id);
        var scim = new SqlOSScimService(context, Options.Create(new SqlOSAuthServerOptions()), services.Crypto);
        var provisioned = await scim.UpsertUserAsync(scimConnection, ScimUser("idp-user-2", "grace@example.test", active: true), replace: false);
        var userId = provisioned["id"]!.GetValue<string>();
        await services.Admin.CreateMembershipAsync(other.Id, new SqlOSCreateMembershipRequest(userId, "member"));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);
        var connectionId = await ConnectAsync(services, oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, userId: userId);

        await scim.UpsertUserAsync(scimConnection, ScimUser("idp-user-2", "grace@example.test", active: false), replace: false);

        context.Set<SqlOSUser>().Single(x => x.Id == userId).IsActive.Should().BeTrue();
        var connection = context.Set<SqlOSCalendarConnection>().Single(x => x.Id == connectionId);
        connection.RevokedAt.Should().BeNull("a user connection is not bound to the deprovisioned organization");
        (await services.Calendar.GetAccessTokenAsync(connectionId, forUserId: userId)).AccessToken.Should().NotBeNullOrEmpty();
    }

    private static JsonObject ScimUser(string externalId, string userName, bool active) => new()
    {
        ["externalId"] = externalId,
        ["userName"] = userName,
        ["displayName"] = userName,
        ["active"] = active
    };

    private static async Task<SqlOSScimConnection> CreateScimConnectionAsync(SqlOSAdminService admin, string organizationId)
    {
        var connection = await admin.CreateScimConnectionDraftAsync(new SqlOSCreateScimConnectionRequest(organizationId, "Directory", false));
        await admin.RotateScimTokenAsync(connection.Id);
        return await admin.SetScimConnectionEnabledAsync(connection.Id, true);
    }

    private static async Task<string> ConnectAsync(
        CalendarTestServices services,
        string oidcConnectionId,
        SqlOSCalendarIntegrationMode mode,
        string? userId = null,
        string? organizationId = null)
    {
        var completion = await services.Calendar.CompleteConnectAsync(
            GooglePayload(oidcConnectionId, mode, userId, organizationId),
            "success:cal@example.com");
        return completion.CalendarConnectionId;
    }

    private static CalendarConnectRequestPayload GooglePayload(
        string oidcConnectionId,
        SqlOSCalendarIntegrationMode mode,
        string? userId = null,
        string? organizationId = null)
        => new(
            oidcConnectionId,
            mode,
            userId,
            organizationId,
            null,
            ["openid", "email", "https://www.googleapis.com/auth/calendar.readonly"],
            ReturnUri,
            "verifier",
            "https://tests.example.local/sqlos/auth/calendar/callback",
            "https://oauth2.googleapis.com/token");

    private static async Task<SqlOSOidcConnection> CreateGoogleConnectionAsync(SqlOSAdminService admin)
        => await admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.Google, "Google", "google-client", "google-secret",
            ["https://app.example.local/callback/google"], true,
            null, null, null, null, null, null, null, null, null, null, null));

    private sealed record CalendarTestServices(
        SqlOSAdminService Admin,
        SqlOSCryptoService Crypto,
        SqlOSCalendarService Calendar,
        SqlOSCalendarSyncService Sync);

    private static CalendarTestServices CreateServices(TestSqlOSInMemoryDbContext context)
    {
        var sqlosOptions = new SqlOSOptions();
        sqlosOptions.AuthServer.PublicOrigin = "https://tests.example.local";
        var authOptions = Options.Create(sqlosOptions.AuthServer);
        var crypto = TestCryptoService.Create(context, authOptions, new EphemeralDataProtectionProvider());
        var admin = new SqlOSAdminService(context, authOptions, crypto);
        var httpFactory = new FakeCalendarProviderHttpClientFactory();
        var adapters = new ISqlOSCalendarProviderAdapter[]
        {
            new SqlOSGoogleCalendarAdapter(httpFactory),
            new SqlOSMicrosoftGraphCalendarAdapter(httpFactory)
        };
        var calendar = new SqlOSCalendarService(
            context, admin, crypto, httpFactory, adapters, Options.Create(sqlosOptions), NullLogger<SqlOSCalendarService>.Instance);
        var sync = new SqlOSCalendarSyncService(
            context, admin, crypto, calendar, Options.Create(sqlosOptions), NullLogger<SqlOSCalendarSyncService>.Instance);
        return new CalendarTestServices(admin, crypto, calendar, sync);
    }

    private static TestSqlOSInMemoryDbContext CreateContext(string? databaseName = null)
        => new(new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString("N"))
            .Options);
}

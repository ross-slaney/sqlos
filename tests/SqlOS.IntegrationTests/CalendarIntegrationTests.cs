using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Calendar.Contracts;
using SqlOS.Calendar.Interfaces;
using SqlOS.Calendar.Models;
using SqlOS.Calendar.Services;
using SqlOS.Configuration;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

[TestClass]
public sealed class CalendarIntegrationTests
{
    private const string ReturnUri = "https://app.example.local/settings/calendar";

    /// <summary>
    /// Calendar connections hold foreign keys to social connections, so leftover rows would
    /// break other test classes that clear all OIDC connections in their own resets. Clean
    /// up after every test (including failures) so no calendar rows outlive this class.
    /// </summary>
    [TestCleanup]
    public async Task CleanupAsync() => await ResetCalendarStateAsync();

    [TestMethod]
    public async Task GoogleReadPull_ConnectSyncListDisconnect_WorksOnSqlServer()
    {
        await ResetCalendarStateAsync();
        var services = CreateServices();
        var user = await services.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Calendar User", $"calendar-{Guid.NewGuid():N}@example.com", null));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);

        var completion = await services.Calendar.CompleteConnectAsync(
            GooglePayload(oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, userId: user.Id),
            "success:calendar-int@example.com");

        completion.ProviderAccountEmail.Should().Be("calendar-int@example.com");

        var stored = await AspireFixture.SharedContext.Set<SqlOSCalendarConnection>()
            .FirstAsync(x => x.Id == completion.CalendarConnectionId);
        stored.AccessTokenEncrypted.Should().StartWith("dp:");
        stored.RefreshTokenEncrypted.Should().StartWith("dp:");

        var calendars = await services.Calendar.ListProviderCalendarsAsync(completion.CalendarConnectionId);
        calendars.Should().Contain(x => x.ProviderCalendarId == "google-primary" && x.IsPrimary);

        var sync = await services.Sync.SyncConnectionAsync(completion.CalendarConnectionId);
        sync.Errors.Should().BeEmpty();
        sync.EventsUpserted.Should().Be(2);

        var events = await services.Calendar.ListEventsAsync(
            completion.CalendarConnectionId,
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            forUserId: user.Id);
        events.Select(x => x.ProviderEventId).Should().BeEquivalentTo(["google-evt-1", "google-evt-2"]);

        var secondSync = await services.Sync.SyncConnectionAsync(completion.CalendarConnectionId);
        secondSync.Errors.Should().BeEmpty();
        secondSync.EventsRemoved.Should().Be(1);

        var disconnect = await services.Calendar.DisconnectAsync(completion.CalendarConnectionId, "test_complete");
        disconnect.Status.Should().Be(nameof(SqlOSCalendarConnectionStatus.Revoked));
        (await AspireFixture.SharedContext.Set<SqlOSCalendarConnection>()
            .FirstAsync(x => x.Id == completion.CalendarConnectionId)).AccessTokenEncrypted.Should().BeNull();
    }

    [TestMethod]
    public async Task MicrosoftOrganizationConnection_MirrorsGoogle()
    {
        await ResetCalendarStateAsync();
        var services = CreateServices();
        var organization = await services.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest(
            $"Calendar Org {Guid.NewGuid():N}"[..28], $"cal-org-{Guid.NewGuid():N}"[..20]));
        var oidc = await CreateMicrosoftConnectionAsync(services.Admin);

        var completion = await services.Calendar.CompleteConnectAsync(
            new CalendarConnectRequestPayload(
                oidc.Id,
                SqlOSCalendarIntegrationMode.ReadPull,
                null,
                organization.Id,
                "Org Outlook",
                ["openid", "email", "offline_access", "Calendars.Read"],
                ReturnUri,
                "verifier",
                "https://tests/sqlos/auth/calendar/callback",
                "https://login.microsoftonline.com/common/oauth2/v2.0/token"),
            "success:calendar-ms@example.com");

        completion.OrganizationId.Should().Be(organization.Id);

        var sync = await services.Sync.SyncConnectionAsync(completion.CalendarConnectionId);
        sync.Errors.Should().BeEmpty();
        sync.EventsUpserted.Should().Be(2);

        var events = await services.Calendar.ListEventsAsync(
            completion.CalendarConnectionId,
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            forOrganizationId: organization.Id);
        events.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task ConnectionOnly_TokenAccessorRefreshesAndNeverStoresEvents()
    {
        await ResetCalendarStateAsync();
        var services = CreateServices();
        var user = await services.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Token User", $"calendar-token-{Guid.NewGuid():N}@example.com", null));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);

        var completion = await services.Calendar.CompleteConnectAsync(
            GooglePayload(oidc.Id, SqlOSCalendarIntegrationMode.ConnectionOnly, userId: user.Id),
            "success:calendar-token@example.com");

        var connection = await AspireFixture.SharedContext.Set<SqlOSCalendarConnection>()
            .FirstAsync(x => x.Id == completion.CalendarConnectionId);
        connection.AccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await AspireFixture.SharedContext.SaveChangesAsync();

        var token = await services.Calendar.GetAccessTokenAsync(completion.CalendarConnectionId, forUserId: user.Id);

        token.AccessToken.Should().Be("google-access-refreshed|calendar-token@example.com");
        (await AspireFixture.SharedContext.Set<SqlOSCalendarEvent>()
            .CountAsync(x => x.CalendarConnectionId == completion.CalendarConnectionId)).Should().Be(0);
    }

    [TestMethod]
    public async Task RevokedRefreshToken_MarksErrorAndWrongOwnerIsRejected()
    {
        await ResetCalendarStateAsync();
        var services = CreateServices();
        var user = await services.Admin.CreateUserAsync(new SqlOSCreateUserRequest(
            "Error User", $"calendar-error-{Guid.NewGuid():N}@example.com", null));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);

        var completion = await services.Calendar.CompleteConnectAsync(
            GooglePayload(oidc.Id, SqlOSCalendarIntegrationMode.ConnectionOnly, userId: user.Id),
            "success:calendar-error@example.com");

        var wrongOwner = () => services.Calendar.GetAccessTokenAsync(completion.CalendarConnectionId, forUserId: "usr_someone_else");
        await wrongOwner.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");

        var connection = await AspireFixture.SharedContext.Set<SqlOSCalendarConnection>()
            .FirstAsync(x => x.Id == completion.CalendarConnectionId);
        connection.AccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        connection.RefreshTokenEncrypted = services.Crypto.ProtectSecret("revoked-refresh");
        await AspireFixture.SharedContext.SaveChangesAsync();

        var act = () => services.Calendar.GetAccessTokenAsync(completion.CalendarConnectionId, forUserId: user.Id);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*revoked*");

        (await AspireFixture.SharedContext.Set<SqlOSCalendarConnection>()
            .FirstAsync(x => x.Id == completion.CalendarConnectionId)).Status
            .Should().Be(SqlOSCalendarConnectionStatus.Error);
    }

    [TestMethod]
    public async Task OrganizationDeactivation_RevokesConnectionsAtomically_AndRacingSyncFailsClosed()
    {
        await ResetCalendarStateAsync();
        var services = CreateServices();
        var organization = await services.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest(
            $"Offboarding {Guid.NewGuid():N}", null));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);
        var readPull = await services.Calendar.CompleteConnectAsync(
            GooglePayload(oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, organizationId: organization.Id),
            "success:offboard-a@example.com");
        var connectionOnly = await services.Calendar.CompleteConnectAsync(
            GooglePayload(oidc.Id, SqlOSCalendarIntegrationMode.ConnectionOnly, organizationId: organization.Id),
            "success:offboard-b@example.com");

        // Phase 1 - race: a worker already tracks both live rows when the central lifecycle
        // policy revokes them from another context (the organization row itself stays active
        // so the execution-time recheck passes and the concurrency token is what stops the write).
        var syncing = await services.Calendar.RequireConnectionAsync(
            readPull.CalendarConnectionId, null, null, includeRevoked: false, CancellationToken.None);
        var refreshing = await services.Calendar.RequireConnectionAsync(
            connectionOnly.CalendarConnectionId, null, null, includeRevoked: false, CancellationToken.None);
        await using (var offboarding = CreateIsolatedContext())
        {
            await SqlOSAuthLifecyclePolicy.RevokeAsync(
                offboarding, userId: null, organizationId: organization.Id, "organization_deactivated", DateTime.UtcNow,
                scope: SqlOSAuthLifecycleRevocationScope.Offboarding);
            await offboarding.SaveChangesAsync();
        }

        var sync = await services.Sync.SyncConnectionAsync(readPull.CalendarConnectionId);
        sync.Errors.Should().ContainSingle(x => x.Contains("disconnected"));
        var refresh = () => services.Calendar.EnsureFreshAccessTokenAsync(refreshing, forceRefresh: true);
        await refresh.Should().ThrowAsync<InvalidOperationException>().WithMessage("*disconnected*");
        syncing.RevokedAt.Should().NotBeNull("the loser reloads the committed revocation");
        refreshing.RefreshTokenEncrypted.Should().BeNull();

        await using (var verify = CreateIsolatedContext())
        {
            foreach (var id in new[] { readPull.CalendarConnectionId, connectionOnly.CalendarConnectionId })
            {
                var stored = await verify.Set<SqlOSCalendarConnection>().AsNoTracking().SingleAsync(x => x.Id == id);
                stored.Status.Should().Be(SqlOSCalendarConnectionStatus.Revoked);
                stored.RevokedReason.Should().Be("organization_deactivated");
                stored.AccessTokenEncrypted.Should().BeNull("a racing refresh must not put provider tokens back");
                stored.RefreshTokenEncrypted.Should().BeNull();
                stored.LastSyncAt.Should().BeNull();
                (await verify.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "calendar.connection.disconnected" && x.ActorId == id))
                    .Should().Be(1);
            }

            // SaveChanges is one transaction on SQL Server: the sync-state and event rows staged
            // alongside the conflicting connection update never reach the database.
            (await verify.Set<SqlOSCalendarSyncState>().AnyAsync(x => x.CalendarConnectionId == readPull.CalendarConnectionId)).Should().BeFalse();
            (await verify.Set<SqlOSCalendarEvent>().AnyAsync(x => x.CalendarConnectionId == readPull.CalendarConnectionId)).Should().BeFalse();
        }

        // Phase 2 - production path: deactivating the organization through its transaction and
        // organization lock revokes a fresh connection, and reactivation does not revive it.
        var third = await services.Calendar.CompleteConnectAsync(
            GooglePayload(oidc.Id, SqlOSCalendarIntegrationMode.ConnectionOnly, organizationId: organization.Id),
            "success:offboard-c@example.com");
        await using (var admin = CreateIsolatedContext())
        {
            var authOptions = Options.Create(AspireFixture.Options);
            var adminService = new SqlOSAdminService(admin, authOptions, new SqlOSCryptoService(admin, authOptions, AspireFixture.DataProtectionProvider));
            await adminService.UpdateOrganizationAsync(
                organization.Id,
                new SqlOSUpdateOrganizationRequest(organization.Name, organization.Slug, IsActive: false));
        }

        var blocked = () => services.Calendar.GetAccessTokenAsync(third.CalendarConnectionId, forOrganizationId: organization.Id);
        await blocked.Should().ThrowAsync<InvalidOperationException>().WithMessage("*disconnected*");
        await using (var verify = CreateIsolatedContext())
        {
            var stored = await verify.Set<SqlOSCalendarConnection>().AsNoTracking().SingleAsync(x => x.Id == third.CalendarConnectionId);
            stored.Status.Should().Be(SqlOSCalendarConnectionStatus.Revoked);
            stored.RevokedReason.Should().Be("organization_deactivated");
            stored.RefreshTokenEncrypted.Should().BeNull();
            (await verify.Set<SqlOSAuditEvent>().CountAsync(x => x.EventType == "calendar.connection.disconnected" && x.ActorId == third.CalendarConnectionId))
                .Should().Be(1);
        }

        await services.Admin.UpdateOrganizationAsync(
            organization.Id,
            new SqlOSUpdateOrganizationRequest(organization.Name, organization.Slug, IsActive: true));
        (await services.Calendar.ListConnectionsAsync(organizationId: organization.Id)).Should().BeEmpty();
        var reconnected = await services.Calendar.CompleteConnectAsync(
            GooglePayload(oidc.Id, SqlOSCalendarIntegrationMode.ConnectionOnly, organizationId: organization.Id),
            "success:offboard-d@example.com");
        (await services.Calendar.GetAccessTokenAsync(reconnected.CalendarConnectionId, forOrganizationId: organization.Id))
            .AccessToken.Should().NotBeNullOrEmpty();
    }

    private static TestSqlOSDbContext CreateIsolatedContext()
        => new(new DbContextOptionsBuilder<TestSqlOSDbContext>().UseTestProvider(AspireFixture.SqlConnectionString).Options);

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
            "https://tests/sqlos/auth/calendar/callback",
            "https://oauth2.googleapis.com/token");

    private static async Task<SqlOSOidcConnection> CreateGoogleConnectionAsync(SqlOSAdminService admin)
        => await admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.Google, "Google", "google-client", "google-secret",
            ["https://app.example.local/callback/google"], true,
            null, null, null, null, null, null, null, null, null, null, null));

    private static async Task<SqlOSOidcConnection> CreateMicrosoftConnectionAsync(SqlOSAdminService admin)
        => await admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.Microsoft, "Microsoft", "ms-client", "ms-secret",
            ["https://app.example.local/callback/microsoft"], true,
            null, null, null, null, null, null, "common", null, null, null, null));

    private sealed record CalendarIntegrationServices(
        SqlOSAdminService Admin,
        SqlOSCryptoService Crypto,
        SqlOSCalendarService Calendar,
        SqlOSCalendarSyncService Sync);

    private static CalendarIntegrationServices CreateServices()
    {
        var authOptions = Options.Create(AspireFixture.Options);
        var crypto = new SqlOSCryptoService(AspireFixture.SharedContext, authOptions, AspireFixture.DataProtectionProvider);
        var admin = new SqlOSAdminService(AspireFixture.SharedContext, authOptions, crypto);

        var sqlosOptions = new SqlOSOptions();
        sqlosOptions.AuthServer.PublicOrigin = "https://tests.example.local";
        var httpFactory = new FakeCalendarProviderHttpClientFactory();
        var adapters = new ISqlOSCalendarProviderAdapter[]
        {
            new SqlOSGoogleCalendarAdapter(httpFactory),
            new SqlOSMicrosoftGraphCalendarAdapter(httpFactory)
        };
        var calendar = new SqlOSCalendarService(
            AspireFixture.SharedContext,
            admin,
            crypto,
            httpFactory,
            adapters,
            Options.Create(sqlosOptions),
            NullLogger<SqlOSCalendarService>.Instance);
        var sync = new SqlOSCalendarSyncService(
            AspireFixture.SharedContext,
            admin,
            crypto,
            calendar,
            Options.Create(sqlosOptions),
            NullLogger<SqlOSCalendarSyncService>.Instance);
        return new CalendarIntegrationServices(admin, crypto, calendar, sync);
    }

    private static async Task ResetCalendarStateAsync()
    {
        var context = AspireFixture.SharedContext;
        var events = await context.Set<SqlOSCalendarEvent>().ToListAsync();
        context.Set<SqlOSCalendarEvent>().RemoveRange(events);
        var states = await context.Set<SqlOSCalendarSyncState>().ToListAsync();
        context.Set<SqlOSCalendarSyncState>().RemoveRange(states);
        var connections = await context.Set<SqlOSCalendarConnection>().ToListAsync();
        context.Set<SqlOSCalendarConnection>().RemoveRange(connections);
        await context.SaveChangesAsync();

        // Providers are unique per type, so clear social connections the same way the
        // OIDC integration tests do (external identities first to satisfy the FK).
        var externalIdentities = await context.Set<SqlOSExternalIdentity>()
            .Where(x => x.OidcConnectionId != null)
            .ToListAsync();
        context.Set<SqlOSExternalIdentity>().RemoveRange(externalIdentities);
        var oidcConnections = await context.Set<SqlOSOidcConnection>().ToListAsync();
        context.Set<SqlOSOidcConnection>().RemoveRange(oidcConnections);
        await context.SaveChangesAsync();
    }
}

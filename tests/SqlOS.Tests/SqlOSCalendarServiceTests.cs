using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Calendar.Configuration;
using SqlOS.Calendar.Contracts;
using SqlOS.Calendar.Interfaces;
using SqlOS.Calendar.Models;
using SqlOS.Calendar.Services;
using SqlOS.Configuration;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSCalendarServiceTests
{
    private const string ReturnUri = "https://app.example.local/settings/calendar";

    [TestMethod]
    public async Task StartConnect_GoogleReadPull_RequestsOfflineCalendarScopes()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var user = await services.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Cal User", "cal@example.com", null));
        var connection = await CreateGoogleConnectionAsync(services.Admin);

        var result = await services.Calendar.StartConnectAsync(new SqlOSStartCalendarConnectRequest(
            connection.Id,
            SqlOSCalendarIntegrationMode.ReadPull,
            ReturnUri,
            UserId: user.Id));

        result.ProviderType.Should().Be(SqlOSCalendarProviderType.Google);
        result.AuthorizationUrl.Should().StartWith("https://accounts.google.com/o/oauth2/v2/auth");

        var query = QueryHelpers.ParseQuery(new Uri(result.AuthorizationUrl).Query);
        query["access_type"].ToString().Should().Be("offline");
        query["prompt"].ToString().Should().Be("consent");
        query["scope"].ToString().Should().Contain("https://www.googleapis.com/auth/calendar.readonly");
        query["scope"].ToString().Should().NotContain("calendar.events");
        query["code_challenge_method"].ToString().Should().Be("S256");
        query["redirect_uri"].ToString().Should().Be("https://tests.example.local/sqlos/auth/calendar/callback");
        query["state"].ToString().Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task StartConnect_TwoWay_AddsWriteScopes()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var user = await services.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Cal User", "cal@example.com", null));
        var connection = await CreateGoogleConnectionAsync(services.Admin);

        var result = await services.Calendar.StartConnectAsync(new SqlOSStartCalendarConnectRequest(
            connection.Id,
            SqlOSCalendarIntegrationMode.TwoWay,
            ReturnUri,
            UserId: user.Id));

        var query = QueryHelpers.ParseQuery(new Uri(result.AuthorizationUrl).Query);
        query["scope"].ToString().Should().Contain("https://www.googleapis.com/auth/calendar.events");
    }

    [TestMethod]
    public async Task StartConnect_BothUserAndOrganization_Throws()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var connection = await CreateGoogleConnectionAsync(services.Admin);

        var act = () => services.Calendar.StartConnectAsync(new SqlOSStartCalendarConnectRequest(
            connection.Id,
            SqlOSCalendarIntegrationMode.ReadPull,
            ReturnUri,
            UserId: "usr_x",
            OrganizationId: "org_x"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exactly one user or one organization*");
    }

    [TestMethod]
    public async Task StartConnect_GitHubProvider_Throws()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var user = await services.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Cal User", "cal@example.com", null));
        var gitHub = await services.Admin.CreateOidcConnectionAsync(new SqlOSCreateOidcConnectionRequest(
            SqlOSOidcProviderType.GitHub, "GitHub", "gh-client", "gh-secret",
            ["https://app.example.local/callback"], true,
            null, null, null, null, null, null, null, null, null, null, null));

        var act = () => services.Calendar.StartConnectAsync(new SqlOSStartCalendarConnectRequest(
            gitHub.Id,
            SqlOSCalendarIntegrationMode.ReadPull,
            ReturnUri,
            UserId: user.Id));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Google and Microsoft*");
    }

    [TestMethod]
    public async Task CompleteConnect_Google_StoresEncryptedTokensAndAccount()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var user = await services.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Cal User", "cal@example.com", null));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);

        var result = await services.Calendar.CompleteConnectAsync(
            GooglePayload(oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, userId: user.Id),
            "success:cal@example.com");

        result.ProviderAccountEmail.Should().Be("cal@example.com");
        var stored = context.Set<SqlOSCalendarConnection>().Single();
        stored.UserId.Should().Be(user.Id);
        stored.Mode.Should().Be(SqlOSCalendarIntegrationMode.ReadPull);
        stored.Status.Should().Be(SqlOSCalendarConnectionStatus.Active);
        stored.AccessTokenEncrypted.Should().StartWith("dp:");
        stored.AccessTokenEncrypted.Should().NotContain("google-access|cal@example.com");
        stored.RefreshTokenEncrypted.Should().StartWith("dp:");
        stored.ProviderAccountSubject.Should().Be("google-cal-cal@example.com");
        stored.AccessTokenExpiresAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(30));
    }

    [TestMethod]
    public async Task CompleteConnect_ReadPullWithoutRefreshToken_Throws()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var user = await services.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Cal User", "cal@example.com", null));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);

        var act = () => services.Calendar.CompleteConnectAsync(
            GooglePayload(oidc.Id, SqlOSCalendarIntegrationMode.ReadPull, userId: user.Id),
            "norefresh:cal@example.com");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*refresh token*");
        context.Set<SqlOSCalendarConnection>().Count().Should().Be(0);
    }

    [TestMethod]
    public async Task ConnectCallback_EndToEnd_RedirectsToReturnUriWithConnectionId()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var user = await services.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Cal User", "cal@example.com", null));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);

        var start = await services.Calendar.StartConnectAsync(new SqlOSStartCalendarConnectRequest(
            oidc.Id,
            SqlOSCalendarIntegrationMode.ConnectionOnly,
            ReturnUri,
            UserId: user.Id));
        var state = QueryHelpers.ParseQuery(new Uri(start.AuthorizationUrl).Query)["state"].ToString();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString($"?code={Uri.EscapeDataString("success:cal@example.com")}&state={Uri.EscapeDataString(state)}");

        var result = await services.Calendar.HandleConnectCallbackAsync(httpContext);

        var redirect = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult>().Subject;
        redirect.Url.Should().StartWith(ReturnUri);
        var connection = context.Set<SqlOSCalendarConnection>().Single();
        redirect.Url.Should().Contain($"calendarConnectionId={connection.Id}");
    }

    [TestMethod]
    public async Task ConnectCallback_ReplayedState_Fails()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var user = await services.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Cal User", "cal@example.com", null));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);

        var start = await services.Calendar.StartConnectAsync(new SqlOSStartCalendarConnectRequest(
            oidc.Id,
            SqlOSCalendarIntegrationMode.ConnectionOnly,
            ReturnUri,
            UserId: user.Id));
        var state = QueryHelpers.ParseQuery(new Uri(start.AuthorizationUrl).Query)["state"].ToString();

        var first = new DefaultHttpContext();
        first.Request.QueryString = new QueryString($"?code={Uri.EscapeDataString("success:cal@example.com")}&state={Uri.EscapeDataString(state)}");
        await services.Calendar.HandleConnectCallbackAsync(first);

        var replay = new DefaultHttpContext();
        replay.Request.QueryString = new QueryString($"?code={Uri.EscapeDataString("success:cal@example.com")}&state={Uri.EscapeDataString(state)}");
        var result = await services.Calendar.HandleConnectCallbackAsync(replay);

        result.Should().NotBeOfType<Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult>();
        context.Set<SqlOSCalendarConnection>().Count().Should().Be(1);
    }

    [TestMethod]
    public async Task GetAccessToken_ConnectionOnly_ReturnsTokenWithoutStoringEvents()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var connectionId = await ConnectGoogleAsync(services, context, SqlOSCalendarIntegrationMode.ConnectionOnly);

        var token = await services.Calendar.GetAccessTokenAsync(connectionId);

        token.AccessToken.Should().Be("google-access|cal@example.com");
        token.ProviderType.Should().Be(SqlOSCalendarProviderType.Google);
        context.Set<SqlOSCalendarEvent>().Count().Should().Be(0);
        context.Set<SqlOSCalendarSyncState>().Count().Should().Be(0);
    }

    [TestMethod]
    public async Task GetAccessToken_WrongOrganization_Throws()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var connectionId = await ConnectGoogleAsync(services, context, SqlOSCalendarIntegrationMode.ConnectionOnly);

        var act = () => services.Calendar.GetAccessTokenAsync(connectionId, forOrganizationId: "org_other");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [TestMethod]
    public async Task GetAccessToken_WrongUser_Throws()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var connectionId = await ConnectGoogleAsync(services, context, SqlOSCalendarIntegrationMode.ConnectionOnly);

        var act = () => services.Calendar.GetAccessTokenAsync(connectionId, forUserId: "usr_other");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [TestMethod]
    public async Task GetAccessToken_ExpiredToken_RefreshesAndRotatesRefreshToken()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var connectionId = await ConnectGoogleAsync(services, context, SqlOSCalendarIntegrationMode.ConnectionOnly);

        var connection = context.Set<SqlOSCalendarConnection>().Single(x => x.Id == connectionId);
        connection.AccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await context.SaveChangesAsync();

        var token = await services.Calendar.GetAccessTokenAsync(connectionId);

        token.AccessToken.Should().Be("google-access-refreshed|cal@example.com");
        services.Crypto.UnprotectSecret(connection.RefreshTokenEncrypted!)
            .Should().Be("google-refresh-rotated|cal@example.com");
        connection.Status.Should().Be(SqlOSCalendarConnectionStatus.Active);
    }

    [TestMethod]
    public async Task GetAccessToken_RevokedRefreshToken_MarksConnectionError()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var connectionId = await ConnectGoogleAsync(services, context, SqlOSCalendarIntegrationMode.ConnectionOnly);

        var connection = context.Set<SqlOSCalendarConnection>().Single(x => x.Id == connectionId);
        connection.AccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        connection.RefreshTokenEncrypted = services.Crypto.ProtectSecret("revoked-refresh");
        await context.SaveChangesAsync();

        var act = () => services.Calendar.GetAccessTokenAsync(connectionId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*revoked*");
        connection.Status.Should().Be(SqlOSCalendarConnectionStatus.Error);
        connection.LastError.Should().Contain("revoked");
        context.Set<SqlOSAuditEvent>().Any(x => x.EventType == "calendar.connection.refresh_failed").Should().BeTrue();
    }

    [TestMethod]
    public async Task ListProviderCalendars_Google_ReturnsCalendars()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var connectionId = await ConnectGoogleAsync(services, context, SqlOSCalendarIntegrationMode.ReadPull);

        var calendars = await services.Calendar.ListProviderCalendarsAsync(connectionId);

        calendars.Should().HaveCount(2);
        calendars.Single(x => x.IsPrimary).ProviderCalendarId.Should().Be("google-primary");
    }

    [TestMethod]
    public async Task Sync_ReadPull_ImportsNormalizedEventsAndCursor()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var connectionId = await ConnectGoogleAsync(services, context, SqlOSCalendarIntegrationMode.ReadPull);

        var result = await services.Sync.SyncConnectionAsync(connectionId);

        result.Errors.Should().BeEmpty();
        result.CalendarsSynced.Should().Be(1);
        result.EventsUpserted.Should().Be(2);

        var state = context.Set<SqlOSCalendarSyncState>().Single();
        state.ProviderCalendarId.Should().Be("google-primary");
        state.SyncCursor.Should().Be("google-sync-1");
        state.LastSyncStatus.Should().Be("ok");
        state.EventCount.Should().Be(2);

        var events = await services.Calendar.ListEventsAsync(
            connectionId,
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        events.Should().HaveCount(2);
        events.Select(x => x.ProviderEventId).Should().BeEquivalentTo(["google-evt-1", "google-evt-2"]);
        events.All(x => x.ShowAs == "busy").Should().BeTrue();

        var connection = context.Set<SqlOSCalendarConnection>().Single();
        connection.LastSyncAt.Should().NotBeNull();
        context.Set<SqlOSAuditEvent>().Any(x => x.EventType == "calendar.connection.synced").Should().BeTrue();
    }

    [TestMethod]
    public async Task Sync_SecondPassWithCursor_AppliesDeltaAndRemovesCancelled()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var connectionId = await ConnectGoogleAsync(services, context, SqlOSCalendarIntegrationMode.ReadPull);

        await services.Sync.SyncConnectionAsync(connectionId);
        var second = await services.Sync.SyncConnectionAsync(connectionId);

        second.Errors.Should().BeEmpty();
        second.EventsRemoved.Should().Be(1);

        var events = context.Set<SqlOSCalendarEvent>().ToList();
        events.Should().NotContain(x => x.ProviderEventId == "google-evt-1");
        events.Single(x => x.ProviderEventId == "google-evt-2").Subject.Should().Be("Standup (moved)");
        events.Should().Contain(x => x.ProviderEventId == "google-evt-3");
        context.Set<SqlOSCalendarSyncState>().Single().SyncCursor.Should().Be("google-sync-2");
    }

    [TestMethod]
    public async Task Sync_ExpiredCursor_FallsBackToFullWindow()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var connectionId = await ConnectGoogleAsync(services, context, SqlOSCalendarIntegrationMode.ReadPull);

        await services.Sync.SyncConnectionAsync(connectionId);
        var state = context.Set<SqlOSCalendarSyncState>().Single();
        state.SyncCursor = "expired";
        await context.SaveChangesAsync();

        var result = await services.Sync.SyncConnectionAsync(connectionId);

        result.Errors.Should().BeEmpty();
        context.Set<SqlOSCalendarSyncState>().Single().SyncCursor.Should().Be("google-sync-1");
    }

    [TestMethod]
    public async Task Sync_ConnectionOnly_Throws()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var connectionId = await ConnectGoogleAsync(services, context, SqlOSCalendarIntegrationMode.ConnectionOnly);

        var act = () => services.Sync.SyncConnectionAsync(connectionId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Connection-only*");
    }

    [TestMethod]
    public async Task ListEvents_ConnectionOnly_Throws()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var connectionId = await ConnectGoogleAsync(services, context, SqlOSCalendarIntegrationMode.ConnectionOnly);

        var act = () => services.Calendar.ListEventsAsync(connectionId, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*do not store events*");
    }

    [TestMethod]
    public async Task CreateEvent_TwoWay_PushesToProviderAndStoresLocalCopy()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var connectionId = await ConnectGoogleAsync(services, context, SqlOSCalendarIntegrationMode.TwoWay);

        var created = await services.Sync.CreateEventAsync(
            connectionId,
            "google-primary",
            new SqlOSCalendarEventDraft(
                "Client kickoff",
                new DateTime(2026, 7, 9, 9, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 9, 9, 30, 0, DateTimeKind.Utc)));

        created.ProviderEventId.Should().Be("google-created-1");
        var local = context.Set<SqlOSCalendarEvent>().Single();
        local.Origin.Should().Be("push");
        local.Subject.Should().Be("Client kickoff");
        context.Set<SqlOSAuditEvent>().Any(x => x.EventType == "calendar.event.created").Should().BeTrue();
    }

    [TestMethod]
    public async Task CreateEvent_ReadPull_Throws()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var connectionId = await ConnectGoogleAsync(services, context, SqlOSCalendarIntegrationMode.ReadPull);

        var act = () => services.Sync.CreateEventAsync(
            connectionId,
            "google-primary",
            new SqlOSCalendarEventDraft("Nope", DateTime.UtcNow, DateTime.UtcNow.AddHours(1)));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*two-way*");
    }

    [TestMethod]
    public async Task Sync_TwoWayConflict_DefaultPrefersProvider()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var connectionId = await ConnectGoogleAsync(services, context, SqlOSCalendarIntegrationMode.TwoWay);

        await services.Sync.SyncConnectionAsync(connectionId);
        await services.Sync.CreateEventAsync(
            connectionId,
            "google-primary",
            new SqlOSCalendarEventDraft(
                "Client kickoff",
                new DateTime(2026, 7, 9, 9, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 9, 9, 30, 0, DateTimeKind.Utc)));

        await services.Sync.SyncConnectionAsync(connectionId);

        context.Set<SqlOSCalendarEvent>()
            .Single(x => x.ProviderEventId == "google-created-1")
            .Subject.Should().Be("Modified remotely");
    }

    [TestMethod]
    public async Task Sync_TwoWayConflict_CallbackCanPreferLocal()
    {
        using var context = CreateContext();
        var conflicts = new List<SqlOSCalendarConflictContext>();
        var services = CreateServices(context, options => options.Calendar.OnTwoWayConflictAsync = (conflict, _) =>
        {
            conflicts.Add(conflict);
            return Task.FromResult(SqlOSCalendarConflictDecision.PreferLocal);
        });
        var connectionId = await ConnectGoogleAsync(services, context, SqlOSCalendarIntegrationMode.TwoWay);

        await services.Sync.SyncConnectionAsync(connectionId);
        await services.Sync.CreateEventAsync(
            connectionId,
            "google-primary",
            new SqlOSCalendarEventDraft(
                "Client kickoff",
                new DateTime(2026, 7, 9, 9, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 9, 9, 30, 0, DateTimeKind.Utc)));

        await services.Sync.SyncConnectionAsync(connectionId);

        conflicts.Should().HaveCount(1);
        conflicts[0].Remote.Subject.Should().Be("Modified remotely");
        context.Set<SqlOSCalendarEvent>()
            .Single(x => x.ProviderEventId == "google-created-1")
            .Subject.Should().Be("Client kickoff");
    }

    [TestMethod]
    public async Task Disconnect_ClearsTokensAndBlocksAccess()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var connectionId = await ConnectGoogleAsync(services, context, SqlOSCalendarIntegrationMode.ConnectionOnly);

        var summary = await services.Calendar.DisconnectAsync(connectionId, "user_requested");

        summary.Status.Should().Be(nameof(SqlOSCalendarConnectionStatus.Revoked));
        var connection = context.Set<SqlOSCalendarConnection>().Single();
        connection.AccessTokenEncrypted.Should().BeNull();
        connection.RefreshTokenEncrypted.Should().BeNull();
        connection.RevokedAt.Should().NotBeNull();
        context.Set<SqlOSAuditEvent>().Any(x => x.EventType == "calendar.connection.disconnected").Should().BeTrue();

        var act = () => services.Calendar.GetAccessTokenAsync(connectionId);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*disconnected*");
    }

    [TestMethod]
    public async Task Microsoft_ConnectAndSync_MirrorsGoogle()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var organization = await services.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Acme", "acme"));
        var oidc = await CreateMicrosoftConnectionAsync(services.Admin);

        var completion = await services.Calendar.CompleteConnectAsync(
            new CalendarConnectRequestPayload(
                oidc.Id,
                SqlOSCalendarIntegrationMode.ReadPull,
                null,
                organization.Id,
                null,
                ["openid", "email", "offline_access", "Calendars.Read"],
                ReturnUri,
                "verifier",
                "https://tests.example.local/sqlos/auth/calendar/callback",
                "https://login.microsoftonline.com/common/oauth2/v2.0/token"),
            "success:cal-ms@example.com");

        completion.OrganizationId.Should().Be(organization.Id);
        completion.ProviderAccountEmail.Should().Be("cal-ms@example.com");

        var calendars = await services.Calendar.ListProviderCalendarsAsync(completion.CalendarConnectionId);
        calendars.Single(x => x.IsPrimary).ProviderCalendarId.Should().Be("graph-default");

        var first = await services.Sync.SyncConnectionAsync(completion.CalendarConnectionId);
        first.Errors.Should().BeEmpty();
        first.EventsUpserted.Should().Be(2);

        var second = await services.Sync.SyncConnectionAsync(completion.CalendarConnectionId);
        second.Errors.Should().BeEmpty();
        second.EventsRemoved.Should().Be(1);

        var events = context.Set<SqlOSCalendarEvent>().ToList();
        events.Should().NotContain(x => x.ProviderEventId == "graph-evt-1");
        events.Single(x => x.ProviderEventId == "graph-evt-2").Subject.Should().Be("Design review (moved)");
    }

    [TestMethod]
    public async Task CreateEvent_MicrosoftTwoWay_PushesToGraph()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        var organization = await services.Admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Acme", "acme-ms"));
        var oidc = await CreateMicrosoftConnectionAsync(services.Admin);
        var completion = await services.Calendar.CompleteConnectAsync(
            new CalendarConnectRequestPayload(
                oidc.Id,
                SqlOSCalendarIntegrationMode.TwoWay,
                null,
                organization.Id,
                null,
                ["openid", "email", "offline_access", "Calendars.ReadWrite"],
                ReturnUri,
                "verifier",
                "https://tests.example.local/sqlos/auth/calendar/callback",
                "https://login.microsoftonline.com/common/oauth2/v2.0/token"),
            "success:cal-ms@example.com");

        var created = await services.Sync.CreateEventAsync(
            completion.CalendarConnectionId,
            "graph-default",
            new SqlOSCalendarEventDraft(
                "Board meeting",
                new DateTime(2026, 7, 10, 14, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 10, 15, 0, 0, DateTimeKind.Utc),
                Location: "HQ",
                Description: "Quarterly review"),
            forOrganizationId: organization.Id);

        created.ProviderEventId.Should().Be("graph-created-1");
        created.Subject.Should().Be("Board meeting");
        context.Set<SqlOSCalendarEvent>().Single().Origin.Should().Be("push");
    }

    [TestMethod]
    public async Task AdminSurface_ListsConnectionsAndSummary()
    {
        using var context = CreateContext();
        var services = CreateServices(context);
        await ConnectGoogleAsync(services, context, SqlOSCalendarIntegrationMode.ReadPull);

        var summary = await services.Calendar.GetAdminSummaryAsync();
        var list = await services.Calendar.GetAdminConnectionsAsync();

        summary.Should().NotBeNull();
        list.Should().NotBeNull();
        var listJson = System.Text.Json.JsonSerializer.Serialize(list);
        listJson.Should().Contain("\"TotalCount\":1");
        listJson.Should().NotContain("google-access|", "admin projections must never expose raw tokens");
        listJson.Should().NotContain("dp:", "admin projections must never expose encrypted token material");
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

    private static async Task<string> ConnectGoogleAsync(
        CalendarTestServices services,
        TestSqlOSInMemoryDbContext context,
        SqlOSCalendarIntegrationMode mode)
    {
        var user = await services.Admin.CreateUserAsync(new SqlOSCreateUserRequest("Cal User", $"cal-{Guid.NewGuid():N}@example.com", null));
        var oidc = await CreateGoogleConnectionAsync(services.Admin);
        var completion = await services.Calendar.CompleteConnectAsync(
            GooglePayload(oidc.Id, mode, userId: user.Id),
            "success:cal@example.com");
        return completion.CalendarConnectionId;
    }

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

    private sealed record CalendarTestServices(
        SqlOSAdminService Admin,
        SqlOSCryptoService Crypto,
        SqlOSCalendarService Calendar,
        SqlOSCalendarSyncService Sync);

    private static CalendarTestServices CreateServices(
        TestSqlOSInMemoryDbContext context,
        Action<SqlOSOptions>? configure = null)
    {
        var sqlosOptions = new SqlOSOptions();
        sqlosOptions.AuthServer.PublicOrigin = "https://tests.example.local";
        configure?.Invoke(sqlosOptions);

        var authOptions = Options.Create(sqlosOptions.AuthServer);
        var crypto = new SqlOSCryptoService(context, authOptions, new EphemeralDataProtectionProvider());
        var admin = new SqlOSAdminService(context, authOptions, crypto);
        var httpFactory = new FakeCalendarProviderHttpClientFactory();
        var adapters = new ISqlOSCalendarProviderAdapter[]
        {
            new SqlOSGoogleCalendarAdapter(httpFactory),
            new SqlOSMicrosoftGraphCalendarAdapter(httpFactory)
        };
        var calendar = new SqlOSCalendarService(
            context,
            admin,
            crypto,
            httpFactory,
            adapters,
            Options.Create(sqlosOptions),
            NullLogger<SqlOSCalendarService>.Instance);
        var sync = new SqlOSCalendarSyncService(
            context,
            admin,
            crypto,
            calendar,
            Options.Create(sqlosOptions),
            NullLogger<SqlOSCalendarSyncService>.Instance);
        return new CalendarTestServices(admin, crypto, calendar, sync);
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }
}

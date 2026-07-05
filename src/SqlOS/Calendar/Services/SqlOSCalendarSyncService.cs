using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Services;
using SqlOS.Calendar.Configuration;
using SqlOS.Calendar.Contracts;
using SqlOS.Calendar.Models;
using SqlOS.Configuration;

namespace SqlOS.Calendar.Services;

/// <summary>
/// Read-pull and two-way sync engine. Pulls provider events into the normalized
/// <see cref="SqlOSCalendarEvent"/> table, keeps per-calendar incremental cursors, and
/// delegates two-way conflicts to the app callback configured on
/// <see cref="SqlOSCalendarOptions.OnTwoWayConflictAsync"/>.
/// </summary>
public sealed class SqlOSCalendarSyncService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSCalendarService _calendarService;
    private readonly SqlOSCalendarOptions _options;
    private readonly ILogger<SqlOSCalendarSyncService> _logger;

    public SqlOSCalendarSyncService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSCryptoService cryptoService,
        SqlOSCalendarService calendarService,
        IOptions<SqlOSOptions> options,
        ILogger<SqlOSCalendarSyncService> logger)
    {
        _context = context;
        _adminService = adminService;
        _cryptoService = cryptoService;
        _calendarService = calendarService;
        _options = options.Value.Calendar;
        _logger = logger;
    }

    /// <summary>
    /// Synchronizes one read-pull or two-way connection. When the connection has no explicit
    /// calendar selection yet, the provider's primary/default calendar is enrolled first.
    /// </summary>
    public async Task<SqlOSCalendarSyncResult> SyncConnectionAsync(
        string calendarConnectionId,
        CancellationToken cancellationToken = default)
    {
        var connection = await _calendarService.RequireConnectionAsync(
            calendarConnectionId,
            forUserId: null,
            forOrganizationId: null,
            includeRevoked: false,
            cancellationToken);

        if (connection.Mode == SqlOSCalendarIntegrationMode.ConnectionOnly)
        {
            throw new InvalidOperationException("Connection-only calendar connections do not synchronize events.");
        }

        var errors = new List<string>();
        var eventsUpserted = 0;
        var eventsRemoved = 0;
        var calendarsSynced = 0;

        try
        {
            var accessToken = await _calendarService.EnsureFreshAccessTokenAsync(connection, forceRefresh: false, cancellationToken);
            var adapter = _calendarService.RequireAdapter(connection.ProviderType);

            var syncStates = await _context.Set<SqlOSCalendarSyncState>()
                .Where(x => x.CalendarConnectionId == connection.Id && x.IsSyncEnabled)
                .ToListAsync(cancellationToken);

            if (syncStates.Count == 0)
            {
                var calendars = await adapter.ListCalendarsAsync(accessToken, cancellationToken);
                var primary = calendars.FirstOrDefault(x => x.IsPrimary) ?? calendars.FirstOrDefault();
                if (primary == null)
                {
                    throw new InvalidOperationException("The provider account does not expose any calendars to synchronize.");
                }

                var now = DateTime.UtcNow;
                var state = new SqlOSCalendarSyncState
                {
                    Id = _cryptoService.GenerateId("csy"),
                    CalendarConnectionId = connection.Id,
                    ProviderCalendarId = primary.ProviderCalendarId,
                    DisplayName = primary.DisplayName,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _context.Set<SqlOSCalendarSyncState>().Add(state);
                await _context.SaveChangesAsync(cancellationToken);
                syncStates.Add(state);
            }

            foreach (var state in syncStates)
            {
                try
                {
                    var (upserted, removed) = await SyncCalendarAsync(connection, adapter, accessToken, state, cancellationToken);
                    eventsUpserted += upserted;
                    eventsRemoved += removed;
                    calendarsSynced++;
                }
                catch (InvalidOperationException ex)
                {
                    errors.Add($"{state.ProviderCalendarId}: {ex.Message}");
                    state.LastSyncStatus = "error";
                    state.LastSyncError = Truncate(ex.Message, 1000);
                    state.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync(cancellationToken);
                }
            }

            var finishedAt = DateTime.UtcNow;
            connection.LastSyncAt = finishedAt;
            if (errors.Count == 0)
            {
                connection.Status = SqlOSCalendarConnectionStatus.Active;
                connection.LastError = null;
                connection.LastErrorAt = null;
            }
            else
            {
                connection.Status = SqlOSCalendarConnectionStatus.Error;
                connection.LastError = Truncate(string.Join("; ", errors), 1000);
                connection.LastErrorAt = finishedAt;
            }

            connection.UpdatedAt = finishedAt;
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            var now = DateTime.UtcNow;
            errors.Add(ex.Message);
            connection.Status = SqlOSCalendarConnectionStatus.Error;
            connection.LastError = Truncate(ex.Message, 1000);
            connection.LastErrorAt = now;
            connection.UpdatedAt = now;
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogWarning(ex, "Calendar sync failed for connection {ConnectionId}.", connection.Id);
        }

        var result = new SqlOSCalendarSyncResult(connection.Id, calendarsSynced, eventsUpserted, eventsRemoved, errors);

        await _adminService.RecordAuditAsync(
            errors.Count == 0 ? "calendar.connection.synced" : "calendar.sync.failed",
            "calendar_connection",
            connection.Id,
            userId: connection.UserId,
            organizationId: connection.OrganizationId,
            data: new
            {
                calendarsSynced,
                eventsUpserted,
                eventsRemoved,
                errors
            },
            cancellationToken: cancellationToken);

        return result;
    }

    /// <summary>
    /// Creates an event on the provider calendar (two-way mode) and stores the local copy
    /// with <c>Origin = "push"</c> so later pulls can detect conflicts.
    /// </summary>
    public async Task<SqlOSCalendarEventSnapshot> CreateEventAsync(
        string calendarConnectionId,
        string providerCalendarId,
        SqlOSCalendarEventDraft draft,
        string? forUserId = null,
        string? forOrganizationId = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await _calendarService.RequireConnectionAsync(
            calendarConnectionId, forUserId, forOrganizationId, includeRevoked: false, cancellationToken);

        if (connection.Mode != SqlOSCalendarIntegrationMode.TwoWay)
        {
            throw new InvalidOperationException("Only two-way calendar connections can create provider events through SqlOS.");
        }

        if (draft.EndsAtUtc <= draft.StartsAtUtc)
        {
            throw new InvalidOperationException("The event end time must be after its start time.");
        }

        var accessToken = await _calendarService.EnsureFreshAccessTokenAsync(connection, forceRefresh: false, cancellationToken);
        var adapter = _calendarService.RequireAdapter(connection.ProviderType);
        var created = await adapter.CreateEventAsync(accessToken, providerCalendarId, draft, cancellationToken);

        var now = DateTime.UtcNow;
        _context.Set<SqlOSCalendarEvent>().Add(new SqlOSCalendarEvent
        {
            Id = _cryptoService.GenerateId("cev"),
            CalendarConnectionId = connection.Id,
            ProviderCalendarId = providerCalendarId,
            ProviderEventId = created.ProviderEventId,
            Subject = created.Subject,
            StartsAtUtc = created.StartsAtUtc,
            EndsAtUtc = created.EndsAtUtc,
            IsAllDay = created.IsAllDay,
            ShowAs = created.ShowAs,
            Status = created.Status,
            Location = created.Location,
            Origin = "push",
            CreatedAt = now,
            UpdatedAt = now
        });
        await _context.SaveChangesAsync(cancellationToken);

        await _adminService.RecordAuditAsync(
            "calendar.event.created",
            "calendar_connection",
            connection.Id,
            userId: connection.UserId,
            organizationId: connection.OrganizationId,
            data: new
            {
                providerCalendarId,
                providerEventId = created.ProviderEventId
            },
            cancellationToken: cancellationToken);

        return created;
    }

    /// <summary>Synchronizes connections that are due, oldest first. Used by the background scheduler.</summary>
    public async Task<int> SyncDueConnectionsAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - _options.SyncScheduler.SyncEvery;
        var due = await _context.Set<SqlOSCalendarConnection>()
            .Where(x => x.RevokedAt == null
                && x.Mode != SqlOSCalendarIntegrationMode.ConnectionOnly
                && (x.LastSyncAt == null || x.LastSyncAt < cutoff))
            .OrderBy(x => x.LastSyncAt ?? DateTime.MinValue)
            .Take(_options.SyncScheduler.MaxConnectionsPerRun)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        foreach (var connectionId in due)
        {
            try
            {
                await SyncConnectionAsync(connectionId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled calendar sync failed for connection {ConnectionId}.", connectionId);
            }
        }

        return due.Count;
    }

    private async Task<(int Upserted, int Removed)> SyncCalendarAsync(
        SqlOSCalendarConnection connection,
        Interfaces.ISqlOSCalendarProviderAdapter adapter,
        string accessToken,
        SqlOSCalendarSyncState state,
        CancellationToken cancellationToken)
    {
        var windowStart = DateTime.UtcNow.AddDays(-_options.SyncWindowPastDays);
        var windowEnd = DateTime.UtcNow.AddDays(_options.SyncWindowFutureDays);
        state.LastSyncStartedAt = DateTime.UtcNow;

        SqlOSCalendarEventPage page;
        try
        {
            page = await adapter.ListEventsAsync(accessToken, state.ProviderCalendarId, windowStart, windowEnd, state.SyncCursor, cancellationToken);
        }
        catch (InvalidOperationException) when (!string.IsNullOrWhiteSpace(state.SyncCursor))
        {
            // Incremental cursors expire (e.g. Google 410 GONE). Fall back to a full window pull.
            state.SyncCursor = null;
            page = await adapter.ListEventsAsync(accessToken, state.ProviderCalendarId, windowStart, windowEnd, syncCursor: null, cancellationToken);
        }

        var existing = await _context.Set<SqlOSCalendarEvent>()
            .Where(x => x.CalendarConnectionId == connection.Id && x.ProviderCalendarId == state.ProviderCalendarId)
            .ToListAsync(cancellationToken);
        var existingByProviderId = existing.ToDictionary(x => x.ProviderEventId, StringComparer.Ordinal);

        var upserted = 0;
        var removed = 0;
        var now = DateTime.UtcNow;

        foreach (var snapshot in page.Events)
        {
            existingByProviderId.TryGetValue(snapshot.ProviderEventId, out var local);

            if (string.Equals(snapshot.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                if (local != null)
                {
                    _context.Set<SqlOSCalendarEvent>().Remove(local);
                    existingByProviderId.Remove(snapshot.ProviderEventId);
                    removed++;
                }

                continue;
            }

            if (local == null)
            {
                var created = new SqlOSCalendarEvent
                {
                    Id = _cryptoService.GenerateId("cev"),
                    CalendarConnectionId = connection.Id,
                    ProviderCalendarId = state.ProviderCalendarId,
                    ProviderEventId = snapshot.ProviderEventId,
                    Origin = "pull",
                    CreatedAt = now
                };
                ApplySnapshot(created, snapshot, now);
                _context.Set<SqlOSCalendarEvent>().Add(created);
                existingByProviderId[snapshot.ProviderEventId] = created;
                upserted++;
                continue;
            }

            if (SnapshotMatches(local, snapshot))
            {
                continue;
            }

            if (connection.Mode == SqlOSCalendarIntegrationMode.TwoWay
                && string.Equals(local.Origin, "push", StringComparison.Ordinal))
            {
                var decision = await ResolveConflictAsync(connection, state, local, snapshot, cancellationToken);
                if (decision == SqlOSCalendarConflictDecision.PreferLocal)
                {
                    continue;
                }
            }

            ApplySnapshot(local, snapshot, now);
            upserted++;
        }

        state.SyncCursor = page.NextSyncCursor ?? state.SyncCursor;
        state.LastSyncCompletedAt = DateTime.UtcNow;
        state.LastSyncStatus = "ok";
        state.LastSyncError = null;
        state.EventCount = existingByProviderId.Count;
        state.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return (upserted, removed);
    }

    private async Task<SqlOSCalendarConflictDecision> ResolveConflictAsync(
        SqlOSCalendarConnection connection,
        SqlOSCalendarSyncState state,
        SqlOSCalendarEvent local,
        SqlOSCalendarEventSnapshot remote,
        CancellationToken cancellationToken)
    {
        if (_options.OnTwoWayConflictAsync == null)
        {
            return SqlOSCalendarConflictDecision.PreferProvider;
        }

        var context = new SqlOSCalendarConflictContext(
            connection.Id,
            state.ProviderCalendarId,
            remote.ProviderEventId,
            new SqlOSCalendarEventSnapshot(
                local.ProviderEventId,
                local.Subject,
                local.StartsAtUtc,
                local.EndsAtUtc,
                local.IsAllDay,
                local.ShowAs,
                local.Status,
                local.Location),
            remote);

        try
        {
            return await _options.OnTwoWayConflictAsync(context, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Two-way conflict callback failed for connection {ConnectionId}; preferring the provider version.", connection.Id);
            return SqlOSCalendarConflictDecision.PreferProvider;
        }
    }

    private static void ApplySnapshot(SqlOSCalendarEvent target, SqlOSCalendarEventSnapshot snapshot, DateTime now)
    {
        target.Subject = snapshot.Subject;
        target.StartsAtUtc = snapshot.StartsAtUtc;
        target.EndsAtUtc = snapshot.EndsAtUtc;
        target.IsAllDay = snapshot.IsAllDay;
        target.ShowAs = snapshot.ShowAs;
        target.Status = snapshot.Status;
        target.Location = snapshot.Location;
        target.UpdatedAt = now;
    }

    private static bool SnapshotMatches(SqlOSCalendarEvent local, SqlOSCalendarEventSnapshot snapshot)
        => string.Equals(local.Subject, snapshot.Subject, StringComparison.Ordinal)
           && local.StartsAtUtc == snapshot.StartsAtUtc
           && local.EndsAtUtc == snapshot.EndsAtUtc
           && local.IsAllDay == snapshot.IsAllDay
           && string.Equals(local.ShowAs, snapshot.ShowAs, StringComparison.Ordinal)
           && string.Equals(local.Status, snapshot.Status, StringComparison.Ordinal)
           && string.Equals(local.Location, snapshot.Location, StringComparison.Ordinal);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}

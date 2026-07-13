using SqlOS.Calendar.Contracts;

namespace SqlOS.Calendar.Configuration;

/// <summary>
/// Options for SqlOS calendar integration. Calendar connections reuse the consumer's
/// seeded Google/Microsoft OIDC connections for OAuth app credentials, so no additional
/// provider registration is required beyond granting the documented calendar scopes.
/// </summary>
public sealed class SqlOSCalendarOptions
{
    /// <summary>Default Google scopes for read-only calendar access.</summary>
    public static readonly IReadOnlyList<string> DefaultGoogleReadScopes =
        ["https://www.googleapis.com/auth/calendar.readonly"];

    /// <summary>Default Google scopes when two-way sync needs event writes.</summary>
    public static readonly IReadOnlyList<string> DefaultGoogleWriteScopes =
        ["https://www.googleapis.com/auth/calendar.events"];

    /// <summary>Default Microsoft Graph scopes for read-only calendar access.</summary>
    public static readonly IReadOnlyList<string> DefaultMicrosoftReadScopes =
        ["offline_access", "Calendars.Read"];

    /// <summary>Default Microsoft Graph scopes when two-way sync needs event writes.</summary>
    public static readonly IReadOnlyList<string> DefaultMicrosoftWriteScopes =
        ["offline_access", "Calendars.ReadWrite"];

    /// <summary>
    /// Enables the hosted calendar connect/callback endpoints and admin API. Connections can
    /// still be managed through <see cref="Services.SqlOSCalendarService"/> when disabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long a pending calendar connect session stays valid.</summary>
    public TimeSpan ConnectSessionLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Access tokens are refreshed when they expire within this window.</summary>
    public TimeSpan AccessTokenRefreshSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How far back read-pull sync imports events.</summary>
    public int SyncWindowPastDays { get; set; } = 30;

    /// <summary>How far ahead read-pull sync imports events.</summary>
    public int SyncWindowFutureDays { get; set; } = 90;

    public SqlOSCalendarSyncSchedulerOptions SyncScheduler { get; } = new();

    /// <summary>
    /// Two-way conflict policy callback. Invoked when a provider change collides with a local
    /// copy that was created or modified through SqlOS. When null, provider changes win
    /// (<see cref="SqlOSCalendarConflictDecision.PreferProvider"/>).
    /// </summary>
    public Func<SqlOSCalendarConflictContext, CancellationToken, Task<SqlOSCalendarConflictDecision>>? OnTwoWayConflictAsync { get; set; }

    public SqlOSCalendarOptions ConfigureSyncScheduler(Action<SqlOSCalendarSyncSchedulerOptions> configure)
    {
        configure(SyncScheduler);
        return this;
    }
}

/// <summary>Background scheduler options for read-pull and two-way connections.</summary>
public sealed class SqlOSCalendarSyncSchedulerOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Delay before the first scheduler pass, letting the bootstrapper finish.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the scheduler scans for connections that are due for a sync.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>A connection is due when its last sync is older than this.</summary>
    public TimeSpan SyncEvery { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Upper bound of connections synced per scheduler pass.</summary>
    public int MaxConnectionsPerRun { get; set; } = 25;
}

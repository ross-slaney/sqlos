using SqlOS.AuthServer.Models;

namespace SqlOS.Calendar.Models;

public enum SqlOSCalendarProviderType
{
    Google = 0,
    Microsoft = 1
}

/// <summary>
/// How a consuming application uses a calendar connection. The mode is chosen by
/// the app when the connection is created and controls what SqlOS persists.
/// </summary>
public enum SqlOSCalendarIntegrationMode
{
    /// <summary>SqlOS stores and refreshes OAuth tokens; the app calls provider APIs directly.</summary>
    ConnectionOnly = 0,

    /// <summary>SqlOS imports events/busy blocks from the provider on a schedule.</summary>
    ReadPull = 1,

    /// <summary>SqlOS pushes app-created events and pulls provider changes, delegating conflicts to the app.</summary>
    TwoWay = 2
}

public enum SqlOSCalendarConnectionStatus
{
    Active = 0,

    /// <summary>The last token refresh or sync failed; see <see cref="SqlOSCalendarConnection.LastError"/>.</summary>
    Error = 1,

    /// <summary>The connection was disconnected; tokens have been cleared.</summary>
    Revoked = 2
}

/// <summary>
/// A calendar resource connection for a user or an organization. The provider OAuth app
/// (client id/secret, tenant) is reused from the referenced <see cref="SqlOSOidcConnection"/>,
/// so consumers register Google/Microsoft apps exactly once. Calendar connections are
/// deliberately separate from sign-in: login flows never request calendar scopes.
/// </summary>
public sealed class SqlOSCalendarConnection
{
    public string Id { get; set; } = string.Empty;
    public SqlOSCalendarProviderType ProviderType { get; set; }
    public SqlOSCalendarIntegrationMode Mode { get; set; } = SqlOSCalendarIntegrationMode.ConnectionOnly;
    public SqlOSCalendarConnectionStatus Status { get; set; } = SqlOSCalendarConnectionStatus.Active;

    /// <summary>The social/OIDC connection whose OAuth app credentials are used for calendar consent.</summary>
    public string OidcConnectionId { get; set; } = string.Empty;

    /// <summary>Owning user. Exactly one of <see cref="UserId"/> and <see cref="OrganizationId"/> is set.</summary>
    public string? UserId { get; set; }

    /// <summary>Owning organization. Exactly one of <see cref="UserId"/> and <see cref="OrganizationId"/> is set.</summary>
    public string? OrganizationId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The provider account (email) that granted calendar consent.</summary>
    public string? ProviderAccountEmail { get; set; }

    /// <summary>The provider subject (stable account id) that granted calendar consent.</summary>
    public string? ProviderAccountSubject { get; set; }

    /// <summary>Granted OAuth scopes as a JSON string array.</summary>
    public string ScopesJson { get; set; } = "[]";

    /// <summary>Access token protected via <see cref="AuthServer.Services.SqlOSCryptoService.ProtectSecret"/>.</summary>
    public string? AccessTokenEncrypted { get; set; }

    /// <summary>Refresh token protected via <see cref="AuthServer.Services.SqlOSCryptoService.ProtectSecret"/>.</summary>
    public string? RefreshTokenEncrypted { get; set; }

    public DateTime? AccessTokenExpiresAt { get; set; }

    public DateTime? LastSyncAt { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }

    public SqlOSUser? User { get; set; }
    public SqlOSOrganization? Organization { get; set; }
    public SqlOSOidcConnection? OidcConnection { get; set; }
    public ICollection<SqlOSCalendarSyncState> SyncStates { get; set; } = new List<SqlOSCalendarSyncState>();
    public ICollection<SqlOSCalendarEvent> Events { get; set; } = new List<SqlOSCalendarEvent>();
}

/// <summary>
/// Per-provider-calendar sync cursor and health for <see cref="SqlOSCalendarIntegrationMode.ReadPull"/>
/// and <see cref="SqlOSCalendarIntegrationMode.TwoWay"/> connections.
/// </summary>
public sealed class SqlOSCalendarSyncState
{
    public string Id { get; set; } = string.Empty;
    public string CalendarConnectionId { get; set; } = string.Empty;

    /// <summary>Provider calendar id (Google calendarId / Microsoft Graph calendar id).</summary>
    public string ProviderCalendarId { get; set; } = string.Empty;

    public string? DisplayName { get; set; }
    public bool IsSyncEnabled { get; set; } = true;

    /// <summary>Provider incremental cursor (Google syncToken / Microsoft delta token), when available.</summary>
    public string? SyncCursor { get; set; }

    public DateTime? LastSyncStartedAt { get; set; }
    public DateTime? LastSyncCompletedAt { get; set; }
    public string? LastSyncStatus { get; set; }
    public string? LastSyncError { get; set; }
    public int EventCount { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public SqlOSCalendarConnection? CalendarConnection { get; set; }
}

/// <summary>
/// A normalized event copy imported from a provider calendar. Only populated for
/// <see cref="SqlOSCalendarIntegrationMode.ReadPull"/> and <see cref="SqlOSCalendarIntegrationMode.TwoWay"/>
/// connections; <see cref="SqlOSCalendarIntegrationMode.ConnectionOnly"/> never persists event copies.
/// </summary>
public sealed class SqlOSCalendarEvent
{
    public string Id { get; set; } = string.Empty;
    public string CalendarConnectionId { get; set; } = string.Empty;
    public string ProviderCalendarId { get; set; } = string.Empty;
    public string ProviderEventId { get; set; } = string.Empty;

    public string? Subject { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public bool IsAllDay { get; set; }

    /// <summary>Availability classification: busy, free, tentative, or oof.</summary>
    public string ShowAs { get; set; } = "busy";

    /// <summary>Provider event status: confirmed, tentative, or cancelled.</summary>
    public string Status { get; set; } = "confirmed";

    public string? Location { get; set; }

    /// <summary>Where the copy originated: "pull" (provider import) or "push" (created through SqlOS two-way).</summary>
    public string Origin { get; set; } = "pull";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public SqlOSCalendarConnection? CalendarConnection { get; set; }
}

using SqlOS.Calendar.Models;

namespace SqlOS.Calendar.Contracts;

/// <summary>
/// Starts a calendar connect flow. The provider OAuth app credentials come from the
/// referenced social/OIDC connection; exactly one of <paramref name="UserId"/> and
/// <paramref name="OrganizationId"/> must be provided.
/// </summary>
public sealed record SqlOSStartCalendarConnectRequest(
    string OidcConnectionId,
    SqlOSCalendarIntegrationMode Mode,
    string ReturnUri,
    string? UserId = null,
    string? OrganizationId = null,
    string? DisplayName = null,
    IReadOnlyList<string>? Scopes = null,
    string? LoginHintEmail = null);

public sealed record SqlOSStartCalendarConnectResult(
    string AuthorizationUrl,
    string OidcConnectionId,
    SqlOSCalendarProviderType ProviderType,
    SqlOSCalendarIntegrationMode Mode);

public sealed record SqlOSCompleteCalendarConnectResult(
    string CalendarConnectionId,
    SqlOSCalendarProviderType ProviderType,
    SqlOSCalendarIntegrationMode Mode,
    string? UserId,
    string? OrganizationId,
    string? ProviderAccountEmail,
    string ReturnUri);

public sealed record SqlOSCalendarConnectionSummary(
    string Id,
    string ProviderType,
    string Mode,
    string Status,
    string? UserId,
    string? OrganizationId,
    string DisplayName,
    string? ProviderAccountEmail,
    IReadOnlyList<string> Scopes,
    DateTime? AccessTokenExpiresAt,
    bool HasRefreshToken,
    DateTime? LastSyncAt,
    string? LastError,
    DateTime? LastErrorAt,
    DateTime CreatedAt,
    DateTime? RevokedAt);

/// <summary>
/// Short-lived provider access token returned to the authorized application in
/// <see cref="SqlOSCalendarIntegrationMode.ConnectionOnly"/> mode (and available in every
/// mode). SqlOS refreshes the token transparently when it is close to expiry.
/// </summary>
public sealed record SqlOSCalendarAccessTokenResult(
    string AccessToken,
    DateTime ExpiresAt,
    IReadOnlyList<string> Scopes,
    SqlOSCalendarProviderType ProviderType);

/// <summary>A calendar owned by the connected provider account.</summary>
public sealed record SqlOSCalendarSummary(
    string ProviderCalendarId,
    string DisplayName,
    bool IsPrimary,
    string? TimeZone);

/// <summary>A normalized provider event, independent of Google/Microsoft payload shapes.</summary>
public sealed record SqlOSCalendarEventSnapshot(
    string ProviderEventId,
    string? Subject,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    bool IsAllDay,
    string ShowAs,
    string Status,
    string? Location);

/// <summary>One page of provider events plus the provider's incremental cursor when offered.</summary>
public sealed record SqlOSCalendarEventPage(
    IReadOnlyList<SqlOSCalendarEventSnapshot> Events,
    string? NextSyncCursor);

/// <summary>A new event pushed to the provider through two-way sync.</summary>
public sealed record SqlOSCalendarEventDraft(
    string? Subject,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    bool IsAllDay = false,
    string? Location = null,
    string? Description = null);

/// <summary>Token payload returned by provider token endpoints.</summary>
public sealed record SqlOSCalendarTokenResult(
    string AccessToken,
    string? RefreshToken,
    DateTime ExpiresAt,
    IReadOnlyList<string> Scopes,
    string? IdToken = null);

public sealed record SqlOSCalendarSyncResult(
    string CalendarConnectionId,
    int CalendarsSynced,
    int EventsUpserted,
    int EventsRemoved,
    IReadOnlyList<string> Errors);

public enum SqlOSCalendarConflictDecision
{
    /// <summary>The provider version wins; the local copy is overwritten.</summary>
    PreferProvider = 0,

    /// <summary>The local copy wins; the provider change is ignored for this sync pass.</summary>
    PreferLocal = 1
}

/// <summary>
/// Context handed to the app's two-way conflict callback when a pulled provider event
/// differs from a local copy that originated through SqlOS.
/// </summary>
public sealed record SqlOSCalendarConflictContext(
    string CalendarConnectionId,
    string ProviderCalendarId,
    string ProviderEventId,
    SqlOSCalendarEventSnapshot Local,
    SqlOSCalendarEventSnapshot Remote);

using SqlOS.Calendar.Contracts;
using SqlOS.Calendar.Models;

namespace SqlOS.Calendar.Interfaces;

/// <summary>
/// Minimal provider client for calendar integration. Implementations call the provider's
/// REST APIs directly through <see cref="IHttpClientFactory"/> so tests can substitute a
/// fake HTTP handler, mirroring the OIDC login test strategy.
/// </summary>
public interface ISqlOSCalendarProviderAdapter
{
    SqlOSCalendarProviderType ProviderType { get; }

    /// <summary>Exchanges an authorization code for calendar tokens at the connection's token endpoint.</summary>
    Task<SqlOSCalendarTokenResult> ExchangeAuthorizationCodeAsync(
        SqlOSCalendarProviderContext context,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken = default);

    /// <summary>Exchanges a refresh token for a new access token.</summary>
    Task<SqlOSCalendarTokenResult> RefreshAccessTokenAsync(
        SqlOSCalendarProviderContext context,
        string refreshToken,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the calendars owned by the connected account.</summary>
    Task<IReadOnlyList<SqlOSCalendarSummary>> ListCalendarsAsync(
        string accessToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists events for one calendar inside the window. When <paramref name="syncCursor"/> is
    /// provided and the provider supports incremental sync, only changes are returned.
    /// </summary>
    Task<SqlOSCalendarEventPage> ListEventsAsync(
        string accessToken,
        string providerCalendarId,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        string? syncCursor,
        CancellationToken cancellationToken = default);

    /// <summary>Creates an event on the provider calendar (two-way mode).</summary>
    Task<SqlOSCalendarEventSnapshot> CreateEventAsync(
        string accessToken,
        string providerCalendarId,
        SqlOSCalendarEventDraft draft,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// OAuth client configuration resolved from the underlying social/OIDC connection.
/// </summary>
public sealed record SqlOSCalendarProviderContext(
    string ClientId,
    string ClientSecret,
    string TokenEndpoint);

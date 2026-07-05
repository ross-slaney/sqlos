using System.Text;
using System.Text.Json;
using SqlOS.Calendar.Contracts;
using SqlOS.Calendar.Interfaces;
using SqlOS.Calendar.Models;

namespace SqlOS.Calendar.Services;

/// <summary>
/// Minimal Google Calendar API v3 client. Uses raw REST calls so the SqlOS package stays
/// dependency-free and tests can fake the HTTP layer.
/// </summary>
public sealed class SqlOSGoogleCalendarAdapter : ISqlOSCalendarProviderAdapter
{
    internal const string CalendarApiBaseUrl = "https://www.googleapis.com/calendar/v3";

    private readonly IHttpClientFactory _httpClientFactory;

    public SqlOSGoogleCalendarAdapter(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public SqlOSCalendarProviderType ProviderType => SqlOSCalendarProviderType.Google;

    public async Task<SqlOSCalendarTokenResult> ExchangeAuthorizationCodeAsync(
        SqlOSCalendarProviderContext context,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken = default)
        => await SqlOSCalendarProviderHttp.PostTokenFormAsync(
            CreateClient(),
            context.TokenEndpoint,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = context.ClientId,
                ["client_secret"] = context.ClientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = codeVerifier
            },
            cancellationToken);

    public async Task<SqlOSCalendarTokenResult> RefreshAccessTokenAsync(
        SqlOSCalendarProviderContext context,
        string refreshToken,
        CancellationToken cancellationToken = default)
        => await SqlOSCalendarProviderHttp.PostTokenFormAsync(
            CreateClient(),
            context.TokenEndpoint,
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = context.ClientId,
                ["client_secret"] = context.ClientSecret,
                ["refresh_token"] = refreshToken
            },
            cancellationToken);

    public async Task<IReadOnlyList<SqlOSCalendarSummary>> ListCalendarsAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        using var payload = await SqlOSCalendarProviderHttp.GetJsonAsync(
            CreateClient(),
            $"{CalendarApiBaseUrl}/users/me/calendarList",
            accessToken,
            "The Google calendar list request failed.",
            cancellationToken);

        var calendars = new List<SqlOSCalendarSummary>();
        if (payload.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var id = SqlOSCalendarProviderHttp.GetString(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                calendars.Add(new SqlOSCalendarSummary(
                    id!,
                    SqlOSCalendarProviderHttp.GetString(item, "summary") ?? id!,
                    item.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True,
                    SqlOSCalendarProviderHttp.GetString(item, "timeZone")));
            }
        }

        return calendars;
    }

    public async Task<SqlOSCalendarEventPage> ListEventsAsync(
        string accessToken,
        string providerCalendarId,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        string? syncCursor,
        CancellationToken cancellationToken = default)
    {
        var url = new StringBuilder($"{CalendarApiBaseUrl}/calendars/{Uri.EscapeDataString(providerCalendarId)}/events?singleEvents=true");
        if (string.IsNullOrWhiteSpace(syncCursor))
        {
            url.Append("&timeMin=").Append(Uri.EscapeDataString(SqlOSCalendarProviderHttp.FormatUtc(windowStartUtc)));
            url.Append("&timeMax=").Append(Uri.EscapeDataString(SqlOSCalendarProviderHttp.FormatUtc(windowEndUtc)));
        }
        else
        {
            url.Append("&syncToken=").Append(Uri.EscapeDataString(syncCursor));
        }

        using var payload = await SqlOSCalendarProviderHttp.GetJsonAsync(
            CreateClient(),
            url.ToString(),
            accessToken,
            "The Google calendar events request failed.",
            cancellationToken);

        var events = new List<SqlOSCalendarEventSnapshot>();
        if (payload.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var snapshot = MapEvent(item);
                if (snapshot != null)
                {
                    events.Add(snapshot);
                }
            }
        }

        var nextCursor = SqlOSCalendarProviderHttp.GetString(payload.RootElement, "nextSyncToken");
        return new SqlOSCalendarEventPage(events, nextCursor);
    }

    public async Task<SqlOSCalendarEventSnapshot> CreateEventAsync(
        string accessToken,
        string providerCalendarId,
        SqlOSCalendarEventDraft draft,
        CancellationToken cancellationToken = default)
    {
        object body = draft.IsAllDay
            ? new
            {
                summary = draft.Subject,
                location = draft.Location,
                description = draft.Description,
                start = new { date = draft.StartsAtUtc.ToString("yyyy-MM-dd") },
                end = new { date = draft.EndsAtUtc.ToString("yyyy-MM-dd") }
            }
            : new
            {
                summary = draft.Subject,
                location = draft.Location,
                description = draft.Description,
                start = new { dateTime = SqlOSCalendarProviderHttp.FormatUtc(draft.StartsAtUtc), timeZone = "UTC" },
                end = new { dateTime = SqlOSCalendarProviderHttp.FormatUtc(draft.EndsAtUtc), timeZone = "UTC" }
            };

        using var payload = await SqlOSCalendarProviderHttp.PostJsonAsync(
            CreateClient(),
            $"{CalendarApiBaseUrl}/calendars/{Uri.EscapeDataString(providerCalendarId)}/events",
            accessToken,
            body,
            "The Google calendar event creation failed.",
            cancellationToken);

        return MapEvent(payload.RootElement)
            ?? throw new InvalidOperationException("Google did not return the created calendar event.");
    }

    private static SqlOSCalendarEventSnapshot? MapEvent(JsonElement item)
    {
        var id = SqlOSCalendarProviderHttp.GetString(item, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var status = SqlOSCalendarProviderHttp.GetString(item, "status") ?? "confirmed";
        var (startsAt, isAllDay) = ReadEventTime(item, "start");
        var (endsAt, _) = ReadEventTime(item, "end");
        if (startsAt == null || endsAt == null)
        {
            // Cancelled incremental entries omit times; surface them so sync can remove local copies.
            if (string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return new SqlOSCalendarEventSnapshot(id!, null, DateTime.MinValue, DateTime.MinValue, false, "free", "cancelled", null);
            }

            return null;
        }

        var transparency = SqlOSCalendarProviderHttp.GetString(item, "transparency");
        var showAs = string.Equals(transparency, "transparent", StringComparison.OrdinalIgnoreCase) ? "free" : "busy";
        if (string.Equals(status, "tentative", StringComparison.OrdinalIgnoreCase))
        {
            showAs = "tentative";
        }

        return new SqlOSCalendarEventSnapshot(
            id!,
            SqlOSCalendarProviderHttp.GetString(item, "summary"),
            startsAt.Value,
            endsAt.Value,
            isAllDay,
            showAs,
            status.ToLowerInvariant(),
            SqlOSCalendarProviderHttp.GetString(item, "location"));
    }

    private static (DateTime? Value, bool IsAllDay) ReadEventTime(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var timeElement) || timeElement.ValueKind != JsonValueKind.Object)
        {
            return (null, false);
        }

        var dateTime = SqlOSCalendarProviderHttp.GetString(timeElement, "dateTime");
        if (!string.IsNullOrWhiteSpace(dateTime) && DateTimeOffset.TryParse(dateTime, out var parsed))
        {
            return (parsed.UtcDateTime, false);
        }

        var date = SqlOSCalendarProviderHttp.GetString(timeElement, "date");
        if (!string.IsNullOrWhiteSpace(date) && DateTime.TryParse(date, out var parsedDate))
        {
            return (DateTime.SpecifyKind(parsedDate.Date, DateTimeKind.Utc), true);
        }

        return (null, false);
    }

    private HttpClient CreateClient() => _httpClientFactory.CreateClient(nameof(SqlOSGoogleCalendarAdapter));
}

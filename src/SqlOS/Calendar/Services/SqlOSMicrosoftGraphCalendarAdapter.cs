using System.Text;
using System.Text.Json;
using SqlOS.Calendar.Contracts;
using SqlOS.Calendar.Interfaces;
using SqlOS.Calendar.Models;

namespace SqlOS.Calendar.Services;

/// <summary>
/// Minimal Microsoft Graph calendar client (v1.0). Uses raw REST calls so the SqlOS package
/// stays dependency-free and tests can fake the HTTP layer.
/// </summary>
public sealed class SqlOSMicrosoftGraphCalendarAdapter : ISqlOSCalendarProviderAdapter
{
    internal const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

    private readonly IHttpClientFactory _httpClientFactory;

    public SqlOSMicrosoftGraphCalendarAdapter(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public SqlOSCalendarProviderType ProviderType => SqlOSCalendarProviderType.Microsoft;

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
            $"{GraphBaseUrl}/me/calendars",
            accessToken,
            "The Microsoft Graph calendar list request failed.",
            cancellationToken);

        var calendars = new List<SqlOSCalendarSummary>();
        if (payload.RootElement.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array)
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
                    SqlOSCalendarProviderHttp.GetString(item, "name") ?? id!,
                    item.TryGetProperty("isDefaultCalendar", out var isDefault) && isDefault.ValueKind == JsonValueKind.True,
                    TimeZone: null));
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
        // Graph delta links embed the original window; reuse them verbatim when present.
        var url = !string.IsNullOrWhiteSpace(syncCursor) && syncCursor.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? syncCursor
            : new StringBuilder($"{GraphBaseUrl}/me/calendars/{Uri.EscapeDataString(providerCalendarId)}/calendarView/delta")
                .Append("?startDateTime=").Append(Uri.EscapeDataString(SqlOSCalendarProviderHttp.FormatUtc(windowStartUtc)))
                .Append("&endDateTime=").Append(Uri.EscapeDataString(SqlOSCalendarProviderHttp.FormatUtc(windowEndUtc)))
                .ToString();

        var events = new List<SqlOSCalendarEventSnapshot>();
        string? nextCursor = null;

        // Follow @odata.nextLink pages until Graph hands back the @odata.deltaLink cursor.
        for (var page = 0; page < 25 && url != null; page++)
        {
            using var payload = await SqlOSCalendarProviderHttp.GetJsonAsync(
                CreateClient(),
                url,
                accessToken,
                "The Microsoft Graph calendar events request failed.",
                cancellationToken);

            if (payload.RootElement.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array)
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

            var nextLink = SqlOSCalendarProviderHttp.GetString(payload.RootElement, "@odata.nextLink");
            var deltaLink = SqlOSCalendarProviderHttp.GetString(payload.RootElement, "@odata.deltaLink");
            if (!string.IsNullOrWhiteSpace(deltaLink))
            {
                nextCursor = deltaLink;
            }

            url = string.IsNullOrWhiteSpace(nextLink) ? null : nextLink;
        }

        return new SqlOSCalendarEventPage(events, nextCursor);
    }

    public async Task<SqlOSCalendarEventSnapshot> CreateEventAsync(
        string accessToken,
        string providerCalendarId,
        SqlOSCalendarEventDraft draft,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            subject = draft.Subject,
            isAllDay = draft.IsAllDay,
            body = draft.Description == null ? null : new { contentType = "text", content = draft.Description },
            location = draft.Location == null ? null : new { displayName = draft.Location },
            start = new { dateTime = SqlOSCalendarProviderHttp.FormatUtc(draft.StartsAtUtc), timeZone = "UTC" },
            end = new { dateTime = SqlOSCalendarProviderHttp.FormatUtc(draft.EndsAtUtc), timeZone = "UTC" }
        };

        using var payload = await SqlOSCalendarProviderHttp.PostJsonAsync(
            CreateClient(),
            $"{GraphBaseUrl}/me/calendars/{Uri.EscapeDataString(providerCalendarId)}/events",
            accessToken,
            body,
            "The Microsoft Graph event creation failed.",
            cancellationToken);

        return MapEvent(payload.RootElement)
            ?? throw new InvalidOperationException("Microsoft Graph did not return the created calendar event.");
    }

    private static SqlOSCalendarEventSnapshot? MapEvent(JsonElement item)
    {
        var id = SqlOSCalendarProviderHttp.GetString(item, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        // Delta payloads mark deletions with an @removed annotation.
        if (item.TryGetProperty("@removed", out var removed) && removed.ValueKind != JsonValueKind.Null)
        {
            return new SqlOSCalendarEventSnapshot(id!, null, DateTime.MinValue, DateTime.MinValue, false, "free", "cancelled", null);
        }

        var startsAt = ReadEventTime(item, "start");
        var endsAt = ReadEventTime(item, "end");
        if (startsAt == null || endsAt == null)
        {
            return null;
        }

        var isCancelled = item.TryGetProperty("isCancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True;
        var showAs = (SqlOSCalendarProviderHttp.GetString(item, "showAs") ?? "busy").ToLowerInvariant();
        if (showAs is not ("busy" or "free" or "tentative" or "oof"))
        {
            showAs = "busy";
        }

        string? location = null;
        if (item.TryGetProperty("location", out var locationElement) && locationElement.ValueKind == JsonValueKind.Object)
        {
            location = SqlOSCalendarProviderHttp.GetString(locationElement, "displayName");
        }

        return new SqlOSCalendarEventSnapshot(
            id!,
            SqlOSCalendarProviderHttp.GetString(item, "subject"),
            startsAt.Value,
            endsAt.Value,
            item.TryGetProperty("isAllDay", out var allDay) && allDay.ValueKind == JsonValueKind.True,
            showAs,
            isCancelled ? "cancelled" : "confirmed",
            location);
    }

    private static DateTime? ReadEventTime(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var timeElement) || timeElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var dateTime = SqlOSCalendarProviderHttp.GetString(timeElement, "dateTime");
        if (string.IsNullOrWhiteSpace(dateTime))
        {
            return null;
        }

        if (!DateTime.TryParse(dateTime, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return null;
        }

        var timeZone = SqlOSCalendarProviderHttp.GetString(timeElement, "timeZone");
        if (!string.IsNullOrWhiteSpace(timeZone) && !string.Equals(timeZone, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZone!);
                parsed = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), tz);
            }
            catch (TimeZoneNotFoundException)
            {
                // Fall back to treating the value as UTC.
            }
        }

        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }

    private HttpClient CreateClient() => _httpClientFactory.CreateClient(nameof(SqlOSMicrosoftGraphCalendarAdapter));
}

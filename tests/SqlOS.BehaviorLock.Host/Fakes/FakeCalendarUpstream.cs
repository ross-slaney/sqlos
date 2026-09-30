using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlOS.BehaviorLock.Host.Fakes;

/// <summary>
/// Fake Google Calendar and Microsoft Graph calendar APIs plus their OAuth token endpoints.
/// Ported from <c>tests/SqlOS.IntegrationTests/Infrastructure/FakeCalendarProviderHttpClientFactory.cs</c>
/// with the same conventions: authorization codes are <c>success:{email}</c>,
/// <c>norefresh:{email}</c>, or start with <c>bad</c>; refresh tokens starting with <c>revoked</c>
/// fail with <c>invalid_grant</c>; sync token <c>google-sync-1</c> and delta token <c>ms-delta-1</c>
/// return incremental changes, and <c>expired</c> returns 410 Gone.
/// </summary>
public sealed class FakeCalendarUpstream
{
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;

        if (request.Method == HttpMethod.Get && uri.Contains(".well-known/openid-configuration", StringComparison.OrdinalIgnoreCase))
        {
            return FakeOidcUpstream.Json(HttpStatusCode.OK, BuildDiscoveryDocument(uri));
        }

        if (request.Method == HttpMethod.Post && uri.Contains("/token", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleTokenAsync(request, uri, cancellationToken);
        }

        if (uri.Contains("googleapis.com/calendar/v3/", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleGoogleCalendarAsync(request, uri, cancellationToken);
        }

        if (uri.Contains("graph.microsoft.com/v1.0/", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleGraphAsync(request, uri, cancellationToken);
        }

        return FakeOidcUpstream.Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = "not_found", ["url"] = uri });
    }

    private static async Task<HttpResponseMessage> HandleTokenAsync(HttpRequestMessage request, string uri, CancellationToken cancellationToken)
    {
        var provider = uri.Contains("microsoftonline.com", StringComparison.OrdinalIgnoreCase) ? "microsoft" : "google";
        var form = FakeOidcUpstream.ParseForm(await request.Content!.ReadAsStringAsync(cancellationToken));
        var grantType = form.GetValueOrDefault("grant_type");

        if (string.Equals(grantType, "refresh_token", StringComparison.Ordinal))
        {
            var refreshToken = form.GetValueOrDefault("refresh_token") ?? string.Empty;
            if (refreshToken.StartsWith("revoked", StringComparison.OrdinalIgnoreCase))
            {
                return FakeOidcUpstream.Json(HttpStatusCode.BadRequest, new JsonObject
                {
                    ["error"] = "invalid_grant",
                    ["error_description"] = "The refresh token has been revoked."
                });
            }

            var email = refreshToken.Split('|').LastOrDefault() ?? "user@example.com";
            return FakeOidcUpstream.Json(HttpStatusCode.OK, new JsonObject
            {
                ["access_token"] = $"{provider}-access-refreshed|{email}",
                ["refresh_token"] = $"{provider}-refresh-rotated|{email}",
                ["token_type"] = "Bearer",
                ["expires_in"] = 3600,
                ["scope"] = provider == "google"
                    ? "https://www.googleapis.com/auth/calendar.readonly"
                    : "Calendars.Read offline_access"
            });
        }

        var code = form.GetValueOrDefault("code") ?? string.Empty;
        if (code.StartsWith("bad", StringComparison.OrdinalIgnoreCase))
        {
            return FakeOidcUpstream.Json(HttpStatusCode.BadRequest, new JsonObject
            {
                ["error"] = "invalid_grant",
                ["error_description"] = "The authorization code is invalid."
            });
        }

        var parts = code.Split(':', 2);
        var accountEmail = parts.Length > 1 ? parts[1] : "user@example.com";
        var payload = new JsonObject
        {
            ["access_token"] = $"{provider}-access|{accountEmail}",
            ["token_type"] = "Bearer",
            ["expires_in"] = 3600,
            ["scope"] = provider == "google"
                ? "openid email https://www.googleapis.com/auth/calendar.readonly"
                : "openid email Calendars.Read offline_access",
            ["id_token"] = CreateUnsignedIdToken(provider, accountEmail)
        };
        if (!parts[0].StartsWith("norefresh", StringComparison.OrdinalIgnoreCase))
        {
            payload["refresh_token"] = $"{provider}-refresh|{accountEmail}";
        }

        return FakeOidcUpstream.Json(HttpStatusCode.OK, payload);
    }

    private static async Task<HttpResponseMessage> HandleGoogleCalendarAsync(HttpRequestMessage request, string uri, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Headers.Authorization?.Parameter))
        {
            return FakeOidcUpstream.Json(HttpStatusCode.Unauthorized, JsonNode.Parse("""{"error":{"code":401,"message":"Login Required."}}""")!);
        }

        if (request.Method == HttpMethod.Get && uri.Contains("/users/me/calendarList", StringComparison.OrdinalIgnoreCase))
        {
            return FakeOidcUpstream.Json(HttpStatusCode.OK, JsonNode.Parse("""
                {"items":[
                  {"id":"google-primary","summary":"Primary","primary":true,"timeZone":"UTC"},
                  {"id":"google-team","summary":"Team","primary":false,"timeZone":"UTC"}]}
                """)!);
        }

        if (request.Method == HttpMethod.Post && uri.Contains("/events", StringComparison.OrdinalIgnoreCase))
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return FakeOidcUpstream.Json(HttpStatusCode.OK, new JsonObject
            {
                ["id"] = "google-created-1",
                ["status"] = "confirmed",
                ["summary"] = body.RootElement.TryGetProperty("summary", out var s) ? s.GetString() : null,
                ["start"] = new JsonObject { ["dateTime"] = body.RootElement.GetProperty("start").GetProperty("dateTime").GetString() },
                ["end"] = new JsonObject { ["dateTime"] = body.RootElement.GetProperty("end").GetProperty("dateTime").GetString() }
            });
        }

        if (request.Method == HttpMethod.Get && uri.Contains("/events", StringComparison.OrdinalIgnoreCase))
        {
            if (uri.Contains("syncToken=expired", StringComparison.OrdinalIgnoreCase))
            {
                return FakeOidcUpstream.Json(HttpStatusCode.Gone, JsonNode.Parse("""{"error":{"code":410,"message":"Sync token is no longer valid."}}""")!);
            }

            if (uri.Contains("syncToken=google-sync-1", StringComparison.OrdinalIgnoreCase))
            {
                return FakeOidcUpstream.Json(HttpStatusCode.OK, new JsonObject
                {
                    ["items"] = new JsonArray(
                        new JsonObject { ["id"] = "google-evt-1", ["status"] = "cancelled" },
                        GoogleEvent("google-evt-2", "Standup (moved)", "2026-07-06T10:00:00Z", "2026-07-06T10:30:00Z"),
                        GoogleEvent("google-evt-3", "Retro", "2026-07-08T15:00:00Z", "2026-07-08T16:00:00Z"),
                        GoogleEvent("google-created-1", "Modified remotely", "2026-07-09T09:00:00Z", "2026-07-09T09:30:00Z")),
                    ["nextSyncToken"] = "google-sync-2"
                });
            }

            return FakeOidcUpstream.Json(HttpStatusCode.OK, new JsonObject
            {
                ["items"] = new JsonArray(
                    GoogleEvent("google-evt-1", "Kickoff", "2026-07-05T09:00:00Z", "2026-07-05T10:00:00Z"),
                    GoogleEvent("google-evt-2", "Standup", "2026-07-06T09:00:00Z", "2026-07-06T09:30:00Z")),
                ["nextSyncToken"] = "google-sync-1"
            });
        }

        return FakeOidcUpstream.Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = "not_found", ["url"] = uri });
    }

    private static async Task<HttpResponseMessage> HandleGraphAsync(HttpRequestMessage request, string uri, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Headers.Authorization?.Parameter))
        {
            return FakeOidcUpstream.Json(HttpStatusCode.Unauthorized, JsonNode.Parse("""{"error":{"code":"InvalidAuthenticationToken","message":"Access token is empty."}}""")!);
        }

        if (request.Method == HttpMethod.Get && uri.EndsWith("/me/calendars", StringComparison.OrdinalIgnoreCase))
        {
            return FakeOidcUpstream.Json(HttpStatusCode.OK, JsonNode.Parse("""
                {"value":[
                  {"id":"graph-default","name":"Calendar","isDefaultCalendar":true},
                  {"id":"graph-team","name":"Team","isDefaultCalendar":false}]}
                """)!);
        }

        if (request.Method == HttpMethod.Post && uri.Contains("/events", StringComparison.OrdinalIgnoreCase))
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return FakeOidcUpstream.Json(HttpStatusCode.Created, new JsonObject
            {
                ["id"] = "graph-created-1",
                ["subject"] = body.RootElement.TryGetProperty("subject", out var s) ? s.GetString() : null,
                ["isCancelled"] = false,
                ["showAs"] = "busy",
                ["isAllDay"] = false,
                ["start"] = new JsonObject { ["dateTime"] = body.RootElement.GetProperty("start").GetProperty("dateTime").GetString(), ["timeZone"] = "UTC" },
                ["end"] = new JsonObject { ["dateTime"] = body.RootElement.GetProperty("end").GetProperty("dateTime").GetString(), ["timeZone"] = "UTC" }
            });
        }

        if (request.Method == HttpMethod.Get && uri.Contains("/calendarView/delta", StringComparison.OrdinalIgnoreCase))
        {
            if (uri.Contains("%24deltatoken=ms-delta-1", StringComparison.OrdinalIgnoreCase)
                || uri.Contains("$deltatoken=ms-delta-1", StringComparison.OrdinalIgnoreCase))
            {
                return FakeOidcUpstream.Json(HttpStatusCode.OK, new JsonObject
                {
                    ["value"] = new JsonArray(
                        new JsonObject { ["id"] = "graph-evt-1", ["@removed"] = new JsonObject { ["reason"] = "deleted" } },
                        GraphEvent("graph-evt-2", "Design review (moved)", "2026-07-07T13:00:00Z", "2026-07-07T14:00:00Z")),
                    ["@odata.deltaLink"] = "https://graph.microsoft.com/v1.0/me/calendars/graph-default/calendarView/delta?$deltatoken=ms-delta-2"
                });
            }

            return FakeOidcUpstream.Json(HttpStatusCode.OK, new JsonObject
            {
                ["value"] = new JsonArray(
                    GraphEvent("graph-evt-1", "Planning", "2026-07-05T11:00:00Z", "2026-07-05T12:00:00Z"),
                    GraphEvent("graph-evt-2", "Design review", "2026-07-07T13:00:00Z", "2026-07-07T14:00:00Z")),
                ["@odata.deltaLink"] = "https://graph.microsoft.com/v1.0/me/calendars/graph-default/calendarView/delta?$deltatoken=ms-delta-1"
            });
        }

        return FakeOidcUpstream.Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = "not_found", ["url"] = uri });
    }

    private static JsonObject GoogleEvent(string id, string summary, string start, string end)
        => new()
        {
            ["id"] = id,
            ["status"] = "confirmed",
            ["summary"] = summary,
            ["location"] = "HQ",
            ["start"] = new JsonObject { ["dateTime"] = start },
            ["end"] = new JsonObject { ["dateTime"] = end }
        };

    private static JsonObject GraphEvent(string id, string subject, string start, string end)
        => new()
        {
            ["id"] = id,
            ["subject"] = subject,
            ["isCancelled"] = false,
            ["isAllDay"] = false,
            ["showAs"] = "busy",
            ["location"] = new JsonObject { ["displayName"] = "HQ" },
            ["start"] = new JsonObject { ["dateTime"] = start, ["timeZone"] = "UTC" },
            ["end"] = new JsonObject { ["dateTime"] = end, ["timeZone"] = "UTC" }
        };

    private static JsonObject BuildDiscoveryDocument(string uri)
    {
        if (uri.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
        {
            var tenant = uri.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .SkipWhile(part => !string.Equals(part, "login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault() ?? "common";
            return new JsonObject
            {
                ["issuer"] = $"https://login.microsoftonline.com/{tenant}/v2.0",
                ["authorization_endpoint"] = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize",
                ["token_endpoint"] = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
                ["userinfo_endpoint"] = "https://graph.microsoft.com/oidc/userinfo",
                ["jwks_uri"] = $"https://login.microsoftonline.com/{tenant}/discovery/v2.0/keys"
            };
        }

        return new JsonObject
        {
            ["issuer"] = "https://accounts.google.com",
            ["authorization_endpoint"] = "https://accounts.google.com/o/oauth2/v2/auth",
            ["token_endpoint"] = "https://oauth2.googleapis.com/token",
            ["userinfo_endpoint"] = "https://openidconnect.googleapis.com/v1/userinfo",
            ["jwks_uri"] = "https://www.googleapis.com/oauth2/v3/certs"
        };
    }

    private static string CreateUnsignedIdToken(string provider, string email)
    {
        var header = FakeOidcUpstream.Base64Url(new JsonObject { ["alg"] = "none", ["typ"] = "JWT" }.ToJsonString());
        var payload = FakeOidcUpstream.Base64Url(new JsonObject
        {
            ["sub"] = $"{provider}-cal-{email}",
            ["email"] = email,
            ["aud"] = "client"
        }.ToJsonString());
        return $"{header}.{payload}.";
    }
}

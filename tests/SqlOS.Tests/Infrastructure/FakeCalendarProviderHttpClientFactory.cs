using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SqlOS.Tests.Infrastructure;

/// <summary>
/// Fakes the Google/Microsoft OAuth and calendar REST APIs for calendar integration tests,
/// mirroring the <see cref="FakeOidcProviderHttpClientFactory"/> magic-string conventions:
/// authorization codes are "success:{email}", "norefresh:{email}", or "bad..."; refresh
/// tokens starting with "revoked" fail with invalid_grant.
/// </summary>
internal sealed class FakeCalendarProviderHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
        => new(new FakeCalendarProviderHttpMessageHandler())
        {
            BaseAddress = new Uri("https://localhost")
        };

    private sealed class FakeCalendarProviderHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;

            if (request.Method == HttpMethod.Get && uri.Contains(".well-known/openid-configuration", StringComparison.OrdinalIgnoreCase))
            {
                return Json(HttpStatusCode.OK, BuildDiscoveryDocument(uri));
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

            return Json(HttpStatusCode.NotFound, new { error = "not_found", url = uri });
        }

        private static async Task<HttpResponseMessage> HandleTokenAsync(HttpRequestMessage request, string uri, CancellationToken cancellationToken)
        {
            var provider = uri.Contains("microsoftonline.com", StringComparison.OrdinalIgnoreCase) ? "microsoft" : "google";
            var form = ParseForm(await request.Content!.ReadAsStringAsync(cancellationToken));
            var grantType = form.GetValueOrDefault("grant_type");

            if (string.Equals(grantType, "refresh_token", StringComparison.Ordinal))
            {
                var refreshToken = form.GetValueOrDefault("refresh_token") ?? string.Empty;
                if (refreshToken.StartsWith("revoked", StringComparison.OrdinalIgnoreCase))
                {
                    return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant", error_description = "The refresh token has been revoked." });
                }

                var email = refreshToken.Split('|').LastOrDefault() ?? "user@example.com";
                return Json(HttpStatusCode.OK, new
                {
                    access_token = $"{provider}-access-refreshed|{email}",
                    refresh_token = $"{provider}-refresh-rotated|{email}",
                    token_type = "Bearer",
                    expires_in = 3600,
                    scope = provider == "google"
                        ? "https://www.googleapis.com/auth/calendar.readonly"
                        : "Calendars.Read offline_access"
                });
            }

            var code = form.GetValueOrDefault("code") ?? string.Empty;
            if (code.StartsWith("bad", StringComparison.OrdinalIgnoreCase))
            {
                return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant", error_description = "The authorization code is invalid." });
            }

            var parts = code.Split(':', 2);
            var status = parts[0];
            var accountEmail = parts.Length > 1 ? parts[1] : "user@example.com";
            var includeRefreshToken = !status.StartsWith("norefresh", StringComparison.OrdinalIgnoreCase);

            var payload = new Dictionary<string, object?>
            {
                ["access_token"] = $"{provider}-access|{accountEmail}",
                ["token_type"] = "Bearer",
                ["expires_in"] = 3600,
                ["scope"] = provider == "google"
                    ? "openid email https://www.googleapis.com/auth/calendar.readonly"
                    : "openid email Calendars.Read offline_access",
                ["id_token"] = CreateUnsignedIdToken(provider, accountEmail)
            };
            if (includeRefreshToken)
            {
                payload["refresh_token"] = $"{provider}-refresh|{accountEmail}";
            }

            return Json(HttpStatusCode.OK, payload);
        }

        private static async Task<HttpResponseMessage> HandleGoogleCalendarAsync(HttpRequestMessage request, string uri, CancellationToken cancellationToken)
        {
            if (!HasBearerToken(request))
            {
                return Json(HttpStatusCode.Unauthorized, new { error = new { code = 401, message = "Login Required." } });
            }

            if (request.Method == HttpMethod.Get && uri.Contains("/users/me/calendarList", StringComparison.OrdinalIgnoreCase))
            {
                return Json(HttpStatusCode.OK, new
                {
                    items = new object[]
                    {
                        new { id = "google-primary", summary = "Primary", primary = true, timeZone = "UTC" },
                        new { id = "google-team", summary = "Team", primary = false, timeZone = "UTC" }
                    }
                });
            }

            if (request.Method == HttpMethod.Post && uri.Contains("/events", StringComparison.OrdinalIgnoreCase))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var summary = body.RootElement.TryGetProperty("summary", out var s) ? s.GetString() : null;
                var start = body.RootElement.GetProperty("start").GetProperty("dateTime").GetString();
                var end = body.RootElement.GetProperty("end").GetProperty("dateTime").GetString();
                return Json(HttpStatusCode.OK, new
                {
                    id = "google-created-1",
                    status = "confirmed",
                    summary,
                    start = new { dateTime = start },
                    end = new { dateTime = end }
                });
            }

            if (request.Method == HttpMethod.Get && uri.Contains("/events", StringComparison.OrdinalIgnoreCase))
            {
                if (uri.Contains("syncToken=expired", StringComparison.OrdinalIgnoreCase))
                {
                    return Json(HttpStatusCode.Gone, new { error = new { code = 410, message = "Sync token is no longer valid." } });
                }

                if (uri.Contains("syncToken=google-sync-1", StringComparison.OrdinalIgnoreCase))
                {
                    return Json(HttpStatusCode.OK, new
                    {
                        items = new object[]
                        {
                            new { id = "google-evt-1", status = "cancelled" },
                            GoogleEvent("google-evt-2", "Standup (moved)", "2026-07-06T10:00:00Z", "2026-07-06T10:30:00Z"),
                            GoogleEvent("google-evt-3", "Retro", "2026-07-08T15:00:00Z", "2026-07-08T16:00:00Z"),
                            GoogleEvent("google-created-1", "Modified remotely", "2026-07-09T09:00:00Z", "2026-07-09T09:30:00Z")
                        },
                        nextSyncToken = "google-sync-2"
                    });
                }

                return Json(HttpStatusCode.OK, new
                {
                    items = new object[]
                    {
                        GoogleEvent("google-evt-1", "Kickoff", "2026-07-05T09:00:00Z", "2026-07-05T10:00:00Z"),
                        GoogleEvent("google-evt-2", "Standup", "2026-07-06T09:00:00Z", "2026-07-06T09:30:00Z")
                    },
                    nextSyncToken = "google-sync-1"
                });
            }

            return Json(HttpStatusCode.NotFound, new { error = "not_found", url = uri });
        }

        private static async Task<HttpResponseMessage> HandleGraphAsync(HttpRequestMessage request, string uri, CancellationToken cancellationToken)
        {
            if (!HasBearerToken(request))
            {
                return Json(HttpStatusCode.Unauthorized, new { error = new { code = "InvalidAuthenticationToken", message = "Access token is empty." } });
            }

            if (request.Method == HttpMethod.Get && uri.EndsWith("/me/calendars", StringComparison.OrdinalIgnoreCase))
            {
                return Json(HttpStatusCode.OK, new
                {
                    value = new object[]
                    {
                        new { id = "graph-default", name = "Calendar", isDefaultCalendar = true },
                        new { id = "graph-team", name = "Team", isDefaultCalendar = false }
                    }
                });
            }

            if (request.Method == HttpMethod.Post && uri.Contains("/events", StringComparison.OrdinalIgnoreCase))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var subject = body.RootElement.TryGetProperty("subject", out var s) ? s.GetString() : null;
                var start = body.RootElement.GetProperty("start").GetProperty("dateTime").GetString();
                var end = body.RootElement.GetProperty("end").GetProperty("dateTime").GetString();
                return Json(HttpStatusCode.Created, new
                {
                    id = "graph-created-1",
                    subject,
                    isCancelled = false,
                    showAs = "busy",
                    isAllDay = false,
                    start = new { dateTime = start, timeZone = "UTC" },
                    end = new { dateTime = end, timeZone = "UTC" }
                });
            }

            if (request.Method == HttpMethod.Get && uri.Contains("/calendarView/delta", StringComparison.OrdinalIgnoreCase))
            {
                if (uri.Contains("%24deltatoken=ms-delta-1", StringComparison.OrdinalIgnoreCase)
                    || uri.Contains("$deltatoken=ms-delta-1", StringComparison.OrdinalIgnoreCase))
                {
                    return Json(HttpStatusCode.OK, new Dictionary<string, object?>
                    {
                        ["value"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["id"] = "graph-evt-1",
                                ["@removed"] = new { reason = "deleted" }
                            },
                            GraphEvent("graph-evt-2", "Design review (moved)", "2026-07-07T13:00:00Z", "2026-07-07T14:00:00Z")
                        },
                        ["@odata.deltaLink"] = "https://graph.microsoft.com/v1.0/me/calendars/graph-default/calendarView/delta?$deltatoken=ms-delta-2"
                    });
                }

                return Json(HttpStatusCode.OK, new Dictionary<string, object?>
                {
                    ["value"] = new object[]
                    {
                        GraphEvent("graph-evt-1", "Planning", "2026-07-05T11:00:00Z", "2026-07-05T12:00:00Z"),
                        GraphEvent("graph-evt-2", "Design review", "2026-07-07T13:00:00Z", "2026-07-07T14:00:00Z")
                    },
                    ["@odata.deltaLink"] = "https://graph.microsoft.com/v1.0/me/calendars/graph-default/calendarView/delta?$deltatoken=ms-delta-1"
                });
            }

            return Json(HttpStatusCode.NotFound, new { error = "not_found", url = uri });
        }

        private static object GoogleEvent(string id, string summary, string start, string end)
            => new
            {
                id,
                status = "confirmed",
                summary,
                location = "HQ",
                start = new { dateTime = start },
                end = new { dateTime = end }
            };

        private static object GraphEvent(string id, string subject, string start, string end)
            => new
            {
                id,
                subject,
                isCancelled = false,
                isAllDay = false,
                showAs = "busy",
                location = new { displayName = "HQ" },
                start = new { dateTime = start, timeZone = "UTC" },
                end = new { dateTime = end, timeZone = "UTC" }
            };

        private static object BuildDiscoveryDocument(string uri)
        {
            if (uri.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
            {
                var tenant = uri.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .SkipWhile(part => !string.Equals(part, "login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
                    .Skip(1)
                    .FirstOrDefault() ?? "common";

                return new
                {
                    issuer = $"https://login.microsoftonline.com/{tenant}/v2.0",
                    authorization_endpoint = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize",
                    token_endpoint = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
                    userinfo_endpoint = "https://graph.microsoft.com/oidc/userinfo",
                    jwks_uri = $"https://login.microsoftonline.com/{tenant}/discovery/v2.0/keys"
                };
            }

            return new
            {
                issuer = "https://accounts.google.com",
                authorization_endpoint = "https://accounts.google.com/o/oauth2/v2/auth",
                token_endpoint = "https://oauth2.googleapis.com/token",
                userinfo_endpoint = "https://openidconnect.googleapis.com/v1/userinfo",
                jwks_uri = "https://www.googleapis.com/oauth2/v3/certs"
            };
        }

        private static string CreateUnsignedIdToken(string provider, string email)
        {
            var header = Base64Url(JsonSerializer.Serialize(new { alg = "none", typ = "JWT" }));
            var payload = Base64Url(JsonSerializer.Serialize(new
            {
                sub = $"{provider}-cal-{email}",
                email,
                aud = "client"
            }));
            return $"{header}.{payload}.";
        }

        private static string Base64Url(string value)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static bool HasBearerToken(HttpRequestMessage request)
            => !string.IsNullOrWhiteSpace(request.Headers.Authorization?.Parameter);

        private static HttpResponseMessage Json(HttpStatusCode statusCode, object payload)
            => new(statusCode)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload))
                {
                    Headers =
                    {
                        ContentType = new MediaTypeHeaderValue("application/json")
                    }
                }
            };

        private static Dictionary<string, string> ParseForm(string payload)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in payload.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
                var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
                result[key] = value;
            }

            return result;
        }
    }
}

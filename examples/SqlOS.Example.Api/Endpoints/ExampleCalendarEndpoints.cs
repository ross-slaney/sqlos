using System.IdentityModel.Tokens.Jwt;
using SqlOS.AuthServer.Services;
using SqlOS.Calendar.Contracts;
using SqlOS.Calendar.Models;
using SqlOS.Calendar.Services;

namespace SqlOS.Example.Api.Endpoints;

/// <summary>
/// Demonstrates SqlOS calendar integration from a consuming app:
/// starting a connect flow for the signed-in user, reading normalized events
/// imported by read-pull sync, and using the connection-only token accessor.
/// The seeded Google/Microsoft social connections provide the OAuth apps, so
/// no extra provider registration is needed beyond granting calendar scopes.
/// </summary>
public static class ExampleCalendarEndpoints
{
    public static void MapExampleCalendarEndpoints(this WebApplication app)
    {
        var calendar = app.MapGroup("/api/calendar");
        calendar.ExcludeFromDescription();

        // Start a calendar connect flow for the signed-in user. The response contains the
        // provider authorization URL; the SPA redirects the browser there, SqlOS handles the
        // provider callback, and the user lands back on returnUri with ?calendarConnectionId=...
        calendar.MapPost("/connect/start", async (
            StartCalendarConnectBody body,
            HttpContext httpContext,
            SqlOSCalendarService calendarService,
            SqlOSOidcAuthService oidcAuthService,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.Unauthorized();
            }

            var providers = await oidcAuthService.ListEnabledProvidersAsync(cancellationToken);
            var provider = providers.FirstOrDefault(x =>
                string.Equals(x.ProviderType, body.Provider, StringComparison.OrdinalIgnoreCase));
            if (provider == null)
            {
                return Results.BadRequest(new
                {
                    message = $"No enabled '{body.Provider}' social connection is seeded. Configure SqlOS:Oidc:{body.Provider}:* first."
                });
            }

            if (!Enum.TryParse<SqlOSCalendarIntegrationMode>(body.Mode, ignoreCase: true, out var mode))
            {
                mode = SqlOSCalendarIntegrationMode.ReadPull;
            }

            try
            {
                var result = await calendarService.StartConnectAsync(
                    new SqlOSStartCalendarConnectRequest(
                        provider.ConnectionId,
                        mode,
                        body.ReturnUri,
                        UserId: userId,
                        LoginHintEmail: httpContext.User.FindFirst("email")?.Value),
                    httpContext,
                    cancellationToken);

                return Results.Ok(new
                {
                    authorizationUrl = result.AuthorizationUrl,
                    provider = result.ProviderType.ToString(),
                    mode = result.Mode.ToString()
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        // List the signed-in user's calendar connections.
        calendar.MapGet("/connections", async (
            HttpContext httpContext,
            SqlOSCalendarService calendarService,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await calendarService.ListConnectionsAsync(userId: userId, cancellationToken: cancellationToken));
        });

        // Read-pull demo: normalized events imported by SqlOS sync for one connection.
        calendar.MapGet("/connections/{connectionId}/events", async (
            string connectionId,
            DateTime? from,
            DateTime? to,
            HttpContext httpContext,
            SqlOSCalendarService calendarService,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.Unauthorized();
            }

            try
            {
                var events = await calendarService.ListEventsAsync(
                    connectionId,
                    from ?? DateTime.UtcNow.Date,
                    to ?? DateTime.UtcNow.Date.AddDays(30),
                    forUserId: userId,
                    cancellationToken: cancellationToken);
                return Results.Ok(events);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        // Trigger a sync now instead of waiting for the background scheduler.
        calendar.MapPost("/connections/{connectionId}/sync", async (
            string connectionId,
            HttpContext httpContext,
            SqlOSCalendarService calendarService,
            SqlOSCalendarSyncService syncService,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.Unauthorized();
            }

            try
            {
                // Ownership check before the admin-shaped sync call.
                await calendarService.GetConnectionAsync(connectionId, forUserId: userId, cancellationToken: cancellationToken);
                return Results.Ok(await syncService.SyncConnectionAsync(connectionId, cancellationToken));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        // Connection-only demo: fetch a short-lived provider access token and call
        // Google/Microsoft directly from the app. SqlOS never stores event copies here.
        calendar.MapGet("/connections/{connectionId}/token", async (
            string connectionId,
            HttpContext httpContext,
            SqlOSCalendarService calendarService,
            CancellationToken cancellationToken) =>
        {
            var userId = httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.Unauthorized();
            }

            try
            {
                var token = await calendarService.GetAccessTokenAsync(
                    connectionId,
                    forUserId: userId,
                    cancellationToken: cancellationToken);
                return Results.Ok(new
                {
                    accessToken = token.AccessToken,
                    expiresAt = token.ExpiresAt,
                    provider = token.ProviderType.ToString(),
                    scopes = token.Scopes
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });
    }

    public sealed record StartCalendarConnectBody(
        string Provider,
        string ReturnUri,
        string? Mode = null);
}

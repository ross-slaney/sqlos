using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Security;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Calendar.Configuration;
using SqlOS.Calendar.Contracts;
using SqlOS.Calendar.Interfaces;
using SqlOS.Calendar.Models;
using SqlOS.Configuration;

namespace SqlOS.Calendar.Services;

/// <summary>
/// Calendar resource connections for users and organizations. Reuses the consumer's seeded
/// Google/Microsoft OIDC connections for OAuth app credentials while keeping calendar consent
/// completely separate from sign-in: login flows never request calendar scopes.
/// </summary>
public sealed class SqlOSCalendarService
{
    internal const string ConnectRequestTokenPurpose = "calendar_connect_request";

    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEnumerable<ISqlOSCalendarProviderAdapter> _adapters;
    private readonly SqlOSCalendarOptions _calendarOptions;
    private readonly SqlOSAuthServerOptions _authOptions;
    private readonly ILogger<SqlOSCalendarService> _logger;

    public SqlOSCalendarService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSCryptoService cryptoService,
        IHttpClientFactory httpClientFactory,
        IEnumerable<ISqlOSCalendarProviderAdapter> adapters,
        IOptions<SqlOSOptions> options,
        ILogger<SqlOSCalendarService> logger)
    {
        _context = context;
        _adminService = adminService;
        _cryptoService = cryptoService;
        _httpClientFactory = httpClientFactory;
        _adapters = adapters;
        _calendarOptions = options.Value.Calendar;
        _authOptions = options.Value.AuthServer;
        _logger = logger;
    }

    /// <summary>
    /// Starts a calendar connect flow and returns the provider authorization URL the user's
    /// browser should be redirected to. The provider callback lands on the SqlOS-owned
    /// <c>{BasePath}/calendar/callback</c> endpoint, which finishes the connection and then
    /// redirects to the request's <see cref="SqlOSStartCalendarConnectRequest.ReturnUri"/>.
    /// </summary>
    public async Task<SqlOSStartCalendarConnectResult> StartConnectAsync(
        SqlOSStartCalendarConnectRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var hasUser = !string.IsNullOrWhiteSpace(request.UserId);
        var hasOrganization = !string.IsNullOrWhiteSpace(request.OrganizationId);
        if (hasUser == hasOrganization)
        {
            throw new InvalidOperationException("A calendar connection must be bound to exactly one user or one organization.");
        }

        if (string.IsNullOrWhiteSpace(request.ReturnUri) || !Uri.TryCreate(request.ReturnUri, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("The calendar connect return URI must be an absolute URI.");
        }

        if (hasUser)
        {
            _ = await _context.Set<SqlOSUser>().FirstOrDefaultAsync(x => x.Id == request.UserId, cancellationToken)
                ?? throw new InvalidOperationException("The calendar connection user was not found.");
        }
        else
        {
            _ = await _context.Set<SqlOSOrganization>().FirstOrDefaultAsync(x => x.Id == request.OrganizationId, cancellationToken)
                ?? throw new InvalidOperationException("The calendar connection organization was not found.");
        }

        var oidcConnection = await RequireCalendarCapableOidcConnectionAsync(request.OidcConnectionId, cancellationToken);
        var providerType = MapProviderType(oidcConnection.ProviderType);
        var endpoints = await ResolveProviderEndpointsAsync(oidcConnection, cancellationToken);
        var scopes = ResolveScopes(providerType, request.Mode, request.Scopes);
        var callbackUri = BuildCallbackUri(httpContext);
        var codeVerifier = _cryptoService.GenerateOpaqueToken();

        var state = await _cryptoService.CreateTemporaryTokenAsync(
            ConnectRequestTokenPurpose,
            request.UserId,
            null,
            request.OrganizationId,
            new CalendarConnectRequestPayload(
                oidcConnection.Id,
                request.Mode,
                request.UserId,
                request.OrganizationId,
                request.DisplayName,
                scopes,
                request.ReturnUri,
                codeVerifier,
                callbackUri,
                endpoints.TokenEndpoint),
            _calendarOptions.ConnectSessionLifetime,
            cancellationToken);

        var authorizationParameters = new Dictionary<string, string?>
        {
            ["client_id"] = oidcConnection.ClientId,
            ["redirect_uri"] = callbackUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', scopes),
            ["state"] = state,
            ["code_challenge"] = _cryptoService.CreatePkceCodeChallenge(codeVerifier),
            ["code_challenge_method"] = "S256",
            ["login_hint"] = request.LoginHintEmail
        };

        if (providerType == SqlOSCalendarProviderType.Google)
        {
            // Google only issues refresh tokens for offline access with forced consent.
            authorizationParameters["access_type"] = "offline";
            authorizationParameters["prompt"] = "consent";
        }

        var authorizationUrl = QueryHelpers.AddQueryString(endpoints.AuthorizationEndpoint, authorizationParameters);

        await _adminService.RecordAuditAsync(
            "calendar.connect.start",
            "oidc_connection",
            oidcConnection.Id,
            userId: request.UserId,
            organizationId: request.OrganizationId,
            ipAddress: GetIp(httpContext),
            data: new
            {
                provider = providerType.ToString(),
                mode = request.Mode.ToString(),
                scopes
            },
            cancellationToken: cancellationToken);

        return new SqlOSStartCalendarConnectResult(authorizationUrl, oidcConnection.Id, providerType, request.Mode);
    }

    /// <summary>
    /// Handles the provider redirect on <c>{BasePath}/calendar/callback</c>: exchanges the code,
    /// stores encrypted tokens, and redirects the browser to the app's return URI with either
    /// <c>calendarConnectionId</c> or <c>error</c> appended.
    /// </summary>
    public async Task<IResult> HandleConnectCallbackAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var query = httpContext.Request.Query;
        var state = query["state"].ToString();
        if (string.IsNullOrWhiteSpace(state))
        {
            return RenderCallbackError("The calendar connect callback was missing the provider state.");
        }

        var requestToken = await _cryptoService.ConsumeTemporaryTokenAsync(ConnectRequestTokenPurpose, state, cancellationToken);
        if (requestToken == null)
        {
            return RenderCallbackError("The calendar connect request is invalid or expired.");
        }

        var payload = _cryptoService.DeserializePayload<CalendarConnectRequestPayload>(requestToken);
        if (payload == null)
        {
            return RenderCallbackError("The calendar connect request payload is invalid.");
        }

        var error = query["error"].ToString();
        if (!string.IsNullOrWhiteSpace(error))
        {
            var description = query["error_description"].ToString();
            return Results.Redirect(AppendQuery(payload.ReturnUri, new Dictionary<string, string?>
            {
                ["error"] = string.IsNullOrWhiteSpace(description) ? error : description
            }));
        }

        var code = query["code"].ToString();
        if (string.IsNullOrWhiteSpace(code))
        {
            return Results.Redirect(AppendQuery(payload.ReturnUri, new Dictionary<string, string?>
            {
                ["error"] = "The calendar connect callback was missing the provider code."
            }));
        }

        try
        {
            var result = await CompleteConnectAsync(payload, code, GetIp(httpContext), cancellationToken);
            return Results.Redirect(AppendQuery(payload.ReturnUri, new Dictionary<string, string?>
            {
                ["calendarConnectionId"] = result.CalendarConnectionId
            }));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Redirect(AppendQuery(payload.ReturnUri, new Dictionary<string, string?>
            {
                ["error"] = ex.Message
            }));
        }
    }

    /// <summary>
    /// Exchanges the provider authorization code and persists the calendar connection with
    /// encrypted tokens. Exposed for headless flows and tests; browser flows should go through
    /// <see cref="HandleConnectCallbackAsync"/>.
    /// </summary>
    public async Task<SqlOSCompleteCalendarConnectResult> CompleteConnectAsync(
        CalendarConnectRequestPayload payload,
        string code,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var oidcConnection = await RequireCalendarCapableOidcConnectionAsync(payload.OidcConnectionId, cancellationToken);
        var providerType = MapProviderType(oidcConnection.ProviderType);
        var adapter = RequireAdapter(providerType);

        try
        {
            var providerContext = CreateProviderContext(oidcConnection, payload.TokenEndpoint);
            var tokens = await adapter.ExchangeAuthorizationCodeAsync(
                providerContext,
                code,
                payload.CallbackUri,
                payload.CodeVerifier,
                cancellationToken);

            if (payload.Mode != SqlOSCalendarIntegrationMode.ConnectionOnly && string.IsNullOrWhiteSpace(tokens.RefreshToken))
            {
                throw new InvalidOperationException(
                    "The provider did not return a refresh token, which read-pull and two-way connections require. " +
                    "Ensure offline access is granted and consent was not silently skipped.");
            }

            var account = ParseIdTokenAccount(tokens);
            var now = DateTime.UtcNow;
            var grantedScopes = tokens.Scopes.Count > 0 ? tokens.Scopes : payload.Scopes;
            var connection = new SqlOSCalendarConnection
            {
                Id = _cryptoService.GenerateId("cal"),
                ProviderType = providerType,
                Mode = payload.Mode,
                Status = SqlOSCalendarConnectionStatus.Active,
                OidcConnectionId = oidcConnection.Id,
                UserId = payload.UserId,
                OrganizationId = payload.OrganizationId,
                DisplayName = string.IsNullOrWhiteSpace(payload.DisplayName)
                    ? $"{oidcConnection.DisplayName} Calendar"
                    : payload.DisplayName!.Trim(),
                ProviderAccountEmail = account.Email,
                ProviderAccountSubject = account.Subject,
                ScopesJson = JsonSerializer.Serialize(grantedScopes),
                AccessTokenEncrypted = _cryptoService.ProtectSecret(tokens.AccessToken),
                RefreshTokenEncrypted = string.IsNullOrWhiteSpace(tokens.RefreshToken)
                    ? null
                    : _cryptoService.ProtectSecret(tokens.RefreshToken!),
                AccessTokenExpiresAt = tokens.ExpiresAt,
                CreatedAt = now,
                UpdatedAt = now
            };

            _context.Set<SqlOSCalendarConnection>().Add(connection);
            await _context.SaveChangesAsync(cancellationToken);

            await _adminService.RecordAuditAsync(
                "calendar.connection.created",
                "calendar_connection",
                connection.Id,
                userId: payload.UserId,
                organizationId: payload.OrganizationId,
                ipAddress: ipAddress,
                data: new
                {
                    provider = providerType.ToString(),
                    mode = payload.Mode.ToString(),
                    providerAccountEmail = account.Email
                },
                cancellationToken: cancellationToken);

            return new SqlOSCompleteCalendarConnectResult(
                connection.Id,
                providerType,
                payload.Mode,
                payload.UserId,
                payload.OrganizationId,
                account.Email,
                payload.ReturnUri);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Calendar connect failed for OIDC connection {ConnectionId}.", payload.OidcConnectionId);
            await _adminService.RecordAuditAsync(
                "calendar.connect.error",
                "oidc_connection",
                payload.OidcConnectionId,
                userId: payload.UserId,
                organizationId: payload.OrganizationId,
                ipAddress: ipAddress,
                data: new { error = ex.Message },
                cancellationToken: cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<SqlOSCalendarConnectionSummary>> ListConnectionsAsync(
        string? userId = null,
        string? organizationId = null,
        bool includeRevoked = false,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Set<SqlOSCalendarConnection>().AsQueryable();
        if (!string.IsNullOrWhiteSpace(userId))
        {
            query = query.Where(x => x.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            query = query.Where(x => x.OrganizationId == organizationId);
        }

        if (!includeRevoked)
        {
            query = query.Where(x => x.RevokedAt == null);
        }

        var connections = await query
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        return connections.Select(ToSummary).ToList();
    }

    public async Task<SqlOSCalendarConnectionSummary> GetConnectionAsync(
        string calendarConnectionId,
        string? forUserId = null,
        string? forOrganizationId = null,
        CancellationToken cancellationToken = default)
        => ToSummary(await RequireConnectionAsync(calendarConnectionId, forUserId, forOrganizationId, includeRevoked: true, cancellationToken));

    /// <summary>
    /// Returns a short-lived provider access token for the authorized caller, refreshing it
    /// transparently when it is close to expiry. This is the whole surface an app needs in
    /// <see cref="SqlOSCalendarIntegrationMode.ConnectionOnly"/> mode — SqlOS never stores
    /// event copies for those connections.
    /// </summary>
    public async Task<SqlOSCalendarAccessTokenResult> GetAccessTokenAsync(
        string calendarConnectionId,
        string? forUserId = null,
        string? forOrganizationId = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await RequireConnectionAsync(calendarConnectionId, forUserId, forOrganizationId, includeRevoked: false, cancellationToken);
        var accessToken = await EnsureFreshAccessTokenAsync(connection, forceRefresh: false, cancellationToken);
        return new SqlOSCalendarAccessTokenResult(
            accessToken,
            connection.AccessTokenExpiresAt ?? DateTime.UtcNow,
            ParseScopes(connection.ScopesJson),
            connection.ProviderType);
    }

    /// <summary>Lists the calendars available on the connected provider account.</summary>
    public async Task<IReadOnlyList<SqlOSCalendarSummary>> ListProviderCalendarsAsync(
        string calendarConnectionId,
        string? forUserId = null,
        string? forOrganizationId = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await RequireConnectionAsync(calendarConnectionId, forUserId, forOrganizationId, includeRevoked: false, cancellationToken);
        var accessToken = await EnsureFreshAccessTokenAsync(connection, forceRefresh: false, cancellationToken);
        return await RequireAdapter(connection.ProviderType).ListCalendarsAsync(accessToken, cancellationToken);
    }

    /// <summary>
    /// Chooses which provider calendar a read-pull or two-way connection synchronizes.
    /// Repeat calls upsert; passing <paramref name="isSyncEnabled"/> false pauses one calendar.
    /// </summary>
    public async Task EnableCalendarSyncAsync(
        string calendarConnectionId,
        string providerCalendarId,
        string? displayName = null,
        bool isSyncEnabled = true,
        string? forUserId = null,
        string? forOrganizationId = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await RequireConnectionAsync(calendarConnectionId, forUserId, forOrganizationId, includeRevoked: false, cancellationToken);
        if (connection.Mode == SqlOSCalendarIntegrationMode.ConnectionOnly)
        {
            throw new InvalidOperationException("Connection-only calendar connections do not synchronize events.");
        }

        if (string.IsNullOrWhiteSpace(providerCalendarId))
        {
            throw new InvalidOperationException("A provider calendar id is required.");
        }

        var now = DateTime.UtcNow;
        var state = await _context.Set<SqlOSCalendarSyncState>()
            .FirstOrDefaultAsync(x => x.CalendarConnectionId == connection.Id && x.ProviderCalendarId == providerCalendarId, cancellationToken);
        if (state == null)
        {
            state = new SqlOSCalendarSyncState
            {
                Id = _cryptoService.GenerateId("csy"),
                CalendarConnectionId = connection.Id,
                ProviderCalendarId = providerCalendarId,
                CreatedAt = now
            };
            _context.Set<SqlOSCalendarSyncState>().Add(state);
        }

        state.DisplayName = displayName ?? state.DisplayName;
        state.IsSyncEnabled = isSyncEnabled;
        state.UpdatedAt = now;
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Reads normalized events imported by read-pull/two-way sync. Connection-only
    /// connections never persist events and therefore always throw here.
    /// </summary>
    public async Task<IReadOnlyList<SqlOSCalendarEventSnapshot>> ListEventsAsync(
        string calendarConnectionId,
        DateTime fromUtc,
        DateTime toUtc,
        string? forUserId = null,
        string? forOrganizationId = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await RequireConnectionAsync(calendarConnectionId, forUserId, forOrganizationId, includeRevoked: false, cancellationToken);
        if (connection.Mode == SqlOSCalendarIntegrationMode.ConnectionOnly)
        {
            throw new InvalidOperationException("Connection-only calendar connections do not store events. Use GetAccessTokenAsync and call the provider directly.");
        }

        var events = await _context.Set<SqlOSCalendarEvent>()
            .Where(x => x.CalendarConnectionId == connection.Id && x.StartsAtUtc < toUtc && x.EndsAtUtc > fromUtc)
            .OrderBy(x => x.StartsAtUtc)
            .ToListAsync(cancellationToken);

        return events
            .Select(x => new SqlOSCalendarEventSnapshot(
                x.ProviderEventId,
                x.Subject,
                x.StartsAtUtc,
                x.EndsAtUtc,
                x.IsAllDay,
                x.ShowAs,
                x.Status,
                x.Location))
            .ToList();
    }

    /// <summary>Disconnects a calendar connection and clears its stored tokens.</summary>
    public async Task<SqlOSCalendarConnectionSummary> DisconnectAsync(
        string calendarConnectionId,
        string reason = "disconnected",
        string? forUserId = null,
        string? forOrganizationId = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await RequireConnectionAsync(calendarConnectionId, forUserId, forOrganizationId, includeRevoked: true, cancellationToken);
        if (connection.RevokedAt == null)
        {
            var now = DateTime.UtcNow;
            connection.Status = SqlOSCalendarConnectionStatus.Revoked;
            connection.AccessTokenEncrypted = null;
            connection.RefreshTokenEncrypted = null;
            connection.AccessTokenExpiresAt = null;
            connection.RevokedAt = now;
            connection.RevokedReason = reason;
            connection.UpdatedAt = now;
            await _context.SaveChangesAsync(cancellationToken);

            await _adminService.RecordAuditAsync(
                "calendar.connection.disconnected",
                "calendar_connection",
                connection.Id,
                userId: connection.UserId,
                organizationId: connection.OrganizationId,
                data: new { reason },
                cancellationToken: cancellationToken);
        }

        return ToSummary(connection);
    }

    /// <summary>
    /// Returns a valid provider access token for the connection, refreshing when it expires
    /// within <see cref="SqlOSCalendarOptions.AccessTokenRefreshSkew"/> (or on demand).
    /// </summary>
    public async Task<string> EnsureFreshAccessTokenAsync(
        SqlOSCalendarConnection connection,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (connection.RevokedAt != null)
        {
            throw new InvalidOperationException("This calendar connection has been disconnected.");
        }

        var needsRefresh = forceRefresh
            || string.IsNullOrWhiteSpace(connection.AccessTokenEncrypted)
            || connection.AccessTokenExpiresAt == null
            || connection.AccessTokenExpiresAt <= DateTime.UtcNow.Add(_calendarOptions.AccessTokenRefreshSkew);

        if (!needsRefresh)
        {
            return _cryptoService.UnprotectSecret(connection.AccessTokenEncrypted!);
        }

        if (string.IsNullOrWhiteSpace(connection.RefreshTokenEncrypted))
        {
            throw new InvalidOperationException("The calendar access token expired and no refresh token is stored for this connection.");
        }

        var oidcConnection = await RequireCalendarCapableOidcConnectionAsync(connection.OidcConnectionId, cancellationToken);
        var endpoints = await ResolveProviderEndpointsAsync(oidcConnection, cancellationToken);
        var adapter = RequireAdapter(connection.ProviderType);

        try
        {
            var tokens = await adapter.RefreshAccessTokenAsync(
                CreateProviderContext(oidcConnection, endpoints.TokenEndpoint),
                _cryptoService.UnprotectSecret(connection.RefreshTokenEncrypted!),
                cancellationToken);

            var now = DateTime.UtcNow;
            connection.AccessTokenEncrypted = _cryptoService.ProtectSecret(tokens.AccessToken);
            connection.AccessTokenExpiresAt = tokens.ExpiresAt;
            if (!string.IsNullOrWhiteSpace(tokens.RefreshToken))
            {
                // Providers may rotate refresh tokens; always keep the newest one.
                connection.RefreshTokenEncrypted = _cryptoService.ProtectSecret(tokens.RefreshToken!);
            }

            connection.Status = SqlOSCalendarConnectionStatus.Active;
            connection.LastError = null;
            connection.LastErrorAt = null;
            connection.UpdatedAt = now;
            await _context.SaveChangesAsync(cancellationToken);
            return tokens.AccessToken;
        }
        catch (InvalidOperationException ex)
        {
            var now = DateTime.UtcNow;
            connection.Status = SqlOSCalendarConnectionStatus.Error;
            connection.LastError = ex.Message;
            connection.LastErrorAt = now;
            connection.UpdatedAt = now;
            await _context.SaveChangesAsync(cancellationToken);

            await _adminService.RecordAuditAsync(
                "calendar.connection.refresh_failed",
                "calendar_connection",
                connection.Id,
                userId: connection.UserId,
                organizationId: connection.OrganizationId,
                data: new { error = ex.Message },
                cancellationToken: cancellationToken);
            throw;
        }
    }

    /// <summary>Force-refreshes the access token (admin surface).</summary>
    public async Task<SqlOSCalendarConnectionSummary> ForceRefreshAsync(
        string calendarConnectionId,
        CancellationToken cancellationToken = default)
    {
        var connection = await RequireConnectionAsync(calendarConnectionId, null, null, includeRevoked: false, cancellationToken);
        await EnsureFreshAccessTokenAsync(connection, forceRefresh: true, cancellationToken);
        return ToSummary(connection);
    }

    /// <summary>Paginated admin listing for the dashboard.</summary>
    public async Task<object> GetAdminConnectionsAsync(
        string? search = null,
        bool includeRevoked = true,
        int? page = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedPage = Math.Max(1, page.GetValueOrDefault(1));
        var resolvedPageSize = Math.Clamp(pageSize.GetValueOrDefault(25), 1, 100);
        var query = _context.Set<SqlOSCalendarConnection>().AsNoTracking();

        if (!includeRevoked)
        {
            query = query.Where(x => x.RevokedAt == null);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmed = search.Trim();
            query = query.Where(x =>
                x.DisplayName.Contains(trimmed) ||
                (x.ProviderAccountEmail != null && x.ProviderAccountEmail.Contains(trimmed)) ||
                (x.UserId != null && x.UserId.Contains(trimmed)) ||
                (x.OrganizationId != null && x.OrganizationId.Contains(trimmed)));
        }

        query = query.OrderByDescending(x => x.CreatedAt);
        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)resolvedPageSize));
        var currentPage = Math.Min(resolvedPage, totalPages);
        var data = await query
            .Skip((currentPage - 1) * resolvedPageSize)
            .Take(resolvedPageSize)
            .ToListAsync(cancellationToken);

        return new
        {
            Data = data.Select(ToSummary).ToList(),
            Page = currentPage,
            PageSize = resolvedPageSize,
            TotalCount = totalCount,
            TotalPages = totalPages
        };
    }

    /// <summary>Admin detail view including per-calendar sync health.</summary>
    public async Task<object> GetAdminConnectionAsync(string calendarConnectionId, CancellationToken cancellationToken = default)
    {
        var connection = await RequireConnectionAsync(calendarConnectionId, null, null, includeRevoked: true, cancellationToken);
        var syncStates = await _context.Set<SqlOSCalendarSyncState>()
            .AsNoTracking()
            .Where(x => x.CalendarConnectionId == connection.Id)
            .OrderBy(x => x.ProviderCalendarId)
            .ToListAsync(cancellationToken);
        var eventCount = await _context.Set<SqlOSCalendarEvent>()
            .CountAsync(x => x.CalendarConnectionId == connection.Id, cancellationToken);

        return new
        {
            Connection = ToSummary(connection),
            EventCount = eventCount,
            Calendars = syncStates.Select(state => new
            {
                state.ProviderCalendarId,
                state.DisplayName,
                state.IsSyncEnabled,
                HasSyncCursor = !string.IsNullOrWhiteSpace(state.SyncCursor),
                state.LastSyncStartedAt,
                state.LastSyncCompletedAt,
                state.LastSyncStatus,
                state.LastSyncError,
                state.EventCount
            }).ToList()
        };
    }

    /// <summary>Aggregate counts for the dashboard overview cards.</summary>
    public async Task<object> GetAdminSummaryAsync(CancellationToken cancellationToken = default)
    {
        var connections = await _context.Set<SqlOSCalendarConnection>().CountAsync(cancellationToken);
        var active = await _context.Set<SqlOSCalendarConnection>()
            .CountAsync(x => x.Status == SqlOSCalendarConnectionStatus.Active && x.RevokedAt == null, cancellationToken);
        var errored = await _context.Set<SqlOSCalendarConnection>()
            .CountAsync(x => x.Status == SqlOSCalendarConnectionStatus.Error && x.RevokedAt == null, cancellationToken);
        var events = await _context.Set<SqlOSCalendarEvent>().CountAsync(cancellationToken);
        return new { connections, active, errored, events };
    }

    internal async Task<SqlOSCalendarConnection> RequireConnectionAsync(
        string calendarConnectionId,
        string? forUserId,
        string? forOrganizationId,
        bool includeRevoked,
        CancellationToken cancellationToken)
    {
        var connection = await _context.Set<SqlOSCalendarConnection>()
            .FirstOrDefaultAsync(x => x.Id == calendarConnectionId, cancellationToken)
            ?? throw new InvalidOperationException("The calendar connection was not found.");

        if (!string.IsNullOrWhiteSpace(forUserId) && !string.Equals(connection.UserId, forUserId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The calendar connection was not found.");
        }

        if (!string.IsNullOrWhiteSpace(forOrganizationId) && !string.Equals(connection.OrganizationId, forOrganizationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The calendar connection was not found.");
        }

        if (!includeRevoked && connection.RevokedAt != null)
        {
            throw new InvalidOperationException("This calendar connection has been disconnected.");
        }

        return connection;
    }

    internal async Task<SqlOSProviderEndpoints> ResolveProviderEndpointsAsync(
        SqlOSOidcConnection connection,
        CancellationToken cancellationToken)
    {
        if (!connection.UseDiscovery)
        {
            return new SqlOSProviderEndpoints(
                connection.AuthorizationEndpoint
                    ?? throw new InvalidOperationException("The social login connection is missing an authorization endpoint."),
                connection.TokenEndpoint
                    ?? throw new InvalidOperationException("The social login connection is missing a token endpoint."));
        }

        var discoveryUrl = connection.DiscoveryUrl
            ?? throw new InvalidOperationException("The OIDC connection is missing a discovery URL.");
        var httpClient = _httpClientFactory.CreateClient(nameof(SqlOSCalendarService));
        using var response = await httpClient.GetAsync(discoveryUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The OIDC discovery endpoint failed.");
        }

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var authorizationEndpoint = payload.RootElement.GetProperty("authorization_endpoint").GetString()
            ?? throw new InvalidOperationException("The OIDC discovery document is missing an authorization endpoint.");
        var tokenEndpoint = payload.RootElement.GetProperty("token_endpoint").GetString()
            ?? throw new InvalidOperationException("The OIDC discovery document is missing a token endpoint.");
        return new SqlOSProviderEndpoints(authorizationEndpoint, tokenEndpoint);
    }

    internal ISqlOSCalendarProviderAdapter RequireAdapter(SqlOSCalendarProviderType providerType)
        => _adapters.FirstOrDefault(x => x.ProviderType == providerType)
            ?? throw new InvalidOperationException($"No calendar provider adapter is registered for '{providerType}'.");

    internal SqlOSCalendarProviderContext CreateProviderContext(SqlOSOidcConnection connection, string tokenEndpoint)
        => new(
            connection.ClientId,
            !string.IsNullOrWhiteSpace(connection.ClientSecretEncrypted)
                ? _cryptoService.UnprotectSecret(connection.ClientSecretEncrypted)
                : throw new InvalidOperationException("The social login connection is missing a client secret."),
            tokenEndpoint);

    internal async Task<SqlOSOidcConnection> RequireCalendarCapableOidcConnectionAsync(string oidcConnectionId, CancellationToken cancellationToken)
    {
        var connection = await _context.Set<SqlOSOidcConnection>()
            .FirstOrDefaultAsync(x => x.Id == oidcConnectionId && x.IsEnabled, cancellationToken)
            ?? throw new InvalidOperationException("No enabled OIDC connection was found for this calendar request.");

        if (connection.ProviderType is not (SqlOSOidcProviderType.Google or SqlOSOidcProviderType.Microsoft))
        {
            throw new InvalidOperationException("Calendar integration supports Google and Microsoft connections only.");
        }

        return connection;
    }

    internal static SqlOSCalendarProviderType MapProviderType(SqlOSOidcProviderType providerType)
        => providerType switch
        {
            SqlOSOidcProviderType.Google => SqlOSCalendarProviderType.Google,
            SqlOSOidcProviderType.Microsoft => SqlOSCalendarProviderType.Microsoft,
            _ => throw new InvalidOperationException("Calendar integration supports Google and Microsoft connections only.")
        };

    internal static IReadOnlyList<string> ResolveScopes(
        SqlOSCalendarProviderType providerType,
        SqlOSCalendarIntegrationMode mode,
        IReadOnlyList<string>? requested)
    {
        if (requested is { Count: > 0 })
        {
            return requested
                .Where(static scope => !string.IsNullOrWhiteSpace(scope))
                .Select(static scope => scope.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        // Identity scopes let SqlOS record which provider account granted consent.
        var scopes = new List<string> { "openid", "email" };
        if (providerType == SqlOSCalendarProviderType.Google)
        {
            scopes.AddRange(SqlOSCalendarOptions.DefaultGoogleReadScopes);
            if (mode == SqlOSCalendarIntegrationMode.TwoWay)
            {
                scopes.AddRange(SqlOSCalendarOptions.DefaultGoogleWriteScopes);
            }
        }
        else
        {
            scopes.AddRange(mode == SqlOSCalendarIntegrationMode.TwoWay
                ? SqlOSCalendarOptions.DefaultMicrosoftWriteScopes
                : SqlOSCalendarOptions.DefaultMicrosoftReadScopes);
        }

        return scopes.Distinct(StringComparer.Ordinal).ToList();
    }

    private string BuildCallbackUri(HttpContext? httpContext)
    {
        var origin = SqlOSPublicOriginResolver.Resolve(_authOptions);

        return $"{origin}{_authOptions.BasePath.TrimEnd('/')}/calendar/callback";
    }

    private static (string? Email, string? Subject) ParseIdTokenAccount(SqlOSCalendarTokenResult tokens)
    {
        if (string.IsNullOrWhiteSpace(tokens.IdToken))
        {
            return (null, null);
        }

        try
        {
            var parts = tokens.IdToken!.Split('.');
            if (parts.Length < 2)
            {
                return (null, null);
            }

            var payloadJson = Encoding.UTF8.GetString(DecodeBase64Url(parts[1]));
            using var document = JsonDocument.Parse(payloadJson);
            var email = SqlOSCalendarProviderHttp.GetString(document.RootElement, "email")
                ?? SqlOSCalendarProviderHttp.GetString(document.RootElement, "preferred_username");
            var subject = SqlOSCalendarProviderHttp.GetString(document.RootElement, "sub");
            return (email, subject);
        }
        catch
        {
            return (null, null);
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        return Convert.FromBase64String(padded);
    }

    private static IReadOnlyList<string> ParseScopes(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static SqlOSCalendarConnectionSummary ToSummary(SqlOSCalendarConnection connection)
        => new(
            connection.Id,
            connection.ProviderType.ToString(),
            connection.Mode.ToString(),
            connection.Status.ToString(),
            connection.UserId,
            connection.OrganizationId,
            connection.DisplayName,
            connection.ProviderAccountEmail,
            ParseScopes(connection.ScopesJson),
            connection.AccessTokenExpiresAt,
            !string.IsNullOrWhiteSpace(connection.RefreshTokenEncrypted),
            connection.LastSyncAt,
            connection.LastError,
            connection.LastErrorAt,
            connection.CreatedAt,
            connection.RevokedAt);

    private static string AppendQuery(string uri, IDictionary<string, string?> parameters)
        => QueryHelpers.AddQueryString(uri, parameters);

    private static string? GetIp(HttpContext? httpContext)
        => httpContext?.Connection.RemoteIpAddress?.ToString();

    private static IResult RenderCallbackError(string message)
        => new SqlOSHostedHtmlResult(
            $$"""
            <html>
              <head>
                <title>SqlOS calendar connect error</title>
                <style>body { font-family: ui-sans-serif, system-ui, sans-serif; padding: 32px; }</style>
              </head>
              <body>
                <h1>SqlOS calendar connect error</h1>
                <p>{{System.Net.WebUtility.HtmlEncode(message)}}</p>
              </body>
            </html>
            """,
            StatusCodes.Status400BadRequest);

    internal sealed record SqlOSProviderEndpoints(string AuthorizationEndpoint, string TokenEndpoint);
}

/// <summary>Payload stored in the temporary state token for a pending calendar connect flow.</summary>
public sealed record CalendarConnectRequestPayload(
    string OidcConnectionId,
    SqlOSCalendarIntegrationMode Mode,
    string? UserId,
    string? OrganizationId,
    string? DisplayName,
    IReadOnlyList<string> Scopes,
    string ReturnUri,
    string CodeVerifier,
    string CallbackUri,
    string TokenEndpoint);

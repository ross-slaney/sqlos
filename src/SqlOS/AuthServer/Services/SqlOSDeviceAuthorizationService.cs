using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSDeviceAuthorizationService
{
    public const string PendingStatus = "pending";
    public const string ApprovedStatus = "approved";
    public const string DeniedStatus = "denied";

    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSAuthService _authService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSAuthServerOptions _options;

    public SqlOSDeviceAuthorizationService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSAuthService authService,
        SqlOSCryptoService cryptoService,
        IOptions<SqlOSAuthServerOptions> options)
    {
        _context = context;
        _adminService = adminService;
        _authService = authService;
        _cryptoService = cryptoService;
        _options = options.Value;
    }

    public async Task<SqlOSDeviceAuthorizationStartResult> StartAsync(
        SqlOSDeviceAuthorizationStartRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var client = await _adminService.RequireClientAsync(request.ClientId, null, cancellationToken, httpContext);
        EnsureClientAllowsDeviceAuthorization(client);
        await EnforceStartRateLimitsAsync(client.Id, GetIp(httpContext), cancellationToken);

        var requestedScopes = NormalizeRequestedScopes(request.Scope);
        var allowedScopes = SqlOSAdminService.DeserializeJsonList(client.AllowedScopesJson);
        if (allowedScopes.Count > 0)
        {
            var unsupportedScopes = requestedScopes
                .Where(scope => !allowedScopes.Contains(scope, StringComparer.Ordinal))
                .ToList();
            if (unsupportedScopes.Count > 0)
            {
                throw new SqlOSDeviceAuthorizationException(
                    "invalid_scope",
                    $"Unsupported scope(s): {string.Join(", ", unsupportedScopes)}.");
            }
        }

        var requestedResource = _options.ResourceIndicators.Enabled && !string.IsNullOrWhiteSpace(request.Resource)
            ? request.Resource.Trim()
            : null;
        if (!string.IsNullOrWhiteSpace(requestedResource)
            && !string.IsNullOrWhiteSpace(client.Audience)
            && !string.Equals(requestedResource, client.Audience, StringComparison.Ordinal))
        {
            throw new SqlOSDeviceAuthorizationException("invalid_target", "Requested resource is not allowed for this client.");
        }

        var rawDeviceCode = _cryptoService.GenerateOpaqueToken();
        var userCode = await GenerateUniqueUserCodeAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var options = _options.DeviceAuthorization;
        var deviceAuthorization = new SqlOSDeviceAuthorization
        {
            Id = _cryptoService.GenerateId("dev"),
            DeviceCodeHash = _cryptoService.HashToken(rawDeviceCode),
            UserCodeHash = _cryptoService.HashToken(NormalizeUserCode(userCode)),
            UserCode = userCode,
            ClientApplicationId = client.Id,
            Scope = string.Join(' ', requestedScopes),
            Resource = requestedResource,
            Status = PendingStatus,
            PollingIntervalSeconds = Math.Max(1, options.PollingIntervalSeconds),
            CreatedAt = now,
            ExpiresAt = now.Add(options.Lifetime),
            IpAddress = GetIp(httpContext),
            UserAgent = httpContext.Request.Headers.UserAgent.ToString()
        };

        _context.Set<SqlOSDeviceAuthorization>().Add(deviceAuthorization);
        await _context.SaveChangesAsync(cancellationToken);
        await _adminService.RecordAuditAsync(
            "oauth.device.started",
            "client",
            client.Id,
            ipAddress: GetIp(httpContext),
            cancellationToken: cancellationToken);

        var origin = SqlOSPublicOriginResolver.Resolve(_options);
        var basePath = _options.BasePath.TrimEnd('/');
        var verificationUri = $"{origin}{basePath}/device";
        var verificationUriComplete = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            verificationUri,
            "user_code",
            userCode);

        return new SqlOSDeviceAuthorizationStartResult(
            rawDeviceCode,
            userCode,
            verificationUri,
            verificationUriComplete,
            Math.Max(1, (int)(deviceAuthorization.ExpiresAt - now).TotalSeconds),
            deviceAuthorization.PollingIntervalSeconds);
    }

    public async Task<SqlOSDeviceAuthorizationResolveResult> ResolveAsync(
        string userCode,
        SqlOSUser? user = null,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var deviceAuthorization = await GetRequiredActiveByUserCodeAsync(userCode, cancellationToken);
        IReadOnlyList<SqlOSOrganizationOption> organizations = user == null
            ? Array.Empty<SqlOSOrganizationOption>()
            : await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);

        return ToResolveResult(
            deviceAuthorization,
            organizations.Count > 1,
            organizations);
    }

    public async Task<SqlOSAuthorizationRequest> CreateOrGetAuthorizationRequestAsync(
        string userCode,
        string presentationMode,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var deviceAuthorization = await GetRequiredActiveByUserCodeAsync(userCode, cancellationToken);
        if (!string.Equals(deviceAuthorization.Status, PendingStatus, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("This device request is no longer pending.");
        }

        var existing = await _context.Set<SqlOSAuthorizationRequest>()
            .Include(x => x.ClientApplication)
            .FirstOrDefaultAsync(
                x => x.DeviceAuthorizationId == deviceAuthorization.Id
                    && x.CancelledAt == null
                    && x.CompletedAt == null
                    && x.ExpiresAt > DateTime.UtcNow,
                cancellationToken);
        if (existing != null)
        {
            return existing;
        }

        var normalizedPresentationMode = string.Equals(presentationMode, "headless", StringComparison.OrdinalIgnoreCase)
            ? "headless"
            : "hosted";
        var authorizationRequest = new SqlOSAuthorizationRequest
        {
            Id = _cryptoService.GenerateId("req"),
            ClientApplicationId = deviceAuthorization.ClientApplicationId,
            DeviceAuthorizationId = deviceAuthorization.Id,
            PresentationMode = normalizedPresentationMode,
            RedirectUri = string.Empty,
            State = _cryptoService.GenerateOpaqueToken(),
            Scope = deviceAuthorization.Scope,
            Resource = deviceAuthorization.Resource,
            CodeChallenge = string.Empty,
            CodeChallengeMethod = "S256",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = deviceAuthorization.ExpiresAt,
            ClientApplication = deviceAuthorization.ClientApplication
        };

        _context.Set<SqlOSAuthorizationRequest>().Add(authorizationRequest);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (_context is DbContext dbContext)
            {
                dbContext.ChangeTracker.Clear();
            }

            var raced = await _context.Set<SqlOSAuthorizationRequest>()
                .Include(x => x.ClientApplication)
                .FirstOrDefaultAsync(
                    x => x.DeviceAuthorizationId == deviceAuthorization.Id
                        && x.CancelledAt == null
                        && x.CompletedAt == null
                        && x.ExpiresAt > DateTime.UtcNow,
                    cancellationToken);
            if (raced != null)
            {
                return raced;
            }

            throw;
        }

        return authorizationRequest;
    }

    public async Task<SqlOSDeviceAuthorizationResolveResult> ResolveAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSUser? user = null,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var deviceAuthorization = await GetRequiredByAuthorizationRequestAsync(authorizationRequest, cancellationToken);
        IReadOnlyList<SqlOSOrganizationOption> organizations = user == null
            ? Array.Empty<SqlOSOrganizationOption>()
            : await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);

        return ToResolveResult(
            deviceAuthorization,
            organizations.Count > 1 && string.IsNullOrWhiteSpace(authorizationRequest.ResolvedOrganizationId),
            organizations);
    }

    public async Task<SqlOSDeviceAuthorizationResolveResult> ApproveAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSUser user,
        string authenticationMethod,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ApproveAsync(
            new SqlOSDeviceAuthorizationApprovalRequest(
                (await GetRequiredByAuthorizationRequestAsync(authorizationRequest, cancellationToken)).UserCode,
                authorizationRequest.ResolvedOrganizationId),
            user,
            authenticationMethod,
            httpContext,
            cancellationToken);
        if (!resolved.RequiresOrganizationSelection)
        {
            authorizationRequest.CompletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
        }

        return resolved;
    }

    public async Task<SqlOSDeviceAuthorizationResolveResult> ApproveAsync(
        SqlOSDeviceAuthorizationApprovalRequest request,
        SqlOSUser user,
        string authenticationMethod,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var deviceAuthorization = await GetRequiredActiveByUserCodeAsync(request.UserCode, cancellationToken);
        if (!string.Equals(deviceAuthorization.Status, PendingStatus, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("This device request is no longer pending.");
        }

        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);
        var organizationId = request.OrganizationId;
        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            if (!organizations.Any(x => string.Equals(x.Id, organizationId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("The selected organization is not available to this user.");
            }
        }
        else if (organizations.Count == 1)
        {
            organizationId = organizations[0].Id;
        }
        else if (organizations.Count > 1)
        {
            return ToResolveResult(deviceAuthorization, true, organizations);
        }

        await _adminService.EnsureApplicationAccessAsync(
            deviceAuthorization.ClientApplication!,
            user.Id,
            organizationId,
            "application.access.device_denied",
            GetIp(httpContext),
            cancellationToken);

        deviceAuthorization.Status = ApprovedStatus;
        deviceAuthorization.ApprovedUserId = user.Id;
        deviceAuthorization.ApprovedOrganizationId = organizationId;
        deviceAuthorization.AuthenticationMethod = authenticationMethod;
        deviceAuthorization.ApprovedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        await _adminService.RecordAuditAsync(
            "oauth.device.approved",
            "user",
            user.Id,
            userId: user.Id,
            organizationId: organizationId,
            ipAddress: GetIp(httpContext),
            cancellationToken: cancellationToken);

        return ToResolveResult(deviceAuthorization, false, organizations);
    }

    public async Task DenyAsync(
        string userCode,
        SqlOSUser? user,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var deviceAuthorization = await GetRequiredActiveByUserCodeAsync(userCode, cancellationToken);
        if (!string.Equals(deviceAuthorization.Status, PendingStatus, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("This device request is no longer pending.");
        }

        deviceAuthorization.Status = DeniedStatus;
        deviceAuthorization.DeniedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        await _adminService.RecordAuditAsync(
            "oauth.device.denied",
            user == null ? "anonymous" : "user",
            user?.Id,
            userId: user?.Id,
            ipAddress: GetIp(httpContext),
            cancellationToken: cancellationToken);
    }

    public async Task<SqlOSDeviceTokenPollResult> PollAsync(
        SqlOSDeviceTokenPollRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.DeviceCode))
        {
            throw new SqlOSDeviceAuthorizationException("invalid_request", "client_id and device_code are required.");
        }

        var client = await _adminService.RequireClientAsync(request.ClientId, null, cancellationToken, httpContext);
        EnsureClientAllowsDeviceAuthorization(client);

        var deviceCodeHash = _cryptoService.HashToken(request.DeviceCode);
        var deviceAuthorization = await _context.Set<SqlOSDeviceAuthorization>()
            .Include(x => x.ClientApplication)
            .Include(x => x.ApprovedUser)
            .FirstOrDefaultAsync(x => x.DeviceCodeHash == deviceCodeHash, cancellationToken)
            ?? throw new SqlOSDeviceAuthorizationException("invalid_grant", "Device code is invalid.");

        if (!string.Equals(deviceAuthorization.ClientApplicationId, client.Id, StringComparison.Ordinal))
        {
            throw new SqlOSDeviceAuthorizationException("invalid_grant", "Device code was not issued for this client.");
        }

        if (deviceAuthorization.ConsumedAt != null)
        {
            throw new SqlOSDeviceAuthorizationException("invalid_grant", "Device code has already been consumed.");
        }

        if (deviceAuthorization.ExpiresAt <= DateTime.UtcNow)
        {
            throw new SqlOSDeviceAuthorizationException("expired_token", "Device code has expired.");
        }

        var requestedResource = _options.ResourceIndicators.Enabled && !string.IsNullOrWhiteSpace(request.Resource)
            ? request.Resource.Trim()
            : null;
        if (!string.IsNullOrWhiteSpace(deviceAuthorization.Resource))
        {
            if (!string.Equals(deviceAuthorization.Resource, requestedResource, StringComparison.Ordinal))
            {
                throw new SqlOSDeviceAuthorizationException("invalid_grant", "Resource does not match the device authorization request.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(requestedResource))
        {
            throw new SqlOSDeviceAuthorizationException("invalid_grant", "Resource cannot be introduced during token polling.");
        }

        if (string.Equals(deviceAuthorization.Status, DeniedStatus, StringComparison.Ordinal))
        {
            throw new SqlOSDeviceAuthorizationException("access_denied", "The user denied this device authorization request.");
        }

        if (!string.Equals(deviceAuthorization.Status, ApprovedStatus, StringComparison.Ordinal))
        {
            await RecordPollAsync(deviceAuthorization, httpContext, cancellationToken);
            throw new SqlOSDeviceAuthorizationException("authorization_pending", "The device authorization request is still pending.", deviceAuthorization.PollingIntervalSeconds);
        }

        if (deviceAuthorization.ApprovedUser == null)
        {
            throw new SqlOSDeviceAuthorizationException("invalid_grant", "Approved user is no longer available.");
        }

        deviceAuthorization.ConsumedAt = DateTime.UtcNow;
        deviceAuthorization.PollCount++;
        deviceAuthorization.LastPolledAt = DateTime.UtcNow;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new SqlOSDeviceAuthorizationException("invalid_grant", "Device code has already been consumed.");
        }

        var tokens = await _authService.CreateSessionTokensForUserAsync(
            deviceAuthorization.ApprovedUser,
            client,
            deviceAuthorization.ApprovedOrganizationId,
            deviceAuthorization.AuthenticationMethod ?? "device",
            httpContext.Request.Headers.UserAgent.ToString(),
            GetIp(httpContext),
            deviceAuthorization.Resource,
            cancellationToken);

        await _adminService.RecordAuditAsync(
            "oauth.device.token-issued",
            "client",
            client.Id,
            userId: deviceAuthorization.ApprovedUserId,
            organizationId: deviceAuthorization.ApprovedOrganizationId,
            ipAddress: GetIp(httpContext),
            cancellationToken: cancellationToken);

        return new SqlOSDeviceTokenPollResult(tokens, deviceAuthorization.Scope);
    }

    private async Task RecordPollAsync(SqlOSDeviceAuthorization deviceAuthorization, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (deviceAuthorization.LastPolledAt.HasValue
            && deviceAuthorization.LastPolledAt.Value.AddSeconds(deviceAuthorization.PollingIntervalSeconds) > now)
        {
            deviceAuthorization.SlowDownCount++;
            deviceAuthorization.PollingIntervalSeconds += 5;
            deviceAuthorization.LastPolledAt = now;
            deviceAuthorization.PollCount++;
            await _context.SaveChangesAsync(cancellationToken);
            await _adminService.RecordAuditAsync(
                "oauth.device.poll-slow-down",
                "client",
                deviceAuthorization.ClientApplicationId,
                ipAddress: GetIp(httpContext),
                cancellationToken: cancellationToken);
            throw new SqlOSDeviceAuthorizationException("slow_down", "Polling is too frequent.", deviceAuthorization.PollingIntervalSeconds);
        }

        deviceAuthorization.LastPolledAt = now;
        deviceAuthorization.PollCount++;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task EnforceStartRateLimitsAsync(string clientApplicationId, string? ipAddress, CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow.AddHours(-1);
        var options = _options.DeviceAuthorization;
        var clientCount = await _context.Set<SqlOSDeviceAuthorization>()
            .CountAsync(x => x.ClientApplicationId == clientApplicationId && x.CreatedAt >= since, cancellationToken);
        if (clientCount >= options.MaxStartsPerClientPerHour)
        {
            throw new SqlOSDeviceAuthorizationException("slow_down", "Too many device authorization requests for this client.");
        }

        if (!string.IsNullOrWhiteSpace(ipAddress))
        {
            var ipCount = await _context.Set<SqlOSDeviceAuthorization>()
                .CountAsync(x => x.IpAddress == ipAddress && x.CreatedAt >= since, cancellationToken);
            if (ipCount >= options.MaxStartsPerIpPerHour)
            {
                throw new SqlOSDeviceAuthorizationException("slow_down", "Too many device authorization requests from this IP address.");
            }
        }
    }

    private async Task<SqlOSDeviceAuthorization> GetRequiredActiveByUserCodeAsync(string userCode, CancellationToken cancellationToken)
    {
        var normalized = NormalizeUserCode(userCode);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Enter the device code shown in your CLI.");
        }

        var hash = _cryptoService.HashToken(normalized);
        var deviceAuthorization = await _context.Set<SqlOSDeviceAuthorization>()
            .Include(x => x.ClientApplication)
            .FirstOrDefaultAsync(x => x.UserCodeHash == hash, cancellationToken)
            ?? throw new InvalidOperationException("Device code is invalid or expired.");

        if (deviceAuthorization.ExpiresAt <= DateTime.UtcNow || deviceAuthorization.ConsumedAt != null)
        {
            throw new InvalidOperationException("Device code is invalid or expired.");
        }

        return deviceAuthorization;
    }

    private async Task<SqlOSDeviceAuthorization> GetRequiredByAuthorizationRequestAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authorizationRequest.DeviceAuthorizationId))
        {
            throw new InvalidOperationException("Authorization request is not bound to a device request.");
        }

        var deviceAuthorization = await _context.Set<SqlOSDeviceAuthorization>()
            .Include(x => x.ClientApplication)
            .FirstOrDefaultAsync(x => x.Id == authorizationRequest.DeviceAuthorizationId, cancellationToken)
            ?? throw new InvalidOperationException("Device request is invalid or expired.");

        if (deviceAuthorization.ExpiresAt <= DateTime.UtcNow || deviceAuthorization.ConsumedAt != null)
        {
            throw new InvalidOperationException("Device request is invalid or expired.");
        }

        return deviceAuthorization;
    }

    private async Task<string> GenerateUniqueUserCodeAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var code = GenerateUserCode();
            var hash = _cryptoService.HashToken(NormalizeUserCode(code));
            var exists = await _context.Set<SqlOSDeviceAuthorization>()
                .AnyAsync(x => x.UserCodeHash == hash && x.ExpiresAt > DateTime.UtcNow && x.ConsumedAt == null, cancellationToken);
            if (!exists)
            {
                return code;
            }
        }

        throw new InvalidOperationException("Unable to allocate a device code.");
    }

    private string GenerateUserCode()
    {
        var options = _options.DeviceAuthorization;
        var alphabet = string.IsNullOrWhiteSpace(options.UserCodeAlphabet)
            ? "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
            : options.UserCodeAlphabet;
        var segmentLength = Math.Max(2, options.UserCodeSegmentLength);
        var segmentCount = Math.Max(1, options.UserCodeSegmentCount);
        var segments = new string[segmentCount];

        for (var segment = 0; segment < segmentCount; segment++)
        {
            var chars = new char[segmentLength];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            }

            segments[segment] = new string(chars);
        }

        return string.Join('-', segments);
    }

    private void EnsureEnabled()
    {
        if (!_options.DeviceAuthorization.Enabled)
        {
            throw new SqlOSDeviceAuthorizationException("unsupported_grant_type", "Device authorization is disabled.");
        }
    }

    private static void EnsureClientAllowsDeviceAuthorization(SqlOSClientApplication client)
    {
        var grants = SqlOSAdminService.DeserializeJsonList(client.GrantTypesJson);
        if (!client.AllowDeviceAuthorization || !grants.Contains(SqlOSOAuthGrantTypes.DeviceCode, StringComparer.Ordinal))
        {
            throw new SqlOSDeviceAuthorizationException("unauthorized_client", "This client is not allowed to use device authorization.");
        }

        if (!string.Equals(client.TokenEndpointAuthMethod, "none", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlOSDeviceAuthorizationException("invalid_client", "Device authorization requires a public client.");
        }
    }

    private static SqlOSDeviceAuthorizationResolveResult ToResolveResult(
        SqlOSDeviceAuthorization deviceAuthorization,
        bool requiresOrganizationSelection,
        IReadOnlyList<SqlOSOrganizationOption> organizations)
        => new(
            deviceAuthorization.Id,
            deviceAuthorization.UserCode,
            deviceAuthorization.ClientApplication?.ClientId ?? string.Empty,
            deviceAuthorization.ClientApplication?.Name ?? "CLI application",
            deviceAuthorization.Scope,
            deviceAuthorization.Resource,
            deviceAuthorization.ExpiresAt,
            deviceAuthorization.Status,
            requiresOrganizationSelection,
            organizations);

    private static List<string> NormalizeRequestedScopes(string? scope)
        => string.IsNullOrWhiteSpace(scope)
            ? []
            : scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();

    public static string NormalizeUserCode(string? code)
        => string.IsNullOrWhiteSpace(code)
            ? string.Empty
            : new string(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string? GetIp(HttpContext httpContext) => httpContext.Connection.RemoteIpAddress?.ToString();
}

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSDynamicClientRegistrationService
{
    private static readonly string[] AllowedGrantTypes = ["authorization_code", "refresh_token"];
    private static readonly string[] AllowedResponseTypes = ["code"];

    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAuthServerOptions _options;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSDynamicClientRegistrationRateLimiter _rateLimiter;

    public SqlOSDynamicClientRegistrationService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSCryptoService cryptoService,
        SqlOSAdminService adminService,
        SqlOSDynamicClientRegistrationRateLimiter rateLimiter)
    {
        _context = context;
        _options = options.Value;
        _cryptoService = cryptoService;
        _adminService = adminService;
        _rateLimiter = rateLimiter;
    }

    public async Task<SqlOSDynamicClientRegistrationResponse> RegisterAsync(
        SqlOSDynamicClientRegistrationRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_options.ClientRegistration.Dcr.Enabled)
            {
                throw new SqlOSClientRegistrationException("unsupported_operation", "Dynamic client registration is disabled.");
            }

            EnforceRateLimit(httpContext);

            var normalized = await NormalizeRequestAsync(request, httpContext, cancellationToken);
            var clientId = await GenerateUniqueClientIdAsync(cancellationToken);
            var now = DateTime.UtcNow;

            var client = new SqlOSClientApplication
            {
                Id = _cryptoService.GenerateId("cli"),
                ClientId = clientId,
                Name = normalized.ClientName,
                Audience = _options.DefaultAudience,
                ClientType = "public_pkce",
                RegistrationSource = "dcr",
                TokenEndpointAuthMethod = normalized.TokenEndpointAuthMethod,
                GrantTypesJson = JsonSerializer.Serialize(normalized.GrantTypes),
                ResponseTypesJson = JsonSerializer.Serialize(normalized.ResponseTypes),
                RequirePkce = _options.ClientRegistration.Dcr.RequirePkce,
                AllowedScopesJson = "[]",
                IsFirstParty = false,
                RedirectUrisJson = JsonSerializer.Serialize(normalized.RedirectUris),
                MetadataJson = JsonSerializer.Serialize(request),
                ClientUri = normalized.ClientUri,
                LogoUri = normalized.LogoUri,
                SoftwareId = normalized.SoftwareId,
                SoftwareVersion = normalized.SoftwareVersion,
                CreatedAt = now,
                IsActive = true
            };

            _context.Set<SqlOSClientApplication>().Add(client);
            await _context.SaveChangesAsync(cancellationToken);

            await _adminService.RecordAuditAsync(
                "client.dcr.registered",
                "client",
                clientId,
                ipAddress: GetIpAddress(httpContext),
                data: new
                {
                    client_id = clientId,
                    client_name = normalized.ClientName,
                    redirect_uris = normalized.RedirectUris,
                    grant_types = normalized.GrantTypes,
                    response_types = normalized.ResponseTypes,
                    token_endpoint_auth_method = normalized.TokenEndpointAuthMethod,
                    software_id = normalized.SoftwareId,
                    software_version = normalized.SoftwareVersion
                },
                cancellationToken: cancellationToken);

            return new SqlOSDynamicClientRegistrationResponse
            {
                ClientId = clientId,
                ClientIdIssuedAt = new DateTimeOffset(now).ToUnixTimeSeconds(),
                ClientName = normalized.ClientName,
                RedirectUris = normalized.RedirectUris.ToArray(),
                GrantTypes = normalized.GrantTypes.ToArray(),
                ResponseTypes = normalized.ResponseTypes.ToArray(),
                TokenEndpointAuthMethod = normalized.TokenEndpointAuthMethod,
                ClientUri = normalized.ClientUri,
                LogoUri = normalized.LogoUri,
                SoftwareId = normalized.SoftwareId,
                SoftwareVersion = normalized.SoftwareVersion
            };
        }
        catch (SqlOSClientRegistrationException ex)
        {
            await _adminService.RecordAuditAsync(
                "client.dcr.rejected",
                "system",
                null,
                ipAddress: GetIpAddress(httpContext),
                data: new
                {
                    error = ex.Error,
                    error_description = ex.Message,
                    client_name = request.ClientName,
                    redirect_uris = request.RedirectUris,
                    grant_types = request.GrantTypes,
                    response_types = request.ResponseTypes,
                    token_endpoint_auth_method = request.TokenEndpointAuthMethod,
                    software_id = request.SoftwareId,
                    software_version = request.SoftwareVersion
                },
                cancellationToken: cancellationToken);
            throw;
        }
    }

    private void EnforceRateLimit(HttpContext httpContext)
    {
        var key = GetIpAddress(httpContext) ?? "unknown";
        if (_rateLimiter.TryConsume(
                key,
                _options.ClientRegistration.Dcr.RateLimitWindow,
                _options.ClientRegistration.Dcr.MaxRegistrationsPerWindow))
        {
            return;
        }

        throw new SqlOSClientRegistrationException(
            "slow_down",
            "Dynamic client registration is temporarily rate limited.",
            StatusCodes.Status429TooManyRequests);
    }

    private async Task<NormalizedDcrRequest> NormalizeRequestAsync(
        SqlOSDynamicClientRegistrationRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.ClientSecret) || request.ClientSecretExpiresAt.HasValue)
        {
            throw new SqlOSClientRegistrationException(
                "invalid_client_metadata",
                "Confidential client secrets are not supported for dynamic client registration.");
        }

        var redirectUris = NormalizeRedirectUris(request.RedirectUris);
        var grantTypes = NormalizeGrantTypes(request.GrantTypes);
        var responseTypes = NormalizeResponseTypes(request.ResponseTypes);
        var tokenEndpointAuthMethod = string.IsNullOrWhiteSpace(request.TokenEndpointAuthMethod)
            ? "none"
            : request.TokenEndpointAuthMethod.Trim();

        if (!string.Equals(tokenEndpointAuthMethod, "none", StringComparison.Ordinal))
        {
            throw new SqlOSClientRegistrationException(
                "invalid_client_metadata",
                "token_endpoint_auth_method must be 'none' for dynamic client registration in this SqlOS version.");
        }

        var clientName = string.IsNullOrWhiteSpace(request.ClientName)
            ? "Registered client"
            : request.ClientName.Trim();
        var clientUri = NormalizeOptionalUrl(request.ClientUri, nameof(request.ClientUri));
        var logoUri = NormalizeOptionalUrl(request.LogoUri, nameof(request.LogoUri));
        var softwareId = NormalizeOptionalText(request.SoftwareId);
        var softwareVersion = NormalizeOptionalText(request.SoftwareVersion);

        var policy = _options.ClientRegistration.Dcr.Policy;
        if (policy != null)
        {
            var decision = await policy(
                new SqlOSDynamicClientRegistrationPolicyContext
                {
                    HttpContext = httpContext,
                    ClientName = clientName,
                    RedirectUris = redirectUris,
                    GrantTypes = grantTypes,
                    ResponseTypes = responseTypes,
                    TokenEndpointAuthMethod = tokenEndpointAuthMethod,
                    SoftwareId = softwareId,
                    SoftwareVersion = softwareVersion
                },
                cancellationToken);

            if (!decision.Allowed)
            {
                throw new SqlOSClientRegistrationException(
                    "invalid_client_metadata",
                    decision.Reason ?? "Dynamic client registration was rejected by policy.");
            }
        }

        return new NormalizedDcrRequest(
            clientName,
            redirectUris,
            grantTypes,
            responseTypes,
            tokenEndpointAuthMethod,
            clientUri,
            logoUri,
            softwareId,
            softwareVersion);
    }

    private List<string> NormalizeRedirectUris(List<string> redirectUris)
    {
        var normalized = (redirectUris ?? [])
            .Where(static uri => !string.IsNullOrWhiteSpace(uri))
            .Select(static uri => uri.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
        {
            throw new SqlOSClientRegistrationException(
                "invalid_client_metadata",
                "At least one redirect_uri is required for dynamic client registration.");
        }

        foreach (var redirectUri in normalized)
        {
            if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
            {
                throw new SqlOSClientRegistrationException(
                    "invalid_redirect_uri",
                    $"Redirect URI '{redirectUri}' is not a valid absolute URI.");
            }

            var isHttps = _options.ClientRegistration.Dcr.AllowHttpsRedirectUris
                && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            var isLoopback = _options.ClientRegistration.Dcr.AllowLoopbackRedirectUris
                && (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase));

            if (!isHttps && !isLoopback)
            {
                throw new SqlOSClientRegistrationException(
                    "invalid_redirect_uri",
                    $"Redirect URI '{redirectUri}' must use https or a loopback localhost redirect.");
            }
        }

        return normalized;
    }

    private static List<string> NormalizeGrantTypes(List<string>? grantTypes)
    {
        var normalized = (grantTypes ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalized.Count == 0)
        {
            normalized = ["authorization_code", "refresh_token"];
        }

        if (!normalized.Contains("authorization_code", StringComparer.Ordinal))
        {
            throw new SqlOSClientRegistrationException(
                "invalid_client_metadata",
                "grant_types must include 'authorization_code'.");
        }

        if (normalized.Except(AllowedGrantTypes, StringComparer.Ordinal).Any())
        {
            throw new SqlOSClientRegistrationException(
                "invalid_client_metadata",
                "Only authorization_code and refresh_token grant types are supported.");
        }

        return normalized;
    }

    private static List<string> NormalizeResponseTypes(List<string>? responseTypes)
    {
        var normalized = (responseTypes ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalized.Count == 0)
        {
            normalized = ["code"];
        }

        if (normalized.Except(AllowedResponseTypes, StringComparer.Ordinal).Any())
        {
            throw new SqlOSClientRegistrationException(
                "invalid_client_metadata",
                "Only response_types = ['code'] are supported.");
        }

        return normalized;
    }

    private static string? NormalizeOptionalUrl(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            throw new SqlOSClientRegistrationException(
                "invalid_client_metadata",
                $"{name} must be an absolute URI when supplied.");
        }

        return trimmed;
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<string> GenerateUniqueClientIdAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var clientId = _cryptoService.GenerateId("dcrcli");
            if (!await _context.Set<SqlOSClientApplication>().AnyAsync(x => x.ClientId == clientId, cancellationToken))
            {
                return clientId;
            }
        }
    }

    private static string? GetIpAddress(HttpContext httpContext)
        => httpContext.Connection.RemoteIpAddress?.ToString();

    private sealed record NormalizedDcrRequest(
        string ClientName,
        List<string> RedirectUris,
        List<string> GrantTypes,
        List<string> ResponseTypes,
        string TokenEndpointAuthMethod,
        string? ClientUri,
        string? LogoUri,
        string? SoftwareId,
        string? SoftwareVersion);
}

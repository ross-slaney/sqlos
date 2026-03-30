using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSAuthorizationServerService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSAuthService _authService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly SqlOSAuthPageSessionService _authPageSessionService;
    private readonly SqlOSAuthServerOptions _options;

    public SqlOSAuthorizationServerService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSAuthService authService,
        SqlOSCryptoService cryptoService,
        SqlOSSettingsService settingsService,
        SqlOSAuthPageSessionService authPageSessionService,
        IOptions<SqlOSAuthServerOptions> options)
    {
        _context = context;
        _adminService = adminService;
        _authService = authService;
        _cryptoService = cryptoService;
        _settingsService = settingsService;
        _authPageSessionService = authPageSessionService;
        _options = options.Value;
    }

    public async Task<SqlOSAuthorizationServerMetadataDto> GetMetadataAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var authPageSettings = await _settingsService.GetAuthPageSettingsAsync(cancellationToken);
        var configuredScopes = await _context.Set<SqlOSClientApplication>()
            .AsNoTracking()
            .Select(x => x.AllowedScopesJson)
            .ToListAsync(cancellationToken);

        var scopes = configuredScopes
            .SelectMany(ParseJsonArray)
            .Concat(authPageSettings.EnabledCredentialTypes.Select(x => $"auth:{x}"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var origin = GetPublicOrigin(httpContext);
        var basePath = _options.BasePath.TrimEnd('/');

        return new SqlOSAuthorizationServerMetadataDto
        {
            Issuer = _options.Issuer,
            AuthorizationEndpoint = $"{origin}{basePath}/authorize",
            TokenEndpoint = $"{origin}{basePath}/token",
            JwksUri = $"{origin}{basePath}/.well-known/jwks.json",
            ResponseTypesSupported = ["code"],
            GrantTypesSupported = ["authorization_code", "refresh_token"],
            CodeChallengeMethodsSupported = ["S256"],
            ScopesSupported = scopes,
            TokenEndpointAuthMethodsSupported = ["none"],
            RegistrationEndpoint = _options.ClientRegistration.Dcr.Enabled
                ? $"{origin}{basePath}/register"
                : null,
            ClientIdMetadataDocumentSupported = _options.ClientRegistration.Cimd.Enabled
                ? true
                : null,
            ResourceParameterSupported = _options.ResourceIndicators.Enabled
                ? true
                : null
        };
    }

    public async Task<SqlOSAuthorizationRequest> CreateAuthorizationRequestAsync(
        SqlOSAuthorizeRequestInput input,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(input.ResponseType, "code", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only authorization code requests are supported.");
        }

        if (string.IsNullOrWhiteSpace(input.State))
        {
            throw new InvalidOperationException("A state value is required.");
        }

        var client = await _adminService.RequireClientAsync(input.ClientId, input.RedirectUri, cancellationToken);
        if (client.RequirePkce)
        {
            if (string.IsNullOrWhiteSpace(input.CodeChallenge))
            {
                throw new InvalidOperationException("A PKCE code challenge is required.");
            }

            if (!string.Equals(input.CodeChallengeMethod, "S256", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Only S256 PKCE is supported.");
            }
        }

        var requestedScopes = NormalizeRequestedScopes(input.Scope);
        var allowedScopes = ParseJsonArray(client.AllowedScopesJson);
        if (allowedScopes.Count > 0)
        {
            requestedScopes = requestedScopes
                .Where(scope => allowedScopes.Contains(scope, StringComparer.Ordinal))
                .ToList();
        }

        var normalizedResource = _options.ResourceIndicators.Enabled && !string.IsNullOrWhiteSpace(input.Resource)
            ? input.Resource.Trim()
            : null;

        var authorizationRequest = new SqlOSAuthorizationRequest
        {
            Id = _cryptoService.GenerateId("req"),
            ClientApplicationId = client.Id,
            PresentationMode = string.Equals(input.PresentationMode, "headless", StringComparison.OrdinalIgnoreCase)
                ? "headless"
                : "hosted",
            RedirectUri = input.RedirectUri,
            State = input.State,
            Scope = string.Join(' ', requestedScopes),
            Resource = normalizedResource,
            Nonce = input.Nonce,
            Prompt = input.Prompt,
            LoginHintEmail = input.LoginHint,
            UiContextJson = SqlOSHeadlessAuthService.NormalizeUiContext(input.UiContextJson),
            CodeChallenge = input.CodeChallenge ?? string.Empty,
            CodeChallengeMethod = input.CodeChallengeMethod ?? "S256",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };

        _context.Set<SqlOSAuthorizationRequest>().Add(authorizationRequest);
        await _context.SaveChangesAsync(cancellationToken);
        return authorizationRequest;
    }

    public async Task<SqlOSAuthorizationRequest?> TryGetActiveAuthorizationRequestAsync(string? authorizationRequestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authorizationRequestId))
        {
            return null;
        }

        return await _context.Set<SqlOSAuthorizationRequest>()
            .Include(x => x.ClientApplication)
            .FirstOrDefaultAsync(
                x => x.Id == authorizationRequestId
                    && x.CancelledAt == null
                    && x.CompletedAt == null
                    && x.ExpiresAt > DateTime.UtcNow,
                cancellationToken);
    }

    public async Task<SqlOSAuthorizationRequest> GetRequiredAuthorizationRequestAsync(string authorizationRequestId, CancellationToken cancellationToken = default)
        => await TryGetActiveAuthorizationRequestAsync(authorizationRequestId, cancellationToken)
            ?? throw new InvalidOperationException("Authorization request is invalid or expired.");

    public async Task<string> BuildAuthorizationErrorRedirectAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        string error,
        string? errorDescription,
        CancellationToken cancellationToken = default)
    {
        if (authorizationRequest.CompletedAt == null && authorizationRequest.CancelledAt == null)
        {
            authorizationRequest.CancelledAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
        }

        var query = new Dictionary<string, string?>
        {
            ["error"] = error,
            ["state"] = authorizationRequest.State
        };

        if (!string.IsNullOrWhiteSpace(errorDescription))
        {
            query["error_description"] = errorDescription;
        }

        return QueryHelpers.AddQueryString(authorizationRequest.RedirectUri, query);
    }

    public async Task<SqlOSPasswordAuthenticationResult> AuthenticatePasswordAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = SqlOSAdminService.NormalizeEmail(email);
        var emailRecord = await _context.Set<SqlOSUserEmail>()
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken)
            ?? throw new InvalidOperationException("Invalid email or password.");

        var credential = await _context.Set<SqlOSCredential>()
            .FirstOrDefaultAsync(x => x.UserId == emailRecord.UserId && x.Type == "password" && x.RevokedAt == null, cancellationToken)
            ?? throw new InvalidOperationException("Invalid email or password.");

        if (!_cryptoService.VerifyPassword(credential.SecretHash, password))
        {
            throw new InvalidOperationException("Invalid email or password.");
        }

        credential.LastUsedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        var user = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == emailRecord.UserId, cancellationToken);
        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);
        return new SqlOSPasswordAuthenticationResult(user, organizations, "password");
    }

    public async Task<SqlOSPasswordAuthenticationResult> SignUpAsync(
        string displayName,
        string email,
        string password,
        string? organizationName,
        string? organizationId,
        CancellationToken cancellationToken = default)
    {
        var user = await _adminService.CreateUserAsync(
            new SqlOSCreateUserRequest(displayName, email, password),
            cancellationToken);

        var selectedOrganizationId = organizationId;
        if (!string.IsNullOrWhiteSpace(organizationName))
        {
            var createdOrganization = await _adminService.CreateOrganizationAsync(
                new SqlOSCreateOrganizationRequest(organizationName, null),
                cancellationToken);
            selectedOrganizationId = createdOrganization.Id;
            await _adminService.CreateMembershipAsync(createdOrganization.Id, new SqlOSCreateMembershipRequest(user.Id, "owner"), cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(organizationId))
        {
            await _adminService.CreateMembershipAsync(organizationId, new SqlOSCreateMembershipRequest(user.Id, "member"), cancellationToken);
        }

        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);
        if (!string.IsNullOrWhiteSpace(selectedOrganizationId) && organizations.All(x => x.Id != selectedOrganizationId))
        {
            organizations = organizations
                .Concat([new SqlOSOrganizationOption(selectedOrganizationId, selectedOrganizationId, selectedOrganizationId, "member")])
                .ToList();
        }

        return new SqlOSPasswordAuthenticationResult(user, organizations, "password");
    }

    public async Task<string> CreatePendingOrganizationSelectionAsync(
        SqlOSUser user,
        SqlOSAuthorizationRequest authorizationRequest,
        string authenticationMethod,
        CancellationToken cancellationToken = default)
    {
        return await _cryptoService.CreateTemporaryTokenAsync(
            "auth_page_pending",
            user.Id,
            authorizationRequest.ClientApplicationId,
            null,
            new PendingAuthorizationPayload(authorizationRequest.Id, authenticationMethod),
            TimeSpan.FromMinutes(10),
            cancellationToken);
    }

    public async Task<string> CompletePendingOrganizationSelectionAsync(
        string pendingToken,
        string organizationId,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var temporaryToken = await _cryptoService.ConsumeTemporaryTokenAsync("auth_page_pending", pendingToken, cancellationToken)
            ?? throw new InvalidOperationException("The organization selection session is invalid or expired.");
        if (temporaryToken.UserId == null)
        {
            throw new InvalidOperationException("The organization selection session is invalid.");
        }

        var payload = _cryptoService.DeserializePayload<PendingAuthorizationPayload>(temporaryToken)
            ?? throw new InvalidOperationException("The organization selection session payload is invalid.");
        var authorizationRequest = await GetRequiredAuthorizationRequestAsync(payload.AuthorizationRequestId, cancellationToken);
        if (!await _adminService.UserHasMembershipAsync(temporaryToken.UserId, organizationId, cancellationToken))
        {
            throw new InvalidOperationException("The selected organization is not available to this user.");
        }

        var user = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == temporaryToken.UserId, cancellationToken);
        return await IssueAuthorizationRedirectAsync(authorizationRequest, user, organizationId, payload.AuthenticationMethod, httpContext, cancellationToken);
    }

    public async Task<string> IssueAuthorizationRedirectAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSUser user,
        string? organizationId,
        string authenticationMethod,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var rawCode = _cryptoService.GenerateOpaqueToken();
        _context.Set<SqlOSAuthorizationCode>().Add(new SqlOSAuthorizationCode
        {
            Id = _cryptoService.GenerateId("acd"),
            AuthorizationRequestId = authorizationRequest.Id,
            UserId = user.Id,
            ClientApplicationId = authorizationRequest.ClientApplicationId,
            OrganizationId = organizationId,
            RedirectUri = authorizationRequest.RedirectUri,
            State = authorizationRequest.State,
            Scope = authorizationRequest.Scope,
            Resource = authorizationRequest.Resource,
            CodeHash = _cryptoService.HashToken(rawCode),
            CodeChallenge = authorizationRequest.CodeChallenge,
            CodeChallengeMethod = authorizationRequest.CodeChallengeMethod,
            AuthenticationMethod = authenticationMethod,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });

        authorizationRequest.CompletedAt = DateTime.UtcNow;
        authorizationRequest.ResolvedAuthMethod = authenticationMethod;
        authorizationRequest.ResolvedOrganizationId = organizationId;

        await _context.SaveChangesAsync(cancellationToken);
        await _authPageSessionService.SignInAsync(httpContext, user, organizationId, authenticationMethod, cancellationToken);

        var query = new Dictionary<string, string?>
        {
            ["code"] = rawCode,
            ["state"] = authorizationRequest.State
        };
        if (!string.IsNullOrWhiteSpace(authorizationRequest.Scope))
        {
            query["scope"] = authorizationRequest.Scope;
        }

        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(authorizationRequest.RedirectUri, query);
    }

    public async Task<SqlOSTokenEndpointResult> ExchangeAuthorizationCodeAsync(
        SqlOSTokenRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(request.GrantType, "refresh_token", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(request.RefreshToken))
            {
                throw new InvalidOperationException("A refresh token is required.");
            }

            var refreshResource = _options.ResourceIndicators.Enabled && !string.IsNullOrWhiteSpace(request.Resource)
                ? request.Resource.Trim()
                : null;
            var refreshed = await _authService.RefreshAsync(new SqlOSRefreshRequest(request.RefreshToken, null, refreshResource), cancellationToken);
            return new SqlOSTokenEndpointResult(refreshed, null);
        }

        if (!string.Equals(request.GrantType, "authorization_code", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unsupported grant type.");
        }

        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.ClientId))
        {
            throw new InvalidOperationException("The code and client_id parameters are required.");
        }

        var codeHash = _cryptoService.HashToken(request.Code);
        var authorizationCode = await _context.Set<SqlOSAuthorizationCode>()
            .Include(x => x.User)
            .Include(x => x.ClientApplication)
            .FirstOrDefaultAsync(x => x.CodeHash == codeHash, cancellationToken)
            ?? throw new InvalidOperationException("Authorization code is invalid.");

        if (authorizationCode.ConsumedAt != null || authorizationCode.ExpiresAt <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("Authorization code is no longer valid.");
        }

        if (!string.Equals(authorizationCode.ClientApplication?.ClientId, request.ClientId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Authorization code was not issued for this client.");
        }

        if (!string.IsNullOrWhiteSpace(request.RedirectUri)
            && !string.Equals(authorizationCode.RedirectUri, request.RedirectUri, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Redirect URI does not match the authorization request.");
        }

        var requestedResource = _options.ResourceIndicators.Enabled && !string.IsNullOrWhiteSpace(request.Resource)
            ? request.Resource.Trim()
            : null;
        if (!string.IsNullOrWhiteSpace(authorizationCode.Resource))
        {
            if (!string.Equals(authorizationCode.Resource, requestedResource, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Resource does not match the authorization request.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(requestedResource))
        {
            throw new InvalidOperationException("Resource cannot be introduced during token exchange.");
        }

        if (!_cryptoService.VerifyPkceCodeVerifier(request.CodeVerifier ?? string.Empty, authorizationCode.CodeChallenge, authorizationCode.CodeChallengeMethod))
        {
            throw new InvalidOperationException("PKCE verification failed.");
        }

        authorizationCode.ConsumedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        var tokens = await _authService.CreateSessionTokensForUserAsync(
            authorizationCode.User!,
            authorizationCode.ClientApplication!,
            authorizationCode.OrganizationId,
            authorizationCode.AuthenticationMethod,
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.Connection.RemoteIpAddress?.ToString(),
            authorizationCode.Resource,
            cancellationToken);

        return new SqlOSTokenEndpointResult(tokens, authorizationCode.Scope);
    }

    public async Task<IReadOnlyList<SqlOSOidcProviderSummary>> ListEnabledOidcProvidersAsync(CancellationToken cancellationToken = default)
    {
        var connections = await _context.Set<SqlOSOidcConnection>()
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);

        return connections
            .Select(x => new SqlOSOidcProviderSummary(
                x.Id,
                x.ProviderType.ToString(),
                x.DisplayName,
                x.IsEnabled,
                SqlOSOidcProviderLogoCatalog.ResolveEffectiveLogoDataUrl(x.ProviderType, x.LogoDataUrl)))
            .ToList();
    }

    public async Task<SqlOSAuthPageSettingsDto> GetAuthPageSettingsAsync(CancellationToken cancellationToken = default)
        => await _settingsService.GetAuthPageSettingsAsync(cancellationToken);

    public async Task<string?> ResolvePostLogoutRedirectAsync(HttpContext httpContext, string? requestedUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestedUrl))
        {
            return null;
        }

        if (Uri.TryCreate(requestedUrl, UriKind.Relative, out var relativeUri) && !relativeUri.IsAbsoluteUri)
        {
            var relativeValue = requestedUrl.Trim();
            return relativeValue.StartsWith("/", StringComparison.Ordinal) ? relativeValue : $"/{relativeValue}";
        }

        if (!Uri.TryCreate(requestedUrl, UriKind.Absolute, out var absoluteUri))
        {
            return null;
        }

        if (!string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            GetPublicOrigin(httpContext)
        };

        var configuredClientRedirectUris = await _context.Set<SqlOSClientApplication>()
            .AsNoTracking()
            .Select(x => x.RedirectUrisJson)
            .ToListAsync(cancellationToken);

        foreach (var redirectUri in configuredClientRedirectUris.SelectMany(ParseJsonArray))
        {
            if (Uri.TryCreate(redirectUri, UriKind.Absolute, out var parsedRedirectUri))
            {
                allowedOrigins.Add(parsedRedirectUri.GetLeftPart(UriPartial.Authority));
            }
        }

        var requestedOrigin = absoluteUri.GetLeftPart(UriPartial.Authority);
        return allowedOrigins.Contains(requestedOrigin) ? absoluteUri.ToString() : null;
    }

    public string GetPublicOrigin(HttpContext httpContext)
    {
        if (!string.IsNullOrWhiteSpace(_options.PublicOrigin))
        {
            return _options.PublicOrigin.TrimEnd('/');
        }

        return $"{httpContext.Request.Scheme}://{httpContext.Request.Host}".TrimEnd('/');
    }

    private static List<string> ParseJsonArray(string json)
        => JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();

    private static List<string> NormalizeRequestedScopes(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return new List<string>();
        }

        return scope
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private sealed record PendingAuthorizationPayload(string AuthorizationRequestId, string AuthenticationMethod);
}

public sealed record SqlOSAuthorizeRequestInput(
    string ResponseType,
    string ClientId,
    string RedirectUri,
    string State,
    string? Scope,
    string? CodeChallenge,
    string? CodeChallengeMethod,
    string? Resource,
    string? LoginHint,
    string? Prompt,
    string? Nonce,
    string? PresentationMode,
    string? UiContextJson);

public sealed record SqlOSPasswordAuthenticationResult(
    SqlOSUser User,
    IReadOnlyList<SqlOSOrganizationOption> Organizations,
    string AuthenticationMethod);

public sealed record SqlOSTokenRequest(
    string GrantType,
    string? Code,
    string? RedirectUri,
    string? ClientId,
    string? CodeVerifier,
    string? RefreshToken,
    string? Resource);

public sealed record SqlOSTokenEndpointResult(
    SqlOSTokenResponse Tokens,
    string? Scope);

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using SqlOS.Database;
using Microsoft.EntityFrameworkCore.Storage;
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
    private readonly SqlOSInvitationService? _invitationService;
    private readonly SqlOSPasswordLoginAbuseService _passwordLoginAbuseService;
    private readonly SqlOSMfaPolicyService _mfaPolicyService;
    private readonly SqlOSTotpMfaService? _totpMfaService;
    private readonly SqlOSConsentService _consentService;

    public SqlOSAuthorizationServerService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSAuthService authService,
        SqlOSCryptoService cryptoService,
        SqlOSSettingsService settingsService,
        SqlOSAuthPageSessionService authPageSessionService,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSInvitationService? invitationService = null,
        SqlOSPasswordLoginAbuseService? passwordLoginAbuseService = null,
        SqlOSMfaPolicyService? mfaPolicyService = null,
        SqlOSTotpMfaService? totpMfaService = null,
        SqlOSConsentService? consentService = null)
    {
        _context = context;
        _adminService = adminService;
        _authService = authService;
        _cryptoService = cryptoService;
        _settingsService = settingsService;
        _authPageSessionService = authPageSessionService;
        _options = options.Value;
        _invitationService = invitationService;
        _passwordLoginAbuseService = passwordLoginAbuseService
            ?? new SqlOSPasswordLoginAbuseService(context, adminService, cryptoService, options);
        _mfaPolicyService = mfaPolicyService ?? new SqlOSMfaPolicyService(context, settingsService, options);
        _totpMfaService = totpMfaService;
        _consentService = consentService ?? new SqlOSConsentService(context, cryptoService);
    }

    public async Task<SqlOSAuthorizationServerMetadataDto> GetMetadataAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var configuredScopes = await _context.Set<SqlOSClientApplication>()
            .AsNoTracking()
            .Select(x => x.AllowedScopesJson)
            .ToListAsync(cancellationToken);

        var openIdProviderEnabled = _options.OpenIdProvider.Enabled;
        var scopes = configuredScopes
            .SelectMany(ParseJsonArray)
            .Where(IsAdvertisedGrantableScope)
            .Concat(openIdProviderEnabled ? AdvertisedOpenIdScopes : [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var origin = GetPublicOrigin(httpContext);
        var basePath = _options.BasePath.TrimEnd('/');
        var grantTypes = new List<string>
        {
            SqlOSOAuthGrantTypes.AuthorizationCode,
            SqlOSOAuthGrantTypes.RefreshToken
        };
        if (_options.DeviceAuthorization.Enabled)
        {
            grantTypes.Add(SqlOSOAuthGrantTypes.DeviceCode);
        }
        var machineGrantConfigurations = await _context.Set<SqlOSClientApplication>()
            .AsNoTracking()
            .Where(x => x.IsActive
                && x.DisabledAt == null
                && x.ClientType == "confidential"
                && x.TokenEndpointAuthMethod == "client_secret_basic")
            .Select(x => x.GrantTypesJson)
            .ToListAsync(cancellationToken);
        if (machineGrantConfigurations.Any(json =>
            SqlOSAdminService.DeserializeJsonList(json)
                .Contains(SqlOSOAuthGrantTypes.ClientCredentials, StringComparer.Ordinal)))
        {
            grantTypes.Add(SqlOSOAuthGrantTypes.ClientCredentials);
        }
        var supportsClientSecretBasic = await _context.Set<SqlOSClientApplication>()
            .AsNoTracking()
            .AnyAsync(x => x.IsActive
                && x.DisabledAt == null
                && x.TokenEndpointAuthMethod == "client_secret_basic", cancellationToken);
        var supportsClientSecretPost = await _context.Set<SqlOSClientApplication>()
            .AsNoTracking()
            .AnyAsync(x => x.IsActive
                && x.DisabledAt == null
                && x.TokenEndpointAuthMethod == "client_secret_post", cancellationToken);
        var tokenEndpointAuthMethods = new List<string> { "none" };
        if (supportsClientSecretBasic)
        {
            tokenEndpointAuthMethods.Add("client_secret_basic");
        }
        if (supportsClientSecretPost)
        {
            tokenEndpointAuthMethods.Add("client_secret_post");
        }

        return new SqlOSAuthorizationServerMetadataDto
        {
            Issuer = _options.Issuer,
            AuthorizationEndpoint = $"{origin}{basePath}/authorize",
            TokenEndpoint = $"{origin}{basePath}/token",
            DeviceAuthorizationEndpoint = _options.DeviceAuthorization.Enabled
                ? $"{origin}{basePath}/device_authorization"
                : null,
            JwksUri = $"{origin}{basePath}/.well-known/jwks.json",
            ResponseTypesSupported = ["code"],
            // Always advertised: SqlOS constructs every authorization response in
            // the redirect query string, so the OIDC Discovery default of
            // query + fragment would be dishonest for OAuth and OIDC alike.
            ResponseModesSupported = ["query"],
            // Always emitted, always false: SqlOS does not support request objects.
            // OIDC Discovery defaults request_uri_parameter_supported to TRUE when
            // absent, so omitting the fields would advertise a lie.
            RequestParameterSupported = false,
            RequestUriParameterSupported = false,
            GrantTypesSupported = grantTypes.ToArray(),
            CodeChallengeMethodsSupported = ["S256"],
            ScopesSupported = scopes,
            TokenEndpointAuthMethodsSupported = tokenEndpointAuthMethods.ToArray(),
            RegistrationEndpoint = _options.ClientRegistration.Dcr.Enabled
                ? $"{origin}{basePath}/register"
                : null,
            ClientIdMetadataDocumentSupported = _options.ClientRegistration.Cimd.Enabled
                ? true
                : null,
            ResourceParameterSupported = _options.ResourceIndicators.Enabled
                ? true
                : null,
            UserInfoEndpoint = openIdProviderEnabled && _options.OpenIdProvider.EnableUserInfoEndpoint
                ? $"{origin}{basePath}/userinfo"
                : null,
            SubjectTypesSupported = openIdProviderEnabled ? ["public"] : null,
            IdTokenSigningAlgValuesSupported = openIdProviderEnabled ? ["RS256"] : null,
            ClaimsSupported = openIdProviderEnabled ? AdvertisedOpenIdClaims : null
        };
    }

    /// <summary>
    /// Scopes the OpenID Provider role itself understands. <c>offline_access</c> is
    /// deliberately absent: SqlOS does not gate refresh-token issuance on it, so
    /// advertising it would be dishonest. It remains storable on client allowlists
    /// for gateway compatibility.
    /// </summary>
    private static readonly string[] AdvertisedOpenIdScopes = ["openid", "profile", "email"];

    private static readonly string[] AdvertisedOpenIdClaims =
    [
        "sub",
        "name",
        "preferred_username",
        "email",
        "email_verified",
        "auth_time",
        "amr",
        "org_id"
    ];

    public async Task<SqlOSAuthorizationRequest> CreateAuthorizationRequestAsync(
        SqlOSAuthorizeRequestInput input,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(input.ResponseType, "code", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only authorization code requests are supported.");
        }

        // OIDC Core makes state optional (RECOMMENDED). PKCE binds the code exchange
        // for the flows SqlOS supports, so an absent state is accepted and simply not
        // echoed. SAML/SSO-broker flows keep their own state requirement. The length
        // limit applies to any present value — whitespace included — because the
        // NVARCHAR(2048) column would otherwise fail with a truncation error instead
        // of the protocol validation error.
        if (input.State is { Length: > 2048 })
        {
            throw new InvalidOperationException("State cannot exceed 2048 characters.");
        }

        // OIDC Core 3.1.2.1: "none" MUST NOT be used with any other prompt value.
        // Independently honoring both memberships would clear the session for
        // "none login" or proceed silently for "none consent" instead of failing.
        var promptValues = TokenizePrompt(input.Prompt);
        if (promptValues.Contains("none", StringComparer.Ordinal)
            && promptValues.Any(static value => !string.Equals(value, "none", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("prompt cannot combine none with other values.");
        }

        if (!TryParseMaxAge(input.MaxAge, out var maxAgeSeconds))
        {
            throw new InvalidOperationException("max_age must be a non-negative integer.");
        }

        var client = await _adminService.RequireClientAsync(input.ClientId, input.RedirectUri, cancellationToken);
        var isPublicClient = string.Equals(
            client.TokenEndpointAuthMethod,
            "none",
            StringComparison.Ordinal);
        if (client.RequirePkce || isPublicClient)
        {
            if (string.IsNullOrWhiteSpace(input.CodeChallenge))
            {
                throw new InvalidOperationException("A PKCE code challenge is required.");
            }

            if (!string.Equals(input.CodeChallengeMethod, "S256", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Only S256 PKCE is supported.");
            }

            if (!_cryptoService.IsValidS256PkceCodeChallenge(input.CodeChallenge))
            {
                throw new InvalidOperationException(
                    "PKCE code challenge must be a 43-character RFC 7636 S256 value.");
            }
        }

        var requestedScopes = SqlOSScopePolicy.Grant(input.Scope, client.AllowedScopesJson);

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
            MaxAgeSeconds = maxAgeSeconds,
            LoginHintEmail = input.LoginHint,
            UiContextJson = SqlOSHeadlessAuthService.NormalizeUiContext(input.UiContextJson),
            CodeChallenge = input.CodeChallenge ?? string.Empty,
            CodeChallengeMethod = input.CodeChallengeMethod ?? "S256",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            ClientApplication = client
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
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // A concurrent writer already completed or cancelled the request (the
                // CompletedAt/CancelledAt concurrency tokens make terminal writes mutually
                // exclusive). Surface the same safe already-inactive failure the issuance
                // path uses instead of a provider concurrency error.
                throw new InvalidOperationException("Authorization request is no longer active.", ex);
            }
        }

        var query = new Dictionary<string, string?>
        {
            ["error"] = error
        };
        if (!string.IsNullOrEmpty(authorizationRequest.State))
        {
            query["state"] = authorizationRequest.State;
        }

        if (!string.IsNullOrWhiteSpace(errorDescription))
        {
            query["error_description"] = errorDescription;
        }

        return QueryHelpers.AddQueryString(authorizationRequest.RedirectUri, query);
    }

    public async Task CancelAuthorizationInteractionAsync(
        SqlOSAuthorizationRequestLoginResult completion,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(completion.PendingToken))
        {
            _ = await _cryptoService.ConsumeTemporaryTokenAsync(
                "auth_page_pending",
                completion.PendingToken,
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(completion.MfaToken))
        {
            _ = await _cryptoService.ConsumeTemporaryTokenAsync(
                SqlOSAuthService.MfaChallengePurpose,
                completion.MfaToken,
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(completion.ConsentToken))
        {
            _ = await _cryptoService.ConsumeTemporaryTokenAsync(
                ConsentTokenPurpose,
                completion.ConsentToken,
                cancellationToken);
        }
    }

    /// <summary>
    /// Consumes a consent token, records the remembered grant (union of stored and granted
    /// scopes), audits <c>oauth.consent.granted</c>, and re-enters the completion flow so the
    /// organization and MFA interstitials still run after approval.
    /// </summary>
    public async Task<SqlOSAuthorizationRequestLoginResult> ApproveConsentAsync(
        string consentToken,
        string authorizationRequestId,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        // Validate without consuming first: precheck failures (stale metadata, scope-union
        // overflow) must not burn the one-time token the user would need for a retry.
        var token = await _cryptoService.FindTemporaryTokenAsync(ConsentTokenPurpose, consentToken, cancellationToken)
            ?? throw new InvalidOperationException("The consent session is invalid or expired.");
        var (payload, authorizationRequest) = await ValidateConsentTokenAsync(token, authorizationRequestId, cancellationToken);

        // The consent gate bound the request to the user it showed the interstitial to;
        // a token minted for anyone else (for example after an account switch) is invalid.
        if (authorizationRequest.PendingConsentUserId != null
            && !string.Equals(token.UserId, authorizationRequest.PendingConsentUserId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The consent session is not valid for this user.");
        }

        // Reject approvals whose consent screen no longer reflects the client's
        // security-sensitive metadata. Tokens minted before fingerprint stamping existed
        // carry null and are accepted; the 10-minute token lifetime bounds that exposure.
        var client = authorizationRequest.ClientApplication
            ?? await _context.Set<SqlOSClientApplication>()
                .FirstAsync(x => x.Id == authorizationRequest.ClientApplicationId, cancellationToken);
        var currentClientMetadataFingerprint = SqlOSCimdClientService.ComputeSensitiveMetadataFingerprint(client);
        if (payload.ClientMetadataFingerprint != null
            && !string.Equals(
                payload.ClientMetadataFingerprint,
                currentClientMetadataFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(ConsentClientMetadataChangedMessage);
        }

        var grantedScopes = SqlOSScopePolicy.Split(authorizationRequest.Scope);
        // Deterministic overflow precheck before the one-time token is consumed, so an
        // oversized union rejects with the token still usable.
        SqlOSConsentService.EnsureUnionWithinLimits(
            await _consentService.GetActiveGrantScopeAsync(
                token.UserId!,
                authorizationRequest.ClientApplicationId,
                cancellationToken),
            grantedScopes);

        _ = await _cryptoService.ConsumeTemporaryTokenAsync(ConsentTokenPurpose, consentToken, cancellationToken)
            ?? throw new InvalidOperationException("The consent session is invalid or expired.");

        var user = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == token.UserId, cancellationToken);
        var grant = await _consentService.UpsertGrantAsync(
            user.Id,
            authorizationRequest.ClientApplicationId,
            grantedScopes,
            // Stamp the metadata fingerprint this approval was granted against, so a grant
            // written after a concurrent CIMD refresh (which recomputes a different current
            // fingerprint) fails the coverage check and the next authorize re-prompts.
            currentClientMetadataFingerprint,
            cancellationToken);
        await _adminService.RecordAuditAsync(
            "oauth.consent.granted",
            "user",
            user.Id,
            userId: user.Id,
            ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
            data: new
            {
                client_id = authorizationRequest.ClientApplication?.ClientId ?? authorizationRequest.ClientApplicationId,
                scope = SqlOSScopePolicy.Join(grantedScopes)
            },
            cancellationToken: cancellationToken);

        try
        {
            return await CompleteAuthorizationRequestLoginAsync(
                authorizationRequest,
                user,
                payload.AuthenticationMethod,
                httpContext,
                cancellationToken,
                consentGranted: true,
                // Preserve the original authentication instant (SAML AuthnInstant / upstream
                // auth_time) across the consent interstitial so issuance does not record the
                // approval click as the authentication time.
                knownAuthenticatedAt: payload.AuthenticatedAt);
        }
        catch (InvalidOperationException ex) when (string.Equals(
            ex.Message,
            "Authorization request is no longer active.",
            StringComparison.Ordinal))
        {
            // A concurrent denial (or another terminal write) won the CancelledAt race after
            // this approval already committed its grant. The winning decision's effect must
            // include the grant, so compensate before surfacing the safe failure.
            await RevokeGrantForInactiveRequestAsync(grant, user.Id, authorizationRequest, httpContext, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Compensation for an approval whose issuance lost the terminal-write race: the grant
    /// that this approval created or updated is revoked (reason
    /// <c>authorization_request_cancelled</c>) and audited as <c>oauth.consent.revoked</c>,
    /// so a winning denial leaves no active remembered grant behind.
    /// </summary>
    private async Task RevokeGrantForInactiveRequestAsync(
        SqlOSConsentGrant grant,
        string userId,
        SqlOSAuthorizationRequest authorizationRequest,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Drop the failed issuance's staged writes (the added authorization code and the
        // terminal request update) so the compensating revocation save cannot replay them.
        if (_context is DbContext db)
        {
            foreach (var entry in db.ChangeTracker.Entries()
                .Where(x => x.Entity is SqlOSAuthorizationCode or SqlOSAuthorizationRequest
                    && x.State is EntityState.Added or EntityState.Modified)
                .ToList())
            {
                entry.State = EntityState.Detached;
            }
        }

        try
        {
            await _consentService.RevokeGrantAsync(
                userId,
                grant.Id,
                "authorization_request_cancelled",
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Already revoked (for example the CIMD tripwire fired concurrently): the
            // required effect — no active grant survives the losing approval — already holds.
            return;
        }

        await _adminService.RecordAuditAsync(
            "oauth.consent.revoked",
            "user",
            userId,
            userId: userId,
            ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
            data: new
            {
                client_id = authorizationRequest.ClientApplication?.ClientId ?? authorizationRequest.ClientApplicationId,
                reason = "authorization_request_cancelled"
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Consumes a consent token, audits <c>oauth.consent.denied</c>, cancels the authorization
    /// request, and returns the RFC 6749 §4.1.2.1 <c>access_denied</c> error redirect.
    /// </summary>
    public async Task<string> DenyConsentAsync(
        string consentToken,
        string authorizationRequestId,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        // Mirror ApproveConsentAsync: validate every request/client binding BEFORE consuming
        // the one-time token. A mismatched request id (for example another tab's) must fail
        // with the binding error while the flow's only approval/denial credential stays
        // usable — headless native flows may have no continuation cookie or auth-page
        // session from which the reload endpoint could re-mint it.
        var token = await _cryptoService.FindTemporaryTokenAsync(ConsentTokenPurpose, consentToken, cancellationToken)
            ?? throw new InvalidOperationException("The consent session is invalid or expired.");
        var (_, authorizationRequest) = await ValidateConsentTokenAsync(token, authorizationRequestId, cancellationToken);

        _ = await _cryptoService.ConsumeTemporaryTokenAsync(ConsentTokenPurpose, consentToken, cancellationToken)
            ?? throw new InvalidOperationException("The consent session is invalid or expired.");
        await _adminService.RecordAuditAsync(
            "oauth.consent.denied",
            "user",
            token.UserId,
            userId: token.UserId,
            ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
            data: new
            {
                client_id = authorizationRequest.ClientApplication?.ClientId ?? authorizationRequest.ClientApplicationId,
                scope = authorizationRequest.Scope
            },
            cancellationToken: cancellationToken);

        return await BuildAuthorizationErrorRedirectAsync(
            authorizationRequest,
            "access_denied",
            "The user denied the authorization request.",
            cancellationToken);
    }

    private async Task<(PendingConsentPayload Payload, SqlOSAuthorizationRequest AuthorizationRequest)> ValidateConsentTokenAsync(
        SqlOSTemporaryToken token,
        string authorizationRequestId,
        CancellationToken cancellationToken)
    {
        if (token.UserId == null)
        {
            throw new InvalidOperationException("The consent session is invalid.");
        }

        var payload = _cryptoService.DeserializePayload<PendingConsentPayload>(token)
            ?? throw new InvalidOperationException("The consent session payload is invalid.");
        if (!string.Equals(payload.AuthorizationRequestId, authorizationRequestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The consent session is not valid for this authorization request.");
        }

        var authorizationRequest = await GetRequiredAuthorizationRequestAsync(payload.AuthorizationRequestId, cancellationToken);
        if (!string.Equals(token.ClientApplicationId, authorizationRequest.ClientApplicationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The consent session is not valid for this client.");
        }

        return (payload, authorizationRequest);
    }

    /// <summary>
    /// Safe public failure for approvals whose consent screen went stale because the
    /// client's security-sensitive metadata changed between mint and approval.
    /// Listed in SqlOSPublicAuthErrorMapper.SafePublicMessages.
    /// </summary>
    internal const string ConsentClientMetadataChangedMessage =
        "The application's registration changed while consent was pending. Start the request again.";

    // AuthenticatedAt and ClientMetadataFingerprint default so consent tokens minted
    // before the fields existed still deserialize; null AuthenticatedAt falls back to
    // issuance-time resolution and null fingerprint skips the staleness check.
    private sealed record PendingConsentPayload(
        string AuthorizationRequestId,
        string AuthenticationMethod,
        DateTime? AuthenticatedAt = null,
        string? ClientMetadataFingerprint = null);

    public async Task<SqlOSPasswordAuthenticationResult> AuthenticatePasswordAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default,
        bool allowUnverifiedEmailForInvitation = false,
        HttpContext? httpContext = null,
        string? clientKey = null,
        string? authorizationRequestId = null,
        string? surface = null)
    {
        var credentialSettings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!credentialSettings.PasswordEnabled)
        {
            throw new InvalidOperationException("Local password authentication is disabled.");
        }

        var normalizedEmail = SqlOSAdminService.NormalizeEmail(email);
        var attempt = _passwordLoginAbuseService.CreateAttempt(
            normalizedEmail,
            httpContext,
            clientKey,
            authorizationRequestId,
            surface ?? "authorization");

        var emailRecord = await _context.Set<SqlOSUserEmail>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);
        attempt = attempt with { UserId = emailRecord?.UserId };

        if (emailRecord != null
            && _options.RequireVerifiedEmailForPasswordLogin
            && !emailRecord.IsVerified
            && !allowUnverifiedEmailForInvitation)
        {
            throw new InvalidOperationException("Email must be verified before password login.");
        }

        var credential = emailRecord == null
            ? null
            : await _context.Set<SqlOSCredential>()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserId == emailRecord.UserId && x.Type == "password" && x.RevokedAt == null, cancellationToken);
        await _passwordLoginAbuseService.ReserveAsync(attempt, cancellationToken);
        var passwordMatches = _cryptoService.VerifyPassword(
            credential?.SecretHash ?? SqlOSClientAuthenticationService.DummyCredentialHash,
            password);
        if (credential == null || !passwordMatches)
        {
            var failureReason = emailRecord == null
                ? "unknown_email"
                : credential == null
                    ? "missing_password_credential"
                    : "invalid_password";
            await _passwordLoginAbuseService.RecordFailureAsync(attempt, failureReason, cancellationToken);
            throw new InvalidOperationException(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
        }

        var user = await _context.Set<SqlOSUser>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == emailRecord!.UserId, cancellationToken);
        if (user == null || !user.IsActive)
        {
            await _passwordLoginAbuseService.RecordFailureAsync(attempt, "inactive_user", cancellationToken);
            throw new InvalidOperationException(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
        }

        await _passwordLoginAbuseService.RecordSuccessAsync(attempt, cancellationToken);
        var storedCredential = await _context.Set<SqlOSCredential>().FirstAsync(x => x.Id == credential.Id, cancellationToken);
        storedCredential.LastUsedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

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
        var credentialSettings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!credentialSettings.PasswordSignupEnabled)
        {
            throw new InvalidOperationException("Password signup is disabled.");
        }

        var input = SqlOSSignupOrchestration.NormalizePasswordSignup(
            displayName,
            email,
            password,
            organizationName);
        SqlOSSignupJoinPolicy.RejectUnauthorizedOrganizationJoin(organizationId);

        return await SqlOSSignupOrchestration.ExecuteAsync(
            _context,
            cancellationToken => SqlOSSignupOrchestration.CreatePasswordAccountAsync(
                _adminService,
                _context,
                input,
                cancellationToken),
            cancellationToken);
    }

    public Task EnsureSignupAuthorizationContextAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        CancellationToken cancellationToken = default)
        => SqlOSSignupOrchestration.EnsureAuthorizationSignupContextAsync(
            _adminService,
            _context,
            _options,
            authorizationRequest,
            cancellationToken);

    public async Task<SqlOSPasswordAuthenticationResult> SignUpWithEmailOtpAsync(
        string displayName,
        string email,
        string? organizationName,
        string? organizationId,
        CancellationToken cancellationToken = default)
    {
        var credentialSettings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!credentialSettings.EmailOtpEnabled)
        {
            throw new InvalidOperationException("Email sign-in is unavailable.");
        }

        SqlOSSignupJoinPolicy.RejectUnauthorizedOrganizationJoin(organizationId);

        var user = await _adminService.CreateUserAsync(
            new SqlOSCreateUserRequest(displayName, email, null),
            cancellationToken);

        var emailRecord = await _context.Set<SqlOSUserEmail>()
            .FirstAsync(x => x.UserId == user.Id && x.IsPrimary, cancellationToken);
        emailRecord.IsVerified = true;
        emailRecord.VerifiedAt = DateTime.UtcNow;
        user.DefaultEmail = emailRecord.Email;
        user.UpdatedAt = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(organizationName))
        {
            var createdOrganization = await _adminService.CreateOrganizationAsync(
                new SqlOSCreateOrganizationRequest(organizationName, null),
                cancellationToken);
            await _adminService.CreateMembershipAsync(createdOrganization.Id, new SqlOSCreateMembershipRequest(user.Id, "owner"), cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);

        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);

        return new SqlOSPasswordAuthenticationResult(user, organizations, "email_otp");
    }

    public async Task<SqlOSPasswordAuthenticationResult> SignUpWithPhoneOtpAsync(
        string displayName,
        string phoneNumber,
        string? organizationName,
        string? organizationId,
        CancellationToken cancellationToken = default)
    {
        var credentialSettings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!credentialSettings.PhoneOtpEnabled)
        {
            throw new InvalidOperationException("Phone sign-in is unavailable.");
        }

        SqlOSSignupJoinPolicy.RejectUnauthorizedOrganizationJoin(organizationId);

        var phoneHash = _cryptoService.HashToken(phoneNumber);
        var existingPhone = await _context.Set<SqlOSUserPhoneNumber>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.PhoneNumberHash == phoneHash && x.RemovedAt == null, cancellationToken);
        if (existingPhone != null)
        {
            throw new InvalidOperationException("An account already exists for this phone number. Sign in with a phone code instead.");
        }

        var now = DateTime.UtcNow;
        var user = new SqlOSUser
        {
            Id = _cryptoService.GenerateId("usr"),
            DisplayName = displayName,
            CreatedAt = now,
            UpdatedAt = now
        };
        _context.Set<SqlOSUser>().Add(user);
        _context.Set<SqlOSUserPhoneNumber>().Add(new SqlOSUserPhoneNumber
        {
            Id = _cryptoService.GenerateId("phn"),
            UserId = user.Id,
            PhoneNumber = phoneNumber,
            PhoneNumberHash = phoneHash,
            DisplayValueEncrypted = _cryptoService.ProtectSecret(phoneNumber),
            IsPrimary = true,
            IsVerified = true,
            VerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        if (!string.IsNullOrWhiteSpace(organizationName))
        {
            var createdOrganization = await _adminService.CreateOrganizationAsync(
                new SqlOSCreateOrganizationRequest(organizationName, null),
                cancellationToken);
            await _adminService.CreateMembershipAsync(createdOrganization.Id, new SqlOSCreateMembershipRequest(user.Id, "owner"), cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);

        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);
        return new SqlOSPasswordAuthenticationResult(user, organizations, "phone_otp");
    }

    public async Task<SqlOSPasswordAuthenticationResult> SignUpWithInvitationAsync(
        string displayName,
        string email,
        CancellationToken cancellationToken = default)
    {
        var user = await _adminService.CreateUserAsync(
            new SqlOSCreateUserRequest(displayName, email, null),
            cancellationToken);

        var emailRecord = await _context.Set<SqlOSUserEmail>()
            .FirstAsync(x => x.UserId == user.Id && x.IsPrimary, cancellationToken);
        emailRecord.IsVerified = true;
        emailRecord.VerifiedAt = DateTime.UtcNow;
        user.DefaultEmail = emailRecord.Email;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        return new SqlOSPasswordAuthenticationResult(
            user,
            Array.Empty<SqlOSOrganizationOption>(),
            "invitation");
    }

    public async Task<string> CreatePendingOrganizationSelectionAsync(
        SqlOSUser user,
        SqlOSAuthorizationRequest authorizationRequest,
        string authenticationMethod,
        CancellationToken cancellationToken = default,
        DateTime? authenticatedAt = null)
    {
        // Stamp the moment the user actually authenticated so organization
        // selection cannot inflate auth_time to the selection-click time. When
        // the caller does not know a better value, the pending token is being
        // created at the moment of a just-completed interactive login, so now
        // is the correct authentication time.
        return await _cryptoService.CreateTemporaryTokenAsync(
            "auth_page_pending",
            user.Id,
            authorizationRequest.ClientApplicationId,
            null,
            new PendingAuthorizationPayload(
                authorizationRequest.Id,
                authenticationMethod,
                authenticatedAt ?? DateTime.UtcNow),
            TimeSpan.FromMinutes(10),
            cancellationToken);
    }

    public async Task<string> CompletePendingOrganizationSelectionAsync(
        string pendingToken,
        string organizationId,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var result = await CompletePendingOrganizationSelectionForLoginAsync(
            pendingToken,
            organizationId,
            httpContext,
            cancellationToken);
        if (result.RequiresMfa)
        {
            throw new InvalidOperationException("The selected organization requires MFA.");
        }

        return result.RedirectUrl ?? throw new InvalidOperationException("The organization selection could not be completed.");
    }

    public async Task<SqlOSAuthorizationRequestLoginResult> CompletePendingOrganizationSelectionForLoginAsync(
        string pendingToken,
        string organizationId,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        // Peek before consuming the one-time pending token: when the requested
        // max_age lapsed while the user parked on the organization chooser, reject
        // without consuming anything so the interaction can be retried after
        // reauthentication instead of dead-ending the flow.
        var peekedToken = await _cryptoService.FindTemporaryTokenAsync("auth_page_pending", pendingToken, cancellationToken)
            ?? throw new InvalidOperationException("The organization selection session is invalid or expired.");
        var peekedPayload = _cryptoService.DeserializePayload<PendingAuthorizationPayload>(peekedToken)
            ?? throw new InvalidOperationException("The organization selection session payload is invalid.");
        var peekedRequest = await GetRequiredAuthorizationRequestAsync(peekedPayload.AuthorizationRequestId, cancellationToken);
        if (peekedRequest.MaxAgeSeconds is { } pendingMaxAgeSeconds
            && pendingMaxAgeSeconds > 0
            && peekedPayload.AuthenticatedAt is { } pendingAuthenticatedAt
            && (DateTime.UtcNow - pendingAuthenticatedAt).TotalSeconds >= pendingMaxAgeSeconds)
        {
            throw new InvalidOperationException("Authentication is older than the requested max_age.");
        }

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
        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);
        return await CompleteAssuredLoginAsync(
            authorizationRequest,
            user,
            organizationId,
            payload.AuthenticationMethod,
            organizations,
            httpContext,
            cancellationToken,
            // Pending tokens minted before AuthenticatedAt existed deserialize
            // null, which falls back to issuance-time resolution.
            knownAuthenticatedAt: payload.AuthenticatedAt);
    }

    internal async Task<SqlOSAuthorizationRequestLoginResult> GetPendingOrganizationSelectionForLoginAsync(
        string pendingToken,
        string authorizationRequestId,
        CancellationToken cancellationToken = default)
    {
        var temporaryToken = await _cryptoService.FindTemporaryTokenAsync("auth_page_pending", pendingToken, cancellationToken)
            ?? throw new InvalidOperationException("The organization selection session is invalid or expired.");
        if (temporaryToken.UserId == null)
        {
            throw new InvalidOperationException("The organization selection session is invalid.");
        }

        var payload = _cryptoService.DeserializePayload<PendingAuthorizationPayload>(temporaryToken)
            ?? throw new InvalidOperationException("The organization selection session payload is invalid.");
        if (!string.Equals(payload.AuthorizationRequestId, authorizationRequestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The organization selection session is not valid for this authorization request.");
        }

        var organizations = await _adminService.GetUserOrganizationsAsync(temporaryToken.UserId, cancellationToken);
        return new SqlOSAuthorizationRequestLoginResult(
            null,
            true,
            pendingToken,
            organizations,
            AuthorizationRequestId: authorizationRequestId);
    }

    internal const string AuthorizationContinuationPurpose = "authorization_continue";
    internal const string AuthorizationContinuationCookiePrefix = "sqlos_auth_continue_";

    /// <summary>
    /// Per-request continuation cookie name (mirroring the antiforgery cookie-name
    /// derivation in <see cref="Security.SqlOSHostedFormAntiforgery"/>): two authorization
    /// flows racing in separate browser tabs each keep their own cookie slot, so the second
    /// callback's Set-Cookie can never clobber the first tab's continuation handle. Writers
    /// and readers all know the authorization request id from the query/route, so the name
    /// is derivable everywhere the handle is needed.
    /// </summary>
    internal static string BuildContinuationCookieName(string authorizationRequestId)
        => AuthorizationContinuationCookiePrefix + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(authorizationRequestId)))[..16].ToLowerInvariant();

    public async Task<string> CreateAuthorizationContinuationRedirectAsync(
        SqlOSAuthorizationRequestLoginResult completion,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(completion.AuthorizationRequestId))
        {
            throw new InvalidOperationException("The authorization interaction is missing its request binding.");
        }

        if (!completion.RequiresMfa && !completion.RequiresOrganizationSelection && !completion.RequiresConsent)
        {
            throw new InvalidOperationException("The authorization interaction is already complete.");
        }

        if (completion.RequiresMfa && string.IsNullOrWhiteSpace(completion.MfaToken))
        {
            throw new InvalidOperationException("The MFA interaction is missing its challenge binding.");
        }

        if (completion.RequiresOrganizationSelection && string.IsNullOrWhiteSpace(completion.PendingToken))
        {
            throw new InvalidOperationException("The organization interaction is missing its pending binding.");
        }

        if (completion.RequiresConsent && string.IsNullOrWhiteSpace(completion.ConsentToken))
        {
            throw new InvalidOperationException("The consent interaction is missing its consent binding.");
        }

        var handle = await _cryptoService.CreateTemporaryTokenAsync(
            AuthorizationContinuationPurpose,
            userId: null,
            clientApplicationId: null,
            organizationId: null,
            new AuthorizationContinuationPayload(
                completion.AuthorizationRequestId,
                completion.MfaToken,
                completion.PendingToken,
                completion.ConsentToken),
            _options.Mfa.Totp.ChallengeTokenLifetime,
            cancellationToken);

        var continuePath = $"{_options.BasePath.TrimEnd('/')}/continue";
        // The cookie must reach both /continue and the headless request-reload endpoint
        // (GET {headlessApiBasePath}/requests/{id}): custom BuildUiUrl delegates may drop
        // the ConsentToken route field, and the reload re-mints it from this cookie via
        // TryCreateConsentTokenForRequestReloadAsync.
        httpContext.Response.Cookies.Append(
            BuildContinuationCookieName(completion.AuthorizationRequestId),
            handle,
            new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = httpContext.Request.IsHttps,
            Path = ResolveContinuationCookiePath(),
            Expires = DateTimeOffset.UtcNow.Add(_options.Mfa.Totp.ChallengeTokenLifetime)
        });

        return QueryHelpers.AddQueryString(continuePath, new Dictionary<string, string?>
        {
            ["request"] = completion.AuthorizationRequestId
        });
    }

    /// <summary>
    /// Scopes the continuation cookie to the auth base path so it covers both
    /// <c>{BasePath}/continue</c> and the default headless API under <c>{BasePath}/headless</c>.
    /// A custom headless API base path outside the auth base path falls back to the site
    /// root — the cookie is an HttpOnly random handle, so the wider path only changes which
    /// server endpoints can observe it.
    /// </summary>
    private string ResolveContinuationCookiePath()
    {
        var basePath = _options.BasePath.TrimEnd('/');
        if (basePath.Length == 0)
        {
            return "/";
        }

        var headlessApiBasePath = _options.Headless.ResolveApiBasePath(_options.BasePath);
        // Browser cookie-path matching is case-sensitive (RFC 6265 section 5.1.4), so the
        // prefix check must be ordinal: configured paths that differ only by case would
        // produce a cookie the browser never sends to the headless reload endpoint, so
        // they fall back to the site root like any other non-prefix path.
        return string.Equals(headlessApiBasePath, basePath, StringComparison.Ordinal)
            || headlessApiBasePath.StartsWith(basePath + "/", StringComparison.Ordinal)
                ? basePath
                : "/";
    }

    internal async Task<SqlOSAuthorizationRequestLoginResult> ResolveAuthorizationContinuationAsync(
        string authorizationRequestId,
        string continuationHandle,
        CancellationToken cancellationToken = default)
    {
        var token = await _cryptoService.FindTemporaryTokenAsync(
            AuthorizationContinuationPurpose,
            continuationHandle,
            cancellationToken)
            ?? throw new InvalidOperationException("Authorization continuation is invalid or expired.");
        var payload = _cryptoService.DeserializePayload<AuthorizationContinuationPayload>(token)
            ?? throw new InvalidOperationException("Authorization continuation is invalid.");
        if (!string.Equals(payload.AuthorizationRequestId, authorizationRequestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Authorization continuation is not valid for this authorization request.");
        }

        if (!string.IsNullOrWhiteSpace(payload.MfaToken))
        {
            var state = await _authService.GetAuthorizationMfaChallengeStateAsync(
                payload.MfaToken,
                authorizationRequestId,
                cancellationToken);
            return new SqlOSAuthorizationRequestLoginResult(
                null,
                false,
                null,
                Array.Empty<SqlOSOrganizationOption>(),
                RequiresMfa: true,
                MfaToken: payload.MfaToken,
                RequiresMfaEnrollment: state.EnrollmentRequired,
                MfaMethods: state.Methods,
                AuthorizationRequestId: authorizationRequestId);
        }

        if (!string.IsNullOrWhiteSpace(payload.PendingToken))
        {
            return await GetPendingOrganizationSelectionForLoginAsync(
                payload.PendingToken,
                authorizationRequestId,
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(payload.ConsentToken))
        {
            return await GetPendingConsentForLoginAsync(
                payload.ConsentToken,
                authorizationRequestId,
                cancellationToken);
        }

        throw new InvalidOperationException("Authorization continuation is invalid.");
    }

    internal async Task<SqlOSAuthorizationRequestLoginResult> GetPendingConsentForLoginAsync(
        string consentToken,
        string authorizationRequestId,
        CancellationToken cancellationToken = default)
    {
        var token = await _cryptoService.FindTemporaryTokenAsync(ConsentTokenPurpose, consentToken, cancellationToken)
            ?? throw new InvalidOperationException("The consent session is invalid or expired.");
        var payload = _cryptoService.DeserializePayload<PendingConsentPayload>(token)
            ?? throw new InvalidOperationException("The consent session payload is invalid.");
        if (!string.Equals(payload.AuthorizationRequestId, authorizationRequestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The consent session is not valid for this authorization request.");
        }

        var authorizationRequest = await GetRequiredAuthorizationRequestAsync(authorizationRequestId, cancellationToken);
        var grantedScopes = SqlOSScopePolicy.Split(authorizationRequest.Scope);
        return new SqlOSAuthorizationRequestLoginResult(
            null,
            false,
            null,
            Array.Empty<SqlOSOrganizationOption>(),
            AuthorizationRequestId: authorizationRequestId,
            RequiresConsent: true,
            ConsentToken: consentToken,
            ConsentScopes: await _consentService.BuildScopeDisplaysAsync(grantedScopes, cancellationToken));
    }

    private sealed record AuthorizationContinuationPayload(
        string AuthorizationRequestId,
        string? MfaToken,
        string? PendingToken,
        string? ConsentToken = null);

    internal const string ConsentTokenPurpose = "auth_page_consent";
    private static readonly TimeSpan ConsentTokenLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Re-mints a consent pending token for a consent view reloaded through the headless
    /// request endpoint. Custom <c>BuildUiUrl</c> delegates may drop the ConsentToken route
    /// field, so the reload recovers the user from what the browser actually carries: the
    /// authorization continuation cookie minted for this request, or a live auth-page
    /// session that still owes consent (silent SSO). Anonymous or foreign-browser reloads
    /// resolve neither and get no token — the consent view then fails closed.
    /// </summary>
    public async Task<string?> TryCreateConsentTokenForRequestReloadAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var continuationToken = await TryMintConsentTokenFromContinuationCookieAsync(
            authorizationRequest,
            httpContext,
            cancellationToken);
        if (continuationToken != null)
        {
            return continuationToken;
        }

        return await TryMintConsentTokenFromAuthPageSessionAsync(authorizationRequest, httpContext, cancellationToken);
    }

    private async Task<string?> TryMintConsentTokenFromContinuationCookieAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var continuationHandle = httpContext.Request.Cookies[BuildContinuationCookieName(authorizationRequest.Id)];
        if (string.IsNullOrWhiteSpace(continuationHandle))
        {
            return null;
        }

        var continuation = await _cryptoService.FindTemporaryTokenAsync(
            AuthorizationContinuationPurpose,
            continuationHandle,
            cancellationToken);
        var continuationPayload = continuation == null
            ? null
            : _cryptoService.DeserializePayload<AuthorizationContinuationPayload>(continuation);
        if (continuationPayload == null
            || !string.Equals(continuationPayload.AuthorizationRequestId, authorizationRequest.Id, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(continuationPayload.ConsentToken))
        {
            return null;
        }

        var consentTokenRecord = await _cryptoService.FindTemporaryTokenAsync(
            ConsentTokenPurpose,
            continuationPayload.ConsentToken,
            cancellationToken);
        var consentPayload = consentTokenRecord == null
            ? null
            : _cryptoService.DeserializePayload<PendingConsentPayload>(consentTokenRecord);
        if (consentTokenRecord?.UserId == null
            || consentPayload == null
            || !string.Equals(consentPayload.AuthorizationRequestId, authorizationRequest.Id, StringComparison.Ordinal)
            || !string.Equals(consentTokenRecord.ClientApplicationId, authorizationRequest.ClientApplicationId, StringComparison.Ordinal))
        {
            return null;
        }

        // The continuation cookie carries the original consent token's user, but it must
        // still match the user the consent gate bound to the request.
        if (authorizationRequest.PendingConsentUserId != null
            && !string.Equals(consentTokenRecord.UserId, authorizationRequest.PendingConsentUserId, StringComparison.Ordinal))
        {
            return null;
        }

        return await _cryptoService.CreateTemporaryTokenAsync(
            ConsentTokenPurpose,
            consentTokenRecord.UserId,
            authorizationRequest.ClientApplicationId,
            null,
            // Carry the original payload's authentication instant and metadata fingerprint
            // forward so the re-minted token behaves exactly like the one it replaces.
            new PendingConsentPayload(
                authorizationRequest.Id,
                consentPayload.AuthenticationMethod,
                consentPayload.AuthenticatedAt,
                consentPayload.ClientMetadataFingerprint),
            ConsentTokenLifetime,
            cancellationToken);
    }

    private async Task<string?> TryMintConsentTokenFromAuthPageSessionAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var session = await _authPageSessionService.TryGetSessionAsync(httpContext, cancellationToken);
        if (session == null)
        {
            return null;
        }

        // The auth-page session cookie is mutable (the browser can switch accounts between
        // reaching consent and reloading it), so the fallback only mints for the user the
        // consent gate bound to the request. The gate always stamps the binding when it
        // mints the first consent token, so a missing binding means no consent is owed by
        // this reload and the view fails closed.
        if (authorizationRequest.PendingConsentUserId == null
            || !string.Equals(session.User.Id, authorizationRequest.PendingConsentUserId, StringComparison.Ordinal))
        {
            return null;
        }

        // Mirror the consent gate in CompleteAuthorizationRequestLoginAsync: only mint when
        // this session's user actually owes consent for this request.
        var client = authorizationRequest.ClientApplication
            ?? await _context.Set<SqlOSClientApplication>()
                .FirstAsync(x => x.Id == authorizationRequest.ClientApplicationId, cancellationToken);
        if (client.IsFirstParty || !string.IsNullOrWhiteSpace(authorizationRequest.DeviceAuthorizationId))
        {
            return null;
        }

        var clientMetadataFingerprint = SqlOSCimdClientService.ComputeSensitiveMetadataFingerprint(client);
        var grantedScopes = SqlOSScopePolicy.Split(authorizationRequest.Scope);
        var promptForcesConsent = SqlOSScopePolicy.Split(authorizationRequest.Prompt).Contains("consent", StringComparer.Ordinal);
        if (!promptForcesConsent
            && await _consentService.HasCoveringGrantAsync(
                session.User.Id,
                authorizationRequest.ClientApplicationId,
                grantedScopes,
                clientMetadataFingerprint,
                cancellationToken))
        {
            return null;
        }

        return await _cryptoService.CreateTemporaryTokenAsync(
            ConsentTokenPurpose,
            session.User.Id,
            authorizationRequest.ClientApplicationId,
            null,
            new PendingConsentPayload(
                authorizationRequest.Id,
                session.AuthenticationMethod,
                session.AuthenticatedAt,
                clientMetadataFingerprint),
            ConsentTokenLifetime,
            cancellationToken);
    }

    public async Task<SqlOSAuthorizationRequestLoginResult> CompleteAuthorizationRequestLoginAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSUser user,
        string authenticationMethod,
        HttpContext httpContext,
        CancellationToken cancellationToken = default,
        bool consentGranted = false,
        DateTime? knownAuthenticatedAt = null)
    {
        // Consent is the first interstitial: it runs before invitation acceptance,
        // organization selection, and MFA so a denied request never advances state.
        // Device authorization requests are exempt because the device-approval page
        // is itself an explicit consent surface for the client and its scopes.
        if (!consentGranted && string.IsNullOrWhiteSpace(authorizationRequest.DeviceAuthorizationId))
        {
            var client = authorizationRequest.ClientApplication
                ?? await _context.Set<SqlOSClientApplication>()
                    .FirstAsync(x => x.Id == authorizationRequest.ClientApplicationId, cancellationToken);
            if (!client.IsFirstParty)
            {
                var clientMetadataFingerprint = SqlOSCimdClientService.ComputeSensitiveMetadataFingerprint(client);
                var grantedScopes = SqlOSScopePolicy.Split(authorizationRequest.Scope);
                var promptForcesConsent = SqlOSScopePolicy.Split(authorizationRequest.Prompt).Contains("consent", StringComparer.Ordinal);
                if (promptForcesConsent
                    || !await _consentService.HasCoveringGrantAsync(
                        user.Id,
                        authorizationRequest.ClientApplicationId,
                        grantedScopes,
                        clientMetadataFingerprint,
                        cancellationToken))
                {
                    // Bind the request to the user who reached the consent interstitial in
                    // the same save that mints the token, so reload re-minting and approval
                    // can reject a browser whose session cookie switched accounts.
                    authorizationRequest.PendingConsentUserId = user.Id;
                    var consentToken = await _cryptoService.CreateTemporaryTokenAsync(
                        ConsentTokenPurpose,
                        user.Id,
                        authorizationRequest.ClientApplicationId,
                        null,
                        new PendingConsentPayload(
                            authorizationRequest.Id,
                            authenticationMethod,
                            // The caller's known authentication instant (SAML AuthnInstant /
                            // upstream auth_time / the auth-page session cookie). When the
                            // caller has none, resolve it now: a live same-user session
                            // yields the original sign-in moment (silent SSO), and a fresh
                            // interactive flow reaches this gate immediately after
                            // credential verification, so now IS the authentication moment.
                            // Never null — approval must not substitute the approval click.
                            knownAuthenticatedAt
                                ?? await ResolveAuthenticatedAtAsync(httpContext, user.Id, cancellationToken),
                            clientMetadataFingerprint),
                        ConsentTokenLifetime,
                        cancellationToken);
                    return new SqlOSAuthorizationRequestLoginResult(
                        null,
                        false,
                        null,
                        Array.Empty<SqlOSOrganizationOption>(),
                        AuthorizationRequestId: authorizationRequest.Id,
                        RequiresConsent: true,
                        ConsentToken: consentToken,
                        ConsentScopes: await _consentService.BuildScopeDisplaysAsync(grantedScopes, cancellationToken));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(authorizationRequest.InvitationId))
        {
            // Enforce max_age freshness before accepting the bound invitation:
            // acceptance commits email verification and membership immediately,
            // and a flow the issuance recheck would reject must not leave that
            // half-committed state behind.
            var invitationAuthenticatedAt = knownAuthenticatedAt
                ?? await ResolveAuthenticatedAtAsync(httpContext, user.Id, cancellationToken);
            EnforceMaxAgeFreshness(authorizationRequest, invitationAuthenticatedAt);

            var invitationAcceptance = await RequireInvitationService().AcceptBoundInvitationAsync(
                authorizationRequest.InvitationId,
                user.Id,
                saveChanges: true,
                httpContext,
                cancellationToken);
            var invitationOrganizationId = invitationAcceptance?.OrganizationId;
            var invitationOrganizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);
            return await CompleteAssuredLoginAsync(
                authorizationRequest,
                user,
                invitationOrganizationId,
                authenticationMethod,
                invitationOrganizations,
                httpContext,
                cancellationToken,
                knownAuthenticatedAt ?? invitationAuthenticatedAt);
        }

        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);

        if (!string.IsNullOrWhiteSpace(authorizationRequest.OrganizationId))
        {
            if (organizations.All(x => x.Id != authorizationRequest.OrganizationId))
            {
                throw new InvalidOperationException("The selected organization is not available to this user.");
            }

            return await CompleteAssuredLoginAsync(
                authorizationRequest,
                user,
                authorizationRequest.OrganizationId,
                authenticationMethod,
                organizations,
                httpContext,
                cancellationToken,
                knownAuthenticatedAt);
        }

        if (organizations.Count > 1)
        {
            return new SqlOSAuthorizationRequestLoginResult(
                null,
                true,
                await CreatePendingOrganizationSelectionAsync(
                    user,
                    authorizationRequest,
                    authenticationMethod,
                    cancellationToken,
                    knownAuthenticatedAt ?? await ResolveAuthenticatedAtAsync(httpContext, user.Id, cancellationToken)),
                organizations,
                AuthorizationRequestId: authorizationRequest.Id);
        }

        var selectedOrganizationId = organizations.FirstOrDefault()?.Id;
        return await CompleteAssuredLoginAsync(
            authorizationRequest,
            user,
            selectedOrganizationId,
            authenticationMethod,
            organizations,
            httpContext,
            cancellationToken,
            knownAuthenticatedAt);
    }

    public async Task<string> CompleteMfaChallengeAsync(
        string mfaToken,
        string code,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var token = await _cryptoService.FindTemporaryTokenAsync(SqlOSAuthService.MfaChallengePurpose, mfaToken, cancellationToken)
            ?? throw new InvalidOperationException("MFA challenge is invalid or expired.");
        if (token.UserId == null || token.ClientApplicationId == null)
        {
            throw new InvalidOperationException("MFA challenge payload is invalid.");
        }

        var payload = _cryptoService.DeserializePayload<SqlOSMfaChallengePayload>(token)
            ?? throw new InvalidOperationException("MFA challenge payload is invalid.");
        if (!string.Equals(payload.Flow, "authorization", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(payload.AuthorizationRequestId))
        {
            throw new InvalidOperationException("MFA challenge is not valid for hosted authorization.");
        }

        if (payload.EnrollmentRequired)
        {
            throw new InvalidOperationException("MFA enrollment must be completed with its challenge-bound enrollment proof.");
        }

        await GetRequiredAuthorizationRequestAsync(payload.AuthorizationRequestId, cancellationToken);
        var factorMethod = await _authService.VerifyMfaChallengeFactorAsync(token, code, httpContext, cancellationToken);
        token.ConsumedAt = DateTime.UtcNow;
        return await CompleteConsumedMfaChallengeAsync(token, factorMethod, httpContext, cancellationToken);
    }

    public async Task<string> VerifyMfaTotpEnrollmentAsync(
        string mfaToken,
        string enrollmentToken,
        string code,
        string authorizationRequestId,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        IDbContextTransaction? transaction = null;
        try
        {
            if (SupportsDatabaseTransactions() && _context.Database.CurrentTransaction == null)
            {
                transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            }

            await GetRequiredAuthorizationRequestAsync(authorizationRequestId, cancellationToken);
            var verification = await _authService.VerifyTotpEnrollmentForAuthorizationChallengeAsync(
                new SqlOSTotpEnrollmentVerifyRequest(enrollmentToken, code, mfaToken),
                authorizationRequestId,
                cancellationToken);
            var redirect = await CompleteConsumedMfaChallengeAsync(
                verification.ChallengeToken,
                SqlOSMfaFactorTypes.Totp,
                httpContext,
                cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return redirect;
        }
        catch
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private async Task<string> CompleteConsumedMfaChallengeAsync(
        SqlOSTemporaryToken token,
        string factorMethod,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var payload = _cryptoService.DeserializePayload<SqlOSMfaChallengePayload>(token)
            ?? throw new InvalidOperationException("MFA challenge payload is invalid.");
        if (!string.Equals(payload.Flow, "authorization", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("MFA challenge is not valid for hosted authorization.");
        }

        if (string.IsNullOrWhiteSpace(payload.AuthorizationRequestId) || token.UserId == null)
        {
            throw new InvalidOperationException("MFA challenge payload is invalid.");
        }

        var authorizationRequest = await GetRequiredAuthorizationRequestAsync(payload.AuthorizationRequestId, cancellationToken);
        var user = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == token.UserId, cancellationToken);
        var authenticationMethod = SqlOSMfaPolicyService.AddAuthenticationMethod(payload.AuthenticationMethod, factorMethod);
        // The user just verified a required authentication factor. That completion
        // is fresh authentication: a session that aged past max_age while the user
        // finished the challenge must not be rejected at issuance, and the minted
        // code's auth_time reflects the factor-verification moment.
        var redirectUrl = await IssueAuthorizationRedirectAsync(
            authorizationRequest,
            user,
            token.OrganizationId,
            authenticationMethod,
            httpContext,
            cancellationToken,
            knownAuthenticatedAt: DateTime.UtcNow);

        await _adminService.RecordAuditAsync(
            "user.login.mfa",
            "user",
            user.Id,
            userId: user.Id,
            organizationId: token.OrganizationId,
            ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken: cancellationToken);

        return redirectUrl;
    }

    private async Task<SqlOSAuthorizationRequestLoginResult> CompleteAssuredLoginAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSUser user,
        string? organizationId,
        string authenticationMethod,
        IReadOnlyList<SqlOSOrganizationOption> organizations,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        DateTime? knownAuthenticatedAt = null)
    {
        var decision = await _mfaPolicyService.EvaluateForIssuanceAsync(
            user.Id,
            organizationId,
            authenticationMethod,
            authorizationRequest.Id,
            cancellationToken);
        if (!decision.CanIssue)
        {
            return await CreateMfaAuthorizationResultAsync(
                authorizationRequest,
                user,
                organizationId,
                authenticationMethod,
                organizations,
                decision.Evaluation,
                cancellationToken);
        }

        return new SqlOSAuthorizationRequestLoginResult(
            await IssueAssuredAuthorizationRedirectAsync(
                decision.Assurance!,
                authorizationRequest,
                user,
                httpContext,
                cancellationToken,
                knownAuthenticatedAt),
            false,
            null,
            organizations,
            AuthorizationRequestId: authorizationRequest.Id);
    }

    private async Task<SqlOSAuthorizationRequestLoginResult> CreateMfaAuthorizationResultAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSUser user,
        string? organizationId,
        string authenticationMethod,
        IReadOnlyList<SqlOSOrganizationOption> organizations,
        SqlOSMfaPolicyEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        var client = authorizationRequest.ClientApplication
            ?? await _context.Set<SqlOSClientApplication>()
                .FirstAsync(x => x.Id == authorizationRequest.ClientApplicationId, cancellationToken);
        var mfaToken = await _authService.CreateMfaChallengeAsync(
            user,
            client,
            organizationId,
            authenticationMethod,
            "authorization",
            evaluation.EnrollmentRequired,
            evaluation.EnrollmentRequired
                ? evaluation.AvailableFactors.Where(static factor =>
                    string.Equals(factor, SqlOSMfaFactorTypes.Totp, StringComparison.OrdinalIgnoreCase)).ToArray()
                : Array.Empty<string>(),
            authorizationRequest.Id,
            authorizationRequest.Resource,
            cancellationToken);

        return new SqlOSAuthorizationRequestLoginResult(
            null,
            false,
            null,
            organizations,
            RequiresMfa: true,
            MfaToken: mfaToken,
            RequiresMfaEnrollment: evaluation.EnrollmentRequired,
            MfaMethods: evaluation.AvailableFactors,
            AuthorizationRequestId: authorizationRequest.Id);
    }

    internal async Task<string> IssueAuthorizationRedirectAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSUser user,
        string? organizationId,
        string authenticationMethod,
        HttpContext httpContext,
        CancellationToken cancellationToken = default,
        DateTime? knownAuthenticatedAt = null)
    {
        var decision = await _mfaPolicyService.EvaluateForIssuanceAsync(
            user.Id,
            organizationId,
            authenticationMethod,
            authorizationRequest.Id,
            cancellationToken);
        if (decision.Assurance == null)
        {
            throw new InvalidOperationException(SqlOSMfaPolicyService.UnsatisfiedPolicyMessage);
        }

        return await IssueAssuredAuthorizationRedirectAsync(
            decision.Assurance,
            authorizationRequest,
            user,
            httpContext,
            cancellationToken,
            knownAuthenticatedAt);
    }

    private async Task<string> IssueAssuredAuthorizationRedirectAsync(
        SqlOSIssuanceAssurance assurance,
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSUser user,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        DateTime? knownAuthenticatedAt = null)
    {
        if (!string.Equals(assurance.UserId, user.Id, StringComparison.Ordinal)
            || !string.Equals(assurance.AuthorizationRequestId, authorizationRequest.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Authorization assurance binding is invalid.");
        }

        var organizationId = assurance.OrganizationId;
        var authenticationMethod = assurance.AuthenticationMethod;

        // Resolve and enforce authentication freshness before any mutation:
        // invitation acceptance and application-access side effects must not be
        // committed for a flow that the max_age recheck is about to reject.
        var authenticatedAt = knownAuthenticatedAt
            ?? await ResolveAuthenticatedAtAsync(httpContext, user.Id, cancellationToken);
        EnforceMaxAgeFreshness(authorizationRequest, authenticatedAt);

        await RequireActiveLifecycleAsync(
            user.Id,
            organizationId: null,
            "authorization_subject",
            allowPendingInvitationMembership: false,
            cancellationToken);

        SqlOSInvitationAcceptanceResult? invitationAcceptance = null;
        if (!string.IsNullOrWhiteSpace(authorizationRequest.InvitationId))
        {
            invitationAcceptance = await RequireInvitationService().AcceptBoundInvitationAsync(
                authorizationRequest.InvitationId,
                user.Id,
                saveChanges: false,
                httpContext,
                cancellationToken);
            organizationId = invitationAcceptance?.OrganizationId ?? organizationId;
        }

        if (!string.Equals(assurance.OrganizationId, organizationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Authorization assurance binding is invalid.");
        }

        var currentPolicy = await _mfaPolicyService.EvaluateForIssuanceAsync(
            user.Id,
            organizationId,
            authenticationMethod,
            authorizationRequest.Id,
            cancellationToken);
        if (!currentPolicy.CanIssue)
        {
            throw new InvalidOperationException(SqlOSMfaPolicyService.UnsatisfiedPolicyMessage);
        }

        await RequireActiveLifecycleAsync(
            user.Id,
            organizationId,
            "authorization_code_issue",
            invitationAcceptance is { MembershipCreated: true } or { MembershipReactivated: true },
            cancellationToken);

        var client = authorizationRequest.ClientApplication
            ?? await _context.Set<SqlOSClientApplication>()
                .FirstAsync(x => x.Id == authorizationRequest.ClientApplicationId, cancellationToken);
        await _adminService.EnsureApplicationAccessAsync(
            client,
            user.Id,
            organizationId,
            "application.access.authorization_denied",
            httpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(authorizationRequest.DeviceAuthorizationId))
        {
            authorizationRequest.ResolvedAuthMethod = authenticationMethod;
            authorizationRequest.ResolvedOrganizationId = organizationId;
            await _context.SaveChangesAsync(cancellationToken);
            await _authPageSessionService.SignInAsync(httpContext, user, organizationId, authenticationMethod, authenticatedAt, cancellationToken);

            return QueryHelpers.AddQueryString(
                $"{_options.BasePath.TrimEnd('/')}/device/approve",
                "request",
                authorizationRequest.Id);
        }

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
            Nonce = authorizationRequest.Nonce,
            AuthTime = authenticatedAt,
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

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new InvalidOperationException("Authorization request is no longer active.", ex);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            throw new InvalidOperationException("Authorization request is no longer active.", ex);
        }

        await _authPageSessionService.SignInAsync(httpContext, user, organizationId, authenticationMethod, authenticatedAt, cancellationToken);

        var query = new Dictionary<string, string?>
        {
            ["code"] = rawCode
        };
        if (!string.IsNullOrEmpty(authorizationRequest.State))
        {
            query["state"] = authorizationRequest.State;
        }

        if (!string.IsNullOrWhiteSpace(authorizationRequest.Scope))
        {
            query["scope"] = authorizationRequest.Scope;
        }

        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(authorizationRequest.RedirectUri, query);
    }

    /// <summary>
    /// max_age is validated when the authorize request arrives, but interstitials
    /// (organization selection, MFA) can outlast it. Re-check the resolved
    /// authentication age at the issuance boundary — and before any committed
    /// mutation such as invitation acceptance. TotalSeconds avoids the
    /// OverflowException TimeSpan.FromSeconds would throw for very large max_age
    /// values. max_age=0 is excluded: the /authorize gate already forces fresh
    /// reauthentication for it, and any elapsed time would make zero unsatisfiable
    /// here; the RP validates auth_time itself.
    /// </summary>
    private static void EnforceMaxAgeFreshness(SqlOSAuthorizationRequest authorizationRequest, DateTime authenticatedAt)
    {
        if (authorizationRequest.MaxAgeSeconds is { } maxAgeSeconds
            && maxAgeSeconds > 0
            && (DateTime.UtcNow - authenticatedAt).TotalSeconds >= maxAgeSeconds)
        {
            throw new InvalidOperationException("Authentication is older than the requested max_age.");
        }
    }

    /// <summary>
    /// Parses the OIDC <c>max_age</c> authorize parameter. Only null or the empty
    /// string mean the parameter was not supplied; any other value — including a
    /// whitespace-only one — must be a non-negative integer number of seconds with
    /// no sign or surrounding whitespace, so a present but malformed value is
    /// rejected instead of silently dropping the freshness constraint.
    /// </summary>
    internal static bool TryParseMaxAge(string? rawMaxAge, out long? maxAgeSeconds)
    {
        maxAgeSeconds = null;
        if (string.IsNullOrEmpty(rawMaxAge))
        {
            return true;
        }

        if (!long.TryParse(
                rawMaxAge,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }

        maxAgeSeconds = parsed;
        return true;
    }

    /// <summary>
    /// Whether the bound authorization request demands a fresh interactive
    /// authentication: <c>max_age=0</c>, or a prompt list containing
    /// <c>login</c> or <c>select_account</c>. Federated start paths propagate
    /// this upstream (<c>prompt=login</c> / SAML <c>ForceAuthn</c>) because
    /// clearing the local SqlOS session does not force the upstream identity
    /// provider to reauthenticate a silently reusable session.
    /// </summary>
    internal static bool RequiresFreshAuthentication(SqlOSAuthorizationRequest authorizationRequest)
        => authorizationRequest.MaxAgeSeconds == 0
            || PromptRequestsFreshLogin(authorizationRequest.Prompt);

    internal static bool PromptRequestsFreshLogin(string? prompt)
    {
        var values = TokenizePrompt(prompt);
        return values.Contains("login", StringComparer.Ordinal)
            || values.Contains("select_account", StringComparer.Ordinal);
    }

    internal static string[] TokenizePrompt(string? prompt)
        => (prompt ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Resolves the moment the user actually authenticated for the sign-in completing
    /// now. When the request carries a live auth-page session for the same user (silent
    /// SSO reuse), the original authentication time is preserved; otherwise the user
    /// authenticated interactively in this request and the moment is now.
    /// </summary>
    private async Task<DateTime> ResolveAuthenticatedAtAsync(
        HttpContext httpContext,
        string userId,
        CancellationToken cancellationToken)
    {
        var existingSession = await _authPageSessionService.TryGetSessionAsync(httpContext, cancellationToken);
        return existingSession != null && string.Equals(existingSession.User.Id, userId, StringComparison.Ordinal)
            ? existingSession.AuthenticatedAt
            : DateTime.UtcNow;
    }

    private async Task RequireActiveLifecycleAsync(
        string userId,
        string? organizationId,
        string boundary,
        bool allowPendingInvitationMembership,
        CancellationToken cancellationToken)
    {
        var lifecycle = await SqlOSAuthLifecyclePolicy.EvaluateAsync(
            _context,
            userId,
            organizationId,
            cancellationToken);
        if (lifecycle.IsActive
            || (allowPendingInvitationMembership
                && string.Equals(lifecycle.Reason, "membership_inactive", StringComparison.Ordinal)))
        {
            return;
        }

        await SqlOSAuthLifecyclePolicy.RevokeForDenialAsync(
            _context,
            userId,
            organizationId,
            lifecycle,
            DateTime.UtcNow,
            cancellationToken);
        SqlOSAuthLifecyclePolicy.AddDeniedAudit(
            _context,
            _cryptoService.GenerateId("aud"),
            boundary,
            lifecycle,
            userId,
            organizationId);
        await _context.SaveChangesAsync(cancellationToken);
        throw new InvalidOperationException("Authentication session is no longer active.");
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

            // RFC 6749 §5.1: echo the originally granted scope. The scope is
            // captured inside the refresh while the session entity is loaded so
            // no query runs after rotation has committed (a post-consumption
            // failure there would burn the rotated token). Sessions created
            // before the Scope column existed return null, which omits the field
            // from the response instead of claiming an empty grant.
            var (refreshed, sessionScope) = await _authService.RefreshWithSessionScopeAsync(
                new SqlOSRefreshRequest(request.RefreshToken, null, refreshResource, request.ClientId),
                cancellationToken);
            return new SqlOSTokenEndpointResult(refreshed, sessionScope);
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

        if (string.IsNullOrWhiteSpace(request.RedirectUri)
            || !string.Equals(authorizationCode.RedirectUri, request.RedirectUri, StringComparison.Ordinal))
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

        // PKCE binds only codes that were issued with a challenge. Public clients
        // always have one (enforced at /authorize); a confidential client that
        // authorized without PKCE exchanges without a verifier, and presenting a
        // verifier for a non-PKCE code fails closed (RFC 7636 §4.4.1).
        if (!string.IsNullOrEmpty(authorizationCode.CodeChallenge))
        {
            if (!_cryptoService.VerifyPkceCodeVerifier(request.CodeVerifier ?? string.Empty, authorizationCode.CodeChallenge, authorizationCode.CodeChallengeMethod))
            {
                throw new InvalidOperationException("PKCE verification failed.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.CodeVerifier))
        {
            throw new InvalidOperationException("PKCE verification failed.");
        }

        authorizationCode.ConsumedAt = DateTime.UtcNow;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new InvalidOperationException("Authorization code is no longer valid.", ex);
        }

        var tokens = await _authService.CreateSessionTokensForUserAsync(
            authorizationCode.User!,
            authorizationCode.ClientApplication!,
            authorizationCode.OrganizationId,
            authorizationCode.AuthenticationMethod,
            httpContext.Request.Headers.UserAgent.ToString(),
            httpContext.Connection.RemoteIpAddress?.ToString(),
            authorizationCode.Resource,
            authorizationCode.Scope,
            authorizationCode.Nonce,
            authorizationCode.AuthTime,
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

        if (SqlOSLocalRedirectDestination.TryResolve(requestedUrl, GetPublicOrigin(httpContext), out var localDestination))
        {
            return localDestination;
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
        => SqlOSPublicOriginResolver.Resolve(_options);

    private static List<string> ParseJsonArray(string json)
        => JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();

    /// <summary>
    /// Reserved OpenID Connect scope names. These never surface through the
    /// client-allowlist-derived scope list; when OpenID Provider mode is enabled,
    /// <c>openid</c>/<c>profile</c>/<c>email</c> are advertised through
    /// <see cref="AdvertisedOpenIdScopes"/> instead. <c>offline_access</c> stays
    /// unadvertised because SqlOS does not gate refresh-token issuance on it.
    /// Client allowlists may still include them; requested-but-unadvertised scopes
    /// are silently intersected.
    /// </summary>
    private static readonly HashSet<string> ReservedOidcScopeNames = new(StringComparer.Ordinal)
    {
        "openid",
        "profile",
        "email",
        "offline_access"
    };

    private static bool IsAdvertisedGrantableScope(string scope)
        => !string.IsNullOrWhiteSpace(scope)
            && !scope.StartsWith("auth:", StringComparison.Ordinal)
            && !ReservedOidcScopeNames.Contains(scope);

    // AuthenticatedAt defaults so pending tokens minted before the field existed
    // still deserialize; null means "unknown" and issuance falls back to its own
    // resolution.
    private sealed record PendingAuthorizationPayload(
        string AuthorizationRequestId,
        string AuthenticationMethod,
        DateTime? AuthenticatedAt = null);

    private SqlOSInvitationService RequireInvitationService()
        => _invitationService ?? throw new InvalidOperationException("SqlOS invitations are not configured.");

    private SqlOSTotpMfaService RequireTotpMfaService()
        => _totpMfaService ?? throw new InvalidOperationException("TOTP MFA service is not registered.");

    private bool SupportsDatabaseTransactions()
        => !string.Equals(_context.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal);

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
        => SqlOSDatabaseErrors.IsUniqueConstraintViolation(exception);
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
    string? UiContextJson,
    string? MaxAge = null,
    string? RequestObject = null,
    string? RequestUri = null);

public sealed record SqlOSPasswordAuthenticationResult(
    SqlOSUser User,
    IReadOnlyList<SqlOSOrganizationOption> Organizations,
    string AuthenticationMethod);

public sealed record SqlOSAuthorizationRequestLoginResult(
    string? RedirectUrl,
    bool RequiresOrganizationSelection,
    string? PendingToken,
    IReadOnlyList<SqlOSOrganizationOption> Organizations,
    bool RequiresMfa = false,
    string? MfaToken = null,
    bool RequiresMfaEnrollment = false,
    IReadOnlyList<string>? MfaMethods = null,
    string? AuthorizationRequestId = null,
    bool RequiresConsent = false,
    string? ConsentToken = null,
    IReadOnlyList<SqlOSConsentScopeDisplay>? ConsentScopes = null);

public sealed record SqlOSTokenRequest(
    string GrantType,
    string? Code,
    string? RedirectUri,
    string? ClientId,
    string? CodeVerifier,
    string? RefreshToken,
    string? Resource,
    string? DeviceCode = null);

public sealed record SqlOSTokenEndpointResult(
    SqlOSTokenResponse Tokens,
    string? Scope);

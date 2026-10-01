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
using SqlOS.AuthServer.Processes.Identity;
using SqlOS.Domain;
using SqlOS.Hosting;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSAuthorizationServerService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSAuthService _authService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly SqlOSIssuerSessionService _issuerSessionService;
    private readonly SqlOSAuthServerOptions _options;
    private readonly SqlOSInvitationService? _invitationService;
    private readonly IAdmissionGate _admission;
    private readonly SqlOSMfaPolicyService _mfaPolicyService;
    private readonly SqlOSConsentService _consentService;

    public SqlOSAuthorizationServerService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSAuthService authService,
        SqlOSCryptoService cryptoService,
        SqlOSSettingsService settingsService,
        SqlOSIssuerSessionService issuerSessionService,
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
        _issuerSessionService = issuerSessionService;
        _options = options.Value;
        _invitationService = invitationService;
        _admission = SqlOSAdmissionGate.Create(
            context,
            adminService,
            cryptoService,
            options,
            passwordAttempts: passwordLoginAbuseService);
        _mfaPolicyService = mfaPolicyService ?? new SqlOSMfaPolicyService(context, settingsService, options);
        _consentService = consentService ?? new SqlOSConsentService(context, cryptoService);
    }

    /// <summary>The crypto service: the headless facade builds its identity processes with it.</summary>
    internal SqlOSCryptoService Crypto => _cryptoService;

    /// <summary>The admission gate the hosted and headless password sign-ins pass.</summary>
    internal IAdmissionGate PasswordAdmission => _admission;

    /// <summary>
    /// The admission gate an authorization request's MFA factor comparisons pass: the auth service's,
    /// as in 7.2.1.
    /// </summary>
    internal IAdmissionGate MfaAdmission => _authService.Admission;

    /// <summary>The authenticator channel an authorization request's MFA challenge uses: the auth service's, as in 7.2.1.</summary>
    internal SqlOSTotpMfaService? Authenticators => _authService.Authenticators;

    /// <summary>The identity processes this service's facades delegate to.</summary>
    private SqlOSIdentityProcesses Processes => new(_context, _adminService, _cryptoService, _settingsService, _options)
    {
        PasswordAdmission = _admission,
        MfaAdmission = MfaAdmission,
        Authenticators = Authenticators,
        AuthorizationServer = this,
        Auth = _authService,
        IssuerSessions = _issuerSessionService,
        Invitations = _invitationService
    };

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
                SqlOSTemporaryTokenKinds.AuthPagePending,
                completion.PendingToken,
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(completion.MfaToken))
        {
            _ = await _cryptoService.ConsumeTemporaryTokenAsync(
                SqlOSTemporaryTokenKinds.MfaChallenge,
                completion.MfaToken,
                cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(completion.ConsentToken))
        {
            _ = await _cryptoService.ConsumeTemporaryTokenAsync(
                SqlOSTemporaryTokenKinds.AuthPageConsent,
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
        var token = await _cryptoService.FindTemporaryTokenAsync(SqlOSTemporaryTokenKinds.AuthPageConsent, consentToken, cancellationToken)
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

        _ = await _cryptoService.ConsumeTemporaryTokenAsync(SqlOSTemporaryTokenKinds.AuthPageConsent, consentToken, cancellationToken)
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
            return await CompleteAuthorizationRequestLoginCoreAsync(
                authorizationRequest,
                user,
                payload.AuthenticationMethod,
                httpContext,
                approverOrganizationId: null,
                // The consent interstitial keeps the evidence of the flow that reached it: a
                // credential sign-in may replace a dead issuer cookie, silent reuse may not.
                payload.CredentialSignIn ? SqlOSSignInEvidence.Credential : SqlOSSignInEvidence.PresentedSession,
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
        var token = await _cryptoService.FindTemporaryTokenAsync(SqlOSTemporaryTokenKinds.AuthPageConsent, consentToken, cancellationToken)
            ?? throw new InvalidOperationException("The consent session is invalid or expired.");
        var (_, authorizationRequest) = await ValidateConsentTokenAsync(token, authorizationRequestId, cancellationToken);

        _ = await _cryptoService.ConsumeTemporaryTokenAsync(SqlOSTemporaryTokenKinds.AuthPageConsent, consentToken, cancellationToken)
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

        var payload = token.ReadPayload(SqlOSTemporaryTokenKinds.AuthPageConsent)
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


    /// <summary>
    /// Checks an address and password without completing a sign-in: the password sign-in
    /// (<see cref="SignInWithPassword"/>) with only its credential. Its caller completes the login.
    /// </summary>
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
        var outcome = await Processes.SignInWithPassword(httpContext).ExecuteAsync(
            new SignInWithPasswordCommand(
                email,
                password,
                LoginTarget.CredentialOnly.Instance,
                allowUnverifiedEmailForInvitation,
                new PasswordAttemptContext(clientKey, authorizationRequestId, surface ?? "authorization"),
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.Hosted)),
            cancellationToken);
        var signedIn = outcome switch
        {
            SignInOutcome.SignedIn success => success,
            SignInOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown sign-in outcome '{outcome.GetType().Name}'.")
        };

        var organizations = await _adminService.GetUserOrganizationsAsync(signedIn.Evidence.UserId, cancellationToken);
        return new SqlOSPasswordAuthenticationResult(signedIn.Evidence.User, organizations, signedIn.Evidence.AuthenticationMethod);
    }

    /// <summary>
    /// Registers a password account without signing it in: the password sign-up
    /// (<see cref="SignUpWithPassword"/>) with only its account. Its caller completes the login.
    /// </summary>
    public async Task<SqlOSPasswordAuthenticationResult> SignUpAsync(
        string displayName,
        string email,
        string password,
        string? organizationName,
        string? organizationId,
        CancellationToken cancellationToken = default)
    {
        var outcome = await Processes.SignUpWithPassword(httpContext: null).ExecuteAsync(
            new SignUpWithPasswordCommand(
                displayName,
                email,
                password,
                organizationName,
                organizationId,
                Invitation: null,
                CustomFields: null,
                LoginTarget.CredentialOnly.Instance,
                SqlOSRequestContext.System),
            cancellationToken);
        var signedUp = outcome switch
        {
            SignUpOutcome.SignedUp success => success,
            SignUpOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown sign-up outcome '{outcome.GetType().Name}'.")
        };

        var organizations = await _adminService.GetUserOrganizationsAsync(signedUp.Evidence.UserId, cancellationToken);
        return new SqlOSPasswordAuthenticationResult(signedUp.Evidence.User, organizations, signedUp.Evidence.AuthenticationMethod);
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

    /// <summary>
    /// Registers the account of an email-code sign-up whose code the caller already verified,
    /// without signing it in (the step <see cref="CompleteEmailOtpSignUp"/> runs).
    /// </summary>
    public async Task<SqlOSPasswordAuthenticationResult> SignUpWithEmailOtpAsync(
        string displayName,
        string email,
        string? organizationName,
        string? organizationId,
        CancellationToken cancellationToken = default)
    {
        // The caller verified the sign-up code sent to this address, which proves the mailbox.
        var registered = await AccountRegistration.RegisterWithEmailOtpAsync(
            _context,
            _adminService,
            _settingsService,
            displayName,
            email,
            SignupProof(email, OwnershipProofMethod.EmailOtp),
            organizationName,
            organizationId,
            _cryptoService.Clock.GetUtcNow().UtcDateTime,
            cancellationToken);
        return Registered(registered, AuthenticationMethods.EmailOtp);
    }

    /// <summary>
    /// Registers the account of a phone-code sign-up whose code the caller already verified,
    /// without signing it in (the step <see cref="CompletePhoneOtpSignUp"/> runs).
    /// </summary>
    public async Task<SqlOSPasswordAuthenticationResult> SignUpWithPhoneOtpAsync(
        string displayName,
        string phoneNumber,
        string? organizationName,
        string? organizationId,
        CancellationToken cancellationToken = default)
    {
        var registered = await AccountRegistration.RegisterWithPhoneOtpAsync(
            _context,
            _adminService,
            _settingsService,
            _cryptoService,
            displayName,
            phoneNumber,
            organizationName,
            organizationId,
            _cryptoService.Clock.GetUtcNow().UtcDateTime,
            cancellationToken);
        return Registered(registered, AuthenticationMethods.PhoneOtp);
    }

    /// <summary>
    /// Registers the account an invitation the caller resolved was sent for, without signing it in
    /// or accepting the invitation (the step <see cref="SignUpWithInvitation"/> runs).
    /// </summary>
    public async Task<SqlOSPasswordAuthenticationResult> SignUpWithInvitationAsync(
        string displayName,
        string email,
        CancellationToken cancellationToken = default)
    {
        // The invitation was mailed to this address and its token is presented now, which proves the mailbox.
        var registration = await AccountRegistration.RegisterWithInvitationAsync(
            _context,
            displayName,
            email,
            SignupProof(email, OwnershipProofMethod.Invitation),
            _cryptoService.Clock.GetUtcNow().UtcDateTime,
            cancellationToken);
        return new SqlOSPasswordAuthenticationResult(registration.User, registration.Organizations, AuthenticationMethods.Invitation);
    }

    private static SqlOSPasswordAuthenticationResult Registered(SignupRegistrationOutcome registered, string authenticationMethod)
        => registered switch
        {
            SignupRegistrationOutcome.Registered { Registration: var registration }
                => new SqlOSPasswordAuthenticationResult(registration.User, registration.Organizations, authenticationMethod),
            SignupRegistrationOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown registration outcome '{registered.GetType().Name}'.")
        };

    /// <summary>
    /// The proof a sign-up's verified code or presented invitation gives for the address it signs
    /// up. An address that is not valid has none, and registration refuses it as 7.x did.
    /// </summary>
    private static OwnershipProof? SignupProof(string email, OwnershipProofMethod method)
        => EmailAddress.TryParse(email, out var address) ? new OwnershipProof(address, method) : null;

    public Task<string> CreatePendingOrganizationSelectionAsync(
        SqlOSUser user,
        SqlOSAuthorizationRequest authorizationRequest,
        string authenticationMethod,
        CancellationToken cancellationToken = default,
        DateTime? authenticatedAt = null)
        => CreatePendingOrganizationSelectionAsync(
            user,
            authorizationRequest,
            authenticationMethod,
            authenticatedAt,
            SqlOSSignInEvidence.PresentedSession,
            cancellationToken);

    private async Task<string> CreatePendingOrganizationSelectionAsync(
        SqlOSUser user,
        SqlOSAuthorizationRequest authorizationRequest,
        string authenticationMethod,
        DateTime? authenticatedAt,
        SqlOSSignInEvidence evidence,
        CancellationToken cancellationToken)
    {
        // Stamp the moment the user actually authenticated so organization
        // selection cannot inflate auth_time to the selection-click time. When
        // the caller does not know a better value, the pending token is being
        // created at the moment of a just-completed interactive login, so now
        // is the correct authentication time.
        var pending = await _cryptoService.CreateTemporaryTokenAsync(
            SqlOSTemporaryTokenKinds.AuthPagePending,
            new PendingAuthorizationPayload(
                authorizationRequest.Id,
                authenticationMethod,
                authenticatedAt ?? DateTime.UtcNow,
                evidence == SqlOSSignInEvidence.Credential),
            new TemporaryTokenBinding(UserId: user.Id, ClientApplicationId: authorizationRequest.ClientApplicationId),
            configuredLifetime: null,
            cancellationToken);
        return pending.RawToken;
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
        var peekedToken = await _cryptoService.FindTemporaryTokenAsync(SqlOSTemporaryTokenKinds.AuthPagePending, pendingToken, cancellationToken)
            ?? throw new InvalidOperationException("The organization selection session is invalid or expired.");
        var peekedPayload = peekedToken.ReadPayload(SqlOSTemporaryTokenKinds.AuthPagePending)
            ?? throw new InvalidOperationException("The organization selection session payload is invalid.");
        var peekedRequest = await GetRequiredAuthorizationRequestAsync(peekedPayload.AuthorizationRequestId, cancellationToken);
        if (peekedRequest.MaxAgeSeconds is { } pendingMaxAgeSeconds
            && pendingMaxAgeSeconds > 0
            && peekedPayload.AuthenticatedAt is { } pendingAuthenticatedAt
            && (DateTime.UtcNow - pendingAuthenticatedAt).TotalSeconds >= pendingMaxAgeSeconds)
        {
            throw new InvalidOperationException("Authentication is older than the requested max_age.");
        }

        var temporaryToken = await _cryptoService.ConsumeTemporaryTokenAsync(SqlOSTemporaryTokenKinds.AuthPagePending, pendingToken, cancellationToken)
            ?? throw new InvalidOperationException("The organization selection session is invalid or expired.");
        if (temporaryToken.UserId == null)
        {
            throw new InvalidOperationException("The organization selection session is invalid.");
        }

        var payload = temporaryToken.ReadPayload(SqlOSTemporaryTokenKinds.AuthPagePending)
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
            knownAuthenticatedAt: payload.AuthenticatedAt,
            payload.CredentialSignIn ? SqlOSSignInEvidence.Credential : SqlOSSignInEvidence.PresentedSession);
    }

    internal async Task<SqlOSAuthorizationRequestLoginResult> GetPendingOrganizationSelectionForLoginAsync(
        string pendingToken,
        string authorizationRequestId,
        CancellationToken cancellationToken = default)
    {
        var temporaryToken = await _cryptoService.FindTemporaryTokenAsync(SqlOSTemporaryTokenKinds.AuthPagePending, pendingToken, cancellationToken)
            ?? throw new InvalidOperationException("The organization selection session is invalid or expired.");
        if (temporaryToken.UserId == null)
        {
            throw new InvalidOperationException("The organization selection session is invalid.");
        }

        var payload = temporaryToken.ReadPayload(SqlOSTemporaryTokenKinds.AuthPagePending)
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

        var handle = (await _cryptoService.CreateTemporaryTokenAsync(
            SqlOSTemporaryTokenKinds.AuthorizationContinuation,
            new AuthorizationContinuationPayload(
                completion.AuthorizationRequestId,
                completion.MfaToken,
                completion.PendingToken,
                completion.ConsentToken),
            TemporaryTokenBinding.None,
            _options.Mfa.Totp.ChallengeTokenLifetime,
            cancellationToken)).RawToken;

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
            SqlOSTemporaryTokenKinds.AuthorizationContinuation,
            continuationHandle,
            cancellationToken)
            ?? throw new InvalidOperationException("Authorization continuation is invalid or expired.");
        var payload = token.ReadPayload(SqlOSTemporaryTokenKinds.AuthorizationContinuation)
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
        var token = await _cryptoService.FindTemporaryTokenAsync(SqlOSTemporaryTokenKinds.AuthPageConsent, consentToken, cancellationToken)
            ?? throw new InvalidOperationException("The consent session is invalid or expired.");
        var payload = token.ReadPayload(SqlOSTemporaryTokenKinds.AuthPageConsent)
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

        return await TryMintConsentTokenFromIssuerSessionAsync(authorizationRequest, httpContext, cancellationToken);
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
            SqlOSTemporaryTokenKinds.AuthorizationContinuation,
            continuationHandle,
            cancellationToken);
        var continuationPayload = continuation == null
            ? null
            : continuation.ReadPayload(SqlOSTemporaryTokenKinds.AuthorizationContinuation);
        if (continuationPayload == null
            || !string.Equals(continuationPayload.AuthorizationRequestId, authorizationRequest.Id, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(continuationPayload.ConsentToken))
        {
            return null;
        }

        var consentTokenRecord = await _cryptoService.FindTemporaryTokenAsync(
            SqlOSTemporaryTokenKinds.AuthPageConsent,
            continuationPayload.ConsentToken,
            cancellationToken);
        var consentPayload = consentTokenRecord == null
            ? null
            : consentTokenRecord.ReadPayload(SqlOSTemporaryTokenKinds.AuthPageConsent);
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

        var reminted = await _cryptoService.CreateTemporaryTokenAsync(
            SqlOSTemporaryTokenKinds.AuthPageConsent,
            // Carry the original payload's authentication instant and metadata fingerprint
            // forward so the re-minted token behaves exactly like the one it replaces.
            new PendingConsentPayload(
                authorizationRequest.Id,
                consentPayload.AuthenticationMethod,
                consentPayload.AuthenticatedAt,
                consentPayload.ClientMetadataFingerprint,
                consentPayload.CredentialSignIn),
            new TemporaryTokenBinding(UserId: consentTokenRecord.UserId, ClientApplicationId: authorizationRequest.ClientApplicationId),
            configuredLifetime: null,
            cancellationToken);
        return reminted.RawToken;
    }

    private async Task<string?> TryMintConsentTokenFromIssuerSessionAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var session = await _issuerSessionService.TryGetSessionAsync(httpContext, cancellationToken);
        if (session == null)
        {
            return null;
        }

        // The issuer session cookie is mutable (the browser can switch accounts between
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

        var consent = await _cryptoService.CreateTemporaryTokenAsync(
            SqlOSTemporaryTokenKinds.AuthPageConsent,
            new PendingConsentPayload(
                authorizationRequest.Id,
                session.AuthenticationMethod,
                session.AuthenticatedAt,
                clientMetadataFingerprint),
            new TemporaryTokenBinding(UserId: session.User.Id, ClientApplicationId: authorizationRequest.ClientApplicationId),
            configuredLifetime: null,
            cancellationToken);
        return consent.RawToken;
    }

    /// <summary>
    /// Completes sign-in for a user whose identity rests on the issuer session the browser
    /// presents (silent reuse, or device approval by a signed-in browser). Issuance fails
    /// closed when that session's family has been revoked.
    /// </summary>
    public Task<SqlOSAuthorizationRequestLoginResult> CompleteAuthorizationRequestLoginAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSUser user,
        string authenticationMethod,
        HttpContext httpContext,
        CancellationToken cancellationToken = default,
        bool consentGranted = false,
        DateTime? knownAuthenticatedAt = null)
        => CompleteAuthorizationRequestLoginCoreAsync(
            authorizationRequest,
            user,
            authenticationMethod,
            httpContext,
            approverOrganizationId: null,
            SqlOSSignInEvidence.PresentedSession,
            cancellationToken,
            consentGranted,
            knownAuthenticatedAt);

    /// <summary>
    /// Completes sign-in for a device authorization request on the approval surface.
    /// The organization the approver picked is authorized by the same membership check as
    /// a request-bound organization, but it is never assigned to the tracked request: a
    /// pick the user is not a member of fails without changing the request. The approval
    /// rests on the approver's presented issuer session.
    /// </summary>
    internal Task<SqlOSAuthorizationRequestLoginResult> CompleteDeviceApprovalLoginAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSUser user,
        string authenticationMethod,
        HttpContext httpContext,
        string? selectedOrganizationId,
        CancellationToken cancellationToken = default)
    {
        SqlOSDeviceAuthorizationService.RequireDeviceAuthorizationRequest(authorizationRequest);
        return CompleteAuthorizationRequestLoginCoreAsync(
            authorizationRequest,
            user,
            authenticationMethod,
            httpContext,
            string.IsNullOrWhiteSpace(selectedOrganizationId) ? null : selectedOrganizationId,
            SqlOSSignInEvidence.PresentedSession,
            cancellationToken,
            consentGranted: false,
            knownAuthenticatedAt: null);
    }

    /// <summary>
    /// Completes sign-in right after the user presented a credential in this flow: password,
    /// one-time code, magic link, SSO or OIDC callback, or signup. The credential proves
    /// identity on its own, so an issuer session cookie that was revoked or cleaned up counts
    /// as signed out: issuance starts a new family and replaces the dead cookie instead of
    /// failing. The revoked family is never revived.
    /// </summary>
    internal Task<SqlOSAuthorizationRequestLoginResult> CompleteCredentialSignInAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSUser user,
        string authenticationMethod,
        HttpContext httpContext,
        CancellationToken cancellationToken = default,
        DateTime? knownAuthenticatedAt = null)
        => CompleteAuthorizationRequestLoginCoreAsync(
            authorizationRequest,
            user,
            authenticationMethod,
            httpContext,
            approverOrganizationId: null,
            SqlOSSignInEvidence.Credential,
            cancellationToken,
            consentGranted: false,
            knownAuthenticatedAt);

    private async Task<SqlOSAuthorizationRequestLoginResult> CompleteAuthorizationRequestLoginCoreAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSUser user,
        string authenticationMethod,
        HttpContext httpContext,
        string? approverOrganizationId,
        SqlOSSignInEvidence evidence,
        CancellationToken cancellationToken,
        bool consentGranted,
        DateTime? knownAuthenticatedAt)
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
                    var consentToken = (await _cryptoService.CreateTemporaryTokenAsync(
                        SqlOSTemporaryTokenKinds.AuthPageConsent,
                        new PendingConsentPayload(
                            authorizationRequest.Id,
                            authenticationMethod,
                            // The caller's known authentication instant (SAML AuthnInstant /
                            // upstream auth_time / the issuer session cookie). When the
                            // caller has none, resolve it now: a live same-user session
                            // yields the original sign-in moment (silent SSO), and a fresh
                            // interactive flow reaches this gate immediately after
                            // credential verification, so now IS the authentication moment.
                            // Never null — approval must not substitute the approval click.
                            knownAuthenticatedAt
                                ?? await ResolveAuthenticatedAtAsync(httpContext, user.Id, cancellationToken),
                            clientMetadataFingerprint,
                            evidence == SqlOSSignInEvidence.Credential),
                        new TemporaryTokenBinding(UserId: user.Id, ClientApplicationId: authorizationRequest.ClientApplicationId),
                        configuredLifetime: null,
                        cancellationToken)).RawToken;
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
                authenticationMethod,
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
                knownAuthenticatedAt ?? invitationAuthenticatedAt,
                evidence);
        }

        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);
        var boundOrganizationId = approverOrganizationId ?? authorizationRequest.OrganizationId;

        if (!string.IsNullOrWhiteSpace(boundOrganizationId))
        {
            if (organizations.All(x => x.Id != boundOrganizationId))
            {
                throw new InvalidOperationException("The selected organization is not available to this user.");
            }

            return await CompleteAssuredLoginAsync(
                authorizationRequest,
                user,
                boundOrganizationId,
                authenticationMethod,
                organizations,
                httpContext,
                cancellationToken,
                knownAuthenticatedAt,
                evidence);
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
                    knownAuthenticatedAt ?? await ResolveAuthenticatedAtAsync(httpContext, user.Id, cancellationToken),
                    evidence,
                    cancellationToken),
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
            knownAuthenticatedAt,
            evidence);
    }

    /// <summary>
    /// Answers an authorization request's MFA challenge with an authenticator code or a recovery
    /// code, and returns the redirect that carries the request's code to the client.
    /// </summary>
    public async Task<string> CompleteMfaChallengeAsync(
        string mfaToken,
        string code,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var outcome = await Processes.VerifyMfaChallenge(httpContext).ExecuteAsync(
            new VerifyMfaChallengeCommand(
                mfaToken,
                code,
                MfaChallengeTarget.AuthorizationRequest,
                SqlOSHttpRequestContext.From(httpContext, SqlOSRequestSurface.Hosted)),
            cancellationToken);
        return outcome switch
        {
            MfaChallengeOutcome.SignedIn { Completion: LoginCompletion.CodeIssued issued } => issued.RedirectUrl,
            MfaChallengeOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown MFA challenge outcome '{outcome.GetType().Name}'.")
        };
    }

    /// <summary>
    /// Confirms the authenticator an authorization request's MFA challenge required with its first
    /// code, and returns the redirect that carries the request's code to the client.
    /// </summary>
    public async Task<string> VerifyMfaTotpEnrollmentAsync(
        string mfaToken,
        string enrollmentToken,
        string code,
        string authorizationRequestId,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var outcome = await Processes.VerifyTotpEnrollment(httpContext).ExecuteAsync(
            new VerifyTotpEnrollmentCommand(
                enrollmentToken,
                code,
                new TotpEnrollmentTarget.Challenge(mfaToken, MfaChallengeTarget.AuthorizationRequest, authorizationRequestId),
                SqlOSHttpRequestContext.From(httpContext, SqlOSRequestSurface.Hosted)),
            cancellationToken);
        return outcome switch
        {
            TotpEnrollmentVerifyOutcome.SignedIn { Completion: LoginCompletion.CodeIssued issued } => issued.RedirectUrl,
            TotpEnrollmentVerifyOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown enrollment outcome '{outcome.GetType().Name}'.")
        };
    }

    /// <summary>
    /// The hub adapter's continuation of an authorization request once its MFA challenge was answered
    /// (<see cref="SqlOSHttpLoginCompletion"/>): the request, which must still be active, gets its code
    /// for the challenge's organization, and <c>user.login.mfa</c> is audited. The second factor is
    /// fresh authentication: a session that aged past <c>max_age</c> while the person finished the
    /// challenge is not refused at issuance, and the code's <c>auth_time</c> is
    /// <paramref name="authenticatedAt"/>.
    /// </summary>
    internal async Task<string> IssueCodeAfterMfaAsync(
        string authorizationRequestId,
        SqlOSUser user,
        string? organizationId,
        string authenticationMethod,
        DateTime authenticatedAt,
        bool credentialSignIn,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var authorizationRequest = await GetRequiredAuthorizationRequestAsync(authorizationRequestId, cancellationToken);
        var redirectUrl = await IssueAuthorizationRedirectAsync(
            authorizationRequest,
            user,
            organizationId,
            authenticationMethod,
            httpContext,
            cancellationToken,
            knownAuthenticatedAt: authenticatedAt,
            credentialSignIn ? SqlOSSignInEvidence.Credential : SqlOSSignInEvidence.PresentedSession);

        await _adminService.RecordAuditAsync(
            "user.login.mfa",
            "user",
            user.Id,
            userId: user.Id,
            organizationId: organizationId,
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
        DateTime? knownAuthenticatedAt,
        SqlOSSignInEvidence evidence)
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
                evidence,
                cancellationToken);
        }

        return new SqlOSAuthorizationRequestLoginResult(
            await IssueAssuredAuthorizationRedirectAsync(
                decision.Assurance!,
                authorizationRequest,
                user,
                httpContext,
                cancellationToken,
                knownAuthenticatedAt,
                evidence),
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
        SqlOSSignInEvidence evidence,
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
            cancellationToken,
            credentialSignIn: evidence == SqlOSSignInEvidence.Credential);

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
        DateTime? knownAuthenticatedAt = null,
        SqlOSSignInEvidence evidence = SqlOSSignInEvidence.PresentedSession)
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
            knownAuthenticatedAt,
            evidence);
    }

    private async Task<string> IssueAssuredAuthorizationRedirectAsync(
        SqlOSIssuanceAssurance assurance,
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSUser user,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        DateTime? knownAuthenticatedAt,
        SqlOSSignInEvidence evidence)
    {
        // Only a sign-in that rests on the presented issuer session must prove that session
        // is still live. A credential sign-in treats a revoked or cleaned-up cookie as signed
        // out and replaces it with a new family below.
        var continuesPresentedSession = evidence == SqlOSSignInEvidence.PresentedSession;
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
                authenticationMethod,
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
            if (continuesPresentedSession
                && !await _issuerSessionService.CanContinuePresentingSessionAsync(httpContext, cancellationToken))
            {
                throw new InvalidOperationException(SqlOSIssuerSessionService.SessionNoLongerActiveMessage);
            }

            authorizationRequest.ResolvedAuthMethod = authenticationMethod;
            authorizationRequest.ResolvedOrganizationId = organizationId;
            await _context.SaveChangesAsync(cancellationToken);
            await SignInForAuthorizationAsync(httpContext, user, organizationId, authenticationMethod, authenticatedAt, continuesPresentedSession, cancellationToken);

            return QueryHelpers.AddQueryString(
                $"{_options.BasePath.TrimEnd('/')}/device/approve",
                "request",
                authorizationRequest.Id);
        }

        if (continuesPresentedSession
            && !await _issuerSessionService.CanContinuePresentingSessionAsync(httpContext, cancellationToken))
        {
            throw new InvalidOperationException(SqlOSIssuerSessionService.SessionNoLongerActiveMessage);
        }

        var rawCode = _cryptoService.GenerateOpaqueToken();
        var codeHash = _cryptoService.HashToken(rawCode);
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
            CodeHash = codeHash,
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

        try
        {
            await SignInForAuthorizationAsync(httpContext, user, organizationId, authenticationMethod, authenticatedAt, continuesPresentedSession, cancellationToken);
        }
        catch (InvalidOperationException ex) when (string.Equals(
            ex.Message,
            SqlOSIssuerSessionService.SessionNoLongerActiveMessage,
            StringComparison.Ordinal))
        {
            await ConsumeIssuedAuthorizationCodeAsync(codeHash, cancellationToken);
            throw;
        }

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
    /// Signs the browser in for an issued authorization. A sign-in that rests on the presented
    /// session renews that session's family and fails closed when the family was revoked,
    /// including a logout that races the renewal. A credential sign-in continues a live
    /// presented family but starts a new one when the presented cookie is dead.
    /// </summary>
    private async Task SignInForAuthorizationAsync(
        HttpContext httpContext,
        SqlOSUser user,
        string? organizationId,
        string authenticationMethod,
        DateTime? authenticatedAt,
        bool continueExistingSession,
        CancellationToken cancellationToken)
    {
        await _issuerSessionService.SignInAsync(
            httpContext,
            user,
            organizationId,
            authenticationMethod,
            authenticatedAt,
            continueExistingSession,
            cancellationToken);
        if (!await _issuerSessionService.CanContinuePresentingSessionAsync(httpContext, cancellationToken))
        {
            throw new InvalidOperationException(SqlOSIssuerSessionService.SessionNoLongerActiveMessage);
        }
    }

    private async Task ConsumeIssuedAuthorizationCodeAsync(string codeHash, CancellationToken cancellationToken)
    {
        var code = await _context.Set<SqlOSAuthorizationCode>()
            .FirstOrDefaultAsync(x => x.CodeHash == codeHash && x.ConsumedAt == null, cancellationToken);
        if (code == null)
        {
            return;
        }

        code.ConsumedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves the moment the user actually authenticated for the sign-in completing
    /// now. When the request carries a live issuer session for the same user (silent
    /// SSO reuse), the original authentication time is preserved; otherwise the user
    /// authenticated interactively in this request and the moment is now.
    /// </summary>
    private async Task<DateTime> ResolveAuthenticatedAtAsync(
        HttpContext httpContext,
        string userId,
        CancellationToken cancellationToken)
    {
        var existingSession = await _issuerSessionService.TryGetSessionAsync(httpContext, cancellationToken);
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

    /// <remarks>
    /// A <c>refresh_token</c> grant bound to a confidential client is rejected here
    /// with <see cref="SqlOSClientAuthenticationException"/>; the mapped
    /// <c>POST /token</c> endpoint authenticates the client before exchange.
    /// </remarks>
    public Task<SqlOSTokenEndpointResult> ExchangeAuthorizationCodeAsync(
        SqlOSTokenRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
        => ExchangeAuthorizationCodeAsync(request, refreshClientAdmission: null, httpContext, cancellationToken);

    internal async Task<SqlOSTokenEndpointResult> ExchangeAuthorizationCodeAsync(
        SqlOSTokenRequest request,
        SqlOSRefreshClientAdmission? refreshClientAdmission,
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
                refreshClientAdmission,
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


    private SqlOSInvitationService RequireInvitationService()
        => _invitationService ?? throw new InvalidOperationException("SqlOS invitations are not configured.");

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

/// <summary>
/// What proves the user's identity when an authorization sign-in is issued. It decides how
/// the issuer session cookie the browser presents is treated.
/// </summary>
internal enum SqlOSSignInEvidence
{
    /// <summary>
    /// The presented issuer session (silent reuse, or device approval by a signed-in browser).
    /// Issuance fails closed unless that session's family is still live, and it renews the
    /// session inside that family.
    /// </summary>
    PresentedSession = 0,

    /// <summary>
    /// A credential the user presented in this flow. A revoked or cleaned-up issuer cookie
    /// counts as signed out: issuance starts a new family and replaces the cookie.
    /// </summary>
    Credential = 1
}

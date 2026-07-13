using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
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
    private readonly SqlOSMfaPolicyService? _mfaPolicyService;
    private readonly SqlOSTotpMfaService? _totpMfaService;

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
        SqlOSTotpMfaService? totpMfaService = null)
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
        _mfaPolicyService = mfaPolicyService;
        _totpMfaService = totpMfaService;
    }

    public async Task<SqlOSAuthorizationServerMetadataDto> GetMetadataAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var credentialSettings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        var configuredScopes = await _context.Set<SqlOSClientApplication>()
            .AsNoTracking()
            .Select(x => x.AllowedScopesJson)
            .ToListAsync(cancellationToken);

        var scopes = configuredScopes
            .SelectMany(ParseJsonArray)
            .Concat(credentialSettings.EnabledCredentialTypes.Select(x => $"auth:{x}"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var origin = GetPublicOrigin(httpContext);
        var basePath = _options.BasePath.TrimEnd('/');
        var grantTypes = _options.DeviceAuthorization.Enabled
            ? new[] { SqlOSOAuthGrantTypes.AuthorizationCode, SqlOSOAuthGrantTypes.RefreshToken, SqlOSOAuthGrantTypes.DeviceCode }
            : new[] { SqlOSOAuthGrantTypes.AuthorizationCode, SqlOSOAuthGrantTypes.RefreshToken };

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
            GrantTypesSupported = grantTypes,
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

        if (input.State.Length > 2048)
        {
            throw new InvalidOperationException("State cannot exceed 2048 characters.");
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
    }

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
        await _passwordLoginAbuseService.EnsureAllowedAsync(attempt, cancellationToken);

        var emailRecord = await _context.Set<SqlOSUserEmail>()
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);
        if (emailRecord == null)
        {
            await _passwordLoginAbuseService.RecordFailureAsync(attempt, "unknown_email", cancellationToken);
            throw new InvalidOperationException(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
        }

        attempt = attempt with { UserId = emailRecord.UserId };
        await _passwordLoginAbuseService.EnsureAllowedAsync(attempt, cancellationToken);

        if (_options.RequireVerifiedEmailForPasswordLogin && !emailRecord.IsVerified && !allowUnverifiedEmailForInvitation)
        {
            throw new InvalidOperationException("Email must be verified before password login.");
        }

        var credential = await _context.Set<SqlOSCredential>()
            .FirstOrDefaultAsync(x => x.UserId == emailRecord.UserId && x.Type == "password" && x.RevokedAt == null, cancellationToken);
        if (credential == null)
        {
            await _passwordLoginAbuseService.RecordFailureAsync(attempt, "missing_password_credential", cancellationToken);
            throw new InvalidOperationException(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
        }

        if (!_cryptoService.VerifyPassword(credential.SecretHash, password))
        {
            await _passwordLoginAbuseService.RecordFailureAsync(attempt, "invalid_password", cancellationToken);
            throw new InvalidOperationException(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
        }

        var user = await _context.Set<SqlOSUser>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == emailRecord.UserId, cancellationToken);
        if (user == null || !user.IsActive)
        {
            await _passwordLoginAbuseService.RecordFailureAsync(attempt, "inactive_user", cancellationToken);
            throw new InvalidOperationException(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
        }

        credential.LastUsedAt = DateTime.UtcNow;
        await _passwordLoginAbuseService.RecordSuccessAsync(attempt, cancellationToken);
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

        SqlOSSignupJoinPolicy.RejectUnauthorizedOrganizationJoin(organizationId);

        var user = await _adminService.CreateUserAsync(
            new SqlOSCreateUserRequest(displayName, email, password),
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(organizationName))
        {
            var createdOrganization = await _adminService.CreateOrganizationAsync(
                new SqlOSCreateOrganizationRequest(organizationName, null),
                cancellationToken);
            await _adminService.CreateMembershipAsync(createdOrganization.Id, new SqlOSCreateMembershipRequest(user.Id, "owner"), cancellationToken);
        }

        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);

        return new SqlOSPasswordAuthenticationResult(user, organizations, "password");
    }

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
        var mfaResult = await TryCreateMfaAuthorizationResultAsync(
            authorizationRequest,
            user,
            organizationId,
            payload.AuthenticationMethod,
            organizations,
            cancellationToken);
        if (mfaResult != null)
        {
            return mfaResult;
        }

        return new SqlOSAuthorizationRequestLoginResult(
            await IssueAuthorizationRedirectAsync(authorizationRequest, user, organizationId, payload.AuthenticationMethod, httpContext, cancellationToken),
            false,
            null,
            organizations,
            AuthorizationRequestId: authorizationRequest.Id);
    }

    public async Task<SqlOSAuthorizationRequestLoginResult> CompleteAuthorizationRequestLoginAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSUser user,
        string authenticationMethod,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(authorizationRequest.InvitationId))
        {
            var invitationAcceptance = await RequireInvitationService().AcceptBoundInvitationAsync(
                authorizationRequest.InvitationId,
                user.Id,
                saveChanges: true,
                httpContext,
                cancellationToken);
            var invitationOrganizationId = invitationAcceptance?.OrganizationId;
            var invitationOrganizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);
            var mfaResult = await TryCreateMfaAuthorizationResultAsync(
                authorizationRequest,
                user,
                invitationOrganizationId,
                authenticationMethod,
                invitationOrganizations,
                cancellationToken);
            if (mfaResult != null)
            {
                return mfaResult;
            }

            return new SqlOSAuthorizationRequestLoginResult(
                await IssueAuthorizationRedirectAsync(
                    authorizationRequest,
                    user,
                    invitationOrganizationId,
                    authenticationMethod,
                    httpContext,
                    cancellationToken),
                false,
                null,
                await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken));
        }

        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);

        if (!string.IsNullOrWhiteSpace(authorizationRequest.OrganizationId))
        {
            if (organizations.All(x => x.Id != authorizationRequest.OrganizationId))
            {
                throw new InvalidOperationException("The selected organization is not available to this user.");
            }

            var mfaResult = await TryCreateMfaAuthorizationResultAsync(
                authorizationRequest,
                user,
                authorizationRequest.OrganizationId,
                authenticationMethod,
                organizations,
                cancellationToken);
            if (mfaResult != null)
            {
                return mfaResult;
            }

            return new SqlOSAuthorizationRequestLoginResult(
                await IssueAuthorizationRedirectAsync(
                    authorizationRequest,
                    user,
                    authorizationRequest.OrganizationId,
                    authenticationMethod,
                    httpContext,
                    cancellationToken),
                false,
                null,
                organizations);
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
                    cancellationToken),
                organizations);
        }

        var selectedOrganizationId = organizations.FirstOrDefault()?.Id;
        var directMfaResult = await TryCreateMfaAuthorizationResultAsync(
            authorizationRequest,
            user,
            selectedOrganizationId,
            authenticationMethod,
            organizations,
            cancellationToken);
        if (directMfaResult != null)
        {
            return directMfaResult;
        }

        return new SqlOSAuthorizationRequestLoginResult(
            await IssueAuthorizationRedirectAsync(
                authorizationRequest,
                user,
                selectedOrganizationId,
                authenticationMethod,
                httpContext,
                cancellationToken),
            false,
            null,
            organizations);
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
        var redirectUrl = await IssueAuthorizationRedirectAsync(
            authorizationRequest,
            user,
            token.OrganizationId,
            authenticationMethod,
            httpContext,
            cancellationToken);

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

    private async Task<SqlOSAuthorizationRequestLoginResult?> TryCreateMfaAuthorizationResultAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSUser user,
        string? organizationId,
        string authenticationMethod,
        IReadOnlyList<SqlOSOrganizationOption> organizations,
        CancellationToken cancellationToken)
    {
        if (_mfaPolicyService == null)
        {
            return null;
        }

        var evaluation = await _mfaPolicyService.EvaluateAsync(user.Id, organizationId, authenticationMethod, cancellationToken);
        if (!evaluation.RequiresMfa)
        {
            return null;
        }

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

    public async Task<string> IssueAuthorizationRedirectAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSUser user,
        string? organizationId,
        string authenticationMethod,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
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
            await _authPageSessionService.SignInAsync(httpContext, user, organizationId, authenticationMethod, cancellationToken);

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

        if (!_cryptoService.VerifyPkceCodeVerifier(request.CodeVerifier ?? string.Empty, authorizationCode.CodeChallenge, authorizationCode.CodeChallengeMethod))
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

    private SqlOSInvitationService RequireInvitationService()
        => _invitationService ?? throw new InvalidOperationException("SqlOS invitations are not configured.");

    private SqlOSTotpMfaService RequireTotpMfaService()
        => _totpMfaService ?? throw new InvalidOperationException("TOTP MFA service is not registered.");

    private bool SupportsDatabaseTransactions()
        => !string.Equals(_context.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal);

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
        => exception.InnerException is SqlException { Number: 2601 or 2627 };
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

public sealed record SqlOSAuthorizationRequestLoginResult(
    string? RedirectUrl,
    bool RequiresOrganizationSelection,
    string? PendingToken,
    IReadOnlyList<SqlOSOrganizationOption> Organizations,
    bool RequiresMfa = false,
    string? MfaToken = null,
    bool RequiresMfaEnrollment = false,
    IReadOnlyList<string>? MfaMethods = null,
    string? AuthorizationRequestId = null);

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

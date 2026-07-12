using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSOidcAuthService
{
    private const string PublicClaimValidationFailure = "The social login could not be completed.";
    private const int MaxAppleCallbackPayloadBytes = 4096;
    private const int MaxUserDisplayNameChars = 200;
    private static readonly IReadOnlyList<string> DefaultOidcScopes = ["openid", "email", "profile"];
    private static readonly IReadOnlyList<string> DefaultAppleScopes = ["name", "email"];
    private static readonly IReadOnlyList<string> DefaultGitHubScopes = ["read:user", "user:email"];

    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SqlOSOidcAuthService> _logger;

    public SqlOSOidcAuthService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSCryptoService cryptoService,
        IHttpClientFactory httpClientFactory,
        ILogger<SqlOSOidcAuthService> logger)
    {
        _context = context;
        _adminService = adminService;
        _cryptoService = cryptoService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SqlOSOidcProviderSummary>> ListEnabledProvidersAsync(CancellationToken cancellationToken = default)
    {
        var connections = await _context.Set<SqlOSOidcConnection>()
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.DisplayName)
            .ToListAsync(cancellationToken);

        return connections
            .Select(x => new
            SqlOSOidcProviderSummary(
                x.Id,
                x.ProviderType.ToString(),
                x.DisplayName,
                x.IsEnabled,
                SqlOSOidcProviderLogoCatalog.ResolveEffectiveLogoDataUrl(x.ProviderType, x.LogoDataUrl),
                x.Protocol.ToString()))
            .ToList();
    }

    public async Task<SqlOSStartOidcAuthorizationResult> StartAuthorizationAsync(
        SqlOSStartOidcAuthorizationRequest request,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await RequireEnabledConnectionAsync(request.ConnectionId, cancellationToken);
        ValidateCallbackUri(connection, request.CallbackUri);

        var resolved = await ResolveConfigurationAsync(connection, cancellationToken);
        if (resolved.Protocol == SqlOSSocialProviderProtocol.Oidc
            && (!string.Equals(request.CodeChallengeMethod, "S256", StringComparison.Ordinal)
                || !_cryptoService.IsValidS256PkceCodeChallenge(request.CodeChallenge)))
        {
            throw new InvalidOperationException(
                "OIDC authorization requires a valid RFC 7636 S256 PKCE code challenge.");
        }

        var authorizationParameters = new Dictionary<string, string?>
        {
            ["client_id"] = connection.ClientId,
            ["redirect_uri"] = request.CallbackUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', resolved.Scopes),
            ["state"] = request.State
        };

        if (resolved.Protocol == SqlOSSocialProviderProtocol.Oidc)
        {
            authorizationParameters["nonce"] = request.Nonce;
            authorizationParameters["code_challenge"] = request.CodeChallenge;
            authorizationParameters["code_challenge_method"] = request.CodeChallengeMethod;
            authorizationParameters["login_hint"] = request.Email;
        }
        else if (connection.ProviderType == SqlOSOidcProviderType.GitHub)
        {
            authorizationParameters["login"] = request.Email;
        }

        if (connection.ProviderType == SqlOSOidcProviderType.Apple)
        {
            authorizationParameters["response_mode"] = "form_post";
        }

        var authorizationUrl = QueryHelpers.AddQueryString(resolved.AuthorizationEndpoint, authorizationParameters);

        await _adminService.RecordAuditAsync(
            "user.login.oidc.start",
            "oidc_connection",
            connection.Id,
            ipAddress: ipAddress,
            data: new
            {
                provider = connection.ProviderType.ToString(),
                request.Email,
                request.ClientId,
                request.CallbackUri
            },
            cancellationToken: cancellationToken);

        return new SqlOSStartOidcAuthorizationResult(
            authorizationUrl,
            connection.Id,
            connection.ProviderType,
            connection.DisplayName,
            ParseJsonArray(connection.AllowedCallbackUrisJson));
    }

    public async Task<SqlOSCompleteOidcAuthorizationResult> CompleteAuthorizationAsync(
        SqlOSCompleteOidcAuthorizationRequest request,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        SqlOSOidcConnection? connection = null;

        try
        {
            connection = await RequireEnabledConnectionAsync(request.ConnectionId, cancellationToken);

            var resolved = await ResolveConfigurationAsync(connection, cancellationToken);
            var providerUser = resolved.Protocol == SqlOSSocialProviderProtocol.OAuthProfile
                ? await CompleteOAuthProfileAuthorizationAsync(connection, resolved, request, cancellationToken)
                : await CompleteOidcAuthorizationAsync(connection, resolved, request, ipAddress, cancellationToken);
            var provisioned = await ResolveOrProvisionUserAsync(connection, resolved, providerUser, ipAddress, cancellationToken);
            var organizations = await _adminService.GetUserOrganizationsAsync(provisioned.User.Id, cancellationToken);
            var organizationId = organizations.Count == 1 ? organizations[0].Id : null;
            var authMethod = connection.ProviderType switch
            {
                SqlOSOidcProviderType.Google => "google",
                SqlOSOidcProviderType.Microsoft => "microsoft",
                SqlOSOidcProviderType.Apple => "apple",
                SqlOSOidcProviderType.GitHub => "github",
                SqlOSOidcProviderType.Custom => "oidc",
                _ => "oidc"
            };

            await _adminService.RecordAuditAsync(
                "user.login.oidc.success",
                "user",
                provisioned.User.Id,
                userId: provisioned.User.Id,
                organizationId: organizationId,
                ipAddress: ipAddress,
                data: new
                {
                    provider = connection.ProviderType.ToString(),
                    protocol = resolved.Protocol.ToString(),
                    oidcConnectionId = connection.Id
                },
                cancellationToken: cancellationToken);

            return new SqlOSCompleteOidcAuthorizationResult(
                connection.Id,
                connection.ProviderType,
                provisioned.User.Id,
                provisioned.User.DefaultEmail ?? providerUser.Email,
                provisioned.User.DisplayName,
                organizationId,
                authMethod,
                organizations.Count)
            {
                UserCreated = provisioned.Created
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OIDC authentication failed for connection {ConnectionId}.", request.ConnectionId);
            await _adminService.RecordAuditAsync(
                "user.login.oidc.error",
                "oidc_connection",
                connection?.Id ?? request.ConnectionId,
                ipAddress: ipAddress,
                data: new
                {
                    error = ex.Message
                },
                cancellationToken: cancellationToken);
            throw;
        }
    }

    private async Task<ProviderUser> CompleteOidcAuthorizationAsync(
        SqlOSOidcConnection connection,
        ResolvedOidcConfiguration resolved,
        SqlOSCompleteOidcAuthorizationRequest request,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var tokenPayload = await ExchangeCodeAsync(connection, resolved, request, cancellationToken);
        var idToken = tokenPayload.IdToken
            ?? throw new InvalidOperationException("The OIDC provider token response did not include an ID token.");
        var idTokenPrincipal = await ValidateIdTokenAsync(connection, resolved, idToken, request.Nonce, cancellationToken);
        IReadOnlyDictionary<string, string>? userInfoClaims = resolved.UseUserInfo && !string.IsNullOrWhiteSpace(resolved.UserInfoEndpoint)
            ? await LoadUserInfoClaimsAsync(resolved.UserInfoEndpoint!, tokenPayload.AccessToken, cancellationToken)
            : null;
        return await MapProviderUserAsync(
            connection,
            resolved,
            idTokenPrincipal,
            userInfoClaims,
            request.UserPayloadJson,
            ipAddress,
            cancellationToken);
    }

    private async Task<ProviderUser> CompleteOAuthProfileAuthorizationAsync(
        SqlOSOidcConnection connection,
        ResolvedOidcConfiguration resolved,
        SqlOSCompleteOidcAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        if (connection.ProviderType != SqlOSOidcProviderType.GitHub)
        {
            throw new InvalidOperationException($"Unsupported OAuth profile provider '{connection.ProviderType}'.");
        }

        var tokenPayload = await ExchangeOAuthProfileCodeAsync(connection, resolved, request, cancellationToken);
        return await LoadGitHubUserAsync(tokenPayload.AccessToken, cancellationToken);
    }

    private async Task<ProviderTokenPayload> ExchangeOAuthProfileCodeAsync(
        SqlOSOidcConnection connection,
        ResolvedOidcConfiguration resolved,
        SqlOSCompleteOidcAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient(nameof(SqlOSOidcAuthService));
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, resolved.TokenEndpoint);
        tokenRequest.Headers.Accept.ParseAdd("application/json");

        tokenRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = connection.ClientId,
            ["client_secret"] = CreateClientSecret(connection),
            ["code"] = request.Code,
            ["redirect_uri"] = request.CallbackUri
        });

        using var response = await httpClient.SendAsync(tokenRequest, cancellationToken);
        using var payload = await ReadJsonAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(payload.RootElement.TryGetProperty("error_description", out var description)
                ? description.GetString() ?? "The OAuth provider rejected the authorization code."
                : "The OAuth provider rejected the authorization code.");
        }

        var accessToken = payload.RootElement.TryGetProperty("access_token", out var accessTokenElement)
            ? accessTokenElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("The OAuth provider token response did not include an access token.");
        }

        return new ProviderTokenPayload(accessToken!, null);
    }

    private async Task<ProviderTokenPayload> ExchangeCodeAsync(
        SqlOSOidcConnection connection,
        ResolvedOidcConfiguration resolved,
        SqlOSCompleteOidcAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient(nameof(SqlOSOidcAuthService));
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, resolved.TokenEndpoint);

        var formValues = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = connection.ClientId,
            ["code"] = request.Code,
            ["redirect_uri"] = request.CallbackUri,
            ["code_verifier"] = request.CodeVerifier
        };

        var clientSecret = CreateClientSecret(connection);
        if (connection.ClientAuthMethod == SqlOSOidcClientAuthMethod.ClientSecretBasic && connection.ProviderType != SqlOSOidcProviderType.Apple)
        {
            var bytes = Encoding.UTF8.GetBytes($"{connection.ClientId}:{clientSecret}");
            tokenRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
        }
        else
        {
            formValues["client_secret"] = clientSecret;
        }

        tokenRequest.Content = new FormUrlEncodedContent(formValues);
        using var response = await httpClient.SendAsync(tokenRequest, cancellationToken);
        using var payload = await ReadJsonAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(payload.RootElement.TryGetProperty("error_description", out var description)
                ? description.GetString() ?? "The OIDC provider rejected the authorization code."
                : "The OIDC provider rejected the authorization code.");
        }

        var accessToken = payload.RootElement.GetProperty("access_token").GetString();
        var idToken = payload.RootElement.TryGetProperty("id_token", out var idTokenElement)
            ? idTokenElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("The OIDC provider token response did not include an access token.");
        }

        if (string.IsNullOrWhiteSpace(idToken))
        {
            throw new InvalidOperationException("The OIDC provider token response did not include an ID token.");
        }

        return new ProviderTokenPayload(accessToken, idToken!);
    }

    private async Task<ClaimsPrincipal> ValidateIdTokenAsync(
        SqlOSOidcConnection connection,
        ResolvedOidcConfiguration resolved,
        string idToken,
        string expectedNonce,
        CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient(nameof(SqlOSOidcAuthService));
        using var jwksResponse = await httpClient.GetAsync(resolved.JwksUri!, cancellationToken);
        using var jwksPayload = await ReadJsonAsync(jwksResponse, cancellationToken);
        if (!jwksResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The OIDC provider JWKS endpoint failed.");
        }

        var jwks = new JsonWebKeySet(jwksPayload.RootElement.GetRawText());
        var handler = new JwtSecurityTokenHandler
        {
            MapInboundClaims = false
        };
        var principal = handler.ValidateToken(idToken, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = resolved.Issuer,
            IssuerValidator = (issuer, _, _) => ValidateResolvedIssuer(issuer, resolved.Issuer),
            ValidateAudience = true,
            ValidAudience = connection.ClientId,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = jwks.GetSigningKeys(),
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        }, out _);

        var nonce = principal.FindFirstValue("nonce");
        if (string.IsNullOrWhiteSpace(nonce) || !string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("OIDC nonce validation failed.");
        }

        return principal;
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadUserInfoClaimsAsync(string userInfoEndpoint, string accessToken, CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient(nameof(SqlOSOidcAuthService));
        using var request = new HttpRequestMessage(HttpMethod.Get, userInfoEndpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        using var payload = await ReadJsonAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The OIDC provider user info request failed.");
        }

        var claims = FlattenJson(payload.RootElement);
        if (payload.RootElement.ValueKind != JsonValueKind.Object ||
            !payload.RootElement.TryGetProperty("sub", out var subjectElement) ||
            subjectElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(subjectElement.GetString()))
        {
            // OIDC Core requires UserInfo sub to be a non-empty JSON string. Removing any
            // flattened non-string representation makes the validation path fail closed.
            claims.Remove("sub");
        }

        return claims;
    }

    private async Task<ProviderUser> LoadGitHubUserAsync(string accessToken, CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient(nameof(SqlOSOidcAuthService));

        using var profileRequest = CreateGitHubApiRequest("https://api.github.com/user", accessToken);
        using var profileResponse = await httpClient.SendAsync(profileRequest, cancellationToken);
        using var profilePayload = await ReadJsonAsync(profileResponse, cancellationToken);
        if (!profileResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The GitHub profile request failed.");
        }

        var root = profilePayload.RootElement;
        var subject = root.TryGetProperty("id", out var idElement) ? idElement.ToString() : null;
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new InvalidOperationException("GitHub did not return a stable user id.");
        }

        var login = root.TryGetProperty("login", out var loginElement) && loginElement.ValueKind == JsonValueKind.String
            ? loginElement.GetString()
            : null;
        var name = root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;

        using var emailRequest = CreateGitHubApiRequest("https://api.github.com/user/emails", accessToken);
        using var emailResponse = await httpClient.SendAsync(emailRequest, cancellationToken);
        using var emailPayload = await ReadJsonAsync(emailResponse, cancellationToken);
        if (!emailResponse.IsSuccessStatusCode || emailPayload.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The GitHub email request failed.");
        }

        string? primaryVerifiedEmail = null;
        foreach (var emailElement in emailPayload.RootElement.EnumerateArray())
        {
            var isPrimary = emailElement.TryGetProperty("primary", out var primaryElement) && primaryElement.GetBoolean();
            var isVerified = emailElement.TryGetProperty("verified", out var verifiedElement) && verifiedElement.GetBoolean();
            if (!isPrimary || !isVerified)
            {
                continue;
            }

            primaryVerifiedEmail = emailElement.TryGetProperty("email", out var emailValue) && emailValue.ValueKind == JsonValueKind.String
                ? emailValue.GetString()
                : null;
            break;
        }

        if (string.IsNullOrWhiteSpace(primaryVerifiedEmail))
        {
            throw new InvalidOperationException("GitHub did not return a verified primary email address.");
        }

        return new ProviderUser(
            subject!,
            primaryVerifiedEmail!,
            string.IsNullOrWhiteSpace(name) ? login ?? primaryVerifiedEmail! : name!,
            EmailVerified: true,
            CanAutoLinkByEmail: true);
    }

    private static HttpRequestMessage CreateGitHubApiRequest(string url, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("SqlOS");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private async Task<ProviderUser> MapProviderUserAsync(
        SqlOSOidcConnection connection,
        ResolvedOidcConfiguration resolved,
        ClaimsPrincipal idTokenPrincipal,
        IReadOnlyDictionary<string, string>? userInfoClaims,
        string? callbackPayload,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var idTokenClaims = idTokenPrincipal.Claims
            .GroupBy(x => x.Type, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First().Value, StringComparer.Ordinal);

        var idTokenSubject = GetClaim(idTokenClaims, "sub");
        if (idTokenSubject == null)
        {
            await RejectClaimSetAsync(
                connection,
                "id_token_subject_missing",
                idTokenSubject: null,
                userInfoSubject: null,
                ipAddress,
                cancellationToken);
        }

        if (userInfoClaims != null)
        {
            var userInfoSubject = GetClaim(userInfoClaims, "sub");
            if (userInfoSubject == null || !string.Equals(idTokenSubject, userInfoSubject, StringComparison.Ordinal))
            {
                await RejectClaimSetAsync(
                    connection,
                    userInfoSubject == null ? "userinfo_subject_missing" : "userinfo_subject_mismatch",
                    idTokenSubject,
                    userInfoSubject,
                    ipAddress,
                    cancellationToken);
            }
        }

        // The locally stored external subject is always taken from the validated ID token.
        // A custom mapping can select another signed claim, but UserInfo can never replace it.
        var subject = GetClaim(idTokenClaims, resolved.ClaimMapping.SubjectClaim);
        if (subject == null)
        {
            await RejectClaimSetAsync(
                connection,
                "mapped_id_token_subject_missing",
                idTokenSubject,
                userInfoSubject: null,
                ipAddress,
                cancellationToken);
        }

        // Email and its verification bit are an inseparable claim pair. Prefer a subject-bound
        // UserInfo pair when it contains an email; otherwise use the pair from the ID token.
        var identityClaims = ResolveEmailClaims(userInfoClaims, resolved.ClaimMapping, "userinfo", allowPreferredUsername: false)
            ?? ResolveEmailClaims(idTokenClaims, resolved.ClaimMapping, "id_token", allowPreferredUsername: false)
            ?? ResolveEmailClaims(userInfoClaims, resolved.ClaimMapping, "userinfo", allowPreferredUsername: true)
            ?? ResolveEmailClaims(idTokenClaims, resolved.ClaimMapping, "id_token", allowPreferredUsername: true);
        var email = identityClaims?.Email;
        var emailVerified = identityClaims?.EmailVerified ?? false;

        var firstName = GetClaim(userInfoClaims, resolved.ClaimMapping.FirstNameClaim)
            ?? GetClaim(idTokenClaims, resolved.ClaimMapping.FirstNameClaim);
        var lastName = GetClaim(userInfoClaims, resolved.ClaimMapping.LastNameClaim)
            ?? GetClaim(idTokenClaims, resolved.ClaimMapping.LastNameClaim);
        var displayName = GetClaim(userInfoClaims, resolved.ClaimMapping.DisplayNameClaim)
            ?? GetClaim(idTokenClaims, resolved.ClaimMapping.DisplayNameClaim);

        if (connection.ProviderType == SqlOSOidcProviderType.Apple)
        {
            var appleName = ParseAppleCallbackDisplayName(callbackPayload);
            firstName ??= appleName.FirstName;
            lastName ??= appleName.LastName;
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = string.Join(' ', new[] { firstName, lastName }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = email ?? subject;
        }

        displayName = TruncateUtf16(displayName!, MaxUserDisplayNameChars);

        // Auto-linking is deliberately secure-by-default for every OIDC provider: only the
        // verification claim from the same authenticated claim set as the email can authorize it.
        var canAutoLinkByEmail = !string.IsNullOrWhiteSpace(email) && emailVerified;

        return new ProviderUser(
            subject!,
            email ?? string.Empty,
            displayName ?? string.Empty,
            emailVerified,
            canAutoLinkByEmail,
            identityClaims?.Source);
    }

    private async Task<ProvisionedProviderUser> ResolveOrProvisionUserAsync(
        SqlOSOidcConnection connection,
        ResolvedOidcConfiguration resolved,
        ProviderUser providerUser,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerUser.Email))
        {
            throw new InvalidOperationException("The social login provider did not return a usable email address.");
        }

        if (resolved.RequireVerifiedEmail && !providerUser.EmailVerified)
        {
            await _adminService.RecordAuditAsync(
                "user.login.oidc.claim_mismatch",
                "oidc_connection",
                connection.Id,
                ipAddress: ipAddress,
                data: new
                {
                    provider = connection.ProviderType.ToString(),
                    oidcConnectionId = connection.Id,
                    claimSource = providerUser.EmailClaimSource,
                    reason = "verified_email_missing_from_source"
                },
                cancellationToken: cancellationToken);
            throw new InvalidOperationException(PublicClaimValidationFailure);
        }

        var externalIdentity = await _context.Set<SqlOSExternalIdentity>()
            .FirstOrDefaultAsync(
                x => x.OidcConnectionId == connection.Id && x.Subject == providerUser.Subject,
                cancellationToken);

        if (externalIdentity != null)
        {
            var existingUser = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == externalIdentity.UserId, cancellationToken);
            return new ProvisionedProviderUser(existingUser, Created: false);
        }

        SqlOSUser? user = null;
        var created = false;
        var normalizedEmail = SqlOSAdminService.NormalizeEmail(providerUser.Email);
        var existingEmail = await _context.Set<SqlOSUserEmail>()
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);

        if (existingEmail != null)
        {
            if (!providerUser.CanAutoLinkByEmail)
            {
                await _adminService.RecordAuditAsync(
                    "user.login.oidc.email_link_rejected",
                    "oidc_connection",
                    connection.Id,
                    ipAddress: ipAddress,
                    data: new
                    {
                        provider = connection.ProviderType.ToString(),
                        oidcConnectionId = connection.Id,
                        claimSource = providerUser.EmailClaimSource,
                        reason = "email_not_verified_in_source"
                    },
                    cancellationToken: cancellationToken);
                throw new InvalidOperationException(PublicClaimValidationFailure);
            }

            user = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == existingEmail.UserId, cancellationToken);
        }

        if (user == null)
        {
            user = new SqlOSUser
            {
                Id = _cryptoService.GenerateId("usr"),
                DisplayName = string.IsNullOrWhiteSpace(providerUser.DisplayName) ? providerUser.Email : providerUser.DisplayName,
                DefaultEmail = providerUser.Email,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.Set<SqlOSUser>().Add(user);
            _context.Set<SqlOSUserEmail>().Add(new SqlOSUserEmail
            {
                Id = _cryptoService.GenerateId("eml"),
                UserId = user.Id,
                Email = providerUser.Email,
                NormalizedEmail = normalizedEmail,
                IsPrimary = true,
                IsVerified = providerUser.EmailVerified,
                VerifiedAt = providerUser.EmailVerified ? DateTime.UtcNow : null,
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync(cancellationToken);

            await _adminService.RecordAuditAsync(
                "user.login.oidc.provisioned",
                "user",
                user.Id,
                userId: user.Id,
                data: new
                {
                    provider = connection.ProviderType.ToString(),
                    oidcConnectionId = connection.Id
                },
                cancellationToken: cancellationToken);

            created = true;
        }

        _context.Set<SqlOSExternalIdentity>().Add(new SqlOSExternalIdentity
        {
            Id = _cryptoService.GenerateId("ext"),
            UserId = user.Id,
            OidcConnectionId = connection.Id,
            Issuer = resolved.Issuer,
            Subject = providerUser.Subject,
            Email = providerUser.Email,
            CreatedAt = DateTime.UtcNow
        });

        await _context.SaveChangesAsync(cancellationToken);
        return new ProvisionedProviderUser(user, created);
    }

    private async Task<SqlOSOidcConnection> RequireEnabledConnectionAsync(string connectionId, CancellationToken cancellationToken)
        => await _context.Set<SqlOSOidcConnection>()
            .FirstOrDefaultAsync(x => x.Id == connectionId && x.IsEnabled, cancellationToken)
            ?? throw new InvalidOperationException("No enabled OIDC connection was found for this request.");

    private async Task<ResolvedOidcConfiguration> ResolveConfigurationAsync(SqlOSOidcConnection connection, CancellationToken cancellationToken)
    {
        var claimMapping = DeserializeClaimMapping(connection.ClaimMappingJson);
        var scopes = ResolveScopes(connection);

        if (!connection.UseDiscovery)
        {
            return new ResolvedOidcConfiguration(
                connection.Protocol,
                connection.ProviderType,
                connection.Issuer ?? throw new InvalidOperationException("The social login connection is missing an issuer."),
                connection.AuthorizationEndpoint ?? throw new InvalidOperationException("The social login connection is missing an authorization endpoint."),
                connection.TokenEndpoint ?? throw new InvalidOperationException("The social login connection is missing a token endpoint."),
                connection.UserInfoEndpoint,
                connection.Protocol == SqlOSSocialProviderProtocol.Oidc
                    ? connection.JwksUri ?? throw new InvalidOperationException("The OIDC connection is missing a JWKS URI.")
                    : connection.JwksUri,
                scopes,
                claimMapping,
                RequireVerifiedEmail: connection.ProviderType is SqlOSOidcProviderType.Google or SqlOSOidcProviderType.GitHub,
                UseUserInfo: connection.UseUserInfo && !string.IsNullOrWhiteSpace(connection.UserInfoEndpoint));
        }

        var discoveryUrl = connection.DiscoveryUrl ?? throw new InvalidOperationException("The OIDC connection is missing a discovery URL.");
        var httpClient = _httpClientFactory.CreateClient(nameof(SqlOSOidcAuthService));
        using var response = await httpClient.GetAsync(discoveryUrl, cancellationToken);
        using var payload = await ReadJsonAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The OIDC discovery endpoint failed.");
        }

        var root = payload.RootElement;
        var issuer = root.GetProperty("issuer").GetString()
            ?? throw new InvalidOperationException("The OIDC discovery document is missing an issuer.");
        var authorizationEndpoint = root.GetProperty("authorization_endpoint").GetString()
            ?? throw new InvalidOperationException("The OIDC discovery document is missing an authorization endpoint.");
        var tokenEndpoint = root.GetProperty("token_endpoint").GetString()
            ?? throw new InvalidOperationException("The OIDC discovery document is missing a token endpoint.");
        var jwksUri = root.GetProperty("jwks_uri").GetString()
            ?? throw new InvalidOperationException("The OIDC discovery document is missing a JWKS URI.");
        var userInfoEndpoint = root.TryGetProperty("userinfo_endpoint", out var userInfoElement) && userInfoElement.ValueKind == JsonValueKind.String
            ? userInfoElement.GetString()
            : connection.UserInfoEndpoint;

        return new ResolvedOidcConfiguration(
            connection.Protocol,
            connection.ProviderType,
            issuer,
            authorizationEndpoint,
            tokenEndpoint,
            userInfoEndpoint,
            jwksUri,
            scopes,
            claimMapping,
            RequireVerifiedEmail: connection.ProviderType == SqlOSOidcProviderType.Google,
            UseUserInfo: connection.UseUserInfo && !string.IsNullOrWhiteSpace(userInfoEndpoint));
    }

    private string CreateClientSecret(SqlOSOidcConnection connection)
    {
        return connection.ProviderType switch
        {
            SqlOSOidcProviderType.Apple => CreateAppleClientSecret(connection),
            _ => !string.IsNullOrWhiteSpace(connection.ClientSecretEncrypted)
                ? _cryptoService.UnprotectSecret(connection.ClientSecretEncrypted)
                : throw new InvalidOperationException("The OIDC connection is missing a client secret.")
        };
    }

    private string CreateAppleClientSecret(SqlOSOidcConnection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.AppleTeamId) ||
            string.IsNullOrWhiteSpace(connection.AppleKeyId) ||
            string.IsNullOrWhiteSpace(connection.ApplePrivateKeyEncrypted))
        {
            throw new InvalidOperationException("The Apple OIDC connection is missing its signing configuration.");
        }

        var privateKeyPem = _cryptoService.UnprotectSecret(connection.ApplePrivateKeyEncrypted);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(ReadPem(privateKeyPem), out _);

        var now = DateTimeOffset.UtcNow;
        var credentials = new SigningCredentials(new ECDsaSecurityKey(ecdsa)
        {
            KeyId = connection.AppleKeyId,
            // This ECDSA instance is intentionally request-scoped. Do not let IdentityModel cache
            // a signature provider that retains it after the using scope disposes the key.
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        }, SecurityAlgorithms.EcdsaSha256);
        var token = new JwtSecurityToken(
            issuer: connection.AppleTeamId,
            audience: "https://appleid.apple.com",
            claims:
            [
                new Claim("sub", connection.ClientId)
            ],
            notBefore: now.UtcDateTime,
            expires: now.AddMinutes(5).UtcDateTime,
            signingCredentials: credentials);
        token.Header["kid"] = connection.AppleKeyId;
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static byte[] ReadPem(string pem)
    {
        var lines = pem.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith("-----", StringComparison.Ordinal))
            .ToArray();
        return Convert.FromBase64String(string.Concat(lines));
    }

    private static void ValidateCallbackUri(SqlOSOidcConnection connection, string callbackUri)
    {
        var allowed = ParseJsonArray(connection.AllowedCallbackUrisJson);
        if (allowed.Count == 0)
        {
            throw new InvalidOperationException("This OIDC connection does not have any allowed callback URIs configured.");
        }

        if (!allowed.Contains(callbackUri, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The callback URI is not allowed for this OIDC connection.");
        }
    }

    private static string ValidateResolvedIssuer(string actualIssuer, string expectedIssuer)
    {
        if (string.Equals(actualIssuer, expectedIssuer, StringComparison.Ordinal))
        {
            return actualIssuer;
        }

        foreach (var marker in new[] { "{tenantid}", "{tenant-id}" })
        {
            var markerIndex = expectedIssuer.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                continue;
            }

            var prefix = expectedIssuer[..markerIndex];
            var suffix = expectedIssuer[(markerIndex + marker.Length)..];
            if (!actualIssuer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !actualIssuer.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tenantSegmentLength = actualIssuer.Length - prefix.Length - suffix.Length;
            if (tenantSegmentLength <= 0)
            {
                continue;
            }

            var tenantSegment = actualIssuer.Substring(prefix.Length, tenantSegmentLength);
            if (!tenantSegment.Contains('/'))
            {
                return actualIssuer;
            }
        }

        throw new SecurityTokenInvalidIssuerException(
            $"OIDC issuer validation failed. Expected '{expectedIssuer}' and received '{actualIssuer}'.");
    }

    private static SqlOSOidcClaimMapping DeserializeClaimMapping(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SqlOSOidcClaimMapping();
        }

        try
        {
            return JsonSerializer.Deserialize<SqlOSOidcClaimMapping>(json) ?? new SqlOSOidcClaimMapping();
        }
        catch
        {
            return new SqlOSOidcClaimMapping();
        }
    }

    private static IReadOnlyList<string> ResolveScopes(SqlOSOidcConnection connection)
    {
        var configured = ParseJsonArray(connection.ScopesJson);
        if (configured.Count > 0)
        {
            return configured;
        }

        return connection.ProviderType switch
        {
            SqlOSOidcProviderType.Apple => DefaultAppleScopes,
            SqlOSOidcProviderType.GitHub => DefaultGitHubScopes,
            _ => DefaultOidcScopes
        };
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private async Task RejectClaimSetAsync(
        SqlOSOidcConnection connection,
        string reason,
        string? idTokenSubject,
        string? userInfoSubject,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Rejected OIDC claims for connection {ConnectionId}: {Reason}. ID-token subject {IdTokenSubject}; UserInfo subject {UserInfoSubject}.",
            connection.Id,
            reason,
            idTokenSubject,
            userInfoSubject);

        await _adminService.RecordAuditAsync(
            "user.login.oidc.claim_mismatch",
            "oidc_connection",
            connection.Id,
            ipAddress: ipAddress,
            data: new
            {
                provider = connection.ProviderType.ToString(),
                oidcConnectionId = connection.Id,
                reason,
                anchorSubject = idTokenSubject,
                userInfoSubject
            },
            cancellationToken: cancellationToken);

        throw new InvalidOperationException(PublicClaimValidationFailure);
    }

    private static ResolvedEmailClaims? ResolveEmailClaims(
        IReadOnlyDictionary<string, string>? claims,
        SqlOSOidcClaimMapping mapping,
        string source,
        bool allowPreferredUsername)
    {
        var email = GetClaim(claims, mapping.EmailClaim);
        if (email != null)
        {
            return new ResolvedEmailClaims(
                email,
                ParseBooleanClaim(GetClaim(claims, mapping.EmailVerifiedClaim)),
                source);
        }

        // preferred_username is a login/display hint in OIDC, not the email value to which an
        // email_verified claim applies. It can provision an external-only account, but it cannot
        // borrow a verification bit or authorize email auto-linking.
        var preferredUsername = allowPreferredUsername
            ? GetClaim(claims, mapping.PreferredUsernameClaim)
            : null;
        return preferredUsername == null
            ? null
            : new ResolvedEmailClaims(preferredUsername, EmailVerified: false, Source: source);
    }

    private static string? GetClaim(IReadOnlyDictionary<string, string>? claims, string? claimType)
    {
        if (claims == null || string.IsNullOrWhiteSpace(claimType))
        {
            return null;
        }

        var value = claims.GetValueOrDefault(claimType);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static AppleCallbackDisplayName ParseAppleCallbackDisplayName(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || Encoding.UTF8.GetByteCount(payload) > MaxAppleCallbackPayloadBytes)
        {
            return new AppleCallbackDisplayName(null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.Object)
            {
                return new AppleCallbackDisplayName(null, null);
            }

            var firstName = nameElement.TryGetProperty("firstName", out var firstNameElement) && firstNameElement.ValueKind == JsonValueKind.String
                ? SanitizeAppleNamePart(firstNameElement.GetString())
                : null;
            var lastName = nameElement.TryGetProperty("lastName", out var lastNameElement) && lastNameElement.ValueKind == JsonValueKind.String
                ? SanitizeAppleNamePart(lastNameElement.GetString())
                : null;
            return new AppleCallbackDisplayName(firstName, lastName);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return new AppleCallbackDisplayName(null, null);
        }
    }

    private static string? SanitizeAppleNamePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder();
        var pendingWhitespace = false;
        foreach (var rune in value.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or
                UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or
                UnicodeCategory.ParagraphSeparator or
                UnicodeCategory.PrivateUse or
                UnicodeCategory.OtherNotAssigned ||
                rune.Value is '<' or '>')
            {
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                pendingWhitespace = builder.Length > 0;
                continue;
            }

            if (pendingWhitespace)
            {
                if (builder.Length + 1 + rune.Utf16SequenceLength > MaxUserDisplayNameChars)
                {
                    break;
                }

                builder.Append(' ');
                pendingWhitespace = false;
            }
            else if (builder.Length + rune.Utf16SequenceLength > MaxUserDisplayNameChars)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        var sanitized = builder.ToString().Trim();
        return sanitized.Length == 0 ? null : sanitized;
    }

    private static string TruncateUtf16(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var length = maxLength;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length].TrimEnd();
    }

    private static Dictionary<string, string> FlattenJson(JsonElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        FlattenJsonInto(element, result, prefix: null);
        return result;
    }

    private static void FlattenJsonInto(JsonElement element, Dictionary<string, string> result, string? prefix)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                FlattenJsonInto(property.Value, result, string.IsNullOrWhiteSpace(prefix) ? property.Name : $"{prefix}.{property.Name}");
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(prefix))
        {
            result[prefix] = element.ValueKind == JsonValueKind.String ? element.GetString()! : element.ToString();
        }
    }

    private static bool ParseBooleanClaim(string? value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);

    private static List<string> ParseJsonArray(string json)
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

    private sealed record ProviderTokenPayload(string AccessToken, string? IdToken);

    private sealed record ProviderUser(
        string Subject,
        string Email,
        string DisplayName,
        bool EmailVerified,
        bool CanAutoLinkByEmail,
        string? EmailClaimSource = null);

    private sealed record ResolvedEmailClaims(string Email, bool EmailVerified, string Source);

    private sealed record AppleCallbackDisplayName(string? FirstName, string? LastName);

    private sealed record ProvisionedProviderUser(SqlOSUser User, bool Created);

    private sealed record ResolvedOidcConfiguration(
        SqlOSSocialProviderProtocol Protocol,
        SqlOSOidcProviderType ProviderType,
        string Issuer,
        string AuthorizationEndpoint,
        string TokenEndpoint,
        string? UserInfoEndpoint,
        string? JwksUri,
        IReadOnlyList<string> Scopes,
        SqlOSOidcClaimMapping ClaimMapping,
        bool RequireVerifiedEmail,
        bool UseUserInfo);
}

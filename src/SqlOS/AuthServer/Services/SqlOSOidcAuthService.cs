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
    private static readonly IReadOnlyList<string> DefaultOidcScopes = ["openid", "email", "profile"];
    private static readonly IReadOnlyList<string> DefaultAppleScopes = ["name", "email"];

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
                x.IsEnabled))
            .ToList();
    }

    public async Task<SqlOSStartOidcAuthorizationResult> StartAuthorizationAsync(
        SqlOSStartOidcAuthorizationRequest request,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await RequireEnabledConnectionAsync(request.ConnectionId, cancellationToken);

        var resolved = await ResolveConfigurationAsync(connection, cancellationToken);
        var authorizationParameters = new Dictionary<string, string?>
        {
            ["client_id"] = connection.ClientId,
            ["redirect_uri"] = request.CallbackUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', resolved.Scopes),
            ["state"] = request.State,
            ["nonce"] = request.Nonce,
            ["code_challenge"] = request.CodeChallenge,
            ["code_challenge_method"] = request.CodeChallengeMethod,
            ["login_hint"] = request.Email
        };

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
            var tokenPayload = await ExchangeCodeAsync(connection, resolved, request, cancellationToken);
            var idTokenPrincipal = await ValidateIdTokenAsync(connection, resolved, tokenPayload.IdToken, request.Nonce, cancellationToken);
            var userInfoClaims = resolved.UseUserInfo && !string.IsNullOrWhiteSpace(resolved.UserInfoEndpoint)
                ? await LoadUserInfoClaimsAsync(resolved.UserInfoEndpoint!, tokenPayload.AccessToken, cancellationToken)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            var callbackClaims = ParseCallbackPayloadClaims(request.UserPayloadJson);
            var providerUser = MapProviderUser(connection, resolved, idTokenPrincipal, userInfoClaims, callbackClaims);
            var user = await ResolveOrProvisionUserAsync(connection, resolved, providerUser, cancellationToken);
            var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);
            var organizationId = organizations.Count == 1 ? organizations[0].Id : null;
            var authMethod = connection.ProviderType switch
            {
                SqlOSOidcProviderType.Google => "google",
                SqlOSOidcProviderType.Microsoft => "microsoft",
                SqlOSOidcProviderType.Apple => "apple",
                SqlOSOidcProviderType.Custom => "oidc",
                _ => "oidc"
            };

            await _adminService.RecordAuditAsync(
                "user.login.oidc.success",
                "user",
                user.Id,
                userId: user.Id,
                organizationId: organizationId,
                ipAddress: ipAddress,
                data: new
                {
                    provider = connection.ProviderType.ToString(),
                    oidcConnectionId = connection.Id
                },
                cancellationToken: cancellationToken);

            return new SqlOSCompleteOidcAuthorizationResult(
                connection.Id,
                connection.ProviderType,
                user.Id,
                user.DefaultEmail ?? providerUser.Email,
                user.DisplayName,
                organizationId,
                authMethod,
                organizations.Count);
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
        using var jwksResponse = await httpClient.GetAsync(resolved.JwksUri, cancellationToken);
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

        return FlattenJson(payload.RootElement);
    }

    private ProviderUser MapProviderUser(
        SqlOSOidcConnection connection,
        ResolvedOidcConfiguration resolved,
        ClaimsPrincipal idTokenPrincipal,
        IReadOnlyDictionary<string, string> userInfoClaims,
        IReadOnlyDictionary<string, string> callbackClaims)
    {
        var idTokenClaims = idTokenPrincipal.Claims
            .GroupBy(x => x.Type, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First().Value, StringComparer.Ordinal);

        string? ResolveClaim(string? claimType)
        {
            if (string.IsNullOrWhiteSpace(claimType))
            {
                return null;
            }

            return callbackClaims.GetValueOrDefault(claimType)
                ?? userInfoClaims.GetValueOrDefault(claimType)
                ?? idTokenClaims.GetValueOrDefault(claimType);
        }

        var subject = ResolveClaim(resolved.ClaimMapping.SubjectClaim)
            ?? throw new InvalidOperationException("The OIDC provider did not return a subject claim.");
        var email = ResolveClaim(resolved.ClaimMapping.EmailClaim)
            ?? ResolveClaim(resolved.ClaimMapping.PreferredUsernameClaim);
        var emailVerified = ParseBooleanClaim(ResolveClaim(resolved.ClaimMapping.EmailVerifiedClaim));
        var firstName = ResolveClaim(resolved.ClaimMapping.FirstNameClaim)
            ?? callbackClaims.GetValueOrDefault("given_name")
            ?? callbackClaims.GetValueOrDefault("name.firstName");
        var lastName = ResolveClaim(resolved.ClaimMapping.LastNameClaim)
            ?? callbackClaims.GetValueOrDefault("family_name")
            ?? callbackClaims.GetValueOrDefault("name.lastName");
        var displayName = ResolveClaim(resolved.ClaimMapping.DisplayNameClaim)
            ?? callbackClaims.GetValueOrDefault("name");
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = string.Join(' ', new[] { firstName, lastName }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = email ?? subject;
        }

        var canAutoLinkByEmail = !string.IsNullOrWhiteSpace(email) &&
            (resolved.AllowEmailLinkWithoutVerifiedClaim || emailVerified);

        if (connection.ProviderType == SqlOSOidcProviderType.Google && !emailVerified)
        {
            canAutoLinkByEmail = false;
        }

        return new ProviderUser(
            subject,
            email ?? string.Empty,
            displayName ?? string.Empty,
            emailVerified,
            canAutoLinkByEmail);
    }

    private async Task<SqlOSUser> ResolveOrProvisionUserAsync(
        SqlOSOidcConnection connection,
        ResolvedOidcConfiguration resolved,
        ProviderUser providerUser,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerUser.Email))
        {
            throw new InvalidOperationException("The OIDC provider did not return a usable email address.");
        }

        if (resolved.RequireVerifiedEmail && !providerUser.EmailVerified)
        {
            throw new InvalidOperationException("The provider email must be verified before it can be linked.");
        }

        var externalIdentity = await _context.Set<SqlOSExternalIdentity>()
            .FirstOrDefaultAsync(
                x => x.OidcConnectionId == connection.Id && x.Subject == providerUser.Subject,
                cancellationToken);

        if (externalIdentity != null)
        {
            return await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == externalIdentity.UserId, cancellationToken);
        }

        SqlOSUser? user = null;
        if (providerUser.CanAutoLinkByEmail)
        {
            var normalizedEmail = SqlOSAdminService.NormalizeEmail(providerUser.Email);
            var existingEmail = await _context.Set<SqlOSUserEmail>()
                .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);

            if (existingEmail != null)
            {
                user = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == existingEmail.UserId, cancellationToken);
            }
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
                NormalizedEmail = SqlOSAdminService.NormalizeEmail(providerUser.Email),
                IsPrimary = true,
                IsVerified = true,
                VerifiedAt = DateTime.UtcNow,
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
        return user;
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
                connection.ProviderType,
                connection.Issuer ?? throw new InvalidOperationException("The OIDC connection is missing an issuer."),
                connection.AuthorizationEndpoint ?? throw new InvalidOperationException("The OIDC connection is missing an authorization endpoint."),
                connection.TokenEndpoint ?? throw new InvalidOperationException("The OIDC connection is missing a token endpoint."),
                connection.UserInfoEndpoint,
                connection.JwksUri ?? throw new InvalidOperationException("The OIDC connection is missing a JWKS URI."),
                scopes,
                claimMapping,
                RequireVerifiedEmail: connection.ProviderType == SqlOSOidcProviderType.Google,
                AllowEmailLinkWithoutVerifiedClaim: connection.ProviderType is SqlOSOidcProviderType.Microsoft or SqlOSOidcProviderType.Apple,
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
            connection.ProviderType,
            issuer,
            authorizationEndpoint,
            tokenEndpoint,
            userInfoEndpoint,
            jwksUri,
            scopes,
            claimMapping,
            RequireVerifiedEmail: connection.ProviderType == SqlOSOidcProviderType.Google,
            AllowEmailLinkWithoutVerifiedClaim: connection.ProviderType is SqlOSOidcProviderType.Microsoft or SqlOSOidcProviderType.Apple,
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
            KeyId = connection.AppleKeyId
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

        if (!allowed.Contains(callbackUri, StringComparer.OrdinalIgnoreCase))
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

        return connection.ProviderType == SqlOSOidcProviderType.Apple ? DefaultAppleScopes : DefaultOidcScopes;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static IReadOnlyDictionary<string, string> ParseCallbackPayloadClaims(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (document.RootElement.TryGetProperty("email", out var emailElement) && emailElement.ValueKind == JsonValueKind.String)
            {
                result["email"] = emailElement.GetString()!;
            }

            if (document.RootElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.Object)
            {
                if (nameElement.TryGetProperty("firstName", out var firstName) && firstName.ValueKind == JsonValueKind.String)
                {
                    result["given_name"] = firstName.GetString()!;
                }

                if (nameElement.TryGetProperty("lastName", out var lastName) && lastName.ValueKind == JsonValueKind.String)
                {
                    result["family_name"] = lastName.GetString()!;
                }
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static IReadOnlyDictionary<string, string> FlattenJson(JsonElement element)
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

    private sealed record ProviderTokenPayload(string AccessToken, string IdToken);

    private sealed record ProviderUser(
        string Subject,
        string Email,
        string DisplayName,
        bool EmailVerified,
        bool CanAutoLinkByEmail);

    private sealed record ResolvedOidcConfiguration(
        SqlOSOidcProviderType ProviderType,
        string Issuer,
        string AuthorizationEndpoint,
        string TokenEndpoint,
        string? UserInfoEndpoint,
        string JwksUri,
        IReadOnlyList<string> Scopes,
        SqlOSOidcClaimMapping ClaimMapping,
        bool RequireVerifiedEmail,
        bool AllowEmailLinkWithoutVerifiedClaim,
        bool UseUserInfo);
}

using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSAuthService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAuthServerOptions _options;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSSettingsService _settingsService;

    public SqlOSAuthService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSAdminService adminService,
        SqlOSCryptoService cryptoService,
        SqlOSSettingsService settingsService)
    {
        _context = context;
        _options = options.Value;
        _adminService = adminService;
        _cryptoService = cryptoService;
        _settingsService = settingsService;
    }

    public async Task<SqlOSLoginResult> SignUpAsync(SqlOSSignupRequest request, HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var user = await _adminService.CreateUserAsync(new SqlOSCreateUserRequest(request.DisplayName, request.Email, request.Password), cancellationToken);

        string? organizationId = request.OrganizationId;
        if (!string.IsNullOrWhiteSpace(request.OrganizationName))
        {
            var organization = await _adminService.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest(request.OrganizationName, null), cancellationToken);
            organizationId = organization.Id;
            await _adminService.CreateMembershipAsync(organization.Id, new SqlOSCreateMembershipRequest(user.Id, "owner"), cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(request.OrganizationId))
        {
            await _adminService.CreateMembershipAsync(request.OrganizationId, new SqlOSCreateMembershipRequest(user.Id, "member"), cancellationToken);
        }

        await _adminService.RecordAuditAsync("user.signup", "user", user.Id, userId: user.Id, organizationId: organizationId, ipAddress: GetIp(httpContext), cancellationToken: cancellationToken);

        var client = await _adminService.RequireClientAsync(request.ClientId, null, cancellationToken);
        var tokens = await CreateSessionAndTokensAsync(user, client, organizationId, "password", httpContext, cancellationToken);
        return new SqlOSLoginResult(false, null, Array.Empty<SqlOSOrganizationOption>(), tokens);
    }

    public async Task<SqlOSLoginResult> LoginWithPasswordAsync(SqlOSPasswordLoginRequest request, HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        if (!_options.EnableLocalPasswordAuth)
        {
            throw new InvalidOperationException("Local password authentication is disabled.");
        }

        var normalizedEmail = SqlOSAdminService.NormalizeEmail(request.Email);
        var email = await _context.Set<SqlOSUserEmail>()
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken)
            ?? throw new InvalidOperationException("Invalid email or password.");

        if (_options.RequireVerifiedEmailForPasswordLogin && !email.IsVerified)
        {
            throw new InvalidOperationException("Email must be verified before password login.");
        }

        var credential = await _context.Set<SqlOSCredential>()
            .FirstOrDefaultAsync(x => x.UserId == email.UserId && x.Type == "password" && x.RevokedAt == null, cancellationToken)
            ?? throw new InvalidOperationException("Invalid email or password.");

        if (!_cryptoService.VerifyPassword(credential.SecretHash, request.Password))
        {
            throw new InvalidOperationException("Invalid email or password.");
        }

        credential.LastUsedAt = DateTime.UtcNow;

        var user = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == email.UserId, cancellationToken);
        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);
        var client = await _adminService.RequireClientAsync(request.ClientId, null, cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.OrganizationId))
        {
            if (!await _adminService.UserHasMembershipAsync(user.Id, request.OrganizationId, cancellationToken))
            {
                throw new InvalidOperationException("User is not a member of the selected organization.");
            }

            await _context.SaveChangesAsync(cancellationToken);
            var tokens = await CreateSessionAndTokensAsync(user, client, request.OrganizationId, "password", httpContext, cancellationToken);
            await _adminService.RecordAuditAsync("user.login.password", "user", user.Id, userId: user.Id, organizationId: request.OrganizationId, ipAddress: GetIp(httpContext), cancellationToken: cancellationToken);
            return new SqlOSLoginResult(false, null, organizations, tokens);
        }

        if (organizations.Count > 1)
        {
            var pendingAuthToken = await _cryptoService.CreateTemporaryTokenAsync(
                "pending_auth",
                user.Id,
                client.Id,
                null,
                new PendingAuthPayload(client.ClientId, "password"),
                cancellationToken: cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            return new SqlOSLoginResult(true, pendingAuthToken, organizations, null);
        }

        var organizationId = organizations.Count == 1 ? organizations[0].Id : null;
        await _context.SaveChangesAsync(cancellationToken);
        var directTokens = await CreateSessionAndTokensAsync(user, client, organizationId, "password", httpContext, cancellationToken);
        await _adminService.RecordAuditAsync("user.login.password", "user", user.Id, userId: user.Id, organizationId: organizationId, ipAddress: GetIp(httpContext), cancellationToken: cancellationToken);
        return new SqlOSLoginResult(false, null, organizations, directTokens);
    }

    public async Task<SqlOSLoginResult> CompleteExternalLoginAsync(
        SqlOSUser user,
        SqlOSClientApplication client,
        string authenticationMethod,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);

        if (organizations.Count > 1)
        {
            var pendingAuthToken = await _cryptoService.CreateTemporaryTokenAsync(
                "pending_auth",
                user.Id,
                client.Id,
                null,
                new PendingAuthPayload(client.ClientId, authenticationMethod),
                cancellationToken: cancellationToken);

            return new SqlOSLoginResult(true, pendingAuthToken, organizations, null);
        }

        var organizationId = organizations.Count == 1 ? organizations[0].Id : null;
        var tokens = await CreateSessionAndTokensAsync(user, client, organizationId, authenticationMethod, httpContext, cancellationToken);
        return new SqlOSLoginResult(false, null, organizations, tokens);
    }

    public async Task<SqlOSTokenResponse> SelectOrganizationAsync(SqlOSSelectOrganizationRequest request, HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var token = await _cryptoService.ConsumeTemporaryTokenAsync("pending_auth", request.PendingAuthToken, cancellationToken)
            ?? throw new InvalidOperationException("Pending auth token is invalid or expired.");
        if (token.UserId == null || token.ClientApplicationId == null)
        {
            throw new InvalidOperationException("Pending auth token payload is invalid.");
        }

        if (!await _adminService.UserHasMembershipAsync(token.UserId, request.OrganizationId, cancellationToken))
        {
            throw new InvalidOperationException("User is not a member of the selected organization.");
        }

        var user = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == token.UserId, cancellationToken);
        var client = await _context.Set<SqlOSClientApplication>().FirstAsync(x => x.Id == token.ClientApplicationId, cancellationToken);
        var payload = _cryptoService.DeserializePayload<PendingAuthPayload>(token);
        var authMethod = payload?.AuthenticationMethod ?? "password";
        var tokens = await CreateSessionAndTokensAsync(user, client, request.OrganizationId, authMethod, httpContext, cancellationToken);
        await _adminService.RecordAuditAsync("user.login.organization-selected", "user", user.Id, userId: user.Id, organizationId: request.OrganizationId, ipAddress: GetIp(httpContext), cancellationToken: cancellationToken);
        return tokens;
    }

    public async Task<SqlOSTokenResponse> ExchangeCodeAsync(SqlOSExchangeCodeRequest request, HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var token = await _cryptoService.ConsumeTemporaryTokenAsync("auth_code", request.Code, cancellationToken)
            ?? throw new InvalidOperationException("Authorization code is invalid or expired.");
        var payload = _cryptoService.DeserializePayload<AuthCodePayload>(token)
            ?? throw new InvalidOperationException("Authorization code payload is invalid.");

        if (!string.Equals(payload.ClientId, request.ClientId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Authorization code was not issued for this client.");
        }

        var user = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == token.UserId!, cancellationToken);
        var client = await _adminService.RequireClientAsync(request.ClientId, payload.RedirectUri, cancellationToken);
        var tokens = await CreateSessionAndTokensAsync(user, client, token.OrganizationId, payload.AuthenticationMethod, httpContext, cancellationToken);
        await _adminService.RecordAuditAsync("user.login.code-exchanged", "user", user.Id, userId: user.Id, organizationId: token.OrganizationId, ipAddress: GetIp(httpContext), cancellationToken: cancellationToken);
        return tokens;
    }

    public async Task<SqlOSTokenResponse> RefreshAsync(SqlOSRefreshRequest request, CancellationToken cancellationToken = default)
    {
        var securitySettings = await _settingsService.GetResolvedSecuritySettingsAsync(cancellationToken);
        var hashedToken = _cryptoService.HashToken(request.RefreshToken);
        var refreshToken = await _context.Set<SqlOSRefreshToken>()
            .Include(x => x.Session)
            .ThenInclude(x => x!.User)
            .Include(x => x.Session)
            .ThenInclude(x => x!.ClientApplication)
            .FirstOrDefaultAsync(x => x.TokenHash == hashedToken, cancellationToken)
            ?? throw new InvalidOperationException("Refresh token is invalid.");

        var session = refreshToken.Session ?? throw new InvalidOperationException("Refresh token session missing.");
        if (refreshToken.RevokedAt != null || refreshToken.ExpiresAt <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("Refresh token is no longer valid.");
        }

        if (refreshToken.ConsumedAt != null)
        {
            // Grace window check: if the token was consumed recently AND a
            // replacement was issued, return the same cached token pair
            // instead of triggering replay detection. This prevents
            // legitimate concurrent refresh requests (multiple tabs,
            // parallel SSR calls, mobile retries, multi-instance load-
            // balanced deployments) from being false-flagged as token theft.
            var graceWindow = securitySettings.RefreshTokenGraceWindow;
            var withinGraceWindow = graceWindow > TimeSpan.Zero
                && refreshToken.ConsumedAt.Value.Add(graceWindow) > DateTime.UtcNow
                && !string.IsNullOrEmpty(refreshToken.ReplacedByTokenId)
                && !string.IsNullOrEmpty(refreshToken.ReplacementAccessToken);

            if (withinGraceWindow)
            {
                var replacement = await _context.Set<SqlOSRefreshToken>()
                    .FirstOrDefaultAsync(x => x.Id == refreshToken.ReplacedByTokenId, cancellationToken);

                if (replacement != null && replacement.RevokedAt == null)
                {
                    // Reissue the previously stored access token + a fresh
                    // copy of the new opaque refresh token. We can't recover
                    // the raw replacement refresh token from its hash, so we
                    // mint a new sibling that points at the same replacement
                    // row, marking the original consumed token's lineage.
                    //
                    // Practically: callers within the grace window get the
                    // SAME access token (same JWT, same expiry) as the
                    // first call, which is the important property for
                    // concurrent SSR / multi-tab scenarios. They also get a
                    // valid refresh token they can continue to use.
                    return new SqlOSTokenResponse(
                        refreshToken.ReplacementAccessToken!,
                        await ReissueGraceWindowRefreshTokenAsync(replacement, cancellationToken),
                        session.Id,
                        session.ClientApplication!.ClientId,
                        request.OrganizationId,
                        DateTime.UtcNow.Add(_options.AccessTokenLifetime),
                        replacement.ExpiresAt);
                }
            }

            await RevokeRefreshTokenFamilyAsync(session.Id, refreshToken.FamilyId, "refresh_token_reuse", cancellationToken);
            throw new InvalidOperationException("Refresh token has already been used.");
        }

        if (session.RevokedAt != null || session.AbsoluteExpiresAt <= DateTime.UtcNow || session.IdleExpiresAt <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("Session is no longer active.");
        }

        if (_options.ResourceIndicators.Enabled && !string.IsNullOrWhiteSpace(request.Resource))
        {
            var requestedResource = request.Resource.Trim();
            if (string.IsNullOrWhiteSpace(session.Resource)
                || !string.Equals(session.Resource, requestedResource, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Resource does not match the original authorization.");
            }
        }

        string? organizationId = request.OrganizationId;
        if (!string.IsNullOrWhiteSpace(organizationId) && !await _adminService.UserHasMembershipAsync(session.UserId, organizationId, cancellationToken))
        {
            throw new InvalidOperationException("User is not a member of the selected organization.");
        }

        refreshToken.ConsumedAt = DateTime.UtcNow;
        var newRawRefreshToken = _cryptoService.GenerateOpaqueToken();
        var nextRefreshToken = new SqlOSRefreshToken
        {
            Id = _cryptoService.GenerateId("rfr"),
            SessionId = session.Id,
            FamilyId = refreshToken.FamilyId,
            TokenHash = _cryptoService.HashToken(newRawRefreshToken),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(securitySettings.RefreshTokenLifetime)
        };
        refreshToken.ReplacedByTokenId = nextRefreshToken.Id;
        session.LastSeenAt = DateTime.UtcNow;
        session.IdleExpiresAt = DateTime.UtcNow.Add(securitySettings.SessionIdleTimeout);
        _context.Set<SqlOSRefreshToken>().Add(nextRefreshToken);

        var accessToken = await _cryptoService.CreateAccessTokenAsync(session.User!, session, session.ClientApplication!, organizationId, cancellationToken);

        // Cache the issued access token on the consumed row so concurrent
        // refresh attempts within the grace window can return the SAME
        // access token instead of getting a divergent fresh one.
        refreshToken.ReplacementAccessToken = accessToken;

        await _context.SaveChangesAsync(cancellationToken);

        return new SqlOSTokenResponse(
            accessToken,
            newRawRefreshToken,
            session.Id,
            session.ClientApplication!.ClientId,
            organizationId,
            DateTime.UtcNow.Add(_options.AccessTokenLifetime),
            nextRefreshToken.ExpiresAt);
    }

    /// <summary>
    /// Issues a sibling refresh token within the same family as the given
    /// replacement, used to satisfy a refresh request that hit the grace
    /// window. The sibling shares lifetime, family, and session with the
    /// replacement. We mint a fresh raw token because we can only store
    /// the hash of the original replacement.
    /// </summary>
    private async Task<string> ReissueGraceWindowRefreshTokenAsync(SqlOSRefreshToken replacement, CancellationToken cancellationToken)
    {
        var newRawRefreshToken = _cryptoService.GenerateOpaqueToken();
        var sibling = new SqlOSRefreshToken
        {
            Id = _cryptoService.GenerateId("rfr"),
            SessionId = replacement.SessionId,
            FamilyId = replacement.FamilyId,
            TokenHash = _cryptoService.HashToken(newRawRefreshToken),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = replacement.ExpiresAt
        };
        _context.Set<SqlOSRefreshToken>().Add(sibling);
        await _context.SaveChangesAsync(cancellationToken);
        return newRawRefreshToken;
    }

    public async Task LogoutAsync(string? refreshToken, string? sessionId, CancellationToken cancellationToken = default)
    {
        SqlOSSession? session = null;
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            var hashed = _cryptoService.HashToken(refreshToken);
            var token = await _context.Set<SqlOSRefreshToken>()
                .Include(x => x.Session)
                .FirstOrDefaultAsync(x => x.TokenHash == hashed, cancellationToken);
            session = token?.Session;
        }
        else if (!string.IsNullOrWhiteSpace(sessionId))
        {
            session = await _context.Set<SqlOSSession>().FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
        }

        if (session == null)
        {
            return;
        }

        session.RevokedAt = DateTime.UtcNow;
        session.RevocationReason = "logout";
        var refreshTokens = await _context.Set<SqlOSRefreshToken>().Where(x => x.SessionId == session.Id && x.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var token in refreshTokens)
        {
            token.RevokedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
        await _adminService.RecordAuditAsync("user.logout", "session", session.Id, userId: session.UserId, sessionId: session.Id, cancellationToken: cancellationToken);
    }

    public async Task LogoutAllAsync(string userId, CancellationToken cancellationToken = default)
    {
        var sessions = await _context.Set<SqlOSSession>()
            .Where(x => x.UserId == userId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        if (sessions.Count == 0)
        {
            return;
        }

        var sessionIds = sessions.Select(x => x.Id).ToList();
        foreach (var session in sessions)
        {
            session.RevokedAt = DateTime.UtcNow;
            session.RevocationReason = "logout_all";
        }

        var refreshTokens = await _context.Set<SqlOSRefreshToken>()
            .Where(x => sessionIds.Contains(x.SessionId) && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var token in refreshTokens)
        {
            token.RevokedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
        await _adminService.RecordAuditAsync("user.logout-all", "user", userId, userId: userId, cancellationToken: cancellationToken);
    }

    public async Task<string> CreatePasswordResetTokenAsync(SqlOSForgotPasswordRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = SqlOSAdminService.NormalizeEmail(request.Email);
        var email = await _context.Set<SqlOSUserEmail>().FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken)
            ?? throw new InvalidOperationException("Unknown email address.");

        var token = await _cryptoService.CreateTemporaryTokenAsync(
            "password_reset",
            email.UserId,
            null,
            null,
            new PasswordResetPayload(email.Id),
            TimeSpan.FromHours(1),
            cancellationToken);

        await _adminService.RecordAuditAsync("user.password-reset-token-created", "user", email.UserId, userId: email.UserId, cancellationToken: cancellationToken);
        return token;
    }

    public async Task ResetPasswordAsync(SqlOSResetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        var token = await _cryptoService.ConsumeTemporaryTokenAsync("password_reset", request.Token, cancellationToken)
            ?? throw new InvalidOperationException("Password reset token is invalid or expired.");

        var credential = await _context.Set<SqlOSCredential>()
            .FirstOrDefaultAsync(x => x.UserId == token.UserId && x.Type == "password" && x.RevokedAt == null, cancellationToken);

        if (credential == null)
        {
            credential = new SqlOSCredential
            {
                Id = _cryptoService.GenerateId("cred"),
                UserId = token.UserId!,
                Type = "password",
                CreatedAt = DateTime.UtcNow
            };
            _context.Set<SqlOSCredential>().Add(credential);
        }

        credential.SecretHash = _cryptoService.HashPassword(request.NewPassword);
        credential.LastUsedAt = null;
        await _context.SaveChangesAsync(cancellationToken);
        await _adminService.RecordAuditAsync("user.password-reset", "user", token.UserId, userId: token.UserId, cancellationToken: cancellationToken);
    }

    public async Task<string> CreateEmailVerificationTokenAsync(SqlOSCreateVerificationTokenRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = SqlOSAdminService.NormalizeEmail(request.Email);
        var email = await _context.Set<SqlOSUserEmail>().FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken)
            ?? throw new InvalidOperationException("Unknown email address.");

        var token = await _cryptoService.CreateTemporaryTokenAsync(
            "email_verification",
            email.UserId,
            null,
            null,
            new EmailVerificationPayload(email.Id),
            TimeSpan.FromDays(1),
            cancellationToken);

        await _adminService.RecordAuditAsync("user.email-verification-token-created", "user", email.UserId, userId: email.UserId, cancellationToken: cancellationToken);
        return token;
    }

    public async Task VerifyEmailAsync(SqlOSVerifyEmailRequest request, CancellationToken cancellationToken = default)
    {
        var token = await _cryptoService.ConsumeTemporaryTokenAsync("email_verification", request.Token, cancellationToken)
            ?? throw new InvalidOperationException("Email verification token is invalid or expired.");
        var payload = _cryptoService.DeserializePayload<EmailVerificationPayload>(token)
            ?? throw new InvalidOperationException("Email verification token payload is invalid.");

        var email = await _context.Set<SqlOSUserEmail>().FirstAsync(x => x.Id == payload.EmailId, cancellationToken);
        email.IsVerified = true;
        email.VerifiedAt = DateTime.UtcNow;
        var user = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == email.UserId, cancellationToken);
        user.DefaultEmail = email.Email;
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        await _adminService.RecordAuditAsync("user.email-verified", "user", email.UserId, userId: email.UserId, cancellationToken: cancellationToken);
    }

    public Task<SqlOSValidatedToken?> ValidateAccessTokenAsync(string rawToken, CancellationToken cancellationToken = default)
        => _cryptoService.ValidateAccessTokenAsync(rawToken, cancellationToken);

    public Task<SqlOSValidatedToken?> ValidateAccessTokenAsync(
        string rawToken,
        string? expectedAudience,
        CancellationToken cancellationToken = default)
        => _cryptoService.ValidateAccessTokenAsync(rawToken, expectedAudience, cancellationToken);

    public async Task<SqlOSTokenResponse> CreateSessionTokensForUserAsync(
        SqlOSUser user,
        SqlOSClientApplication client,
        string? organizationId,
        string authenticationMethod,
        string? userAgent,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var securitySettings = await _settingsService.GetResolvedSecuritySettingsAsync(cancellationToken);
        return await CreateSessionAndTokensAsync(
            user,
            client,
            organizationId,
            authenticationMethod,
            userAgent,
            ipAddress,
            null,
            securitySettings,
            cancellationToken);
    }

    public async Task<SqlOSTokenResponse> CreateSessionTokensForUserAsync(
        SqlOSUser user,
        SqlOSClientApplication client,
        string? organizationId,
        string authenticationMethod,
        string? userAgent,
        string? ipAddress,
        string? resource,
        CancellationToken cancellationToken = default)
    {
        var securitySettings = await _settingsService.GetResolvedSecuritySettingsAsync(cancellationToken);
        return await CreateSessionAndTokensAsync(
            user,
            client,
            organizationId,
            authenticationMethod,
            userAgent,
            ipAddress,
            resource,
            securitySettings,
            cancellationToken);
    }

    private async Task<SqlOSTokenResponse> CreateSessionAndTokensAsync(
        SqlOSUser user,
        SqlOSClientApplication client,
        string? organizationId,
        string authenticationMethod,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var securitySettings = await _settingsService.GetResolvedSecuritySettingsAsync(cancellationToken);
        return await CreateSessionAndTokensAsync(
            user,
            client,
            organizationId,
            authenticationMethod,
            httpContext.Request.Headers.UserAgent.ToString(),
            GetIp(httpContext),
            null,
            securitySettings,
            cancellationToken);
    }

    private async Task<SqlOSTokenResponse> CreateSessionAndTokensAsync(
        SqlOSUser user,
        SqlOSClientApplication client,
        string? organizationId,
        string authenticationMethod,
        string? userAgent,
        string? ipAddress,
        string? resource,
        SqlOSResolvedSecuritySettings securitySettings,
        CancellationToken cancellationToken)
    {
        var effectiveAudience = ResolveEffectiveAudience(client, resource);
        var session = new SqlOSSession
        {
            Id = _cryptoService.GenerateId("ses"),
            UserId = user.Id,
            ClientApplicationId = client.Id,
            AuthenticationMethod = authenticationMethod,
            Resource = resource,
            EffectiveAudience = effectiveAudience,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            IdleExpiresAt = DateTime.UtcNow.Add(securitySettings.SessionIdleTimeout),
            AbsoluteExpiresAt = DateTime.UtcNow.Add(securitySettings.SessionAbsoluteLifetime),
            UserAgent = userAgent,
            IpAddress = ipAddress
        };
        _context.Set<SqlOSSession>().Add(session);

        var rawRefreshToken = _cryptoService.GenerateOpaqueToken();
        var refreshToken = new SqlOSRefreshToken
        {
            Id = _cryptoService.GenerateId("rfr"),
            SessionId = session.Id,
            FamilyId = _cryptoService.GenerateId("fam"),
            TokenHash = _cryptoService.HashToken(rawRefreshToken),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(securitySettings.RefreshTokenLifetime)
        };
        _context.Set<SqlOSRefreshToken>().Add(refreshToken);
        await _context.SaveChangesAsync(cancellationToken);

        var accessToken = await _cryptoService.CreateAccessTokenAsync(user, session, client, organizationId, cancellationToken);
        return new SqlOSTokenResponse(
            accessToken,
            rawRefreshToken,
            session.Id,
            client.ClientId,
            organizationId,
            DateTime.UtcNow.Add(_options.AccessTokenLifetime),
            refreshToken.ExpiresAt);
    }

    private static string? GetIp(HttpContext httpContext) => httpContext.Connection.RemoteIpAddress?.ToString();

    private static string ResolveEffectiveAudience(SqlOSClientApplication client, string? resource)
        => string.IsNullOrWhiteSpace(resource)
            ? client.Audience
            : resource.Trim();

    private async Task RevokeRefreshTokenFamilyAsync(string sessionId, string familyId, string reason, CancellationToken cancellationToken)
    {
        var session = await _context.Set<SqlOSSession>().FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
        if (session != null && session.RevokedAt == null)
        {
            session.RevokedAt = DateTime.UtcNow;
            session.RevocationReason = reason;
        }

        var refreshTokens = await _context.Set<SqlOSRefreshToken>()
            .Where(x => x.SessionId == sessionId && x.FamilyId == familyId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in refreshTokens)
        {
            token.RevokedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private sealed record PendingAuthPayload(string ClientId, string AuthenticationMethod);
    private sealed record AuthCodePayload(string ClientId, string RedirectUri, string AuthenticationMethod);
    private sealed record PasswordResetPayload(string EmailId);
    private sealed record EmailVerificationPayload(string EmailId);
}

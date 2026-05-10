using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
    private readonly SqlOSEmailOtpService _emailOtpService;
    private readonly SqlOSInvitationService? _invitationService;

    public SqlOSAuthService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSAdminService adminService,
        SqlOSCryptoService cryptoService,
        SqlOSSettingsService settingsService,
        SqlOSEmailOtpService emailOtpService,
        SqlOSInvitationService? invitationService = null)
    {
        _context = context;
        _options = options.Value;
        _adminService = adminService;
        _cryptoService = cryptoService;
        _settingsService = settingsService;
        _emailOtpService = emailOtpService;
        _invitationService = invitationService;
    }

    public async Task<SqlOSLoginResult> SignUpAsync(SqlOSSignupRequest request, HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var credentialSettings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!credentialSettings.PasswordSignupEnabled)
        {
            throw new InvalidOperationException("Password signup is disabled.");
        }

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
        var credentialSettings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!credentialSettings.PasswordEnabled)
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
        var client = await _adminService.RequireClientAsync(request.ClientId, null, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return await FinalizeClientLoginAsync(user, client, request.OrganizationId, "password", httpContext, cancellationToken);
    }

    public async Task<SqlOSEmailOtpStartResult> RequestEmailOtpAsync(
        SqlOSEmailOtpStartRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => await _emailOtpService.StartForClientAsync(request, httpContext, cancellationToken);

    public async Task<SqlOSEmailOtpSignupStartResult> RequestEmailOtpSignupAsync(
        SqlOSEmailOtpSignupStartRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => await _emailOtpService.StartSignupForClientAsync(request, httpContext, cancellationToken);

    public async Task<SqlOSEmailInvitationResult> CreateEmailInvitationAsync(
        SqlOSCreateEmailInvitationRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => await RequireInvitationService().CreateEmailInvitationAsync(request, httpContext, cancellationToken);

    public async Task<SqlOSEmailInvitationResult> ResendEmailInvitationAsync(
        SqlOSResendEmailInvitationRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => await RequireInvitationService().ResendEmailInvitationAsync(request, httpContext, cancellationToken);

    public async Task<SqlOSEmailInvitationResult> RevokeEmailInvitationAsync(
        SqlOSRevokeEmailInvitationRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => await RequireInvitationService().RevokeEmailInvitationAsync(request, httpContext, cancellationToken);

    public async Task<SqlOSInvitationAcceptanceResult> AcceptEmailInvitationAsync(
        SqlOSAcceptEmailInvitationRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => await RequireInvitationService().AcceptEmailInvitationAsync(request, httpContext, cancellationToken);

    public async Task<SqlOSLoginResult> AcceptEmailInvitationSignupAsync(
        SqlOSAcceptEmailInvitationSignupRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        IDbContextTransaction? transaction = null;
        SqlOSPasswordAuthenticationResult? signup = null;

        try
        {
            if (SupportsDatabaseTransactions())
            {
                transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            }

            var invitation = await RequireInvitationService().ResolveEmailInvitationAsync(
                request.InvitationToken,
                httpContext,
                cancellationToken);
            var client = await _adminService.RequireClientAsync(request.ClientId, null, cancellationToken);

            signup = await CreateInvitationSignupUserAsync(
                request.DisplayName,
                invitation.Email,
                cancellationToken);

            if (_options.Headless.OnHeadlessSignupAsync != null)
            {
                var organization = await _context.Set<SqlOSOrganization>()
                    .FirstOrDefaultAsync(x => x.Id == invitation.OrganizationId, cancellationToken);
                await _options.Headless.OnHeadlessSignupAsync(
                    new SqlOSHeadlessSignupHookContext(
                        httpContext,
                        null,
                        signup.User,
                        organization,
                        request.CustomFields ?? invitation.CustomFields ?? new JsonObject()),
                    cancellationToken);
            }

            var acceptance = await RequireInvitationService().AcceptEmailInvitationInCurrentTransactionAsync(
                new SqlOSAcceptEmailInvitationRequest(request.InvitationToken, signup.User.Id),
                httpContext,
                cancellationToken);

            await _adminService.RecordAuditAsync(
                "user.signup.invitation",
                "user",
                signup.User.Id,
                userId: signup.User.Id,
                organizationId: acceptance.OrganizationId,
                ipAddress: GetIp(httpContext),
                cancellationToken: cancellationToken);

            var tokens = await CreateSessionAndTokensAsync(
                signup.User,
                client,
                acceptance.OrganizationId,
                signup.AuthenticationMethod,
                httpContext,
                cancellationToken);
            var organizations = await _adminService.GetUserOrganizationsAsync(signup.User.Id, cancellationToken);

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return new SqlOSLoginResult(false, null, organizations, tokens);
        }
        catch
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            else
            {
                await CleanupNonTransactionalSignupArtifactsAsync(
                    signup,
                    existingOrganizationId: null,
                    organizationName: null,
                    cancellationToken: cancellationToken);
            }

            throw;
        }
    }

    public async Task<SqlOSLoginResult> VerifyEmailOtpAsync(
        SqlOSEmailOtpVerifyRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var verification = await _emailOtpService.VerifyAsync(request, cancellationToken);
        if (verification.Challenge.ClientApplicationId == null)
        {
            throw new InvalidOperationException("The sign-in code is invalid or expired.");
        }

        var client = await _context.Set<SqlOSClientApplication>()
            .FirstAsync(x => x.Id == verification.Challenge.ClientApplicationId, cancellationToken);

        return await FinalizeClientLoginAsync(
            verification.User,
            client,
            verification.Challenge.RequestedOrganizationId,
            verification.AuthenticationMethod,
            httpContext,
            cancellationToken);
    }

    public async Task<SqlOSLoginResult> VerifyEmailOtpSignupAsync(
        SqlOSEmailOtpSignupVerifyRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        IDbContextTransaction? transaction = null;
        SqlOSPasswordAuthenticationResult? signup = null;
        SqlOSEmailOtpSignupVerificationResult? verification = null;

        try
        {
            if (SupportsDatabaseTransactions())
            {
                transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            }

            verification = await _emailOtpService.VerifySignupAsync(
                request,
                expectedAuthorizationRequestId: null,
                requireAuthorizationRequestMatch: false,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(verification.ClientApplicationId))
            {
                throw new InvalidOperationException("The sign-in code is invalid or expired.");
            }

            var client = await _context.Set<SqlOSClientApplication>()
                .FirstAsync(x => x.Id == verification.ClientApplicationId, cancellationToken);

            signup = await CreateEmailOtpSignupUserAsync(
                verification.DisplayName,
                verification.Email,
                verification.OrganizationName,
                verification.OrganizationId,
                cancellationToken);

            var selectedOrganizationId = verification.OrganizationId ?? signup.Organizations.FirstOrDefault()?.Id;
            SqlOSOrganization? organization = null;
            if (!string.IsNullOrWhiteSpace(selectedOrganizationId))
            {
                organization = await _context.Set<SqlOSOrganization>()
                    .FirstOrDefaultAsync(x => x.Id == selectedOrganizationId, cancellationToken);
            }

            if (_options.Headless.OnHeadlessSignupAsync != null)
            {
                await _options.Headless.OnHeadlessSignupAsync(
                    new SqlOSHeadlessSignupHookContext(
                        httpContext,
                        null,
                        signup.User,
                        organization,
                        verification.CustomFields ?? new JsonObject()),
                    cancellationToken);
            }

            await _emailOtpService.ConsumeSignupTokenAsync(verification.SignupToken, cancellationToken);
            await _adminService.RecordAuditAsync(
                "user.signup.email_otp",
                "user",
                signup.User.Id,
                userId: signup.User.Id,
                organizationId: selectedOrganizationId,
                ipAddress: GetIp(httpContext),
                cancellationToken: cancellationToken);

            var result = await FinalizeClientLoginAsync(
                signup.User,
                client,
                selectedOrganizationId,
                signup.AuthenticationMethod,
                httpContext,
                cancellationToken);

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        }
        catch
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            else
            {
                await CleanupNonTransactionalSignupArtifactsAsync(
                    signup,
                    verification?.OrganizationId,
                    verification?.OrganizationName,
                    cancellationToken);
            }

            throw;
        }
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
            return await HandleConsumedRefreshTokenAsync(refreshToken, session, request, securitySettings, cancellationToken);
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

        // Mint the access token, build the new refresh token row, and
        // populate the grace-window cache fields all BEFORE the single
        // SaveChangesAsync. This avoids a visibility window where
        // ConsumedAt is set but ReplacementAccessToken is still null
        // (which would cause concurrent callers to fail the grace
        // window check and trigger false-positive replay detection).
        //
        // Atomicity is enforced by EF Core optimistic concurrency on
        // ConsumedAt (configured via IsConcurrencyToken in the model
        // builder). Only one concurrent refresh wins the UPDATE; the
        // loser(s) get DbUpdateConcurrencyException and route to the
        // grace window path on retry. This makes rotation strictly
        // atomic across any number of app instances behind a load
        // balancer, with no in-process coordination required.
        var accessToken = await _cryptoService.CreateAccessTokenAsync(session.User!, session, session.ClientApplication!, organizationId, cancellationToken);
        var accessTokenExpiresAt = DateTime.UtcNow.Add(_options.AccessTokenLifetime);

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

        // Cache the issued access token on the consumed row, encrypted
        // at rest, alongside the org id and expiry, so concurrent
        // refresh attempts within the grace window can return the SAME
        // access token instead of getting a divergent fresh one.
        refreshToken.ReplacementAccessToken = _cryptoService.ProtectSecret(accessToken);
        refreshToken.ReplacementOrganizationId = organizationId;
        refreshToken.ReplacementAccessTokenExpiresAt = accessTokenExpiresAt;

        session.LastSeenAt = DateTime.UtcNow;
        session.IdleExpiresAt = DateTime.UtcNow.Add(securitySettings.SessionIdleTimeout);
        _context.Set<SqlOSRefreshToken>().Add(nextRefreshToken);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Lost the rotation race to a concurrent refresh on this or
            // another instance. The winner has already committed the
            // entire rotation (ConsumedAt + ReplacementAccessToken cache
            // + new refresh token row) atomically, so a fresh re-read
            // will see a fully populated grace-window cache. We need to:
            //   1) Discard our failed-rotation change tracker state (the
            //      stale ConsumedAt UPDATE and the orphan sibling INSERT)
            //      so it doesn't get re-flushed by the next SaveChanges.
            //   2) Re-fetch the row from the database showing the
            //      winner's state.
            //   3) Route to the grace window path so this caller gets the
            //      same cached access token the winner produced.
            //
            // The interface ISqlOSAuthServerDbContext doesn't expose the
            // change tracker, but every concrete implementation is a
            // DbContext subclass — cast and reset.
            if (_context is DbContext dbContext)
            {
                dbContext.ChangeTracker.Clear();
            }

            var fresh = await _context.Set<SqlOSRefreshToken>()
                .Include(x => x.Session)
                .ThenInclude(x => x!.User)
                .Include(x => x.Session)
                .ThenInclude(x => x!.ClientApplication)
                .FirstOrDefaultAsync(x => x.Id == refreshToken.Id, cancellationToken)
                ?? throw new InvalidOperationException("Refresh token vanished after concurrency conflict.");

            return await HandleConsumedRefreshTokenAsync(fresh, fresh.Session!, request, securitySettings, cancellationToken);
        }

        return new SqlOSTokenResponse(
            accessToken,
            newRawRefreshToken,
            session.Id,
            session.ClientApplication!.ClientId,
            organizationId,
            accessTokenExpiresAt,
            nextRefreshToken.ExpiresAt);
    }

    /// <summary>
    /// Handles a refresh request where the presented token has already been
    /// consumed. If the consumption happened recently AND a replacement
    /// access token was cached, return the same cached token pair (grace
    /// window). Otherwise, trigger replay detection and revoke the family.
    /// </summary>
    private async Task<SqlOSTokenResponse> HandleConsumedRefreshTokenAsync(
        SqlOSRefreshToken refreshToken,
        SqlOSSession session,
        SqlOSRefreshRequest request,
        SqlOSResolvedSecuritySettings securitySettings,
        CancellationToken cancellationToken)
    {
        var graceWindow = securitySettings.RefreshTokenGraceWindow;
        var withinGraceWindow = graceWindow > TimeSpan.Zero
            && refreshToken.ConsumedAt!.Value.Add(graceWindow) > DateTime.UtcNow
            && !string.IsNullOrEmpty(refreshToken.ReplacedByTokenId)
            && !string.IsNullOrEmpty(refreshToken.ReplacementAccessToken)
            && refreshToken.ReplacementAccessTokenExpiresAt is { } cachedExpiry
            && cachedExpiry > DateTime.UtcNow;

        if (withinGraceWindow)
        {
            var replacement = await _context.Set<SqlOSRefreshToken>()
                .FirstOrDefaultAsync(x => x.Id == refreshToken.ReplacedByTokenId, cancellationToken);

            if (replacement != null && replacement.RevokedAt == null)
            {
                // Resource indicator validation must still match the original
                // authorization, even on the grace window path.
                if (_options.ResourceIndicators.Enabled && !string.IsNullOrWhiteSpace(request.Resource))
                {
                    var requestedResource = request.Resource.Trim();
                    if (string.IsNullOrWhiteSpace(session.Resource)
                        || !string.Equals(session.Resource, requestedResource, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Resource does not match the original authorization.");
                    }
                }

                // Reject any attempt to switch organization on the grace
                // window path. The cached JWT was minted for a specific
                // organization and we must not return it to a caller asking
                // for a different one — that would let a caller skip the
                // membership check by replaying a sibling's refresh token.
                if (!string.IsNullOrWhiteSpace(request.OrganizationId)
                    && !string.Equals(request.OrganizationId, refreshToken.ReplacementOrganizationId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Organization does not match the original refresh.");
                }

                var cachedAccessToken = _cryptoService.UnprotectSecret(refreshToken.ReplacementAccessToken!);

                return new SqlOSTokenResponse(
                    cachedAccessToken,
                    await ReissueGraceWindowRefreshTokenAsync(replacement, cancellationToken),
                    session.Id,
                    session.ClientApplication!.ClientId,
                    refreshToken.ReplacementOrganizationId,
                    refreshToken.ReplacementAccessTokenExpiresAt!.Value,
                    replacement.ExpiresAt);
            }
        }

        await RevokeRefreshTokenFamilyAsync(session.Id, refreshToken.FamilyId, "refresh_token_reuse", cancellationToken);
        throw new InvalidOperationException("Refresh token has already been used.");
    }

    /// <summary>
    /// Mints a fresh opaque refresh token in the same family as the given
    /// replacement and persists it. Used by the grace window path: we
    /// can't return the original raw replacement refresh token because we
    /// only stored its hash, so callers in the grace window receive a new
    /// valid token in the same refresh-token family rather than the
    /// original replacement token value. The new token shares lifetime,
    /// family, and session with the replacement and rotates normally on
    /// next use.
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

    private bool SupportsDatabaseTransactions()
        => !string.Equals(_context.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal);

    private async Task<SqlOSPasswordAuthenticationResult> CreateEmailOtpSignupUserAsync(
        string displayName,
        string email,
        string? organizationName,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        var credentialSettings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!credentialSettings.EmailOtpEnabled)
        {
            throw new InvalidOperationException("Email sign-in is unavailable.");
        }

        var user = await _adminService.CreateUserAsync(
            new SqlOSCreateUserRequest(displayName, email, null),
            cancellationToken);

        var emailRecord = await _context.Set<SqlOSUserEmail>()
            .FirstAsync(x => x.UserId == user.Id && x.IsPrimary, cancellationToken);
        emailRecord.IsVerified = true;
        emailRecord.VerifiedAt = DateTime.UtcNow;
        user.DefaultEmail = emailRecord.Email;
        user.UpdatedAt = DateTime.UtcNow;

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

        await _context.SaveChangesAsync(cancellationToken);

        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);
        if (!string.IsNullOrWhiteSpace(selectedOrganizationId) && organizations.All(x => x.Id != selectedOrganizationId))
        {
            organizations = organizations
                .Concat([new SqlOSOrganizationOption(selectedOrganizationId, selectedOrganizationId, selectedOrganizationId, "member")])
                .ToList();
        }

        return new SqlOSPasswordAuthenticationResult(user, organizations, "email_otp");
    }

    private async Task<SqlOSPasswordAuthenticationResult> CreateInvitationSignupUserAsync(
        string displayName,
        string email,
        CancellationToken cancellationToken)
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

    private async Task CleanupNonTransactionalSignupArtifactsAsync(
        SqlOSPasswordAuthenticationResult? signup,
        string? existingOrganizationId,
        string? organizationName,
        CancellationToken cancellationToken)
    {
        if (signup == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(organizationName) && string.IsNullOrWhiteSpace(existingOrganizationId))
        {
            var organizationIds = signup.Organizations
                .Select(static x => x.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (organizationIds.Length > 0)
            {
                var organizations = await _context.Set<SqlOSOrganization>()
                    .Where(x => organizationIds.Contains(x.Id))
                    .ToListAsync(cancellationToken);
                if (organizations.Count > 0)
                {
                    _context.Set<SqlOSOrganization>().RemoveRange(organizations);
                }
            }
        }

        var user = await _context.Set<SqlOSUser>()
            .FirstOrDefaultAsync(x => x.Id == signup.User.Id, cancellationToken);
        if (user != null)
        {
            _context.Set<SqlOSUser>().Remove(user);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<SqlOSLoginResult> FinalizeClientLoginAsync(
        SqlOSUser user,
        SqlOSClientApplication client,
        string? requestedOrganizationId,
        string authenticationMethod,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);

        if (!string.IsNullOrWhiteSpace(requestedOrganizationId))
        {
            if (!await _adminService.UserHasMembershipAsync(user.Id, requestedOrganizationId, cancellationToken))
            {
                throw new InvalidOperationException("User is not a member of the selected organization.");
            }

            var tokens = await CreateSessionAndTokensAsync(user, client, requestedOrganizationId, authenticationMethod, httpContext, cancellationToken);
            await _adminService.RecordAuditAsync(
                $"user.login.{authenticationMethod}",
                "user",
                user.Id,
                userId: user.Id,
                organizationId: requestedOrganizationId,
                ipAddress: GetIp(httpContext),
                cancellationToken: cancellationToken);
            return new SqlOSLoginResult(false, null, organizations, tokens);
        }

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
        var directTokens = await CreateSessionAndTokensAsync(user, client, organizationId, authenticationMethod, httpContext, cancellationToken);
        await _adminService.RecordAuditAsync(
            $"user.login.{authenticationMethod}",
            "user",
            user.Id,
            userId: user.Id,
            organizationId: organizationId,
            ipAddress: GetIp(httpContext),
            cancellationToken: cancellationToken);
        return new SqlOSLoginResult(false, null, organizations, directTokens);
    }

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

    private SqlOSInvitationService RequireInvitationService()
        => _invitationService ?? throw new InvalidOperationException("SqlOS invitations are not configured.");
}

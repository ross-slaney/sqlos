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
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;
using SqlOS.Email.Models;
using SqlOS.Email.Services;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSAuthService
{
    public const string MfaChallengePurpose = "mfa_challenge";
    internal const string MfaChallengeFailureMessage = "MFA code is invalid.";
    private const string MfaChallengeFailedAuditEvent = "user.mfa.challenge_failed";
    private const string PasswordResetPurpose = "password_reset";
    private const string PasswordResetRequestPurpose = "password_reset_request";
    private const string PasswordResetGenericMessage = "If an account can be reset, you'll receive a password reset email shortly.";
    private const string EmailVerificationPurpose = "email_verification";
    private const string EmailVerificationGenericMessage = "If the email can be verified, you'll receive a verification email shortly.";
    private static readonly TimeSpan EmailVerificationLifetime = TimeSpan.FromDays(1);
    private static readonly TimeSpan EmailVerificationResendCooldown = TimeSpan.FromMinutes(1);

    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAuthServerOptions _options;
    private readonly SqlOSPasswordResetOptions _passwordResetOptions;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly SqlOSEmailOtpService _emailOtpService;
    private readonly SqlOSMagicLinkService? _magicLinkService;
    private readonly SqlOSPhoneOtpService? _phoneOtpService;
    private readonly SqlOSMfaPolicyService? _mfaPolicyService;
    private readonly SqlOSTotpMfaService? _totpMfaService;
    private readonly SqlOSInvitationService? _invitationService;
    private readonly SqlOSPasswordLoginAbuseService _passwordLoginAbuseService;
    private readonly ISqlOSTransactionalEmailService? _transactionalEmailService;
    private readonly ISqlOSAuthEmailSender? _authEmailSender;

    public SqlOSAuthService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSAdminService adminService,
        SqlOSCryptoService cryptoService,
        SqlOSSettingsService settingsService,
        SqlOSEmailOtpService emailOtpService,
        SqlOSInvitationService? invitationService = null,
        SqlOSPasswordLoginAbuseService? passwordLoginAbuseService = null,
        ISqlOSTransactionalEmailService? transactionalEmailService = null,
        SqlOSPhoneOtpService? phoneOtpService = null,
        ISqlOSAuthEmailSender? authEmailSender = null,
        SqlOSMfaPolicyService? mfaPolicyService = null,
        SqlOSTotpMfaService? totpMfaService = null,
        SqlOSMagicLinkService? magicLinkService = null)
    {
        _context = context;
        _options = options.Value;
        _passwordResetOptions = _options.PasswordReset;
        _adminService = adminService;
        _cryptoService = cryptoService;
        _settingsService = settingsService;
        _emailOtpService = emailOtpService;
        _magicLinkService = magicLinkService;
        _phoneOtpService = phoneOtpService;
        _invitationService = invitationService;
        _passwordLoginAbuseService = passwordLoginAbuseService
            ?? new SqlOSPasswordLoginAbuseService(context, adminService, cryptoService, options);
        _transactionalEmailService = transactionalEmailService;
        _authEmailSender = authEmailSender;
        _mfaPolicyService = mfaPolicyService;
        _totpMfaService = totpMfaService;
    }

    public async Task<SqlOSLoginResult> SignUpAsync(SqlOSSignupRequest request, HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var credentialSettings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!credentialSettings.PasswordSignupEnabled)
        {
            throw new InvalidOperationException("Password signup is disabled.");
        }

        SqlOSSignupJoinPolicy.RejectUnauthorizedOrganizationJoin(request.OrganizationId);

        var user = await _adminService.CreateUserAsync(new SqlOSCreateUserRequest(request.DisplayName, request.Email, request.Password), cancellationToken);

        string? organizationId = null;
        if (!string.IsNullOrWhiteSpace(request.OrganizationName))
        {
            var organization = await _adminService.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest(request.OrganizationName, null), cancellationToken);
            organizationId = organization.Id;
            await _adminService.CreateMembershipAsync(organization.Id, new SqlOSCreateMembershipRequest(user.Id, "owner"), cancellationToken);
        }

        await _adminService.RecordAuditAsync("user.signup", "user", user.Id, userId: user.Id, organizationId: organizationId, ipAddress: GetIp(httpContext), cancellationToken: cancellationToken);

        var client = await _adminService.RequireClientAsync(request.ClientId, null, cancellationToken);
        return await FinalizeClientLoginAsync(user, client, organizationId, "password", httpContext, cancellationToken);
    }

    public async Task<SqlOSLoginResult> LoginWithPasswordAsync(SqlOSPasswordLoginRequest request, HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var credentialSettings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!credentialSettings.PasswordEnabled)
        {
            throw new InvalidOperationException("Local password authentication is disabled.");
        }

        var normalizedEmail = SqlOSAdminService.NormalizeEmail(request.Email);
        var attempt = _passwordLoginAbuseService.CreateAttempt(
            normalizedEmail,
            httpContext,
            clientKey: request.ClientId,
            surface: "api");
        await _passwordLoginAbuseService.EnsureAllowedAsync(attempt, cancellationToken);

        var email = await _context.Set<SqlOSUserEmail>()
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);
        if (email == null)
        {
            await _passwordLoginAbuseService.RecordFailureAsync(attempt, "unknown_email", cancellationToken);
            throw new InvalidOperationException(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
        }

        attempt = attempt with { UserId = email.UserId };
        await _passwordLoginAbuseService.EnsureAllowedAsync(attempt, cancellationToken);

        if (_options.RequireVerifiedEmailForPasswordLogin && !email.IsVerified)
        {
            throw new InvalidOperationException("Email must be verified before password login.");
        }

        var credential = await _context.Set<SqlOSCredential>()
            .FirstOrDefaultAsync(x => x.UserId == email.UserId && x.Type == "password" && x.RevokedAt == null, cancellationToken);
        if (credential == null)
        {
            await _passwordLoginAbuseService.RecordFailureAsync(attempt, "missing_password_credential", cancellationToken);
            throw new InvalidOperationException(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
        }

        if (!_cryptoService.VerifyPassword(credential.SecretHash, request.Password))
        {
            await _passwordLoginAbuseService.RecordFailureAsync(attempt, "invalid_password", cancellationToken);
            throw new InvalidOperationException(SqlOSPasswordLoginAbuseService.PublicFailureMessage);
        }

        credential.LastUsedAt = DateTime.UtcNow;

        var user = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == email.UserId, cancellationToken);
        var client = await _adminService.RequireClientAsync(request.ClientId, null, cancellationToken);
        await _passwordLoginAbuseService.RecordSuccessAsync(attempt, cancellationToken);
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

    public async Task<SqlOSMagicLinkStartResult> RequestMagicLinkAsync(
        SqlOSMagicLinkStartRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => await RequireMagicLinkService().StartForClientAsync(request, httpContext, cancellationToken);

    public async Task<SqlOSPhoneOtpStartResult> RequestPhoneOtpAsync(
        SqlOSPhoneOtpStartRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => await RequirePhoneOtpService().StartForClientAsync(request, httpContext, cancellationToken);

    public async Task<SqlOSPhoneOtpSignupStartResult> RequestPhoneOtpSignupAsync(
        SqlOSPhoneOtpSignupStartRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => await RequirePhoneOtpService().StartSignupForClientAsync(request, httpContext, cancellationToken);

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

    public async Task<SqlOSDeviceAuthorizationStartResult> StartDeviceAuthorizationAsync(
        SqlOSDeviceAuthorizationStartRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
        => await CreateDeviceAuthorizationService().StartAsync(request, httpContext, cancellationToken);

    public async Task<SqlOSDeviceAuthorizationResolveResult> ResolveDeviceAuthorizationAsync(
        string userCode,
        SqlOSUser? user = null,
        CancellationToken cancellationToken = default)
        => await CreateDeviceAuthorizationService().ResolveAsync(userCode, user, cancellationToken);

    public async Task<SqlOSDeviceAuthorizationResolveResult> ApproveDeviceAuthorizationAsync(
        SqlOSDeviceAuthorizationApprovalRequest request,
        SqlOSUser user,
        string authenticationMethod,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
        => await CreateDeviceAuthorizationService().ApproveAsync(request, user, authenticationMethod, httpContext, cancellationToken);

    public async Task DenyDeviceAuthorizationAsync(
        string userCode,
        SqlOSUser? user,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
        => await CreateDeviceAuthorizationService().DenyAsync(userCode, user, httpContext, cancellationToken);

    public async Task<SqlOSDeviceTokenPollResult> PollDeviceAuthorizationAsync(
        SqlOSDeviceTokenPollRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
        => await CreateDeviceAuthorizationService().PollAsync(request, httpContext, cancellationToken);

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

            var result = await FinalizeClientLoginAsync(
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

            return result with { Organizations = organizations };
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

    public async Task<SqlOSLoginResult> CompleteMagicLinkAsync(
        SqlOSMagicLinkCompleteRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var verification = await RequireMagicLinkService().CompleteAsync(
            request,
            expectedAuthorizationRequestId: null,
            requireAuthorizationRequestMatch: true,
            cancellationToken);
        if (verification.Token.ClientApplicationId == null)
        {
            throw new InvalidOperationException("The sign-in link is invalid or expired.");
        }

        var client = await _context.Set<SqlOSClientApplication>()
            .FirstAsync(x => x.Id == verification.Token.ClientApplicationId, cancellationToken);

        return await FinalizeClientLoginAsync(
            verification.User,
            client,
            verification.Payload.RequestedOrganizationId,
            verification.AuthenticationMethod,
            httpContext,
            cancellationToken);
    }

    public async Task<SqlOSLoginResult> VerifyPhoneOtpAsync(
        SqlOSPhoneOtpVerifyRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var verification = await RequirePhoneOtpService().VerifyAsync(request, cancellationToken);
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

    public async Task<SqlOSLoginResult> VerifyPhoneOtpSignupAsync(
        SqlOSPhoneOtpSignupVerifyRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        IDbContextTransaction? transaction = null;
        SqlOSPasswordAuthenticationResult? signup = null;
        SqlOSPhoneOtpSignupVerificationResult? verification = null;

        try
        {
            if (SupportsDatabaseTransactions())
            {
                transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            }

            verification = await RequirePhoneOtpService().VerifySignupAsync(
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

            signup = await CreatePhoneOtpSignupUserAsync(
                verification.DisplayName,
                verification.PhoneNumber,
                verification.OrganizationName,
                verification.OrganizationId,
                cancellationToken);

            var selectedOrganizationId = verification.OrganizationId ?? signup.Organizations.FirstOrDefault()?.Id;
            var result = await FinalizeClientLoginAsync(
                signup.User,
                client,
                selectedOrganizationId,
                signup.AuthenticationMethod,
                httpContext,
                cancellationToken);

            await RequirePhoneOtpService().ConsumeSignupTokenAsync(verification.SignupToken, cancellationToken);
            await _adminService.RecordAuditAsync(
                "user.signup.phone_otp",
                "user",
                signup.User.Id,
                userId: signup.User.Id,
                organizationId: selectedOrganizationId,
                ipAddress: GetIp(httpContext),
                cancellationToken: cancellationToken);

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
        return await FinalizeClientLoginAsync(user, client, organizationId, authenticationMethod, httpContext, cancellationToken);
    }

    public async Task<SqlOSTokenResponse> SelectOrganizationAsync(SqlOSSelectOrganizationRequest request, HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var result = await SelectOrganizationForLoginAsync(request, httpContext, cancellationToken);
        if (result.Tokens == null)
        {
            throw new InvalidOperationException("The selected organization requires MFA.");
        }

        return result.Tokens;
    }

    public async Task<SqlOSLoginResult> SelectOrganizationForLoginAsync(SqlOSSelectOrganizationRequest request, HttpContext httpContext, CancellationToken cancellationToken = default)
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
        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);
        var result = await FinalizeClientLoginAsync(user, client, request.OrganizationId, authMethod, httpContext, cancellationToken);
        await _adminService.RecordAuditAsync(
            "user.login.organization-selected",
            "user",
            user.Id,
            userId: user.Id,
            organizationId: request.OrganizationId,
            ipAddress: GetIp(httpContext),
            cancellationToken: cancellationToken);
        return result with { Organizations = organizations };
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

        EnsureSessionIsActive(session);

        if (_options.ResourceIndicators.Enabled && !string.IsNullOrWhiteSpace(request.Resource))
        {
            var requestedResource = request.Resource.Trim();
            if (string.IsNullOrWhiteSpace(session.Resource)
                || !string.Equals(session.Resource, requestedResource, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Resource does not match the original authorization.");
            }
        }

        if (refreshToken.ConsumedAt != null)
        {
            return await HandleConsumedRefreshTokenAsync(refreshToken, session, request, securitySettings, cancellationToken);
        }

        var requestedOrganizationId = string.IsNullOrWhiteSpace(request.OrganizationId)
            ? null
            : request.OrganizationId.Trim();
        var organizationId = requestedOrganizationId ?? session.OrganizationId;
        await RequireActiveLifecycleAsync(
            session.UserId,
            organizationId,
            "refresh",
            session.Id,
            cancellationToken);
        await _adminService.EnsureApplicationAccessAsync(
            session.ClientApplication!,
            session.UserId,
            organizationId,
            "application.access.refresh_denied",
            cancellationToken: cancellationToken);

        // Mint the access token, build the new refresh token row, and
        // populate the grace-window cache fields all BEFORE the single
        // SaveChangesAsync. This avoids a visibility window where
        // ConsumedAt is set but ReplacementTokenResponse is still null
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

        string? protectedReplacementResponse = null;
        if (securitySettings.RefreshTokenGraceWindow > TimeSpan.Zero)
        {
            var replacementResponse = JsonSerializer.Serialize(
                new RefreshTokenReplacementPayload(accessToken, newRawRefreshToken));
            protectedReplacementResponse = _cryptoService.ProtectRefreshTokenResponse(
                replacementResponse,
                securitySettings.RefreshTokenGraceWindow);
        }

        refreshToken.ConsumedAt = DateTime.UtcNow;
        refreshToken.ReplacedByTokenId = nextRefreshToken.Id;

        // Cache the complete response as one purpose-bound, time-limited
        // Data Protection payload. The raw replacement refresh token is
        // otherwise never persisted, and every retry receives this exact
        // pair instead of minting a sibling lineage.
        refreshToken.ReplacementTokenResponse = protectedReplacementResponse;
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
            // entire rotation (ConsumedAt + ReplacementTokenResponse cache
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

            if (fresh.RevokedAt != null || fresh.ExpiresAt <= DateTime.UtcNow)
            {
                throw new InvalidOperationException("Refresh token is no longer valid.");
            }

            // A concurrency conflict can now also mean replay detection
            // revoked the session while this request was rotating a live
            // descendant. In that case the parent was not consumed by a
            // winning rotation, so it must never enter the grace path.
            EnsureSessionIsActive(fresh.Session!);
            if (fresh.ConsumedAt == null)
            {
                throw new InvalidOperationException("Refresh token rotation could not be completed.");
            }

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
    /// access token was cached, return that access token plus a fresh sibling
    /// refresh token in the same family (grace window). Otherwise, trigger
    /// replay detection and revoke the family.
    /// </summary>
    private async Task<SqlOSTokenResponse> HandleConsumedRefreshTokenAsync(
        SqlOSRefreshToken refreshToken,
        SqlOSSession session,
        SqlOSRefreshRequest request,
        SqlOSResolvedSecuritySettings securitySettings,
        CancellationToken cancellationToken)
    {
        // Consumed-token retries bypass the normal rotation path, so the
        // session lifecycle must be checked here before protected response
        // material is read or released. Token-row revocation is not a
        // substitute for the session security boundary.
        EnsureSessionIsActive(session);

        var cachedOrganizationId = refreshToken.ReplacementOrganizationId ?? session.OrganizationId;

        // A consumed refresh token can only return the cached access token
        // minted by the winning rotation. Never let a retry select a
        // different tenant than that cached token.
        if (!string.IsNullOrWhiteSpace(request.OrganizationId)
            && !string.Equals(request.OrganizationId, cachedOrganizationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Organization does not match the original refresh.");
        }

        await RequireActiveLifecycleAsync(
            session.UserId,
            cachedOrganizationId,
            "refresh_grace_window",
            session.Id,
            cancellationToken);
        await _adminService.EnsureApplicationAccessAsync(
            session.ClientApplication!,
            session.UserId,
            cachedOrganizationId,
            "application.access.refresh_denied",
            cancellationToken: cancellationToken);

        var graceWindow = securitySettings.RefreshTokenGraceWindow;
        var withinGraceWindow = graceWindow > TimeSpan.Zero
            && refreshToken.ConsumedAt!.Value.Add(graceWindow) > DateTime.UtcNow
            && !string.IsNullOrEmpty(refreshToken.ReplacedByTokenId)
            && !string.IsNullOrEmpty(refreshToken.ReplacementTokenResponse)
            && refreshToken.ReplacementAccessTokenExpiresAt is { } cachedExpiry
            && cachedExpiry > DateTime.UtcNow;

        if (withinGraceWindow)
        {
            var replacement = await _context.Set<SqlOSRefreshToken>()
                .FirstOrDefaultAsync(x => x.Id == refreshToken.ReplacedByTokenId, cancellationToken);

            if (replacement != null
                && replacement.RevokedAt == null
                && replacement.ExpiresAt > DateTime.UtcNow
                && string.Equals(replacement.SessionId, refreshToken.SessionId, StringComparison.Ordinal)
                && string.Equals(replacement.FamilyId, refreshToken.FamilyId, StringComparison.Ordinal))
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

                RefreshTokenReplacementPayload cachedResponse;
                try
                {
                    var responseJson = _cryptoService.UnprotectRefreshTokenResponse(refreshToken.ReplacementTokenResponse!);
                    cachedResponse = JsonSerializer.Deserialize<RefreshTokenReplacementPayload>(responseJson)
                        ?? throw new InvalidOperationException("The cached refresh token response is invalid.");

                    if (string.IsNullOrWhiteSpace(cachedResponse.AccessToken)
                        || string.IsNullOrWhiteSpace(cachedResponse.RefreshToken)
                        || !string.Equals(
                            _cryptoService.HashToken(cachedResponse.RefreshToken),
                            replacement.TokenHash,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("The cached refresh token response does not match its replacement token.");
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or JsonException)
                {
                    await RevokeRefreshTokenFamilyAsync(
                        session.Id,
                        refreshToken.FamilyId,
                        "refresh_token_response_invalid",
                        cancellationToken);
                    throw new InvalidOperationException("Refresh token has already been used.");
                }

                return new SqlOSTokenResponse(
                    cachedResponse.AccessToken,
                    cachedResponse.RefreshToken,
                    session.Id,
                    session.ClientApplication!.ClientId,
                    cachedOrganizationId,
                    refreshToken.ReplacementAccessTokenExpiresAt!.Value,
                    replacement.ExpiresAt);
            }
        }

        await RevokeRefreshTokenFamilyAsync(session.Id, refreshToken.FamilyId, "refresh_token_reuse", cancellationToken);
        throw new InvalidOperationException("Refresh token has already been used.");
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

        await RevokeSessionAsync(session, cancellationToken);
    }

    internal async Task<bool> LogoutByRefreshTokenAsync(
        string? refreshToken,
        CancellationToken cancellationToken = default)
    {
        var session = await FindActiveSessionByRefreshTokenAsync(refreshToken, cancellationToken);
        if (session == null)
        {
            return false;
        }

        await RevokeSessionAsync(session, cancellationToken);
        return true;
    }

    private async Task RevokeSessionAsync(SqlOSSession session, CancellationToken cancellationToken)
    {
        session.RevokedAt = DateTime.UtcNow;
        session.RevocationReason = "logout";
        var refreshTokens = await _context.Set<SqlOSRefreshToken>().Where(x => x.SessionId == session.Id && x.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var token in refreshTokens)
        {
            token.RevokedAt = DateTime.UtcNow;
            token.ReplacementTokenResponse = null;
            token.ReplacementOrganizationId = null;
            token.ReplacementAccessTokenExpiresAt = null;
        }

        await _context.SaveChangesAsync(cancellationToken);
        await _adminService.RecordAuditAsync("user.logout", "session", session.Id, userId: session.UserId, sessionId: session.Id, cancellationToken: cancellationToken);
    }

    public async Task LogoutAllAsync(string userId, CancellationToken cancellationToken = default)
    {
        await SqlOSAuthLifecyclePolicy.RevokeAsync(
            _context,
            userId,
            organizationId: null,
            "logout_all",
            DateTime.UtcNow,
            cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        await _adminService.RecordAuditAsync("user.logout-all", "user", userId, userId: userId, cancellationToken: cancellationToken);
    }

    internal async Task<bool> LogoutAllByRefreshTokenAsync(
        string? refreshToken,
        CancellationToken cancellationToken = default)
    {
        var session = await FindActiveSessionByRefreshTokenAsync(refreshToken, cancellationToken);
        if (session == null)
        {
            return false;
        }

        await LogoutAllAsync(session.UserId, cancellationToken);
        return true;
    }

    private async Task<SqlOSSession?> FindActiveSessionByRefreshTokenAsync(
        string? refreshToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var hashed = _cryptoService.HashToken(refreshToken);
        var token = await _context.Set<SqlOSRefreshToken>()
            .Include(x => x.Session)
            .FirstOrDefaultAsync(x => x.TokenHash == hashed
                && x.RevokedAt == null
                && x.ConsumedAt == null
                && x.ExpiresAt >= now,
                cancellationToken);
        var session = token?.Session;
        return session != null
            && session.RevokedAt == null
            && session.IdleExpiresAt >= now
            && session.AbsoluteExpiresAt >= now
                ? session
                : null;
    }

    public async Task<string> CreatePasswordResetTokenAsync(SqlOSForgotPasswordRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = SqlOSAdminService.NormalizeEmail(request.Email);
        var email = await _context.Set<SqlOSUserEmail>()
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken)
            ?? throw new InvalidOperationException("Unknown email address.");

        var credentialSettings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!await IsPasswordResetEligibleAsync(email, credentialSettings, cancellationToken))
        {
            throw new InvalidOperationException("Password reset is unavailable for this account.");
        }

        var client = await TryResolveClientApplicationAsync(request.ClientId, cancellationToken);
        var (token, _) = await CreatePasswordResetTokenForEmailAsync(email, client?.Id, cancellationToken);
        return token;
    }

    public async Task<SqlOSPasswordResetRequestResult> RequestPasswordResetEmailAsync(
        SqlOSForgotPasswordRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var trimmedEmail = NormalizeEmailInput(request.Email);
        var normalizedEmail = SqlOSAdminService.NormalizeEmail(trimmedEmail);
        var maskedEmail = MaskEmail(trimmedEmail);
        var ipAddress = GetIp(httpContext);
        var client = await TryResolveClientApplicationAsync(request.ClientId, cancellationToken);
        var clientKey = NormalizeClientKey(request.ClientId);

        var email = await _context.Set<SqlOSUserEmail>()
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);

        var rateLimit = await CheckPasswordResetRateLimitAsync(
            normalizedEmail,
            email?.UserId,
            ipAddress,
            clientKey,
            now,
            cancellationToken);

        if (rateLimit.IsLimited)
        {
            await RecordPasswordResetAuditAsync(
                "password_reset.rate_limit_rejected",
                "system",
                null,
                email?.UserId,
                maskedEmail,
                ipAddress,
                new
                {
                    scope = rateLimit.Scope,
                    retryAfter = rateLimit.RetryAfter,
                    clientKey
                },
                cancellationToken);
            return BuildPasswordResetRequestResult(trimmedEmail, maskedEmail, now, rateLimit.RetryAfter);
        }

        await RecordPasswordResetRequestMarkerAsync(
            normalizedEmail,
            email?.UserId,
            client?.Id,
            ipAddress,
            clientKey,
            "public",
            cancellationToken);

        var credentialSettings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        var eligible = await IsPasswordResetEligibleAsync(email, credentialSettings, cancellationToken);

        await RecordPasswordResetAuditAsync(
            "password_reset.requested",
            "system",
            null,
            email?.UserId,
            maskedEmail,
            ipAddress,
            new { eligible, clientKey },
            cancellationToken);

        if (!eligible || email == null)
        {
            return BuildPasswordResetRequestResult(trimmedEmail, maskedEmail, now);
        }

        try
        {
            await SendPasswordResetEmailToEligibleUserAsync(
                email,
                trustedResetUrlTemplate: null,
                client?.Id,
                client?.IsFirstParty == true ? client.ClientId : null,
                httpContext,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BuildPasswordResetRequestResult(trimmedEmail, maskedEmail, now);
        }

        return BuildPasswordResetRequestResult(trimmedEmail, maskedEmail, now);
    }

    public async Task<SqlOSPasswordResetEmailResult> SendPasswordResetEmailAsync(
        SqlOSSendPasswordResetEmailRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = SqlOSAdminService.NormalizeEmail(request.Email);
        var email = await _context.Set<SqlOSUserEmail>()
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken)
            ?? throw new InvalidOperationException("Unknown email address.");

        var credentialSettings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!await IsPasswordResetEligibleAsync(email, credentialSettings, cancellationToken))
        {
            throw new InvalidOperationException("Password reset is unavailable for this account.");
        }

        var client = await TryResolveClientApplicationAsync(request.ClientId, cancellationToken);
        return await SendPasswordResetEmailToEligibleUserAsync(
            email,
            request.ResetUrlTemplate,
            client?.Id,
            client?.IsFirstParty == true ? client.ClientId : null,
            httpContext,
            cancellationToken);
    }

    public async Task<SqlOSPasswordResetEmailResult> SendPasswordResetEmailForUserAsync(
        string userId,
        SqlOSSendUserPasswordResetEmailRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = userId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedUserId))
        {
            throw new InvalidOperationException("User id is required.");
        }

        var user = await _context.Set<SqlOSUser>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == normalizedUserId, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");

        var email = await _context.Set<SqlOSUserEmail>()
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.UserId == user.Id && x.Email == user.DefaultEmail, cancellationToken)
            ?? await _context.Set<SqlOSUserEmail>()
                .Include(x => x.User)
                .OrderByDescending(x => x.IsVerified)
                .ThenBy(x => x.CreatedAt)
                .FirstOrDefaultAsync(x => x.UserId == user.Id, cancellationToken)
            ?? throw new InvalidOperationException("User does not have an email address.");

        var credentialSettings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!await IsPasswordResetEligibleAsync(email, credentialSettings, cancellationToken))
        {
            throw new InvalidOperationException("Password reset is unavailable for this account.");
        }

        var result = await SendPasswordResetEmailToEligibleUserAsync(
            email,
            request.ResetUrlTemplate,
            clientApplicationId: null,
            clientId: null,
            httpContext,
            cancellationToken);
        await RecordPasswordResetAuditAsync(
            "password_reset.admin_email_sent",
            "admin",
            null,
            email.UserId,
            result.MaskedEmail,
            GetIp(httpContext),
            new { result.DeliveryId, result.DeliveryStatus },
            cancellationToken);
        return result;
    }

    private async Task<SqlOSPasswordResetEmailResult> SendPasswordResetEmailToEligibleUserAsync(
        SqlOSUserEmail email,
        string? trustedResetUrlTemplate,
        string? clientApplicationId,
        string? clientId,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        var maskedEmail = MaskEmail(email.Email);
        string? token = null;

        try
        {
            var tokenResult = await CreatePasswordResetTokenForEmailAsync(email, clientApplicationId, cancellationToken);
            token = tokenResult.Token;
            var expiresAt = tokenResult.ExpiresAt;
            var context = await BuildPasswordResetMessageContextAsync(
                email.Email,
                maskedEmail,
                token,
                expiresAt,
                trustedResetUrlTemplate,
                clientId,
                cancellationToken);

            if (_passwordResetOptions.BuildMessage != null)
            {
                var authEmailSender = _authEmailSender
                    ?? throw new InvalidOperationException("Auth email sender is not registered.");
                if (!authEmailSender.IsConfigured)
                {
                    throw new InvalidOperationException("Auth email delivery is not configured.");
                }

                var message = BuildLegacyPasswordResetMessage(context);
                await authEmailSender.SendAsync(message, cancellationToken);
                var deliveryId = _cryptoService.GenerateId("edl");
                await RecordPasswordResetAuditAsync(
                    "password_reset.email_sent",
                    "user",
                    email.UserId,
                    email.UserId,
                    context.MaskedEmail,
                    GetIp(httpContext),
                    new { deliveryId, customMessage = true },
                    cancellationToken);
                return new SqlOSPasswordResetEmailResult(
                    email.Email,
                    context.MaskedEmail,
                    expiresAt,
                    deliveryId,
                    SqlOSEmailDeliveryStatuses.Queued,
                    ProviderMessageId: null,
                    SanitizedError: null,
                    $"Password reset email queued for {context.MaskedEmail}.");
            }

            var transactionalEmailService = _transactionalEmailService
                ?? throw new InvalidOperationException("Transactional email service is not registered.");
            var result = await transactionalEmailService.SendAsync(
                new SqlOSSendEmailRequest(
                    SqlOSBuiltInEmailTemplates.AuthPasswordResetKey,
                    email.Email,
                    BuildPasswordResetTemplateVariables(context),
                    IdempotencyKey: $"auth-password-reset:{email.UserId}:{_cryptoService.HashToken(token)[..32]}"),
                cancellationToken);

            if (string.Equals(result.Status, SqlOSEmailDeliveryStatuses.Failed, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(result.SanitizedError ?? "Password reset email delivery failed.");
            }

            await RecordPasswordResetAuditAsync(
                "password_reset.email_sent",
                "user",
                email.UserId,
                email.UserId,
                context.MaskedEmail,
                GetIp(httpContext),
                new { result.DeliveryId, DeliveryStatus = result.Status, result.ProviderMessageId },
                cancellationToken);

            return new SqlOSPasswordResetEmailResult(
                email.Email,
                context.MaskedEmail,
                expiresAt,
                result.DeliveryId,
                result.Status,
                result.ProviderMessageId,
                result.SanitizedError,
                $"Password reset email queued for {context.MaskedEmail}.");
        }
        catch (Exception ex)
        {
            if (token != null)
            {
                // Once the reset token has been persisted, cleanup must not inherit request
                // cancellation. A disconnected caller must not be able to strand a live token
                // between link generation and delivery.
                await InvalidateActivePasswordResetTokensAsync(email.UserId, CancellationToken.None);
            }
            await RecordPasswordResetAuditAsync(
                "password_reset.email_send_failed",
                "system",
                null,
                email.UserId,
                maskedEmail,
                GetIp(httpContext),
                new { error = ex.Message },
                CancellationToken.None);
            throw;
        }
    }

    public async Task ResetPasswordAsync(SqlOSResetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        var credentialSettings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!credentialSettings.PasswordEnabled)
        {
            throw new InvalidOperationException("Local password authentication is disabled.");
        }

        var token = await _cryptoService.ConsumeTemporaryTokenAsync(PasswordResetPurpose, request.Token, cancellationToken);
        if (token == null)
        {
            await RecordPasswordResetAuditAsync(
                "password_reset.invalid_or_expired",
                "system",
                null,
                null,
                null,
                null,
                new { reason = "missing_or_consumed" },
                cancellationToken);
            throw new InvalidOperationException("Password reset token is invalid or expired.");
        }

        var user = await _context.Set<SqlOSUser>()
            .FirstOrDefaultAsync(x => x.Id == token.UserId, cancellationToken);
        if (user == null || !user.IsActive)
        {
            await RecordPasswordResetAuditAsync(
                "password_reset.invalid_or_expired",
                "system",
                null,
                token.UserId,
                null,
                null,
                new { reason = user == null ? "missing_user" : "inactive_user" },
                cancellationToken);
            throw new InvalidOperationException("Password reset token is invalid or expired.");
        }

        var credential = await _context.Set<SqlOSCredential>()
            .FirstOrDefaultAsync(x => x.UserId == token.UserId && x.Type == "password" && x.RevokedAt == null, cancellationToken);

        if (credential == null)
        {
            await RecordPasswordResetAuditAsync(
                "password_reset.invalid_or_expired",
                "system",
                null,
                token.UserId,
                null,
                null,
                new { reason = "missing_password_credential" },
                cancellationToken);
            throw new InvalidOperationException("Password reset token is invalid or expired.");
        }

        credential.SecretHash = _cryptoService.HashPassword(request.NewPassword);
        credential.LastUsedAt = null;
        await SqlOSAuthLifecyclePolicy.RevokeAsync(
            _context,
            user.Id,
            organizationId: null,
            "password_reset",
            DateTime.UtcNow,
            cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        await RecordPasswordResetAuditAsync(
            "password_reset.completed",
            "user",
            token.UserId,
            token.UserId,
            null,
            null,
            null,
            cancellationToken);
    }

    private async Task<(string Token, DateTime ExpiresAt)> CreatePasswordResetTokenForEmailAsync(
        SqlOSUserEmail email,
        string? clientApplicationId,
        CancellationToken cancellationToken)
    {
        await InvalidateActivePasswordResetTokensAsync(email.UserId, cancellationToken);

        var expiresAt = DateTime.UtcNow.Add(_passwordResetOptions.TokenLifetime);
        var token = await _cryptoService.CreateTemporaryTokenAsync(
            PasswordResetPurpose,
            email.UserId,
            clientApplicationId,
            null,
            new PasswordResetPayload(email.Id, email.NormalizedEmail),
            _passwordResetOptions.TokenLifetime,
            cancellationToken);

        return (token, expiresAt);
    }

    public async Task<string> CreateEmailVerificationTokenAsync(SqlOSCreateVerificationTokenRequest request, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = SqlOSAdminService.NormalizeEmail(request.Email);
        var email = await _context.Set<SqlOSUserEmail>().FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken)
            ?? throw new InvalidOperationException("Unknown email address.");

        var token = await _cryptoService.CreateTemporaryTokenAsync(
            EmailVerificationPurpose,
            email.UserId,
            null,
            null,
            new EmailVerificationPayload(email.Id),
            EmailVerificationLifetime,
            cancellationToken);

        await _adminService.RecordAuditAsync("user.email-verification-token-created", "system", null, userId: email.UserId, cancellationToken: cancellationToken);
        return token;
    }

    public async Task<SqlOSEmailVerificationRequestResult> RequestEmailVerificationAsync(
        SqlOSCreateVerificationTokenRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var trimmedEmail = NormalizeEmailInput(request.Email);
        var normalizedEmail = SqlOSAdminService.NormalizeEmail(trimmedEmail);
        var maskedEmail = MaskEmail(trimmedEmail);
        var now = DateTime.UtcNow;
        var email = await _context.Set<SqlOSUserEmail>()
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);

        await _adminService.RecordAuditAsync(
            "user.email-verification-requested",
            "system",
            null,
            userId: email?.UserId,
            ipAddress: GetIp(httpContext),
            data: new { maskedEmail, eligible = email is { IsVerified: false } },
            cancellationToken: cancellationToken);

        if (email == null || email.IsVerified)
        {
            return new SqlOSEmailVerificationRequestResult(EmailVerificationGenericMessage);
        }

        var recentTokens = await _context.Set<SqlOSTemporaryToken>()
            .Where(x => x.Purpose == EmailVerificationPurpose
                && x.UserId == email.UserId
                && x.ConsumedAt == null
                && x.ExpiresAt >= now
                && x.CreatedAt >= now.Subtract(EmailVerificationResendCooldown))
            .ToListAsync(cancellationToken);
        if (recentTokens.Any(token =>
                _cryptoService.DeserializePayload<EmailVerificationPayload>(token)?.EmailId == email.Id))
        {
            return new SqlOSEmailVerificationRequestResult(EmailVerificationGenericMessage);
        }

        string? rawToken = null;
        try
        {
            rawToken = await CreateEmailVerificationTokenAsync(request, cancellationToken);
            var branding = await _settingsService.GetResolvedAuthEmailBrandingAsync(cancellationToken);
            var applicationName = string.IsNullOrWhiteSpace(branding.ApplicationName)
                ? string.IsNullOrWhiteSpace(_options.EmailOtp.ApplicationName)
                    ? "SqlOS"
                    : _options.EmailOtp.ApplicationName.Trim()
                : branding.ApplicationName;
            var verificationUrl = $"{GetTrustedPublicOrigin()}{_options.BasePath.TrimEnd('/')}/email/verify?token={Uri.EscapeDataString(rawToken)}";
            var result = await (_transactionalEmailService
                    ?? throw new InvalidOperationException("Transactional email service is not registered."))
                .SendAsync(
                    new SqlOSSendEmailRequest(
                        SqlOSBuiltInEmailTemplates.AuthEmailVerificationKey,
                        email.Email,
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["applicationName"] = applicationName,
                            ["logoBase64"] = branding.LogoBase64 ?? string.Empty,
                            ["logoImageDisplay"] = string.IsNullOrWhiteSpace(branding.LogoBase64) ? "none" : "block",
                            ["logoTextDisplay"] = string.IsNullOrWhiteSpace(branding.LogoBase64) ? "block" : "none",
                            ["maskedEmail"] = MaskEmail(email.Email),
                            ["verificationUrl"] = verificationUrl,
                            ["expiresInHours"] = (int)EmailVerificationLifetime.TotalHours,
                            ["primaryColor"] = branding.PrimaryColor,
                            ["accentColor"] = branding.AccentColor,
                            ["backgroundColor"] = branding.BackgroundColor
                        },
                        IdempotencyKey: $"auth-email-verification:{email.Id}:{_cryptoService.HashToken(rawToken)[..32]}"),
                    cancellationToken);

            if (string.Equals(result.Status, SqlOSEmailDeliveryStatuses.Failed, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(result.SanitizedError ?? "Email verification delivery failed.");
            }

            await _adminService.RecordAuditAsync(
                "user.email-verification-sent",
                "system",
                null,
                userId: email.UserId,
                ipAddress: GetIp(httpContext),
                data: new { maskedEmail, result.DeliveryId, DeliveryStatus = result.Status, result.ProviderMessageId },
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (rawToken != null)
            {
                var token = await _cryptoService.FindTemporaryTokenAsync(
                    EmailVerificationPurpose,
                    rawToken,
                    CancellationToken.None);
                if (token != null)
                {
                    token.ConsumedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync(CancellationToken.None);
                }
            }

            await _adminService.RecordAuditAsync(
                "user.email-verification-send-failed",
                "system",
                null,
                userId: email.UserId,
                ipAddress: GetIp(httpContext),
                data: new { maskedEmail, error = ex.Message },
                cancellationToken: CancellationToken.None);
        }

        return new SqlOSEmailVerificationRequestResult(EmailVerificationGenericMessage);
    }

    public async Task VerifyEmailAsync(SqlOSVerifyEmailRequest request, CancellationToken cancellationToken = default)
    {
        var token = await _cryptoService.ConsumeTemporaryTokenAsync(EmailVerificationPurpose, request.Token, cancellationToken)
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

    public Task<SqlOSValidatedToken?> ValidateAccessTokenAsync(
        string rawToken,
        string expectedAudience,
        CancellationToken cancellationToken = default)
        => _cryptoService.ValidateAccessTokenAsync(rawToken, expectedAudience, cancellationToken);

    [Obsolete("This method does not validate the JWT aud claim and must only be used for token introspection or diagnostics. Resource servers must call ValidateAccessTokenAsync(rawToken, expectedAudience, cancellationToken).", false)]
    public Task<SqlOSValidatedToken?> ValidateAccessTokenWithoutAudienceForIntrospectionOnlyAsync(
        string rawToken,
        CancellationToken cancellationToken = default)
        => _cryptoService.ValidateAccessTokenWithoutAudienceForIntrospectionOnlyAsync(rawToken, cancellationToken);

    public async Task<SqlOSMfaStatusResult> GetMfaStatusAsync(
        string userId,
        string? organizationId = null,
        CancellationToken cancellationToken = default)
        => await RequireTotpMfaService().GetStatusAsync(userId, organizationId, cancellationToken);

    public async Task<IReadOnlyList<SqlOSMfaAuthenticatorDto>> ListMfaAuthenticatorsAsync(
        string userId,
        CancellationToken cancellationToken = default)
        => await RequireTotpMfaService().ListAuthenticatorsAsync(userId, cancellationToken);

    public async Task<SqlOSTotpEnrollmentStartResult> StartTotpEnrollmentAsync(
        string userId,
        SqlOSTotpEnrollmentStartRequest request,
        string? organizationId = null,
        CancellationToken cancellationToken = default)
        => await RequireTotpMfaService().StartEnrollmentAsync(
            userId,
            organizationId,
            request.DisplayName,
            cancellationToken: cancellationToken);

    public async Task<SqlOSTotpEnrollmentStartResult> StartTotpEnrollmentForChallengeAsync(
        string mfaToken,
        SqlOSTotpEnrollmentStartRequest request,
        CancellationToken cancellationToken = default)
        => await StartTotpEnrollmentForChallengeCoreAsync(
            mfaToken,
            request,
            expectedFlow: "client",
            expectedAuthorizationRequestId: null,
            cancellationToken);

    internal async Task<SqlOSTotpEnrollmentStartResult> StartTotpEnrollmentForAuthorizationChallengeAsync(
        string mfaToken,
        string authorizationRequestId,
        SqlOSTotpEnrollmentStartRequest request,
        CancellationToken cancellationToken = default)
        => await StartTotpEnrollmentForChallengeCoreAsync(
            mfaToken,
            request,
            expectedFlow: "authorization",
            expectedAuthorizationRequestId: authorizationRequestId,
            cancellationToken);

    private async Task<SqlOSTotpEnrollmentStartResult> StartTotpEnrollmentForChallengeCoreAsync(
        string mfaToken,
        SqlOSTotpEnrollmentStartRequest request,
        string expectedFlow,
        string? expectedAuthorizationRequestId,
        CancellationToken cancellationToken)
    {
        var token = await RequireTotpMfaService().GetPendingMfaTokenAsync(mfaToken, cancellationToken);
        try
        {
            var payload = await ValidateEnrollmentChallengeAsync(
                token,
                expectedFlow,
                expectedAuthorizationRequestId,
                cancellationToken);
            return await RequireTotpMfaService().StartChallengeEnrollmentAsync(
                token,
                payload,
                request.DisplayName,
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            await RecordRejectedChallengeEnrollmentAsync(token, "start", cancellationToken);
            throw new InvalidOperationException("MFA enrollment is not authorized for this challenge.");
        }
    }

    public async Task<SqlOSTotpEnrollmentVerifyResult> VerifyTotpEnrollmentAsync(
        SqlOSTotpEnrollmentVerifyRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.MfaToken))
        {
            return await RequireTotpMfaService().VerifyEnrollmentAsync(request, cancellationToken);
        }

        IDbContextTransaction? transaction = null;
        try
        {
            if (SupportsDatabaseTransactions() && _context.Database.CurrentTransaction == null)
            {
                transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            }

            var verification = await VerifyTotpChallengeEnrollmentCoreAsync(
                request,
                expectedFlow: "client",
                expectedAuthorizationRequestId: null,
                cancellationToken);
            var challengeResult = await CompleteConsumedMfaChallengeAsync(
                verification.ChallengeToken,
                SqlOSMfaFactorTypes.Totp,
                httpContext,
                cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return verification.Enrollment with
            {
                Tokens = challengeResult.Tokens,
                RedirectUrl = challengeResult.RedirectUrl
            };
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

    internal async Task<SqlOSTotpChallengeEnrollmentVerification> VerifyTotpEnrollmentForAuthorizationChallengeAsync(
        SqlOSTotpEnrollmentVerifyRequest request,
        string authorizationRequestId,
        CancellationToken cancellationToken = default)
        => await VerifyTotpChallengeEnrollmentCoreAsync(
            request,
            expectedFlow: "authorization",
            expectedAuthorizationRequestId: authorizationRequestId,
            cancellationToken);

    private async Task<SqlOSTotpChallengeEnrollmentVerification> VerifyTotpChallengeEnrollmentCoreAsync(
        SqlOSTotpEnrollmentVerifyRequest request,
        string expectedFlow,
        string? expectedAuthorizationRequestId,
        CancellationToken cancellationToken)
        => await RequireTotpMfaService().VerifyChallengeEnrollmentAsync(
            request,
            expectedFlow,
            expectedAuthorizationRequestId,
            cancellationToken);

    private async Task<SqlOSMfaChallengePayload> ValidateEnrollmentChallengeAsync(
        SqlOSTemporaryToken token,
        string expectedFlow,
        string? expectedAuthorizationRequestId,
        CancellationToken cancellationToken)
    {
        if (token.UserId == null || token.ClientApplicationId == null)
        {
            throw new InvalidOperationException("MFA challenge payload is invalid.");
        }

        var payload = _cryptoService.DeserializePayload<SqlOSMfaChallengePayload>(token)
            ?? throw new InvalidOperationException("MFA challenge payload is invalid.");
        if (!payload.EnrollmentRequired
            || payload.PermittedEnrollmentFactors?.Contains(SqlOSMfaFactorTypes.Totp, StringComparer.OrdinalIgnoreCase) != true
            || !string.Equals(payload.Flow, expectedFlow, StringComparison.Ordinal)
            || (expectedAuthorizationRequestId != null
                && !string.Equals(payload.AuthorizationRequestId, expectedAuthorizationRequestId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("MFA enrollment is not authorized for this challenge.");
        }

        var client = await _context.Set<SqlOSClientApplication>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == token.ClientApplicationId, cancellationToken);
        if (client == null || !string.Equals(client.ClientId, payload.ClientId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("MFA challenge client binding is invalid.");
        }

        if (string.Equals(expectedFlow, "authorization", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(payload.AuthorizationRequestId))
            {
                throw new InvalidOperationException("MFA challenge authorization binding is invalid.");
            }

            var request = await _context.Set<SqlOSAuthorizationRequest>()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == payload.AuthorizationRequestId, cancellationToken);
            if (request == null || !string.Equals(request.ClientApplicationId, token.ClientApplicationId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("MFA challenge authorization binding is invalid.");
            }
        }

        return payload;
    }

    private async Task RecordRejectedChallengeEnrollmentAsync(
        SqlOSTemporaryToken token,
        string stage,
        CancellationToken cancellationToken)
    {
        try
        {
            await _adminService.RecordAuditAsync(
                "user.mfa.enrollment.challenge_rejected",
                "user",
                token.UserId,
                userId: token.UserId,
                organizationId: token.OrganizationId,
                data: new
                {
                    stage,
                    challenge_id = token.Id,
                    client_application_id = token.ClientApplicationId
                },
                cancellationToken: cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Rejection must remain fail-closed even if audit persistence is unavailable.
        }
    }

    public async Task<SqlOSMfaChallengeVerifyResult> VerifyMfaChallengeAsync(
        SqlOSMfaChallengeVerifyRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var token = await _cryptoService.FindTemporaryTokenAsync(MfaChallengePurpose, request.MfaToken, cancellationToken)
            ?? throw new InvalidOperationException("MFA challenge is invalid or expired.");
        if (token.UserId == null || token.ClientApplicationId == null)
        {
            throw new InvalidOperationException("MFA challenge payload is invalid.");
        }

        var payload = _cryptoService.DeserializePayload<SqlOSMfaChallengePayload>(token)
            ?? throw new InvalidOperationException("MFA challenge payload is invalid.");
        if (!string.Equals(payload.Flow, "client", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("MFA challenge is not valid for direct authentication.");
        }

        if (payload.EnrollmentRequired)
        {
            throw new InvalidOperationException("MFA enrollment must be completed with its challenge-bound enrollment proof.");
        }

        var factorMethod = await VerifyMfaChallengeFactorAsync(token, request.Code, httpContext, cancellationToken);
        token.ConsumedAt = DateTime.UtcNow;
        return await CompleteConsumedMfaChallengeAsync(token, factorMethod, httpContext, cancellationToken);
    }

    internal async Task<string> VerifyMfaChallengeFactorAsync(
        SqlOSTemporaryToken token,
        string code,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        if (token.UserId == null)
        {
            throw new InvalidOperationException("MFA challenge payload is invalid.");
        }

        await EnsureMfaAttemptAllowedAsync(token, httpContext, cancellationToken);
        try
        {
            return await RequireTotpMfaService().VerifySecondFactorCodeAsync(token.UserId, code, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            var attemptCount = await RecordMfaChallengeFailureAsync(token, cancellationToken);
            await TryRecordMfaChallengeAuditAsync(
                MfaChallengeFailedAuditEvent,
                token,
                httpContext,
                new
                {
                    attemptCount,
                    challengeLocked = attemptCount >= _options.Mfa.Totp.MaxFailedAttemptsPerChallenge
                },
                cancellationToken);
            throw new InvalidOperationException(MfaChallengeFailureMessage);
        }
    }

    internal async Task<string> CreateMfaChallengeAsync(
        SqlOSUser user,
        SqlOSClientApplication client,
        string? organizationId,
        string authenticationMethod,
        string flow,
        bool enrollmentRequired,
        IReadOnlyList<string> permittedEnrollmentFactors,
        string? authorizationRequestId = null,
        string? resource = null,
        CancellationToken cancellationToken = default)
    {
        var recentFailures = await CountRecentMfaFailuresAsync(user.Id, null, cancellationToken);
        if (recentFailures >= _options.Mfa.Totp.MaxFailedAttemptsPerUser)
        {
            await TryRecordMfaChallengeAuditAsync(
                "user.mfa.challenge_issue_rejected",
                user.Id,
                organizationId,
                null,
                new { recentFailures },
                cancellationToken);
            throw new InvalidOperationException(MfaChallengeFailureMessage);
        }

        return await _cryptoService.CreateTemporaryTokenAsync(
            MfaChallengePurpose,
            user.Id,
            client.Id,
            organizationId,
            new SqlOSMfaChallengePayload(
                flow,
                client.ClientId,
                authenticationMethod,
                authorizationRequestId,
                resource,
                enrollmentRequired,
                permittedEnrollmentFactors),
            _options.Mfa.Totp.ChallengeTokenLifetime,
            cancellationToken);
    }

    private async Task EnsureMfaAttemptAllowedAsync(
        SqlOSTemporaryToken token,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        var ipAddress = GetIp(httpContext);
        var recentUserFailures = await CountRecentMfaFailuresAsync(token.UserId!, null, cancellationToken);
        var recentIpFailures = ipAddress == null
            ? 0
            : await CountRecentMfaFailuresAsync(null, ipAddress, cancellationToken);
        if (recentUserFailures < _options.Mfa.Totp.MaxFailedAttemptsPerUser
            && recentIpFailures < _options.Mfa.Totp.MaxFailedAttemptsPerIp)
        {
            return;
        }

        token.ConsumedAt = DateTime.UtcNow;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request has already consumed or updated this challenge.
        }

        await TryRecordMfaChallengeAuditAsync(
            "user.mfa.challenge_rate_limited",
            token,
            httpContext,
            new { recentUserFailures, recentIpFailures },
            cancellationToken);
        throw new InvalidOperationException(MfaChallengeFailureMessage);
    }

    private async Task<int> RecordMfaChallengeFailureAsync(
        SqlOSTemporaryToken token,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var payload = _cryptoService.DeserializePayload<SqlOSMfaChallengePayload>(token)
                ?? throw new InvalidOperationException("MFA challenge payload is invalid.");
            if (token.ConsumedAt != null)
            {
                return payload.FailedAttempts;
            }

            var attemptCount = payload.FailedAttempts + 1;
            token.PayloadJson = JsonSerializer.Serialize(payload with { FailedAttempts = attemptCount });
            if (attemptCount >= _options.Mfa.Totp.MaxFailedAttemptsPerChallenge)
            {
                token.ConsumedAt = DateTime.UtcNow;
            }

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                return attemptCount;
            }
            catch (DbUpdateConcurrencyException) when (_context is DbContext dbContext)
            {
                dbContext.ChangeTracker.Clear();
                token = await _context.Set<SqlOSTemporaryToken>()
                    .FirstOrDefaultAsync(x => x.Id == token.Id, cancellationToken)
                    ?? throw new InvalidOperationException(MfaChallengeFailureMessage);
            }
        }
    }

    private async Task<int> CountRecentMfaFailuresAsync(
        string? userId,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow.Subtract(_options.Mfa.Totp.FailedAttemptWindow);
        return await _context.Set<SqlOSAuditEvent>()
            .AsNoTracking()
            .CountAsync(
                x => x.Action == MfaChallengeFailedAuditEvent
                    && x.OccurredAt >= cutoff
                    && (userId == null || x.UserId == userId)
                    && (ipAddress == null || x.IpAddress == ipAddress),
                cancellationToken);
    }

    private Task TryRecordMfaChallengeAuditAsync(
        string eventType,
        SqlOSTemporaryToken token,
        HttpContext? httpContext,
        object data,
        CancellationToken cancellationToken)
        => TryRecordMfaChallengeAuditAsync(
            eventType,
            token.UserId!,
            token.OrganizationId,
            GetIp(httpContext),
            new { challengeId = token.Id, details = data },
            cancellationToken);

    private async Task TryRecordMfaChallengeAuditAsync(
        string eventType,
        string userId,
        string? organizationId,
        string? ipAddress,
        object data,
        CancellationToken cancellationToken)
    {
        try
        {
            await _adminService.RecordAuditAsync(
                eventType,
                "system",
                null,
                userId: userId,
                organizationId: organizationId,
                ipAddress: ipAddress,
                data: data,
                cancellationToken: cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The challenge state is already fail-closed; audit availability must not reopen it.
        }
    }

    private async Task<SqlOSMfaChallengeVerifyResult> CompleteConsumedMfaChallengeAsync(
        SqlOSTemporaryToken token,
        string factorMethod,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        if (token.UserId == null || token.ClientApplicationId == null)
        {
            throw new InvalidOperationException("MFA challenge payload is invalid.");
        }

        var payload = _cryptoService.DeserializePayload<SqlOSMfaChallengePayload>(token)
            ?? throw new InvalidOperationException("MFA challenge payload is invalid.");
        if (!string.Equals(payload.Flow, "client", StringComparison.Ordinal))
        {
            return new SqlOSMfaChallengeVerifyResult(null, null);
        }

        var user = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == token.UserId, cancellationToken);
        var client = await _context.Set<SqlOSClientApplication>().FirstAsync(x => x.Id == token.ClientApplicationId, cancellationToken);
        var authenticationMethod = SqlOSMfaPolicyService.AddAuthenticationMethod(payload.AuthenticationMethod, factorMethod);
        var tokens = await CreateSessionAndTokensAsync(
            user,
            client,
            token.OrganizationId,
            authenticationMethod,
            httpContext?.Request.Headers.UserAgent.ToString(),
            GetIp(httpContext),
            payload.Resource,
            await _settingsService.GetResolvedSecuritySettingsAsync(cancellationToken),
            cancellationToken);

        await _adminService.RecordAuditAsync(
            "user.login.mfa",
            "user",
            user.Id,
            userId: user.Id,
            organizationId: token.OrganizationId,
            ipAddress: GetIp(httpContext),
            cancellationToken: cancellationToken);

        return new SqlOSMfaChallengeVerifyResult(tokens, null);
    }

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
        organizationId = string.IsNullOrWhiteSpace(organizationId) ? null : organizationId.Trim();
        await RequireActiveLifecycleAsync(
            user.Id,
            organizationId,
            "token_issue",
            sessionId: null,
            cancellationToken);
        var effectiveAudience = ResolveEffectiveAudience(client, resource);
        await _adminService.EnsureApplicationAccessAsync(
            client,
            user.Id,
            organizationId,
            "application.access.token_denied",
            ipAddress,
            cancellationToken);
        var session = new SqlOSSession
        {
            Id = _cryptoService.GenerateId("ses"),
            UserId = user.Id,
            ClientApplicationId = client.Id,
            OrganizationId = organizationId,
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

    private async Task RequireActiveLifecycleAsync(
        string userId,
        string? organizationId,
        string boundary,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var lifecycle = await SqlOSAuthLifecyclePolicy.EvaluateAsync(
            _context,
            userId,
            organizationId,
            cancellationToken);
        if (lifecycle.IsActive)
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
            organizationId,
            sessionId);
        await _context.SaveChangesAsync(cancellationToken);
        throw new InvalidOperationException("Session is no longer active.");
    }

    private string BuildPasswordResetUrl(string token, string? trustedResetUrlTemplate)
    {
        var escapedToken = Uri.EscapeDataString(token);
        if (!string.IsNullOrWhiteSpace(trustedResetUrlTemplate))
        {
            var template = trustedResetUrlTemplate.Trim();
            if (template.Contains("{token}", StringComparison.Ordinal))
            {
                ValidatePasswordResetTemplate(template);
                return template.Replace("{token}", escapedToken, StringComparison.Ordinal);
            }

            var templateUri = new Uri(ValidatePasswordResetUrl(template), UriKind.Absolute);
            var builder = new UriBuilder(templateUri);
            var query = builder.Query.TrimStart('?');
            builder.Query = string.IsNullOrEmpty(query)
                ? $"token={escapedToken}"
                : $"{query}&token={escapedToken}";
            return builder.Uri.AbsoluteUri;
        }

        return $"{GetTrustedPublicOrigin()}{_options.BasePath.TrimEnd('/')}/password/reset?token={escapedToken}";
    }

    private async Task<SqlOSPasswordResetMessageContext> BuildPasswordResetMessageContextAsync(
        string email,
        string maskedEmail,
        string token,
        DateTime expiresAt,
        string? trustedResetUrlTemplate,
        string? clientId,
        CancellationToken cancellationToken)
    {
        var branding = await _settingsService.GetResolvedAuthEmailBrandingAsync(cancellationToken);
        var applicationName = string.IsNullOrWhiteSpace(branding.ApplicationName)
            ? string.IsNullOrWhiteSpace(_options.EmailOtp.ApplicationName)
                ? "SqlOS"
                : _options.EmailOtp.ApplicationName.Trim()
            : branding.ApplicationName;
        var resetUrl = _passwordResetOptions.BuildResetUrl?.Invoke(
            new SqlOSPasswordResetUrlContext(
                token,
                email,
                maskedEmail,
                expiresAt,
                _passwordResetOptions.TokenLifetime,
                clientId))
            ?? BuildPasswordResetUrl(token, trustedResetUrlTemplate);
        resetUrl = ValidateGeneratedPasswordResetUrl(resetUrl, token);

        return new SqlOSPasswordResetMessageContext(
            applicationName,
            email,
            maskedEmail,
            resetUrl,
            expiresAt,
            _passwordResetOptions.TokenLifetime)
        {
            Branding = branding with { ApplicationName = applicationName }
        };
    }

    private IReadOnlyDictionary<string, object?> BuildPasswordResetTemplateVariables(SqlOSPasswordResetMessageContext context)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(context.TokenLifetime.TotalMinutes));
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["applicationName"] = context.ApplicationName,
            ["logoBase64"] = context.Branding.LogoBase64 ?? string.Empty,
            ["logoImageDisplay"] = string.IsNullOrWhiteSpace(context.Branding.LogoBase64) ? "none" : "block",
            ["logoTextDisplay"] = string.IsNullOrWhiteSpace(context.Branding.LogoBase64) ? "block" : "none",
            ["maskedEmail"] = context.MaskedEmail,
            ["resetUrl"] = context.ResetUrl,
            ["expiresInMinutes"] = minutes,
            ["primaryColor"] = context.Branding.PrimaryColor,
            ["accentColor"] = context.Branding.AccentColor,
            ["backgroundColor"] = context.Branding.BackgroundColor
        };
    }

    private SqlOSAuthEmailMessage BuildLegacyPasswordResetMessage(SqlOSPasswordResetMessageContext context)
    {
        var subject = string.IsNullOrWhiteSpace(_passwordResetOptions.Subject)
            ? "Reset your password"
            : _passwordResetOptions.Subject
                .Replace("{applicationName}", context.ApplicationName, StringComparison.Ordinal)
                .Replace("{ApplicationName}", context.ApplicationName, StringComparison.Ordinal);

        return _passwordResetOptions.BuildMessage?.Invoke(context)
            ?? new SqlOSAuthEmailMessage(
                context.Email,
                subject,
                SqlOSAuthEmailTemplateRenderer.BuildPasswordResetHtmlBody(context),
                SqlOSAuthEmailTemplateRenderer.BuildPasswordResetTextBody(context));
    }

    private async Task<bool> IsPasswordResetEligibleAsync(
        SqlOSUserEmail? email,
        SqlOSResolvedCredentialSettings credentialSettings,
        CancellationToken cancellationToken)
    {
        if (!credentialSettings.PasswordEnabled || email?.User == null || !email.User.IsActive)
        {
            return false;
        }

        return await _context.Set<SqlOSCredential>()
            .AnyAsync(x => x.UserId == email.UserId && x.Type == "password" && x.RevokedAt == null, cancellationToken);
    }

    private async Task<SqlOSClientApplication?> TryResolveClientApplicationAsync(
        string? clientId,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeClientKey(clientId);
        if (normalized == null)
        {
            return null;
        }

        return await _context.Set<SqlOSClientApplication>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ClientId == normalized && x.IsActive && x.DisabledAt == null, cancellationToken);
    }

    private async Task RecordPasswordResetRequestMarkerAsync(
        string normalizedEmail,
        string? userId,
        string? clientApplicationId,
        string? ipAddress,
        string? clientKey,
        string surface,
        CancellationToken cancellationToken)
    {
        await _cryptoService.CreateTemporaryTokenAsync(
            PasswordResetRequestPurpose,
            userId,
            clientApplicationId,
            organizationId: null,
            payload: new PasswordResetRequestPayload(normalizedEmail, ipAddress, clientKey, surface),
            lifetime: _passwordResetOptions.RateLimitWindow,
            cancellationToken: cancellationToken);
    }

    private async Task<PasswordResetRateLimitResult> CheckPasswordResetRateLimitAsync(
        string normalizedEmail,
        string? userId,
        string? ipAddress,
        string? clientKey,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var windowStart = now.Subtract(_passwordResetOptions.RateLimitWindow);
        var recentRequests = await _context.Set<SqlOSTemporaryToken>()
            .AsNoTracking()
            .Where(x => x.Purpose == PasswordResetRequestPurpose && x.CreatedAt >= windowStart)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        var requests = recentRequests
            .Select(token => new
            {
                token.CreatedAt,
                token.UserId,
                token.ClientApplicationId,
                Payload = _cryptoService.DeserializePayload<PasswordResetRequestPayload>(token)
            })
            .Where(x => x.Payload != null)
            .ToList();

        var emailMatches = requests
            .Where(x => string.Equals(x.Payload!.NormalizedEmail, normalizedEmail, StringComparison.Ordinal))
            .ToList();
        if (emailMatches.Count >= _passwordResetOptions.MaxRequestsPerEmailPerWindow)
        {
            return PasswordResetRateLimitResult.Limited("email", emailMatches[0].CreatedAt.Add(_passwordResetOptions.RateLimitWindow));
        }

        if (!string.IsNullOrWhiteSpace(userId))
        {
            var userMatches = requests
                .Where(x => string.Equals(x.UserId, userId, StringComparison.Ordinal))
                .ToList();
            if (userMatches.Count >= _passwordResetOptions.MaxRequestsPerEmailPerWindow)
            {
                return PasswordResetRateLimitResult.Limited("user", userMatches[0].CreatedAt.Add(_passwordResetOptions.RateLimitWindow));
            }
        }

        if (!string.IsNullOrWhiteSpace(ipAddress))
        {
            var ipMatches = requests
                .Where(x => string.Equals(x.Payload!.IpAddress, ipAddress, StringComparison.Ordinal))
                .ToList();
            if (ipMatches.Count >= _passwordResetOptions.MaxRequestsPerIpPerWindow)
            {
                return PasswordResetRateLimitResult.Limited("ip", ipMatches[0].CreatedAt.Add(_passwordResetOptions.RateLimitWindow));
            }
        }

        if (!string.IsNullOrWhiteSpace(clientKey))
        {
            var clientMatches = requests
                .Where(x => string.Equals(x.Payload!.ClientKey, clientKey, StringComparison.Ordinal))
                .ToList();
            if (clientMatches.Count >= _passwordResetOptions.MaxRequestsPerClientPerWindow)
            {
                return PasswordResetRateLimitResult.Limited("client", clientMatches[0].CreatedAt.Add(_passwordResetOptions.RateLimitWindow));
            }
        }

        return PasswordResetRateLimitResult.Allowed();
    }

    private async Task InvalidateActivePasswordResetTokensAsync(string userId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var activeTokens = await _context.Set<SqlOSTemporaryToken>()
            .Where(x => x.Purpose == PasswordResetPurpose
                && x.UserId == userId
                && x.ConsumedAt == null
                && x.ExpiresAt > now)
            .ToListAsync(cancellationToken);

        if (activeTokens.Count == 0)
        {
            return;
        }

        foreach (var activeToken in activeTokens)
        {
            activeToken.ConsumedAt = now;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordPasswordResetAuditAsync(
        string eventType,
        string actorType,
        string? actorId,
        string? userId,
        string? maskedEmail,
        string? ipAddress,
        object? data,
        CancellationToken cancellationToken)
        => await _adminService.RecordAuditAsync(
            eventType,
            actorType,
            actorId,
            userId: userId,
            ipAddress: ipAddress,
            data: new
            {
                maskedEmail,
                details = data
            },
            cancellationToken: cancellationToken);

    private SqlOSPasswordResetRequestResult BuildPasswordResetRequestResult(
        string email,
        string maskedEmail,
        DateTime now,
        DateTime? nextAllowedSendAt = null)
        => new(
            email,
            maskedEmail,
            PasswordResetGenericMessage,
            now.Add(_passwordResetOptions.TokenLifetime),
            nextAllowedSendAt ?? now.Add(_passwordResetOptions.ResendCooldown));

    private string GetTrustedPublicOrigin()
    {
        if (!string.IsNullOrWhiteSpace(_options.PublicOrigin))
        {
            return _options.PublicOrigin.TrimEnd('/');
        }

        if (!Uri.TryCreate(_options.Issuer, UriKind.Absolute, out var issuer))
        {
            throw new InvalidOperationException("AuthServer.Issuer must be an absolute URI before password reset links can be generated.");
        }

        return issuer.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static string ValidatePasswordResetUrl(string? resetUrl)
    {
        var trimmed = resetUrl?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)
            || trimmed.Any(char.IsControl)
            || trimmed.Contains('\\', StringComparison.Ordinal)
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri == null
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) || !uri.IsLoopback))
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("The configured password reset URL must be an absolute HTTPS URL (or loopback HTTP URL) without user information.");
        }

        return trimmed;
    }

    private static void ValidatePasswordResetTemplate(string template)
    {
        const string marker = "sqlos-password-reset-token-marker";
        var probe = template.Replace("{token}", marker, StringComparison.Ordinal);
        var probeUri = new Uri(ValidatePasswordResetUrl(probe), UriKind.Absolute);
        if (probeUri.GetLeftPart(UriPartial.Authority).Contains(marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The password reset token placeholder cannot appear in the URL authority.");
        }
    }

    private static string ValidateGeneratedPasswordResetUrl(string? resetUrl, string token)
    {
        var validated = ValidatePasswordResetUrl(resetUrl);
        var uri = new Uri(validated, UriKind.Absolute);
        if (uri.GetLeftPart(UriPartial.Authority).Contains(token, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The password reset token cannot appear in the URL authority.");
        }

        return validated;
    }

    private static string MaskEmail(string email)
    {
        var atIndex = email.IndexOf('@');
        if (atIndex <= 1 || atIndex == email.Length - 1)
        {
            return email;
        }

        var local = email[..atIndex];
        var domain = email[(atIndex + 1)..];
        var visibleCount = Math.Min(2, local.Length);
        return $"{local[..visibleCount]}***@{domain}";
    }

    private static string NormalizeEmailInput(string? email)
    {
        var trimmed = email?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("Email address is required.");
        }

        return trimmed;
    }

    private static string? NormalizeClientKey(string? clientKey)
        => string.IsNullOrWhiteSpace(clientKey) ? null : clientKey.Trim();

    private static string? GetIp(HttpContext? httpContext) => httpContext?.Connection.RemoteIpAddress?.ToString();

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

    private async Task<SqlOSPasswordAuthenticationResult> CreatePhoneOtpSignupUserAsync(
        string displayName,
        string phoneNumber,
        string? organizationName,
        string? organizationId,
        CancellationToken cancellationToken)
    {
        var credentialSettings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!credentialSettings.PhoneOtpEnabled)
        {
            throw new InvalidOperationException("Phone sign-in is unavailable.");
        }

        SqlOSSignupJoinPolicy.RejectUnauthorizedOrganizationJoin(organizationId);

        var user = new SqlOSUser
        {
            Id = _cryptoService.GenerateId("usr"),
            DisplayName = displayName,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.Set<SqlOSUser>().Add(user);
        await _context.SaveChangesAsync(cancellationToken);
        await RequirePhoneOtpService().AddVerifiedPhoneNumberAsync(user, phoneNumber, cancellationToken);

        if (!string.IsNullOrWhiteSpace(organizationName))
        {
            var createdOrganization = await _adminService.CreateOrganizationAsync(
                new SqlOSCreateOrganizationRequest(organizationName, null),
                cancellationToken);
            await _adminService.CreateMembershipAsync(createdOrganization.Id, new SqlOSCreateMembershipRequest(user.Id, "owner"), cancellationToken);
        }

        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);
        return new SqlOSPasswordAuthenticationResult(user, organizations, "phone_otp");
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
            var phoneNumbers = await _context.Set<SqlOSUserPhoneNumber>()
                .Where(x => x.UserId == user.Id)
                .ToListAsync(cancellationToken);
            if (phoneNumbers.Count > 0)
            {
                _context.Set<SqlOSUserPhoneNumber>().RemoveRange(phoneNumbers);
            }

            _context.Set<SqlOSUser>().Remove(user);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private SqlOSPhoneOtpService RequirePhoneOtpService()
        => _phoneOtpService ?? throw new InvalidOperationException("Phone OTP service is not registered.");

    private SqlOSMagicLinkService RequireMagicLinkService()
        => _magicLinkService ?? throw new InvalidOperationException("Magic-link service is not registered.");

    private SqlOSTotpMfaService RequireTotpMfaService()
        => _totpMfaService ?? throw new InvalidOperationException("TOTP MFA service is not registered.");

    private async Task<SqlOSLoginResult?> TryCreateMfaLoginResultAsync(
        SqlOSUser user,
        SqlOSClientApplication client,
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

        var mfaToken = await CreateMfaChallengeAsync(
            user,
            client,
            organizationId,
            authenticationMethod,
            "client",
            evaluation.EnrollmentRequired,
            evaluation.EnrollmentRequired
                ? evaluation.AvailableFactors.Where(static factor =>
                    string.Equals(factor, SqlOSMfaFactorTypes.Totp, StringComparison.OrdinalIgnoreCase)).ToArray()
                : Array.Empty<string>(),
            cancellationToken: cancellationToken);

        return new SqlOSLoginResult(
            false,
            null,
            organizations,
            null,
            RequiresMfa: true,
            MfaToken: mfaToken,
            RequiresMfaEnrollment: evaluation.EnrollmentRequired,
            MfaMethods: evaluation.AvailableFactors);
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

            var mfaResult = await TryCreateMfaLoginResultAsync(
                user,
                client,
                requestedOrganizationId,
                authenticationMethod,
                organizations,
                cancellationToken);
            if (mfaResult != null)
            {
                return mfaResult;
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
        var directMfaResult = await TryCreateMfaLoginResultAsync(
            user,
            client,
            organizationId,
            authenticationMethod,
            organizations,
            cancellationToken);
        if (directMfaResult != null)
        {
            return directMfaResult;
        }

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
        var revokedAt = DateTime.UtcNow;

        if (SupportsDatabaseTransactions())
        {
            // Replay revocation must win against a concurrent rotation on a
            // different app instance. Revoking the session first takes the
            // lifecycle lock observed by RefreshAsync's concurrency token;
            // the family update then covers every descendant visible in the
            // same transaction, including one committed just before it.
            IDbContextTransaction? transaction = null;
            try
            {
                if (_context.Database.CurrentTransaction == null)
                {
                    transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
                }

                await _context.Set<SqlOSSession>()
                    .Where(x => x.Id == sessionId && x.RevokedAt == null)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.RevokedAt, revokedAt)
                        .SetProperty(x => x.RevocationReason, reason), cancellationToken);

                await _context.Set<SqlOSRefreshToken>()
                    .Where(x => x.SessionId == sessionId && x.FamilyId == familyId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.RevokedAt, x => x.RevokedAt ?? revokedAt)
                        .SetProperty(x => x.ReplacementTokenResponse, (string?)null)
                        .SetProperty(x => x.ReplacementOrganizationId, (string?)null)
                        .SetProperty(x => x.ReplacementAccessTokenExpiresAt, (DateTime?)null), cancellationToken);

                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
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

            // ExecuteUpdate intentionally bypasses tracked state. Nothing in
            // this failed grant may subsequently flush a stale active token
            // or cached response back to the database.
            if (_context is DbContext dbContext)
            {
                dbContext.ChangeTracker.Clear();
            }

            return;
        }

        // The in-memory provider used by unit tests has no transactions or
        // server-side ExecuteUpdate support, so retain an equivalent tracked
        // implementation for that provider.
        var session = await _context.Set<SqlOSSession>().FirstOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
        if (session != null && session.RevokedAt == null)
        {
            session.RevokedAt = revokedAt;
            session.RevocationReason = reason;
        }

        var refreshTokens = await _context.Set<SqlOSRefreshToken>()
            .Where(x => x.SessionId == sessionId && x.FamilyId == familyId)
            .ToListAsync(cancellationToken);

        foreach (var token in refreshTokens)
        {
            token.RevokedAt ??= revokedAt;
            token.ReplacementTokenResponse = null;
            token.ReplacementOrganizationId = null;
            token.ReplacementAccessTokenExpiresAt = null;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private static void EnsureSessionIsActive(SqlOSSession session)
    {
        var now = DateTime.UtcNow;
        if (session.RevokedAt != null || session.AbsoluteExpiresAt <= now || session.IdleExpiresAt <= now)
        {
            throw new InvalidOperationException("Session is no longer active.");
        }
    }

    private sealed record PendingAuthPayload(string ClientId, string AuthenticationMethod);
    private sealed record AuthCodePayload(string ClientId, string RedirectUri, string AuthenticationMethod);
    private sealed record RefreshTokenReplacementPayload(string AccessToken, string RefreshToken);
    private sealed record PasswordResetPayload(string EmailId, string NormalizedEmail);
    private sealed record PasswordResetRequestPayload(
        string NormalizedEmail,
        string? IpAddress,
        string? ClientKey,
        string Surface);
    private sealed record PasswordResetRateLimitResult(bool IsLimited, string? Scope, DateTime? RetryAfter)
    {
        public static PasswordResetRateLimitResult Allowed() => new(false, null, null);
        public static PasswordResetRateLimitResult Limited(string scope, DateTime retryAfter) => new(true, scope, retryAfter);
    }
    private sealed record EmailVerificationPayload(string EmailId);

    private SqlOSInvitationService RequireInvitationService()
        => _invitationService ?? throw new InvalidOperationException("SqlOS invitations are not configured.");

    private SqlOSDeviceAuthorizationService CreateDeviceAuthorizationService()
        => new(_context, _adminService, this, _cryptoService, Options.Create(_options));
}

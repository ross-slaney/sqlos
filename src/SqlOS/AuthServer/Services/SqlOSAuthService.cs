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
using SqlOS.AuthServer.Policies;
using SqlOS.AuthServer.Processes.Identity;
using SqlOS.Domain;
using SqlOS.Domain.Events;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;
using SqlOS.Email.Models;
using SqlOS.Email.Services;
using SqlOS.Hosting;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSAuthService
{
    public const string MfaChallengePurpose = SqlOSTemporaryTokenKinds.Purposes.MfaChallenge;
    internal const string MfaChallengeFailureMessage = "MFA code is invalid.";
    private const string MfaChallengeFailedAuditEvent = "user.mfa.challenge_failed";

    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAuthServerOptions _options;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly SqlOSEmailOtpService _emailOtpService;
    private readonly SqlOSMagicLinkService? _magicLinkService;
    private readonly SqlOSPhoneOtpService? _phoneOtpService;
    private readonly SqlOSMfaPolicyService _mfaPolicyService;
    private readonly SqlOSTotpMfaService? _totpMfaService;
    private readonly SqlOSInvitationService? _invitationService;
    private readonly IAdmissionGate _admission;

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
        SqlOSMagicLinkService? magicLinkService = null,
        SqlOSMfaAttemptAdmissionService? mfaAttemptAdmissionService = null,
        SqlOSDeliveryAdmissionService? deliveryAdmissionService = null)
    {
        _context = context;
        _options = options.Value;
        _adminService = adminService;
        _cryptoService = cryptoService;
        _settingsService = settingsService;
        _emailOtpService = emailOtpService;
        _magicLinkService = magicLinkService;
        _phoneOtpService = phoneOtpService;
        _invitationService = invitationService;
        _admission = SqlOSAdmissionGate.Create(
            context,
            adminService,
            cryptoService,
            options,
            deliveryAdmissionService ?? new SqlOSDeliveryAdmissionService(),
            mfaAttemptAdmissionService,
            passwordLoginAbuseService);
        _mfaPolicyService = mfaPolicyService ?? new SqlOSMfaPolicyService(context, settingsService, options);
        _totpMfaService = totpMfaService;
        PasswordResetEmails = new SqlOSPasswordResetDelivery(_options, settingsService, authEmailSender, transactionalEmailService);
        VerificationEmails = new SqlOSEmailVerificationDelivery(_options, settingsService, transactionalEmailService);
    }

    /// <summary>
    /// The admission gate of the flows this service owned in 7.2.1: password-reset emails, MFA factor
    /// comparisons and the public API's password sign-ins.
    /// </summary>
    internal IAdmissionGate Admission => _admission;

    /// <summary>The password-reset email channel the reset processes send through.</summary>
    internal SqlOSPasswordResetDelivery PasswordResetEmails { get; }

    /// <summary>The email-verification channel the verification process sends through.</summary>
    internal SqlOSEmailVerificationDelivery VerificationEmails { get; }

    /// <summary>
    /// The identity processes this facade delegates to. Its sign-ins complete as first-party direct
    /// logins (<see cref="FinalizeClientLoginAsync"/>).
    /// </summary>
    private SqlOSIdentityProcesses Processes => new(_context, _adminService, _cryptoService, _settingsService, _options)
    {
        PasswordAdmission = _admission,
        PasswordResetAdmission = _admission,
        PasswordResetEmails = PasswordResetEmails,
        VerificationEmails = VerificationEmails,
        Auth = this,
        Invitations = _invitationService,
        EmailCodes = _emailOtpService,
        SignInLinks = _magicLinkService,
        PhoneCodes = _phoneOtpService
    };

    public async Task<SqlOSLoginResult> SignUpAsync(SqlOSSignupRequest request, HttpContext httpContext, CancellationToken cancellationToken = default)
        => DirectLogin(await Processes.SignUpWithPassword(httpContext).ExecuteAsync(
            new SignUpWithPasswordCommand(
                request.DisplayName,
                request.Email,
                request.Password,
                request.OrganizationName,
                request.OrganizationId,
                Invitation: null,
                CustomFields: null,
                new LoginTarget.DirectLogin(request.ClientId),
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.PublicApi)),
            cancellationToken));

    public async Task<SqlOSLoginResult> LoginWithPasswordAsync(SqlOSPasswordLoginRequest request, HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        var outcome = await Processes.SignInWithPassword(httpContext).ExecuteAsync(
            new SignInWithPasswordCommand(
                request.Email,
                request.Password,
                new LoginTarget.DirectLogin(request.ClientId, request.OrganizationId),
                CarriesInvitation: false,
                new PasswordAttemptContext(request.ClientId, AuthorizationRequestId: null, "api"),
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.PublicApi)),
            cancellationToken);
        return outcome switch
        {
            SignInOutcome.SignedIn { Completion: LoginCompletion.DirectLoginCompleted completed } => completed.Result,
            SignInOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown sign-in outcome '{outcome.GetType().Name}'.")
        };
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
        => DirectLogin(await Processes.SignUpWithInvitation(httpContext).ExecuteAsync(
            new SignUpWithInvitationCommand(
                request.DisplayName,
                request.InvitationToken,
                Invitation: null,
                request.CustomFields,
                new LoginTarget.DirectLogin(request.ClientId),
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.PublicApi)),
            cancellationToken));

    public async Task<SqlOSLoginResult> VerifyEmailOtpAsync(
        SqlOSEmailOtpVerifyRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var target = new LoginTarget.DirectLogin();
        var outcome = await Processes.VerifyEmailOtpSignIn(httpContext).ExecuteAsync(
            new VerifyEmailOtpSignInCommand(request.ChallengeToken, request.Code, target, ChallengeBinding.For(target)),
            cancellationToken);
        return outcome switch
        {
            EmailCodeSignInOutcome.SignedIn { Completion: LoginCompletion.DirectLoginCompleted completed } => completed.Result,
            EmailCodeSignInOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown email-code sign-in outcome '{outcome.GetType().Name}'.")
        };
    }

    public async Task<SqlOSLoginResult> CompleteMagicLinkAsync(
        SqlOSMagicLinkCompleteRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var outcome = await Processes.CompleteMagicLinkSignIn(httpContext).ExecuteAsync(
            new CompleteMagicLinkSignInCommand(request.Token, SignInLinkTarget.DirectLogin.Instance),
            cancellationToken);
        return outcome switch
        {
            SignInLinkOutcome.SignedIn { Completion: LoginCompletion.DirectLoginCompleted completed } => completed.Result,
            SignInLinkOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown sign-in link outcome '{outcome.GetType().Name}'.")
        };
    }

    public async Task<SqlOSLoginResult> VerifyPhoneOtpAsync(
        SqlOSPhoneOtpVerifyRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var target = new LoginTarget.DirectLogin();
        var outcome = await Processes.VerifyPhoneOtpSignIn(httpContext).ExecuteAsync(
            new VerifyPhoneOtpSignInCommand(request.ChallengeToken, request.Code, target, ChallengeBinding.For(target)),
            cancellationToken);
        return outcome switch
        {
            PhoneCodeSignInOutcome.SignedIn { Completion: LoginCompletion.DirectLoginCompleted completed } => completed.Result,
            PhoneCodeSignInOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown phone-code sign-in outcome '{outcome.GetType().Name}'.")
        };
    }

    public async Task<SqlOSLoginResult> VerifyPhoneOtpSignupAsync(
        SqlOSPhoneOtpSignupVerifyRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
        => DirectLogin(await Processes.CompletePhoneOtpSignUp(httpContext).ExecuteAsync(
            new CompletePhoneOtpSignUpCommand(
                request.SignupToken,
                request.ChallengeToken,
                request.Code,
                CarriesInvitation: false,
                new LoginTarget.DirectLogin(),
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.PublicApi)),
            cancellationToken));

    public async Task<SqlOSLoginResult> VerifyEmailOtpSignupAsync(
        SqlOSEmailOtpSignupVerifyRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
        => DirectLogin(await Processes.CompleteEmailOtpSignUp(httpContext).ExecuteAsync(
            new CompleteEmailOtpSignUpCommand(
                request.SignupToken,
                request.ChallengeToken,
                request.Code,
                Invitation: null,
                new LoginTarget.DirectLogin(),
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.PublicApi)),
            cancellationToken));

    /// <summary>The direct login a sign-up completed, or its refusal as the 7.x exception.</summary>
    private static SqlOSLoginResult DirectLogin(SignUpOutcome outcome) => outcome switch
    {
        SignUpOutcome.SignedUp { Completion: LoginCompletion.DirectLoginCompleted completed } => completed.Result,
        SignUpOutcome.Refused refused => throw refused.Refusal.ToException(),
        _ => throw new InvalidOperationException($"Unknown sign-up outcome '{outcome.GetType().Name}'.")
    };

    public async Task<SqlOSLoginResult> CompleteExternalLoginAsync(
        SqlOSUser user,
        SqlOSClientApplication client,
        string authenticationMethod,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        // The organization pending token below is minted outside FinalizeClientLoginAsync.
        await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(_adminService, client, httpContext, user.Id, cancellationToken);
        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);

        if (organizations.Count > 1)
        {
            var pendingAuthToken = (await _cryptoService.CreateTemporaryTokenAsync(
                SqlOSTemporaryTokenKinds.PendingAuth,
                new PendingAuthPayload(client.ClientId, authenticationMethod),
                new TemporaryTokenBinding(UserId: user.Id, ClientApplicationId: client.Id),
                _options.TemporaryTokenLifetime,
                cancellationToken)).RawToken;

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
        var token = await _cryptoService.ConsumeTemporaryTokenAsync(SqlOSTemporaryTokenKinds.PendingAuth, request.PendingAuthToken, cancellationToken)
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
        var payload = token.ReadPayload(SqlOSTemporaryTokenKinds.PendingAuth);
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

    /// <summary>
    /// Rotates a public client's refresh token. A refresh token issued to a
    /// confidential client is rejected with <see cref="SqlOSClientAuthenticationException"/>
    /// before rotation; exchange it at the OAuth token endpoint with the client's
    /// registered authentication method.
    /// </summary>
    public async Task<SqlOSTokenResponse> RefreshAsync(SqlOSRefreshRequest request, CancellationToken cancellationToken = default)
        => (await RefreshWithSessionScopeAsync(request, clientAdmission: null, cancellationToken)).Tokens;

    /// <summary>
    /// Refreshes tokens and also returns the session's granted scope, captured while
    /// the session entity is already loaded. Callers that must echo the scope (RFC
    /// 6749 §5.1) use this instead of querying after <see cref="RefreshAsync"/>
    /// returns — a post-rotation query is a failure boundary that would burn the
    /// consumed refresh token without delivering a response.
    /// </summary>
    internal async Task<(SqlOSTokenResponse Tokens, string? SessionScope)> RefreshWithSessionScopeAsync(
        SqlOSRefreshRequest request,
        SqlOSRefreshClientAdmission? clientAdmission,
        CancellationToken cancellationToken = default)
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
        if (!string.IsNullOrWhiteSpace(request.ClientId)
            && !string.Equals(session.ClientApplication?.ClientId, request.ClientId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refresh token was not issued for this client.");
        }

        // Every refresh route converges here, so this is where a confidential
        // client's token requires proof of that client's credentials. It runs
        // before the grace-window branch and before any write: a rejected
        // attempt neither rotates the token nor reads its cached successor.
        if (!SqlOSClientAuthenticationService.IsAdmittedForRefresh(session.ClientApplication, clientAdmission))
        {
            await _adminService.RecordAuditAsync(
                "oauth.client_authentication.failed",
                "oauth_client",
                session.ClientApplication?.ClientId,
                sessionId: session.Id,
                data: new { clientId = session.ClientApplication?.ClientId, grantType = "refresh_token" },
                cancellationToken: cancellationToken);
            throw new SqlOSClientAuthenticationException();
        }

        if (refreshToken.RevokedAt != null || refreshToken.ExpiresAt <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("Refresh token is no longer valid.");
        }

        if (refreshToken.ConsumedAt != null)
        {
            return (await HandleConsumedRefreshTokenAsync(refreshToken, session, request, securitySettings, cancellationToken), session.Scope);
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
            return (await HandleConsumedRefreshTokenAsync(refreshToken, session, request, securitySettings, cancellationToken), session.Scope);
        }

        var requestedOrganizationId = string.IsNullOrWhiteSpace(request.OrganizationId)
            ? null
            : request.OrganizationId.Trim();
        var organizationId = requestedOrganizationId ?? session.OrganizationId;
        var issuanceDecision = await _mfaPolicyService.EvaluateForIssuanceAsync(
            session.UserId,
            organizationId,
            session.AuthenticationMethod ?? "password",
            authorizationRequestId: null,
            cancellationToken);
        if (!issuanceDecision.CanIssue)
        {
            throw new InvalidOperationException(SqlOSMfaPolicyService.UnsatisfiedPolicyMessage);
        }

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
        // OIDC refresh responses MAY include a fresh ID token; the nonce is only
        // ever bound to the original authorization request, never a refresh.
        var refreshIdToken = await MintIdTokenIfGrantedAsync(
            session.User!,
            session,
            session.ClientApplication!,
            organizationId,
            session.Scope,
            accessToken,
            nonce: null,
            cancellationToken);

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
                new RefreshTokenReplacementPayload(accessToken, newRawRefreshToken, refreshIdToken));
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

            return (await HandleConsumedRefreshTokenAsync(fresh, fresh.Session!, request, securitySettings, cancellationToken), fresh.Session!.Scope);
        }

        return (new SqlOSTokenResponse(
            accessToken,
            newRawRefreshToken,
            session.Id,
            session.ClientApplication!.ClientId,
            organizationId,
            accessTokenExpiresAt,
            nextRefreshToken.ExpiresAt,
            refreshIdToken), session.Scope);
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
                    replacement.ExpiresAt,
                    cachedResponse.IdToken);
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
            cancellationToken: cancellationToken);
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

    internal async Task<SqlOSSession?> FindActiveSessionByRefreshTokenAsync(
        string? refreshToken,
        CancellationToken cancellationToken,
        bool includeClientApplication = false)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var hashed = _cryptoService.HashToken(refreshToken);
        IQueryable<SqlOSRefreshToken> query = _context.Set<SqlOSRefreshToken>()
            .Include(x => x.Session);
        if (includeClientApplication)
        {
            query = query.Include(x => x.Session!.ClientApplication);
        }

        var token = await query
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

    /// <summary>
    /// Issues a password-reset token for the account that owns <see cref="SqlOSForgotPasswordRequest.Email"/>
    /// and returns it, for host code that delivers the link itself. The account must be resettable.
    /// </summary>
    public async Task<string> CreatePasswordResetTokenAsync(SqlOSForgotPasswordRequest request, CancellationToken cancellationToken = default)
    {
        var outcome = await Processes.IssuePasswordResetToken().ExecuteAsync(
            new IssuePasswordResetTokenCommand(request.Email, request.ClientId),
            cancellationToken);
        return outcome switch
        {
            PasswordResetTokenOutcome.Issued issued => issued.RawToken,
            PasswordResetTokenOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown password-reset token outcome '{outcome.GetType().Name}'.")
        };
    }

    public async Task<SqlOSPasswordResetRequestResult> RequestPasswordResetEmailAsync(
        SqlOSForgotPasswordRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var outcome = await Processes.RequestPasswordReset().ExecuteAsync(
            new RequestPasswordResetCommand(
                request.Email,
                request.ClientId,
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.PublicApi)),
            cancellationToken);
        return outcome switch
        {
            PasswordResetRequestOutcome.Answered answered => answered.Result,
            PasswordResetRequestOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown password-reset request outcome '{outcome.GetType().Name}'.")
        };
    }

    public Task<SqlOSPasswordResetEmailResult> SendPasswordResetEmailAsync(
        SqlOSSendPasswordResetEmailRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => SendResetEmailAsync(
            new SendPasswordResetEmailCommand(
                new PasswordResetRecipient.Address(request.Email),
                request.ResetUrlTemplate,
                request.ClientId,
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.PublicApi)),
            cancellationToken);

    public Task<SqlOSPasswordResetEmailResult> SendPasswordResetEmailForUserAsync(
        string userId,
        SqlOSSendUserPasswordResetEmailRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => SendResetEmailAsync(
            new SendPasswordResetEmailCommand(
                new PasswordResetRecipient.User(userId),
                request.ResetUrlTemplate,
                ClientId: null,
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.Admin)),
            cancellationToken);

    private async Task<SqlOSPasswordResetEmailResult> SendResetEmailAsync(
        SendPasswordResetEmailCommand command,
        CancellationToken cancellationToken)
    {
        var outcome = await Processes.SendPasswordResetEmail().ExecuteAsync(command, cancellationToken);
        return outcome switch
        {
            PasswordResetEmailOutcome.Sent sent => sent.Result,
            PasswordResetEmailOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown password-reset email outcome '{outcome.GetType().Name}'.")
        };
    }

    public async Task ResetPasswordAsync(SqlOSResetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        var outcome = await Processes.ResetPassword().ExecuteAsync(
            new ResetPasswordCommand(request.Token, request.NewPassword),
            cancellationToken);
        if (outcome is PasswordResetOutcome.Refused refused)
        {
            throw refused.Refusal.ToException();
        }
    }

    /// <summary>
    /// Issues an email-verification token for the address <see cref="SqlOSCreateVerificationTokenRequest.Email"/>
    /// and returns it, for host code that delivers the link itself.
    /// </summary>
    public async Task<string> CreateEmailVerificationTokenAsync(SqlOSCreateVerificationTokenRequest request, CancellationToken cancellationToken = default)
    {
        var outcome = await Processes.IssueEmailVerificationToken().ExecuteAsync(
            new IssueEmailVerificationTokenCommand(request.Email),
            cancellationToken);
        return outcome switch
        {
            EmailVerificationTokenOutcome.Issued issued => issued.RawToken,
            EmailVerificationTokenOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown email-verification token outcome '{outcome.GetType().Name}'.")
        };
    }

    public async Task<SqlOSEmailVerificationRequestResult> RequestEmailVerificationAsync(
        SqlOSCreateVerificationTokenRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var outcome = await Processes.RequestEmailVerification().ExecuteAsync(
            new RequestEmailVerificationCommand(
                request.Email,
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.PublicApi)),
            cancellationToken);
        return outcome switch
        {
            EmailVerificationRequestOutcome.Answered answered => answered.Result,
            EmailVerificationRequestOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown email-verification request outcome '{outcome.GetType().Name}'.")
        };
    }

    public async Task VerifyEmailAsync(SqlOSVerifyEmailRequest request, CancellationToken cancellationToken = default)
    {
        var outcome = await Processes.VerifyEmail().ExecuteAsync(new VerifyEmailCommand(request.Token), cancellationToken);
        if (outcome is EmailVerificationOutcome.Refused refused)
        {
            throw refused.Refusal.ToException();
        }
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
        => await StartTotpEnrollmentForChallengeAsync(mfaToken, request, httpContext: null, cancellationToken);

    internal async Task<SqlOSTotpEnrollmentStartResult> StartTotpEnrollmentForChallengeAsync(
        string mfaToken,
        SqlOSTotpEnrollmentStartRequest request,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
        => await StartTotpEnrollmentForChallengeCoreAsync(
            mfaToken,
            request,
            expectedFlow: "client",
            expectedAuthorizationRequestId: null,
            httpContext,
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
            httpContext: null,
            cancellationToken);

    private async Task<SqlOSTotpEnrollmentStartResult> StartTotpEnrollmentForChallengeCoreAsync(
        string mfaToken,
        SqlOSTotpEnrollmentStartRequest request,
        string expectedFlow,
        string? expectedAuthorizationRequestId,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        var token = await RequireTotpMfaService().GetPendingMfaTokenAsync(mfaToken, cancellationToken);
        await EnsureClientFlowChallengeIsFirstPartyAsync(token, httpContext, cancellationToken);
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

        // Outside the enrollment transaction, so a refused client's audit event is not rolled back.
        await EnsureClientFlowChallengeIsFirstPartyAsync(
            await _cryptoService.FindTemporaryTokenAsync(SqlOSTemporaryTokenKinds.MfaChallenge, request.MfaToken, cancellationToken),
            httpContext,
            cancellationToken);

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

        var payload = token.ReadPayload(SqlOSTemporaryTokenKinds.MfaChallenge)
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
        var token = await _cryptoService.FindTemporaryTokenAsync(SqlOSTemporaryTokenKinds.MfaChallenge, request.MfaToken, cancellationToken)
            ?? throw new InvalidOperationException("MFA challenge is invalid or expired.");
        if (token.UserId == null || token.ClientApplicationId == null)
        {
            throw new InvalidOperationException("MFA challenge payload is invalid.");
        }

        var payload = token.ReadPayload(SqlOSTemporaryTokenKinds.MfaChallenge)
            ?? throw new InvalidOperationException("MFA challenge payload is invalid.");
        if (!string.Equals(payload.Flow, "client", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("MFA challenge is not valid for direct authentication.");
        }

        if (payload.EnrollmentRequired)
        {
            throw new InvalidOperationException("MFA enrollment must be completed with its challenge-bound enrollment proof.");
        }

        await EnsureClientFlowChallengeIsFirstPartyAsync(token, httpContext, cancellationToken);
        var factorMethod = await VerifyMfaChallengeFactorAsync(token, request.Code, httpContext, cancellationToken);
        token.Consume(SqlOSTemporaryTokenKinds.MfaChallenge, DateTime.UtcNow);
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

        var payload = token.ReadPayload(SqlOSTemporaryTokenKinds.MfaChallenge)
            ?? throw new InvalidOperationException("MFA challenge payload is invalid.");
        var reservationId = await _admission.AdmitMfaAttemptAsync(
            token,
            AdmissionOrigin.Of(httpContext),
            payload.AuthorizationRequestId,
            cancellationToken);
        try
        {
            var factorMethod = await RequireTotpMfaService().VerifySecondFactorCodeAsync(token.UserId, code, cancellationToken);
            await _admission.RecordMfaAttemptSucceededAsync(reservationId, CancellationToken.None);
            return factorMethod;
        }
        catch (InvalidOperationException)
        {
            await _admission.RecordMfaAttemptFailedAsync(reservationId, CancellationToken.None);
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
        CancellationToken cancellationToken = default,
        bool credentialSignIn = false)
    {
        if (await _admission.IsMfaBudgetExhaustedAsync(user.Id, cancellationToken))
        {
            await TryRecordMfaChallengeAuditAsync(
                "user.mfa.challenge_issue_rejected",
                user.Id,
                organizationId,
                null,
                new { reason = "user_budget_exhausted" },
                cancellationToken);
            throw new InvalidOperationException(MfaChallengeFailureMessage);
        }

        var challenge = await _cryptoService.CreateTemporaryTokenAsync(
            SqlOSTemporaryTokenKinds.MfaChallenge,
            new SqlOSMfaChallengePayload(
                flow,
                client.ClientId,
                authenticationMethod,
                authorizationRequestId,
                resource,
                enrollmentRequired,
                permittedEnrollmentFactors,
                CredentialSignIn: credentialSignIn),
            new TemporaryTokenBinding(UserId: user.Id, ClientApplicationId: client.Id, OrganizationId: organizationId),
            _options.Mfa.Totp.ChallengeTokenLifetime,
            cancellationToken);
        return challenge.RawToken;
    }

    private async Task<int> RecordMfaChallengeFailureAsync(
        SqlOSTemporaryToken token,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var payload = token.ReadPayload(SqlOSTemporaryTokenKinds.MfaChallenge)
                ?? throw new InvalidOperationException("MFA challenge payload is invalid.");
            if (token.ConsumedAt != null)
            {
                return payload.FailedAttempts;
            }

            var attemptCount = payload.FailedAttempts + 1;
            token.ReplacePayload(SqlOSTemporaryTokenKinds.MfaChallenge, payload with { FailedAttempts = attemptCount });
            if (attemptCount >= _options.Mfa.Totp.MaxFailedAttemptsPerChallenge)
            {
                // The last allowed failure locks the challenge: it is withdrawn, never completed.
                token.Retire(DateTime.UtcNow);
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

    internal async Task<SqlOSAuthorizationMfaChallengeState> GetAuthorizationMfaChallengeStateAsync(
        string mfaToken,
        string authorizationRequestId,
        CancellationToken cancellationToken = default)
    {
        var token = await RequireTotpMfaService().GetPendingMfaTokenAsync(mfaToken, cancellationToken);
        if (token.UserId == null)
        {
            throw new InvalidOperationException("MFA challenge is invalid or expired.");
        }

        var payload = token.ReadPayload(SqlOSTemporaryTokenKinds.MfaChallenge);
        if (payload == null
            || !string.Equals(payload.Flow, "authorization", StringComparison.Ordinal)
            || !string.Equals(payload.AuthorizationRequestId, authorizationRequestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("MFA challenge is invalid or expired.");
        }

        var evaluation = await RequireMfaPolicyService().EvaluateAsync(
            token.UserId,
            token.OrganizationId,
            payload.AuthenticationMethod,
            cancellationToken);
        if (!evaluation.RequiresMfa || evaluation.EnrollmentRequired != payload.EnrollmentRequired)
        {
            throw new InvalidOperationException("MFA challenge is invalid or expired.");
        }

        return new SqlOSAuthorizationMfaChallengeState(
            payload.EnrollmentRequired,
            evaluation.AvailableFactors);
    }

    /// <summary>
    /// A client-flow MFA challenge finishes as direct login, so it is first-party only. A third-party
    /// client can hold one only if it was issued before the direct-login gate; refuse it before the
    /// factor is checked or any enrollment state is written. Authorization-flow challenges pass.
    /// </summary>
    private async Task EnsureClientFlowChallengeIsFirstPartyAsync(
        SqlOSTemporaryToken? token,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        if (token != null
            && string.Equals(
                token.ReadPayload(SqlOSTemporaryTokenKinds.MfaChallenge)?.Flow,
                "client",
                StringComparison.Ordinal))
        {
            await EnsureArtifactClientIsFirstPartyAsync(token, httpContext, cancellationToken);
        }
    }

    /// <summary>
    /// Refuses the third-party client bound to a direct-login artifact (an MFA challenge issued
    /// before the direct-login gate) before the caller writes anything or opens a transaction, so
    /// the refusal's audit event is never rolled back.
    /// </summary>
    private async Task EnsureArtifactClientIsFirstPartyAsync(
        SqlOSTemporaryToken? token,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        if (token?.ClientApplicationId == null)
        {
            return;
        }

        var client = await _context.Set<SqlOSClientApplication>()
            .FirstOrDefaultAsync(x => x.Id == token.ClientApplicationId, cancellationToken);
        if (client != null)
        {
            await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(_adminService, client, httpContext, token.UserId, cancellationToken);
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

        var payload = token.ReadPayload(SqlOSTemporaryTokenKinds.MfaChallenge)
            ?? throw new InvalidOperationException("MFA challenge payload is invalid.");
        if (!string.Equals(payload.Flow, "client", StringComparison.Ordinal))
        {
            return new SqlOSMfaChallengeVerifyResult(null, null);
        }

        var user = await _context.Set<SqlOSUser>().FirstAsync(x => x.Id == token.UserId, cancellationToken);
        var client = await _context.Set<SqlOSClientApplication>().FirstAsync(x => x.Id == token.ClientApplicationId, cancellationToken);
        // The client flow is direct login: it mints tokens here, outside FinalizeClientLoginAsync.
        await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(_adminService, client, httpContext, user.Id, cancellationToken);
        var authenticationMethod = SqlOSMfaPolicyService.AddAuthenticationMethod(payload.AuthenticationMethod, factorMethod);
        var tokens = await CreateSessionAndTokensAsync(
            user,
            client,
            token.OrganizationId,
            authenticationMethod,
            httpContext?.Request.Headers.UserAgent.ToString(),
            GetIp(httpContext),
            payload.Resource,
            scope: null,
            nonce: null,
            authenticatedAt: null,
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

    public Task<SqlOSLoginResult> CompleteClientAuthenticationAsync(
        SqlOSUser user,
        SqlOSClientApplication client,
        string? organizationId,
        string authenticationMethod,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
        => FinalizeClientLoginAsync(user, client, organizationId, authenticationMethod, httpContext, cancellationToken);

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
            scope: null,
            nonce: null,
            authenticatedAt: null,
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
        => await CreateSessionTokensForUserAsync(
            user,
            client,
            organizationId,
            authenticationMethod,
            userAgent,
            ipAddress,
            resource,
            scope: null,
            nonce: null,
            authenticatedAt: null,
            cancellationToken);

    /// <summary>
    /// Creates a session and token pair for an OAuth grant, persisting the granted
    /// scope and the moment of actual user authentication on the session. The nonce
    /// is accepted alongside them so OIDC token issuance can bind it once OpenID
    /// Provider mode ships; it is not stored on the session.
    /// </summary>
    public async Task<SqlOSTokenResponse> CreateSessionTokensForUserAsync(
        SqlOSUser user,
        SqlOSClientApplication client,
        string? organizationId,
        string authenticationMethod,
        string? userAgent,
        string? ipAddress,
        string? resource,
        string? scope,
        string? nonce,
        DateTime? authenticatedAt,
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
            scope,
            nonce,
            authenticatedAt,
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
            scope: null,
            nonce: null,
            authenticatedAt: null,
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
        string? scope,
        string? nonce,
        DateTime? authenticatedAt,
        SqlOSResolvedSecuritySettings securitySettings,
        CancellationToken cancellationToken)
    {
        organizationId = string.IsNullOrWhiteSpace(organizationId) ? null : organizationId.Trim();
        var issuanceDecision = await _mfaPolicyService.EvaluateForIssuanceAsync(
            user.Id,
            organizationId,
            authenticationMethod,
            authorizationRequestId: null,
            cancellationToken);
        if (!issuanceDecision.CanIssue)
        {
            throw new InvalidOperationException(SqlOSMfaPolicyService.UnsatisfiedPolicyMessage);
        }

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
            Scope = scope,
            AuthenticatedAt = authenticatedAt,
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
        var idToken = await MintIdTokenIfGrantedAsync(
            user,
            session,
            client,
            organizationId,
            scope,
            accessToken,
            nonce,
            cancellationToken);
        return new SqlOSTokenResponse(
            accessToken,
            rawRefreshToken,
            session.Id,
            client.ClientId,
            organizationId,
            DateTime.UtcNow.Add(_options.AccessTokenLifetime),
            refreshToken.ExpiresAt,
            idToken);
    }

    /// <summary>
    /// Mints an ID token when OpenID Provider mode is enabled and the granted scope
    /// includes <c>openid</c>. Returns null otherwise — client-credentials and
    /// OAuth-only grants never receive one.
    /// </summary>
    private async Task<string?> MintIdTokenIfGrantedAsync(
        SqlOSUser user,
        SqlOSSession session,
        SqlOSClientApplication client,
        string? organizationId,
        string? scope,
        string accessToken,
        string? nonce,
        CancellationToken cancellationToken)
    {
        if (!_options.OpenIdProvider.Enabled)
        {
            return null;
        }

        var grantedScopes = SqlOSScopePolicy.Split(scope);
        if (!grantedScopes.Contains(SqlOSOpenIdScopeWarnings.OpenIdScope, StringComparer.Ordinal))
        {
            return null;
        }

        return await _cryptoService.CreateIdTokenAsync(
            user,
            session,
            client,
            organizationId,
            grantedScopes,
            accessToken,
            nonce,
            cancellationToken);
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



    private static string? GetIp(HttpContext? httpContext) => httpContext?.Connection.RemoteIpAddress?.ToString();

    private static string ResolveEffectiveAudience(SqlOSClientApplication client, string? resource)
        => string.IsNullOrWhiteSpace(resource)
            ? client.Audience
            : resource.Trim();

    private bool SupportsDatabaseTransactions()
        => !string.Equals(_context.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal);

    private SqlOSPhoneOtpService RequirePhoneOtpService()
        => _phoneOtpService ?? throw new InvalidOperationException("Phone OTP service is not registered.");

    private SqlOSMagicLinkService RequireMagicLinkService()
        => _magicLinkService ?? throw new InvalidOperationException("Magic-link service is not registered.");

    private SqlOSTotpMfaService RequireTotpMfaService()
        => _totpMfaService ?? throw new InvalidOperationException("TOTP MFA service is not registered.");

    private SqlOSMfaPolicyService RequireMfaPolicyService()
        => _mfaPolicyService;

    private async Task<SqlOSLoginResult?> TryCreateMfaLoginResultAsync(
        SqlOSUser user,
        SqlOSClientApplication client,
        string? organizationId,
        string authenticationMethod,
        IReadOnlyList<SqlOSOrganizationOption> organizations,
        CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// Completes a first-party client's direct login: an MFA challenge, an organization choice, or
    /// a session and tokens with the <c>user.login.*</c> audit. The hub adapter's direct-login
    /// completion (<see cref="SqlOSHttpLoginCompletion"/>).
    /// </summary>
    internal async Task<SqlOSLoginResult> FinalizeClientLoginAsync(
        SqlOSUser user,
        SqlOSClientApplication client,
        string? requestedOrganizationId,
        string authenticationMethod,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Every direct-login completion converges here. It returns tokens to the caller with no
        // consent screen, so it is first-party only; checked before any MFA challenge, organization
        // pending token, or session is minted.
        await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(_adminService, client, httpContext, user.Id, cancellationToken);
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
            var pendingAuthToken = (await _cryptoService.CreateTemporaryTokenAsync(
                SqlOSTemporaryTokenKinds.PendingAuth,
                new PendingAuthPayload(client.ClientId, authenticationMethod),
                new TemporaryTokenBinding(UserId: user.Id, ClientApplicationId: client.Id),
                _options.TemporaryTokenLifetime,
                cancellationToken)).RawToken;

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

    // IdToken defaults so replacement payloads cached before OP mode deserialize cleanly.
    private sealed record RefreshTokenReplacementPayload(string AccessToken, string RefreshToken, string? IdToken = null);

    private SqlOSInvitationService RequireInvitationService()
        => _invitationService ?? throw new InvalidOperationException("SqlOS invitations are not configured.");

    private SqlOSDeviceAuthorizationService CreateDeviceAuthorizationService()
        => new(_context, _adminService, this, _cryptoService, Options.Create(_options), _mfaPolicyService);
}

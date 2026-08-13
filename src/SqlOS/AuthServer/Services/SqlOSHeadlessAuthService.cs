using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Errors;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSHeadlessAuthService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSAuthService? _authService;
    private readonly SqlOSAuthorizationServerService _authorizationServerService;
    private readonly SqlOSHomeRealmDiscoveryService _discoveryService;
    private readonly SqlOSOidcBrowserAuthService _oidcBrowserAuthService;
    private readonly SqlOSSamlService _samlService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly SqlOSEmailOtpService _emailOtpService;
    private readonly SqlOSMagicLinkService? _magicLinkService;
    private readonly SqlOSPhoneOtpService? _phoneOtpService;
    private readonly SqlOSInvitationService? _invitationService;
    private readonly SqlOSDeviceAuthorizationService? _deviceAuthorizationService;
    private readonly SqlOSAuthPageSessionService? _authPageSessionService;
    private readonly SqlOSAuthServerOptions _options;

    public SqlOSHeadlessAuthService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSAuthorizationServerService authorizationServerService,
        SqlOSHomeRealmDiscoveryService discoveryService,
        SqlOSOidcBrowserAuthService oidcBrowserAuthService,
        SqlOSSamlService samlService,
        SqlOSSettingsService settingsService,
        SqlOSEmailOtpService emailOtpService,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSInvitationService? invitationService = null,
        SqlOSDeviceAuthorizationService? deviceAuthorizationService = null,
        SqlOSAuthPageSessionService? authPageSessionService = null,
        SqlOSPhoneOtpService? phoneOtpService = null,
        SqlOSAuthService? authService = null,
        SqlOSMagicLinkService? magicLinkService = null)
    {
        _context = context;
        _adminService = adminService;
        _authService = authService;
        _authorizationServerService = authorizationServerService;
        _discoveryService = discoveryService;
        _oidcBrowserAuthService = oidcBrowserAuthService;
        _samlService = samlService;
        _settingsService = settingsService;
        _emailOtpService = emailOtpService;
        _magicLinkService = magicLinkService;
        _phoneOtpService = phoneOtpService;
        _invitationService = invitationService;
        _deviceAuthorizationService = deviceAuthorizationService;
        _authPageSessionService = authPageSessionService;
        _options = options.Value;
    }

    public bool IsApiEnabled => _options.Headless.EnableApi;
    public bool IsBrowserUiEnabled => _options.Headless.BuildUiUrl != null;
    public bool IsEnabled => IsBrowserUiEnabled;

    public string GetHeadlessApiBasePath() => _options.Headless.ResolveApiBasePath(_options.BasePath);

    public string BuildStandaloneUiUrl(
        HttpContext httpContext,
        string view,
        string? requestId = null,
        string? email = null,
        JsonObject? uiContext = null)
        => BuildUiUrl(
            httpContext,
            requestId,
            view,
            error: null,
            pendingToken: null,
            email: email,
            displayName: null,
            uiContext: uiContext);

    public string BuildUiUrl(
        HttpContext httpContext,
        string? requestId,
        string view,
        string? error,
        string? pendingToken,
        string? email,
        string? displayName,
        JsonObject? uiContext,
        string? mfaToken = null)
    {
        if (!IsBrowserUiEnabled)
        {
            throw new InvalidOperationException("Headless browser handoff is not enabled.");
        }

        if (_options.Headless.BuildUiUrl == null)
        {
            throw new InvalidOperationException("Headless auth mode requires BuildUiUrl to be configured.");
        }

        return _options.Headless.BuildUiUrl(
            new SqlOSHeadlessUiRouteContext(
                httpContext,
                requestId,
                NormalizeView(view),
                error,
                pendingToken,
                email,
                displayName,
                uiContext,
                mfaToken));
    }

    public async Task<string?> TryBuildUiUrlForAuthorizationRequestAsync(
        HttpContext httpContext,
        string authorizationRequestId,
        string view,
        string? error,
        string? pendingToken,
        string? email,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.TryGetActiveAuthorizationRequestAsync(authorizationRequestId, cancellationToken);
        if (authorizationRequest == null || !IsHeadlessRequest(authorizationRequest) || !IsBrowserUiEnabled)
        {
            return null;
        }

        return BuildUiUrl(
            httpContext,
            authorizationRequest.Id,
            view,
            error,
            pendingToken,
            email ?? authorizationRequest.LoginHintEmail,
            displayName,
            ParseUiContext(authorizationRequest.UiContextJson));
    }

    public async Task<SqlOSHeadlessViewModel> GetRequestAsync(
        string requestId,
        string? requestedView,
        string? error,
        string? pendingToken,
        string? email,
        string? displayName,
        CancellationToken cancellationToken = default,
        string? mfaToken = null)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(requestId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(mfaToken))
        {
            var state = await RequireAuthService().GetAuthorizationMfaChallengeStateAsync(
                mfaToken,
                authorizationRequest.Id,
                cancellationToken);
            SqlOSTotpEnrollmentStartResult? enrollment = null;
            if (state.EnrollmentRequired)
            {
                enrollment = await RequireAuthService().StartTotpEnrollmentForAuthorizationChallengeAsync(
                    mfaToken,
                    authorizationRequest.Id,
                    new SqlOSTotpEnrollmentStartRequest(),
                    cancellationToken);
            }

            return await BuildViewModelAsync(
                authorizationRequest,
                state.EnrollmentRequired ? "mfa-enroll" : "mfa",
                error,
                pendingToken: null,
                email,
                displayName,
                fieldErrors: null,
                organizationSelection: null,
                mfaToken: mfaToken,
                requiresMfaEnrollment: state.EnrollmentRequired,
                mfaMethods: state.Methods,
                totpEnrollment: enrollment,
                cancellationToken: cancellationToken);
        }

        return await BuildViewModelAsync(
            authorizationRequest,
            requestedView,
            error,
            pendingToken,
            email,
            displayName,
            fieldErrors: null,
            organizationSelection: null,
            cancellationToken: cancellationToken);
    }

    public async Task<SqlOSHeadlessViewModel> ResolveInvitationAsync(
        HttpContext httpContext,
        SqlOSHeadlessInvitationResolveRequest request,
        CancellationToken cancellationToken = default)
    {
        var invitation = await RequireInvitationService().ResolveEmailInvitationAsync(request.InvitationToken, httpContext, cancellationToken);
        var settings = await _settingsService.GetAuthPageSettingsAsync(cancellationToken);
        var providers = (await _authorizationServerService.ListEnabledOidcProvidersAsync(cancellationToken))
            .Select(provider => new SqlOSHeadlessProviderDto(
                provider.ConnectionId,
                provider.ProviderType,
                provider.DisplayName,
                provider.LogoDataUrl))
            .ToArray();
        var uiContext = new JsonObject
        {
            ["invitationToken"] = request.InvitationToken
        };

        return new SqlOSHeadlessViewModel(
            "invite",
            _options.BasePath.TrimEnd('/'),
            GetHeadlessApiBasePath(),
            settings,
            RequestId: null,
            ClientId: null,
            ClientName: null,
            Email: invitation.Email,
            DisplayName: null,
            Error: null,
            Info: null,
            FieldErrors: new Dictionary<string, string>(StringComparer.Ordinal),
            ChallengeToken: null,
            SignupToken: null,
            PendingToken: null,
            OrganizationSelection: Array.Empty<SqlOSOrganizationOption>(),
            Providers: providers,
            Invitation: invitation,
            UiContext: uiContext);
    }

    public async Task<SqlOSHeadlessViewModel> ResolveDeviceAuthorizationAsync(
        SqlOSHeadlessDeviceAuthorizationResolveRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(request.RequestId))
        {
            var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
            var resolvedRequest = await RequireDeviceAuthorizationService().ResolveAsync(authorizationRequest, user: null, cancellationToken);
            return await BuildViewModelAsync(
                authorizationRequest,
                "device",
                error: null,
                pendingToken: null,
                email: null,
                displayName: null,
                fieldErrors: null,
                organizationSelection: resolvedRequest.Organizations,
                info: null,
                cancellationToken: cancellationToken);
        }

        var userCode = RequireDeviceUserCode(request.UserCode);
        var resolved = await RequireDeviceAuthorizationService().ResolveAsync(userCode, user: null, cancellationToken);
        return await BuildStandaloneDeviceViewModelAsync(
            "device",
            resolved,
            error: null,
            info: null,
            organizationSelection: resolved.Organizations,
            cancellationToken);
    }

    public async Task<SqlOSHeadlessActionResult> ApproveDeviceAuthorizationAsync(
        HttpContext httpContext,
        SqlOSHeadlessDeviceAuthorizationApproveRequest request,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireAuthPageSessionService().TryGetSessionAsync(httpContext, cancellationToken)
            ?? throw new InvalidOperationException("Sign in before approving this device request.");

        if (!string.IsNullOrWhiteSpace(request.RequestId))
        {
            var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(request.OrganizationId))
            {
                authorizationRequest.ResolvedOrganizationId = request.OrganizationId;
            }

            var requestResolved = await RequireDeviceAuthorizationService().ApproveAsync(
                authorizationRequest,
                session.User,
                session.AuthenticationMethod,
                httpContext,
                cancellationToken);

            if (requestResolved.RequiresOrganizationSelection)
            {
                return View(await BuildViewModelAsync(
                    authorizationRequest,
                    "device-approve",
                    error: null,
                    pendingToken: null,
                    email: session.User.DefaultEmail,
                    displayName: null,
                    fieldErrors: null,
                    organizationSelection: requestResolved.Organizations,
                    cancellationToken: cancellationToken));
            }

            return View(await BuildViewModelAsync(
                authorizationRequest,
                "device-approved",
                error: null,
                pendingToken: null,
                email: session.User.DefaultEmail,
                displayName: null,
                fieldErrors: null,
                organizationSelection: requestResolved.Organizations,
                info: "Your CLI is signed in. You can return to your terminal.",
                cancellationToken: cancellationToken));
        }

        var userCode = RequireDeviceUserCode(request.UserCode);
        var resolved = await RequireDeviceAuthorizationService().ApproveAsync(
            new SqlOSDeviceAuthorizationApprovalRequest(userCode, request.OrganizationId),
            session.User,
            session.AuthenticationMethod,
            httpContext,
            cancellationToken);

        if (resolved.RequiresOrganizationSelection)
        {
            return View(await BuildStandaloneDeviceViewModelAsync(
                "device-approve",
                resolved,
                error: null,
                info: null,
                organizationSelection: resolved.Organizations,
                cancellationToken));
        }

        return View(await BuildStandaloneDeviceViewModelAsync(
            "device-approved",
            resolved,
            error: null,
            info: "Your CLI is signed in. You can return to your terminal.",
            organizationSelection: resolved.Organizations,
            cancellationToken));
    }

    public async Task<SqlOSHeadlessActionResult> DenyDeviceAuthorizationAsync(
        HttpContext httpContext,
        SqlOSHeadlessDeviceAuthorizationResolveRequest request,
        CancellationToken cancellationToken = default)
    {
        var session = await RequireAuthPageSessionService().TryGetSessionAsync(httpContext, cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.RequestId))
        {
            var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
            var requestResolved = await RequireDeviceAuthorizationService().ResolveAsync(authorizationRequest, session?.User, cancellationToken);
            authorizationRequest.CancelledAt = DateTime.UtcNow;
            await RequireDeviceAuthorizationService().DenyAsync(requestResolved.UserCode, session?.User, httpContext, cancellationToken);
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "device-denied",
                error: null,
                pendingToken: null,
                email: session?.User.DefaultEmail,
                displayName: null,
                fieldErrors: null,
                organizationSelection: Array.Empty<SqlOSOrganizationOption>(),
                info: "CLI access was denied.",
                cancellationToken: cancellationToken));
        }

        var userCode = RequireDeviceUserCode(request.UserCode);
        await RequireDeviceAuthorizationService().DenyAsync(userCode, session?.User, httpContext, cancellationToken);
        var resolved = await RequireDeviceAuthorizationService().ResolveAsync(userCode, session?.User, cancellationToken);
        return View(await BuildStandaloneDeviceViewModelAsync(
            "device-denied",
            resolved,
            error: null,
            info: "CLI access was denied.",
            organizationSelection: Array.Empty<SqlOSOrganizationOption>(),
            cancellationToken));
    }

    public async Task<SqlOSHeadlessActionResult> IdentifyAsync(
        SqlOSHeadlessIdentifyRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        await BindInvitationIfPresentAsync(authorizationRequest, request.InvitationToken, cancellationToken);
        var email = await ResolveEffectiveEmailAsync(authorizationRequest, request.Email, cancellationToken);
        var discovery = await _discoveryService.DiscoverAsync(new SqlOSHomeRealmDiscoveryRequest(email), cancellationToken);

        authorizationRequest.LoginHintEmail = email;
        if (!string.IsNullOrWhiteSpace(discovery.OrganizationId))
        {
            authorizationRequest.OrganizationId = discovery.OrganizationId;
            authorizationRequest.ResolvedOrganizationId = discovery.OrganizationId;
        }

        if (!string.IsNullOrWhiteSpace(discovery.ConnectionId))
        {
            authorizationRequest.ConnectionId = discovery.ConnectionId;
            authorizationRequest.ResolvedConnectionId = discovery.ConnectionId;
        }

        await _context.SaveChangesAsync(cancellationToken);

        if (string.Equals(discovery.Mode, "sso", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(discovery.ConnectionId))
        {
            return Redirect(await _samlService.BuildIdentityProviderRedirectForAuthorizationRequestAsync(authorizationRequest.Id, cancellationToken));
        }

        var credentialSettings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);

        return View(await BuildViewModelAsync(
            authorizationRequest,
            ResolvePreferredLocalView(credentialSettings),
            error: null,
            pendingToken: null,
            email: email,
            displayName: null,
            fieldErrors: null,
            organizationSelection: null,
            info: null,
            challengeToken: null,
            cancellationToken: cancellationToken));
    }

    public async Task<SqlOSHeadlessActionResult> PasswordLoginAsync(
        HttpContext httpContext,
        SqlOSHeadlessPasswordLoginRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        await BindInvitationIfPresentAsync(authorizationRequest, request.InvitationToken, cancellationToken);
        var email = await ResolveEffectiveEmailAsync(authorizationRequest, request.Email, cancellationToken);
        var ssoRedirect = await RedirectToSsoIfRequiredAsync(authorizationRequest, email, cancellationToken);
        if (ssoRedirect != null)
        {
            return ssoRedirect;
        }

        try
        {
            var authentication = await _authorizationServerService.AuthenticatePasswordAsync(
                email,
                request.Password,
                cancellationToken,
                allowUnverifiedEmailForInvitation: !string.IsNullOrWhiteSpace(authorizationRequest.InvitationId),
                httpContext: httpContext,
                clientKey: authorizationRequest.ClientApplication?.ClientId ?? authorizationRequest.ClientApplicationId,
                authorizationRequestId: authorizationRequest.Id,
                surface: "headless");
            var completion = await _authorizationServerService.CompleteAuthorizationRequestLoginAsync(
                authorizationRequest,
                authentication.User,
                authentication.AuthenticationMethod,
                httpContext,
                cancellationToken);

            return await BuildCompletionActionResultAsync(
                authorizationRequest,
                completion,
                email,
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "password",
                await PublicViewErrorMessageAsync(httpContext, ex, cancellationToken),
                pendingToken: null,
                email: email,
                displayName: null,
                fieldErrors: null,
                organizationSelection: null,
                info: null,
                challengeToken: null,
                cancellationToken: cancellationToken));
        }
    }

    public async Task<SqlOSHeadlessActionResult> RequestEmailOtpAsync(
        HttpContext httpContext,
        SqlOSHeadlessEmailOtpStartRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        await BindInvitationIfPresentAsync(authorizationRequest, request.InvitationToken, cancellationToken);
        var email = await ResolveEffectiveEmailAsync(authorizationRequest, request.Email, cancellationToken);
        var ssoRedirect = await RedirectToSsoIfRequiredAsync(authorizationRequest, email, cancellationToken);
        if (ssoRedirect != null)
        {
            return ssoRedirect;
        }

        var boundInvitation = await GetBoundInvitationOrNullAsync(authorizationRequest, cancellationToken);

        if (boundInvitation != null)
        {
            var invitedAccountIsActive = await GetAccountActiveStateForEmailAsync(email, cancellationToken);
            if (invitedAccountIsActive == null)
            {
                return View(await BuildViewModelAsync(
                    authorizationRequest,
                    "signup",
                    "Create an account to accept this invitation.",
                    pendingToken: null,
                    email: email,
                    displayName: null,
                    fieldErrors: null,
                    organizationSelection: null,
                    info: null,
                    challengeToken: null,
                    cancellationToken: cancellationToken));
            }

            if (invitedAccountIsActive == false)
            {
                return View(await BuildViewModelAsync(
                    authorizationRequest,
                    "login",
                    "This invited account is inactive. Contact the workspace admin.",
                    pendingToken: null,
                    email: email,
                    displayName: null,
                    fieldErrors: null,
                    organizationSelection: null,
                    info: null,
                    challengeToken: null,
                    cancellationToken: cancellationToken));
            }
        }

        try
        {
            var challenge = await _emailOtpService.StartForAuthorizationRequestAsync(
                authorizationRequest,
                email,
                httpContext,
                cancellationToken);

            return View(await BuildViewModelAsync(
                authorizationRequest,
                "email-otp-verify",
                error: null,
                pendingToken: null,
                email: email,
                displayName: null,
                fieldErrors: null,
                organizationSelection: null,
                info: challenge.Message,
                challengeToken: challenge.ChallengeToken,
                cancellationToken: cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "email-otp",
                await PublicViewErrorMessageAsync(httpContext, ex, cancellationToken),
                pendingToken: null,
                email: email,
                displayName: null,
                fieldErrors: null,
                organizationSelection: null,
                info: null,
                challengeToken: null,
                cancellationToken: cancellationToken));
        }
    }

    public async Task<SqlOSHeadlessActionResult> RequestEmailOtpSignupAsync(
        HttpContext httpContext,
        SqlOSHeadlessEmailOtpSignupStartRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        await BindInvitationIfPresentAsync(authorizationRequest, request.InvitationToken, cancellationToken);
        var email = await ResolveEffectiveEmailAsync(authorizationRequest, request.Email, cancellationToken);
        var ssoRedirect = await RedirectToSsoIfRequiredAsync(authorizationRequest, email, cancellationToken);
        if (ssoRedirect != null)
        {
            return ssoRedirect;
        }

        try
        {
            var signup = await _emailOtpService.StartSignupForAuthorizationRequestAsync(
                authorizationRequest,
                request.DisplayName,
                email,
                string.IsNullOrWhiteSpace(authorizationRequest.InvitationId) ? request.OrganizationName : null,
                request.CustomFields,
                httpContext,
                cancellationToken);

            return View(await BuildViewModelAsync(
                authorizationRequest,
                "email-otp-signup-verify",
                error: null,
                pendingToken: null,
                email: email,
                displayName: request.DisplayName,
                fieldErrors: null,
                organizationSelection: null,
                info: signup.Message,
                challengeToken: signup.ChallengeToken,
                signupToken: signup.SignupToken,
                cancellationToken: cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "signup",
                await PublicViewErrorMessageAsync(httpContext, ex, cancellationToken),
                pendingToken: null,
                email: email,
                displayName: request.DisplayName,
                fieldErrors: null,
                organizationSelection: null,
                cancellationToken: cancellationToken));
        }
    }

    public async Task<SqlOSHeadlessActionResult> RequestMagicLinkAsync(
        HttpContext httpContext,
        SqlOSHeadlessMagicLinkStartRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        await BindInvitationIfPresentAsync(authorizationRequest, request.InvitationToken, cancellationToken);
        var email = await ResolveEffectiveEmailAsync(authorizationRequest, request.Email, cancellationToken);
        var ssoRedirect = await RedirectToSsoIfRequiredAsync(authorizationRequest, email, cancellationToken);
        if (ssoRedirect != null)
        {
            return ssoRedirect;
        }

        try
        {
            var start = await RequireMagicLinkService().StartForAuthorizationRequestAsync(
                authorizationRequest,
                email,
                httpContext,
                cancellationToken);

            return View(await BuildViewModelAsync(
                authorizationRequest,
                "magic-link-sent",
                error: null,
                pendingToken: null,
                email: email,
                displayName: null,
                fieldErrors: null,
                organizationSelection: null,
                info: start.Message,
                cancellationToken: cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "magic-link",
                ex.Message,
                pendingToken: null,
                email: email,
                displayName: null,
                fieldErrors: null,
                organizationSelection: null,
                info: null,
                cancellationToken: cancellationToken));
        }
    }

    public async Task<SqlOSHeadlessActionResult> VerifyEmailOtpAsync(
        HttpContext httpContext,
        SqlOSHeadlessEmailOtpVerifyRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        await BindInvitationIfPresentAsync(authorizationRequest, request.InvitationToken, cancellationToken);

        try
        {
            var verification = await _emailOtpService.VerifyAsync(
                new SqlOSEmailOtpVerifyRequest(request.ChallengeToken, request.Code),
                authorizationRequest.Id,
                requireAuthorizationRequestMatch: true,
                cancellationToken);

            if (!string.Equals(verification.Challenge.AuthorizationRequestId, authorizationRequest.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The sign-in code is invalid or expired.");
            }

            var completion = await _authorizationServerService.CompleteAuthorizationRequestLoginAsync(
                authorizationRequest,
                verification.User,
                verification.AuthenticationMethod,
                httpContext,
                cancellationToken);

            return await BuildCompletionActionResultAsync(
                authorizationRequest,
                completion,
                verification.Challenge.Email,
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "email-otp-verify",
                await PublicViewErrorMessageAsync(httpContext, ex, cancellationToken),
                pendingToken: null,
                email: authorizationRequest.LoginHintEmail,
                displayName: null,
                fieldErrors: null,
                organizationSelection: null,
                info: null,
                challengeToken: request.ChallengeToken,
                cancellationToken: cancellationToken));
        }
    }

    public async Task<SqlOSHeadlessActionResult> CompleteMagicLinkAsync(
        HttpContext httpContext,
        SqlOSHeadlessMagicLinkCompleteRequest request,
        CancellationToken cancellationToken = default)
    {
        var verification = await RequireMagicLinkService().CompleteAsync(
            new SqlOSMagicLinkCompleteRequest(request.Token),
            request.RequestId,
            requireAuthorizationRequestMatch: !string.IsNullOrWhiteSpace(request.RequestId),
            cancellationToken);
        var authorizationRequestId = verification.Payload.AuthorizationRequestId ?? request.RequestId;
        if (string.IsNullOrWhiteSpace(authorizationRequestId))
        {
            throw new InvalidOperationException("The sign-in link is invalid or expired.");
        }

        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(authorizationRequestId, cancellationToken);
        await BindInvitationIfPresentAsync(authorizationRequest, request.InvitationToken, cancellationToken);
        if (!string.Equals(verification.Payload.AuthorizationRequestId, authorizationRequest.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The sign-in link is invalid or expired.");
        }

        var completion = await _authorizationServerService.CompleteAuthorizationRequestLoginAsync(
            authorizationRequest,
            verification.User,
            verification.AuthenticationMethod,
            httpContext,
            cancellationToken);

        return await BuildCompletionActionResultAsync(
            authorizationRequest,
            completion,
            verification.Payload.Email,
            cancellationToken);
    }

    public async Task<SqlOSHeadlessActionResult> VerifyEmailOtpSignupAsync(
        HttpContext httpContext,
        SqlOSHeadlessEmailOtpSignupVerifyRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        await BindInvitationIfPresentAsync(authorizationRequest, request.InvitationToken, cancellationToken);
        var boundInvitation = await GetBoundInvitationOrNullAsync(authorizationRequest, cancellationToken);
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
                new SqlOSEmailOtpSignupVerifyRequest(request.SignupToken, request.ChallengeToken, request.Code),
                authorizationRequest.Id,
                requireAuthorizationRequestMatch: true,
                cancellationToken);

            signup = await _authorizationServerService.SignUpWithEmailOtpAsync(
                verification.DisplayName,
                verification.Email,
                boundInvitation == null ? verification.OrganizationName : null,
                boundInvitation == null ? authorizationRequest.OrganizationId ?? verification.OrganizationId : null,
                cancellationToken);

            var selectedOrganizationId = boundInvitation?.OrganizationId
                ?? signup.Organizations.FirstOrDefault()?.Id;
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
                        authorizationRequest,
                        signup.User,
                        organization,
                        verification.CustomFields ?? boundInvitation?.CustomFields ?? new JsonObject()),
                    cancellationToken);
            }

            var completion = await _authorizationServerService.CompleteAuthorizationRequestLoginAsync(
                authorizationRequest,
                signup.User,
                signup.AuthenticationMethod,
                httpContext,
                cancellationToken);

            await _emailOtpService.ConsumeSignupTokenAsync(verification.SignupToken, cancellationToken);
            await _adminService.RecordAuditAsync(
                "user.signup.email_otp",
                "user",
                signup.User.Id,
                userId: signup.User.Id,
                organizationId: selectedOrganizationId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken: cancellationToken);

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return await BuildCompletionActionResultAsync(
                authorizationRequest,
                completion,
                verification.Email,
                cancellationToken);
        }
        catch (SqlOSHeadlessValidationException ex)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            else
            {
                await CleanupNonTransactionalSignupArtifactsAsync(
                    signup,
                    boundInvitation?.OrganizationId ?? authorizationRequest.OrganizationId ?? verification?.OrganizationId,
                    boundInvitation == null ? verification?.OrganizationName : null,
                    cancellationToken);
            }

            return View(await BuildViewModelAsync(
                authorizationRequest,
                "email-otp-signup-verify",
                ex.GlobalErrors.FirstOrDefault() ?? ex.Message,
                pendingToken: null,
                email: authorizationRequest.LoginHintEmail,
                displayName: null,
                fieldErrors: ex.FieldErrors,
                organizationSelection: null,
                challengeToken: request.ChallengeToken,
                signupToken: request.SignupToken,
                cancellationToken: cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            else
            {
                await CleanupNonTransactionalSignupArtifactsAsync(
                    signup,
                    boundInvitation?.OrganizationId ?? authorizationRequest.OrganizationId ?? verification?.OrganizationId,
                    boundInvitation == null ? verification?.OrganizationName : null,
                    cancellationToken);
            }

            return View(await BuildViewModelAsync(
                authorizationRequest,
                "email-otp-signup-verify",
                await PublicViewErrorMessageAsync(httpContext, ex, cancellationToken),
                pendingToken: null,
                email: authorizationRequest.LoginHintEmail,
                displayName: null,
                fieldErrors: null,
                organizationSelection: null,
                challengeToken: request.ChallengeToken,
                signupToken: request.SignupToken,
            cancellationToken: cancellationToken));
        }
    }

    public async Task<SqlOSHeadlessActionResult> RequestPhoneOtpAsync(
        HttpContext httpContext,
        SqlOSHeadlessPhoneOtpStartRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);

        try
        {
            var challenge = await RequirePhoneOtpService().StartForAuthorizationRequestAsync(
                authorizationRequest,
                request.PhoneNumber,
                httpContext,
                cancellationToken);

            return View(await BuildViewModelAsync(
                authorizationRequest,
                "phone-otp-verify",
                error: null,
                pendingToken: null,
                email: null,
                displayName: null,
                fieldErrors: null,
                organizationSelection: null,
                info: challenge.Message,
                challengeToken: challenge.ChallengeToken,
                phoneNumber: challenge.PhoneNumber,
                cancellationToken: cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "phone-otp",
                await PublicViewErrorMessageAsync(httpContext, ex, cancellationToken),
                pendingToken: null,
                email: null,
                displayName: null,
                fieldErrors: null,
                organizationSelection: null,
                phoneNumber: request.PhoneNumber,
                cancellationToken: cancellationToken));
        }
    }

    public async Task<SqlOSHeadlessActionResult> VerifyPhoneOtpAsync(
        HttpContext httpContext,
        SqlOSHeadlessPhoneOtpVerifyRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);

        try
        {
            var verification = await RequirePhoneOtpService().VerifyAsync(
                new SqlOSPhoneOtpVerifyRequest(request.ChallengeToken, request.Code),
                authorizationRequest.Id,
                requireAuthorizationRequestMatch: true,
                cancellationToken);

            var completion = await _authorizationServerService.CompleteAuthorizationRequestLoginAsync(
                authorizationRequest,
                verification.User,
                verification.AuthenticationMethod,
                httpContext,
                cancellationToken);

            return await BuildCompletionActionResultAsync(
                authorizationRequest,
                completion,
                email: null,
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "phone-otp-verify",
                await PublicViewErrorMessageAsync(httpContext, ex, cancellationToken),
                pendingToken: null,
                email: null,
                displayName: null,
                fieldErrors: null,
                organizationSelection: null,
                challengeToken: request.ChallengeToken,
                cancellationToken: cancellationToken));
        }
    }

    public async Task<SqlOSHeadlessActionResult> RequestPhoneOtpSignupAsync(
        HttpContext httpContext,
        SqlOSHeadlessPhoneOtpSignupStartRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        await BindInvitationIfPresentAsync(authorizationRequest, request.InvitationToken, cancellationToken);

        try
        {
            if (await GetBoundInvitationOrNullAsync(authorizationRequest, cancellationToken) != null)
            {
                throw new InvalidOperationException("Phone signup is not available for email invitations.");
            }

            var signup = await RequirePhoneOtpService().StartSignupForAuthorizationRequestAsync(
                authorizationRequest,
                request.DisplayName,
                request.PhoneNumber,
                request.OrganizationName,
                request.CustomFields,
                httpContext,
                cancellationToken);

            return View(await BuildViewModelAsync(
                authorizationRequest,
                "phone-otp-signup-verify",
                error: null,
                pendingToken: null,
                email: null,
                displayName: request.DisplayName,
                fieldErrors: null,
                organizationSelection: null,
                info: signup.Message,
                challengeToken: signup.ChallengeToken,
                signupToken: signup.SignupToken,
                phoneNumber: signup.PhoneNumber,
                cancellationToken: cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "phone-otp-signup",
                await PublicViewErrorMessageAsync(httpContext, ex, cancellationToken),
                pendingToken: null,
                email: null,
                displayName: request.DisplayName,
                fieldErrors: null,
                organizationSelection: null,
                phoneNumber: request.PhoneNumber,
                cancellationToken: cancellationToken));
        }
    }

    public async Task<SqlOSHeadlessActionResult> VerifyPhoneOtpSignupAsync(
        HttpContext httpContext,
        SqlOSHeadlessPhoneOtpSignupVerifyRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        await BindInvitationIfPresentAsync(authorizationRequest, request.InvitationToken, cancellationToken);
        IDbContextTransaction? transaction = null;
        SqlOSPasswordAuthenticationResult? signup = null;
        SqlOSPhoneOtpSignupVerificationResult? verification = null;

        try
        {
            if (SupportsDatabaseTransactions())
            {
                transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            }

            if (await GetBoundInvitationOrNullAsync(authorizationRequest, cancellationToken) != null)
            {
                throw new InvalidOperationException("Phone signup is not available for email invitations.");
            }

            verification = await RequirePhoneOtpService().VerifySignupAsync(
                new SqlOSPhoneOtpSignupVerifyRequest(request.SignupToken, request.ChallengeToken, request.Code),
                authorizationRequest.Id,
                requireAuthorizationRequestMatch: true,
                cancellationToken);

            signup = await _authorizationServerService.SignUpWithPhoneOtpAsync(
                verification.DisplayName,
                verification.PhoneNumber,
                verification.OrganizationName,
                authorizationRequest.OrganizationId ?? verification.OrganizationId,
                cancellationToken);

            var selectedOrganizationId = signup.Organizations.FirstOrDefault()?.Id;
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
                        authorizationRequest,
                        signup.User,
                        organization,
                        verification.CustomFields ?? new JsonObject()),
                    cancellationToken);
            }

            var completion = await _authorizationServerService.CompleteAuthorizationRequestLoginAsync(
                authorizationRequest,
                signup.User,
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
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken: cancellationToken);

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return await BuildCompletionActionResultAsync(
                authorizationRequest,
                completion,
                email: null,
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            else
            {
                await CleanupNonTransactionalSignupArtifactsAsync(
                    signup,
                    authorizationRequest.OrganizationId ?? verification?.OrganizationId,
                    verification?.OrganizationName,
                    cancellationToken);
            }

            return View(await BuildViewModelAsync(
                authorizationRequest,
                "phone-otp-signup-verify",
                await PublicViewErrorMessageAsync(httpContext, ex, cancellationToken),
                pendingToken: null,
                email: null,
                displayName: null,
                fieldErrors: null,
                organizationSelection: null,
                challengeToken: request.ChallengeToken,
                signupToken: request.SignupToken,
                phoneNumber: verification?.PhoneNumber,
                cancellationToken: cancellationToken));
        }
    }

    public async Task<SqlOSHeadlessActionResult> SignUpWithInvitationAsync(
        HttpContext httpContext,
        SqlOSHeadlessInvitationSignupRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        await BindInvitationIfPresentAsync(authorizationRequest, request.InvitationToken, cancellationToken);
        var boundInvitation = await GetBoundInvitationOrNullAsync(authorizationRequest, cancellationToken)
            ?? throw new InvalidOperationException("Invitation is invalid or expired.");
        var credentialSettings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!credentialSettings.EmailOtpEnabled)
        {
            throw new InvalidOperationException("Invitation signup without a password requires Email OTP to be enabled.");
        }

        var email = boundInvitation.Email;
        var ssoRedirect = await RedirectToSsoIfRequiredAsync(authorizationRequest, email, cancellationToken);
        if (ssoRedirect != null)
        {
            return ssoRedirect;
        }

        IDbContextTransaction? transaction = null;
        SqlOSPasswordAuthenticationResult? signup = null;

        try
        {
            if (SupportsDatabaseTransactions())
            {
                transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            }

            signup = await _authorizationServerService.SignUpWithInvitationAsync(
                request.DisplayName,
                email,
                cancellationToken);

            SqlOSOrganization? organization = null;
            if (!string.IsNullOrWhiteSpace(boundInvitation.OrganizationId))
            {
                organization = await _context.Set<SqlOSOrganization>()
                    .FirstOrDefaultAsync(x => x.Id == boundInvitation.OrganizationId, cancellationToken);
            }

            if (_options.Headless.OnHeadlessSignupAsync != null)
            {
                await _options.Headless.OnHeadlessSignupAsync(
                    new SqlOSHeadlessSignupHookContext(
                        httpContext,
                        authorizationRequest,
                        signup.User,
                        organization,
                        request.CustomFields ?? boundInvitation.CustomFields ?? new JsonObject()),
                    cancellationToken);
            }

            var completion = await _authorizationServerService.CompleteAuthorizationRequestLoginAsync(
                authorizationRequest,
                signup.User,
                signup.AuthenticationMethod,
                httpContext,
                cancellationToken);

            await _adminService.RecordAuditAsync(
                "user.signup.invitation",
                "user",
                signup.User.Id,
                userId: signup.User.Id,
                organizationId: boundInvitation.OrganizationId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken: cancellationToken);

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return await BuildCompletionActionResultAsync(
                authorizationRequest,
                completion,
                email,
                cancellationToken);
        }
        catch (SqlOSHeadlessValidationException ex)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            else
            {
                await CleanupNonTransactionalSignupArtifactsAsync(
                    signup,
                    boundInvitation.OrganizationId,
                    organizationName: null,
                    cancellationToken: cancellationToken);
            }

            return View(await BuildViewModelAsync(
                authorizationRequest,
                "signup",
                ex.GlobalErrors.FirstOrDefault() ?? ex.Message,
                pendingToken: null,
                email: email,
                displayName: request.DisplayName,
                fieldErrors: ex.FieldErrors,
                organizationSelection: null,
                cancellationToken: cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            else
            {
                await CleanupNonTransactionalSignupArtifactsAsync(
                    signup,
                    boundInvitation.OrganizationId,
                    organizationName: null,
                    cancellationToken: cancellationToken);
            }

            return View(await BuildViewModelAsync(
                authorizationRequest,
                "signup",
                await PublicViewErrorMessageAsync(httpContext, ex, cancellationToken),
                pendingToken: null,
                email: email,
                displayName: request.DisplayName,
                fieldErrors: null,
                organizationSelection: null,
                cancellationToken: cancellationToken));
        }
    }

    public async Task<SqlOSHeadlessActionResult> SignUpAsync(
        HttpContext httpContext,
        SqlOSHeadlessSignupRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        await BindInvitationIfPresentAsync(authorizationRequest, request.InvitationToken, cancellationToken);
        var boundInvitation = await GetBoundInvitationOrNullAsync(authorizationRequest, cancellationToken);
        SqlOSSignupOrchestration.RejectInvitationEmailMismatch(boundInvitation?.Email, request.Email);
        var email = await ResolveEffectiveEmailAsync(authorizationRequest, request.Email, cancellationToken);
        var ssoRedirect = await RedirectToSsoIfRequiredAsync(authorizationRequest, email, cancellationToken);
        if (ssoRedirect != null)
        {
            return ssoRedirect;
        }

        IDbContextTransaction? transaction = null;
        SqlOSPasswordAuthenticationResult? signup = null;

        try
        {
            await _authorizationServerService.EnsureSignupAuthorizationContextAsync(authorizationRequest, cancellationToken);
            if (SupportsDatabaseTransactions())
            {
                transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            }

            signup = await _authorizationServerService.SignUpAsync(
                request.DisplayName,
                email,
                request.Password,
                boundInvitation == null ? request.OrganizationName : null,
                boundInvitation == null ? authorizationRequest.OrganizationId : null,
                cancellationToken);

            var selectedOrganizationId = boundInvitation?.OrganizationId
                ?? signup.Organizations.FirstOrDefault()?.Id;
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
                        authorizationRequest,
                        signup.User,
                        organization,
                        request.CustomFields ?? new JsonObject()),
                    cancellationToken);
            }

            var completion = await _authorizationServerService.CompleteAuthorizationRequestLoginAsync(
                authorizationRequest,
                signup.User,
                signup.AuthenticationMethod,
                httpContext,
                cancellationToken);

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return await BuildCompletionActionResultAsync(
                authorizationRequest,
                completion,
                email,
                cancellationToken);
        }
        catch (SqlOSHeadlessValidationException ex)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            else
            {
                await CleanupNonTransactionalSignupArtifactsAsync(
                    signup,
                    boundInvitation?.OrganizationId ?? authorizationRequest.OrganizationId,
                    boundInvitation == null ? request.OrganizationName : null,
                    cancellationToken);
            }
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "signup",
                ex.GlobalErrors.FirstOrDefault() ?? ex.Message,
                pendingToken: null,
                email: email,
                displayName: request.DisplayName,
                fieldErrors: ex.FieldErrors,
                organizationSelection: null,
                cancellationToken: cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            else
            {
                await CleanupNonTransactionalSignupArtifactsAsync(
                    signup,
                    boundInvitation?.OrganizationId ?? authorizationRequest.OrganizationId,
                    boundInvitation == null ? request.OrganizationName : null,
                    cancellationToken);
            }
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "signup",
                await PublicViewErrorMessageAsync(httpContext, ex, cancellationToken),
                pendingToken: null,
                email: email,
                displayName: request.DisplayName,
                fieldErrors: null,
                organizationSelection: null,
                cancellationToken: cancellationToken));
        }
    }

    public async Task<SqlOSHeadlessActionResult> SelectOrganizationAsync(
        HttpContext httpContext,
        SqlOSHeadlessOrganizationSelectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var completion = await _authorizationServerService.CompletePendingOrganizationSelectionForLoginAsync(
            request.PendingToken,
            request.OrganizationId,
            httpContext,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(completion.AuthorizationRequestId))
        {
            return Redirect(completion.RedirectUrl!);
        }

        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(
            completion.AuthorizationRequestId,
            cancellationToken);
        return await BuildCompletionActionResultAsync(
            authorizationRequest,
            completion,
            email: null,
            cancellationToken);
    }

    public async Task<SqlOSHeadlessActionResult> VerifyMfaAsync(
        HttpContext httpContext,
        SqlOSHeadlessMfaVerifyRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        try
        {
            return Redirect(await _authorizationServerService.CompleteMfaChallengeAsync(
                request.MfaToken,
                request.Code,
                httpContext,
                cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "mfa",
                await PublicViewErrorMessageAsync(httpContext, ex, cancellationToken),
                pendingToken: null,
                email: authorizationRequest.LoginHintEmail,
                displayName: null,
                fieldErrors: null,
                organizationSelection: null,
                mfaToken: request.MfaToken,
                mfaMethods: [SqlOSMfaFactorTypes.Totp, SqlOSMfaFactorTypes.RecoveryCode],
                cancellationToken: cancellationToken));
        }
    }

    public async Task<SqlOSHeadlessActionResult> StartMfaTotpEnrollmentAsync(
        SqlOSHeadlessMfaTotpEnrollmentStartRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        try
        {
            var enrollment = await RequireAuthService().StartTotpEnrollmentForAuthorizationChallengeAsync(
                request.MfaToken,
                request.RequestId,
                new SqlOSTotpEnrollmentStartRequest(request.DisplayName),
                cancellationToken);
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "mfa-enroll",
                error: null,
                pendingToken: null,
                email: authorizationRequest.LoginHintEmail,
                displayName: null,
                fieldErrors: null,
                organizationSelection: null,
                mfaToken: request.MfaToken,
                requiresMfaEnrollment: true,
                mfaMethods: [SqlOSMfaFactorTypes.Totp],
                totpEnrollment: enrollment,
                cancellationToken: cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "mfa-enroll",
                await PublicViewErrorMessageAsync(null, ex, cancellationToken),
                pendingToken: null,
                email: authorizationRequest.LoginHintEmail,
                displayName: null,
                fieldErrors: null,
                organizationSelection: null,
                mfaToken: request.MfaToken,
                requiresMfaEnrollment: true,
                mfaMethods: [SqlOSMfaFactorTypes.Totp],
                cancellationToken: cancellationToken));
        }
    }

    public async Task<SqlOSHeadlessActionResult> VerifyMfaTotpEnrollmentAsync(
        HttpContext httpContext,
        SqlOSHeadlessMfaTotpEnrollmentVerifyRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        try
        {
            return Redirect(await _authorizationServerService.VerifyMfaTotpEnrollmentAsync(
                request.MfaToken,
                request.EnrollmentToken,
                request.Code,
                request.RequestId,
                httpContext,
                cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "mfa-enroll",
                await PublicViewErrorMessageAsync(httpContext, ex, cancellationToken),
                pendingToken: null,
                email: authorizationRequest.LoginHintEmail,
                displayName: null,
                fieldErrors: null,
                organizationSelection: null,
                mfaToken: request.MfaToken,
                requiresMfaEnrollment: true,
                mfaMethods: [SqlOSMfaFactorTypes.Totp],
                cancellationToken: cancellationToken));
        }
    }

    public async Task<SqlOSHeadlessActionResult> StartProviderAsync(
        HttpContext httpContext,
        SqlOSHeadlessProviderStartRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        await BindInvitationIfPresentAsync(authorizationRequest, request.InvitationToken, cancellationToken);
        var email = string.IsNullOrWhiteSpace(request.Email)
            ? authorizationRequest.LoginHintEmail
            : await ResolveEffectiveEmailAsync(authorizationRequest, request.Email, cancellationToken);

        var result = await _oidcBrowserAuthService.CreateAuthorizationUrlForAuthRequestAsync(
            request.RequestId,
            request.ConnectionId,
            email,
            httpContext,
            cancellationToken);

        return Redirect(result.AuthorizationUrl);
    }

    public async Task<SqlOSHeadlessViewModel> BuildViewModelAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        string? requestedView,
        string? error,
        string? pendingToken,
        string? email,
        string? displayName,
        IReadOnlyDictionary<string, string>? fieldErrors,
        IReadOnlyList<SqlOSOrganizationOption>? organizationSelection,
        string? info = null,
        string? challengeToken = null,
        string? signupToken = null,
        string? phoneNumber = null,
        string? mfaToken = null,
        bool requiresMfaEnrollment = false,
        IReadOnlyList<string>? mfaMethods = null,
        SqlOSTotpEnrollmentStartResult? totpEnrollment = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetAuthPageSettingsAsync(cancellationToken);
        var providers = (await _authorizationServerService.ListEnabledOidcProvidersAsync(cancellationToken))
            .Select(provider => new SqlOSHeadlessProviderDto(
                provider.ConnectionId,
                provider.ProviderType,
                provider.DisplayName,
                provider.LogoDataUrl))
            .ToArray();
        var deviceAuthorization = string.IsNullOrWhiteSpace(authorizationRequest.DeviceAuthorizationId) || _deviceAuthorizationService == null
            ? null
            : ToHeadlessDeviceAuthorization(await _deviceAuthorizationService.ResolveAsync(authorizationRequest, user: null, cancellationToken));

        return new SqlOSHeadlessViewModel(
            NormalizeView(requestedView),
            _options.BasePath.TrimEnd('/'),
            GetHeadlessApiBasePath(),
            settings,
            authorizationRequest.Id,
            authorizationRequest.ClientApplication?.ClientId,
            authorizationRequest.ClientApplication?.Name,
            email ?? authorizationRequest.LoginHintEmail,
            displayName,
            error,
            info,
            fieldErrors ?? new Dictionary<string, string>(StringComparer.Ordinal),
            challengeToken,
            signupToken,
            pendingToken,
            organizationSelection ?? Array.Empty<SqlOSOrganizationOption>(),
            providers,
            await GetBoundInvitationOrNullAsync(authorizationRequest, cancellationToken),
            ParseUiContext(authorizationRequest.UiContextJson),
            DeviceAuthorization: deviceAuthorization,
            PhoneNumber: phoneNumber,
            MfaToken: mfaToken,
            RequiresMfaEnrollment: requiresMfaEnrollment,
            MfaMethods: mfaMethods ?? Array.Empty<string>(),
            TotpEnrollment: totpEnrollment);
    }

    private async Task<string> PublicViewErrorMessageAsync(
        HttpContext? httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var error = SqlOSPublicAuthErrorMapper.Map(exception, SqlOSPublicAuthErrorSurface.HeadlessView);
        if (httpContext != null)
        {
            await SqlOSPublicAuthErrorAudit.RecordIfDiagnosticAsync(
                _adminService,
                httpContext,
                SqlOSPublicAuthErrorSurface.HeadlessView,
                exception,
                error,
                cancellationToken);
        }

        return error.PublicMessage;
    }

    public static bool IsHeadlessRequest(SqlOSAuthorizationRequest authorizationRequest)
        => string.Equals(authorizationRequest.PresentationMode, "headless", StringComparison.OrdinalIgnoreCase);

    public static string? NormalizeUiContext(JsonObject? uiContext)
        => uiContext?.ToJsonString();

    public async Task EnsureNativeHeadlessClientAllowedAsync(
        string clientId,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        var client = await _adminService.RequireClientAsync(clientId, redirectUri, cancellationToken);

        if (!IsApiEnabled)
        {
            throw new InvalidOperationException("Native headless auth is not enabled.");
        }

        if (!client.IsFirstParty)
        {
            throw new InvalidOperationException("Native headless auth is only available to first-party clients.");
        }

        if (!client.RequirePkce || !string.Equals(client.ClientType, "public_pkce", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Native headless auth requires a PKCE public client.");
        }

        if (!client.AllowNativeHeadlessAuth)
        {
            throw new InvalidOperationException("This client is not allowed to start native headless auth.");
        }
    }

    public static JsonObject? ParseUiContext(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    public static string? NormalizeUiContext(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return ParseUiContext(json)?.ToJsonString();
    }

    public static string NormalizeView(string? requestedView)
    {
        var normalized = requestedView?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "signup" => "signup",
            "password" => "password",
            "forgot-password" => "forgot-password",
            "forgot-password-sent" => "forgot-password-sent",
            "password-reset" => "password-reset",
            "email-otp" => "email-otp",
            "email-otp-verify" => "email-otp-verify",
            "email-otp-signup-verify" => "email-otp-signup-verify",
            "magic-link" => "magic-link",
            "magic-link-sent" => "magic-link-sent",
            "phone-otp" => "phone-otp",
            "phone-otp-verify" => "phone-otp-verify",
            "phone-otp-signup" => "phone-otp-signup",
            "phone-otp-signup-verify" => "phone-otp-signup-verify",
            "invite" => "invite",
            "invite-login" => "invite-login",
            "invite-email-otp-verify" => "invite-email-otp-verify",
            "invite-accepted" => "invite-accepted",
            "device" => "device",
            "device-approve" => "device-approve",
            "device-approved" => "device-approved",
            "device-denied" => "device-denied",
            "mfa" => "mfa",
            "mfa-enroll" => "mfa-enroll",
            "organization" => "organization",
            "logged-out" => "logged-out",
            _ => "login"
        };
    }

    private async Task<SqlOSHeadlessActionResult> BuildCompletionActionResultAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        SqlOSAuthorizationRequestLoginResult completion,
        string? email,
        CancellationToken cancellationToken)
    {
        if (completion.RequiresOrganizationSelection)
        {
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "organization",
                error: null,
                pendingToken: completion.PendingToken,
                email: email,
                displayName: null,
                fieldErrors: null,
                organizationSelection: completion.Organizations,
                info: null,
                challengeToken: null,
                cancellationToken: cancellationToken));
        }

        if (completion.RequiresMfa)
        {
            SqlOSTotpEnrollmentStartResult? totpEnrollment = null;
            if (completion.RequiresMfaEnrollment && !string.IsNullOrWhiteSpace(completion.MfaToken))
            {
                totpEnrollment = await RequireAuthService().StartTotpEnrollmentForAuthorizationChallengeAsync(
                    completion.MfaToken,
                    authorizationRequest.Id,
                    new SqlOSTotpEnrollmentStartRequest(),
                    cancellationToken);
            }

            return View(await BuildViewModelAsync(
                authorizationRequest,
                completion.RequiresMfaEnrollment ? "mfa-enroll" : "mfa",
                error: null,
                pendingToken: null,
                email: email,
                displayName: null,
                fieldErrors: null,
                organizationSelection: completion.Organizations,
                mfaToken: completion.MfaToken,
                requiresMfaEnrollment: completion.RequiresMfaEnrollment,
                mfaMethods: completion.MfaMethods ?? Array.Empty<string>(),
                totpEnrollment: totpEnrollment,
                cancellationToken: cancellationToken));
        }

        return Redirect(completion.RedirectUrl!);
    }

    private static SqlOSHeadlessActionResult Redirect(string url)
        => new("redirect", url, null);

    private static SqlOSHeadlessActionResult View(SqlOSHeadlessViewModel viewModel)
        => new("view", null, viewModel);

    private SqlOSMagicLinkService RequireMagicLinkService()
        => _magicLinkService ?? throw new InvalidOperationException("Magic-link service is not registered.");

    private static string ResolvePreferredLocalView(SqlOSResolvedCredentialSettings credentialSettings)
    {
        if (credentialSettings.EmailOtpEnabled)
        {
            return "email-otp";
        }

        if (credentialSettings.MagicLinkEnabled)
        {
            return "magic-link";
        }

        if (credentialSettings.PhoneOtpEnabled)
        {
            return "phone-otp";
        }

        if (credentialSettings.PasswordEnabled)
        {
            return "password";
        }

        return "login";
    }

    private bool SupportsDatabaseTransactions()
        => !string.Equals(_context.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal);

    private SqlOSPhoneOtpService RequirePhoneOtpService()
        => _phoneOtpService ?? throw new InvalidOperationException("Phone OTP service is not registered.");

    private SqlOSAuthService RequireAuthService()
        => _authService ?? throw new InvalidOperationException("Auth service is not registered.");

    private async Task BindInvitationIfPresentAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        string? invitationToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(invitationToken) || _invitationService == null)
        {
            return;
        }

        await _invitationService.BindInvitationToAuthorizationRequestAsync(invitationToken, authorizationRequest, cancellationToken);
    }

    private async Task<string> ResolveEffectiveEmailAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        string requestedEmail,
        CancellationToken cancellationToken)
    {
        var invitation = await GetBoundInvitationOrNullAsync(authorizationRequest, cancellationToken);
        return invitation?.Email ?? requestedEmail;
    }

    private async Task<SqlOSHeadlessActionResult?> RedirectToSsoIfRequiredAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        string email,
        CancellationToken cancellationToken)
    {
        var discovery = await _discoveryService.DiscoverAsync(new SqlOSHomeRealmDiscoveryRequest(email), cancellationToken);
        authorizationRequest.LoginHintEmail = email;
        if (!string.IsNullOrWhiteSpace(discovery.OrganizationId))
        {
            authorizationRequest.OrganizationId = discovery.OrganizationId;
            authorizationRequest.ResolvedOrganizationId = discovery.OrganizationId;
        }

        if (!string.IsNullOrWhiteSpace(discovery.ConnectionId))
        {
            authorizationRequest.ConnectionId = discovery.ConnectionId;
            authorizationRequest.ResolvedConnectionId = discovery.ConnectionId;
        }

        await _context.SaveChangesAsync(cancellationToken);

        if (string.Equals(discovery.Mode, "sso", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(discovery.ConnectionId))
        {
            return Redirect(await _samlService.BuildIdentityProviderRedirectForAuthorizationRequestAsync(authorizationRequest.Id, cancellationToken));
        }

        return null;
    }

    private async Task<bool?> GetAccountActiveStateForEmailAsync(
        string email,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = SqlOSAdminService.NormalizeEmail(email);
        var emailRecord = await _context.Set<SqlOSUserEmail>()
            .Include(x => x.User)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);

        return emailRecord == null
            ? null
            : emailRecord.User?.IsActive == true;
    }

    private async Task<SqlOSEmailInvitationResult?> GetBoundInvitationOrNullAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authorizationRequest.InvitationId) || _invitationService == null)
        {
            return null;
        }

        return await _invitationService.GetBoundInvitationAsync(authorizationRequest, cancellationToken);
    }

    private SqlOSInvitationService RequireInvitationService()
        => _invitationService ?? throw new InvalidOperationException("SqlOS invitations are not configured.");

    private SqlOSDeviceAuthorizationService RequireDeviceAuthorizationService()
        => _deviceAuthorizationService ?? throw new InvalidOperationException("Device authorization support is not configured.");

    private SqlOSAuthPageSessionService RequireAuthPageSessionService()
        => _authPageSessionService ?? throw new InvalidOperationException("AuthPage session support is not configured.");

    private async Task<SqlOSHeadlessViewModel> BuildStandaloneDeviceViewModelAsync(
        string view,
        SqlOSDeviceAuthorizationResolveResult resolved,
        string? error,
        string? info,
        IReadOnlyList<SqlOSOrganizationOption>? organizationSelection,
        CancellationToken cancellationToken)
    {
        var settings = await _settingsService.GetAuthPageSettingsAsync(cancellationToken);
        var providers = (await _authorizationServerService.ListEnabledOidcProvidersAsync(cancellationToken))
            .Select(provider => new SqlOSHeadlessProviderDto(
                provider.ConnectionId,
                provider.ProviderType,
                provider.DisplayName,
                provider.LogoDataUrl))
            .ToArray();
        var uiContext = new JsonObject
        {
            ["deviceUserCode"] = resolved.UserCode
        };

        return new SqlOSHeadlessViewModel(
            NormalizeView(view),
            _options.BasePath.TrimEnd('/'),
            GetHeadlessApiBasePath(),
            settings,
            RequestId: null,
            ClientId: resolved.ClientId,
            ClientName: resolved.ClientName,
            Email: null,
            DisplayName: null,
            Error: error,
            Info: info,
            FieldErrors: new Dictionary<string, string>(StringComparer.Ordinal),
            ChallengeToken: null,
            SignupToken: null,
            PendingToken: null,
            OrganizationSelection: organizationSelection ?? Array.Empty<SqlOSOrganizationOption>(),
            Providers: providers,
            Invitation: null,
            UiContext: uiContext,
            DeviceAuthorization: ToHeadlessDeviceAuthorization(resolved));
    }

    private static SqlOSHeadlessDeviceAuthorizationDto ToHeadlessDeviceAuthorization(SqlOSDeviceAuthorizationResolveResult resolved)
        => new(
            resolved.UserCode,
            resolved.ClientId,
            resolved.ClientName,
            resolved.Scope,
            resolved.Resource,
            resolved.ExpiresAt,
            resolved.Status);

    private static string RequireDeviceUserCode(string? userCode)
    {
        if (string.IsNullOrWhiteSpace(userCode))
        {
            throw new InvalidOperationException("Device user code is required.");
        }

        return userCode;
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
}

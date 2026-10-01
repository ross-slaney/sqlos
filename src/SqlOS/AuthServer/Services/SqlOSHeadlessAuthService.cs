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
using SqlOS.AuthServer.Processes.Identity;
using SqlOS.Domain;
using SqlOS.Hosting;

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
    private readonly SqlOSIssuerSessionService? _issuerSessionService;
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
        SqlOSIssuerSessionService? issuerSessionService = null,
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
        _issuerSessionService = issuerSessionService;
        _options = options.Value;
    }

    /// <summary>The identity processes the headless API runs, shared with the hosted and public surfaces.</summary>
    private SqlOSIdentityProcesses Processes => new(
        _context,
        _adminService,
        _authorizationServerService.Crypto,
        _settingsService,
        _options)
    {
        PasswordAdmission = _authorizationServerService.PasswordAdmission,
        AuthorizationServer = _authorizationServerService,
        Auth = _authService,
        IssuerSessions = _issuerSessionService,
        Invitations = _invitationService,
        HomeRealms = _discoveryService,
        Saml = _samlService,
        EmailCodes = _emailOtpService,
        SignInLinks = _magicLinkService,
        PhoneCodes = _phoneOtpService
    };

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
        string? mfaToken = null,
        string? consentToken = null)
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
                mfaToken,
                consentToken));
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
        string? mfaToken = null,
        HttpContext? httpContext = null)
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

        // A consent reload arrives without its consent token whenever a custom BuildUiUrl
        // delegate did not forward the ConsentToken route field. Re-mint one from the
        // browser's continuation cookie or live issuer session; anonymous reloads get
        // the consent view without a token (fail closed).
        string? consentToken = null;
        if (httpContext != null && string.Equals(NormalizeView(requestedView), "consent", StringComparison.Ordinal))
        {
            consentToken = await _authorizationServerService.TryCreateConsentTokenForRequestReloadAsync(
                authorizationRequest,
                httpContext,
                cancellationToken);
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
            cancellationToken: cancellationToken,
            consentToken: consentToken);
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
            UiContext: uiContext,
            Scope: "");
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
        var session = await RequireIssuerSessionService().TryGetSessionAsync(httpContext, cancellationToken)
            ?? throw new InvalidOperationException("Sign in before approving this device request.");

        // The picked organization is authorized by the membership-checked completion and
        // approval below. It is never written to the authorization request itself.
        if (!string.IsNullOrWhiteSpace(request.RequestId))
        {
            var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
            SqlOSDeviceAuthorizationService.RequireDeviceAuthorizationRequest(authorizationRequest);

            if (string.IsNullOrWhiteSpace(authorizationRequest.ResolvedAuthMethod))
            {
                var completion = await _authorizationServerService.CompleteDeviceApprovalLoginAsync(
                    authorizationRequest,
                    session.User,
                    session.AuthenticationMethod,
                    httpContext,
                    request.OrganizationId,
                    cancellationToken);
                if (completion.RequiresMfa || completion.RequiresOrganizationSelection)
                {
                    return await BuildCompletionActionResultAsync(
                        authorizationRequest,
                        completion,
                        session.User.DefaultEmail,
                        cancellationToken);
                }

                session = await RequireIssuerSessionService().TryGetSessionAsync(httpContext, cancellationToken)
                    ?? throw new InvalidOperationException("Sign in before approving this device request.");
            }

            var requestResolved = await RequireDeviceAuthorizationService().ApproveAuthorizationRequestAsync(
                authorizationRequest,
                session.User,
                session.AuthenticationMethod,
                httpContext,
                request.OrganizationId,
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
        var standaloneRequest = await RequireDeviceAuthorizationService().CreateOrGetAuthorizationRequestAsync(
            userCode,
            "headless",
            cancellationToken);

        var standaloneCompletion = await _authorizationServerService.CompleteDeviceApprovalLoginAsync(
            standaloneRequest,
            session.User,
            session.AuthenticationMethod,
            httpContext,
            request.OrganizationId,
            cancellationToken);
        if (standaloneCompletion.RequiresMfa || standaloneCompletion.RequiresOrganizationSelection)
        {
            return await BuildCompletionActionResultAsync(
                standaloneRequest,
                standaloneCompletion,
                session.User.DefaultEmail,
                cancellationToken);
        }

        session = await RequireIssuerSessionService().TryGetSessionAsync(httpContext, cancellationToken)
            ?? throw new InvalidOperationException("Sign in before approving this device request.");
        var resolved = await RequireDeviceAuthorizationService().ApproveAuthorizationRequestAsync(
            standaloneRequest,
            session.User,
            session.AuthenticationMethod,
            httpContext,
            request.OrganizationId,
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
        var session = await RequireIssuerSessionService().TryGetSessionAsync(httpContext, cancellationToken);
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

    public async Task<SqlOSHeadlessActionResult> ApproveConsentAsync(
        HttpContext httpContext,
        SqlOSHeadlessConsentRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        var completion = await _authorizationServerService.ApproveConsentAsync(
            request.ConsentToken,
            authorizationRequest.Id,
            httpContext,
            cancellationToken);
        return await BuildCompletionActionResultAsync(
            authorizationRequest,
            completion,
            authorizationRequest.LoginHintEmail,
            cancellationToken);
    }

    public async Task<SqlOSHeadlessActionResult> DenyConsentAsync(
        HttpContext httpContext,
        SqlOSHeadlessConsentRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        return Redirect(await _authorizationServerService.DenyConsentAsync(
            request.ConsentToken,
            authorizationRequest.Id,
            httpContext,
            cancellationToken));
    }

    public async Task<SqlOSHeadlessActionResult> IdentifyAsync(
        SqlOSHeadlessIdentifyRequest request,
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
            var outcome = await Processes.SignInWithPassword(httpContext).ExecuteAsync(
                new SignInWithPasswordCommand(
                    email,
                    request.Password,
                    new LoginTarget.AuthorizationRequest(authorizationRequest),
                    CarriesInvitation: !string.IsNullOrWhiteSpace(authorizationRequest.InvitationId),
                    PasswordAttemptContext.ForAuthorizationRequest(authorizationRequest, "headless"),
                    SqlOSHttpRequestContext.From(httpContext, SqlOSRequestSurface.Headless)),
                cancellationToken);
            var completion = outcome switch
            {
                SignInOutcome.SignedIn { Completion: LoginCompletion.AuthorizationRequestContinued continued } => continued.Result,
                SignInOutcome.Refused refused => throw refused.Refusal.ToException(),
                _ => throw new InvalidOperationException($"Unknown sign-in outcome '{outcome.GetType().Name}'.")
            };

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

        try
        {
            var outcome = await Processes.StartEmailOtpSignIn().ExecuteAsync(
                new StartEmailOtpSignInCommand(
                    email,
                    new LoginTarget.AuthorizationRequest(authorizationRequest),
                    SqlOSHttpRequestContext.From(httpContext, SqlOSRequestSurface.Headless),
                    RequiresInvitedAccount: boundInvitation != null),
                cancellationToken);
            var challenge = outcome switch
            {
                EmailCodeStartOutcome.Sent sent => sent.Result,
                EmailCodeStartOutcome.Refused { Refusal: var refusal } when refusal == IdentityRefusals.InvitedAccountMissing => null,
                EmailCodeStartOutcome.Refused { Refusal: var refusal } when refusal == IdentityRefusals.InvitedAccountInactive => null,
                EmailCodeStartOutcome.Refused refused => throw refused.Refusal.ToException(),
                _ => throw new InvalidOperationException($"Unknown email-code start outcome '{outcome.GetType().Name}'.")
            };
            if (challenge == null)
            {
                // An invitation is accepted by its account: an unknown address signs up instead,
                // and an inactive one is refused.
                var missing = ((EmailCodeStartOutcome.Refused)outcome).Refusal == IdentityRefusals.InvitedAccountMissing;
                return View(await BuildViewModelAsync(
                    authorizationRequest,
                    missing ? "signup" : "login",
                    ((EmailCodeStartOutcome.Refused)outcome).Refusal.Message,
                    pendingToken: null,
                    email: email,
                    displayName: null,
                    fieldErrors: null,
                    organizationSelection: null,
                    info: null,
                    challengeToken: null,
                    cancellationToken: cancellationToken));
            }

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
            var outcome = await Processes.StartEmailOtpSignUp().ExecuteAsync(
                new StartEmailOtpSignUpCommand(
                    request.DisplayName,
                    email,
                    string.IsNullOrWhiteSpace(authorizationRequest.InvitationId) ? request.OrganizationName : null,
                    request.CustomFields,
                    new LoginTarget.AuthorizationRequest(authorizationRequest),
                    SqlOSHttpRequestContext.From(httpContext, SqlOSRequestSurface.Headless)),
                cancellationToken);
            var signup = outcome switch
            {
                EmailCodeSignUpStartOutcome.Sent sent => sent.Result,
                EmailCodeSignUpStartOutcome.Refused refused => throw refused.Refusal.ToException(),
                _ => throw new InvalidOperationException($"Unknown email-code sign-up start outcome '{outcome.GetType().Name}'.")
            };

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
            var outcome = await Processes.StartMagicLinkSignIn(httpContext).ExecuteAsync(
                new StartMagicLinkSignInCommand(
                    email,
                    new LoginTarget.AuthorizationRequest(authorizationRequest),
                    SqlOSHttpRequestContext.From(httpContext, SqlOSRequestSurface.Headless)),
                cancellationToken);
            var start = outcome switch
            {
                SignInLinkStartOutcome.Sent sent => sent.Result,
                SignInLinkStartOutcome.Refused refused => throw refused.Refusal.ToException(),
                _ => throw new InvalidOperationException($"Unknown sign-in link start outcome '{outcome.GetType().Name}'.")
            };

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
            var target = new LoginTarget.AuthorizationRequest(authorizationRequest);
            var outcome = await Processes.VerifyEmailOtpSignIn(httpContext).ExecuteAsync(
                new VerifyEmailOtpSignInCommand(request.ChallengeToken, request.Code, target, ChallengeBinding.For(target)),
                cancellationToken);
            var signedIn = outcome switch
            {
                EmailCodeSignInOutcome.SignedIn success => success,
                EmailCodeSignInOutcome.Refused refused => throw refused.Refusal.ToException(),
                _ => throw new InvalidOperationException($"Unknown email-code sign-in outcome '{outcome.GetType().Name}'.")
            };

            return await BuildCompletionActionResultAsync(
                authorizationRequest,
                ((LoginCompletion.AuthorizationRequestContinued)signedIn.Completion).Result,
                signedIn.Challenge.Email,
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
        var outcome = await Processes.CompleteMagicLinkSignIn(httpContext).ExecuteAsync(
            new CompleteMagicLinkSignInCommand(
                request.Token,
                new SignInLinkTarget.Headless(request.RequestId, request.InvitationToken)),
            cancellationToken);
        var signedIn = outcome switch
        {
            SignInLinkOutcome.SignedIn success => success,
            SignInLinkOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown sign-in link outcome '{outcome.GetType().Name}'.")
        };

        return await BuildCompletionActionResultAsync(
            ((LoginDestination.AuthorizationRequest)signedIn.Destination).Request,
            ((LoginCompletion.AuthorizationRequestContinued)signedIn.Completion).Result,
            signedIn.Email,
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

        try
        {
            var outcome = await Processes.CompleteEmailOtpSignUp(httpContext).ExecuteAsync(
                new CompleteEmailOtpSignUpCommand(
                    request.SignupToken,
                    request.ChallengeToken,
                    request.Code,
                    boundInvitation,
                    new LoginTarget.AuthorizationRequest(authorizationRequest),
                    SqlOSHttpRequestContext.From(httpContext, SqlOSRequestSurface.Headless)),
                cancellationToken);
            var signedUp = SignedUp(outcome);
            return await BuildCompletionActionResultAsync(
                authorizationRequest,
                ((LoginCompletion.AuthorizationRequestContinued)signedUp.Completion).Result,
                signedUp.Email,
                cancellationToken);
        }
        catch (SqlOSHeadlessValidationException ex)
        {
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
            var outcome = await Processes.StartPhoneOtpSignIn().ExecuteAsync(
                new StartPhoneOtpSignInCommand(
                    request.PhoneNumber,
                    new LoginTarget.AuthorizationRequest(authorizationRequest),
                    SqlOSHttpRequestContext.From(httpContext, SqlOSRequestSurface.Headless)),
                cancellationToken);
            var challenge = outcome switch
            {
                PhoneCodeStartOutcome.Sent sent => sent.Result,
                PhoneCodeStartOutcome.Refused refused => throw refused.Refusal.ToException(),
                _ => throw new InvalidOperationException($"Unknown phone-code start outcome '{outcome.GetType().Name}'.")
            };

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
            var target = new LoginTarget.AuthorizationRequest(authorizationRequest);
            var outcome = await Processes.VerifyPhoneOtpSignIn(httpContext).ExecuteAsync(
                new VerifyPhoneOtpSignInCommand(request.ChallengeToken, request.Code, target, ChallengeBinding.For(target)),
                cancellationToken);
            var signedIn = outcome switch
            {
                PhoneCodeSignInOutcome.SignedIn success => success,
                PhoneCodeSignInOutcome.Refused refused => throw refused.Refusal.ToException(),
                _ => throw new InvalidOperationException($"Unknown phone-code sign-in outcome '{outcome.GetType().Name}'.")
            };

            return await BuildCompletionActionResultAsync(
                authorizationRequest,
                ((LoginCompletion.AuthorizationRequestContinued)signedIn.Completion).Result,
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
            var outcome = await Processes.StartPhoneOtpSignUp().ExecuteAsync(
                new StartPhoneOtpSignUpCommand(
                    request.DisplayName,
                    request.PhoneNumber,
                    request.OrganizationName,
                    request.CustomFields,
                    new LoginTarget.AuthorizationRequest(authorizationRequest),
                    CarriesInvitation: await GetBoundInvitationOrNullAsync(authorizationRequest, cancellationToken) != null,
                    SqlOSHttpRequestContext.From(httpContext, SqlOSRequestSurface.Headless)),
                cancellationToken);
            var signup = outcome switch
            {
                PhoneCodeSignUpStartOutcome.Sent sent => sent.Result,
                PhoneCodeSignUpStartOutcome.Refused refused => throw refused.Refusal.ToException(),
                _ => throw new InvalidOperationException($"Unknown phone-code sign-up start outcome '{outcome.GetType().Name}'.")
            };

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
        var process = Processes.CompletePhoneOtpSignUp(httpContext);

        try
        {
            var outcome = await process.ExecuteAsync(
                new CompletePhoneOtpSignUpCommand(
                    request.SignupToken,
                    request.ChallengeToken,
                    request.Code,
                    CarriesInvitation: await GetBoundInvitationOrNullAsync(authorizationRequest, cancellationToken) != null,
                    new LoginTarget.AuthorizationRequest(authorizationRequest),
                    SqlOSHttpRequestContext.From(httpContext, SqlOSRequestSurface.Headless)),
                cancellationToken);
            var signedUp = SignedUp(outcome);
            return await BuildCompletionActionResultAsync(
                authorizationRequest,
                ((LoginCompletion.AuthorizationRequestContinued)signedUp.Completion).Result,
                email: null,
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
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
                phoneNumber: process.VerifiedPhoneNumber,
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
            throw IdentityRefusals.InvitationSignupNeedsEmailCodes.ToException();
        }

        var email = boundInvitation.Email;

        try
        {
            await _authorizationServerService.EnsureSignupAuthorizationContextAsync(authorizationRequest, cancellationToken);
            var ssoRedirect = await RedirectToSsoIfRequiredAsync(authorizationRequest, email, cancellationToken);
            if (ssoRedirect != null)
            {
                return ssoRedirect;
            }

            var outcome = await Processes.SignUpWithInvitation(httpContext).ExecuteAsync(
                new SignUpWithInvitationCommand(
                    request.DisplayName,
                    InvitationToken: null,
                    boundInvitation,
                    request.CustomFields,
                    new LoginTarget.AuthorizationRequest(authorizationRequest),
                    SqlOSHttpRequestContext.From(httpContext, SqlOSRequestSurface.Headless)),
                cancellationToken);
            return await BuildCompletionActionResultAsync(
                authorizationRequest,
                ((LoginCompletion.AuthorizationRequestContinued)SignedUp(outcome).Completion).Result,
                email,
                cancellationToken);
        }
        catch (SqlOSHeadlessValidationException ex)
        {
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

        try
        {
            await _authorizationServerService.EnsureSignupAuthorizationContextAsync(authorizationRequest, cancellationToken);
            var ssoRedirect = await RedirectToSsoIfRequiredAsync(authorizationRequest, email, cancellationToken);
            if (ssoRedirect != null)
            {
                return ssoRedirect;
            }

            var outcome = await Processes.SignUpWithPassword(httpContext).ExecuteAsync(
                new SignUpWithPasswordCommand(
                    request.DisplayName,
                    email,
                    request.Password,
                    boundInvitation == null ? request.OrganizationName : null,
                    boundInvitation == null ? authorizationRequest.OrganizationId : null,
                    boundInvitation,
                    request.CustomFields,
                    new LoginTarget.AuthorizationRequest(authorizationRequest),
                    SqlOSHttpRequestContext.From(httpContext, SqlOSRequestSurface.Headless)),
                cancellationToken);
            return await BuildCompletionActionResultAsync(
                authorizationRequest,
                ((LoginCompletion.AuthorizationRequestContinued)SignedUp(outcome).Completion).Result,
                email,
                cancellationToken);
        }
        catch (SqlOSHeadlessValidationException ex)
        {
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

    /// <summary>The sign-up's success, or its refusal as the 7.x exception the surface maps.</summary>
    private static SignUpOutcome.SignedUp SignedUp(SignUpOutcome outcome) => outcome switch
    {
        SignUpOutcome.SignedUp signedUp => signedUp,
        SignUpOutcome.Refused refused => throw refused.Refusal.ToException(),
        _ => throw new InvalidOperationException($"Unknown sign-up outcome '{outcome.GetType().Name}'.")
    };

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
        CancellationToken cancellationToken = default,
        string? consentToken = null,
        IReadOnlyList<SqlOSConsentScopeDisplay>? consentScopes = null)
    {
        if (consentScopes == null && string.Equals(NormalizeView(requestedView), "consent", StringComparison.Ordinal))
        {
            consentScopes = await SqlOSConsentService.BuildScopeDisplaysAsync(
                _context,
                SqlOSScopePolicy.Split(authorizationRequest.Scope),
                cancellationToken);
        }

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
        var allowedScopes = SqlOSAdminService.DeserializeJsonList(authorizationRequest.ClientApplication?.AllowedScopesJson);
        var requestWarning = SqlOSOpenIdScopeWarnings.ApplyRequestWarning(info, allowedScopes, authorizationRequest.Scope);

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
            requestWarning.Info,
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
            TotpEnrollment: totpEnrollment,
            Scope: authorizationRequest.Scope,
            OmittedOpenId: requestWarning.OmittedOpenId,
            ConsentToken: consentToken,
            ConsentScopes: consentScopes);
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
            "consent" => "consent",
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
        if (completion.RequiresConsent)
        {
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "consent",
                error: null,
                pendingToken: null,
                email: email,
                displayName: null,
                fieldErrors: null,
                organizationSelection: null,
                cancellationToken: cancellationToken,
                consentToken: completion.ConsentToken,
                consentScopes: completion.ConsentScopes));
        }

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

    /// <summary>
    /// Home-realm discovery for the request (<see cref="RouteToHomeRealm"/>): the redirect to the
    /// organization's identity provider, or null when the person signs in here.
    /// </summary>
    private async Task<SqlOSHeadlessActionResult?> RedirectToSsoIfRequiredAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        string email,
        CancellationToken cancellationToken)
        => await Processes.RouteToHomeRealm().ExecuteAsync(new RouteToHomeRealmCommand(authorizationRequest, email), cancellationToken)
            is HomeRealmRoute.IdentityProvider identityProvider
            ? Redirect(identityProvider.RedirectUrl)
            : null;

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

    private SqlOSIssuerSessionService RequireIssuerSessionService()
        => _issuerSessionService ?? throw new InvalidOperationException("Issuer session support is not configured.");

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
        var client = await _context.Set<SqlOSClientApplication>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ClientId == resolved.ClientId, cancellationToken);
        var requestWarning = SqlOSOpenIdScopeWarnings.ApplyRequestWarning(
            info,
            SqlOSAdminService.DeserializeJsonList(client?.AllowedScopesJson),
            resolved.Scope);

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
            Info: requestWarning.Info,
            FieldErrors: new Dictionary<string, string>(StringComparer.Ordinal),
            ChallengeToken: null,
            SignupToken: null,
            PendingToken: null,
            OrganizationSelection: organizationSelection ?? Array.Empty<SqlOSOrganizationOption>(),
            Providers: providers,
            Invitation: null,
            UiContext: uiContext,
            DeviceAuthorization: ToHeadlessDeviceAuthorization(resolved),
            Scope: resolved.Scope,
            OmittedOpenId: requestWarning.OmittedOpenId);
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
}

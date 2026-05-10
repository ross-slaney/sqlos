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

public sealed class SqlOSHeadlessAuthService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSAuthorizationServerService _authorizationServerService;
    private readonly SqlOSHomeRealmDiscoveryService _discoveryService;
    private readonly SqlOSOidcBrowserAuthService _oidcBrowserAuthService;
    private readonly SqlOSSamlService _samlService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly SqlOSEmailOtpService _emailOtpService;
    private readonly SqlOSInvitationService? _invitationService;
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
        SqlOSInvitationService? invitationService = null)
    {
        _context = context;
        _adminService = adminService;
        _authorizationServerService = authorizationServerService;
        _discoveryService = discoveryService;
        _oidcBrowserAuthService = oidcBrowserAuthService;
        _samlService = samlService;
        _settingsService = settingsService;
        _emailOtpService = emailOtpService;
        _invitationService = invitationService;
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
        JsonObject? uiContext)
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
                uiContext));
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
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(requestId, cancellationToken);
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
                allowUnverifiedEmailForInvitation: !string.IsNullOrWhiteSpace(authorizationRequest.InvitationId));
            var completion = await _authorizationServerService.CompleteAuthorizationRequestLoginAsync(
                authorizationRequest,
                authentication.User,
                authentication.AuthenticationMethod,
                httpContext,
                cancellationToken);

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

            return Redirect(completion.RedirectUrl!);
        }
        catch (InvalidOperationException ex)
        {
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "password",
                ex.Message,
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
                ex.Message,
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
                ex.Message,
                pendingToken: null,
                email: email,
                displayName: request.DisplayName,
                fieldErrors: null,
                organizationSelection: null,
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

            if (completion.RequiresOrganizationSelection)
            {
                return View(await BuildViewModelAsync(
                    authorizationRequest,
                    "organization",
                    error: null,
                    pendingToken: completion.PendingToken,
                    email: verification.Challenge.Email,
                    displayName: null,
                    fieldErrors: null,
                    organizationSelection: completion.Organizations,
                    info: null,
                    challengeToken: null,
                    cancellationToken: cancellationToken));
            }

            return Redirect(completion.RedirectUrl!);
        }
        catch (InvalidOperationException ex)
        {
            return View(await BuildViewModelAsync(
                authorizationRequest,
                "email-otp-verify",
                ex.Message,
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
                ?? authorizationRequest.OrganizationId
                ?? verification.OrganizationId
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

            var redirectUrl = await _authorizationServerService.IssueAuthorizationRedirectAsync(
                authorizationRequest,
                signup.User,
                selectedOrganizationId,
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

            return Redirect(redirectUrl);
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
                ex.Message,
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

    public async Task<SqlOSHeadlessActionResult> SignUpAsync(
        HttpContext httpContext,
        SqlOSHeadlessSignupRequest request,
        CancellationToken cancellationToken = default)
    {
        var authorizationRequest = await _authorizationServerService.GetRequiredAuthorizationRequestAsync(request.RequestId, cancellationToken);
        await BindInvitationIfPresentAsync(authorizationRequest, request.InvitationToken, cancellationToken);
        var boundInvitation = await GetBoundInvitationOrNullAsync(authorizationRequest, cancellationToken);
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
                ?? authorizationRequest.OrganizationId
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

            var redirectUrl = await _authorizationServerService.IssueAuthorizationRedirectAsync(
                authorizationRequest,
                signup.User,
                selectedOrganizationId,
                signup.AuthenticationMethod,
                httpContext,
                cancellationToken);

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            return Redirect(redirectUrl);
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
                ex.Message,
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
        => Redirect(await _authorizationServerService.CompletePendingOrganizationSelectionAsync(
            request.PendingToken,
            request.OrganizationId,
            httpContext,
            cancellationToken));

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
            ParseUiContext(authorizationRequest.UiContextJson));
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
        => string.Equals(requestedView, "signup", StringComparison.OrdinalIgnoreCase)
            ? "signup"
            : string.Equals(requestedView, "password", StringComparison.OrdinalIgnoreCase)
                ? "password"
                : string.Equals(requestedView, "email-otp", StringComparison.OrdinalIgnoreCase)
                    ? "email-otp"
                    : string.Equals(requestedView, "email-otp-verify", StringComparison.OrdinalIgnoreCase)
                        ? "email-otp-verify"
                        : string.Equals(requestedView, "email-otp-signup-verify", StringComparison.OrdinalIgnoreCase)
                            ? "email-otp-signup-verify"
                            : string.Equals(requestedView, "invite", StringComparison.OrdinalIgnoreCase)
                                ? "invite"
                                : string.Equals(requestedView, "invite-login", StringComparison.OrdinalIgnoreCase)
                                    ? "invite-login"
                                    : string.Equals(requestedView, "invite-email-otp-verify", StringComparison.OrdinalIgnoreCase)
                                        ? "invite-email-otp-verify"
                                        : string.Equals(requestedView, "invite-accepted", StringComparison.OrdinalIgnoreCase)
                                            ? "invite-accepted"
                                            : string.Equals(requestedView, "organization", StringComparison.OrdinalIgnoreCase)
                                                ? "organization"
                                                : string.Equals(requestedView, "logged-out", StringComparison.OrdinalIgnoreCase)
                                                    ? "logged-out"
                                                    : "login";

    private static SqlOSHeadlessActionResult Redirect(string url)
        => new("redirect", url, null);

    private static SqlOSHeadlessActionResult View(SqlOSHeadlessViewModel viewModel)
        => new("view", null, viewModel);

    private static string ResolvePreferredLocalView(SqlOSResolvedCredentialSettings credentialSettings)
    {
        if (credentialSettings.EmailOtpEnabled)
        {
            return "email-otp";
        }

        if (credentialSettings.PasswordEnabled)
        {
            return "password";
        }

        return "login";
    }

    private bool SupportsDatabaseTransactions()
        => !string.Equals(_context.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal);

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
}

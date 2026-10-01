using Microsoft.AspNetCore.Http;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Processes.Identity;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Builds the identity processes (<c>SqlOS.AuthServer.Processes.Identity</c>) for a request: every
/// surface that runs an identity flow gets its process here, so the hosted AuthPage, the headless
/// API and the public facades run the same implementation with the same collaborators.
/// </summary>
/// <remarks>
/// <para>
/// The hosted endpoints resolve it from dependency injection. The public facades
/// (<see cref="SqlOSAuthService"/>, <see cref="SqlOSHeadlessAuthService"/> and the code and link
/// services) build one from their own dependencies, because hosts and tests construct them by hand
/// with the 7.x constructors. A collaborator a facade was built without stays unset, and a process
/// that needs it fails as the facade did ("… is not registered").
/// </para>
/// <para>
/// A process that completes a login gets the temporary hub adapter
/// (<see cref="SqlOSHttpLoginCompletion"/>) bound to the request's <see cref="HttpContext"/>; the
/// processes themselves never see the HTTP request (architecture rule 7). Each process uses the
/// admission gate of the service that owned its flow in 7.2.1, so in-memory admission stores keep
/// their 7.x lifetime.
/// </para>
/// </remarks>
internal sealed class SqlOSIdentityProcesses
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly SqlOSAuthServerOptions _options;

    public SqlOSIdentityProcesses(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSCryptoService cryptoService,
        SqlOSSettingsService settingsService,
        SqlOSAuthServerOptions options)
    {
        _context = context;
        _adminService = adminService;
        _cryptoService = cryptoService;
        _settingsService = settingsService;
        _options = options;
    }

    /// <summary>The admission gate password sign-ins pass (the password-login buckets).</summary>
    public IAdmissionGate? PasswordAdmission { get; init; }

    /// <summary>The admission gate password-reset emails pass (the delivery buckets).</summary>
    public IAdmissionGate? PasswordResetAdmission { get; init; }

    /// <summary>The password-reset email channel: the reset options and the delivery.</summary>
    public SqlOSPasswordResetDelivery? PasswordResetEmails { get; init; }

    /// <summary>The email-verification channel.</summary>
    public SqlOSEmailVerificationDelivery? VerificationEmails { get; init; }

    public SqlOSAuthorizationServerService? AuthorizationServer { get; init; }

    public SqlOSAuthService? Auth { get; init; }

    public SqlOSIssuerSessionService? IssuerSessions { get; init; }

    public SqlOSInvitationService? Invitations { get; init; }

    public SqlOSHomeRealmDiscoveryService? HomeRealms { get; init; }

    public SqlOSSamlService? Saml { get; init; }

    /// <summary>The email-code channel: options, admission, attempts and delivery.</summary>
    public SqlOSEmailOtpService? EmailCodes { get; init; }

    /// <summary>The sign-in link channel: options, admission and delivery.</summary>
    public SqlOSMagicLinkService? SignInLinks { get; init; }

    /// <summary>The phone-code channel: options, admission and the delivery provider.</summary>
    public SqlOSPhoneOtpService? PhoneCodes { get; init; }

    public SignInWithPassword SignInWithPassword(HttpContext? httpContext)
        => new(
            _context,
            _settingsService,
            _adminService,
            PasswordAdmission ?? throw new InvalidOperationException("The password-login admission gate is not configured."),
            Hub(httpContext),
            _options,
            _cryptoService.Clock);

    public StartEmailOtpSignIn StartEmailOtpSignIn()
        => new(_context, _settingsService, _adminService, RequireEmailCodes(), _cryptoService.Clock);

    public VerifyEmailOtpSignIn VerifyEmailOtpSignIn(HttpContext? httpContext)
        => new(_context, _settingsService, RequireEmailCodes(), Hub(httpContext), _cryptoService.Clock);

    public StartEmailOtpSignUp StartEmailOtpSignUp()
        => new(_context, _settingsService, _adminService, RequireEmailCodes(), _cryptoService.Clock);

    public StartMagicLinkSignIn StartMagicLinkSignIn(HttpContext? httpContext)
    {
        var links = RequireSignInLinks();
        return new(_context, _settingsService, _adminService, links, links.Delivery(httpContext), _cryptoService.Clock);
    }

    public CompleteMagicLinkSignIn CompleteMagicLinkSignIn(HttpContext? httpContext)
        => new(_context, _settingsService, RequireSignInLinks(), Invitations, Hub(httpContext), _cryptoService.Clock);

    public StartPhoneOtpSignIn StartPhoneOtpSignIn()
        => new(_context, _settingsService, _adminService, RequirePhoneCodes(), _cryptoService.Clock);

    public VerifyPhoneOtpSignIn VerifyPhoneOtpSignIn(HttpContext? httpContext)
        => new(_context, _settingsService, RequirePhoneCodes(), Hub(httpContext), _cryptoService.Clock);

    public StartPhoneOtpSignUp StartPhoneOtpSignUp()
        => new(_context, _settingsService, _adminService, RequirePhoneCodes(), _cryptoService.Clock);

    public SignUpWithPassword SignUpWithPassword(HttpContext? httpContext)
    {
        var hub = Hub(httpContext);
        return new(_context, _settingsService, _adminService, hub, hub, _cryptoService.Clock);
    }

    public CompleteEmailOtpSignUp CompleteEmailOtpSignUp(HttpContext? httpContext)
    {
        var hub = Hub(httpContext);
        return new(_context, _settingsService, _adminService, RequireEmailCodes(), hub, hub, _cryptoService.Clock);
    }

    public CompletePhoneOtpSignUp CompletePhoneOtpSignUp(HttpContext? httpContext)
    {
        var hub = Hub(httpContext);
        return new(_context, _settingsService, _adminService, _cryptoService, RequirePhoneCodes(), hub, hub, _cryptoService.Clock);
    }

    public SignUpWithInvitation SignUpWithInvitation(HttpContext? httpContext)
    {
        var hub = Hub(httpContext);
        return new(_context, _settingsService, _adminService, Invitations, hub, hub, _cryptoService.Clock);
    }

    public RequestPasswordReset RequestPasswordReset()
        => new(
            _context,
            _settingsService,
            PasswordResetAdmission ?? throw new InvalidOperationException("The password-reset admission gate is not configured."),
            PasswordResetLinks(),
            RequirePasswordResetEmails(),
            AuditRecorder(),
            _cryptoService.Clock);

    public SendPasswordResetEmail SendPasswordResetEmail()
        => new(_context, _settingsService, PasswordResetLinks(), _cryptoService.Clock);

    public IssuePasswordResetToken IssuePasswordResetToken()
        => new(_context, _settingsService, PasswordResetLinks(), _cryptoService.Clock);

    public ResetPassword ResetPassword()
        => new(_context, _settingsService, AuditRecorder(), _cryptoService.Clock);

    public RequestEmailVerification RequestEmailVerification()
        => new(
            _context,
            VerificationEmails ?? throw new InvalidOperationException("The email-verification delivery is not configured."),
            AuditRecorder(),
            _cryptoService.Clock);

    public IssueEmailVerificationToken IssueEmailVerificationToken()
        => new(_context, _cryptoService.Clock);

    public VerifyEmail VerifyEmail()
        => new(_context, _cryptoService.Clock);

    public CreateUser CreateUser()
        => new(_context, _cryptoService.Clock);

    public RouteToHomeRealm RouteToHomeRealm()
        => new(
            _context,
            HomeRealms ?? throw new InvalidOperationException("Home-realm discovery is not configured."),
            Saml ?? throw new InvalidOperationException("SAML is not configured."));

    private PasswordResetLinks PasswordResetLinks()
        => new(_context, RequirePasswordResetEmails(), AuditRecorder());

    private SqlOSPasswordResetDelivery RequirePasswordResetEmails()
        => PasswordResetEmails ?? throw new InvalidOperationException("The password-reset delivery is not configured.");

    /// <summary>Records the failures and outcomes a process audits without a state change, in its unit of work.</summary>
    private IAuditRecorder AuditRecorder() => new SqlOSAuditRecorder(_context);

    private SqlOSPhoneOtpService RequirePhoneCodes()
        => PhoneCodes ?? throw new InvalidOperationException("Phone OTP service is not registered.");

    private SqlOSMagicLinkService RequireSignInLinks()
        => SignInLinks ?? throw new InvalidOperationException("Magic-link service is not registered.");

    private SqlOSEmailOtpService RequireEmailCodes()
        => EmailCodes ?? throw new InvalidOperationException("Email OTP service is not registered.");

    /// <summary>The hub adapter for <paramref name="httpContext"/>, or one that refuses to complete without a request.</summary>
    private SqlOSHttpLoginCompletion Hub(HttpContext? httpContext)
        => new(
            httpContext,
            _adminService,
            _options,
            AuthorizationServer,
            Auth,
            IssuerSessions,
            Invitations);
}

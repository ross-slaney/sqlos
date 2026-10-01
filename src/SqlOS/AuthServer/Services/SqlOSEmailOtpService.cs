using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Processes.Identity;
using SqlOS.Domain;
using SqlOS.Domain.Events;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;
using SqlOS.Email.Models;
using SqlOS.Email.Services;
using SqlOS.Hosting;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSEmailOtpService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly ISqlOSAuthEmailSender _emailSender;
    private readonly ISqlOSTransactionalEmailService? _transactionalEmailService;
    private readonly IAuditRecorder _auditRecorder;
    private readonly SqlOSEmailOtpAttemptLedger _attempts;
    private readonly IAdmissionGate _admission;
    private readonly SqlOSAuthServerOptions _authOptions;
    private readonly SqlOSEmailOtpOptions _options;

    public SqlOSEmailOtpService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSCryptoService cryptoService,
        SqlOSSettingsService settingsService,
        ISqlOSAuthEmailSender emailSender,
        IOptions<SqlOSAuthServerOptions> options,
        ISqlOSTransactionalEmailService? transactionalEmailService = null)
    {
        _context = context;
        _adminService = adminService;
        _cryptoService = cryptoService;
        _settingsService = settingsService;
        _emailSender = emailSender;
        _transactionalEmailService = transactionalEmailService;
        _auditRecorder = new SqlOSAuditRecorder(context);
        _attempts = new SqlOSEmailOtpAttemptLedger(context);
        _admission = SqlOSAdmissionGate.Create(context, adminService, cryptoService, options);
        _authOptions = options.Value;
        _options = options.Value.EmailOtp;
    }

    public bool IsRuntimeConfigured => _options.BuildMessage == null || _emailSender.IsConfigured;

    /// <summary>The email-code options: code length, attempts, lifetimes and cooldown.</summary>
    internal SqlOSEmailOtpOptions Options => _options;

    /// <summary>The admission gate email codes pass (the delivery buckets, #424).</summary>
    internal IAdmissionGate Admission => _admission;

    /// <summary>Spends email-code attempts in the database before a code is compared (#424).</summary>
    internal SqlOSEmailOtpAttemptLedger Attempts => _attempts;

    /// <summary>Records the failures email codes audit without a state change.</summary>
    internal IAuditRecorder AuditRecorder => _auditRecorder;

    /// <summary>The identity processes the email-code facades below delegate to.</summary>
    private SqlOSIdentityProcesses Processes => new(_context, _adminService, _cryptoService, _settingsService, _authOptions)
    {
        EmailCodes = this
    };

    public async Task<SqlOSEmailOtpStartResult> StartForAuthorizationRequestAsync(
        SqlOSAuthorizationRequest? authorizationRequest,
        string email,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => Started(await Processes.StartEmailOtpSignIn().ExecuteAsync(
            new StartEmailOtpSignInCommand(
                email,
                LoginTarget.ForBrowser(authorizationRequest, invitationToken: null),
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.Hosted)),
            cancellationToken));

    public async Task<SqlOSEmailOtpSignupStartResult> StartSignupForAuthorizationRequestAsync(
        SqlOSAuthorizationRequest? authorizationRequest,
        string displayName,
        string email,
        string? organizationName,
        JsonObject? customFields = null,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => Started(await Processes.StartEmailOtpSignUp().ExecuteAsync(
            new StartEmailOtpSignUpCommand(
                displayName,
                email,
                organizationName,
                customFields,
                LoginTarget.ForBrowser(authorizationRequest, invitationToken: null),
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.Hosted)),
            cancellationToken));

    public async Task<SqlOSEmailOtpSignupStartResult> StartSignupForClientAsync(
        SqlOSEmailOtpSignupStartRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => Started(await Processes.StartEmailOtpSignUp().ExecuteAsync(
            new StartEmailOtpSignUpCommand(
                request.DisplayName,
                request.Email,
                request.OrganizationName,
                request.CustomFields,
                new LoginTarget.DirectLogin(request.ClientId, request.OrganizationId),
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.PublicApi)),
            cancellationToken));

    public async Task<SqlOSEmailOtpStartResult> StartForClientAsync(
        SqlOSEmailOtpStartRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => Started(await Processes.StartEmailOtpSignIn().ExecuteAsync(
            new StartEmailOtpSignInCommand(
                request.Email,
                new LoginTarget.DirectLogin(request.ClientId, request.OrganizationId),
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.PublicApi)),
            cancellationToken));

    public async Task<SqlOSEmailOtpVerificationResult> VerifyAsync(
        SqlOSEmailOtpVerifyRequest request,
        CancellationToken cancellationToken = default)
        => await VerifyAsync(
            request,
            expectedAuthorizationRequestId: null,
            requireAuthorizationRequestMatch: false,
            cancellationToken);

    /// <summary>
    /// Verifies a sign-in code without completing the sign-in: the email-code sign-in
    /// (<see cref="VerifyEmailOtpSignIn"/>) with only its credential.
    /// </summary>
    public async Task<SqlOSEmailOtpVerificationResult> VerifyAsync(
        SqlOSEmailOtpVerifyRequest request,
        string? expectedAuthorizationRequestId,
        bool requireAuthorizationRequestMatch,
        CancellationToken cancellationToken = default)
    {
        var outcome = await Processes.VerifyEmailOtpSignIn(httpContext: null).ExecuteAsync(
            new VerifyEmailOtpSignInCommand(
                request.ChallengeToken,
                request.Code,
                LoginTarget.CredentialOnly.Instance,
                new ChallengeBinding(expectedAuthorizationRequestId, requireAuthorizationRequestMatch)),
            cancellationToken);
        var signedIn = outcome switch
        {
            EmailCodeSignInOutcome.SignedIn success => success,
            EmailCodeSignInOutcome.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown email-code sign-in outcome '{outcome.GetType().Name}'.")
        };

        var organizations = await _adminService.GetUserOrganizationsAsync(signedIn.Evidence.UserId, cancellationToken);
        return new SqlOSEmailOtpVerificationResult(signedIn.Challenge, signedIn.Evidence.User, organizations, signedIn.Evidence.AuthenticationMethod);
    }

    /// <summary>
    /// Verifies an email-code sign-up's token and code without creating the account (the step the
    /// email-code sign-up runs inside its transaction).
    /// </summary>
    public async Task<SqlOSEmailOtpSignupVerificationResult> VerifySignupAsync(
        SqlOSEmailOtpSignupVerifyRequest request,
        string? expectedAuthorizationRequestId,
        bool requireAuthorizationRequestMatch,
        CancellationToken cancellationToken = default)
    {
        if (!(await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken)).EmailOtpEnabled)
        {
            throw IdentityRefusals.EmailCodesUnavailable.ToException();
        }

        var check = await EmailOtpSignupTokens.VerifyAsync(
            _context,
            this,
            request.SignupToken,
            request.ChallengeToken,
            request.Code,
            new ChallengeBinding(expectedAuthorizationRequestId, requireAuthorizationRequestMatch),
            _cryptoService.Clock.GetUtcNow().UtcDateTime,
            cancellationToken);
        return check switch
        {
            EmailOtpSignupCheck.Verified verified => verified.Result,
            EmailOtpSignupCheck.Refused refused => throw refused.Refusal.ToException(),
            _ => throw new InvalidOperationException($"Unknown email-code sign-up check '{check.GetType().Name}'.")
        };
    }

    public async Task ConsumeSignupTokenAsync(
        string signupToken,
        CancellationToken cancellationToken = default)
    {
        var rawSignupToken = signupToken?.Trim()
            ?? throw IdentityRefusals.InvalidCode.ToException();
        _ = await _cryptoService.ConsumeTemporaryTokenAsync(SqlOSTemporaryTokenKinds.EmailOtpSignup, rawSignupToken, cancellationToken)
            ?? throw IdentityRefusals.InvalidCode.ToException();
    }

    private static SqlOSEmailOtpStartResult Started(EmailCodeStartOutcome outcome) => outcome switch
    {
        EmailCodeStartOutcome.Sent sent => sent.Result,
        EmailCodeStartOutcome.Refused refused => throw refused.Refusal.ToException(),
        _ => throw new InvalidOperationException($"Unknown email-code start outcome '{outcome.GetType().Name}'.")
    };

    private static SqlOSEmailOtpSignupStartResult Started(EmailCodeSignUpStartOutcome outcome) => outcome switch
    {
        EmailCodeSignUpStartOutcome.Sent sent => sent.Result,
        EmailCodeSignUpStartOutcome.Refused refused => throw refused.Refusal.ToException(),
        _ => throw new InvalidOperationException($"Unknown email-code sign-up start outcome '{outcome.GetType().Name}'.")
    };

    /// <summary>Sends the code to the challenge's stored recipient, the only address it ever goes to.</summary>
    internal async Task SendCodeAsync(
        IssuedEmailOtpChallenge issued,
        string purpose,
        CancellationToken cancellationToken)
    {
        var challenge = issued.Challenge;
        var context = await BuildMessageContextAsync(
            challenge.Email,
            Masked.Email(challenge.Email),
            issued.Code,
            challenge.ExpiresAt,
            purpose,
            cancellationToken);
        if (_options.BuildMessage != null)
        {
            await _emailSender.SendAsync(BuildLegacyMessage(context), cancellationToken);
            return;
        }

        var transactionalEmailService = _transactionalEmailService
            ?? throw new InvalidOperationException("Transactional email service is not registered.");
        var result = await transactionalEmailService.SendAsync(
            new SqlOSSendEmailRequest(
                SqlOSBuiltInEmailTemplates.AuthEmailOtpKey,
                challenge.Email,
                BuildTemplateVariables(context),
                IdempotencyKey: $"auth-email-otp:{challenge.Id}"),
            cancellationToken);

        if (string.Equals(result.Status, SqlOSEmailDeliveryStatuses.Failed, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(result.SanitizedError ?? "Email OTP delivery failed.");
        }
    }

    private async Task<SqlOSEmailOtpMessageContext> BuildMessageContextAsync(
        string email,
        string maskedEmail,
        string code,
        DateTime expiresAt,
        string purpose,
        CancellationToken cancellationToken)
    {
        var branding = await _settingsService.GetResolvedAuthEmailBrandingAsync(cancellationToken);
        var applicationName = string.IsNullOrWhiteSpace(branding.ApplicationName)
            ? string.IsNullOrWhiteSpace(_options.ApplicationName)
                ? "SqlOS"
                : _options.ApplicationName.Trim()
            : branding.ApplicationName;
        var context = new SqlOSEmailOtpMessageContext(
            purpose,
            email,
            maskedEmail,
            code,
            expiresAt,
            _options.ChallengeLifetime,
            applicationName)
        {
            Branding = branding with { ApplicationName = applicationName }
        };

        return context;
    }

    private SqlOSAuthEmailMessage BuildLegacyMessage(SqlOSEmailOtpMessageContext context)
    {
        var defaultSubject = context.Purpose == "signup"
            ? $"Your {context.ApplicationName} sign-up code"
            : $"Your {context.ApplicationName} sign-in code";
        var subject = string.Equals(_options.Subject, "Your SqlOS sign-in code", StringComparison.Ordinal)
            ? defaultSubject
            : _options.Subject;

        return _options.BuildMessage?.Invoke(context)
            ?? new SqlOSAuthEmailMessage(
                context.Email,
                subject,
                SqlOSAuthEmailTemplateRenderer.BuildOtpHtmlBody(context),
                SqlOSAuthEmailTemplateRenderer.BuildOtpTextBody(context));
    }

    private static IReadOnlyDictionary<string, object?> BuildTemplateVariables(SqlOSEmailOtpMessageContext context)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(context.ChallengeLifetime.TotalMinutes));
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["applicationName"] = context.ApplicationName,
            ["logoBase64"] = context.Branding.LogoBase64 ?? string.Empty,
            ["logoImageDisplay"] = string.IsNullOrWhiteSpace(context.Branding.LogoBase64) ? "none" : "block",
            ["logoTextDisplay"] = string.IsNullOrWhiteSpace(context.Branding.LogoBase64) ? "block" : "none",
            ["purposeLabel"] = context.Purpose == "signup" ? "sign-up" : "sign-in",
            ["heading"] = context.Purpose == "signup" ? "Your sign-up code" : "Your sign-in code",
            ["action"] = context.Purpose == "signup" ? "creating your account" : "signing in",
            ["maskedEmail"] = context.MaskedEmail,
            ["code"] = context.Code,
            ["expiresInMinutes"] = minutes,
            ["primaryColor"] = context.Branding.PrimaryColor,
            ["accentColor"] = context.Branding.AccentColor,
            ["backgroundColor"] = context.Branding.BackgroundColor
        };
    }
}

public sealed record SqlOSEmailOtpVerificationResult(
    SqlOSEmailOtpChallenge Challenge,
    SqlOSUser User,
    IReadOnlyList<SqlOSOrganizationOption> Organizations,
    string AuthenticationMethod);

public sealed record SqlOSEmailOtpSignupVerificationResult(
    string SignupToken,
    string? ClientApplicationId,
    string? ClientId,
    string DisplayName,
    string Email,
    string? OrganizationName,
    string? OrganizationId,
    JsonObject? CustomFields);

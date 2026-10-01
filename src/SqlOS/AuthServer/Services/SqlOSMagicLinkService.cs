using System.Net;
using System.Text.Json;
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

public sealed class SqlOSMagicLinkService
{
    public const string TokenPurpose = SqlOSTemporaryTokenKinds.Purposes.MagicLink;

    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly ISqlOSAuthEmailSender _emailSender;
    private readonly ISqlOSTransactionalEmailService? _transactionalEmailService;
    private readonly IAuditRecorder _auditRecorder;
    private readonly IAdmissionGate _admission;
    private readonly SqlOSAuthServerOptions _authOptions;
    private readonly SqlOSMagicLinkOptions _options;

    public SqlOSMagicLinkService(
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
        _admission = SqlOSAdmissionGate.Create(context, adminService, cryptoService, options);
        _authOptions = options.Value;
        _options = options.Value.MagicLink;
    }

    public bool IsRuntimeConfigured => _options.BuildMessage == null || _emailSender.IsConfigured;

    /// <summary>The sign-in link options: lifetime, cooldown and limits.</summary>
    internal SqlOSMagicLinkOptions Options => _options;

    /// <summary>The admission gate sign-in links pass (the delivery buckets, #424).</summary>
    internal IAdmissionGate Admission => _admission;

    /// <summary>Records the failures sign-in links audit without a state change.</summary>
    internal IAuditRecorder AuditRecorder => _auditRecorder;

    /// <summary>Delivers sign-in links for the request <paramref name="httpContext"/> serves.</summary>
    internal SqlOSSignInLinkDelivery Delivery(HttpContext? httpContext) => new(this, httpContext);

    /// <summary>The identity processes the sign-in link facades below delegate to.</summary>
    private SqlOSIdentityProcesses Processes => new(_context, _adminService, _cryptoService, _settingsService, _authOptions)
    {
        SignInLinks = this
    };

    public async Task<SqlOSMagicLinkStartResult> StartForAuthorizationRequestAsync(
        SqlOSAuthorizationRequest? authorizationRequest,
        string email,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => Started(await Processes.StartMagicLinkSignIn(httpContext).ExecuteAsync(
            new StartMagicLinkSignInCommand(
                email,
                LoginTarget.ForBrowser(authorizationRequest, invitationToken: null),
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.Hosted)),
            cancellationToken));

    public async Task<SqlOSMagicLinkStartResult> StartForClientAsync(
        SqlOSMagicLinkStartRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
        => Started(await Processes.StartMagicLinkSignIn(httpContext).ExecuteAsync(
            new StartMagicLinkSignInCommand(
                request.Email,
                new LoginTarget.DirectLogin(request.ClientId, request.OrganizationId),
                SqlOSHttpRequestContext.FromOptional(httpContext, SqlOSRequestSurface.PublicApi)),
            cancellationToken));

    /// <summary>
    /// Sends a sign-in link to its stored recipient: through the host's message builder when it
    /// configured one, else as the built-in transactional email.
    /// </summary>
    internal async Task SendLinkAsync(
        MagicLinkPayload payload,
        string rawToken,
        DateTime expiresAt,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        var context = await BuildMessageContextAsync(payload.Email, Masked.Email(payload.Email), rawToken, expiresAt, httpContext, cancellationToken);
        await SendEmailAsync(context, rawToken, cancellationToken);
    }

    private static SqlOSMagicLinkStartResult Started(SignInLinkStartOutcome outcome) => outcome switch
    {
        SignInLinkStartOutcome.Sent sent => sent.Result,
        SignInLinkStartOutcome.Refused refused => throw refused.Refusal.ToException(),
        _ => throw new InvalidOperationException($"Unknown sign-in link start outcome '{outcome.GetType().Name}'.")
    };

    private async Task SendEmailAsync(
        SqlOSMagicLinkMessageContext context,
        string rawToken,
        CancellationToken cancellationToken)
    {
        if (_options.BuildMessage != null)
        {
            if (!_emailSender.IsConfigured)
            {
                throw new InvalidOperationException("Auth email delivery is not configured.");
            }

            await _emailSender.SendAsync(BuildLegacyMessage(context), cancellationToken);
            return;
        }

        var transactionalEmailService = _transactionalEmailService
            ?? throw new InvalidOperationException("Transactional email service is not registered.");
        var result = await transactionalEmailService.SendAsync(
            new SqlOSSendEmailRequest(
                SqlOSBuiltInEmailTemplates.AuthMagicLinkKey,
                context.Email,
                BuildTemplateVariables(context),
                IdempotencyKey: $"auth-magic-link:{_cryptoService.HashToken(rawToken)[..32]}"),
            cancellationToken);

        if (string.Equals(result.Status, SqlOSEmailDeliveryStatuses.Failed, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(result.SanitizedError ?? "Magic-link email delivery failed.");
        }
    }

    private async Task<SqlOSMagicLinkMessageContext> BuildMessageContextAsync(
        string email,
        string maskedEmail,
        string rawToken,
        DateTime expiresAt,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        var branding = await _settingsService.GetResolvedAuthEmailBrandingAsync(cancellationToken);
        var applicationName = string.IsNullOrWhiteSpace(branding.ApplicationName)
            ? string.IsNullOrWhiteSpace(_options.ApplicationName)
                ? "SqlOS"
                : _options.ApplicationName.Trim()
            : branding.ApplicationName;
        var loginUrl = _options.BuildLoginUrl?.Invoke(
            new SqlOSMagicLinkUrlContext(
                rawToken,
                email,
                maskedEmail,
                expiresAt,
                _options.TokenLifetime,
                httpContext))
            ?? BuildLoginUrl(rawToken);

        return new SqlOSMagicLinkMessageContext(
            applicationName,
            email,
            maskedEmail,
            loginUrl,
            expiresAt,
            _options.TokenLifetime)
        {
            Branding = branding with { ApplicationName = applicationName }
        };
    }

    private SqlOSAuthEmailMessage BuildLegacyMessage(SqlOSMagicLinkMessageContext context)
        => _options.BuildMessage?.Invoke(context)
            ?? new SqlOSAuthEmailMessage(
                context.Email,
                ResolveSubject(context.ApplicationName),
                SqlOSAuthEmailTemplateRenderer.BuildMagicLinkHtmlBody(context),
                SqlOSAuthEmailTemplateRenderer.BuildMagicLinkTextBody(context));

    private string ResolveSubject(string applicationName)
        => string.IsNullOrWhiteSpace(_options.Subject)
            ? $"Sign in to {applicationName}"
            : _options.Subject.Replace("{applicationName}", applicationName, StringComparison.Ordinal);

    private string BuildLoginUrl(string rawToken)
        => $"{GetPublicOrigin()}{_authOptions.BasePath.TrimEnd('/')}/login/magic-link/complete?token={Uri.EscapeDataString(rawToken)}";

    private string GetPublicOrigin()
    {
        if (!string.IsNullOrWhiteSpace(_authOptions.PublicOrigin))
        {
            return _authOptions.PublicOrigin.TrimEnd('/');
        }

        return _authOptions.Issuer.TrimEnd('/').EndsWith(_authOptions.BasePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            ? _authOptions.Issuer.TrimEnd('/')[..^_authOptions.BasePath.TrimEnd('/').Length]
            : _authOptions.Issuer.TrimEnd('/');
    }

    private static IReadOnlyDictionary<string, object?> BuildTemplateVariables(SqlOSMagicLinkMessageContext context)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(context.TokenLifetime.TotalMinutes));
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["applicationName"] = context.ApplicationName,
            ["logoBase64"] = context.Branding.LogoBase64 ?? string.Empty,
            ["logoImageDisplay"] = string.IsNullOrWhiteSpace(context.Branding.LogoBase64) ? "none" : "block",
            ["logoTextDisplay"] = string.IsNullOrWhiteSpace(context.Branding.LogoBase64) ? "block" : "none",
            ["maskedEmail"] = context.MaskedEmail,
            ["loginUrl"] = context.LoginUrl,
            ["expiresInMinutes"] = minutes,
            ["primaryColor"] = context.Branding.PrimaryColor,
            ["accentColor"] = context.Branding.AccentColor,
            ["backgroundColor"] = context.Branding.BackgroundColor
        };
    }
}

/// <summary>
/// Delivers sign-in links for one request: the host's link builder
/// (<see cref="SqlOSMagicLinkOptions.BuildLoginUrl"/>) receives that request.
/// </summary>
internal sealed class SqlOSSignInLinkDelivery(SqlOSMagicLinkService links, HttpContext? httpContext)
{
    public Task SendAsync(MagicLinkPayload payload, string rawToken, DateTime expiresAt, CancellationToken cancellationToken)
        => links.SendLinkAsync(payload, rawToken, expiresAt, httpContext, cancellationToken);
}

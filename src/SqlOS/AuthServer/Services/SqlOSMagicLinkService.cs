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
using SqlOS.Domain;
using SqlOS.Domain.Events;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;
using SqlOS.Email.Models;
using SqlOS.Email.Services;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSMagicLinkService
{
    public const string TokenPurpose = SqlOSTemporaryTokenKinds.Purposes.MagicLink;
    private const string InvalidLinkMessage = "The sign-in link is invalid or expired.";

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

    public async Task<SqlOSMagicLinkStartResult> StartForAuthorizationRequestAsync(
        SqlOSAuthorizationRequest? authorizationRequest,
        string email,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureMagicLinkEnabledAsync(cancellationToken);

        if (authorizationRequest != null)
        {
            authorizationRequest.LoginHintEmail = email.Trim();
            await _context.SaveChangesAsync(cancellationToken);
        }

        return await CreateLinkAsync(
            email,
            authorizationRequestId: authorizationRequest?.Id,
            clientApplicationId: authorizationRequest?.ClientApplicationId,
            requestedOrganizationId: null,
            httpContext,
            cancellationToken);
    }

    public async Task<SqlOSMagicLinkStartResult> StartForClientAsync(
        SqlOSMagicLinkStartRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureMagicLinkEnabledAsync(cancellationToken);

        var client = await _adminService.RequireClientAsync(request.ClientId, null, cancellationToken);
        await SqlOSDirectLoginPolicy.EnsureFirstPartyAsync(_adminService, client, httpContext, userId: null, cancellationToken);
        return await CreateLinkAsync(
            request.Email,
            authorizationRequestId: null,
            clientApplicationId: client.Id,
            requestedOrganizationId: request.OrganizationId,
            httpContext,
            cancellationToken);
    }

    internal async Task<SqlOSMagicLinkVerificationResult> CompleteAsync(
        SqlOSMagicLinkCompleteRequest request,
        string? expectedAuthorizationRequestId,
        bool requireAuthorizationRequestMatch,
        CancellationToken cancellationToken = default)
    {
        await EnsureMagicLinkEnabledAsync(cancellationToken);

        var rawToken = request.Token?.Trim()
            ?? throw new InvalidOperationException(InvalidLinkMessage);
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            throw new InvalidOperationException(InvalidLinkMessage);
        }

        var token = await _cryptoService.FindTemporaryTokenAsync(SqlOSTemporaryTokenKinds.MagicLink, rawToken, cancellationToken);
        if (token == null)
        {
            _auditRecorder.Record(new MagicLinkNotFound());
            await _context.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(InvalidLinkMessage);
        }

        var payload = token.ReadPayload(SqlOSTemporaryTokenKinds.MagicLink)
            ?? throw new InvalidOperationException(InvalidLinkMessage);

        ValidateBinding(token, payload, expectedAuthorizationRequestId, requireAuthorizationRequestMatch);

        var consumed = await _cryptoService.ConsumeTemporaryTokenAsync(SqlOSTemporaryTokenKinds.MagicLink, rawToken, cancellationToken);
        if (consumed == null)
        {
            _auditRecorder.Record(new MagicLinkReplayed(
                payload.MaskedEmail,
                payload.IpAddress,
                payload.ClientApplicationId,
                payload.AuthorizationRequestId));
            await _context.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(InvalidLinkMessage);
        }

        if (string.IsNullOrWhiteSpace(consumed.UserId))
        {
            throw new InvalidOperationException(InvalidLinkMessage);
        }

        var user = await _context.Set<SqlOSUser>()
            .FirstOrDefaultAsync(x => x.Id == consumed.UserId && x.IsActive, cancellationToken)
            ?? throw new InvalidOperationException(InvalidLinkMessage);

        // The link was delivered to one stored address; it signs in only while that exact
        // address still belongs to this account.
        var userEmail = string.IsNullOrWhiteSpace(payload.UserEmailId)
            ? null
            : await _context.Set<SqlOSUserEmail>()
                .FirstOrDefaultAsync(x => x.Id == payload.UserEmailId && x.UserId == user.Id, cancellationToken);
        if (userEmail == null || !SqlOSEmailAddress.MatchesStoredEmail(userEmail, payload.NormalizedEmail))
        {
            throw new InvalidOperationException(InvalidLinkMessage);
        }

        // The link proved the mailbox it was delivered to. An unverified address is claimed:
        // whatever was attached before the owner proved it is evicted in this same save.
        await SqlOSEmailOwnershipClaim.ClaimAsync(
            _context,
            userEmail,
            new OwnershipProof(EmailAddress.Parse(payload.Email), OwnershipProofMethod.MagicLink),
            SqlOSEmailClaimPresentation.None,
            DateTime.UtcNow,
            cancellationToken);

        user.UpdatedAt = DateTime.UtcNow;
        user.DefaultEmail = userEmail.Email;
        consumed.Record(new MagicLinkCompleted(
            consumed.Id,
            payload.MaskedEmail,
            payload.IpAddress,
            user.Id,
            payload.ClientApplicationId,
            payload.AuthorizationRequestId,
            payload.RequestedOrganizationId));
        await _context.SaveChangesAsync(cancellationToken);

        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);

        return new SqlOSMagicLinkVerificationResult(
            consumed,
            payload,
            user,
            userEmail,
            organizations,
            "magic_link");
    }

    private async Task<SqlOSMagicLinkStartResult> CreateLinkAsync(
        string email,
        string? authorizationRequestId,
        string? clientApplicationId,
        string? requestedOrganizationId,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        var trimmedEmail = email?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedEmail))
        {
            throw new InvalidOperationException("Email address is required.");
        }

        if (!SqlOSEmailAddress.TryCanonicalize(trimmedEmail, out var typedAddress, out var normalizedEmail))
        {
            throw new InvalidOperationException(SqlOSEmailAddress.InvalidEmailMessage);
        }

        trimmedEmail = typedAddress;
        var now = DateTime.UtcNow;
        var origin = AdmissionOrigin.Of(httpContext);
        var ipAddress = origin.IpAddress;
        var maskedEmail = Masked.Email(trimmedEmail);

        // The send is admitted atomically before anything is written, so requests sent together
        // can never exceed a limit between them (#424).
        var admission = await _admission.AdmitSignInLinkAsync(EmailAddress.Parse(typedAddress), origin, clientApplicationId, now, cancellationToken);
        if (!admission.Admitted)
        {
            _auditRecorder.Record(new MagicLinkSendRateLimited(maskedEmail, ipAddress, admission.RefusedLimit!, clientApplicationId, requestedOrganizationId));
            await _context.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("Too many sign-in link requests. Try again later.");
        }

        // Only this client's recent links can be in the same sign-in context.
        var recent = (await _context.Set<SqlOSTemporaryToken>()
                .Where(SqlOSTemporaryToken.OfKind(SqlOSTemporaryTokenKinds.MagicLink))
                .Where(x => x.ClientApplicationId == clientApplicationId && x.CreatedAt >= now.Subtract(_options.RateLimitWindow))
                .ToListAsync(cancellationToken))
            .Select(token => new RecentMagicLinkToken(token, token.ReadPayload(SqlOSTemporaryTokenKinds.MagicLink)))
            .Where(x => x.Payload != null)
            .ToArray();

        var latestContextToken = recent
            .Where(x => x.Token.ConsumedAt == null
                && x.Token.ExpiresAt > now
                && string.Equals(x.Payload!.NormalizedEmail, normalizedEmail, StringComparison.Ordinal)
                && string.Equals(x.Payload.AuthorizationRequestId, authorizationRequestId, StringComparison.Ordinal)
                && string.Equals(x.Token.ClientApplicationId, clientApplicationId, StringComparison.Ordinal)
                && string.Equals(x.Payload.RequestedOrganizationId, requestedOrganizationId, StringComparison.Ordinal))
            .OrderByDescending(x => x.Token.CreatedAt)
            .FirstOrDefault();
        if (latestContextToken != null && latestContextToken.Token.CreatedAt > now.Subtract(_options.ResendCooldown))
        {
            // A resend the cooldown refuses sends nothing, so it does not count against a limit.
            await _admission.WithdrawAsync(admission, now, cancellationToken);
            throw new InvalidOperationException($"Wait {(int)Math.Ceiling(_options.ResendCooldown.TotalSeconds)} seconds before requesting another sign-in link.");
        }

        foreach (var activeToken in recent.Where(x => x.Token.ConsumedAt == null
            && x.Token.ExpiresAt > now
            && string.Equals(x.Payload!.NormalizedEmail, normalizedEmail, StringComparison.Ordinal)
            && string.Equals(x.Payload.AuthorizationRequestId, authorizationRequestId, StringComparison.Ordinal)
            && string.Equals(x.Token.ClientApplicationId, clientApplicationId, StringComparison.Ordinal)
            && string.Equals(x.Payload.RequestedOrganizationId, requestedOrganizationId, StringComparison.Ordinal)))
        {
            activeToken.Token.Retire(now);
        }

        var emailRecord = await _context.Set<SqlOSUserEmail>()
            .Include(x => x.User)
            .FindByNormalizedEmailAsync(normalizedEmail, email, cancellationToken);
        var shouldSend = emailRecord?.User != null && emailRecord.User.IsActive;
        var expiresAt = now.Add(_options.TokenLifetime);

        // A link for an existing account is only ever delivered to the address stored on that
        // account, never to the typed spelling.
        var deliveryAddress = emailRecord?.Email.Trim() ?? typedAddress;
        var payload = new MagicLinkPayload(
            deliveryAddress,
            normalizedEmail,
            maskedEmail,
            emailRecord?.Id,
            authorizationRequestId,
            clientApplicationId,
            requestedOrganizationId,
            ipAddress,
            httpContext?.Request.Headers.UserAgent.ToString(),
            shouldSend);
        var link = await _cryptoService.CreateTemporaryTokenAsync(
            SqlOSTemporaryTokenKinds.MagicLink,
            payload,
            new TemporaryTokenBinding(emailRecord?.UserId, clientApplicationId, requestedOrganizationId),
            _options.TokenLifetime,
            cancellationToken);
        var rawToken = link.RawToken;

        if (shouldSend)
        {
            try
            {
                var context = await BuildMessageContextAsync(payload.Email, Masked.Email(payload.Email), rawToken, expiresAt, httpContext, cancellationToken);
                await SendEmailAsync(context, rawToken, cancellationToken);
            }
            catch
            {
                link.Token.Retire(DateTime.UtcNow);
                link.Token.Record(new MagicLinkDeliveryFailed(
                    link.Token.Id,
                    maskedEmail,
                    ipAddress,
                    clientApplicationId,
                    authorizationRequestId,
                    requestedOrganizationId));
                await _context.SaveChangesAsync(cancellationToken);
                throw new InvalidOperationException("We couldn't send a sign-in link right now.");
            }
        }

        link.Token.Record(new MagicLinkRequested(
            link.Token.Id,
            maskedEmail,
            ipAddress,
            clientApplicationId,
            authorizationRequestId,
            requestedOrganizationId,
            shouldSend));
        await _context.SaveChangesAsync(cancellationToken);

        return new SqlOSMagicLinkStartResult(
            trimmedEmail,
            maskedEmail,
            $"If an account exists for {maskedEmail}, check your email for a sign-in link.",
            expiresAt,
            now.Add(_options.ResendCooldown));
    }

    private void ValidateBinding(
        SqlOSTemporaryToken token,
        MagicLinkPayload payload,
        string? expectedAuthorizationRequestId,
        bool requireAuthorizationRequestMatch)
    {
        if (!string.Equals(token.ClientApplicationId, payload.ClientApplicationId, StringComparison.Ordinal)
            || !string.Equals(token.OrganizationId, payload.RequestedOrganizationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(InvalidLinkMessage);
        }

        if (requireAuthorizationRequestMatch)
        {
            if (string.IsNullOrWhiteSpace(expectedAuthorizationRequestId))
            {
                if (!string.IsNullOrWhiteSpace(payload.AuthorizationRequestId))
                {
                    throw new InvalidOperationException(InvalidLinkMessage);
                }
            }
            else if (!string.Equals(payload.AuthorizationRequestId, expectedAuthorizationRequestId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(InvalidLinkMessage);
            }
        }
    }

    private async Task EnsureMagicLinkEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsService.GetResolvedCredentialSettingsAsync(cancellationToken);
        if (!settings.MagicLinkEnabled)
        {
            throw new InvalidOperationException("Magic-link sign-in is unavailable.");
        }
    }

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

    private sealed record RecentMagicLinkToken(SqlOSTemporaryToken Token, MagicLinkPayload? Payload);
}

internal sealed record SqlOSMagicLinkVerificationResult(
    SqlOSTemporaryToken Token,
    MagicLinkPayload Payload,
    SqlOSUser User,
    SqlOSUserEmail UserEmail,
    IReadOnlyList<SqlOSOrganizationOption> Organizations,
    string AuthenticationMethod);

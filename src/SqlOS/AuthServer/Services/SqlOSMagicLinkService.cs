using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
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

public sealed class SqlOSMagicLinkService
{
    public const string TokenPurpose = "auth.magic_link";
    private const string InvalidLinkMessage = "The sign-in link is invalid or expired.";

    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly ISqlOSAuthEmailSender _emailSender;
    private readonly ISqlOSTransactionalEmailService? _transactionalEmailService;
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

        var token = await _cryptoService.FindTemporaryTokenAsync(TokenPurpose, rawToken, cancellationToken);
        if (token == null)
        {
            await RecordMagicLinkAuditAsync(
                "magic_link.rejected",
                null,
                "complete",
                ipAddress: null,
                new { reason = "missing_expired_or_replayed" },
                cancellationToken);
            throw new InvalidOperationException(InvalidLinkMessage);
        }

        var payload = _cryptoService.DeserializePayload<MagicLinkPayload>(token)
            ?? throw new InvalidOperationException(InvalidLinkMessage);

        ValidateBinding(token, payload, expectedAuthorizationRequestId, requireAuthorizationRequestMatch);

        var consumed = await _cryptoService.ConsumeTemporaryTokenAsync(TokenPurpose, rawToken, cancellationToken);
        if (consumed == null)
        {
            await RecordMagicLinkAuditAsync(
                "magic_link.rejected",
                payload.MaskedEmail,
                "complete",
                payload.IpAddress,
                new
                {
                    reason = "replayed",
                    payload.ClientApplicationId,
                    payload.AuthorizationRequestId
                },
                cancellationToken);
            throw new InvalidOperationException(InvalidLinkMessage);
        }

        if (string.IsNullOrWhiteSpace(consumed.UserId))
        {
            throw new InvalidOperationException(InvalidLinkMessage);
        }

        var user = await _context.Set<SqlOSUser>()
            .FirstOrDefaultAsync(x => x.Id == consumed.UserId && x.IsActive, cancellationToken)
            ?? throw new InvalidOperationException(InvalidLinkMessage);

        var userEmail = await _context.Set<SqlOSUserEmail>()
            .FirstOrDefaultAsync(x => x.UserId == user.Id && x.NormalizedEmail == payload.NormalizedEmail, cancellationToken)
            ?? throw new InvalidOperationException(InvalidLinkMessage);

        if (!userEmail.IsVerified)
        {
            userEmail.IsVerified = true;
            userEmail.VerifiedAt = DateTime.UtcNow;
        }

        user.UpdatedAt = DateTime.UtcNow;
        user.DefaultEmail = userEmail.Email;
        await _context.SaveChangesAsync(cancellationToken);

        var organizations = await _adminService.GetUserOrganizationsAsync(user.Id, cancellationToken);
        await RecordMagicLinkAuditAsync(
            "magic_link.completed",
            payload.MaskedEmail,
            "complete",
            payload.IpAddress,
            new
            {
                user.Id,
                payload.ClientApplicationId,
                payload.AuthorizationRequestId,
                payload.RequestedOrganizationId
            },
            cancellationToken);

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
        var trimmedEmail = email?.Trim()
            ?? throw new InvalidOperationException("Email address is required.");
        if (string.IsNullOrWhiteSpace(trimmedEmail))
        {
            throw new InvalidOperationException("Email address is required.");
        }

        var normalizedEmail = SqlOSAdminService.NormalizeEmail(trimmedEmail);
        var now = DateTime.UtcNow;
        var ipAddress = httpContext?.Connection.RemoteIpAddress?.ToString();
        var maskedEmail = MaskEmail(trimmedEmail);

        var recentTokens = await _context.Set<SqlOSTemporaryToken>()
            .Where(x => x.Purpose == TokenPurpose && x.CreatedAt >= now.Subtract(_options.RateLimitWindow))
            .ToListAsync(cancellationToken);
        var recent = recentTokens
            .Select(token => new RecentMagicLinkToken(token, _cryptoService.DeserializePayload<MagicLinkPayload>(token)))
            .Where(x => x.Payload != null)
            .ToArray();

        if (recent.Count(x => string.Equals(x.Payload!.NormalizedEmail, normalizedEmail, StringComparison.Ordinal)) >= _options.MaxLinksPerEmailPerWindow)
        {
            await RecordMagicLinkRateLimitAsync("email", maskedEmail, ipAddress, clientApplicationId, requestedOrganizationId, cancellationToken);
            throw new InvalidOperationException("Too many sign-in link requests. Try again later.");
        }

        if (!string.IsNullOrWhiteSpace(ipAddress)
            && recent.Count(x => string.Equals(x.Payload!.IpAddress, ipAddress, StringComparison.Ordinal)) >= _options.MaxLinksPerIpPerWindow)
        {
            await RecordMagicLinkRateLimitAsync("ip", maskedEmail, ipAddress, clientApplicationId, requestedOrganizationId, cancellationToken);
            throw new InvalidOperationException("Too many sign-in link requests. Try again later.");
        }

        if (!string.IsNullOrWhiteSpace(clientApplicationId)
            && recent.Count(x => string.Equals(x.Token.ClientApplicationId, clientApplicationId, StringComparison.Ordinal)) >= _options.MaxLinksPerClientPerWindow)
        {
            await RecordMagicLinkRateLimitAsync("client", maskedEmail, ipAddress, clientApplicationId, requestedOrganizationId, cancellationToken);
            throw new InvalidOperationException("Too many sign-in link requests. Try again later.");
        }

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
            throw new InvalidOperationException($"Wait {(int)Math.Ceiling(_options.ResendCooldown.TotalSeconds)} seconds before requesting another sign-in link.");
        }

        foreach (var activeToken in recent.Where(x => x.Token.ConsumedAt == null
            && x.Token.ExpiresAt > now
            && string.Equals(x.Payload!.NormalizedEmail, normalizedEmail, StringComparison.Ordinal)
            && string.Equals(x.Payload.AuthorizationRequestId, authorizationRequestId, StringComparison.Ordinal)
            && string.Equals(x.Token.ClientApplicationId, clientApplicationId, StringComparison.Ordinal)
            && string.Equals(x.Payload.RequestedOrganizationId, requestedOrganizationId, StringComparison.Ordinal)))
        {
            activeToken.Token.ConsumedAt = now;
        }

        var emailRecord = await _context.Set<SqlOSUserEmail>()
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken);
        var shouldSend = emailRecord?.User != null && emailRecord.User.IsActive;
        var expiresAt = now.Add(_options.TokenLifetime);

        var payload = new MagicLinkPayload(
            trimmedEmail,
            normalizedEmail,
            maskedEmail,
            emailRecord?.Id,
            authorizationRequestId,
            clientApplicationId,
            requestedOrganizationId,
            ipAddress,
            httpContext?.Request.Headers.UserAgent.ToString(),
            shouldSend);
        var rawToken = await _cryptoService.CreateTemporaryTokenAsync(
            TokenPurpose,
            emailRecord?.UserId,
            clientApplicationId,
            requestedOrganizationId,
            payload,
            _options.TokenLifetime,
            cancellationToken);

        if (shouldSend)
        {
            try
            {
                var context = await BuildMessageContextAsync(trimmedEmail, maskedEmail, rawToken, expiresAt, httpContext, cancellationToken);
                await SendEmailAsync(context, rawToken, cancellationToken);
            }
            catch
            {
                var tokenHash = _cryptoService.HashToken(rawToken);
                var createdToken = await _context.Set<SqlOSTemporaryToken>()
                    .FirstOrDefaultAsync(x => x.Purpose == TokenPurpose && x.TokenHash == tokenHash, cancellationToken);
                if (createdToken != null)
                {
                    createdToken.ConsumedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync(cancellationToken);
                }

                await RecordMagicLinkAuditAsync(
                    "magic_link.send_failed",
                    maskedEmail,
                    "start",
                    ipAddress,
                    new { clientApplicationId, authorizationRequestId, requestedOrganizationId },
                    cancellationToken);
                throw new InvalidOperationException("We couldn't send a sign-in link right now.");
            }
        }

        await RecordMagicLinkAuditAsync(
            "magic_link.requested",
            maskedEmail,
            "start",
            ipAddress,
            new
            {
                clientApplicationId,
                authorizationRequestId,
                requestedOrganizationId,
                sent = shouldSend
            },
            cancellationToken);

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

    private async Task RecordMagicLinkRateLimitAsync(
        string limit,
        string maskedEmail,
        string? ipAddress,
        string? clientApplicationId,
        string? requestedOrganizationId,
        CancellationToken cancellationToken)
        => await RecordMagicLinkAuditAsync(
            "magic_link.rate_limit_rejected",
            maskedEmail,
            "start",
            ipAddress,
            new { limit, clientApplicationId, requestedOrganizationId },
            cancellationToken);

    private async Task RecordMagicLinkAuditAsync(
        string eventType,
        string? maskedEmail,
        string phase,
        string? ipAddress,
        object? data,
        CancellationToken cancellationToken)
        => await _adminService.RecordAuditAsync(
            eventType,
            "system",
            null,
            ipAddress: ipAddress,
            data: new
            {
                phase,
                maskedEmail,
                details = data
            },
            cancellationToken: cancellationToken);

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

    internal sealed record MagicLinkPayload(
        string Email,
        string NormalizedEmail,
        string MaskedEmail,
        string? UserEmailId,
        string? AuthorizationRequestId,
        string? ClientApplicationId,
        string? RequestedOrganizationId,
        string? IpAddress,
        string? UserAgent,
        bool Sent);

    private sealed record RecentMagicLinkToken(SqlOSTemporaryToken Token, MagicLinkPayload? Payload);
}

internal sealed record SqlOSMagicLinkVerificationResult(
    SqlOSTemporaryToken Token,
    SqlOSMagicLinkService.MagicLinkPayload Payload,
    SqlOSUser User,
    SqlOSUserEmail UserEmail,
    IReadOnlyList<SqlOSOrganizationOption> Organizations,
    string AuthenticationMethod);

using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Domain;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;
using SqlOS.Email.Models;
using SqlOS.Email.Services;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Builds and sends password-reset emails: the reset processes decide which account gets a link and
/// issue it, and this delivers it (<c>docs/architecture/domain-model.md</c> §2, infrastructure).
/// </summary>
/// <remarks>
/// <para>
/// The reset URL is the host's (<see cref="SqlOSPasswordResetOptions.BuildResetUrl"/>), a trusted
/// template the host passed in process, or the hosted reset page on the trusted public origin. Every
/// URL must be absolute HTTPS (or loopback HTTP) without user information, and the token may never
/// appear in its authority.
/// </para>
/// <para>
/// The message is the host's (<see cref="SqlOSPasswordResetOptions.BuildMessage"/>, through the auth
/// email sender) or the built-in transactional template. A link that cannot be built or sent throws,
/// and the caller withdraws it.
/// </para>
/// </remarks>
internal sealed class SqlOSPasswordResetDelivery
{
    private readonly SqlOSAuthServerOptions _options;
    private readonly SqlOSSettingsService _settingsService;
    private readonly ISqlOSAuthEmailSender? _authEmailSender;
    private readonly ISqlOSTransactionalEmailService? _transactionalEmailService;

    public SqlOSPasswordResetDelivery(
        SqlOSAuthServerOptions options,
        SqlOSSettingsService settingsService,
        ISqlOSAuthEmailSender? authEmailSender,
        ISqlOSTransactionalEmailService? transactionalEmailService)
    {
        _options = options;
        _settingsService = settingsService;
        _authEmailSender = authEmailSender;
        _transactionalEmailService = transactionalEmailService;
    }

    /// <summary>The reset options: lifetimes, cooldown, limits and the host's builders.</summary>
    public SqlOSPasswordResetOptions Options => _options.PasswordReset;

    /// <summary>
    /// Sends the reset link <paramref name="rawToken"/> to <paramref name="email"/>, the address
    /// stored on account <paramref name="userId"/>.
    /// </summary>
    /// <param name="email">The address stored on the account.</param>
    /// <param name="userId">The account.</param>
    /// <param name="rawToken">The link's raw token, which goes only into the message.</param>
    /// <param name="expiresAt">When the link expires.</param>
    /// <param name="trustedResetUrlTemplate">A template the host passed in process, never one from a public request.</param>
    /// <param name="clientId">The first-party client the host's URL builder may route by, or null.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>What was sent, and the masked address the message named.</returns>
    public async Task<PasswordResetEmailDelivery> SendAsync(
        string email,
        string userId,
        string rawToken,
        DateTime expiresAt,
        string? trustedResetUrlTemplate,
        string? clientId,
        CancellationToken cancellationToken)
    {
        var context = await BuildMessageContextAsync(
            email,
            Masked.Email(email),
            rawToken,
            expiresAt,
            trustedResetUrlTemplate,
            clientId,
            cancellationToken);

        if (Options.BuildMessage != null)
        {
            var authEmailSender = _authEmailSender
                ?? throw new InvalidOperationException("Auth email sender is not registered.");
            if (!authEmailSender.IsConfigured)
            {
                throw new InvalidOperationException("Auth email delivery is not configured.");
            }

            await authEmailSender.SendAsync(BuildHostMessage(context), cancellationToken);
            return new PasswordResetEmailDelivery.HostMessage(context.MaskedEmail, SqlOSIds.New("edl"));
        }

        var transactionalEmailService = _transactionalEmailService
            ?? throw new InvalidOperationException("Transactional email service is not registered.");
        var result = await transactionalEmailService.SendAsync(
            new SqlOSSendEmailRequest(
                SqlOSBuiltInEmailTemplates.AuthPasswordResetKey,
                email,
                BuildTemplateVariables(context),
                IdempotencyKey: $"auth-password-reset:{userId}:{HashedSecret.Sha256(rawToken).Hash[..32]}"),
            cancellationToken);

        if (string.Equals(result.Status, SqlOSEmailDeliveryStatuses.Failed, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(result.SanitizedError ?? "Password reset email delivery failed.");
        }

        return new PasswordResetEmailDelivery.Template(context.MaskedEmail, result);
    }

    private async Task<SqlOSPasswordResetMessageContext> BuildMessageContextAsync(
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
        var resetUrl = Options.BuildResetUrl?.Invoke(
            new SqlOSPasswordResetUrlContext(
                token,
                email,
                maskedEmail,
                expiresAt,
                Options.TokenLifetime,
                clientId))
            ?? BuildResetUrl(token, trustedResetUrlTemplate);
        resetUrl = ValidateGeneratedResetUrl(resetUrl, token);

        return new SqlOSPasswordResetMessageContext(
            applicationName,
            email,
            maskedEmail,
            resetUrl,
            expiresAt,
            Options.TokenLifetime)
        {
            Branding = branding with { ApplicationName = applicationName }
        };
    }

    private string BuildResetUrl(string token, string? trustedResetUrlTemplate)
    {
        var escapedToken = Uri.EscapeDataString(token);
        if (!string.IsNullOrWhiteSpace(trustedResetUrlTemplate))
        {
            var template = trustedResetUrlTemplate.Trim();
            if (template.Contains("{token}", StringComparison.Ordinal))
            {
                ValidateResetTemplate(template);
                return template.Replace("{token}", escapedToken, StringComparison.Ordinal);
            }

            var templateUri = new Uri(ValidateResetUrl(template), UriKind.Absolute);
            var builder = new UriBuilder(templateUri);
            var query = builder.Query.TrimStart('?');
            builder.Query = string.IsNullOrEmpty(query)
                ? $"token={escapedToken}"
                : $"{query}&token={escapedToken}";
            return builder.Uri.AbsoluteUri;
        }

        return $"{SqlOSTrustedPublicOrigin.Of(_options)}{_options.BasePath.TrimEnd('/')}/password/reset?token={escapedToken}";
    }

    private static IReadOnlyDictionary<string, object?> BuildTemplateVariables(SqlOSPasswordResetMessageContext context)
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

    private SqlOSAuthEmailMessage BuildHostMessage(SqlOSPasswordResetMessageContext context)
    {
        var subject = string.IsNullOrWhiteSpace(Options.Subject)
            ? "Reset your password"
            : Options.Subject
                .Replace("{applicationName}", context.ApplicationName, StringComparison.Ordinal)
                .Replace("{ApplicationName}", context.ApplicationName, StringComparison.Ordinal);

        return Options.BuildMessage?.Invoke(context)
            ?? new SqlOSAuthEmailMessage(
                context.Email,
                subject,
                SqlOSAuthEmailTemplateRenderer.BuildPasswordResetHtmlBody(context),
                SqlOSAuthEmailTemplateRenderer.BuildPasswordResetTextBody(context));
    }

    private static string ValidateResetUrl(string? resetUrl)
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

    private static void ValidateResetTemplate(string template)
    {
        const string marker = "sqlos-password-reset-token-marker";
        var probe = template.Replace("{token}", marker, StringComparison.Ordinal);
        var probeUri = new Uri(ValidateResetUrl(probe), UriKind.Absolute);
        if (probeUri.GetLeftPart(UriPartial.Authority).Contains(marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The password reset token placeholder cannot appear in the URL authority.");
        }
    }

    private static string ValidateGeneratedResetUrl(string? resetUrl, string token)
    {
        var validated = ValidateResetUrl(resetUrl);
        var uri = new Uri(validated, UriKind.Absolute);
        if (uri.GetLeftPart(UriPartial.Authority).Contains(token, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The password reset token cannot appear in the URL authority.");
        }

        return validated;
    }
}

/// <summary>What a password-reset email delivery sent.</summary>
internal abstract record PasswordResetEmailDelivery
{
    private PasswordResetEmailDelivery(string maskedEmail, string deliveryId)
    {
        MaskedEmail = maskedEmail;
        DeliveryId = deliveryId;
    }

    /// <summary>The address as the message named it.</summary>
    public string MaskedEmail { get; }

    public string DeliveryId { get; }

    /// <summary>The host's message builder produced the message and its sender accepted it: it counts as queued.</summary>
    public sealed record HostMessage : PasswordResetEmailDelivery
    {
        public HostMessage(string maskedEmail, string deliveryId)
            : base(maskedEmail, deliveryId)
        {
        }
    }

    /// <summary>The built-in template went through the transactional email service.</summary>
    public sealed record Template : PasswordResetEmailDelivery
    {
        public Template(string maskedEmail, SqlOSSendEmailResult result)
            : base(maskedEmail, result.DeliveryId)
        {
            Result = result;
        }

        public SqlOSSendEmailResult Result { get; }
    }
}

/// <summary>
/// The origin SqlOS puts in the links it emails: the configured public origin, else the issuer's
/// authority. It never comes from the request, whose host header a caller controls.
/// </summary>
internal static class SqlOSTrustedPublicOrigin
{
    public static string Of(SqlOSAuthServerOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PublicOrigin))
        {
            return options.PublicOrigin.TrimEnd('/');
        }

        if (!Uri.TryCreate(options.Issuer, UriKind.Absolute, out var issuer))
        {
            throw new InvalidOperationException("AuthServer.Issuer must be an absolute URI before password reset links can be generated.");
        }

        return issuer.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }
}

using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;
using SqlOS.Email.Models;
using SqlOS.Email.Services;

namespace SqlOS.AuthServer.Services;

/// <summary>
/// Sends email-verification links with the built-in transactional template, linking to the hosted
/// verification page on the trusted public origin. The verification process decides which address
/// gets a link and issues it; this delivers it (<c>docs/architecture/domain-model.md</c> §2,
/// infrastructure). A link that cannot be sent throws, and the caller withdraws it.
/// </summary>
internal sealed class SqlOSEmailVerificationDelivery
{
    private readonly SqlOSAuthServerOptions _options;
    private readonly SqlOSSettingsService _settingsService;
    private readonly ISqlOSTransactionalEmailService? _transactionalEmailService;

    public SqlOSEmailVerificationDelivery(
        SqlOSAuthServerOptions options,
        SqlOSSettingsService settingsService,
        ISqlOSTransactionalEmailService? transactionalEmailService)
    {
        _options = options;
        _settingsService = settingsService;
        _transactionalEmailService = transactionalEmailService;
    }

    /// <summary>Sends the link <paramref name="rawToken"/> for address <paramref name="emailId"/> to <paramref name="email"/>.</summary>
    public async Task<SqlOSSendEmailResult> SendAsync(
        string email,
        string emailId,
        string rawToken,
        CancellationToken cancellationToken)
    {
        var branding = await _settingsService.GetResolvedAuthEmailBrandingAsync(cancellationToken);
        var applicationName = string.IsNullOrWhiteSpace(branding.ApplicationName)
            ? string.IsNullOrWhiteSpace(_options.EmailOtp.ApplicationName)
                ? "SqlOS"
                : _options.EmailOtp.ApplicationName.Trim()
            : branding.ApplicationName;
        var verificationUrl = $"{SqlOSTrustedPublicOrigin.Of(_options)}{_options.BasePath.TrimEnd('/')}/email/verify?token={Uri.EscapeDataString(rawToken)}";
        var result = await (_transactionalEmailService
                ?? throw new InvalidOperationException("Transactional email service is not registered."))
            .SendAsync(
                new SqlOSSendEmailRequest(
                    SqlOSBuiltInEmailTemplates.AuthEmailVerificationKey,
                    email,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["applicationName"] = applicationName,
                        ["logoBase64"] = branding.LogoBase64 ?? string.Empty,
                        ["logoImageDisplay"] = string.IsNullOrWhiteSpace(branding.LogoBase64) ? "none" : "block",
                        ["logoTextDisplay"] = string.IsNullOrWhiteSpace(branding.LogoBase64) ? "block" : "none",
                        ["maskedEmail"] = Masked.Email(email),
                        ["verificationUrl"] = verificationUrl,
                        ["expiresInHours"] = (int)SqlOSTemporaryTokenKinds.EmailVerification.Lifetime.FixedLifetime!.Value.TotalHours,
                        ["primaryColor"] = branding.PrimaryColor,
                        ["accentColor"] = branding.AccentColor,
                        ["backgroundColor"] = branding.BackgroundColor
                    },
                    IdempotencyKey: $"auth-email-verification:{emailId}:{HashedSecret.Sha256(rawToken).Hash[..32]}"),
                cancellationToken);

        if (string.Equals(result.Status, SqlOSEmailDeliveryStatuses.Failed, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(result.SanitizedError ?? "Email verification delivery failed.");
        }

        return result;
    }
}

using System.Net.Mail;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PhoneNumbers;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSOtpAdminService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _admin;
    private readonly SqlOSCryptoService _crypto;
    private readonly SqlOSSettingsService _settings;
    private readonly ISqlOSAuthEmailSender _emailSender;
    private readonly ISqlOSOtpDeliveryChannel _phoneChannel;
    private readonly SqlOSOtpAdminRateLimiter _rateLimiter;
    private readonly SqlOSAuthServerOptions _options;
    private readonly PhoneNumberUtil _phoneNumbers = PhoneNumberUtil.GetInstance();

    public SqlOSOtpAdminService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService admin,
        SqlOSCryptoService crypto,
        SqlOSSettingsService settings,
        ISqlOSAuthEmailSender emailSender,
        ISqlOSOtpDeliveryChannel phoneChannel,
        SqlOSOtpAdminRateLimiter rateLimiter,
        IOptions<SqlOSAuthServerOptions> options)
    {
        _context = context;
        _admin = admin;
        _crypto = crypto;
        _settings = settings;
        _emailSender = emailSender;
        _phoneChannel = phoneChannel;
        _rateLimiter = rateLimiter;
        _options = options.Value;
    }

    public async Task<SqlOSOtpReadinessResponse> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        var credentials = await _settings.GetResolvedCredentialSettingsAsync(cancellationToken);
        var emailReasons = GetEmailReasons();
        var phoneReasons = GetPhoneReasons();
        var diagnostics = await _context.Set<SqlOSAuditEvent>()
            .AsNoTracking()
            .Where(x => x.Action.StartsWith("otp.admin_test.")
                || x.Action == "email_otp.send_failed"
                || x.Action == "email_otp.verify_failed"
                || x.Action == "email_otp.rate_limit_rejected"
                || x.Action == "phone_otp.send_failed"
                || x.Action == "phone_otp.verify_failed"
                || x.Action == "phone_otp.rate_limit_rejected")
            .OrderByDescending(x => x.OccurredAt)
            .Take(20)
            .Select(x => new SqlOSOtpDiagnostic(x.Action, x.OccurredAt, x.MetadataJson))
            .ToListAsync(cancellationToken);

        return new SqlOSOtpReadinessResponse(
            BuildEmailStatus(credentials.EmailOtpEnabled, emailReasons),
            BuildPhoneStatus(credentials.PhoneOtpEnabled, phoneReasons),
            diagnostics.Select(ToDiagnostic).ToArray());
    }

    public async Task<SqlOSOtpTestDeliveryResult> SendTestAsync(
        string method,
        string destination,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        var normalizedMethod = (method ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedDestination = normalizedMethod switch
        {
            "email" => NormalizeEmail(destination),
            "phone" => NormalizePhone(destination),
            _ => throw new ArgumentException("Method must be 'email' or 'phone'.", nameof(method))
        };
        var reasons = normalizedMethod == "email" ? GetEmailReasons() : GetPhoneReasons();
        if (reasons.Count != 0)
        {
            throw new InvalidOperationException($"{normalizedMethod} OTP test delivery is unavailable: {string.Join(", ", reasons)}.");
        }

        var destinationHash = _crypto.HashToken(normalizedDestination);
        var actorHash = _crypto.HashToken(string.IsNullOrWhiteSpace(ipAddress) ? "unknown" : ipAddress);
        var now = DateTimeOffset.UtcNow;
        var destinationAllowed = await _rateLimiter.TryConsumeAsync($"destination:{normalizedMethod}:{destinationHash}", now, cancellationToken: cancellationToken);
        var actorAllowed = await _rateLimiter.TryConsumeAsync($"actor:{actorHash}", now, maxAttempts: 20, cancellationToken: cancellationToken);
        if (!destinationAllowed || !actorAllowed)
        {
            await AuditAsync(normalizedMethod, "rate_limited", Mask(normalizedMethod, normalizedDestination), null, ipAddress, cancellationToken);
            throw new InvalidOperationException("Test delivery limit reached. Try again later.");
        }

        var masked = Mask(normalizedMethod, normalizedDestination);
        try
        {
            string provider;
            string? providerStatus = null;
            if (normalizedMethod == "email")
            {
                provider = _options.EmailOtp.BuildMessage == null ? "azure_communication_services" : "custom_email_sender";
                await _emailSender.SendAsync(new SqlOSAuthEmailMessage(
                    normalizedDestination,
                    $"{_options.EmailOtp.ApplicationName} delivery test",
                    "<p>Your SqlOS email delivery configuration is working. This message is not a sign-in code.</p>",
                    "Your SqlOS email delivery configuration is working. This message is not a sign-in code."), cancellationToken);
            }
            else
            {
                var delivery = await _phoneChannel.StartAsync(normalizedDestination,
                    new SqlOSOtpDeliveryContext("admin_test", null, null, ipAddress, null), cancellationToken);
                provider = delivery.Provider;
                providerStatus = delivery.ProviderStatus;
                if (!delivery.Accepted)
                {
                    throw new InvalidOperationException(delivery.SanitizedError ?? "The OTP provider rejected the test delivery.");
                }
            }

            await AuditAsync(normalizedMethod, "succeeded", masked, providerStatus, ipAddress, cancellationToken, provider);
            return new SqlOSOtpTestDeliveryResult(normalizedMethod, masked, provider, providerStatus, DateTime.UtcNow);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AuditAsync(normalizedMethod, "failed", masked, "provider_error", ipAddress, cancellationToken);
            throw new InvalidOperationException("The OTP provider could not complete the test delivery. Review recent diagnostics.");
        }
    }

    private SqlOSOtpMethodReadiness BuildEmailStatus(bool enabled, IReadOnlyList<string> reasons)
        => new("email", enabled, reasons.Count == 0, _options.EmailOtp.BuildMessage == null ? "azure_communication_services" : "custom_email_sender",
            reasons, ["SqlOS:EmailOtp:AzureCommunicationServicesConnectionString", "SqlOS:EmailOtp:FromAddress"],
            new Dictionary<string, object?>
            {
                ["fromAddress"] = MaskEmail(_options.EmailOtp.FromAddress),
                ["codeLength"] = _options.EmailOtp.CodeLength,
                ["challengeLifetimeMinutes"] = _options.EmailOtp.ChallengeLifetime.TotalMinutes,
                ["maxAttempts"] = _options.EmailOtp.MaxAttempts,
                ["maxChallengesPerHour"] = _options.EmailOtp.MaxChallengesPerHour
            });

    private SqlOSOtpMethodReadiness BuildPhoneStatus(bool enabled, IReadOnlyList<string> reasons)
        => new("phone", enabled, reasons.Count == 0, "twilio_verify", reasons,
            ["SqlOS:PhoneOtp:Enabled", "SqlOS:PhoneOtp:TwilioAccountSid", "SqlOS:PhoneOtp:TwilioAuthToken", "SqlOS:PhoneOtp:TwilioVerifyServiceSid"],
            new Dictionary<string, object?>
            {
                ["defaultRegion"] = _options.PhoneOtp.DefaultRegion,
                ["serviceSidSuffix"] = LastFour(_options.PhoneOtp.TwilioVerifyServiceSid),
                ["challengeLifetimeMinutes"] = _options.PhoneOtp.ChallengeLifetime.TotalMinutes,
                ["maxSendsPerPhone"] = _options.PhoneOtp.MaxSendsPerPhone,
                ["countryAllowList"] = _options.PhoneOtp.CountryAllowList,
                ["countryDenyList"] = _options.PhoneOtp.CountryDenyList
            });

    private List<string> GetEmailReasons()
    {
        var reasons = new List<string>();
        if (!_emailSender.IsConfigured) reasons.Add("email_sender_unavailable");
        if (_options.EmailOtp.BuildMessage == null && string.IsNullOrWhiteSpace(_options.EmailOtp.FromAddress)) reasons.Add("missing_from_address");
        if (_options.EmailOtp.CodeLength is < 4 or > 10) reasons.Add("invalid_code_length");
        if (_options.EmailOtp.MaxAttempts < 1) reasons.Add("invalid_max_attempts");
        return reasons;
    }

    private List<string> GetPhoneReasons()
    {
        var reasons = new List<string>();
        if (!_options.PhoneOtp.Enabled) reasons.Add("method_disabled_in_host_configuration");
        if (string.IsNullOrWhiteSpace(_options.PhoneOtp.TwilioAccountSid)) reasons.Add("missing_account_sid");
        if (string.IsNullOrWhiteSpace(_options.PhoneOtp.TwilioAuthToken)) reasons.Add("missing_auth_token");
        if (string.IsNullOrWhiteSpace(_options.PhoneOtp.TwilioVerifyServiceSid)) reasons.Add("missing_verify_service_sid");
        if (_options.PhoneOtp.MaxSendsPerPhone < 1) reasons.Add("invalid_send_limit");
        return reasons;
    }

    private async Task AuditAsync(string method, string outcome, string masked, string? status, string? ipAddress, CancellationToken cancellationToken, string? provider = null)
        => await _admin.RecordAuditAsync($"otp.admin_test.{outcome}", "admin", null, ipAddress: ipAddress,
            data: new { method, maskedDestination = masked, provider, providerStatus = status }, cancellationToken: cancellationToken);

    private static SqlOSOtpDiagnosticResponse ToDiagnostic(SqlOSOtpDiagnostic diagnostic)
    {
        string? method = null, masked = null, provider = null, status = null;
        if (!string.IsNullOrWhiteSpace(diagnostic.MetadataJson))
        {
            using var document = JsonDocument.Parse(diagnostic.MetadataJson);
            var root = document.RootElement;
            method = ReadString(root, "method");
            masked = ReadString(root, "maskedDestination");
            provider = ReadString(root, "provider");
            status = ReadString(root, "providerStatus");
            method ??= diagnostic.Action.StartsWith("email_otp.", StringComparison.Ordinal) ? "email"
                : diagnostic.Action.StartsWith("phone_otp.", StringComparison.Ordinal) ? "phone" : null;
            masked ??= ReadString(root, "maskedEmail") ?? ReadString(root, "maskedPhone");
            if (status == null && root.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object)
            {
                status = ReadString(details, "providerStatus") ?? ReadString(details, "reason");
            }
        }
        return new SqlOSOtpDiagnosticResponse(diagnostic.Action, diagnostic.OccurredAt, method, masked, provider, status);
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string NormalizeEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 320) throw new ArgumentException("A valid email destination is required.");
        try { return new MailAddress(value.Trim()).Address.ToLowerInvariant(); }
        catch (FormatException exception) { throw new ArgumentException("A valid email destination is required.", exception); }
    }

    private string NormalizePhone(string value)
    {
        try
        {
            var parsed = _phoneNumbers.Parse(value?.Trim(), _options.PhoneOtp.DefaultRegion);
            if (!_phoneNumbers.IsValidNumber(parsed)) throw new ArgumentException("A valid phone destination is required.");
            return _phoneNumbers.Format(parsed, PhoneNumberFormat.E164);
        }
        catch (NumberParseException exception) { throw new ArgumentException("A valid phone destination is required.", exception); }
    }

    private static string Mask(string method, string value) => method == "email" ? MaskEmail(value)! : value.Length <= 4 ? "****" : $"***{value[^4..]}";
    private static string? MaskEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('@')) return null;
        var parts = value.Split('@', 2);
        return $"{parts[0][0]}***@{parts[1]}";
    }
    private static string? LastFour(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= 4 ? "****" : $"***{value[^4..]}";

    private sealed record SqlOSOtpDiagnostic(string Action, DateTime OccurredAt, string? MetadataJson);
}

public sealed record SqlOSOtpReadinessResponse(SqlOSOtpMethodReadiness Email, SqlOSOtpMethodReadiness Phone, IReadOnlyList<SqlOSOtpDiagnosticResponse> RecentDiagnostics);
public sealed record SqlOSOtpMethodReadiness(string Method, bool Enabled, bool LocallyConfigured, string Provider, IReadOnlyList<string> ReasonCodes, IReadOnlyList<string> ConfigurationKeys, IReadOnlyDictionary<string, object?> Policy);
public sealed record SqlOSOtpTestDeliveryResult(string Method, string MaskedDestination, string Provider, string? ProviderStatus, DateTime SentAt);
public sealed record SqlOSOtpDiagnosticResponse(string Action, DateTime OccurredAt, string? Method, string? MaskedDestination, string? Provider, string? ProviderStatus);

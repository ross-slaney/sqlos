using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using System.Text.Json;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSSettingsService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAuthServerOptions _options;
    private readonly ISqlOSAuthEmailSender _emailSender;

    public SqlOSSettingsService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        ISqlOSAuthEmailSender emailSender)
    {
        _context = context;
        _options = options.Value;
        _emailSender = emailSender;
    }

    public async Task EnsureDefaultSettingsAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _context.Set<SqlOSSettings>().FirstOrDefaultAsync(x => x.Id == "default", cancellationToken);
        if (existing != null)
        {
            return;
        }

        _context.Set<SqlOSSettings>().Add(new SqlOSSettings
        {
            Id = "default",
            RefreshTokenLifetimeMinutes = (int)_options.RefreshTokenLifetime.TotalMinutes,
            SessionIdleTimeoutMinutes = (int)_options.SessionIdleTimeout.TotalMinutes,
            SessionAbsoluteLifetimeMinutes = (int)_options.SessionAbsoluteLifetime.TotalMinutes,
            SigningKeyRotationIntervalDays = _options.DefaultSigningKeyRotationIntervalDays,
            SigningKeyGraceWindowDays = _options.DefaultSigningKeyGraceWindowDays,
            SigningKeyRetiredCleanupDays = _options.DefaultSigningKeyRetiredCleanupDays,
            RefreshTokenGraceWindowSeconds = _options.RefreshTokenGraceWindowSeconds,
            UpdatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task EnsureDefaultAuthPageSettingsAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _context.Set<SqlOSAuthPageSettings>().FirstOrDefaultAsync(x => x.Id == "default", cancellationToken);
        if (existing != null)
        {
            return;
        }

        _context.Set<SqlOSAuthPageSettings>().Add(new SqlOSAuthPageSettings
        {
            Id = "default",
            EmailApplicationName = ResolveDefaultEmailApplicationName(),
            EmailPrimaryColor = "#2563eb",
            EmailAccentColor = "#0f172a",
            EmailBackgroundColor = "#f8fafc",
            UpdatedAt = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertSeededAuthPageSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_options.AuthPageSeed == null)
        {
            return;
        }

        await EnsureDefaultAuthPageSettingsAsync(cancellationToken);
        var settings = await _context.Set<SqlOSAuthPageSettings>().FirstAsync(x => x.Id == "default", cancellationToken);

        if (!string.Equals(_options.AuthPageSeed.Layout, "split", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(_options.AuthPageSeed.Layout, "stacked", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Auth page layout must be either 'split' or 'stacked'.");
        }

        var enabledCredentialTypes = (_options.AuthPageSeed.EnabledCredentialTypes ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (enabledCredentialTypes.Length == 0)
        {
            enabledCredentialTypes = ["password"];
        }

        settings.LogoBase64 = string.IsNullOrWhiteSpace(_options.AuthPageSeed.LogoBase64) ? null : _options.AuthPageSeed.LogoBase64.Trim();
        settings.PrimaryColor = RequireColor(_options.AuthPageSeed.PrimaryColor, nameof(_options.AuthPageSeed.PrimaryColor));
        settings.AccentColor = RequireColor(_options.AuthPageSeed.AccentColor, nameof(_options.AuthPageSeed.AccentColor));
        settings.BackgroundColor = RequireColor(_options.AuthPageSeed.BackgroundColor, nameof(_options.AuthPageSeed.BackgroundColor));
        settings.Layout = _options.AuthPageSeed.Layout.Trim().ToLowerInvariant();
        settings.PageTitle = RequireText(_options.AuthPageSeed.PageTitle, nameof(_options.AuthPageSeed.PageTitle));
        settings.PageSubtitle = RequireText(_options.AuthPageSeed.PageSubtitle, nameof(_options.AuthPageSeed.PageSubtitle));
        settings.EnablePasswordSignup = _options.AuthPageSeed.EnablePasswordSignup;
        settings.EnabledCredentialTypesJson = JsonSerializer.Serialize(enabledCredentialTypes);

        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertSeededAuthEmailSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_options.AuthEmailSeed == null)
        {
            return;
        }

        await EnsureDefaultAuthPageSettingsAsync(cancellationToken);
        var settings = await _context.Set<SqlOSAuthPageSettings>().FirstAsync(x => x.Id == "default", cancellationToken);

        settings.EmailApplicationName = RequireText(_options.AuthEmailSeed.ApplicationName, nameof(_options.AuthEmailSeed.ApplicationName));
        settings.EmailLogoBase64 = string.IsNullOrWhiteSpace(_options.AuthEmailSeed.LogoBase64) ? null : _options.AuthEmailSeed.LogoBase64.Trim();
        settings.EmailPrimaryColor = RequireColor(_options.AuthEmailSeed.PrimaryColor, nameof(_options.AuthEmailSeed.PrimaryColor));
        settings.EmailAccentColor = RequireColor(_options.AuthEmailSeed.AccentColor, nameof(_options.AuthEmailSeed.AccentColor));
        settings.EmailBackgroundColor = RequireColor(_options.AuthEmailSeed.BackgroundColor, nameof(_options.AuthEmailSeed.BackgroundColor));
        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<SqlOSSecuritySettingsDto> GetSecuritySettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDefaultSettingsAsync(cancellationToken);
        var settings = await _context.Set<SqlOSSettings>().FirstAsync(x => x.Id == "default", cancellationToken);
        return new SqlOSSecuritySettingsDto(
            settings.RefreshTokenLifetimeMinutes,
            settings.SessionIdleTimeoutMinutes,
            settings.SessionAbsoluteLifetimeMinutes,
            settings.SigningKeyRotationIntervalDays,
            settings.SigningKeyGraceWindowDays,
            settings.SigningKeyRetiredCleanupDays,
            settings.RefreshTokenGraceWindowSeconds,
            settings.UpdatedAt);
    }

    public async Task<SqlOSKeyRotationSettings> GetKeyRotationSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDefaultSettingsAsync(cancellationToken);
        var settings = await _context.Set<SqlOSSettings>().FirstAsync(x => x.Id == "default", cancellationToken);
        return new SqlOSKeyRotationSettings(
            TimeSpan.FromDays(settings.SigningKeyRotationIntervalDays),
            TimeSpan.FromDays(settings.SigningKeyGraceWindowDays),
            TimeSpan.FromDays(settings.SigningKeyRetiredCleanupDays));
    }

    public async Task<SqlOSResolvedSecuritySettings> GetResolvedSecuritySettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSecuritySettingsAsync(cancellationToken);
        return new SqlOSResolvedSecuritySettings(
            TimeSpan.FromMinutes(settings.RefreshTokenLifetimeMinutes),
            TimeSpan.FromMinutes(settings.SessionIdleTimeoutMinutes),
            TimeSpan.FromMinutes(settings.SessionAbsoluteLifetimeMinutes),
            TimeSpan.FromSeconds(settings.RefreshTokenGraceWindowSeconds));
    }

    public async Task<SqlOSSecuritySettingsDto> UpdateSecuritySettingsAsync(SqlOSUpdateSecuritySettingsRequest request, CancellationToken cancellationToken = default)
    {
        if (request.RefreshTokenLifetimeMinutes <= 0 || request.SessionIdleTimeoutMinutes <= 0 || request.SessionAbsoluteLifetimeMinutes <= 0)
        {
            throw new InvalidOperationException("Security settings must be positive minute values.");
        }

        if (request.SigningKeyRotationIntervalDays <= 0 || request.SigningKeyGraceWindowDays <= 0 || request.SigningKeyRetiredCleanupDays <= 0)
        {
            throw new InvalidOperationException("Signing key rotation settings must be positive day values.");
        }

        if (request.SigningKeyGraceWindowDays >= request.SigningKeyRotationIntervalDays)
        {
            throw new InvalidOperationException("Grace window must be shorter than the rotation interval.");
        }

        if (request.RefreshTokenGraceWindowSeconds < 0)
        {
            throw new InvalidOperationException("Refresh token grace window must be 0 or greater.");
        }

        // The grace window must not exceed the access token lifetime,
        // otherwise a grace window hit could legitimately return an
        // already-expired cached access token. The cached JWT inherits
        // the original access token expiry — once that expiry passes,
        // the cached token is useless to the caller.
        var accessTokenLifetimeSeconds = (int)_options.AccessTokenLifetime.TotalSeconds;
        if (request.RefreshTokenGraceWindowSeconds > accessTokenLifetimeSeconds)
        {
            throw new InvalidOperationException(
                $"Refresh token grace window must not exceed the access token lifetime ({accessTokenLifetimeSeconds} seconds).");
        }

        await EnsureDefaultSettingsAsync(cancellationToken);
        var settings = await _context.Set<SqlOSSettings>().FirstAsync(x => x.Id == "default", cancellationToken);
        settings.RefreshTokenLifetimeMinutes = request.RefreshTokenLifetimeMinutes;
        settings.SessionIdleTimeoutMinutes = request.SessionIdleTimeoutMinutes;
        settings.SessionAbsoluteLifetimeMinutes = request.SessionAbsoluteLifetimeMinutes;
        settings.SigningKeyRotationIntervalDays = request.SigningKeyRotationIntervalDays;
        settings.SigningKeyGraceWindowDays = request.SigningKeyGraceWindowDays;
        settings.SigningKeyRetiredCleanupDays = request.SigningKeyRetiredCleanupDays;
        settings.RefreshTokenGraceWindowSeconds = request.RefreshTokenGraceWindowSeconds;
        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return new SqlOSSecuritySettingsDto(
            settings.RefreshTokenLifetimeMinutes,
            settings.SessionIdleTimeoutMinutes,
            settings.SessionAbsoluteLifetimeMinutes,
            settings.SigningKeyRotationIntervalDays,
            settings.SigningKeyGraceWindowDays,
            settings.SigningKeyRetiredCleanupDays,
            settings.RefreshTokenGraceWindowSeconds,
            settings.UpdatedAt);
    }

    public async Task<SqlOSAuthPageSettingsDto> GetAuthPageSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDefaultAuthPageSettingsAsync(cancellationToken);
        var settings = await _context.Set<SqlOSAuthPageSettings>().FirstAsync(x => x.Id == "default", cancellationToken);
        return new SqlOSAuthPageSettingsDto(
            settings.LogoBase64,
            settings.PrimaryColor,
            settings.AccentColor,
            settings.BackgroundColor,
            settings.Layout,
            settings.PageTitle,
            settings.PageSubtitle,
            settings.EnablePasswordSignup,
            DeserializeCredentialTypes(settings.EnabledCredentialTypesJson),
            settings.UpdatedAt,
            _options.AuthPageSeed != null,
            _options.Headless.BuildUiUrl != null,
            _options.EnableLocalPasswordAuth,
            _emailSender.IsConfigured);
    }

    public async Task<SqlOSAuthPageSettingsDto> UpdateAuthPageSettingsAsync(SqlOSUpdateAuthPageSettingsRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.PrimaryColor) ||
            string.IsNullOrWhiteSpace(request.AccentColor) ||
            string.IsNullOrWhiteSpace(request.BackgroundColor))
        {
            throw new InvalidOperationException("Auth page colors are required.");
        }

        if (!string.Equals(request.Layout, "split", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Layout, "stacked", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Auth page layout must be either 'split' or 'stacked'.");
        }

        await EnsureDefaultAuthPageSettingsAsync(cancellationToken);
        var settings = await _context.Set<SqlOSAuthPageSettings>().FirstAsync(x => x.Id == "default", cancellationToken);
        settings.LogoBase64 = string.IsNullOrWhiteSpace(request.LogoBase64) ? null : request.LogoBase64;
        settings.PrimaryColor = request.PrimaryColor.Trim();
        settings.AccentColor = request.AccentColor.Trim();
        settings.BackgroundColor = request.BackgroundColor.Trim();
        settings.Layout = request.Layout.Trim().ToLowerInvariant();
        settings.PageTitle = request.PageTitle.Trim();
        settings.PageSubtitle = request.PageSubtitle.Trim();
        settings.EnablePasswordSignup = request.EnablePasswordSignup;
        settings.EnabledCredentialTypesJson = JsonSerializer.Serialize(
            (request.EnabledCredentialTypes ?? Array.Empty<string>())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());

        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return await GetAuthPageSettingsAsync(cancellationToken);
    }

    public async Task<SqlOSAuthEmailBrandingSettingsDto> GetAuthEmailBrandingSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDefaultAuthPageSettingsAsync(cancellationToken);
        var settings = await _context.Set<SqlOSAuthPageSettings>().FirstAsync(x => x.Id == "default", cancellationToken);
        var resolved = ResolveEmailBranding(settings);
        return new SqlOSAuthEmailBrandingSettingsDto(
            resolved.ApplicationName,
            resolved.LogoBase64,
            resolved.PrimaryColor,
            resolved.AccentColor,
            resolved.BackgroundColor,
            settings.UpdatedAt,
            _options.AuthEmailSeed != null);
    }

    public async Task<SqlOSAuthEmailBrandingSettingsDto> UpdateAuthEmailBrandingSettingsAsync(SqlOSUpdateAuthEmailBrandingSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureDefaultAuthPageSettingsAsync(cancellationToken);
        var settings = await _context.Set<SqlOSAuthPageSettings>().FirstAsync(x => x.Id == "default", cancellationToken);

        settings.EmailApplicationName = RequireText(request.ApplicationName, nameof(request.ApplicationName));
        settings.EmailLogoBase64 = string.IsNullOrWhiteSpace(request.LogoBase64) ? null : request.LogoBase64.Trim();
        settings.EmailPrimaryColor = RequireColor(request.PrimaryColor, nameof(request.PrimaryColor));
        settings.EmailAccentColor = RequireColor(request.AccentColor, nameof(request.AccentColor));
        settings.EmailBackgroundColor = RequireColor(request.BackgroundColor, nameof(request.BackgroundColor));
        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return await GetAuthEmailBrandingSettingsAsync(cancellationToken);
    }

    public async Task<SqlOSAuthEmailBranding> GetResolvedAuthEmailBrandingAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDefaultAuthPageSettingsAsync(cancellationToken);
        var settings = await _context.Set<SqlOSAuthPageSettings>().FirstAsync(x => x.Id == "default", cancellationToken);
        return ResolveEmailBranding(settings);
    }

    public async Task<SqlOSResolvedCredentialSettings> GetResolvedCredentialSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetAuthPageSettingsAsync(cancellationToken);
        var effectiveTypes = (settings.EnabledCredentialTypes ?? Array.Empty<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(value =>
                (string.Equals(value, "password", StringComparison.OrdinalIgnoreCase) && settings.LocalPasswordRuntimeEnabled)
                || (string.Equals(value, "email_otp", StringComparison.OrdinalIgnoreCase) && settings.EmailOtpRuntimeConfigured))
            .ToArray();

        var passwordEnabled = effectiveTypes.Contains("password", StringComparer.OrdinalIgnoreCase);
        var emailOtpEnabled = effectiveTypes.Contains("email_otp", StringComparer.OrdinalIgnoreCase);

        return new SqlOSResolvedCredentialSettings(
            effectiveTypes,
            passwordEnabled,
            passwordEnabled && settings.EnablePasswordSignup,
            emailOtpEnabled);
    }

    private static string[] DeserializeCredentialTypes(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? ["password"];
        }
        catch
        {
            return ["password"];
        }
    }

    private static string RequireColor(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required for SqlOS auth page seeding.");
        }

        return value.Trim();
    }

    private static string RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is required for SqlOS auth page seeding.");
        }

        return value.Trim();
    }

    private SqlOSAuthEmailBranding ResolveEmailBranding(SqlOSAuthPageSettings settings)
        => new(
            string.IsNullOrWhiteSpace(settings.EmailApplicationName)
                ? ResolveDefaultEmailApplicationName()
                : settings.EmailApplicationName.Trim(),
            string.IsNullOrWhiteSpace(settings.EmailLogoBase64)
                ? settings.LogoBase64
                : settings.EmailLogoBase64.Trim(),
            string.IsNullOrWhiteSpace(settings.EmailPrimaryColor)
                ? settings.PrimaryColor
                : settings.EmailPrimaryColor.Trim(),
            string.IsNullOrWhiteSpace(settings.EmailAccentColor)
                ? settings.AccentColor
                : settings.EmailAccentColor.Trim(),
            string.IsNullOrWhiteSpace(settings.EmailBackgroundColor)
                ? settings.BackgroundColor
                : settings.EmailBackgroundColor.Trim());

    private string ResolveDefaultEmailApplicationName()
    {
        if (!string.IsNullOrWhiteSpace(_options.Invitations.ApplicationName))
        {
            return _options.Invitations.ApplicationName.Trim();
        }

        return string.IsNullOrWhiteSpace(_options.EmailOtp.ApplicationName)
            ? "SqlOS"
            : _options.EmailOtp.ApplicationName.Trim();
    }
}

public sealed record SqlOSResolvedSecuritySettings(
    TimeSpan RefreshTokenLifetime,
    TimeSpan SessionIdleTimeout,
    TimeSpan SessionAbsoluteLifetime,
    TimeSpan RefreshTokenGraceWindow);

public sealed record SqlOSKeyRotationSettings(
    TimeSpan RotationInterval,
    TimeSpan GraceWindow,
    TimeSpan RetiredCleanupWindow);

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
    private static volatile string _cachedPresentationMode = "hosted";

    public string CurrentPresentationMode => _cachedPresentationMode;

    public SqlOSSettingsService(ISqlOSAuthServerDbContext context, IOptions<SqlOSAuthServerOptions> options)
    {
        _context = context;
        _options = options.Value;
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
            UpdatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task EnsureDefaultAuthPageSettingsAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _context.Set<SqlOSAuthPageSettings>().FirstOrDefaultAsync(x => x.Id == "default", cancellationToken);
        if (existing != null)
        {
            _cachedPresentationMode = existing.PresentationMode;
            return;
        }

        _context.Set<SqlOSAuthPageSettings>().Add(new SqlOSAuthPageSettings
        {
            Id = "default",
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

        var presentationMode = _options.AuthPageSeed.PresentationMode?.Trim().ToLowerInvariant() ?? "hosted";
        if (presentationMode != "hosted" && presentationMode != "headless")
        {
            throw new InvalidOperationException($"Auth page PresentationMode must be either 'hosted' or 'headless', got '{_options.AuthPageSeed.PresentationMode}'.");
        }
        settings.PresentationMode = presentationMode;

        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        _cachedPresentationMode = settings.PresentationMode;
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
            TimeSpan.FromMinutes(settings.SessionAbsoluteLifetimeMinutes));
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

        await EnsureDefaultSettingsAsync(cancellationToken);
        var settings = await _context.Set<SqlOSSettings>().FirstAsync(x => x.Id == "default", cancellationToken);
        settings.RefreshTokenLifetimeMinutes = request.RefreshTokenLifetimeMinutes;
        settings.SessionIdleTimeoutMinutes = request.SessionIdleTimeoutMinutes;
        settings.SessionAbsoluteLifetimeMinutes = request.SessionAbsoluteLifetimeMinutes;
        settings.SigningKeyRotationIntervalDays = request.SigningKeyRotationIntervalDays;
        settings.SigningKeyGraceWindowDays = request.SigningKeyGraceWindowDays;
        settings.SigningKeyRetiredCleanupDays = request.SigningKeyRetiredCleanupDays;
        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return new SqlOSSecuritySettingsDto(
            settings.RefreshTokenLifetimeMinutes,
            settings.SessionIdleTimeoutMinutes,
            settings.SessionAbsoluteLifetimeMinutes,
            settings.SigningKeyRotationIntervalDays,
            settings.SigningKeyGraceWindowDays,
            settings.SigningKeyRetiredCleanupDays,
            settings.UpdatedAt);
    }

    public async Task<SqlOSAuthPageSettingsDto> GetAuthPageSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDefaultAuthPageSettingsAsync(cancellationToken);
        var settings = await _context.Set<SqlOSAuthPageSettings>().FirstAsync(x => x.Id == "default", cancellationToken);
        _cachedPresentationMode = settings.PresentationMode;
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
            settings.PresentationMode,
            settings.UpdatedAt,
            _options.AuthPageSeed != null,
            _options.Headless.BuildUiUrl != null);
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

        var presentationMode = request.PresentationMode?.Trim().ToLowerInvariant() ?? "hosted";
        if (presentationMode != "hosted" && presentationMode != "headless")
        {
            throw new InvalidOperationException("Auth page PresentationMode must be either 'hosted' or 'headless'.");
        }
        settings.PresentationMode = presentationMode;

        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        _cachedPresentationMode = settings.PresentationMode;

        return await GetAuthPageSettingsAsync(cancellationToken);
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
}

public sealed record SqlOSResolvedSecuritySettings(
    TimeSpan RefreshTokenLifetime,
    TimeSpan SessionIdleTimeout,
    TimeSpan SessionAbsoluteLifetime);

public sealed record SqlOSKeyRotationSettings(
    TimeSpan RotationInterval,
    TimeSpan GraceWindow,
    TimeSpan RetiredCleanupWindow);

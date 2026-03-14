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
            UpdatedAt = DateTime.UtcNow,
        });
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
            settings.UpdatedAt);
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

        await EnsureDefaultSettingsAsync(cancellationToken);
        var settings = await _context.Set<SqlOSSettings>().FirstAsync(x => x.Id == "default", cancellationToken);
        settings.RefreshTokenLifetimeMinutes = request.RefreshTokenLifetimeMinutes;
        settings.SessionIdleTimeoutMinutes = request.SessionIdleTimeoutMinutes;
        settings.SessionAbsoluteLifetimeMinutes = request.SessionAbsoluteLifetimeMinutes;
        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return new SqlOSSecuritySettingsDto(
            settings.RefreshTokenLifetimeMinutes,
            settings.SessionIdleTimeoutMinutes,
            settings.SessionAbsoluteLifetimeMinutes,
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
            settings.UpdatedAt);
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
}

public sealed record SqlOSResolvedSecuritySettings(
    TimeSpan RefreshTokenLifetime,
    TimeSpan SessionIdleTimeout,
    TimeSpan SessionAbsoluteLifetime);

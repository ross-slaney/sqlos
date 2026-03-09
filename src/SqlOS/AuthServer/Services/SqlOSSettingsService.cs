using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;

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
}

public sealed record SqlOSResolvedSecuritySettings(
    TimeSpan RefreshTokenLifetime,
    TimeSpan SessionIdleTimeout,
    TimeSpan SessionAbsoluteLifetime);

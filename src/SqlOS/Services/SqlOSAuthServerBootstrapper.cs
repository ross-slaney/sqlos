using Microsoft.Extensions.Logging;
using SqlOS.AuthServer.Services;

namespace SqlOS.Services;

public sealed class SqlOSAuthServerBootstrapper
{
    private readonly SqlOSSchemaInitializer _schemaInitializer;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly ILogger<SqlOSAuthServerBootstrapper> _logger;

    public SqlOSAuthServerBootstrapper(
        SqlOSSchemaInitializer schemaInitializer,
        SqlOSCryptoService cryptoService,
        SqlOSAdminService adminService,
        SqlOSSettingsService settingsService,
        ILogger<SqlOSAuthServerBootstrapper> logger)
    {
        _schemaInitializer = schemaInitializer;
        _cryptoService = cryptoService;
        _adminService = adminService;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing SqlOS AuthServer...");
        await _schemaInitializer.EnsureSchemaAsync(cancellationToken);
        await _cryptoService.EnsureActiveSigningKeyAsync(cancellationToken);
        await _adminService.EnsureDefaultClientAsync(cancellationToken);
        await _settingsService.EnsureDefaultSettingsAsync(cancellationToken);
        await _settingsService.EnsureDefaultAuthPageSettingsAsync(cancellationToken);
        await _adminService.CleanupExpiredTemporaryTokensAsync(cancellationToken);
        await _adminService.CleanupExpiredRefreshTokensAsync(cancellationToken);
        _logger.LogInformation("SqlOS AuthServer initialization complete.");
    }
}

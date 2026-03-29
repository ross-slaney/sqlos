using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlOS.Configuration;
using SqlOS.AuthServer.Services;
using SqlOS.Fga.Services;

namespace SqlOS.Services;

public sealed class SqlOSBootstrapper
{
    private readonly SqlOSOptions _options;
    private readonly SqlOSSchemaInitializer _authSchemaInitializer;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly SqlOSFgaSchemaInitializer _fgaSchemaInitializer;
    private readonly SqlOSFgaFunctionInitializer _fgaFunctionInitializer;
    private readonly SqlOSFgaSeedService _fgaSeedService;
    private readonly ILogger<SqlOSBootstrapper> _logger;

    public SqlOSBootstrapper(
        IOptions<SqlOSOptions> options,
        SqlOSSchemaInitializer authSchemaInitializer,
        SqlOSCryptoService cryptoService,
        SqlOSAdminService adminService,
        SqlOSSettingsService settingsService,
        SqlOSFgaSchemaInitializer fgaSchemaInitializer,
        SqlOSFgaFunctionInitializer fgaFunctionInitializer,
        SqlOSFgaSeedService fgaSeedService,
        ILogger<SqlOSBootstrapper> logger)
    {
        _options = options.Value;
        _authSchemaInitializer = authSchemaInitializer;
        _cryptoService = cryptoService;
        _adminService = adminService;
        _settingsService = settingsService;
        _fgaSchemaInitializer = fgaSchemaInitializer;
        _fgaFunctionInitializer = fgaFunctionInitializer;
        _fgaSeedService = fgaSeedService;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing SqlOS...");

        await _authSchemaInitializer.EnsureSchemaAsync(cancellationToken);
        await _cryptoService.EnsureActiveSigningKeyAsync(cancellationToken);
        await _settingsService.EnsureDefaultSettingsAsync(cancellationToken);
        await _settingsService.EnsureDefaultAuthPageSettingsAsync(cancellationToken);
        await _settingsService.UpsertSeededAuthPageSettingsAsync(cancellationToken);
        await _adminService.UpsertSeededClientsAsync(cancellationToken);
        await _adminService.CleanupExpiredTemporaryTokensAsync(cancellationToken);
        await _adminService.CleanupExpiredRefreshTokensAsync(cancellationToken);
        await _adminService.CleanupStaleDynamicClientsAsync(cancellationToken);

        await _fgaSchemaInitializer.EnsureSchemaAsync(cancellationToken);

        if (_options.Fga.InitializeFunctions)
        {
            await _fgaFunctionInitializer.EnsureFunctionsExistAsync(cancellationToken);
        }

        if (_options.Fga.SeedCoreData)
        {
            await _fgaSeedService.SeedCoreAsync(cancellationToken);
        }

        if (_options.Fga.StartupSeedData != null)
        {
            await _fgaSeedService.SeedAuthorizationDataAsync(_options.Fga.StartupSeedData, cancellationToken);
        }

        _logger.LogInformation("SqlOS initialization complete.");
    }
}

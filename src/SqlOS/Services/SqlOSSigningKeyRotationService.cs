using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlOS.AuthServer.Services;

namespace SqlOS.Services;

public sealed class SqlOSSigningKeyRotationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SqlOSSigningKeyRotationService> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    public SqlOSSigningKeyRotationService(
        IServiceScopeFactory scopeFactory,
        ILogger<SqlOSSigningKeyRotationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay initial check to let the bootstrapper finish
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckAndRotateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during signing key rotation check.");
            }
        }
    }

    private async Task CheckAndRotateAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<SqlOSSettingsService>();
        var cryptoService = scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>();
        var adminService = scope.ServiceProvider.GetRequiredService<SqlOSAdminService>();

        var rotationSettings = await settingsService.GetKeyRotationSettingsAsync(cancellationToken);

        if (await cryptoService.ShouldRotateSigningKeyAsync(rotationSettings.RotationInterval, cancellationToken))
        {
            _logger.LogInformation("Signing key rotation interval exceeded. Rotating signing key...");
            var newKey = await cryptoService.RotateSigningKeyAsync(cancellationToken);
            await adminService.RecordAuditAsync(
                "signing_key_rotated",
                "system",
                "key-rotation-service",
                data: new { newKeyId = newKey.Id, newKid = newKey.Kid },
                cancellationToken: cancellationToken);
            _logger.LogInformation("Signing key rotated successfully. New key ID: {KeyId}, Kid: {Kid}.", newKey.Id, newKey.Kid);
        }

        var cleaned = await cryptoService.CleanupRetiredSigningKeysAsync(
            rotationSettings.RetiredCleanupWindow, cancellationToken);
        if (cleaned > 0)
        {
            _logger.LogInformation("Cleaned up {Count} retired signing key(s).", cleaned);
        }
    }
}

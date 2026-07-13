using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlOS.Configuration;

namespace SqlOS.Calendar.Services;

/// <summary>
/// Background scheduler for read-pull and two-way calendar connections. Follows the
/// <see cref="Services"/> hosted-service conventions: initial delay for the bootstrapper,
/// a <see cref="PeriodicTimer"/> loop, and a fresh DI scope per pass.
/// </summary>
public sealed class SqlOSCalendarSyncHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SqlOSOptions _options;
    private readonly ILogger<SqlOSCalendarSyncHostedService> _logger;

    public SqlOSCalendarSyncHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<SqlOSOptions> options,
        ILogger<SqlOSCalendarSyncHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scheduler = _options.Calendar.SyncScheduler;
        if (!_options.Calendar.Enabled || !scheduler.Enabled)
        {
            return;
        }

        // Delay the first pass to let the bootstrapper finish
        await Task.Delay(scheduler.InitialDelay, stoppingToken);

        using var timer = new PeriodicTimer(scheduler.Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SyncDueConnectionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during scheduled calendar sync pass.");
            }
        }
    }

    private async Task SyncDueConnectionsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var syncService = scope.ServiceProvider.GetRequiredService<SqlOSCalendarSyncService>();
        var synced = await syncService.SyncDueConnectionsAsync(cancellationToken);
        if (synced > 0)
        {
            _logger.LogInformation("Scheduled calendar sync completed for {Count} connection(s).", synced);
        }
    }
}

using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlOS.Database;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Services;

/// <summary>
/// Keeps the grant counts behind authorized pages aligned with the clock, ahead of time. The triggers count a
/// grant when it is written and usable; a grant whose window opens or closes later changes nothing in the
/// database by itself, so this service wakes at the next window boundary (and at least every hour) and
/// rebuilds the counts of the principals whose grants crossed one since its last run. A page does not depend
/// on it: a caller whose counts fell behind the clock (each principal's root count row carries the next
/// boundary) has them rebuilt before the walk, at the cost of that one page.
/// </summary>
public sealed class SqlOSFgaGrantCountsRefreshService(
    IServiceScopeFactory scopes,
    IOptions<SqlOSFgaOptions> options,
    ILogger<SqlOSFgaGrantCountsRefreshService> logger) : BackgroundService
{
    /// <summary>The longest the service sleeps between checks; a boundary found in the grants wakes it sooner.</summary>
    internal static TimeSpan MaxSleep { get; set; } = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var last = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            DateTime? boundary = null;
            try
            {
                boundary = await StepAsync(last, stoppingToken);
                last = DateTime.UtcNow;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The SqlOS FGA grant counts refresh failed; it will try again.");
            }

            var wait = boundary is { } next && next > DateTime.UtcNow ? next - DateTime.UtcNow + TimeSpan.FromSeconds(1) : MaxSleep;
            if (wait > MaxSleep)
            {
                wait = MaxSleep;
            }

            try
            {
                await Task.Delay(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Rebuilds the counts of the principals whose grants crossed a boundary in (last, now]; returns the next boundary.</summary>
    internal async Task<DateTime?> StepAsync(DateTime last, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ISqlOSFgaDbContext>();
        if (!context.Database.IsRelational())
        {
            return null;
        }

        var provider = SqlOSDatabase.Resolve(context.Database);
        var now = DateTime.UtcNow;
        var connection = context.Database.GetDbConnection();
        var opened = false;
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
            opened = true;
        }

        try
        {
            await using (var refresh = connection.CreateCommand())
            {
                refresh.CommandText = provider.BuildCountsRefreshSql(options.Value);
                refresh.CommandTimeout = 0;
                refresh.Parameters.Add(provider.CreateParameter("@From", last));
                refresh.Parameters.Add(provider.CreateParameter("@To", now));
                var refreshed = await refresh.ExecuteScalarAsync(cancellationToken);
                if (refreshed is not null and not DBNull && Convert.ToInt32(refreshed, CultureInfo.InvariantCulture) is var n and > 0)
                {
                    logger.LogInformation("Rebuilt the SqlOS FGA grant counts of {Principals} principal(s) whose grants opened or expired.", n);
                }
            }

            await using var next = connection.CreateCommand();
            next.CommandText = provider.BuildNextValidityBoundarySql(options.Value);
            next.Parameters.Add(provider.CreateParameter("@Now", now));
            var value = await next.ExecuteScalarAsync(cancellationToken);
            return value is DateTime boundary ? DateTime.SpecifyKind(boundary, DateTimeKind.Utc) : null;
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }
}

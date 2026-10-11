using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlOS.Database;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Services;

public class SqlOSFgaFunctionInitializer
{
    private readonly ISqlOSFgaDbContext _context;
    private readonly SqlOSFgaOptions _options;
    private readonly ILogger<SqlOSFgaFunctionInitializer> _logger;

    public SqlOSFgaFunctionInitializer(
        ISqlOSFgaDbContext context,
        IOptions<SqlOSFgaOptions> options,
        ILogger<SqlOSFgaFunctionInitializer> logger)
    {
        _context = context;
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureFunctionsExistAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Ensuring database functions exist...");
        await EnsureIsResourceAccessibleFunctionAsync(cancellationToken);
        _logger.LogInformation("Database functions verified.");
    }

    private async Task EnsureIsResourceAccessibleFunctionAsync(CancellationToken cancellationToken)
    {
        var functionSql = SqlOSDatabase.Resolve(_context.Database).BuildIsResourceAccessibleFunctionSql(_options);

        try
        {
            _logger.LogDebug("Creating or updating fn_IsResourceAccessible TVF...");
            if (_context.Database.IsRelational() && _context.Database.CurrentTransaction == null)
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
                await SqlOSDatabase.AcquireExclusiveTransactionLockAsync(
                    _context.Database,
                    "SqlOS:FgaFunctionInitializer",
                    TimeSpan.FromSeconds(30),
                    "Could not acquire the SqlOS FGA function lock.",
                    cancellationToken);
                await _context.Database.ExecuteSqlRawAsync(functionSql, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await _context.Database.ExecuteSqlRawAsync(functionSql, cancellationToken);
            }

            _logger.LogInformation("fn_IsResourceAccessible TVF is ready.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create or update fn_IsResourceAccessible TVF. Authorization queries may fail.");
            throw;
        }
    }

    internal static string BuildIsResourceAccessibleFunctionSql(SqlOSFgaOptions options)
        => SqlServerDatabaseProvider.Instance.BuildIsResourceAccessibleFunctionSql(options);
}

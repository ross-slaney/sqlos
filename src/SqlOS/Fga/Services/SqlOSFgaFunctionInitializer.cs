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
        var provider = SqlOSDatabase.Resolve(_context.Database);

        // fn_PageNodes calls fn_ResourceAncestors, so the ancestors function is installed first.
        var functions = new (string Name, string Sql)[]
        {
            ("fn_IsResourceAccessible", provider.BuildIsResourceAccessibleFunctionSql(_options)),
            ("fn_ResourceAncestors", provider.BuildResourceAncestorsFunctionSql(_options)),
            ("fn_PageNodes", provider.BuildPageNodesFunctionSql(_options)),
        };

        if (_context.Database.IsRelational() && _context.Database.CurrentTransaction == null)
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            await SqlOSDatabase.AcquireExclusiveTransactionLockAsync(
                _context.Database,
                "SqlOS:FgaFunctionInitializer",
                TimeSpan.FromSeconds(30),
                "Could not acquire the SqlOS FGA function lock.",
                cancellationToken);
            foreach (var function in functions)
            {
                await EnsureFunctionAsync(function.Name, function.Sql, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            foreach (var function in functions)
            {
                await EnsureFunctionAsync(function.Name, function.Sql, cancellationToken);
            }
        }

        _logger.LogInformation("Database functions verified.");
    }

    private async Task EnsureFunctionAsync(string name, string functionSql, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Creating or updating {FunctionName}...", name);
            await _context.Database.ExecuteSqlRawAsync(functionSql, cancellationToken);
            _logger.LogInformation("{FunctionName} is ready.", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create or update {FunctionName}. Authorization queries may fail.", name);
            throw;
        }
    }

    internal static string BuildIsResourceAccessibleFunctionSql(SqlOSFgaOptions options)
        => SqlServerDatabaseProvider.Instance.BuildIsResourceAccessibleFunctionSql(options);
}

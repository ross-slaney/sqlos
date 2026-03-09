using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        var schema = _options.Schema;
        var tables = _options.TableNames;

        var dropFunctionSql = $"DROP FUNCTION IF EXISTS [{schema}].fn_IsResourceAccessible";

        var createFunctionSql = $@"
CREATE FUNCTION [{schema}].fn_IsResourceAccessible(
    @ResourceId NVARCHAR(128),
    @SubjectIds NVARCHAR(MAX),
    @PermissionId NVARCHAR(128)
)
RETURNS TABLE
AS
RETURN
(
    WITH ancestors AS (
        SELECT Id, ParentId, 0 AS Depth
        FROM [{schema}].[{tables.Resources}]
        WHERE Id = @ResourceId

        UNION ALL

        SELECT r.Id, r.ParentId, a.Depth + 1
        FROM [{schema}].[{tables.Resources}] r
        INNER JOIN ancestors a ON r.Id = a.ParentId
    )
    SELECT TOP 1 a.Id
    FROM ancestors a
    INNER JOIN [{schema}].[{tables.Grants}] g ON a.Id = g.ResourceId
    INNER JOIN [{schema}].[{tables.RolePermissions}] rp ON g.RoleId = rp.RoleId
    WHERE g.SubjectId IN (SELECT LTRIM(RTRIM(value)) FROM STRING_SPLIT(@SubjectIds, ','))
      AND rp.PermissionId = @PermissionId
      AND (g.EffectiveFrom IS NULL OR g.EffectiveFrom <= GETUTCDATE())
      AND (g.EffectiveTo IS NULL OR g.EffectiveTo >= GETUTCDATE())
)";

        try
        {
            _logger.LogDebug("Creating fn_IsResourceAccessible TVF...");
            await _context.Database.ExecuteSqlRawAsync(dropFunctionSql, cancellationToken);
            await _context.Database.ExecuteSqlRawAsync(createFunctionSql, cancellationToken);
            _logger.LogInformation("fn_IsResourceAccessible TVF created successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create fn_IsResourceAccessible TVF. Authorization queries may fail.");
            throw;
        }
    }
}

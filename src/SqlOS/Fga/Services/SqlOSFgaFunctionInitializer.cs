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
        var maxDepth = Math.Max(1, _options.MaxResourceHierarchyDepth);

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
        WHERE Id = @ResourceId AND IsActive = 1

        UNION ALL

	        SELECT r.Id, r.ParentId, a.Depth + 1
	        FROM [{schema}].[{tables.Resources}] r
	        INNER JOIN ancestors a ON r.Id = a.ParentId
	        WHERE a.Depth < {maxDepth} AND r.IsActive = 1
	    )
    SELECT TOP 1 a.Id
    FROM ancestors a
    INNER JOIN [{schema}].[{tables.Grants}] g ON a.Id = g.ResourceId
    INNER JOIN [{schema}].[{tables.RolePermissions}] rp ON g.RoleId = rp.RoleId
    INNER JOIN [{schema}].[{tables.Subjects}] s ON g.SubjectId = s.Id
    LEFT JOIN [{schema}].[{tables.Users}] u ON s.Id = u.SubjectId
    LEFT JOIN [{schema}].[{tables.ServiceAccounts}] sa ON s.Id = sa.SubjectId
    LEFT JOIN [{schema}].[{tables.UserGroups}] ug ON s.Id = ug.SubjectId
    LEFT JOIN [{schema}].[{tables.Agents}] ag ON s.Id = ag.SubjectId
    WHERE g.SubjectId IN (SELECT CONVERT(NVARCHAR(450), [value]) FROM OPENJSON(@SubjectIds))
      AND rp.PermissionId = @PermissionId
      AND (s.SubjectTypeId <> 'user' OR u.IsActive = 1)
      AND (s.SubjectTypeId <> 'service_account' OR (sa.SubjectId IS NOT NULL AND (sa.ExpiresAt IS NULL OR sa.ExpiresAt > GETUTCDATE())))
      AND (s.SubjectTypeId <> 'group' OR ug.IsActive = 1)
      AND (s.SubjectTypeId <> 'agent' OR ag.SubjectId IS NOT NULL)
      AND EXISTS (
          SELECT 1
          FROM [{schema}].[{tables.Subjects}] caller
          LEFT JOIN [{schema}].[{tables.Users}] callerUser ON caller.Id = callerUser.SubjectId
          LEFT JOIN [{schema}].[{tables.ServiceAccounts}] callerSa ON caller.Id = callerSa.SubjectId
          LEFT JOIN [{schema}].[{tables.UserGroups}] callerGroup ON caller.Id = callerGroup.SubjectId
          LEFT JOIN [{schema}].[{tables.Agents}] callerAgent ON caller.Id = callerAgent.SubjectId
          WHERE caller.Id = JSON_VALUE(@SubjectIds, '$[0]')
            AND (caller.SubjectTypeId <> 'user' OR callerUser.IsActive = 1)
            AND (caller.SubjectTypeId <> 'service_account' OR (callerSa.SubjectId IS NOT NULL AND (callerSa.ExpiresAt IS NULL OR callerSa.ExpiresAt > GETUTCDATE())))
            AND (caller.SubjectTypeId <> 'group' OR callerGroup.IsActive = 1)
            AND (caller.SubjectTypeId <> 'agent' OR callerAgent.SubjectId IS NOT NULL)
      )
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

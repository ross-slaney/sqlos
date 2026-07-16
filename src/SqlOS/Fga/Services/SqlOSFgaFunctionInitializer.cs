using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
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
        var functionSql = BuildIsResourceAccessibleFunctionSql(_options);

        try
        {
            _logger.LogDebug("Creating or updating fn_IsResourceAccessible TVF...");
            await _context.Database.ExecuteSqlRawAsync(functionSql, cancellationToken);
            _logger.LogInformation("fn_IsResourceAccessible TVF is ready.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create or update fn_IsResourceAccessible TVF. Authorization queries may fail.");
            throw;
        }
    }

    internal static string BuildIsResourceAccessibleFunctionSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var schema = EscapeIdentifier(options.Schema);
        var tables = options.TableNames;
        var resources = EscapeIdentifier(tables.Resources);
        var grants = EscapeIdentifier(tables.Grants);
        var rolePermissions = EscapeIdentifier(tables.RolePermissions);
        var subjects = EscapeIdentifier(tables.Subjects);
        var users = EscapeIdentifier(tables.Users);
        var serviceAccounts = EscapeIdentifier(tables.ServiceAccounts);
        var userGroups = EscapeIdentifier(tables.UserGroups);
        var agents = EscapeIdentifier(tables.Agents);
        var permissions = EscapeIdentifier(tables.Permissions);
        var maxDepth = Math.Max(1, options.MaxResourceHierarchyDepth)
            .ToString(CultureInfo.InvariantCulture);

        return $@"
CREATE OR ALTER FUNCTION [{schema}].fn_IsResourceAccessible(
    @ResourceId NVARCHAR(128),
    @SubjectIds NVARCHAR(MAX),
    @PermissionId NVARCHAR(128)
)
RETURNS TABLE
AS
RETURN
(
    WITH ancestors AS (
        SELECT
            Id,
            ParentId,
            0 AS Depth,
            CAST(N'|' + Id + N'|' AS NVARCHAR(MAX)) AS VisitedPath,
            CAST(0 AS BIT) AS CycleDetected
        FROM [{schema}].[{resources}]
        WHERE Id = @ResourceId AND IsActive = 1

        UNION ALL

        SELECT
            r.Id,
            r.ParentId,
            a.Depth + 1,
            CAST(a.VisitedPath + r.Id + N'|' AS NVARCHAR(MAX)),
            CAST(CASE
                WHEN CHARINDEX(N'|' + r.Id + N'|', a.VisitedPath) > 0 THEN 1
                ELSE 0
            END AS BIT)
        FROM [{schema}].[{resources}] r
        INNER JOIN ancestors a ON r.Id = a.ParentId
        WHERE a.Depth < {maxDepth}
          AND a.CycleDetected = 0
          AND r.IsActive = 1
    )
    SELECT TOP 1 a.Id
    FROM ancestors a
    INNER JOIN [{schema}].[{grants}] g ON a.Id = g.ResourceId
    INNER JOIN [{schema}].[{rolePermissions}] rp ON g.RoleId = rp.RoleId
    INNER JOIN [{schema}].[{subjects}] s ON g.SubjectId = s.Id
    LEFT JOIN [{schema}].[{users}] u ON s.Id = u.SubjectId
    LEFT JOIN [{schema}].[{serviceAccounts}] sa ON s.Id = sa.SubjectId
    LEFT JOIN [{schema}].[{userGroups}] ug ON s.Id = ug.SubjectId
    LEFT JOIN [{schema}].[{agents}] ag ON s.Id = ag.SubjectId
    WHERE g.SubjectId IN (SELECT CONVERT(NVARCHAR(450), [value]) FROM OPENJSON(@SubjectIds))
      AND rp.PermissionId = @PermissionId
      AND NOT EXISTS (SELECT 1 FROM ancestors malformed WHERE malformed.CycleDetected = 1)
      AND NOT EXISTS (
          SELECT 1
          FROM ancestors truncated
          WHERE truncated.Depth = {maxDepth}
            AND truncated.ParentId IS NOT NULL
      )
      AND EXISTS (
          SELECT 1
          FROM [{schema}].[{resources}] target
          INNER JOIN [{schema}].[{permissions}] permission ON permission.Id = @PermissionId
          WHERE target.Id = @ResourceId
            AND (permission.ResourceTypeId IS NULL OR permission.ResourceTypeId = target.ResourceTypeId)
      )
      AND (s.SubjectTypeId <> 'user' OR u.IsActive = 1)
      AND (s.SubjectTypeId <> 'service_account' OR (sa.SubjectId IS NOT NULL AND (sa.ExpiresAt IS NULL OR sa.ExpiresAt > GETUTCDATE())))
      AND (s.SubjectTypeId <> 'group' OR ug.IsActive = 1)
      AND (s.SubjectTypeId <> 'agent' OR ag.SubjectId IS NOT NULL)
      AND EXISTS (
          SELECT 1
          FROM [{schema}].[{subjects}] caller
          LEFT JOIN [{schema}].[{users}] callerUser ON caller.Id = callerUser.SubjectId
          LEFT JOIN [{schema}].[{serviceAccounts}] callerSa ON caller.Id = callerSa.SubjectId
          LEFT JOIN [{schema}].[{userGroups}] callerGroup ON caller.Id = callerGroup.SubjectId
          LEFT JOIN [{schema}].[{agents}] callerAgent ON caller.Id = callerAgent.SubjectId
          WHERE caller.Id = JSON_VALUE(@SubjectIds, '$[0]')
            AND (caller.SubjectTypeId <> 'user' OR callerUser.IsActive = 1)
            AND (caller.SubjectTypeId <> 'service_account' OR (callerSa.SubjectId IS NOT NULL AND (callerSa.ExpiresAt IS NULL OR callerSa.ExpiresAt > GETUTCDATE())))
            AND (caller.SubjectTypeId <> 'group' OR callerGroup.IsActive = 1)
            AND (caller.SubjectTypeId <> 'agent' OR callerAgent.SubjectId IS NOT NULL)
      )
      AND (g.EffectiveFrom IS NULL OR g.EffectiveFrom <= GETUTCDATE())
      AND (g.EffectiveTo IS NULL OR g.EffectiveTo >= GETUTCDATE())
)";
    }

    private static string EscapeIdentifier(string identifier)
        => identifier.Replace("]", "]]", StringComparison.Ordinal);
}

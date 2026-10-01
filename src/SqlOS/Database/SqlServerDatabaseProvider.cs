using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SqlOS.Fga.Configuration;

namespace SqlOS.Database;

internal sealed partial class SqlServerDatabaseProvider : ISqlOSDatabaseProvider
{
    public static SqlServerDatabaseProvider Instance { get; } = new();

    public SqlOSDatabaseProviderKind Kind => SqlOSDatabaseProviderKind.SqlServer;
    public string EfProviderName => SqlOSDatabase.SqlServerProviderName;
    public string DisplayName => "SQL Server";
    public string AuthMigrationResourcePrefix => "SqlOS.AuthServer.Schema.";
    public string FgaMigrationResourcePrefix => "SqlOS.Fga.Schema.";
    public string MaxStringStoreType => "nvarchar(max)";

    public string QuoteIdentifier(string identifier)
        => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public string Qualify(string schema, string name)
        => $"{QuoteIdentifier(schema)}.{QuoteIdentifier(name)}";

    public string FilteredIndexIsNotNull(string column) => $"{QuoteIdentifier(column)} IS NOT NULL";
    public string FilteredIndexEqualsTrue(string column) => $"{QuoteIdentifier(column)} = 1";

    public IReadOnlyList<string> SplitBatches(string sql)
        => Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Where(static batch => !string.IsNullOrWhiteSpace(batch))
            .ToArray();

    public DbParameter CreateParameter(string name, object? value)
        => new SqlParameter(name, value ?? DBNull.Value);

    public string BuildEnsureAuthVersionTablesSql(string schema)
    {
        var quoted = QuoteIdentifier(schema);
        return $"""
            IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = '{schema}')
            BEGIN
                EXEC('CREATE SCHEMA {quoted}');
            END
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSSchema' AND schema_id = SCHEMA_ID('{schema}'))
            BEGIN
                CREATE TABLE {quoted}.[SqlOSSchema] ([Version] INT NOT NULL);
            END
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSAppliedMigrations' AND schema_id = SCHEMA_ID('{schema}'))
            BEGIN
                CREATE TABLE {quoted}.[SqlOSAppliedMigrations] (
                    [Sequence] BIGINT IDENTITY(1,1) NOT NULL,
                    [ScriptName] NVARCHAR(450) NOT NULL,
                    [Version] INT NOT NULL,
                    [AppliedAt] DATETIME2 NOT NULL,
                    CONSTRAINT [PK_SqlOSAppliedMigrations] PRIMARY KEY ([ScriptName]),
                    CONSTRAINT [UX_SqlOSAppliedMigrations_Sequence] UNIQUE ([Sequence])
                );
            END
            """;
    }

    public string BuildSelectVersionSql(string schema)
        => $"SELECT TOP 1 [Version] FROM {Qualify(schema, "SqlOSSchema")}";

    public string BuildSelectAppliedMigrationsSql(string schema)
        => $"SELECT [ScriptName] FROM {Qualify(schema, "SqlOSAppliedMigrations")}";

    public string BuildRecordAppliedMigrationSql(string schema)
        => $"""
            IF NOT EXISTS (SELECT 1 FROM {Qualify(schema, "SqlOSAppliedMigrations")} WHERE [ScriptName] = @scriptName)
            BEGIN
                INSERT INTO {Qualify(schema, "SqlOSAppliedMigrations")} ([ScriptName], [Version], [AppliedAt])
                VALUES (@scriptName, @version, SYSUTCDATETIME());
            END
            """;

    public string BuildUpdateVersionSql(string schema)
        => $"UPDATE {Qualify(schema, "SqlOSSchema")} SET [Version] = @targetVersion";

    public string BuildEnsureFgaVersionTableSql(string schema)
        => $"""
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SqlOSFgaSchema' AND schema_id = SCHEMA_ID('{schema}'))
            BEGIN
                CREATE TABLE {Qualify(schema, "SqlOSFgaSchema")} ([Version] INT NOT NULL);
            END
            """;

    public string BuildSelectFgaVersionSql(string schema)
        => $"SELECT TOP 1 [Version] FROM {Qualify(schema, "SqlOSFgaSchema")}";

    public string BuildLockedSelectSql(string schema, string table, string whereSql, string? orderBySql = null)
    {
        var sql = $"SELECT * FROM {Qualify(schema, table)} WITH (UPDLOCK, HOLDLOCK) WHERE {whereSql}";
        if (!string.IsNullOrWhiteSpace(orderBySql))
        {
            sql += $" ORDER BY {orderBySql}";
        }

        return sql;
    }

    /// <summary>
    /// <c>fn_IsResourceAccessible</c>: the row filter. It walks up from the target over active resources, as the
    /// paper describes, and looks for a current grant on each ancestor by the grants' ResourceId index, held
    /// by one of the caller's live subjects (<c>fn_ActiveSubjects</c>) through a role that carries the
    /// permission. Nothing in it depends on how many grants the caller holds elsewhere.
    /// </summary>
    public string BuildIsResourceAccessibleFunctionSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        var tables = options.TableNames;
        var resources = Escape(tables.Resources);
        var permissions = Escape(tables.Permissions);
        var grants = Escape(tables.Grants);
        var rolePermissions = Escape(tables.RolePermissions);
        var maxDepth = Math.Max(1, options.MaxResourceHierarchyDepth)
            .ToString(CultureInfo.InvariantCulture);
        return $"""
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
                WHERE EXISTS (
                      SELECT 1
                      FROM [{schema}].[{grants}] g
                      INNER JOIN [{schema}].[{rolePermissions}] rp ON rp.RoleId = g.RoleId AND rp.PermissionId = @PermissionId
                      WHERE g.ResourceId = a.Id
                        AND g.SubjectId IN (SELECT live.SubjectId FROM [{schema}].fn_ActiveSubjects(@SubjectIds) live)
                        AND (g.EffectiveFrom IS NULL OR g.EffectiveFrom <= GETUTCDATE())
                        AND (g.EffectiveTo IS NULL OR g.EffectiveTo >= GETUTCDATE())
                  )
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
            )
            """;
    }

    public async Task AcquireTransactionLockAsync(
        DatabaseFacade database,
        string resource,
        TimeSpan timeout,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var timeoutMs = Math.Max(0, (int)timeout.TotalMilliseconds);
        var escaped = failureMessage.Replace("'", "''", StringComparison.Ordinal);
        await database.ExecuteSqlRawAsync(
            $"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = {timeoutMs};
            IF @result < 0 THROW 51000, '{escaped}', 1;
            """,
            [CreateParameter("@resource", resource)],
            cancellationToken);
    }

    public async Task AcquireSessionLockAsync(
        DatabaseFacade database,
        string resource,
        TimeSpan timeout,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var timeoutMs = Math.Max(0, (int)timeout.TotalMilliseconds);
        var escaped = failureMessage.Replace("'", "''", StringComparison.Ordinal);
        await database.ExecuteSqlRawAsync(
            $"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Session',
                @LockTimeout = {timeoutMs};
            IF @result < 0 THROW 51000, '{escaped}', 1;
            """,
            [CreateParameter("@resource", resource)],
            cancellationToken);
    }

    public Task ReleaseSessionLockAsync(DatabaseFacade database, string resource, CancellationToken cancellationToken)
        => database.ExecuteSqlRawAsync(
            "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';",
            [CreateParameter("@resource", resource)],
            cancellationToken);

    private static string Escape(string identifier)
        => identifier.Replace("]", "]]", StringComparison.Ordinal);
}

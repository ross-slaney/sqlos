using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SqlOS.Fga;
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
    /// <c>fn_IsResourceAccessible(@ResourceId, @SubjectIds, @PermissionId)</c>: the point check. The target's
    /// lineage names its ancestor at every level; the levels its reach covers are the active path a grant may
    /// sit on. One grant seek per such ancestor and live subject, on (ResourceSeq, SubjectId) (schema v16): the
    /// cost is the depth of the tree times the caller's subjects, never the number of grants the caller holds,
    /// nor the number other people hold on the same ancestors. The seek sits under a TOP, which the optimizer
    /// does not move conditions past, so it stays one exact seek per subject instead of a read of every grant on
    /// the ancestor. Returns the grant that decides it (on the nearest ancestor that holds one): the ancestor's
    /// id, the grant, its subject and role, and the ancestor's level; or no row.
    /// </summary>
    public string BuildIsResourceAccessibleFunctionSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        var tables = options.TableNames;
        var resources = Escape(tables.Resources);
        var grants = Escape(tables.Grants);
        var rolePermissions = Escape(tables.RolePermissions);
        var permissions = Escape(tables.Permissions);
        var levels = string.Join(", ", Enumerable.Range(0, SqlOSFgaLineage.Levels(options))
            .Select(level => $"({level.ToString(CultureInfo.InvariantCulture)}, x.{SqlOSFgaLineage.AncestorColumn(level)})"));
        return $"""
            CREATE OR ALTER FUNCTION [{schema}].fn_IsResourceAccessible(
                @ResourceId NVARCHAR(450),
                @SubjectIds NVARCHAR(MAX),
                @PermissionId NVARCHAR(450)
            )
            RETURNS TABLE
            AS
            RETURN
            (
                SELECT TOP 1 g.ResourceId AS Id, g.Id AS GrantId, g.SubjectId, g.RoleId, lv.[Level] AS [Level]
                FROM [{schema}].[{resources}] x
                INNER JOIN [{schema}].[{permissions}] permission ON permission.Id = @PermissionId
                CROSS APPLY (VALUES {levels}) AS lv([Level], [Seq])
                CROSS JOIN [{schema}].fn_ActiveSubjects(@SubjectIds) s
                CROSS APPLY (
                    SELECT TOP (9223372036854775807) g.Id, g.ResourceId, g.SubjectId, g.RoleId, g.EffectiveFrom, g.EffectiveTo
                    FROM [{schema}].[{grants}] g
                    WHERE g.ResourceSeq = lv.Seq AND g.SubjectId = s.SubjectId
                ) g
                INNER JOIN [{schema}].[{rolePermissions}] rp ON rp.RoleId = g.RoleId AND rp.PermissionId = @PermissionId
                WHERE x.Id = @ResourceId
                  AND x.Reach IS NOT NULL
                  AND lv.Seq IS NOT NULL
                  AND lv.[Level] >= x.Reach
                  AND (permission.ResourceTypeId IS NULL OR permission.ResourceTypeId = x.ResourceTypeId)
                  AND (g.EffectiveFrom IS NULL OR g.EffectiveFrom <= GETUTCDATE())
                  AND (g.EffectiveTo IS NULL OR g.EffectiveTo >= GETUTCDATE())
                ORDER BY lv.[Level] DESC
            )
            """;
    }

    /// <summary>The point check as a query; <c>{0}</c> is the resource id, <c>{1}</c> the subject ids JSON, <c>{2}</c> the permission id.</summary>
    public string BuildAccessMatchQuerySql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"SELECT m.Id, m.GrantId, m.SubjectId, m.RoleId, m.[Level] FROM [{Escape(options.Schema)}].fn_IsResourceAccessible({{0}}, {{1}}, {{2}}) AS m";
    }

    /// <summary>The query's first argument, as EF Core's FromSqlRaw numbers them.</summary>
    private const string ResourceIdArgument = "{0}";

    /// <summary>
    /// The path from the top of a resource's tree down to it, from its lineage (<c>{0}</c> is the resource id):
    /// each ancestor's level, and whether access flows down from it to the resource. No rows when the
    /// resource has no lineage (it lies in a cycle, or deeper than the configured depth).
    /// </summary>
    public string BuildResourcePathQuerySql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var resources = $"[{Escape(options.Schema)}].[{Escape(options.TableNames.Resources)}]";
        var levels = string.Join(", ", Enumerable.Range(0, SqlOSFgaLineage.Levels(options))
            .Select(level => $"({level.ToString(CultureInfo.InvariantCulture)}, x.{SqlOSFgaLineage.AncestorColumn(level)})"));
        return $"""
            SELECT lv.[Level] AS [Level], a.Id AS ResourceId, a.Name AS Name, a.ResourceTypeId AS ResourceTypeId, a.IsActive AS IsActive,
                   CAST(CASE WHEN x.Reach IS NOT NULL AND lv.[Level] >= x.Reach THEN 1 ELSE 0 END AS BIT) AS InReach
            FROM {resources} x
            CROSS APPLY (VALUES {levels}) AS lv([Level], [Seq])
            INNER JOIN {resources} a ON a.Seq = lv.Seq
            WHERE x.Id = {ResourceIdArgument}
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
        await database.ExecuteSqlRawAsync(
            BuildAcquireSessionLockSql(timeout, failureMessage),
            [CreateParameter("@resource", resource)],
            cancellationToken);
    }

    public Task ReleaseSessionLockAsync(DatabaseFacade database, string resource, CancellationToken cancellationToken)
        => database.ExecuteSqlRawAsync(
            ReleaseSessionLockSql,
            [CreateParameter("@resource", resource)],
            cancellationToken);

    /// <summary>
    /// Takes the session lock. The DDL run under it takes schema-modification locks that can deadlock with
    /// the schema-stability locks of queries running beside it; the session volunteers as the deadlock
    /// victim, so a query never is, and the caller retries.
    /// </summary>
    internal static string BuildAcquireSessionLockSql(TimeSpan timeout, string failureMessage)
    {
        var timeoutMs = Math.Max(0, (int)timeout.TotalMilliseconds);
        var escaped = failureMessage.Replace("'", "''", StringComparison.Ordinal);
        return $"""
            SET DEADLOCK_PRIORITY LOW;
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Session',
                @LockTimeout = {timeoutMs};
            IF @result < 0 THROW 51000, '{escaped}', 1;
            """;
    }

    internal const string ReleaseSessionLockSql =
        "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = 'Session'; SET DEADLOCK_PRIORITY NORMAL;";

    private static string Escape(string identifier)
        => identifier.Replace("]", "]]", StringComparison.Ordinal);
}

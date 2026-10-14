using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
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

    public string BuildIsResourceAccessibleFunctionSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        var tables = options.TableNames;
        var resources = Escape(tables.Resources);
        var grants = Escape(tables.Grants);
        var rolePermissions = Escape(tables.RolePermissions);
        var subjects = Escape(tables.Subjects);
        var users = Escape(tables.Users);
        var serviceAccounts = Escape(tables.ServiceAccounts);
        var userGroups = Escape(tables.UserGroups);
        var agents = Escape(tables.Agents);
        var permissions = Escape(tables.Permissions);
        var maxDepth = MaxDepth(options);
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
                {BuildAncestorsCte(options)}
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
                  {BuildSubjectActivityConditions(options)}
                  AND (g.EffectiveFrom IS NULL OR g.EffectiveFrom <= GETUTCDATE())
                  AND (g.EffectiveTo IS NULL OR g.EffectiveTo >= GETUTCDATE())
            )
            """;
    }

    public string BuildResourceAncestorsFunctionSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        var maxDepth = MaxDepth(options);
        return $"""
            CREATE OR ALTER FUNCTION [{schema}].fn_ResourceAncestors(
                @ResourceId NVARCHAR(450)
            )
            RETURNS TABLE
            AS
            RETURN
            (
                {BuildAncestorsCte(options)}
                SELECT a.Id AS NodeId, a.Depth
                FROM ancestors a
                WHERE NOT EXISTS (SELECT 1 FROM ancestors malformed WHERE malformed.CycleDetected = 1)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM ancestors truncated
                      WHERE truncated.Depth = {maxDepth}
                        AND truncated.ParentId IS NOT NULL
                  )
            )
            """;
    }

    public string BuildPageNodesFunctionSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        var tables = options.TableNames;
        var resources = Escape(tables.Resources);
        var grants = Escape(tables.Grants);
        var rolePermissions = Escape(tables.RolePermissions);
        var subjects = Escape(tables.Subjects);
        var users = Escape(tables.Users);
        var serviceAccounts = Escape(tables.ServiceAccounts);
        var userGroups = Escape(tables.UserGroups);
        var agents = Escape(tables.Agents);

        // The grant conditions sit at the same indentation as in fn_IsResourceAccessible so the
        // shared subject-activity text is byte-identical in both functions.
        return $"""
            CREATE OR ALTER FUNCTION [{schema}].fn_PageNodes(
                @SubjectIds NVARCHAR(MAX),
                @PermissionId NVARCHAR(128)
            )
            RETURNS TABLE
            AS
            RETURN
            (
                WITH granted AS (
                SELECT DISTINCT g.ResourceId AS NodeId
                FROM [{schema}].[{grants}] g
                INNER JOIN [{schema}].[{rolePermissions}] rp ON g.RoleId = rp.RoleId
                INNER JOIN [{schema}].[{subjects}] s ON g.SubjectId = s.Id
                INNER JOIN [{schema}].[{resources}] node ON node.Id = g.ResourceId
                LEFT JOIN [{schema}].[{users}] u ON s.Id = u.SubjectId
                LEFT JOIN [{schema}].[{serviceAccounts}] sa ON s.Id = sa.SubjectId
                LEFT JOIN [{schema}].[{userGroups}] ug ON s.Id = ug.SubjectId
                LEFT JOIN [{schema}].[{agents}] ag ON s.Id = ag.SubjectId
                WHERE g.SubjectId IN (SELECT CONVERT(NVARCHAR(450), [value]) FROM OPENJSON(@SubjectIds))
                  AND rp.PermissionId = @PermissionId
                  AND node.IsActive = 1
                  {BuildSubjectActivityConditions(options)}
                  AND (g.EffectiveFrom IS NULL OR g.EffectiveFrom <= GETUTCDATE())
                  AND (g.EffectiveTo IS NULL OR g.EffectiveTo >= GETUTCDATE())
                )
                SELECT granted.NodeId
                FROM granted
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM [{schema}].fn_ResourceAncestors(granted.NodeId) ancestor
                    INNER JOIN granted covering ON covering.NodeId = ancestor.NodeId
                    WHERE ancestor.Depth >= 1
                )
            )
            """;
    }

    public string BuildDeletePageEntriesByKeysSql(SqlOSFgaPageOrderStore order, string keys)
        => $"""
            DELETE FROM {Entries(order)}
            WHERE {QuoteIdentifier(order.EntryRowKeyColumn)} IN ({KeyList(order, keys)});
            """;

    public string BuildInsertPageEntriesByKeysSql(SqlOSFgaPageOrderStore order, string keys)
        => $"""
            {InsertEntries(order)}
            WHERE t.{QuoteIdentifier(order.AppKeyColumn)} IN ({KeyList(order, keys)});
            """;

    public string BuildDeletePageEntriesBySubtreeSql(SqlOSFgaPageOrderStore order, SqlOSFgaChangedResources resources)
        => $"""
            {SubtreeCte(order, resources)}DELETE FROM {Entries(order)}
            WHERE {QuoteIdentifier(order.EntryRowKeyColumn)} IN (
                SELECT t.{QuoteIdentifier(order.AppKeyColumn)}
                FROM {AppTable(order)} AS t
                WHERE t.{QuoteIdentifier(order.AppResourceIdColumn)} IN ({ChangedResourceIds(resources)})
            );
            """;

    public string BuildInsertPageEntriesBySubtreeSql(SqlOSFgaPageOrderStore order, SqlOSFgaChangedResources resources, string? exceptKeys)
    {
        var except = exceptKeys == null
            ? string.Empty
            : $" AND t.{QuoteIdentifier(order.AppKeyColumn)} NOT IN ({KeyList(order, exceptKeys)})";
        return $"""
            {SubtreeCte(order, resources)}{InsertEntries(order)}
            WHERE t.{QuoteIdentifier(order.AppResourceIdColumn)} IN ({ChangedResourceIds(resources)}){except};
            """;
    }

    public string BuildDeleteAllPageEntriesSql(SqlOSFgaPageOrderStore order)
        => $"DELETE FROM {Entries(order)};";

    public string BuildInsertAllPageEntriesSql(SqlOSFgaPageOrderStore order)
        => $"{InsertEntries(order)};";

    public string WrapPageMaintenanceSql(string statements)
        => statements;

    public string BuildTableExistsSql(string? schema, string table)
    {
        var name = (schema == null ? QuoteIdentifier(table) : Qualify(schema, table)).Replace("'", "''", StringComparison.Ordinal);
        return $"SELECT CASE WHEN OBJECT_ID(N'{name}', N'U') IS NULL THEN 0 ELSE 1 END AS [Value]";
    }

    public string BuildTableHasRowsSql(string? schema, string table)
        => $"SELECT CASE WHEN EXISTS (SELECT 1 FROM {Table(schema, table)}) THEN 1 ELSE 0 END AS [Value]";

    public DbParameter CreatePageKeysParameter(string name, IReadOnlyList<object> keys)
        => new SqlParameter(name, SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(keys) };

    public DbParameter CreatePageResourceIdsParameter(string name, IReadOnlyCollection<string> resourceIds)
        => new SqlParameter(name, SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(resourceIds) };

    /// <summary>
    /// The ancestors CTE of <c>fn_IsResourceAccessible</c>, shared verbatim with
    /// <c>fn_ResourceAncestors</c>. Lines after the first carry their absolute indentation in
    /// the function text.
    /// </summary>
    internal static string BuildAncestorsCte(SqlOSFgaOptions options)
    {
        var schema = Escape(options.Schema);
        var resources = Escape(options.TableNames.Resources);
        var maxDepth = MaxDepth(options);
        return $"""
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
            """;
    }

    /// <summary>
    /// The subject-activity conditions of <c>fn_IsResourceAccessible</c> for the grant's subject
    /// and for the caller (<c>$[0]</c>), shared verbatim with <c>fn_PageNodes</c>. Lines after the
    /// first carry their absolute indentation in the function text.
    /// </summary>
    internal static string BuildSubjectActivityConditions(SqlOSFgaOptions options)
    {
        var schema = Escape(options.Schema);
        var tables = options.TableNames;
        var subjects = Escape(tables.Subjects);
        var users = Escape(tables.Users);
        var serviceAccounts = Escape(tables.ServiceAccounts);
        var userGroups = Escape(tables.UserGroups);
        var agents = Escape(tables.Agents);
        return $"""
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
            """;
    }

    private string Table(string? schema, string table)
        => schema == null ? QuoteIdentifier(table) : Qualify(schema, table);

    private string Entries(SqlOSFgaPageOrderStore order)
        => Table(order.EntrySchema, order.EntryTable);

    private string AppTable(SqlOSFgaPageOrderStore order)
        => Table(order.AppSchema, order.AppTable);

    private static string KeyList(SqlOSFgaPageOrderStore order, string keys)
        => $"SELECT CAST(k.[value] AS {order.KeyStoreType}) FROM OPENJSON({keys}) AS k";

    private string InsertEntries(SqlOSFgaPageOrderStore order)
    {
        var entryColumns = new[] { order.EntryNodeIdColumn }
            .Concat(order.EntrySortColumns)
            .Append(order.EntryRowKeyColumn)
            .Select(QuoteIdentifier);
        var appColumns = new[] { "a.NodeId" }
            .Concat(order.AppSortColumns.Select(column => $"t.{QuoteIdentifier(column)}"))
            .Append($"t.{QuoteIdentifier(order.AppKeyColumn)}");
        return $"""
            INSERT INTO {Entries(order)} ({string.Join(", ", entryColumns)})
            SELECT {string.Join(", ", appColumns)}
            FROM {AppTable(order)} AS t
            CROSS APPLY {Qualify(order.FgaSchema, "fn_ResourceAncestors")}(t.{QuoteIdentifier(order.AppResourceIdColumn)}) AS a
            """;
    }

    /// <summary>
    /// The moved resources and their descendants, bounded by the hierarchy depth, followed by a line
    /// break; empty when no resource moved. Only moved resources are walked: estimating a walk from
    /// many anchors (a save that adds a thousand resources) makes the engines read the whole entry table.
    /// </summary>
    private string SubtreeCte(SqlOSFgaPageOrderStore order, SqlOSFgaChangedResources resources)
        => resources.Moved == null
            ? string.Empty
            : $"""
              WITH subtree AS (
                  SELECT CAST(r.[value] AS NVARCHAR(450)) AS Id, 0 AS Depth
                  FROM OPENJSON({resources.Moved}) AS r
                  UNION ALL
                  SELECT child.Id, subtree.Depth + 1
                  FROM {Qualify(order.FgaSchema, order.ResourcesTable)} AS child
                  INNER JOIN subtree ON child.ParentId = subtree.Id
                  WHERE subtree.Depth < {order.MaxDepth.ToString(CultureInfo.InvariantCulture)}
              )

              """;

    /// <summary>
    /// The ids of the resources whose rows a save rewrites: the moved subtree, and the added or deleted
    /// resources themselves. The ids anchor it, so a deleted resource still names the rows that point at it.
    /// </summary>
    private static string ChangedResourceIds(SqlOSFgaChangedResources resources)
        => string.Join(
            " UNION ALL ",
            new[]
            {
                resources.Moved == null ? null : "SELECT subtree.Id FROM subtree",
                resources.AddedOrDeleted == null ? null : $"SELECT CAST(changed.[value] AS NVARCHAR(450)) FROM OPENJSON({resources.AddedOrDeleted}) AS changed",
            }.OfType<string>());

    private static string MaxDepth(SqlOSFgaOptions options)
        => Math.Max(1, options.MaxResourceHierarchyDepth).ToString(CultureInfo.InvariantCulture);

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

    private static string Escape(string identifier)
        => identifier.Replace("]", "]]", StringComparison.Ordinal);
}

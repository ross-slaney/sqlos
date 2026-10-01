using System.Globalization;
using System.Text;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;

namespace SqlOS.Database;

/// <summary>
/// The SHRBAC enforcement artifacts around the row filter: the caller's live subjects and access roots, the
/// resource lineage (each resource's ancestor at every level, depth, and reach) with the routines and
/// triggers that keep it exact, and the scope columns that copy it onto application tables.
/// </summary>
internal sealed partial class SqlServerDatabaseProvider
{
    private const int MalformedError = 51012;

    /// <summary>
    /// <c>fn_ActiveSubjects(@SubjectIds)</c>: the caller's principal set as the grant lookups may use it: the
    /// listed subjects that are alive (active user or group, unexpired service account, existing agent), and
    /// none at all unless the caller (the first id) is alive. It depends on who is asking, never on what they
    /// were granted, so it costs a handful of rows however many grants they hold.
    /// </summary>
    public string BuildActiveSubjectsFunctionSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        var tables = options.TableNames;
        var subjects = Escape(tables.Subjects);
        var users = Escape(tables.Users);
        var serviceAccounts = Escape(tables.ServiceAccounts);
        var userGroups = Escape(tables.UserGroups);
        var agents = Escape(tables.Agents);
        return $"""
            CREATE OR ALTER FUNCTION [{schema}].fn_ActiveSubjects(
                @SubjectIds NVARCHAR(MAX)
            )
            RETURNS TABLE
            AS
            RETURN
            (
                SELECT s.Id AS SubjectId
                FROM [{schema}].[{subjects}] s
                LEFT JOIN [{schema}].[{users}] u ON s.Id = u.SubjectId
                LEFT JOIN [{schema}].[{serviceAccounts}] sa ON s.Id = sa.SubjectId
                LEFT JOIN [{schema}].[{userGroups}] ug ON s.Id = ug.SubjectId
                LEFT JOIN [{schema}].[{agents}] ag ON s.Id = ag.SubjectId
                WHERE s.Id IN (SELECT CONVERT(NVARCHAR(450), [value]) FROM OPENJSON(@SubjectIds))
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
            )
            """;
    }

    /// <summary>
    /// <c>fn_AccessRoots(@SubjectIds, @PermissionId)</c>: the caller's access roots, one row per resource on
    /// which one of the caller's live subjects holds a current grant whose role includes the permission: the
    /// resource's compact key and its level. <c>BuildFilterAsync</c> reads them once per query and compares
    /// each row's ancestor at the root's level with them. Inactive or malformed resources are left out: no row
    /// can reach them anyway.
    /// </summary>
    public string BuildAccessRootsFunctionSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        var tables = options.TableNames;
        var resources = Escape(tables.Resources);
        var grants = Escape(tables.Grants);
        var rolePermissions = Escape(tables.RolePermissions);
        return $"""
            CREATE OR ALTER FUNCTION [{schema}].fn_AccessRoots(
                @SubjectIds NVARCHAR(MAX),
                @PermissionId NVARCHAR(128)
            )
            RETURNS TABLE
            AS
            RETURN
            (
                SELECT DISTINCT r.Seq AS ResourceSeq, r.Depth
                FROM [{schema}].[{grants}] g
                INNER JOIN [{schema}].[{rolePermissions}] rp ON rp.RoleId = g.RoleId AND rp.PermissionId = @PermissionId
                INNER JOIN [{schema}].[{resources}] r ON r.Id = g.ResourceId
                WHERE g.SubjectId IN (SELECT live.SubjectId FROM [{schema}].fn_ActiveSubjects(@SubjectIds) live)
                  AND r.IsActive = 1
                  AND r.Depth IS NOT NULL
                  AND (g.EffectiveFrom IS NULL OR g.EffectiveFrom <= GETUTCDATE())
                  AND (g.EffectiveTo IS NULL OR g.EffectiveTo >= GETUTCDATE())
            )
            """;
    }

    /// <summary>The composable query over <c>fn_ActiveSubjects</c>; <c>{0}</c> is the subject ids JSON.</summary>
    public string BuildActiveSubjectsQuerySql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"SELECT live.SubjectId FROM [{Escape(options.Schema)}].fn_ActiveSubjects({{0}}) AS live";
    }

    /// <summary>The composable query over <c>fn_AccessRoots</c>; <c>{0}</c> is the subject ids JSON, <c>{1}</c> the permission id.</summary>
    public string BuildAccessRootsQuerySql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"SELECT a.ResourceSeq, a.Depth FROM [{Escape(options.Schema)}].fn_AccessRoots({{0}}, {{1}}) AS a";
    }

    /// <summary>
    /// Adds the ancestor columns the configured depth calls for, and their indexes: one filtered index per
    /// level, which is the range a caller's scope is read from when the optimizer starts from the caller's
    /// grants. Idempotent; a deeper configuration adds columns, never removes any.
    /// </summary>
    public IReadOnlyList<string> BuildEnsureLineageColumnsSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        var resources = Escape(options.TableNames.Resources);
        var table = $"[{schema}].[{resources}]";
        var columns = new StringBuilder();
        var indexes = new StringBuilder();
        for (var level = 0; level < SqlOSFgaLineage.Levels(options); level++)
        {
            var column = SqlOSFgaLineage.AncestorColumn(level);
            var index = Escape(SqlOSFgaLineage.AncestorIndexName(options.TableNames.Resources, level));
            columns.AppendLine(CultureInfo.InvariantCulture, $"""
                IF COL_LENGTH('{SqlLiteral(table)}', '{column}') IS NULL
                    ALTER TABLE {table} ADD [{column}] BIGINT NULL;
                """);
            indexes.AppendLine(CultureInfo.InvariantCulture, $"""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = '{SqlLiteral(SqlOSFgaLineage.AncestorIndexName(options.TableNames.Resources, level))}' AND object_id = OBJECT_ID('{SqlLiteral(table)}'))
                    CREATE NONCLUSTERED INDEX [{index}] ON {table}([{column}]) INCLUDE ([{SqlOSFgaLineage.ReachColumn}]) WHERE [{column}] IS NOT NULL;
                """);
        }

        return [columns.ToString(), indexes.ToString()];
    }

    /// <summary>
    /// The lineage maintenance: a refresh procedure (recompute the lineage of a set of changed resources and
    /// everything beneath them, rejecting a cycle or an over-deep row), a rebuild procedure (recompute every
    /// row level by level), the three triggers on the resources table, and the two triggers on each
    /// application table that carries the scope columns. Each batch is idempotent (<c>CREATE OR ALTER</c>).
    /// </summary>
    public IReadOnlyList<string> BuildLineageMaintenanceSql(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> scopeTables)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopeTables);
        var schema = Escape(options.Schema);
        var resources = $"[{schema}].[{Escape(options.TableNames.Resources)}]";
        var resourceTypes = $"[{schema}].[{Escape(options.TableNames.ResourceTypes)}]";
        var refresh = $"[{schema}].[sp_{Escape(SqlOSFgaLineage.RefreshRoutineName(options.TableNames.Resources))}]";
        var rebuild = $"[{schema}].[sp_{Escape(SqlOSFgaLineage.RebuildRoutineName(options.TableNames.Resources))}]";
        var triggers = SqlOSFgaLineage.TriggerNames(options.TableNames.Resources);
        var levels = SqlOSFgaLineage.Levels(options);
        var maxLevel = (levels - 1).ToString(CultureInfo.InvariantCulture);
        var malformed = $"THROW {MalformedError}, 'SqlOS FGA: the change would create a cycle, or place a resource deeper than the configured maximum hierarchy depth of {maxLevel}.', 1;";

        // The lineage of row r computed from its parent p, where nd.Depth is r's new level (NULL when malformed).
        var newDepth = $"CASE WHEN r.ParentId IS NULL THEN 0 WHEN p.Depth IS NULL OR p.Depth >= {maxLevel} THEN NULL ELSE p.Depth + 1 END";
        string Lineage(string depth)
        {
            var set = new StringBuilder();
            set.Append(CultureInfo.InvariantCulture, $"Depth = {depth},\n");
            set.Append(CultureInfo.InvariantCulture, $"    Reach = CASE WHEN r.IsActive = 0 OR {depth} IS NULL THEN NULL WHEN r.ParentId IS NULL THEN 0 WHEN p.Reach IS NULL THEN {depth} ELSE p.Reach END");
            for (var level = 0; level < levels; level++)
            {
                var column = SqlOSFgaLineage.AncestorColumn(level);
                set.Append(CultureInfo.InvariantCulture, $",\n    {column} = CASE WHEN {depth} = {level} THEN r.Seq WHEN {depth} > {level} THEN p.{column} ELSE NULL END");
            }

            return set.ToString();
        }

        var nulls = string.Join(", ", new[] { "Depth = NULL", "Reach = NULL" }.Concat(Enumerable.Range(0, levels).Select(l => $"{SqlOSFgaLineage.AncestorColumn(l)} = NULL")));

        // Copies the lineage of the resources in `source` (a table with an Id column) onto their rows in each
        // scope table.
        string Propagate(string source)
        {
            var sql = new StringBuilder();
            foreach (var table in scopeTables)
            {
                sql.AppendLine(CultureInfo.InvariantCulture, $"""
                    UPDATE t SET {ScopeAssignments(levels, "r", "rt")}
                    FROM {ScopeTable(table)} t
                    INNER JOIN {source} s ON s.Id = t.[{Escape(table.ResourceIdColumn)}]
                    INNER JOIN {resources} r ON r.Id = s.Id
                    INNER JOIN {resourceTypes} rt ON rt.Id = r.ResourceTypeId;
                    """);
            }

            return sql.ToString();
        }

        var refreshProcedure = $"""
            CREATE OR ALTER PROCEDURE {refresh}
                @RejectMalformed BIT = 1,
                @WalkAll BIT = 1
            AS
            BEGIN
                SET NOCOUNT ON;
                -- #SqlOSLineageChanged (Id): the resources whose parent or activity changed, from the caller.

                -- 1. A changed row whose chain does not reach a root within the depth limit lies in a cycle or
                --    too deep: reject the statement. A moved row is always walked (its new parent may be its
                --    own descendant); an inserted row has no descendants, so its chain is walked only when
                --    the parent is itself new, has no lineage, or sits at the deepest level.
                IF @RejectMalformed = 1
                BEGIN
                    ;WITH walk AS (
                        SELECT r.ParentId AS CurrentId, 1 AS Steps
                        FROM #SqlOSLineageChanged c
                        INNER JOIN {resources} r ON r.Id = c.Id
                        LEFT JOIN {resources} p ON p.Id = r.ParentId
                        WHERE r.ParentId IS NOT NULL
                          AND (@WalkAll = 1 OR p.Depth IS NULL OR p.Depth >= {maxLevel} OR EXISTS (SELECT 1 FROM #SqlOSLineageChanged pc WHERE pc.Id = r.ParentId))
                        UNION ALL
                        SELECT r.ParentId, w.Steps + 1
                        FROM walk w
                        INNER JOIN {resources} r ON r.Id = w.CurrentId
                        WHERE r.ParentId IS NOT NULL AND w.Steps <= {maxLevel}
                    )
                    SELECT TOP 1 Steps INTO #SqlOSLineageWalk FROM walk WHERE Steps > {maxLevel}
                    OPTION (MAXRECURSION {(levels + 1).ToString(CultureInfo.InvariantCulture)});
                    IF EXISTS (SELECT 1 FROM #SqlOSLineageWalk)
                    BEGIN
                        DROP TABLE #SqlOSLineageWalk;
                        {malformed}
                    END
                    DROP TABLE #SqlOSLineageWalk;
                END

                -- 2. The affected rows: the changed rows and everything beneath them, collected level by level.
                CREATE TABLE #SqlOSLineageAffected (
                    Id NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
                    Round INT NOT NULL,
                    Wave INT NULL
                );
                INSERT INTO #SqlOSLineageAffected (Id, Round) SELECT Id, 0 FROM #SqlOSLineageChanged;
                DECLARE @round INT = 0;
                WHILE @round <= {maxLevel}
                BEGIN
                    INSERT INTO #SqlOSLineageAffected (Id, Round)
                    SELECT r.Id, @round + 1
                    FROM {resources} r
                    INNER JOIN #SqlOSLineageAffected a ON a.Id = r.ParentId
                    WHERE a.Round = @round
                      AND NOT EXISTS (SELECT 1 FROM #SqlOSLineageAffected x WHERE x.Id = r.Id);
                    IF @@ROWCOUNT = 0 BREAK;
                    SET @round += 1;
                END

                -- 3. Descend: first the affected rows whose parent is outside the set (its lineage is final),
                --    then their children, wave by wave; each wave is one set-based update from the parents.
                UPDATE a SET Wave = 0
                FROM #SqlOSLineageAffected a
                INNER JOIN {resources} r ON r.Id = a.Id
                WHERE r.ParentId IS NULL OR NOT EXISTS (SELECT 1 FROM #SqlOSLineageAffected p WHERE p.Id = r.ParentId);

                DECLARE @wave INT = 0;
                WHILE @wave <= {levels.ToString(CultureInfo.InvariantCulture)}
                BEGIN
                    IF @RejectMalformed = 1 AND EXISTS (
                        SELECT 1
                        FROM #SqlOSLineageAffected a
                        INNER JOIN {resources} r ON r.Id = a.Id
                        INNER JOIN {resources} p ON p.Id = r.ParentId
                        WHERE a.Wave = @wave AND p.Depth = {maxLevel})
                    BEGIN
                        DROP TABLE #SqlOSLineageAffected;
                        {malformed}
                    END

                    UPDATE r SET
                        {Lineage("nd.Depth")}
                    FROM {resources} r
                    INNER JOIN #SqlOSLineageAffected a ON a.Id = r.Id
                    LEFT JOIN {resources} p ON p.Id = r.ParentId
                    CROSS APPLY (SELECT {newDepth} AS Depth) nd
                    WHERE a.Wave = @wave;

                    UPDATE c SET Wave = @wave + 1
                    FROM #SqlOSLineageAffected c
                    INNER JOIN {resources} r ON r.Id = c.Id
                    INNER JOIN #SqlOSLineageAffected p ON p.Id = r.ParentId
                    WHERE c.Wave IS NULL AND p.Wave = @wave;
                    IF @@ROWCOUNT = 0 BREAK;
                    SET @wave += 1;
                END

                -- 4. A row no wave reached has no path to a root within the set: it is malformed.
                UPDATE r SET {nulls}
                FROM {resources} r
                INNER JOIN #SqlOSLineageAffected a ON a.Id = r.Id
                WHERE a.Wave IS NULL;

                -- 5. The scope columns of the affected rows' application rows.
                {Propagate("#SqlOSLineageAffected")}
                DROP TABLE #SqlOSLineageAffected;
            END
            """;

        // Level by level over the whole table, in one transaction: a failed rebuild leaves the previous lineage.
        var rebuildProcedure = $"""
            CREATE OR ALTER PROCEDURE {rebuild}
            AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                UPDATE {resources} SET {nulls} WHERE Depth IS NOT NULL OR Reach IS NOT NULL OR {SqlOSFgaLineage.AncestorColumn(0)} IS NOT NULL;
                UPDATE r SET
                    {Lineage("nd.Depth")}
                FROM {resources} r
                LEFT JOIN {resources} p ON 1 = 0
                CROSS APPLY (SELECT 0 AS Depth) nd
                WHERE r.ParentId IS NULL;
                DECLARE @level INT = 1;
                WHILE @level <= {maxLevel}
                BEGIN
                    UPDATE r SET
                        {Lineage("nd.Depth")}
                    FROM {resources} r
                    INNER JOIN {resources} p ON p.Id = r.ParentId
                    CROSS APPLY (SELECT @level AS Depth) nd
                    WHERE p.Depth = @level - 1;
                    IF @@ROWCOUNT = 0 BREAK;
                    SET @level += 1;
                END
                {Propagate(resources)}
                COMMIT TRANSACTION;
            END
            """;

        var changedTable = """
            CREATE TABLE #SqlOSLineageChanged (Id NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY);
            """;

        var insertTrigger = $"""
            CREATE OR ALTER TRIGGER [{schema}].[{Escape(triggers[0])}] ON {resources}
            AFTER INSERT
            AS
            BEGIN
                SET NOCOUNT ON;
                IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
                {changedTable}
                INSERT INTO #SqlOSLineageChanged (Id) SELECT Id FROM inserted;
                EXEC {refresh} @RejectMalformed = 1, @WalkAll = 0;
                DROP TABLE #SqlOSLineageChanged;
            END
            """;

        // Changed rows are found with EXCEPT, a hashed set operation: a join of inserted and deleted has no
        // index or statistics to plan by, and a bulk update would pay for it quadratically.
        var typeChanges = new StringBuilder();
        foreach (var table in scopeTables)
        {
            typeChanges.AppendLine(CultureInfo.InvariantCulture, $"""
                UPDATE t SET [{SqlOSFgaLineage.ScopeTypeSeqColumn}] = rt.Seq
                FROM {ScopeTable(table)} t
                INNER JOIN (SELECT Id, ResourceTypeId FROM inserted EXCEPT SELECT Id, ResourceTypeId FROM deleted) i ON i.Id = t.[{Escape(table.ResourceIdColumn)}]
                INNER JOIN {resourceTypes} rt ON rt.Id = i.ResourceTypeId;
                """);
        }

        var updateTrigger = $"""
            CREATE OR ALTER TRIGGER [{schema}].[{Escape(triggers[1])}] ON {resources}
            AFTER UPDATE
            AS
            BEGIN
                SET NOCOUNT ON;
                IF NOT (UPDATE(ParentId) OR UPDATE(IsActive) OR UPDATE(ResourceTypeId)) RETURN;
                {changedTable}
                INSERT INTO #SqlOSLineageChanged (Id)
                SELECT Id FROM (SELECT Id, ParentId, IsActive FROM inserted EXCEPT SELECT Id, ParentId, IsActive FROM deleted) changed;
                IF EXISTS (SELECT 1 FROM #SqlOSLineageChanged)
                BEGIN
                    EXEC {refresh} @RejectMalformed = 1, @WalkAll = 1;
                END
                DROP TABLE #SqlOSLineageChanged;
                IF UPDATE(ResourceTypeId)
                BEGIN
                    {typeChanges}
                END
            END
            """;

        var clears = new StringBuilder();
        foreach (var table in scopeTables)
        {
            clears.AppendLine(CultureInfo.InvariantCulture, $"""
                UPDATE t SET {ScopeNullAssignments(levels)}
                FROM {ScopeTable(table)} t
                INNER JOIN deleted d ON d.Id = t.[{Escape(table.ResourceIdColumn)}];
                """);
        }

        var deleteTrigger = $"""
            CREATE OR ALTER TRIGGER [{schema}].[{Escape(triggers[2])}] ON {resources}
            AFTER DELETE
            AS
            BEGIN
                SET NOCOUNT ON;
                {clears}
            END
            """;

        var batches = new List<string> { refreshProcedure, rebuildProcedure, insertTrigger, updateTrigger, deleteTrigger };
        foreach (var table in scopeTables)
        {
            batches.AddRange(ScopeTriggers(options, table, levels));
        }

        return batches;
    }

    /// <summary>
    /// The two triggers on an application table: an inserted row, or one whose resource id changed, takes
    /// the lineage of its resource (NULL when there is none, which denies it).
    /// </summary>
    private IReadOnlyList<string> ScopeTriggers(SqlOSFgaOptions options, SqlOSFgaScopeTable table, int levels)
    {
        var schema = Escape(options.Schema);
        var resources = $"[{schema}].[{Escape(options.TableNames.Resources)}]";
        var resourceTypes = $"[{schema}].[{Escape(options.TableNames.ResourceTypes)}]";
        var triggers = SqlOSFgaLineage.ScopeTriggerNames(table.Table);
        var triggerSchema = table.Schema is null ? "" : $"[{Escape(table.Schema)}].";
        var keys = string.Join(" AND ", table.KeyColumns.Select(k => $"i.[{Escape(k)}] = t.[{Escape(k)}]"));
        var copy = $"""
            UPDATE t SET {ScopeAssignments(levels, "r", "rt")}
            FROM {ScopeTable(table)} t
            INNER JOIN inserted i ON {keys}
            LEFT JOIN {resources} r ON r.Id = t.[{Escape(table.ResourceIdColumn)}]
            LEFT JOIN {resourceTypes} rt ON rt.Id = r.ResourceTypeId;
            """;
        return
        [
            $"""
            CREATE OR ALTER TRIGGER {triggerSchema}[{Escape(triggers[0])}] ON {ScopeTable(table)}
            AFTER INSERT
            AS
            BEGIN
                SET NOCOUNT ON;
                {copy}
            END
            """,
            $"""
            CREATE OR ALTER TRIGGER {triggerSchema}[{Escape(triggers[1])}] ON {ScopeTable(table)}
            AFTER UPDATE
            AS
            BEGIN
                SET NOCOUNT ON;
                IF NOT UPDATE([{Escape(table.ResourceIdColumn)}]) RETURN;
                {copy}
            END
            """,
        ];
    }

    /// <summary>Returns 1 when the lineage was never built: a root (no parent) without a depth.</summary>
    public string BuildLineageNeedsBuildSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM [{Escape(options.Schema)}].[{Escape(options.TableNames.Resources)}]
                WHERE ParentId IS NULL AND Depth IS NULL)
            THEN 1 ELSE 0 END AS [Value]
            """;
    }

    public string BuildLineageRebuildSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"EXEC [{Escape(options.Schema)}].[sp_{Escape(SqlOSFgaLineage.RebuildRoutineName(options.TableNames.Resources))}];";
    }

    /// <summary>Fills the scope columns of every row of one application table from the lineage, once.</summary>
    public string BuildScopeFillSql(SqlOSFgaOptions options, SqlOSFgaScopeTable table)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(table);
        var schema = Escape(options.Schema);
        return $"""
            UPDATE t SET {ScopeAssignments(SqlOSFgaLineage.Levels(options), "r", "rt")}
            FROM {ScopeTable(table)} t
            INNER JOIN [{schema}].[{Escape(options.TableNames.Resources)}] r ON r.Id = t.[{Escape(table.ResourceIdColumn)}]
            INNER JOIN [{schema}].[{Escape(options.TableNames.ResourceTypes)}] rt ON rt.Id = r.ResourceTypeId
            """;
    }

    public string BuildSelectRoutinesHashSql(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> scopeTables)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopeTables);
        var schema = Escape(options.Schema);
        var resourcesTable = options.TableNames.Resources;
        string Exists(string schemaName, string name, string type)
            => $"OBJECT_ID(N'{SqlLiteral($"[{Escape(schemaName)}].[{Escape(name)}]")}', N'{type}') IS NOT NULL";

        var conditions = new List<string>
        {
            Exists(options.Schema, "fn_ActiveSubjects", "IF"),
            Exists(options.Schema, "fn_AccessRoots", "IF"),
            Exists(options.Schema, "fn_IsResourceAccessible", "IF"),
            Exists(options.Schema, "sp_" + SqlOSFgaLineage.RefreshRoutineName(resourcesTable), "P"),
            Exists(options.Schema, "sp_" + SqlOSFgaLineage.RebuildRoutineName(resourcesTable), "P"),
        };
        conditions.AddRange(SqlOSFgaLineage.TriggerNames(resourcesTable).Select(t => Exists(options.Schema, t, "TR")));
        conditions.Add($"COL_LENGTH('{SqlLiteral($"[{schema}].[{Escape(resourcesTable)}]")}', '{SqlOSFgaLineage.AncestorColumn(SqlOSFgaLineage.MaxLevel(options))}') IS NOT NULL");
        foreach (var table in scopeTables)
        {
            conditions.AddRange(SqlOSFgaLineage.ScopeTriggerNames(table.Table).Select(t => Exists(table.Schema ?? "dbo", t, "TR")));
        }

        return $"""
            SELECT TOP 1 [RoutinesHash]
            FROM [{schema}].[SqlOSFgaSchema]
            WHERE {string.Join("\n  AND ", conditions)}
            """;
    }

    public string BuildStoreRoutinesHashSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"UPDATE [{Escape(options.Schema)}].[SqlOSFgaSchema] SET [RoutinesHash] = @RoutinesHash";
    }

    private static string ScopeTable(SqlOSFgaScopeTable table)
        => table.Schema is null ? $"[{Escape(table.Table)}]" : $"[{Escape(table.Schema)}].[{Escape(table.Table)}]";

    /// <summary>The scope columns set from a resource row <paramref name="r"/> and its type row <paramref name="rt"/>.</summary>
    private static string ScopeAssignments(int levels, string r, string rt)
    {
        var parts = new List<string>();
        for (var level = 0; level < levels; level++)
        {
            parts.Add($"[{SqlOSFgaLineage.ScopeAncestorColumn(level)}] = {r}.{SqlOSFgaLineage.AncestorColumn(level)}");
        }

        parts.Add($"[{SqlOSFgaLineage.ScopeReachColumn}] = {r}.{SqlOSFgaLineage.ReachColumn}");
        parts.Add($"[{SqlOSFgaLineage.ScopeTypeSeqColumn}] = {rt}.{SqlOSFgaLineage.SeqColumn}");
        return string.Join(", ", parts);
    }

    private static string ScopeNullAssignments(int levels)
    {
        var parts = new List<string>();
        for (var level = 0; level < levels; level++)
        {
            parts.Add($"[{SqlOSFgaLineage.ScopeAncestorColumn(level)}] = NULL");
        }

        parts.Add($"[{SqlOSFgaLineage.ScopeReachColumn}] = NULL");
        parts.Add($"[{SqlOSFgaLineage.ScopeTypeSeqColumn}] = NULL");
        return string.Join(", ", parts);
    }

    private static string SqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}

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

    /// <summary>The query over <c>fn_AccessRoots</c>; <c>{0}</c> is the subject ids JSON, <c>{1}</c> the permission id.</summary>
    public string BuildAccessRootsQuerySql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"SELECT a.ResourceSeq, a.Depth FROM [{Escape(options.Schema)}].fn_AccessRoots({{0}}, {{1}}) AS a";
    }

    /// <summary>
    /// Adds the ancestor columns the configured depth calls for. Idempotent; a deeper configuration adds
    /// columns, never removes any. The columns carry no index: they are read by resource id or by Seq.
    /// </summary>
    public IReadOnlyList<string> BuildEnsureLineageColumnsSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var table = $"[{Escape(options.Schema)}].[{Escape(options.TableNames.Resources)}]";
        var columns = new StringBuilder();
        for (var level = 0; level < SqlOSFgaLineage.Levels(options); level++)
        {
            var column = SqlOSFgaLineage.AncestorColumn(level);
            columns.AppendLine(CultureInfo.InvariantCulture, $"""
                IF COL_LENGTH('{SqlLiteral(table)}', '{column}') IS NULL
                    ALTER TABLE {table} ADD [{column}] BIGINT NULL;
                """);
        }

        return [columns.ToString()];
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
                    UPDATE t SET {ScopeAssignment(levels, "r", "rt")}
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

        // The whole table, in ranges of the key: the internal nodes (every resource that is some row's parent;
        // few next to the leaves) get their lineage level by level in a temp table, then every range of the
        // table takes its rows' lineage in one transaction: the nodes' from the temp table, the leaves' from
        // their parent node, and the scope columns of the range's application rows. A failed rebuild leaves
        // some ranges written and the routines' hash unstored, so the next startup runs it again.
        var nodeColumns = string.Join(", ", new[] { "Depth", "Reach" }.Concat(Enumerable.Range(0, levels).Select(SqlOSFgaLineage.AncestorColumn)));
        var copyNode = string.Join(", ", new[] { "Depth", "Reach" }.Concat(Enumerable.Range(0, levels).Select(SqlOSFgaLineage.AncestorColumn)).Select(c => $"{c} = n.{c}"));
        var rebuildProcedure = $"""
            CREATE OR ALTER PROCEDURE {rebuild}
                @RangeRows INT = {SqlOSFgaLineage.RebuildRangeRows.ToString(CultureInfo.InvariantCulture)}
            AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                {WithSessionLineageLock(options, $"""

                -- 1. The internal nodes, level by level.
                CREATE TABLE #SqlOSLineageNodes (
                    Id NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
                    ParentId NVARCHAR(450) COLLATE DATABASE_DEFAULT NULL,
                    IsActive BIT NOT NULL,
                    Seq BIGINT NOT NULL,
                    Depth SMALLINT NULL,
                    Reach SMALLINT NULL,
                    {string.Join(",\n        ", Enumerable.Range(0, levels).Select(l => $"{SqlOSFgaLineage.AncestorColumn(l)} BIGINT NULL"))}
                );
                INSERT INTO #SqlOSLineageNodes (Id, ParentId, IsActive, Seq)
                SELECT r.Id, r.ParentId, r.IsActive, r.Seq
                FROM {resources} r
                WHERE EXISTS (SELECT 1 FROM {resources} c WHERE c.ParentId = r.Id);

                UPDATE r SET
                    {Lineage("nd.Depth")}
                FROM #SqlOSLineageNodes r
                LEFT JOIN #SqlOSLineageNodes p ON 1 = 0
                CROSS APPLY (SELECT 0 AS Depth) nd
                WHERE r.ParentId IS NULL;
                DECLARE @level INT = 1;
                WHILE @level <= {maxLevel}
                BEGIN
                    UPDATE r SET
                        {Lineage("nd.Depth")}
                    FROM #SqlOSLineageNodes r
                    INNER JOIN #SqlOSLineageNodes p ON p.Id = r.ParentId
                    CROSS APPLY (SELECT @level AS Depth) nd
                    WHERE p.Depth = @level - 1;
                    IF @@ROWCOUNT = 0 BREAK;
                    SET @level += 1;
                END

                -- 2. Every range of the table: its nodes, its leaves, its application rows.
                {RangeLoop(resources, "@RangeRows", $"""
                UPDATE r SET {copyNode}
                FROM {resources} r
                INNER JOIN #SqlOSLineageNodes n ON n.Id = r.Id
                WHERE r.Id >= @from AND r.Id <= @to;

                UPDATE r SET
                    {Lineage("nd.Depth")}
                FROM {resources} r
                INNER JOIN #SqlOSLineageNodes p ON p.Id = r.ParentId
                CROSS APPLY (SELECT {newDepth} AS Depth) nd
                WHERE r.Id >= @from AND r.Id <= @to
                  AND NOT EXISTS (SELECT 1 FROM #SqlOSLineageNodes n WHERE n.Id = r.Id);

                UPDATE r SET
                    {Lineage("nd.Depth")}
                FROM {resources} r
                LEFT JOIN {resources} p ON 1 = 0
                CROSS APPLY (SELECT 0 AS Depth) nd
                WHERE r.Id >= @from AND r.Id <= @to
                  AND r.ParentId IS NULL
                  AND NOT EXISTS (SELECT 1 FROM #SqlOSLineageNodes n WHERE n.Id = r.Id);
                {string.Concat(scopeTables.Select(t => ScopeFillRange(options, t, levels)))}
                """)}
                DROP TABLE #SqlOSLineageNodes;
                """)}
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
                {LineageLock(options, "Shared")}
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
                UPDATE t SET {ScopeAssignment(levels, "r", "rt")}
                FROM {ScopeTable(table)} t
                INNER JOIN (SELECT Id, ResourceTypeId FROM inserted EXCEPT SELECT Id, ResourceTypeId FROM deleted) i ON i.Id = t.[{Escape(table.ResourceIdColumn)}]
                INNER JOIN {resources} r ON r.Id = i.Id
                INNER JOIN {resourceTypes} rt ON rt.Id = r.ResourceTypeId;
                """);
        }

        var updateTrigger = $"""
            CREATE OR ALTER TRIGGER [{schema}].[{Escape(triggers[1])}] ON {resources}
            AFTER UPDATE
            AS
            BEGIN
                SET NOCOUNT ON;
                IF NOT (UPDATE(ParentId) OR UPDATE(IsActive) OR UPDATE(ResourceTypeId)) RETURN;
                {LineageLock(options, "Exclusive")}
                {changedTable}
                INSERT INTO #SqlOSLineageChanged (Id)
                SELECT Id FROM (SELECT Id, ParentId, IsActive FROM inserted EXCEPT SELECT Id, ParentId, IsActive FROM deleted) changed;
                IF EXISTS (SELECT 1 FROM #SqlOSLineageChanged)
                BEGIN
                    EXEC {refresh} @RejectMalformed = 1, @WalkAll = 1;
                END
                DROP TABLE #SqlOSLineageChanged;
                {(typeChanges.Length == 0 ? "" : $"IF UPDATE(ResourceTypeId)\nBEGIN\n{typeChanges}END")}
            END
            """;

        var clears = new StringBuilder();
        foreach (var table in scopeTables)
        {
            clears.AppendLine(CultureInfo.InvariantCulture, $"""
                UPDATE t SET [{SqlOSFgaLineage.ScopeColumn}] = NULL
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
                {(clears.Length == 0 ? "" : LineageLock(options, "Exclusive"))}
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
    /// the scope value of its resource (NULL when there is none, which denies it).
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
            UPDATE t SET {ScopeAssignment(levels, "r", "rt")}
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
                IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
                {LineageLock(options, "Shared")}
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
                {LineageLock(options, "Shared")}
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

    /// <summary>
    /// Fills the scope column of every row of one application table from the lineage, once, in ranges of
    /// the resources table's key, each its own transaction.
    /// </summary>
    public string BuildScopeFillSql(SqlOSFgaOptions options, SqlOSFgaScopeTable table)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(table);
        var resources = $"[{Escape(options.Schema)}].[{Escape(options.TableNames.Resources)}]";
        return $"""
            SET NOCOUNT ON;
            SET XACT_ABORT ON;
            {WithSessionLineageLock(options, RangeLoop(resources, SqlOSFgaLineage.RebuildRangeRows.ToString(CultureInfo.InvariantCulture), ScopeFillRange(options, table, SqlOSFgaLineage.Levels(options))))}
            """;
    }

    /// <summary>
    /// Runs <paramref name="body"/> once per range of <paramref name="rangeRows"/> resources (an expression:
    /// a literal, or the rebuild's parameter) in key order, with <c>@from</c> and <c>@to</c> the range's first
    /// and last id (inclusive), each range in its own transaction. The ranges are found with one ordered pass
    /// over the key.
    /// </summary>
    private static string RangeLoop(string resources, string rangeRows, string body)
        => $"""
            CREATE TABLE #SqlOSLineageRanges (
                N INT IDENTITY(1, 1) PRIMARY KEY,
                FromId NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL,
                ToId NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL
            );
            INSERT INTO #SqlOSLineageRanges (FromId, ToId)
            SELECT MIN(x.Id), MAX(x.Id)
            FROM (SELECT Id, (ROW_NUMBER() OVER (ORDER BY Id) - 1) / {rangeRows} AS Range FROM {resources}) x
            GROUP BY x.Range
            ORDER BY x.Range;
            DECLARE @n INT = 1, @ranges INT = (SELECT COUNT(*) FROM #SqlOSLineageRanges);
            DECLARE @from NVARCHAR(450), @to NVARCHAR(450);
            WHILE @n <= @ranges
            BEGIN
                SELECT @from = FromId, @to = ToId FROM #SqlOSLineageRanges WHERE N = @n;
                BEGIN TRANSACTION;
                {body}
                COMMIT TRANSACTION;
                SET @n += 1;
            END
            DROP TABLE #SqlOSLineageRanges;
            """;

    /// <summary>The scope column of one application table's rows whose resource id lies in <c>@from</c>..<c>@to</c>.</summary>
    private static string ScopeFillRange(SqlOSFgaOptions options, SqlOSFgaScopeTable table, int levels)
    {
        var schema = Escape(options.Schema);
        var resourceId = $"t.[{Escape(table.ResourceIdColumn)}]";
        return $"""

            UPDATE t SET {ScopeAssignment(levels, "r", "rt")}
            FROM {ScopeTable(table)} t
            INNER JOIN [{schema}].[{Escape(options.TableNames.Resources)}] r ON r.Id = {resourceId}
            INNER JOIN [{schema}].[{Escape(options.TableNames.ResourceTypes)}] rt ON rt.Id = r.ResourceTypeId
            WHERE {resourceId} >= @from AND {resourceId} <= @to
              AND r.Id >= @from AND r.Id <= @to;
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
            conditions.Add($"EXISTS (SELECT 1 FROM sys.indexes WHERE name = '{SqlLiteral(SqlOSFgaLineage.ScopeIndexName(table.Table, 0, null))}' AND object_id = OBJECT_ID(N'{SqlLiteral(ScopeTable(table))}'))");
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

    /// <summary>
    /// The scope value of a row from its resource row <paramref name="r"/> (which may be NULL: the row then has
    /// no scope and nobody sees it) and the resource's type row <paramref name="rt"/>: the depth byte, the four
    /// type bytes, then eight bytes per level holding the ancestor where access flows down from that level and
    /// zero elsewhere. <c>CAST(bigint AS BINARY(8))</c> is big-endian, which is how <c>SqlOSFgaScope.Bytes</c>
    /// encodes a parameter.
    /// </summary>
    private static string ScopeValue(int levels, string r, string rt)
    {
        var parts = new StringBuilder();
        parts.Append(CultureInfo.InvariantCulture, $"CAST(ISNULL({r}.{SqlOSFgaLineage.DepthColumn}, 0) AS BINARY(1)) + CAST({rt}.{SqlOSFgaLineage.SeqColumn} AS BINARY(4))");
        for (var level = 0; level < levels; level++)
        {
            parts.Append(CultureInfo.InvariantCulture, $" + CAST(ISNULL(CASE WHEN {r}.{SqlOSFgaLineage.ReachColumn} <= {level} THEN {r}.{SqlOSFgaLineage.AncestorColumn(level)} END, 0) AS BINARY(8))");
        }

        return $"CASE WHEN {r}.Id IS NULL THEN NULL ELSE {parts} END";
    }

    private static string ScopeAssignment(int levels, string r, string rt)
        => $"[{SqlOSFgaLineage.ScopeColumn}] = {ScopeValue(levels, r, rt)}";

    /// <summary>
    /// The indexes of one application table, per level: a computed column reading the level's ancestor out of
    /// the scope column (no storage; the optimizer matches a query that spells out the same expression), then
    /// an index on it followed by the primary key, and one more per order the application declared, each
    /// filtered on the depth byte to the rows at or below the level. Indexes of orders no longer declared are
    /// dropped. Also a computed column over the type with statistics on it, so the type test in every query
    /// is estimated from data. Idempotent.
    /// </summary>
    public IReadOnlyList<string> BuildEnsureScopeIndexesSql(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> scopeTables)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopeTables);
        var levels = SqlOSFgaLineage.Levels(options);
        var batches = new List<string>();
        foreach (var table in scopeTables)
        {
            var target = ScopeTable(table);
            var literal = SqlLiteral(target);
            var key = string.Join(", ", table.KeyColumns.Select(k => $"[{Escape(k)}]"));
            var sql = new StringBuilder();
            sql.AppendLine(CultureInfo.InvariantCulture, $"""
                IF COL_LENGTH('{literal}', '{SqlOSFgaLineage.ScopeColumn}') IS NULL
                    THROW 51013, 'SqlOS FGA: {literal} has no {SqlOSFgaLineage.ScopeColumn} column. Add and apply the EF Core migration that carries it before starting SqlOS.', 1;
                """);
            sql.AppendLine(CultureInfo.InvariantCulture, $"""
                IF COL_LENGTH('{literal}', '{SqlOSFgaLineage.ScopeTypeColumn}') IS NULL
                    ALTER TABLE {target} ADD [{SqlOSFgaLineage.ScopeTypeColumn}] AS SUBSTRING([{SqlOSFgaLineage.ScopeColumn}], {SqlOSFgaLineage.ScopeTypeOffset}, 4);
                IF NOT EXISTS (SELECT 1 FROM sys.stats WHERE name = '{SqlLiteral(SqlOSFgaLineage.ScopeTypeStatisticsName(table.Table))}' AND object_id = OBJECT_ID(N'{literal}'))
                    CREATE STATISTICS [{Escape(SqlOSFgaLineage.ScopeTypeStatisticsName(table.Table))}] ON {target} ([{SqlOSFgaLineage.ScopeTypeColumn}]);
                """);
            var wanted = new List<string>();
            for (var level = 0; level < levels; level++)
            {
                var column = SqlOSFgaLineage.ScopeLevelColumn(level);
                var filter = $"[{SqlOSFgaLineage.ScopeColumn}] >= 0x{level:X2}";
                sql.AppendLine(CultureInfo.InvariantCulture, $"""
                    IF COL_LENGTH('{literal}', '{column}') IS NULL
                        ALTER TABLE {target} ADD [{column}] AS SUBSTRING([{SqlOSFgaLineage.ScopeColumn}], {SqlOSFgaLineage.ScopeAncestorOffset(level)}, 8);
                    """);
                foreach (var (name, columns) in new[] { (SqlOSFgaLineage.ScopeIndexName(table.Table, level, null), key) }
                    .Concat(table.Orders.Select(o => (SqlOSFgaLineage.ScopeIndexName(table.Table, level, o.Suffix), string.Join(", ", o.Columns.Select(c => $"[{Escape(c)}]"))))))
                {
                    wanted.Add(name);
                    sql.AppendLine(CultureInfo.InvariantCulture, $"""
                        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = '{SqlLiteral(name)}' AND object_id = OBJECT_ID(N'{literal}'))
                            CREATE NONCLUSTERED INDEX [{Escape(name)}] ON {target} ([{column}], {columns}) WHERE {filter};
                        """);
                }
            }

            var wantedList = string.Join(", ", wanted.Select(w => $"'{SqlLiteral(w)}'"));
            sql.AppendLine(CultureInfo.InvariantCulture, $"""
                DECLARE @stale NVARCHAR(MAX) = N'';
                SELECT @stale += N'DROP INDEX ' + QUOTENAME(name) + N' ON {target};'
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'{literal}')
                  AND name LIKE '{SqlLiteral(SqlOSFgaLineage.ScopeIndexPrefix(table.Table)).Replace("_", "[_]", StringComparison.Ordinal)}%'
                  AND name NOT IN ({wantedList});
                EXEC (@stale);
                """);
            batches.Add(sql.ToString());
        }

        return batches;
    }

    private static string SqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>Takes the lineage lock (see <see cref="SqlOSFgaLineage.LineageLockName"/>) for the trigger's transaction.</summary>
    private static string LineageLock(SqlOSFgaOptions options, string mode)
        => $"""
            DECLARE @sqlosLineageLock INT;
            EXEC @sqlosLineageLock = sys.sp_getapplock @Resource = N'{SqlLiteral(SqlOSFgaLineage.LineageLockName(options))}', @LockMode = '{mode}', @LockOwner = 'Transaction', @LockTimeout = -1;
            IF @sqlosLineageLock < 0 THROW 51014, 'SqlOS FGA: the resource lineage lock was not granted.', 1;
            """;

    /// <summary>
    /// Runs <paramref name="body"/> holding the lineage lock exclusively for the session: the rebuild and the
    /// one-time fill commit in ranges, and no change to the tree may land between two of them.
    /// </summary>
    private static string WithSessionLineageLock(SqlOSFgaOptions options, string body)
    {
        var name = SqlLiteral(SqlOSFgaLineage.LineageLockName(options));
        return $"""
            DECLARE @sqlosLineageLock INT;
            EXEC @sqlosLineageLock = sys.sp_getapplock @Resource = N'{name}', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = -1;
            IF @sqlosLineageLock < 0 THROW 51014, 'SqlOS FGA: the resource lineage lock was not granted.', 1;
            BEGIN TRY
                {body}
                EXEC sys.sp_releaseapplock @Resource = N'{name}', @LockOwner = 'Session';
            END TRY
            BEGIN CATCH
                IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
                EXEC sys.sp_releaseapplock @Resource = N'{name}', @LockOwner = 'Session';
                THROW;
            END CATCH
            """;
    }
}

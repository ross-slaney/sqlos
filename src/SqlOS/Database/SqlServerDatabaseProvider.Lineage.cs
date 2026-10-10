using System.Globalization;
using System.Text;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;

namespace SqlOS.Database;

/// <summary>
/// The SHRBAC enforcement artifacts around the row filter: the caller's live subjects and access roots, the
/// resource lineage (each resource's ancestor at every level, depth, and reach: the tree's closure, one row
/// per resource) with the index per level, the routines and triggers that keep it exact, and the functions
/// a list filter is made of: <c>fn_ListVisible</c> (the caller's visible resources, root by root),
/// <c>fn_VisibleSet</c> (the same, materialized) and <c>fn_ListFirst</c> (whether the caller sees few
/// enough rows of a table to list them first).
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
    /// <c>fn_AccessRoots(@SubjectIds, @PermissionId)</c>: the caller's access roots, one row per current grant
    /// of one of the caller's live subjects whose role includes the permission: the granted resource's compact
    /// key and its level. Not deduplicated, so a statement that needs only the first roots (a count up to a
    /// cap) stops reading grants once it has them. Inactive or malformed resources are left out: no row can
    /// reach them anyway.
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
                @PermissionId NVARCHAR(450)
            )
            RETURNS TABLE
            AS
            RETURN
            (
                SELECT r.Seq AS ResourceSeq, r.Depth
                FROM [{schema}].[{grants}] g
                INNER JOIN [{schema}].[{rolePermissions}] rp ON rp.RoleId = g.RoleId AND rp.PermissionId = @PermissionId
                -- Each grant's resource by its key: a lookup per grant, never a pass over the resources.
                CROSS APPLY (
                    SELECT TOP (1) x.Seq, x.Depth
                    FROM [{schema}].[{resources}] x
                    WHERE x.Id = g.ResourceId AND x.IsActive = 1 AND x.Depth IS NOT NULL
                ) r
                WHERE g.SubjectId IN (SELECT live.SubjectId FROM [{schema}].fn_ActiveSubjects(@SubjectIds) live)
                  AND (g.EffectiveFrom IS NULL OR g.EffectiveFrom <= GETUTCDATE())
                  AND (g.EffectiveTo IS NULL OR g.EffectiveTo >= GETUTCDATE())
            )
            """;
    }

    /// <summary>
    /// <c>fn_ListVisible(@SubjectIds, @PermissionId, @TypeId)</c>: the resources the caller may see with the
    /// permission (of its type, when it has one), listed from the caller's roots: for each root, the rows of
    /// the index of the root's level that hold the root as their ancestor there and whose reach extends up to
    /// it (nothing inactive between). Each root reads one range of one index; the branch of every other
    /// level is skipped by its startup filter. A resource under two roots is listed twice. The rule is the
    /// one <c>fn_IsResourceAccessible</c> applies to one resource; they agree row for row. Each branch states
    /// that the level's ancestor is not null: SQL Server uses a filtered index only for a predicate that
    /// states its filter, and does not infer it from an equality with a value it doesn't know yet.
    /// </summary>
    public string BuildListVisibleFunctionSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        var resources = Escape(options.TableNames.Resources);
        var branches = string.Join("\n        UNION ALL\n", Enumerable.Range(0, SqlOSFgaLineage.Levels(options)).Select(level =>
        {
            var l = level.ToString(CultureInfo.InvariantCulture);
            var ancestor = "r." + SqlOSFgaLineage.AncestorColumn(level);
            return $"        SELECT r.Id FROM [{schema}].[{resources}] r WHERE a.Depth = {l} AND {ancestor} = a.ResourceSeq AND {ancestor} IS NOT NULL AND r.Reach <= {l} AND (@TypeId IS NULL OR r.ResourceTypeId = @TypeId)";
        }));
        return $"""
            CREATE OR ALTER FUNCTION [{schema}].fn_ListVisible(
                @SubjectIds NVARCHAR(MAX),
                @PermissionId NVARCHAR(450),
                @TypeId NVARCHAR(450)
            )
            RETURNS TABLE
            AS
            RETURN
            (
                SELECT v.Id AS ResourceId
                FROM [{schema}].fn_AccessRoots(@SubjectIds, @PermissionId) a
                CROSS APPLY (
            {branches}
                ) v
            )
            """;
    }

    /// <summary>
    /// <c>fn_VisibleSet(@SubjectIds, @PermissionId, @TypeId)</c>: the rows of <c>fn_ListVisible</c>, each
    /// once, materialized before the statement that reads it goes on. A multi-statement function: the
    /// statement starts from it (its few rows, then the table's resource id index), instead of reading the
    /// table in order and testing each row. Used only for a caller <c>fn_ListFirst</c> found to see few rows.
    /// </summary>
    public string BuildVisibleSetFunctionSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        return $"""
            CREATE OR ALTER FUNCTION [{schema}].fn_VisibleSet(
                @SubjectIds NVARCHAR(MAX),
                @PermissionId NVARCHAR(450),
                @TypeId NVARCHAR(450)
            )
            RETURNS @visible TABLE (ResourceId NVARCHAR(450) NOT NULL PRIMARY KEY WITH (IGNORE_DUP_KEY = ON))
            AS
            BEGIN
                INSERT INTO @visible (ResourceId)
                SELECT l.ResourceId FROM [{schema}].fn_ListVisible(@SubjectIds, @PermissionId, @TypeId) l;
                RETURN;
            END
            """;
    }

    /// <summary>
    /// <c>fn_ListFirst(@SubjectIds, @PermissionId, @TypeId, @Table)</c>: one row, whether the caller sees
    /// fewer resources than the table's cap: <see cref="SqlOSFgaLineage.ListFirstCapSql"/> of its row count, as
    /// the engine keeps it in its catalog. Counts <c>fn_ListVisible</c> up to the cap and stops there, so it
    /// costs at most the cap, whoever asks. Below the cap, listing the caller's rows first and sorting them
    /// costs less than reading the table in order until a page of them turns up; above it, the reverse.
    /// </summary>
    public string BuildListFirstFunctionSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        return $"""
            CREATE OR ALTER FUNCTION [{schema}].fn_ListFirst(
                @SubjectIds NVARCHAR(MAX),
                @PermissionId NVARCHAR(450),
                @TypeId NVARCHAR(450),
                @Table NVARCHAR(776)
            )
            RETURNS TABLE
            AS
            RETURN
            (
                SELECT CAST(CASE WHEN v.Visible < c.Cap THEN 1 ELSE 0 END AS BIT) AS ListFirst
                FROM (
                    SELECT CAST({SqlOSFgaLineage.ListFirstCapSql("CAST(ISNULL(SUM(p.rows), 0) AS FLOAT)", "SQRT")} AS BIGINT) AS Cap
                    FROM sys.partitions p
                    WHERE p.object_id = OBJECT_ID(@Table) AND p.index_id IN (0, 1)
                ) c
                CROSS APPLY (
                    SELECT COUNT_BIG(*) AS Visible
                    FROM (SELECT TOP (c.Cap) 1 AS One FROM [{schema}].fn_ListVisible(@SubjectIds, @PermissionId, @TypeId)) t
                ) v
            )
            """;
    }

    /// <summary>
    /// <c>fn_CheckRow(@ResourceId, @SubjectIds, @PermissionId)</c>: one row when the point check allows the
    /// resource, none otherwise. The "check each row" filter reads it for each row the query reads. It is the
    /// point check under a name of SqlOS's own: an application that maps <c>fn_IsResourceAccessible</c> in its
    /// own model (EF Core allows one mapping per function) keeps working.
    /// </summary>
    public string BuildCheckRowFunctionSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        return $"""
            CREATE OR ALTER FUNCTION [{schema}].fn_CheckRow(
                @ResourceId NVARCHAR(450),
                @SubjectIds NVARCHAR(MAX),
                @PermissionId NVARCHAR(450)
            )
            RETURNS TABLE
            AS
            RETURN
            (
                SELECT CAST(1 AS BIT) AS Allowed
                FROM [{schema}].fn_IsResourceAccessible(@ResourceId, @SubjectIds, @PermissionId)
            )
            """;
    }

    /// <summary>The query over <c>fn_ListFirst</c>: <c>{0}</c> subject ids JSON, <c>{1}</c> permission id, <c>{2}</c> type id, <c>{3}</c> the table's quoted name.</summary>
    public string BuildListFirstQuerySql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"SELECT f.ListFirst AS [Value] FROM [{Escape(options.Schema)}].fn_ListFirst({{0}}, {{1}}, {{2}}, {{3}}) AS f";
    }

    /// <summary>
    /// Adds the ancestor columns the configured depth calls for, and the index of each level: the ancestor
    /// at that level and the type, covering what <c>fn_ListVisible</c> reads of a row beneath a root, over the
    /// rows that have an ancestor at the level. Idempotent; a deeper configuration adds columns and indexes,
    /// never removes any. Two batches: the indexes are compiled after the columns exist.
    /// </summary>
    public IReadOnlyList<string> BuildEnsureLineageColumnsSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var table = $"[{Escape(options.Schema)}].[{Escape(options.TableNames.Resources)}]";
        var columns = new StringBuilder();
        var indexes = new StringBuilder();
        for (var level = 0; level < SqlOSFgaLineage.Levels(options); level++)
        {
            var column = SqlOSFgaLineage.AncestorColumn(level);
            var index = SqlOSFgaLineage.AncestorIndexName(options.TableNames.Resources, level);
            columns.AppendLine(CultureInfo.InvariantCulture, $"""
                IF COL_LENGTH('{SqlLiteral(table)}', '{column}') IS NULL
                    ALTER TABLE {table} ADD [{column}] BIGINT NULL;
                """);
            indexes.AppendLine(CultureInfo.InvariantCulture, $"""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = '{SqlLiteral(index)}' AND object_id = OBJECT_ID(N'{SqlLiteral(table)}'))
                    CREATE NONCLUSTERED INDEX [{Escape(index)}] ON {table} ([{column}], [ResourceTypeId]) INCLUDE ([{SqlOSFgaLineage.ReachColumn}], [Id]) WHERE [{column}] IS NOT NULL;
                """);
        }

        return [columns.ToString(), indexes.ToString()];
    }

    /// <summary>
    /// The lineage maintenance: a refresh procedure (recompute the lineage of a set of changed resources and
    /// everything beneath them, rejecting a cycle or an over-deep row), a rebuild procedure (recompute every
    /// row level by level), and the two triggers on the resources table. Each batch is idempotent
    /// (<c>CREATE OR ALTER</c>).
    /// </summary>
    public IReadOnlyList<string> BuildLineageMaintenanceSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        var resources = $"[{schema}].[{Escape(options.TableNames.Resources)}]";
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
                DROP TABLE #SqlOSLineageAffected;
            END
            """;

        // The whole table, in ranges of the key: the internal nodes (every resource that is some row's parent;
        // few next to the leaves) get their lineage level by level in a temp table, then every range of the
        // table takes its rows' lineage in one transaction: the nodes' from the temp table, the leaves' from
        // their parent node. LineageBuilt is cleared before the first range and set after the last, so a
        // rebuild that fails part-way leaves it clear and the next startup runs it again.
        var copyNode = string.Join(", ", new[] { "Depth", "Reach" }.Concat(Enumerable.Range(0, levels).Select(SqlOSFgaLineage.AncestorColumn)).Select(c => $"{c} = n.{c}"));
        var rebuildProcedure = $"""
            CREATE OR ALTER PROCEDURE {rebuild}
                @RangeRows INT = {SqlOSFgaLineage.RebuildRangeRows.ToString(CultureInfo.InvariantCulture)}
            AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                {WithSessionLineageLock(options, $"""

                UPDATE [{schema}].[SqlOSFgaSchema] SET [LineageBuilt] = 0;

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

                -- 2. Every range of the table: its nodes, then its leaves.
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
                """)}
                DROP TABLE #SqlOSLineageNodes;
                UPDATE [{schema}].[SqlOSFgaSchema] SET [LineageBuilt] = 1;
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
        // index or statistics to plan by, and a bulk update would pay for it quadratically. A deleted row
        // needs no trigger: a resource with children cannot be deleted, and a leaf is in no other row's lineage.
        var updateTrigger = $"""
            CREATE OR ALTER TRIGGER [{schema}].[{Escape(triggers[1])}] ON {resources}
            AFTER UPDATE
            AS
            BEGIN
                SET NOCOUNT ON;
                IF NOT (UPDATE(ParentId) OR UPDATE(IsActive)) RETURN;
                {LineageLock(options, "Exclusive")}
                {changedTable}
                INSERT INTO #SqlOSLineageChanged (Id)
                SELECT Id FROM (SELECT Id, ParentId, IsActive FROM inserted EXCEPT SELECT Id, ParentId, IsActive FROM deleted) changed;
                IF EXISTS (SELECT 1 FROM #SqlOSLineageChanged)
                    EXEC {refresh} @RejectMalformed = 1, @WalkAll = 1;
                DROP TABLE #SqlOSLineageChanged;
            END
            """;

        return [refreshProcedure, rebuildProcedure, insertTrigger, updateTrigger];
    }

    /// <summary>Returns 1 until a rebuild has finished every range (see <c>LineageBuilt</c>).</summary>
    public string BuildLineageNeedsBuildSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM [{Escape(options.Schema)}].[SqlOSFgaSchema] WHERE [LineageBuilt] = 1)
            THEN 0 ELSE 1 END AS [Value]
            """;
    }

    public string BuildLineageRebuildSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"EXEC [{Escape(options.Schema)}].[sp_{Escape(SqlOSFgaLineage.RebuildRoutineName(options.TableNames.Resources))}];";
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

    public string BuildSelectRoutinesHashSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        var resourcesTable = options.TableNames.Resources;
        var resources = $"[{schema}].[{Escape(resourcesTable)}]";
        string Exists(string name, string type)
            => $"OBJECT_ID(N'{SqlLiteral($"[{schema}].[{Escape(name)}]")}', N'{type}') IS NOT NULL";

        var indexes = SqlOSFgaLineage.AncestorIndexNames(options);
        var conditions = new List<string>
        {
            Exists("fn_ActiveSubjects", "IF"),
            Exists("fn_AccessRoots", "IF"),
            Exists("fn_ListVisible", "IF"),
            Exists("fn_VisibleSet", "TF"),
            Exists("fn_ListFirst", "IF"),
            Exists("fn_IsResourceAccessible", "IF"),
            Exists("fn_CheckRow", "IF"),
            Exists("sp_" + SqlOSFgaLineage.RefreshRoutineName(resourcesTable), "P"),
            Exists("sp_" + SqlOSFgaLineage.RebuildRoutineName(resourcesTable), "P"),
        };
        conditions.AddRange(SqlOSFgaLineage.TriggerNames(resourcesTable).Select(t => Exists(t, "TR")));
        conditions.Add($"COL_LENGTH('{SqlLiteral(resources)}', '{SqlOSFgaLineage.AncestorColumn(SqlOSFgaLineage.MaxLevel(options))}') IS NOT NULL");
        conditions.Add($"(SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'{SqlLiteral(resources)}') AND name IN ({string.Join(", ", indexes.Select(n => $"N'{SqlLiteral(n)}'"))})) = {indexes.Count.ToString(CultureInfo.InvariantCulture)}");

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

    private static string SqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// Takes the lineage lock (see <see cref="SqlOSFgaLineage.LineageLockName"/>) for the trigger's transaction,
    /// after refusing a SNAPSHOT transaction: it keeps reading the state it started with even after waiting for
    /// the lock, so it could compute from state another transaction has since changed (PostgreSQL refuses
    /// REPEATABLE READ for the same reason). Read-committed snapshot is READ COMMITTED and is fine. A session sees
    /// its own row of <c>sys.dm_exec_sessions</c> without any server permission.
    /// </summary>
    private static string LineageLock(SqlOSFgaOptions options, string mode)
        => $"""
            IF (SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID) = 5
                THROW 51015, 'SqlOS FGA: change resources in a READ COMMITTED or SERIALIZABLE transaction, not SNAPSHOT: a snapshot transaction can miss a concurrent change to the tree.', 1;
            DECLARE @sqlosLineageLock INT;
            EXEC @sqlosLineageLock = sys.sp_getapplock @Resource = N'{SqlLiteral(SqlOSFgaLineage.LineageLockName(options))}', @LockMode = '{mode}', @LockOwner = 'Transaction', @LockTimeout = -1;
            IF @sqlosLineageLock < 0 THROW 51014, 'SqlOS FGA: the resource lineage lock was not granted.', 1;
            """;

    /// <summary>
    /// Runs <paramref name="body"/> holding the lineage lock exclusively for the session: the rebuild commits
    /// in ranges, and no change to the tree may land between two of them.
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

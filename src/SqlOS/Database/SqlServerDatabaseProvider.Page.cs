using System.Globalization;
using System.Text;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Paging;

namespace SqlOS.Database;

/// <summary>
/// What a page needs beyond the lineage (see <see cref="SqlOSFgaPageIndex"/>).
/// <list type="bullet">
/// <item>The grant counts: per (principal, resource) the principal's usable grants at or below the resource,
/// the children holding such grants, and the grants cut below an inactive descendant. The page executor
/// reads them to decide, at every node, whether to stream the node's rows or jump to its children. Kept by
/// triggers on the grants table (a grant's contribution is added or removed along its ancestors) and rebuilt
/// per principal when the tree under a grant changes or a grant's window opens or closes.</item>
/// <item>The direct index per application table: one row per (grant, row attached to the granted resource)
/// with the row's key and sort columns, indexed per declared order, so rows a principal is granted one by one
/// are one seek however many there are. Kept by the grants triggers and by the scope triggers of the table.</item>
/// <item>The round: two statements that open nodes (state, counts, first-batch size) and return the rows of
/// every stream merged in the page's order by the engine, so the executor never compares keys itself.</item>
/// </list>
/// </summary>
internal sealed partial class SqlServerDatabaseProvider
{
    private static string Validity(string g)
        => $"({g}.EffectiveFrom IS NULL OR {g}.EffectiveFrom <= GETUTCDATE()) AND ({g}.EffectiveTo IS NULL OR {g}.EffectiveTo >= GETUTCDATE())";

    private static string Counts(SqlOSFgaOptions options) => $"[{Escape(options.Schema)}].[{SqlOSFgaPageIndex.CountsTable}]";

    private static string DirectTable(SqlOSFgaOptions options, SqlOSFgaScopeTable table) => $"[{Escape(options.Schema)}].[{Escape(SqlOSFgaPageIndex.DirectTable(table))}]";

    private static string LevelCases(int levels)
        => string.Join(" ", Enumerable.Range(0, levels).Select(l => $"WHEN {l} THEN r.{SqlOSFgaLineage.AncestorColumn(l)}"));

    private static string LevelValues(int levels)
        => string.Join(", ", Enumerable.Range(0, levels).Select(l => $"({l})"));

    /// <summary>
    /// The contributions of a set of grants (<paramref name="grantsFrom"/>: a FROM item aliased <c>g</c> with
    /// SubjectId, ResourceId, EffectiveFrom, EffectiveTo) to the counts (see the PostgreSQL provider).
    /// </summary>
    private static string CountContributions(SqlOSFgaOptions options, string grantsFrom, int levels)
    {
        var resources = $"[{Escape(options.Schema)}].[{Escape(options.TableNames.Resources)}]";
        return $"""
            SELECT lv.SubjectId, lv.Node AS ResourceSeq, nd.ParentSeq,
                   COUNT(*) AS Grants,
                   SUM(CASE WHEN lv.Level < lv.Depth AND lv.Reach > lv.Level + 1 THEN 1 ELSE 0 END) AS CutGrants
            FROM (
                SELECT g.SubjectId, r.Depth, r.Reach, lvl.Level, CASE lvl.Level {LevelCases(levels)} END AS Node
                FROM {grantsFrom}
                INNER JOIN {resources} r ON r.Id = g.ResourceId
                CROSS JOIN (VALUES {LevelValues(levels)}) AS lvl(Level)
                WHERE r.Depth IS NOT NULL AND r.Reach IS NOT NULL AND lvl.Level <= r.Depth
                  AND EXISTS (SELECT 1 FROM {resources} ch WHERE ch.ParentId = r.Id)
                  AND {Validity("g")}
            ) lv
            INNER JOIN (
                SELECT c.Seq, p.Seq AS ParentSeq
                FROM {resources} c
                LEFT JOIN {resources} p ON p.Id = c.ParentId
            ) nd ON nd.Seq = lv.Node
            GROUP BY lv.SubjectId, lv.Node, nd.ParentSeq
            """;
    }

    public IReadOnlyList<string> BuildPageIndexSql(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> scopeTables)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopeTables);
        var schema = Escape(options.Schema);
        var levels = SqlOSFgaLineage.Levels(options);
        var counts = Counts(options);
        var grants = $"[{schema}].[{Escape(options.TableNames.Grants)}]";
        var rebuildCounts = $"[{schema}].[sp_{SqlOSFgaPageIndex.CountsRebuildRoutine}]";
        var adjust = $"[{schema}].[sp_{SqlOSFgaPageIndex.CountsAdjustRoutine}]";
        var refresh = $"[{schema}].[sp_{SqlOSFgaPageIndex.CountsRefreshRoutine}]";
        var rebuildAll = $"[{schema}].[sp_{SqlOSFgaPageIndex.RebuildRoutine}]";

        var countsRebuild = $"""
            CREATE OR ALTER PROCEDURE {rebuildCounts}
                @Subjects NVARCHAR(MAX) = NULL
            AS
            BEGIN
                SET NOCOUNT ON;
                DECLARE @SubjectSet TABLE (SubjectId NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY);
                IF @Subjects IS NOT NULL
                    INSERT INTO @SubjectSet (SubjectId) SELECT DISTINCT CONVERT(NVARCHAR(450), [value]) FROM OPENJSON(@Subjects);
                IF @Subjects IS NULL
                    DELETE FROM {counts};
                ELSE
                    DELETE c FROM {counts} c INNER JOIN @SubjectSet s ON s.SubjectId = c.SubjectId;
                INSERT INTO {counts} (SubjectId, ResourceSeq, ParentSeq, Grants, GrantChildren, CutGrants)
                SELECT x.SubjectId, x.ResourceSeq, x.ParentSeq, x.Grants, 0, x.CutGrants
                FROM ({CountContributions(options, $"(SELECT g0.* FROM {grants} g0 WHERE @Subjects IS NULL OR g0.SubjectId IN (SELECT SubjectId FROM @SubjectSet)) g", levels)}) x;
                UPDATE c SET GrantChildren = ch.n
                FROM {counts} c
                INNER JOIN (
                    SELECT SubjectId, ParentSeq, COUNT(*) AS n
                    FROM {counts}
                    WHERE ParentSeq IS NOT NULL AND Grants > 0 AND (@Subjects IS NULL OR SubjectId IN (SELECT SubjectId FROM @SubjectSet))
                    GROUP BY SubjectId, ParentSeq
                ) ch ON ch.SubjectId = c.SubjectId AND ch.ParentSeq = c.ResourceSeq;
            END
            """;

        // Adds (@Sign = 1) or removes (-1) the contributions of the grants in #SqlOSGrantDelta (SubjectId,
        // ResourceId, EffectiveFrom, EffectiveTo), created by the caller. The parent of a node whose grants
        // cross zero gains or loses one grant-bearing child.
        var countsAdjust = $"""
            CREATE OR ALTER PROCEDURE {adjust}
                @Sign INT
            AS
            BEGIN
                SET NOCOUNT ON;
                SELECT x.*, ISNULL(c.Grants, 0) AS Old
                INTO #SqlOSGrantCountDelta
                FROM ({CountContributions(options, "#SqlOSGrantDelta g", levels)}) x
                LEFT JOIN {counts} c ON c.SubjectId = x.SubjectId AND c.ResourceSeq = x.ResourceSeq;

                UPDATE c SET Grants = c.Grants + @Sign * d.Grants, CutGrants = c.CutGrants + @Sign * d.CutGrants, ParentSeq = d.ParentSeq
                FROM {counts} c
                INNER JOIN #SqlOSGrantCountDelta d ON d.SubjectId = c.SubjectId AND d.ResourceSeq = c.ResourceSeq;
                INSERT INTO {counts} (SubjectId, ResourceSeq, ParentSeq, Grants, GrantChildren, CutGrants)
                SELECT d.SubjectId, d.ResourceSeq, d.ParentSeq, @Sign * d.Grants, 0, @Sign * d.CutGrants
                FROM #SqlOSGrantCountDelta d
                WHERE NOT EXISTS (SELECT 1 FROM {counts} c WHERE c.SubjectId = d.SubjectId AND c.ResourceSeq = d.ResourceSeq);

                UPDATE c SET GrantChildren = c.GrantChildren + x.n
                FROM {counts} c
                INNER JOIN (
                    SELECT SubjectId, ParentSeq,
                           SUM(CASE WHEN Old <= 0 AND Old + @Sign * Grants > 0 THEN 1
                                    WHEN Old > 0 AND Old + @Sign * Grants <= 0 THEN -1
                                    ELSE 0 END) AS n
                    FROM #SqlOSGrantCountDelta
                    WHERE ParentSeq IS NOT NULL
                    GROUP BY SubjectId, ParentSeq
                ) x ON x.SubjectId = c.SubjectId AND x.ParentSeq = c.ResourceSeq
                WHERE x.n <> 0;

                DELETE c
                FROM {counts} c
                INNER JOIN #SqlOSGrantCountDelta d ON d.SubjectId = c.SubjectId AND d.ResourceSeq = c.ResourceSeq
                WHERE c.Grants <= 0 AND c.GrantChildren <= 0;
                DROP TABLE #SqlOSGrantCountDelta;
            END
            """;

        var countsRefresh = $"""
            CREATE OR ALTER PROCEDURE {refresh}
                @From DATETIME2,
                @To DATETIME2
            AS
            BEGIN
                SET NOCOUNT ON;
                DECLARE @Subjects NVARCHAR(MAX) = (
                    SELECT DISTINCT g.SubjectId AS [value]
                    FROM {grants} g
                    WHERE (g.EffectiveFrom > @From AND g.EffectiveFrom <= @To) OR (g.EffectiveTo > @From AND g.EffectiveTo <= @To)
                    FOR JSON PATH);
                IF @Subjects IS NULL
                BEGIN
                    SELECT 0 AS Refreshed;
                    RETURN;
                END
                SET @Subjects = (SELECT '[' + STRING_AGG('"' + STRING_ESCAPE(j.SubjectId, 'json') + '"', ',') + ']' FROM OPENJSON(@Subjects) WITH (SubjectId NVARCHAR(450) '$.value') j);
                EXEC {rebuildCounts} @Subjects = @Subjects;
                SELECT COUNT(*) AS Refreshed FROM OPENJSON(@Subjects);
            END
            """;

        var batches = new List<string> { countsRebuild, countsAdjust, countsRefresh };
        foreach (var table in scopeTables)
        {
            batches.AddRange(DirectTableSql(options, table));
        }

        var grantTriggers = SqlOSFgaPageIndex.GrantTriggerNames(options.TableNames.Grants);
        const string deltaTable = "CREATE TABLE #SqlOSGrantDelta (SubjectId NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL, ResourceId NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL, EffectiveFrom DATETIME2 NULL, EffectiveTo DATETIME2 NULL);";
        const string grantColumns = "Id, SubjectId, ResourceId, RoleId, EffectiveFrom, EffectiveTo";
        string DirectInserts(string grantsFrom) => string.Concat(scopeTables.Select(t => DirectInsertFromGrants(options, t, grantsFrom) + "\n"));
        string DirectDeletes(string relation) => string.Concat(scopeTables.Select(t => $"DELETE d FROM {DirectTable(options, t)} d WHERE d.GrantId IN (SELECT Id FROM {relation});\n"));
        batches.Add($"""
            CREATE OR ALTER TRIGGER [{schema}].[{Escape(grantTriggers[0])}] ON {grants}
            AFTER INSERT
            AS
            BEGIN
                SET NOCOUNT ON;
                IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
                {deltaTable}
                INSERT INTO #SqlOSGrantDelta SELECT SubjectId, ResourceId, EffectiveFrom, EffectiveTo FROM inserted;
                EXEC {adjust} @Sign = 1;
                DROP TABLE #SqlOSGrantDelta;
                {Indent(DirectInserts("inserted g"), 4)}
            END
            """);
        batches.Add($"""
            CREATE OR ALTER TRIGGER [{schema}].[{Escape(grantTriggers[2])}] ON {grants}
            AFTER DELETE
            AS
            BEGIN
                SET NOCOUNT ON;
                IF NOT EXISTS (SELECT 1 FROM deleted) RETURN;
                {deltaTable}
                INSERT INTO #SqlOSGrantDelta SELECT SubjectId, ResourceId, EffectiveFrom, EffectiveTo FROM deleted;
                EXEC {adjust} @Sign = -1;
                DROP TABLE #SqlOSGrantDelta;
                {Indent(DirectDeletes("deleted"), 4)}
            END
            """);
        batches.Add($"""
            CREATE OR ALTER TRIGGER [{schema}].[{Escape(grantTriggers[1])}] ON {grants}
            AFTER UPDATE
            AS
            BEGIN
                SET NOCOUNT ON;
                IF NOT (UPDATE(SubjectId) OR UPDATE(ResourceId) OR UPDATE(RoleId) OR UPDATE(EffectiveFrom) OR UPDATE(EffectiveTo)) RETURN;
                SELECT {grantColumns} INTO #SqlOSGrantChanged FROM (SELECT {grantColumns} FROM deleted EXCEPT SELECT {grantColumns} FROM inserted) o;
                IF NOT EXISTS (SELECT 1 FROM #SqlOSGrantChanged)
                BEGIN
                    DROP TABLE #SqlOSGrantChanged;
                    RETURN;
                END
                {deltaTable}
                INSERT INTO #SqlOSGrantDelta SELECT SubjectId, ResourceId, EffectiveFrom, EffectiveTo FROM #SqlOSGrantChanged;
                EXEC {adjust} @Sign = -1;
                {Indent(DirectDeletes("#SqlOSGrantChanged"), 4)}
                DELETE FROM #SqlOSGrantChanged;
                INSERT INTO #SqlOSGrantChanged SELECT {grantColumns} FROM inserted EXCEPT SELECT {grantColumns} FROM deleted;
                DELETE FROM #SqlOSGrantDelta;
                INSERT INTO #SqlOSGrantDelta SELECT SubjectId, ResourceId, EffectiveFrom, EffectiveTo FROM #SqlOSGrantChanged;
                EXEC {adjust} @Sign = 1;
                {Indent(DirectInserts("#SqlOSGrantChanged g"), 4)}
                DROP TABLE #SqlOSGrantDelta;
                DROP TABLE #SqlOSGrantChanged;
            END
            """);

        batches.Add($"""
            CREATE OR ALTER PROCEDURE {rebuildAll}
            AS
            BEGIN
                SET NOCOUNT ON;
                EXEC {rebuildCounts};
                {string.Concat(scopeTables.Select(t => $"EXEC [{schema}].[sp_{Escape(SqlOSFgaPageIndex.DirectRebuildRoutine(t))}];\n    "))}
            END
            """);
        return batches;
    }

    private IReadOnlyList<string> DirectTableSql(SqlOSFgaOptions options, SqlOSFgaScopeTable table)
    {
        var direct = DirectTable(options, table);
        var literal = SqlLiteral(direct);
        var columns = SqlOSFgaPageIndex.DirectColumns(table);
        var keys = string.Join(", ", table.KeyColumns.Select(k => $"[{Escape(k)}]"));
        var sql = new StringBuilder();
        sql.AppendLine(CultureInfo.InvariantCulture, $"""
            IF OBJECT_ID(N'{literal}', N'U') IS NULL
            CREATE TABLE {direct} (
                GrantId NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL,
                SubjectId NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL,
                RoleId NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL,
                EffectiveFrom DATETIME2 NULL,
                EffectiveTo DATETIME2 NULL,
                TypeSeq INT NOT NULL,
                Active BIT NOT NULL,
                {string.Join(",\n    ", columns.Select(c => $"[{Escape(c.Column)}] {c.StoreType} {(c.IsNullable ? "NULL" : "NOT NULL")}"))},
                CONSTRAINT [PK_{Escape(SqlOSFgaPageIndex.DirectTable(table))}] PRIMARY KEY (GrantId, {keys})
            );
            """);
        foreach (var order in table.Orders)
        {
            var name = SqlOSFgaPageIndex.DirectIndexName(table, order.Suffix);
            sql.AppendLine(CultureInfo.InvariantCulture, $"""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{SqlLiteral(name)}' AND object_id = OBJECT_ID(N'{literal}'))
                    CREATE NONCLUSTERED INDEX [{Escape(name)}] ON {direct} (SubjectId, RoleId, {string.Join(", ", order.Columns.Select(c => $"[{Escape(c)}]"))});
                """);
        }

        foreach (var (name, list) in new[] { (SqlOSFgaPageIndex.DirectIndexName(table, "Key"), $"SubjectId, RoleId, {keys}"), (SqlOSFgaPageIndex.DirectIndexName(table, "Row"), keys) })
        {
            sql.AppendLine(CultureInfo.InvariantCulture, $"""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{SqlLiteral(name)}' AND object_id = OBJECT_ID(N'{literal}'))
                    CREATE NONCLUSTERED INDEX [{Escape(name)}] ON {direct} ({list});
                """);
        }

        var rebuild = $"""
            CREATE OR ALTER PROCEDURE [{Escape(options.Schema)}].[sp_{Escape(SqlOSFgaPageIndex.DirectRebuildRoutine(table))}]
            AS
            BEGIN
                SET NOCOUNT ON;
                TRUNCATE TABLE {direct};
                {Indent(DirectInsertFromGrants(options, table, $"[{Escape(options.Schema)}].[{Escape(options.TableNames.Grants)}] g"), 4)}
            END
            """;
        return [sql.ToString(), rebuild];
    }

    /// <summary>Inserts the direct entries of the grants in <paramref name="grantsFrom"/> (a FROM item aliased <c>g</c>) for one table's rows.</summary>
    private static string DirectInsertFromGrants(SqlOSFgaOptions options, SqlOSFgaScopeTable table, string grantsFrom)
    {
        var columns = SqlOSFgaPageIndex.DirectColumns(table);
        var list = string.Join(", ", columns.Select(c => $"[{Escape(c.Column)}]"));
        var direct = DirectTable(options, table);
        return $"""
            INSERT INTO {direct} (GrantId, SubjectId, RoleId, EffectiveFrom, EffectiveTo, TypeSeq, Active, {list})
            SELECT g.Id, g.SubjectId, g.RoleId, g.EffectiveFrom, g.EffectiveTo, rt.Seq, CASE WHEN r.Reach IS NULL THEN 0 ELSE 1 END, {string.Join(", ", columns.Select(c => $"t.[{Escape(c.Column)}]"))}
            FROM {grantsFrom}
            INNER JOIN [{Escape(options.Schema)}].[{Escape(options.TableNames.Resources)}] r ON r.Id = g.ResourceId
            INNER JOIN [{Escape(options.Schema)}].[{Escape(options.TableNames.ResourceTypes)}] rt ON rt.Id = r.ResourceTypeId
            INNER JOIN {ScopeTable(table)} t ON t.[{Escape(table.ResourceIdColumn)}] = g.ResourceId
            WHERE r.Depth IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM {direct} d WHERE d.GrantId = g.Id AND {string.Join(" AND ", table.KeyColumns.Select(k => $"d.[{Escape(k)}] = t.[{Escape(k)}]"))});
            """;
    }

    /// <summary>Inserts the direct entries of the rows in <paramref name="rows"/> (a relation with the table's columns) from the grants on their resources.</summary>
    private static string DirectInsertFromRows(SqlOSFgaOptions options, SqlOSFgaScopeTable table, string rows)
    {
        var columns = SqlOSFgaPageIndex.DirectColumns(table);
        var list = string.Join(", ", columns.Select(c => $"[{Escape(c.Column)}]"));
        var direct = DirectTable(options, table);
        return $"""
            INSERT INTO {direct} (GrantId, SubjectId, RoleId, EffectiveFrom, EffectiveTo, TypeSeq, Active, {list})
            SELECT g.Id, g.SubjectId, g.RoleId, g.EffectiveFrom, g.EffectiveTo, rt.Seq, CASE WHEN r.Reach IS NULL THEN 0 ELSE 1 END, {string.Join(", ", columns.Select(c => $"t.[{Escape(c.Column)}]"))}
            FROM {rows} t
            INNER JOIN [{Escape(options.Schema)}].[{Escape(options.TableNames.Grants)}] g ON g.ResourceId = t.[{Escape(table.ResourceIdColumn)}]
            INNER JOIN [{Escape(options.Schema)}].[{Escape(options.TableNames.Resources)}] r ON r.Id = g.ResourceId
            INNER JOIN [{Escape(options.Schema)}].[{Escape(options.TableNames.ResourceTypes)}] rt ON rt.Id = r.ResourceTypeId
            WHERE r.Depth IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM {direct} d WHERE d.GrantId = g.Id AND {string.Join(" AND ", table.KeyColumns.Select(k => $"d.[{Escape(k)}] = t.[{Escape(k)}]"))});
            """;
    }

    private static string DirectDeleteRows(SqlOSFgaOptions options, SqlOSFgaScopeTable table, string rows)
        => $"""
            DELETE d
            FROM {DirectTable(options, table)} d
            INNER JOIN {rows} x ON {string.Join(" AND ", table.KeyColumns.Select(k => $"d.[{Escape(k)}] = x.[{Escape(k)}]"))};
            """;

    private static string DirectRefresh(SqlOSFgaOptions options, SqlOSFgaScopeTable table, string resourceIds)
        => $"""
            UPDATE d SET Active = CASE WHEN r.Reach IS NULL THEN 0 ELSE 1 END, TypeSeq = rt.Seq
            FROM {DirectTable(options, table)} d
            INNER JOIN {ScopeTable(table)} t ON {string.Join(" AND ", table.KeyColumns.Select(k => $"d.[{Escape(k)}] = t.[{Escape(k)}]"))}
            INNER JOIN {resourceIds} a ON a.Id = t.[{Escape(table.ResourceIdColumn)}]
            INNER JOIN [{Escape(options.Schema)}].[{Escape(options.TableNames.Resources)}] r ON r.Id = a.Id
            INNER JOIN [{Escape(options.Schema)}].[{Escape(options.TableNames.ResourceTypes)}] rt ON rt.Id = r.ResourceTypeId;
            """;

    /// <summary>Rebuilds the counts of the principals holding grants on the resources in <paramref name="resourceIds"/> (a relation with an Id column).</summary>
    private static string CountsRefreshForResources(SqlOSFgaOptions options, string resourceIds)
        => $"""
            DECLARE @sqlosCountSubjects NVARCHAR(MAX) = (
                SELECT '[' + STRING_AGG('"' + STRING_ESCAPE(s.SubjectId, 'json') + '"', ',') + ']'
                FROM (SELECT DISTINCT g.SubjectId FROM [{Escape(options.Schema)}].[{Escape(options.TableNames.Grants)}] g INNER JOIN {resourceIds} a ON a.Id = g.ResourceId) s);
            IF @sqlosCountSubjects IS NOT NULL EXEC [{Escape(options.Schema)}].[sp_{SqlOSFgaPageIndex.CountsRebuildRoutine}] @Subjects = @sqlosCountSubjects;
            """;

    public string BuildPageIndexRebuildSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"EXEC [{Escape(options.Schema)}].[sp_{SqlOSFgaPageIndex.RebuildRoutine}];";
    }

    public string BuildCountsRefreshSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"EXEC [{Escape(options.Schema)}].[sp_{SqlOSFgaPageIndex.CountsRefreshRoutine}] @From = @From, @To = @To;";
    }

    public string BuildNextValidityBoundarySql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var grants = $"[{Escape(options.Schema)}].[{Escape(options.TableNames.Grants)}]";
        return $"""
            SELECT MIN(b.Boundary) AS [Value] FROM (
                SELECT MIN(EffectiveFrom) AS Boundary FROM {grants} WHERE EffectiveFrom > @Now
                UNION ALL
                SELECT MIN(EffectiveTo) FROM {grants} WHERE EffectiveTo > @Now) b
            """;
    }

    private static string Indent(string sql, int spaces)
        => sql.Replace("\n", "\n" + new string(' ', spaces), StringComparison.Ordinal);

    // ---- The page round ----

    public string BuildPagePreludeSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        return $"""
            SELECT live.SubjectId FROM [{schema}].fn_ActiveSubjects(@SubjectIds) live;
            SELECT rp.RoleId FROM [{schema}].[{Escape(options.TableNames.RolePermissions)}] rp WHERE rp.PermissionId = @PermissionId;
            SELECT p.ResourceTypeId, rt.Seq AS TypeSeq, (SELECT r.Seq FROM [{schema}].[{Escape(options.TableNames.Resources)}] r WHERE r.Id = @RootId) AS RootSeq
            FROM [{schema}].[{Escape(options.TableNames.Permissions)}] p
            LEFT JOIN [{schema}].[{Escape(options.TableNames.ResourceTypes)}] rt ON rt.Id = p.ResourceTypeId
            WHERE p.Id = @PermissionId;
            """;
    }

    /// <summary>
    /// A keyset comparison SQL Server's optimizer seeks on: the first column as a range, the rest nested.
    /// (<c>c0 >= a0 AND (c0 > a0 OR (c1 >= a1 AND (c1 > a1 OR …)))</c>).
    /// </summary>
    private static string Keyset(IReadOnlyList<string> columns, IReadOnlyList<string> values)
    {
        string Rest(int i)
        {
            if (i == columns.Count - 1)
            {
                return $"{columns[i]} > {values[i]}";
            }

            return $"({columns[i]} >= {values[i]} AND ({columns[i]} > {values[i]} OR {Rest(i + 1)}))";
        }

        return Rest(0);
    }

    public string BuildPageRoundSql(SqlOSFgaOptions options, SqlOSFgaPageSpec spec)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(spec);
        var schema = Escape(options.Schema);
        var levels = SqlOSFgaLineage.Levels(options);
        var resources = $"[{schema}].[{Escape(options.TableNames.Resources)}]";
        var grants = $"[{schema}].[{Escape(options.TableNames.Grants)}]";
        var counts = Counts(options);
        var a = spec.Alias;
        var scope = $"{a}.[{SqlOSFgaLineage.ScopeColumn}]";
        var order = spec.Order;
        var afterJson = string.Join(", ", order.Select((c, i) => $"a{i} {c.StoreType} '$.a{i}'"));
        var afterList = string.Join(", ", order.Select((_, i) => $"a{i}"));
        var orderCols = order.Select(c => $"{a}.[{Escape(c.Column)}]").ToList();
        var outCols = string.Join(", ", order.Select((_, i) => $"c{i}"));
        var selectCols = string.Join(", ", order.Select((c, i) => $"{a}.[{Escape(c.Column)}] AS c{i}"));
        var afterValues = order.Select((_, i) => $"s.a{i}").ToList();
        var predicate = spec.PredicateSql is null ? "" : $"\n              AND ({spec.PredicateSql})";
        var typeFilter = spec.Typed ? $"\n              AND SUBSTRING({scope}, {SqlOSFgaLineage.ScopeTypeOffset}, 4) = @Type" : "";
        var keyset = $"(s.has_after = 0 OR {Keyset(orderCols, afterValues)})";

        var header = $"""
            DECLARE @LiveT TABLE (SubjectId NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY);
            INSERT INTO @LiveT (SubjectId) SELECT DISTINCT CONVERT(NVARCHAR(450), [value]) FROM OPENJSON(@Live);
            DECLARE @RolesT TABLE (RoleId NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY);
            INSERT INTO @RolesT (RoleId) SELECT DISTINCT CONVERT(NVARCHAR(450), [value]) FROM OPENJSON(@Roles);
            """;

        var afterColumns = string.Join(", ", order.Select((_, i) => $"o.a{i}"));
        var afterFromN = string.Join(", ", order.Select((_, i) => $"n.a{i}"));
        // A grant on the node itself. The principal test is on an expression (COLLATE), so no index on SubjectId
        // can serve it and the way in is the grants' resource index: otherwise the optimizer may start from a
        // principal's grants, all of them for a caller with thousands, and filter the resource afterwards.
        var grantedHere = $"EXISTS (SELECT 1 FROM {grants} g WHERE g.ResourceId = r.Id AND {PrincipalFilter("g")} AND {Validity("g")})";
        var common = $"""
            WITH o AS (
                SELECT * FROM OPENJSON(@Opens) WITH (req INT '$.req', seq BIGINT '$.seq', children BIT '$.children', [level] INT '$.level', cut BIT '$.cut', f INT '$.f', has_after BIT '$.has_after', {afterJson})
            ),
            gc AS (
                -- The grant-bearing children (one edge each) of every node the caller holds grants beneath.
                SELECT DISTINCT c.ParentSeq, c.ResourceSeq
                FROM {counts} c INNER JOIN @LiveT live ON live.SubjectId = c.SubjectId
                WHERE c.Grants > 0 AND c.ParentSeq IS NOT NULL
            ),
            nf AS (
                -- Per such node: how many grant-bearing children, and whether it is passed through: an active,
                -- ungranted node with exactly one grant-bearing child is opened as that child, because a denial
                -- paid at the node buys nothing (the one stream is needed either way).
                SELECT p.ParentSeq AS node, COUNT(*) AS n,
                       CASE WHEN COUNT(*) = 1 AND r.IsActive = 1 AND NOT {grantedHere} THEN 1 ELSE 0 END AS collapse
                FROM gc p INNER JOIN {resources} r ON r.Seq = p.ParentSeq
                GROUP BY p.ParentSeq, r.Id, r.IsActive
            ),
            n AS (
                SELECT o.req, o.[level], o.cut, o.f, o.has_after, {afterColumns}, o.children, o.seq AS node
                FROM o WHERE o.children = 0
                UNION ALL
                SELECT o.req, o.[level], o.cut, o.f, o.has_after, {afterColumns}, o.children, gc.ResourceSeq
                FROM o INNER JOIN gc ON gc.ParentSeq = o.seq
                WHERE o.children = 1
                UNION ALL
                SELECT n.req, n.[level] + 1, n.cut, n.f, n.has_after, {afterFromN}, n.children, gc.ResourceSeq
                FROM n
                INNER JOIN nf ON nf.node = n.node AND nf.collapse = 1
                INNER JOIN gc ON gc.ParentSeq = n.node
            ),
            nodes AS (
                SELECT n.*, CAST(r.IsActive AS BIT) AS active,
                       CAST(CASE WHEN EXISTS (SELECT 1 FROM {resources} ch WHERE ch.ParentId = r.Id) THEN 1 ELSE 0 END AS BIT) AS has_children,
                       CAST(CASE WHEN {grantedHere} THEN 1 ELSE 0 END AS BIT) AS granted,
                       ISNULL(nf.n, 0) AS thr, ISNULL(agg.cutg, 0) AS cutg,
                       CASE WHEN n.children = 1 THEN CAST(CEILING(CAST(n.f AS DECIMAL(18, 4)) / COUNT(*) OVER (PARTITION BY n.req)) AS INT) ELSE n.f END AS pf
                FROM n
                INNER JOIN {resources} r ON r.Seq = n.node
                LEFT JOIN nf ON nf.node = n.node
                OUTER APPLY (
                    SELECT SUM(c.CutGrants) AS cutg
                    FROM {counts} c INNER JOIN @LiveT live ON live.SubjectId = c.SubjectId
                    WHERE c.ResourceSeq = n.node) agg
                WHERE ISNULL(nf.collapse, 0) = 0
            ),
            opened AS (
                SELECT nodes.*,
                       CASE WHEN active = 0 THEN -1 WHEN cut = 1 THEN -2 WHEN granted = 1 AND has_children = 1 THEN 0 WHEN granted = 1 THEN -3 WHEN thr > 0 THEN 1 ELSE -4 END AS kind,
                       CASE WHEN active = 0 OR cut = 1 THEN 0
                            WHEN granted = 1 AND has_children = 1 THEN CASE WHEN pf < 2 THEN 2 ELSE pf END
                            WHEN granted = 1 THEN 0
                            WHEN thr > 0 THEN (SELECT MIN(v) FROM (VALUES (f), ((SELECT MAX(v2) FROM (VALUES (pf), (thr)) m2(v2)))) m(v))
                            ELSE 0 END AS fetch_n
                FROM nodes
            )
            """;

        var first = $"""
            {header}
            {common}
            SELECT req, node, [level], active, granted, has_children, thr, cutg, fetch_n FROM opened ORDER BY req, node;
            """;

        // Every stream is a seek of the level's mirrored index, by hint: the key order is also the clustered
        // index's order, and left to its estimates the optimizer may read the table in key order and filter
        // the level instead, which costs rows in proportion to the table rather than to the page.
        var blocks = new StringBuilder();
        for (var level = 0; level < levels; level++)
        {
            var seek = $"SUBSTRING({scope}, {SqlOSFgaPageIndex.Offset(level)}, 8) = CAST(s.seq AS BINARY(8)) AND {scope} >= 0x{level:X2}";
            var hint = $"WITH (FORCESEEK ([{Escape(SqlOSFgaLineage.ScopeIndexName(spec.Table.Table, level, spec.IndexSuffix))}] ([{SqlOSFgaLineage.ScopeLevelColumn(level)}])))";
            blocks.AppendLine(CultureInfo.InvariantCulture, $"""
                UNION ALL
                SELECT 0 AS kind, s.[level], s.seq, CAST(NULL AS NVARCHAR(450)) AS principal, CAST(NULL AS NVARCHAR(450)) AS role, q.rn, q.cnt, CAST(1 AS BIT) AS granted, {string.Join(", ", order.Select((_, i) => $"q.c{i}"))}
                FROM @StreamsT s
                CROSS APPLY (
                    SELECT ROW_NUMBER() OVER (ORDER BY {outCols}) AS rn, COUNT(*) OVER () AS cnt, z.*
                    FROM (SELECT TOP (s.f) {selectCols}
                          FROM {ScopeTable(spec.Table)} AS {a} {hint}
                          WHERE {seek}{typeFilter}{predicate}
                            AND {keyset}
                          ORDER BY {string.Join(", ", orderCols)}) z) q
                WHERE s.kind = 0 AND s.[level] = {level}
                UNION ALL
                SELECT 1, s.[level], s.seq, NULL, NULL, q.rn, q.cnt, q.granted, {string.Join(", ", order.Select((_, i) => $"q.c{i}"))}
                FROM @StreamsT s
                CROSS APPLY (
                    SELECT ROW_NUMBER() OVER (ORDER BY {outCols}) AS rn, COUNT(*) OVER () AS cnt, z.*
                    FROM (SELECT TOP (s.f) {selectCols}, CAST(CASE WHEN {RowTest(options, spec, level, levels)} THEN 1 ELSE 0 END AS BIT) AS granted
                          FROM {ScopeTable(spec.Table)} AS {a} {hint}
                          WHERE {seek}{predicate}
                            AND {keyset}
                          ORDER BY {string.Join(", ", orderCols)}) z) q
                WHERE s.kind = 1 AND s.[level] = {level}
                """);
        }

        var directCols = order.Select(c => $"d.[{Escape(c.Column)}]").ToList();
        var directJoin = spec.PredicateSql is null
            ? ""
            : $"\n                      INNER JOIN {ScopeTable(spec.Table)} AS {a} ON {string.Join(" AND ", spec.Table.KeyColumns.Select(k => $"{a}.[{Escape(k)}] = d.[{Escape(k)}]"))}";
        var directBlock = $"""
            UNION ALL
            SELECT 2, -1, CAST(0 AS BIGINT), s.principal, s.role, q.rn, q.cnt, CAST(1 AS BIT), {string.Join(", ", order.Select((_, i) => $"q.c{i}"))}
            FROM @StreamsT s
            CROSS APPLY (
                SELECT ROW_NUMBER() OVER (ORDER BY {outCols}) AS rn, COUNT(*) OVER () AS cnt, z.*
                FROM (SELECT TOP (s.f) {string.Join(", ", order.Select((c, i) => $"d.[{Escape(c.Column)}] AS c{i}"))}
                      FROM {DirectTable(options, spec.Table)} d WITH (FORCESEEK ([{Escape(SqlOSFgaPageIndex.DirectIndexName(spec.Table, spec.IndexSuffix ?? "Key"))}] ([SubjectId], [RoleId]))){directJoin}
                      WHERE d.SubjectId = s.principal AND d.RoleId = s.role AND d.Active = 1
                        AND (@TypeSeq IS NULL OR d.TypeSeq = @TypeSeq)
                        AND {Validity("d")}{predicate}
                        AND (s.has_after = 0 OR {Keyset(directCols, afterValues)})
                      ORDER BY {string.Join(", ", directCols)}) z) q
            WHERE s.kind = 2
            """;

        // The streams of the round go into a table variable first. SQL Server inlines a common table expression
        // at every reference, and the stream set is referenced by every level's blocks: evaluated there, the
        // opened nodes (the JSON, the recursive descent, the lookups) would be computed twenty times a round.
        var second = $"""
            DECLARE @StreamsT TABLE (sid INT NOT NULL, kind INT NOT NULL, [level] INT NOT NULL, seq BIGINT NOT NULL, principal NVARCHAR(450) COLLATE DATABASE_DEFAULT NULL, role NVARCHAR(450) COLLATE DATABASE_DEFAULT NULL, f INT NOT NULL, has_after BIT NOT NULL, {string.Join(", ", order.Select((c, i) => $"a{i} {c.StoreType} NULL"))});
            {common}
            INSERT INTO @StreamsT (sid, kind, [level], seq, principal, role, f, has_after, {afterList})
            SELECT s.sid, s.kind, s.[level], s.seq, s.principal, s.role, s.f, s.has_after, {afterList}
            FROM OPENJSON(@Streams) WITH (sid INT '$.sid', kind INT '$.kind', [level] INT '$.level', seq BIGINT '$.seq', principal NVARCHAR(450) '$.principal', role NVARCHAR(450) '$.role', f INT '$.f', has_after BIT '$.has_after', {afterJson}) s
            UNION ALL
            SELECT -1, kind, [level], node, NULL, NULL, fetch_n, has_after, {afterList}
            FROM opened WHERE kind IN (0, 1);
            SELECT u.* FROM (
                SELECT 0 AS kind, 0 AS [level], CAST(0 AS BIGINT) AS seq, CAST(NULL AS NVARCHAR(450)) AS principal, CAST(NULL AS NVARCHAR(450)) AS role, CAST(0 AS BIGINT) AS rn, 0 AS cnt, CAST(0 AS BIT) AS granted, {string.Join(", ", order.Select((c, i) => $"CAST(NULL AS {c.StoreType}) AS c{i}"))}
                WHERE 1 = 0
                {blocks}
                {directBlock}
            ) u
            ORDER BY {outCols};
            """;

        return first + "\n" + second;
    }

    private static string RowTest(SqlOSFgaOptions options, SqlOSFgaPageSpec spec, int level, int levels)
    {
        var scope = $"{spec.Alias}.[{SqlOSFgaLineage.ScopeColumn}]";
        var type = spec.Typed ? $"SUBSTRING({scope}, {SqlOSFgaLineage.ScopeTypeOffset}, 4) = @Type AND " : "";
        if (level + 1 >= levels)
        {
            return "1 = 0";
        }

        var ancestors = string.Join(", ", Enumerable.Range(level + 1, levels - level - 1).Select(l => $"(SUBSTRING({scope}, {SqlOSFgaPageIndex.Offset(l)}, 8))"));
        return $"""
            {type}EXISTS (
                        SELECT 1
                        FROM (VALUES {ancestors}) AS lv(anc)
                        INNER JOIN [{Escape(options.Schema)}].[{Escape(options.TableNames.Resources)}] x ON x.Seq = CAST(lv.anc AS BIGINT)
                        INNER JOIN [{Escape(options.Schema)}].[{Escape(options.TableNames.Grants)}] g ON g.ResourceId = x.Id
                        WHERE DATALENGTH(lv.anc) = 8 AND lv.anc <> 0x0000000000000000
                          AND {PrincipalFilter("g")} AND {Validity("g")})
            """;
    }

    /// <summary>
    /// A grant row's principal and role tests against the live principals and the roles. The principal test
    /// compares an expression (COLLATE), which no index on SubjectId can serve: the grants are reached through
    /// their resource, never by scanning a principal's grants (all of them, for a caller with thousands).
    /// </summary>
    private static string PrincipalFilter(string grant)
        => $"{grant}.SubjectId COLLATE DATABASE_DEFAULT IN (SELECT live.SubjectId FROM @LiveT live) AND {grant}.RoleId IN (SELECT ro.RoleId FROM @RolesT ro)";
}

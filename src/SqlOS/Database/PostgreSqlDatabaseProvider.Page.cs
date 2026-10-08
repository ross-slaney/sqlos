using System.Globalization;
using System.Text;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Paging;

namespace SqlOS.Database;

/// <summary>
/// What a page needs beyond the lineage (see <see cref="SqlOSFgaPageIndex"/>): the grant counts, the direct
/// indexes, their maintenance, and the two statements of a page round. See the SQL Server provider for the
/// design notes; this is its PostgreSQL translation.
/// </summary>
internal sealed partial class PostgreSqlDatabaseProvider
{
    private const string ZeroAncestor = "'\\x0000000000000000'::bytea";

    private static string Validity(string g)
        => $"({g}.\"EffectiveFrom\" IS NULL OR {g}.\"EffectiveFrom\" <= (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')) AND ({g}.\"EffectiveTo\" IS NULL OR {g}.\"EffectiveTo\" >= (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'))";

    private string Counts(SqlOSFgaOptions options) => Qualify(options.Schema, SqlOSFgaPageIndex.CountsTable);

    private string DirectTable(SqlOSFgaOptions options, SqlOSFgaScopeTable table) => Qualify(options.Schema, SqlOSFgaPageIndex.DirectTable(table));

    private static string LevelCases(int levels)
        => string.Join(" ", Enumerable.Range(0, levels).Select(l => $"WHEN {l} THEN r.\"{SqlOSFgaLineage.AncestorColumn(l)}\""));

    /// <summary>
    /// The contributions of a set of grants (<paramref name="grantsFrom"/>: a FROM item aliased <c>g</c> with
    /// SubjectId, ResourceId, EffectiveFrom, EffectiveTo) to the counts: one row per (principal,
    /// ancestor-or-self of the granted resource) with the grants at or below it and those cut below it. Only
    /// usable grants (within their window, on an active, well-formed resource) contribute: an unusable grant
    /// reaches nothing.
    /// </summary>
    private string CountContributions(SqlOSFgaOptions options, string grantsFrom, int levels)
    {
        var resources = Qualify(options.Schema, options.TableNames.Resources);
        return $"""
            SELECT lv."SubjectId", lv.node AS "ResourceSeq", nd."ParentSeq",
                   count(*)::int AS "Grants",
                   (count(*) FILTER (WHERE lv.level < lv."Depth" AND lv."Reach" > lv.level + 1))::int AS "CutGrants"
            FROM (
                SELECT g."SubjectId", r."Depth", r."Reach", gs.level, CASE gs.level {LevelCases(levels)} END AS node
                FROM {grantsFrom}
                INNER JOIN {resources} r ON r."Id" = g."ResourceId"
                CROSS JOIN generate_series(0, {(levels - 1).ToString(CultureInfo.InvariantCulture)}) AS gs(level)
                WHERE r."Depth" IS NOT NULL AND r."Reach" IS NOT NULL AND gs.level <= r."Depth"
                  AND EXISTS (SELECT 1 FROM {resources} ch WHERE ch."ParentId" = r."Id")
                  AND {Validity("g")}
            ) lv
            INNER JOIN (
                SELECT c."Seq", p."Seq" AS "ParentSeq"
                FROM {resources} c
                LEFT JOIN {resources} p ON p."Id" = c."ParentId"
            ) nd ON nd."Seq" = lv.node
            GROUP BY lv."SubjectId", lv.node, nd."ParentSeq"
            """;
    }

    /// <summary>The routines of the counts and the direct indexes, the triggers on the grants table, and the rebuild of all of it. Idempotent.</summary>
    public IReadOnlyList<string> BuildPageIndexSql(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> scopeTables)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopeTables);
        var schema = options.Schema;
        var levels = SqlOSFgaLineage.Levels(options);
        var counts = Counts(options);
        var grants = Qualify(schema, options.TableNames.Grants);
        var rebuildCounts = Qualify(schema, "fn_" + SqlOSFgaPageIndex.CountsRebuildRoutine);
        var adjust = Qualify(schema, "fn_" + SqlOSFgaPageIndex.CountsAdjustRoutine);
        var refresh = Qualify(schema, "fn_" + SqlOSFgaPageIndex.CountsRefreshRoutine);
        var rebuildAll = Qualify(schema, "fn_" + SqlOSFgaPageIndex.RebuildRoutine);

        var countsRebuild = $"""
            CREATE OR REPLACE FUNCTION {rebuildCounts}(p_subjects varchar[] DEFAULT NULL)
            RETURNS void
            LANGUAGE plpgsql
            AS $sqlos$
            BEGIN
                IF p_subjects IS NULL THEN
                    DELETE FROM {counts};
                ELSE
                    DELETE FROM {counts} WHERE "SubjectId" = ANY (p_subjects);
                END IF;
                INSERT INTO {counts} ("SubjectId", "ResourceSeq", "ParentSeq", "Grants", "GrantChildren", "CutGrants")
                SELECT x."SubjectId", x."ResourceSeq", x."ParentSeq", x."Grants", 0, x."CutGrants"
                FROM ({CountContributions(options, $"(SELECT * FROM {grants} g0 WHERE p_subjects IS NULL OR g0.\"SubjectId\" = ANY (p_subjects)) g", levels)}) x;
                UPDATE {counts} c SET "GrantChildren" = ch.n
                FROM (
                    SELECT "SubjectId", "ParentSeq", count(*)::int AS n
                    FROM {counts}
                    WHERE "ParentSeq" IS NOT NULL AND "Grants" > 0 AND (p_subjects IS NULL OR "SubjectId" = ANY (p_subjects))
                    GROUP BY "SubjectId", "ParentSeq"
                ) ch
                WHERE c."SubjectId" = ch."SubjectId" AND c."ResourceSeq" = ch."ParentSeq";
            END
            $sqlos$;
            """;

        // Adds or removes a set of grants' contributions. The parent of a node whose grants cross zero gains
        // or loses one grant-bearing child; every ancestor row exists once the contributions are in, since a
        // granted node's ancestors each count it at or below them. The delta lives in arrays, not a temp
        // table: a statement trigger may run this thousands of times in one transaction (a bulk insert of
        // grants, one statement per row), and every temp table created in a transaction holds locks until it
        // commits, which exhausts the lock table (PostgreSQL's "out of shared memory").
        const string delta = "unnest(v_subjects, v_seqs, v_parents, v_grants, v_cuts, v_old) AS d(\"SubjectId\", \"ResourceSeq\", \"ParentSeq\", \"Grants\", \"CutGrants\", \"Old\")";
        var countsAdjust = $"""
            CREATE OR REPLACE FUNCTION {adjust}(p_subjects varchar[], p_resources varchar[], p_from timestamp[], p_to timestamp[], p_sign int)
            RETURNS void
            LANGUAGE plpgsql
            AS $sqlos$
            DECLARE
                v_subjects varchar[];
                v_seqs bigint[];
                v_parents bigint[];
                v_grants int[];
                v_cuts int[];
                v_old int[];
            BEGIN
                SELECT array_agg(x."SubjectId"), array_agg(x."ResourceSeq"), array_agg(x."ParentSeq"), array_agg(x."Grants"), array_agg(x."CutGrants"), array_agg(COALESCE(c."Grants", 0))
                INTO v_subjects, v_seqs, v_parents, v_grants, v_cuts, v_old
                FROM ({CountContributions(options, "unnest(p_subjects, p_resources, p_from, p_to) AS g(\"SubjectId\", \"ResourceId\", \"EffectiveFrom\", \"EffectiveTo\")", levels)}) x
                LEFT JOIN {counts} c ON c."SubjectId" = x."SubjectId" AND c."ResourceSeq" = x."ResourceSeq";
                IF v_subjects IS NULL THEN
                    RETURN;
                END IF;

                INSERT INTO {counts} ("SubjectId", "ResourceSeq", "ParentSeq", "Grants", "GrantChildren", "CutGrants")
                SELECT d."SubjectId", d."ResourceSeq", d."ParentSeq", p_sign * d."Grants", 0, p_sign * d."CutGrants"
                FROM {delta}
                ON CONFLICT ("SubjectId", "ResourceSeq") DO UPDATE
                    SET "Grants" = {counts}."Grants" + EXCLUDED."Grants",
                        "CutGrants" = {counts}."CutGrants" + EXCLUDED."CutGrants",
                        "ParentSeq" = EXCLUDED."ParentSeq";

                UPDATE {counts} c SET "GrantChildren" = c."GrantChildren" + x.n
                FROM (
                    SELECT d."SubjectId", d."ParentSeq",
                           sum(CASE WHEN d."Old" <= 0 AND d."Old" + p_sign * d."Grants" > 0 THEN 1
                                    WHEN d."Old" > 0 AND d."Old" + p_sign * d."Grants" <= 0 THEN -1
                                    ELSE 0 END)::int AS n
                    FROM {delta}
                    WHERE d."ParentSeq" IS NOT NULL
                    GROUP BY d."SubjectId", d."ParentSeq"
                ) x
                WHERE c."SubjectId" = x."SubjectId" AND c."ResourceSeq" = x."ParentSeq" AND x.n <> 0;

                DELETE FROM {counts} c
                USING {delta}
                WHERE c."SubjectId" = d."SubjectId" AND c."ResourceSeq" = d."ResourceSeq" AND c."Grants" <= 0 AND c."GrantChildren" <= 0;
            END
            $sqlos$;
            """;

        // A grant's window opening or closing is a clock event, not a write: the principals whose grants
        // crossed a boundary in (p_from, p_to] are rebuilt, by the hosted refresh service.
        var countsRefresh = $"""
            CREATE OR REPLACE FUNCTION {refresh}(p_from timestamp, p_to timestamp)
            RETURNS int
            LANGUAGE plpgsql
            AS $sqlos$
            DECLARE
                v_subjects varchar[];
            BEGIN
                SELECT array_agg(DISTINCT g."SubjectId") INTO v_subjects
                FROM {grants} g
                WHERE (g."EffectiveFrom" > p_from AND g."EffectiveFrom" <= p_to) OR (g."EffectiveTo" > p_from AND g."EffectiveTo" <= p_to);
                IF v_subjects IS NULL THEN
                    RETURN 0;
                END IF;
                PERFORM {rebuildCounts}(v_subjects);
                RETURN cardinality(v_subjects);
            END
            $sqlos$;
            """;

        var batches = new List<string> { countsRebuild, countsAdjust, countsRefresh };
        foreach (var table in scopeTables)
        {
            batches.Add(DirectTableSql(options, table));
        }

        // The grants triggers: every change to a grant adjusts the counts of its principal and the direct
        // entries of its rows. A change of a grant's subject, role, resource or window is a removal and an insertion.
        var grantTriggers = SqlOSFgaPageIndex.GrantTriggerNames(options.TableNames.Grants);
        var onInsert = Qualify(schema, $"fn_{options.TableNames.Grants}_PageOnInsert");
        var onUpdate = Qualify(schema, $"fn_{options.TableNames.Grants}_PageOnUpdate");
        var onDelete = Qualify(schema, $"fn_{options.TableNames.Grants}_PageOnDelete");
        string Adjust(string relation, int sign)
            => $"PERFORM {adjust}(ARRAY(SELECT \"SubjectId\" FROM {relation}), ARRAY(SELECT \"ResourceId\" FROM {relation}), ARRAY(SELECT \"EffectiveFrom\" FROM {relation}), ARRAY(SELECT \"EffectiveTo\" FROM {relation}), {sign.ToString(CultureInfo.InvariantCulture)});";
        string DirectInserts(string grantsFrom) => string.Concat(scopeTables.Select(t => DirectInsertFromGrants(options, t, grantsFrom) + "\n"));
        string DirectDeletes(string relation) => string.Concat(scopeTables.Select(t => $"DELETE FROM {DirectTable(options, t)} WHERE \"GrantId\" IN (SELECT \"Id\" FROM {relation});\n"));
        const string grantColumns = "\"Id\", \"SubjectId\", \"ResourceId\", \"RoleId\", \"EffectiveFrom\", \"EffectiveTo\"";

        // The changed grants of an update, held in arrays (see the counts adjustment above for why not a temp table).
        const string changedArrays = "v_ids, v_subjects, v_resources, v_roles, v_from, v_to";
        const string changedRows = "unnest(v_ids, v_subjects, v_resources, v_roles, v_from, v_to) AS g(\"Id\", \"SubjectId\", \"ResourceId\", \"RoleId\", \"EffectiveFrom\", \"EffectiveTo\")";
        string Collect(string fromRows, string notInRows)
            => $"""
                SELECT array_agg(r."Id"), array_agg(r."SubjectId"), array_agg(r."ResourceId"), array_agg(r."RoleId"), array_agg(r."EffectiveFrom"), array_agg(r."EffectiveTo")
                    INTO {changedArrays}
                    FROM (SELECT {grantColumns} FROM {fromRows} EXCEPT SELECT {grantColumns} FROM {notInRows}) r;
                """;
        batches.Add($"""
            CREATE OR REPLACE FUNCTION {onInsert}()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $sqlos$
            BEGIN
                {Adjust("new_rows", 1)}
                {Indent(DirectInserts("new_rows g"), 4)}
                RETURN NULL;
            END
            $sqlos$;
            CREATE OR REPLACE FUNCTION {onDelete}()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $sqlos$
            BEGIN
                {Adjust("old_rows", -1)}
                {Indent(DirectDeletes("old_rows"), 4)}
                RETURN NULL;
            END
            $sqlos$;
            CREATE OR REPLACE FUNCTION {onUpdate}()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $sqlos$
            DECLARE
                v_ids varchar[];
                v_subjects varchar[];
                v_resources varchar[];
                v_roles varchar[];
                v_from timestamp[];
                v_to timestamp[];
            BEGIN
                -- Only a changed subject, role, resource or window matters; found with EXCEPT, never a join of
                -- the transition tables. The old values leave, the new ones arrive.
                {Collect("old_rows", "new_rows")}
                IF v_ids IS NULL THEN
                    RETURN NULL;
                END IF;
                PERFORM {adjust}(v_subjects, v_resources, v_from, v_to, -1);
                {Indent(string.Concat(scopeTables.Select(t => $"DELETE FROM {DirectTable(options, t)} WHERE \"GrantId\" = ANY (v_ids);\n")), 4)}
                {Collect("new_rows", "old_rows")}
                PERFORM {adjust}(v_subjects, v_resources, v_from, v_to, 1);
                {Indent(DirectInserts(changedRows), 4)}
                RETURN NULL;
            END
            $sqlos$;
            CREATE OR REPLACE TRIGGER {QuoteIdentifier(grantTriggers[0])}
                AFTER INSERT ON {grants}
                REFERENCING NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION {onInsert}();
            CREATE OR REPLACE TRIGGER {QuoteIdentifier(grantTriggers[1])}
                AFTER UPDATE ON {grants}
                REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION {onUpdate}();
            CREATE OR REPLACE TRIGGER {QuoteIdentifier(grantTriggers[2])}
                AFTER DELETE ON {grants}
                REFERENCING OLD TABLE AS old_rows
                FOR EACH STATEMENT EXECUTE FUNCTION {onDelete}();
            """);

        batches.Add($"""
            CREATE OR REPLACE FUNCTION {rebuildAll}()
            RETURNS void
            LANGUAGE plpgsql
            AS $sqlos$
            BEGIN
                PERFORM {rebuildCounts}(NULL);
                {string.Concat(scopeTables.Select(t => $"PERFORM {Qualify(schema, "fn_" + SqlOSFgaPageIndex.DirectRebuildRoutine(t))}();\n    "))}
            END
            $sqlos$;
            """);
        return batches;
    }

    /// <summary>The direct index of one table: the table, its indexes, and its rebuild routine.</summary>
    private string DirectTableSql(SqlOSFgaOptions options, SqlOSFgaScopeTable table)
    {
        var direct = DirectTable(options, table);
        var columns = SqlOSFgaPageIndex.DirectColumns(table);
        var sql = new StringBuilder();
        sql.AppendLine(CultureInfo.InvariantCulture, $"""
            CREATE TABLE IF NOT EXISTS {direct} (
                "GrantId" varchar(450) NOT NULL,
                "SubjectId" varchar(450) NOT NULL,
                "RoleId" varchar(450) NOT NULL,
                "EffectiveFrom" timestamp NULL,
                "EffectiveTo" timestamp NULL,
                "TypeSeq" integer NOT NULL,
                "Active" boolean NOT NULL,
                {string.Join(",\n    ", columns.Select(c => $"{QuoteIdentifier(c.Column)} {c.StoreType} {(c.IsNullable ? "NULL" : "NOT NULL")}"))},
                PRIMARY KEY ("GrantId", {string.Join(", ", table.KeyColumns.Select(QuoteIdentifier))})
            );
            """);
        foreach (var order in table.Orders)
        {
            sql.AppendLine(CultureInfo.InvariantCulture,
                $"CREATE INDEX IF NOT EXISTS {QuoteIdentifier(SqlOSFgaPageIndex.DirectIndexName(table, order.Suffix))} ON {direct} (\"SubjectId\", \"RoleId\", {string.Join(", ", order.Columns.Select(QuoteIdentifier))});");
        }

        sql.AppendLine(CultureInfo.InvariantCulture, $"""
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier(SqlOSFgaPageIndex.DirectIndexName(table, "Key"))} ON {direct} ("SubjectId", "RoleId", {string.Join(", ", table.KeyColumns.Select(QuoteIdentifier))});
            CREATE INDEX IF NOT EXISTS {QuoteIdentifier(SqlOSFgaPageIndex.DirectIndexName(table, "Row"))} ON {direct} ({string.Join(", ", table.KeyColumns.Select(QuoteIdentifier))});
            CREATE OR REPLACE FUNCTION {Qualify(options.Schema, "fn_" + SqlOSFgaPageIndex.DirectRebuildRoutine(table))}()
            RETURNS void
            LANGUAGE plpgsql
            AS $sqlos$
            BEGIN
                TRUNCATE {direct};
                {Indent(DirectInsertFromGrants(options, table, Qualify(options.Schema, options.TableNames.Grants) + " g"), 4)}
            END
            $sqlos$;
            """);
        return sql.ToString();
    }

    /// <summary>Inserts the direct entries of the grants in <paramref name="grantsFrom"/> (a FROM item aliased <c>g</c> with the grant columns) for one table's rows.</summary>
    private string DirectInsertFromGrants(SqlOSFgaOptions options, SqlOSFgaScopeTable table, string grantsFrom)
    {
        var columns = SqlOSFgaPageIndex.DirectColumns(table);
        var list = string.Join(", ", columns.Select(c => QuoteIdentifier(c.Column)));
        return $"""
            INSERT INTO {DirectTable(options, table)} ("GrantId", "SubjectId", "RoleId", "EffectiveFrom", "EffectiveTo", "TypeSeq", "Active", {list})
            SELECT g."Id", g."SubjectId", g."RoleId", g."EffectiveFrom", g."EffectiveTo", rt."Seq", (r."Reach" IS NOT NULL), {string.Join(", ", columns.Select(c => "t." + QuoteIdentifier(c.Column)))}
            FROM {grantsFrom}
            INNER JOIN {Qualify(options.Schema, options.TableNames.Resources)} r ON r."Id" = g."ResourceId"
            INNER JOIN {Qualify(options.Schema, options.TableNames.ResourceTypes)} rt ON rt."Id" = r."ResourceTypeId"
            INNER JOIN {ScopeTable(table)} t ON t.{QuoteIdentifier(table.ResourceIdColumn)} = g."ResourceId"
            WHERE r."Depth" IS NOT NULL
            ON CONFLICT DO NOTHING;
            """;
    }

    /// <summary>Inserts the direct entries of the rows in <paramref name="rows"/> (a relation with the table's columns) from the grants on their resources.</summary>
    private string DirectInsertFromRows(SqlOSFgaOptions options, SqlOSFgaScopeTable table, string rows)
    {
        var columns = SqlOSFgaPageIndex.DirectColumns(table);
        var list = string.Join(", ", columns.Select(c => QuoteIdentifier(c.Column)));
        return $"""
            INSERT INTO {DirectTable(options, table)} ("GrantId", "SubjectId", "RoleId", "EffectiveFrom", "EffectiveTo", "TypeSeq", "Active", {list})
            SELECT g."Id", g."SubjectId", g."RoleId", g."EffectiveFrom", g."EffectiveTo", rt."Seq", (r."Reach" IS NOT NULL), {string.Join(", ", columns.Select(c => "t." + QuoteIdentifier(c.Column)))}
            FROM {rows} t
            INNER JOIN {Qualify(options.Schema, options.TableNames.Grants)} g ON g."ResourceId" = t.{QuoteIdentifier(table.ResourceIdColumn)}
            INNER JOIN {Qualify(options.Schema, options.TableNames.Resources)} r ON r."Id" = g."ResourceId"
            INNER JOIN {Qualify(options.Schema, options.TableNames.ResourceTypes)} rt ON rt."Id" = r."ResourceTypeId"
            WHERE r."Depth" IS NOT NULL
            ON CONFLICT DO NOTHING;
            """;
    }

    /// <summary>Deletes the direct entries of the rows in <paramref name="rows"/> (a relation with the key columns).</summary>
    private string DirectDeleteRows(SqlOSFgaOptions options, SqlOSFgaScopeTable table, string rows)
        => $"""
            DELETE FROM {DirectTable(options, table)} d
            USING {rows} x
            WHERE {string.Join(" AND ", table.KeyColumns.Select(k => $"d.{QuoteIdentifier(k)} = x.{QuoteIdentifier(k)}"))};
            """;

    /// <summary>Sets the activity and type of the direct entries of the rows whose resource is in <paramref name="resourceIds"/> (a relation with an "Id" column).</summary>
    private string DirectRefresh(SqlOSFgaOptions options, SqlOSFgaScopeTable table, string resourceIds)
        => $"""
            UPDATE {DirectTable(options, table)} d SET "Active" = (r."Reach" IS NOT NULL), "TypeSeq" = rt."Seq"
            FROM {resourceIds} a
            INNER JOIN {ScopeTable(table)} t ON t.{QuoteIdentifier(table.ResourceIdColumn)} = a."Id"
            INNER JOIN {Qualify(options.Schema, options.TableNames.Resources)} r ON r."Id" = a."Id"
            INNER JOIN {Qualify(options.Schema, options.TableNames.ResourceTypes)} rt ON rt."Id" = r."ResourceTypeId"
            WHERE {string.Join(" AND ", table.KeyColumns.Select(k => $"d.{QuoteIdentifier(k)} = t.{QuoteIdentifier(k)}"))};
            """;

    /// <summary>Rebuilds the counts of the principals holding grants on the resources in <paramref name="resourceIds"/> (a relation with an "Id" column): their lineage changed.</summary>
    private string CountsRefreshForResources(SqlOSFgaOptions options, string resourceIds)
        => $"""
            PERFORM {Qualify(options.Schema, "fn_" + SqlOSFgaPageIndex.CountsRebuildRoutine)}(ARRAY(
                SELECT DISTINCT g."SubjectId" FROM {Qualify(options.Schema, options.TableNames.Grants)} g
                INNER JOIN {resourceIds} a ON a."Id" = g."ResourceId"));
            """;

    public string BuildPageIndexRebuildSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"SELECT {Qualify(options.Schema, "fn_" + SqlOSFgaPageIndex.RebuildRoutine)}();";
    }

    public string BuildCountsRefreshSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"SELECT {Qualify(options.Schema, "fn_" + SqlOSFgaPageIndex.CountsRefreshRoutine)}(@From, @To);";
    }

    public string BuildNextValidityBoundarySql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var grants = Qualify(options.Schema, options.TableNames.Grants);
        return $"""
            SELECT LEAST(
                (SELECT MIN("EffectiveFrom") FROM {grants} WHERE "EffectiveFrom" > @Now),
                (SELECT MIN("EffectiveTo") FROM {grants} WHERE "EffectiveTo" > @Now)) AS "Value"
            """;
    }

    private static string Indent(string sql, int spaces)
        => sql.Replace("\n", "\n" + new string(' ', spaces), StringComparison.Ordinal);

    // ---- The page round ----

    /// <summary>
    /// The caller's live principals, the roles that carry the permission, and the permission's type: three
    /// result sets. Parameters: <c>@SubjectIds</c> (JSON), <c>@PermissionId</c>, <c>@RootId</c>.
    /// </summary>
    public string BuildPagePreludeSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = QuoteIdentifier(options.Schema);
        return $"""
            SELECT live."SubjectId" FROM {schema}."fn_ActiveSubjects"(@SubjectIds) live;
            SELECT rp."RoleId" FROM {Qualify(options.Schema, options.TableNames.RolePermissions)} rp WHERE rp."PermissionId" = @PermissionId;
            SELECT p."ResourceTypeId", rt."Seq" AS "TypeSeq", (SELECT r."Seq" FROM {Qualify(options.Schema, options.TableNames.Resources)} r WHERE r."Id" = @RootId) AS "RootSeq"
            FROM {Qualify(options.Schema, options.TableNames.Permissions)} p
            LEFT JOIN {Qualify(options.Schema, options.TableNames.ResourceTypes)} rt ON rt."Id" = p."ResourceTypeId"
            WHERE p."Id" = @PermissionId;
            """;
    }

    /// <summary>
    /// The two statements of a round: the nodes opened (their state, counts and first-batch size), then the
    /// rows of every stream — the ones opened by this round included — merged in the page's order. Inputs are
    /// JSON: <c>@Opens</c> (req, seq, children, level, cut, f, has_after, a0…), <c>@Streams</c> (sid, kind,
    /// level, seq, principal, role, f, has_after, a0…), <c>@Live</c> and <c>@Roles</c> (arrays), <c>@Type</c>
    /// (the type bytes, or NULL), <c>@TypeSeq</c>; plus the filter's own parameters.
    /// </summary>
    public string BuildPageRoundSql(SqlOSFgaOptions options, SqlOSFgaPageSpec spec)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(spec);
        var levels = SqlOSFgaLineage.Levels(options);
        var resources = Qualify(options.Schema, options.TableNames.Resources);
        var grants = Qualify(options.Schema, options.TableNames.Grants);
        var counts = Counts(options);
        var a = spec.Alias;
        var scope = $"{a}.{QuoteIdentifier(SqlOSFgaLineage.ScopeColumn)}";
        var order = spec.Order;
        var after = string.Join(", ", order.Select((_, i) => $"a{i}"));
        var afterTyped = string.Join(", ", order.Select((c, i) => $"a{i} {c.StoreType}"));
        // A descending page reads the same indexes backwards: the order, the merge and the keyset all flip.
        var direction = spec.Descending ? " DESC" : "";
        var orderBy = string.Join(", ", order.Select(c => $"{a}.{QuoteIdentifier(c.Column)}{direction}"));
        var outOrder = string.Join(", ", order.Select((c, i) => $"c{i}{direction}"));
        var selectCols = string.Join(", ", order.Select((c, i) => $"{a}.{QuoteIdentifier(c.Column)} AS c{i}"));
        var predicate = spec.PredicateSql is null ? "" : $"\n              AND ({spec.PredicateSql})";
        var typeFilter = spec.Typed ? $"\n              AND SUBSTRING({scope}, {SqlOSFgaLineage.ScopeTypeOffset}, 4) = @Type" : "";

        // A stream with a position seeks straight to it: a range on the order's first column (which the index
        // serves), the rest nested, written only in the blocks for positioned streams. Wrapped in "no position
        // OR …" it is no range at all, and a root stream would read every row before the position.
        var afterValues = order.Select((_, i) => $"s.a{i}").ToList();
        var keyset = Keyset(order.Select(c => $"{a}.{QuoteIdentifier(c.Column)}").ToList(), afterValues, spec.Descending);

        var afterColumns = string.Join(", ", order.Select((_, i) => $"o.a{i}"));
        var afterFromN = string.Join(", ", order.Select((_, i) => $"n.a{i}"));
        // A grant on the node itself: the grants on the resource first (the OFFSET 0 fence keeps that the only
        // way in), the principals and roles as filters after. Left to itself the planner starts from the
        // principals' grants, all of them for a caller with thousands.
        var grantedHere = $"EXISTS (SELECT 1 FROM {GrantsOn(grants, "r.\"Id\"")} g WHERE g.\"SubjectId\" = ANY ({LiveArray}) AND g.\"RoleId\" = ANY ({RolesArray}) AND {Validity("g")})";

        // The grant-bearing children of a node, one row each: an index seek per live principal, never a scan
        // of the caller's whole counts (the plan is generic, so the principals' number is unknown to it).
        string GrantChildren(string node) => $"(SELECT DISTINCT c.\"ResourceSeq\" AS child FROM {counts} c INNER JOIN live ON live.\"SubjectId\" = c.\"SubjectId\" WHERE c.\"ParentSeq\" = {node} AND c.\"Grants\" > 0)";
        string GrantChildCount(string node) => $"(SELECT count(*) FROM {GrantChildren(node)} x)";
        var common = $"""
            WITH RECURSIVE live AS (SELECT CAST(value AS varchar(450)) AS "SubjectId" FROM jsonb_array_elements_text(@Live::jsonb) AS t(value)),
            roles AS (SELECT CAST(value AS varchar(450)) AS "RoleId" FROM jsonb_array_elements_text(@Roles::jsonb) AS t(value)),
            o AS (SELECT * FROM jsonb_to_recordset(@Opens::jsonb) AS o(req int, seq bigint, children boolean, level int, cut boolean, f int, has_after boolean, {afterTyped})),
            n AS (
                SELECT o.req, o.level, o.cut, o.f, o.has_after, {afterColumns}, o.children, o.seq AS node
                FROM o WHERE NOT o.children
                UNION ALL
                SELECT o.req, o.level, o.cut, o.f, o.has_after, {afterColumns}, o.children, gc.child
                FROM o CROSS JOIN LATERAL {GrantChildren("o.seq")} gc
                WHERE o.children
                UNION ALL
                -- An active, ungranted node with exactly one grant-bearing child is opened as that child, as far
                -- down as the shape repeats: a denial paid at the node buys nothing (the one stream is needed
                -- either way) and costs a round trip.
                SELECT n.req, n.level + 1, n.cut, n.f, n.has_after, {afterFromN}, n.children, gc.child
                FROM n
                CROSS JOIN LATERAL (SELECT r."Id" FROM {resources} r WHERE r."Seq" = n.node AND r."IsActive" LIMIT 1) r
                CROSS JOIN LATERAL {GrantChildren("n.node")} gc
                WHERE {GrantChildCount("n.node")} = 1 AND NOT {grantedHere}
            ),
            nodes AS (
                -- The resource of each node, and whether it has a child, through lateral lookups (the LIMIT keeps
                -- each one probe): the planner's estimate of a recursive set is loose, and a join on it would
                -- hash the whole resource table, as an EXISTS on the children would (the planner turns such an
                -- EXISTS into a hashed subplan that reads every resource's parent once per statement).
                SELECT n.*, r."IsActive" AS active,
                       COALESCE(hc.yes, FALSE) AS has_children,
                       g.granted,
                       g.thr, COALESCE(agg.cutg, 0) AS cutg,
                       CASE WHEN n.children THEN GREATEST(2, CEIL(n.f::numeric / COUNT(*) OVER (PARTITION BY n.req)))::int ELSE n.f END AS pf
                FROM n
                CROSS JOIN LATERAL (SELECT r."Id", r."IsActive" FROM {resources} r WHERE r."Seq" = n.node LIMIT 1) r
                LEFT JOIN LATERAL (SELECT TRUE AS yes FROM {resources} ch WHERE ch."ParentId" = r."Id" LIMIT 1) hc ON TRUE
                CROSS JOIN LATERAL (SELECT {GrantChildCount("n.node")}::int AS thr, {grantedHere} AS granted) g
                LEFT JOIN LATERAL (
                    SELECT SUM(c."CutGrants")::int AS cutg
                    FROM {counts} c INNER JOIN live ON live."SubjectId" = c."SubjectId"
                    WHERE c."ResourceSeq" = n.node) agg ON true
                WHERE NOT (g.thr = 1 AND r."IsActive" AND NOT g.granted)
            ),
            opened AS (
                SELECT nodes.*,
                       CASE WHEN NOT active THEN -1 WHEN cut THEN -2 WHEN granted AND has_children THEN 0 WHEN granted THEN -3 WHEN thr > 0 THEN 1 ELSE -4 END AS kind,
                       CASE WHEN NOT active OR cut THEN 0 WHEN granted AND has_children THEN pf WHEN granted THEN 0 WHEN thr > 0 THEN LEAST(f, GREATEST(pf, thr)) ELSE 0 END AS fetch_n
                FROM nodes
            )
            """;

        var first = $"""
            {common}
            SELECT req, node, level, active, granted, has_children, thr, cutg, fetch_n FROM opened ORDER BY req, node;
            """;

        // Two blocks per stream kind and level: one for the streams that have a position (the keyset written
        // as a range the index serves), one for those that have none.
        var blocks = new StringBuilder();
        for (var level = 0; level < levels; level++)
        {
            var seek = $"SUBSTRING({scope}, {SqlOSFgaPageIndex.Offset(level)}, 8) = int8send(s.seq) AND {scope} >= '\\x{level:x2}'::bytea";
            foreach (var positioned in new[] { true, false })
            {
                var position = positioned ? $"\n                            AND {keyset}" : "";
                var streams = positioned ? "s.has_after" : "NOT s.has_after";
                blocks.AppendLine(CultureInfo.InvariantCulture, $"""
                    UNION ALL
                    SELECT 0 AS kind, s.level, s.seq, NULL::varchar AS principal, NULL::varchar AS role, q.rn, q.cnt, TRUE AS granted, {string.Join(", ", order.Select((_, i) => $"q.c{i}"))}
                    FROM streams s
                    CROSS JOIN LATERAL (
                        SELECT row_number() OVER (ORDER BY {outOrder}) AS rn, count(*) OVER () AS cnt, z.*
                        FROM (SELECT {selectCols}
                              FROM {ScopeTable(spec.Table)} AS {a}
                              WHERE {seek}{typeFilter}{predicate}{position}
                              ORDER BY {orderBy}
                              LIMIT s.f) z) q
                    WHERE s.kind = 0 AND s.level = {level} AND {streams}
                    UNION ALL
                    SELECT 1, s.level, s.seq, NULL, NULL, q.rn, q.cnt, q.granted, {string.Join(", ", order.Select((_, i) => $"q.c{i}"))}
                    FROM streams s
                    CROSS JOIN LATERAL (
                        SELECT row_number() OVER (ORDER BY {outOrder}) AS rn, count(*) OVER () AS cnt, z.*
                        FROM (SELECT {selectCols}, {RowTest(options, spec, level, levels)} AS granted
                              FROM {ScopeTable(spec.Table)} AS {a}
                              WHERE {seek}{predicate}{position}
                              ORDER BY {orderBy}
                              LIMIT s.f) z) q
                    WHERE s.kind = 1 AND s.level = {level} AND {streams}
                    """);
            }
        }

        var directColumns = string.Join(", ", order.Select((c, i) => $"d.{QuoteIdentifier(c.Column)} AS c{i}"));
        var directOrderBy = string.Join(", ", order.Select(c => $"d.{QuoteIdentifier(c.Column)}{direction}"));
        var directKeyset = Keyset(order.Select(c => $"d.{QuoteIdentifier(c.Column)}").ToList(), afterValues, spec.Descending);
        var directJoin = spec.PredicateSql is null
            ? ""
            : $"\n                          INNER JOIN {ScopeTable(spec.Table)} AS {a} ON {string.Join(" AND ", spec.Table.KeyColumns.Select(k => $"{a}.{QuoteIdentifier(k)} = d.{QuoteIdentifier(k)}"))}";
        var directBlock = new StringBuilder();
        foreach (var positioned in new[] { true, false })
        {
            var position = positioned ? $"\n                        AND {directKeyset}" : "";
            var streams = positioned ? "s.has_after" : "NOT s.has_after";
            directBlock.AppendLine(CultureInfo.InvariantCulture, $"""
                UNION ALL
                SELECT 2, -1, 0, s.principal, s.role, q.rn, q.cnt, TRUE, {string.Join(", ", order.Select((_, i) => $"q.c{i}"))}
                FROM streams s
                CROSS JOIN LATERAL (
                    SELECT row_number() OVER (ORDER BY {outOrder}) AS rn, count(*) OVER () AS cnt, z.*
                    FROM (SELECT {directColumns}
                          FROM {DirectTable(options, spec.Table)} d{directJoin}
                          WHERE d."SubjectId" = s.principal AND d."RoleId" = s.role AND d."Active"
                            AND (@TypeSeq IS NULL OR d."TypeSeq" = @TypeSeq)
                            AND {Validity("d")}{predicate}{position}
                          ORDER BY {directOrderBy}
                          LIMIT s.f) z) q
                WHERE s.kind = 2 AND {streams}
                """);
        }

        var second = $"""
            {common},
            streams AS (
                SELECT s.sid, s.kind, s.level, s.seq, s.principal, s.role, s.f, s.has_after, {after}
                FROM jsonb_to_recordset(@Streams::jsonb) AS s(sid int, kind int, level int, seq bigint, principal varchar(450), role varchar(450), f int, has_after boolean, {afterTyped})
                UNION ALL
                SELECT -1, kind, level, node, NULL, NULL, fetch_n, has_after, {after}
                FROM opened WHERE kind IN (0, 1)
            )
            SELECT u.* FROM (
                SELECT 0 AS kind, 0 AS level, 0::bigint AS seq, NULL::varchar AS principal, NULL::varchar AS role, 0::bigint AS rn, 0::bigint AS cnt, FALSE AS granted, {string.Join(", ", order.Select((c, i) => $"NULL::{c.StoreType} AS c{i}"))}
                WHERE FALSE
                {blocks}
                {directBlock}
            ) u
            ORDER BY {outOrder};
            """;

        return first + "\n" + second;
    }

    /// <summary>
    /// A keyset comparison the planner seeks on: the first column as a range, the rest nested
    /// (<c>c0 >= a0 AND (c0 > a0 OR (c1 >= a1 AND (c1 > a1 OR …)))</c>); mirrored for a descending order.
    /// </summary>
    private static string Keyset(IReadOnlyList<string> columns, IReadOnlyList<string> values, bool descending)
    {
        var beyond = descending ? "<" : ">";
        var orEqual = descending ? "<=" : ">=";
        string Rest(int i)
        {
            if (i == columns.Count - 1)
            {
                return $"{columns[i]} {beyond} {values[i]}";
            }

            return $"({columns[i]} {orEqual} {values[i]} AND ({columns[i]} {beyond} {values[i]} OR {Rest(i + 1)}))";
        }

        return Rest(0);
    }

    /// <summary>
    /// The row test of a structural stream at <paramref name="level"/>: the row's resource type is the
    /// permission's, and at some level below the stream's node — the row's scope holds the ancestor there only
    /// where access flows down from that level — one of the caller's principals holds a usable grant whose role
    /// carries the permission. Inline, so it is planned once with the statement.
    /// </summary>
    private string RowTest(SqlOSFgaOptions options, SqlOSFgaPageSpec spec, int level, int levels)
    {
        var scope = $"{spec.Alias}.{QuoteIdentifier(SqlOSFgaLineage.ScopeColumn)}";
        var type = spec.Typed ? $"SUBSTRING({scope}, {SqlOSFgaLineage.ScopeTypeOffset}, 4) = @Type AND " : "";
        if (level + 1 >= levels)
        {
            return "FALSE";
        }

        var ancestors = string.Join(", ", Enumerable.Range(level + 1, levels - level - 1).Select(l => $"(SUBSTRING({scope}, {SqlOSFgaPageIndex.Offset(l)}, 8))"));
        return $"""
            ({type}EXISTS (
                        SELECT 1
                        FROM (VALUES {ancestors}) AS lv(anc)
                        INNER JOIN {Qualify(options.Schema, options.TableNames.Resources)} x ON x."Seq" = ('x' || encode(lv.anc, 'hex'))::bit(64)::bigint
                        CROSS JOIN LATERAL {GrantsOn(Qualify(options.Schema, options.TableNames.Grants), "x.\"Id\"")} g
                        WHERE octet_length(lv.anc) = 8 AND lv.anc <> {ZeroAncestor}
                          AND g."SubjectId" = ANY ({LiveArray}) AND g."RoleId" = ANY ({RolesArray}) AND {Validity("g")}))
            """;
    }

    /// <summary>
    /// The grants on one resource, behind an optimization fence (OFFSET 0): the resource index is the only way
    /// in, whatever filters follow. Without it the planner may start from the principals' grants, all of them
    /// for a caller with thousands, and filter the resource afterwards.
    /// </summary>
    private static string GrantsOn(string grants, string resourceId)
        => $"(SELECT g.\"SubjectId\", g.\"RoleId\", g.\"EffectiveFrom\", g.\"EffectiveTo\" FROM {grants} g WHERE g.\"ResourceId\" = {resourceId} OFFSET 0)";

    /// <summary>The live principals and the roles as arrays (one initplan each), for filters that must not become joins.</summary>
    private const string LiveArray = "ARRAY(SELECT live.\"SubjectId\" FROM live)";

    private const string RolesArray = "ARRAY(SELECT roles.\"RoleId\" FROM roles)";
}

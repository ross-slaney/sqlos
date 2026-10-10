using System.Globalization;
using System.Text;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;

namespace SqlOS.Database;

/// <summary>
/// The SHRBAC enforcement artifacts around the row filter (see the SQL Server provider): the caller's live
/// subjects and access roots, the resource lineage with the index per level and the routines and triggers
/// that keep it exact, and <c>fn_ListVisible</c>, <c>fn_VisibleSet</c> and <c>fn_ListFirst</c>.
/// </summary>
internal sealed partial class PostgreSqlDatabaseProvider
{
    private const string MalformedErrorCode = "SQ012";

    /// <summary><c>fn_ActiveSubjects(p_subject_ids)</c>: the caller's live principal set.</summary>
    public string BuildActiveSubjectsFunctionSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = QuoteIdentifier(options.Schema);
        var tables = options.TableNames;
        var subjects = Qualify(options.Schema, tables.Subjects);
        var users = Qualify(options.Schema, tables.Users);
        var serviceAccounts = Qualify(options.Schema, tables.ServiceAccounts);
        var userGroups = Qualify(options.Schema, tables.UserGroups);
        var agents = Qualify(options.Schema, tables.Agents);
        return $"""
            CREATE OR REPLACE FUNCTION {schema}."fn_ActiveSubjects"(
                p_subject_ids text
            )
            RETURNS TABLE("SubjectId" varchar(450))
            LANGUAGE sql
            STABLE
            AS $sqlos$
            SELECT s."Id"
            FROM {subjects} s
            LEFT JOIN {users} u ON s."Id" = u."SubjectId"
            LEFT JOIN {serviceAccounts} sa ON s."Id" = sa."SubjectId"
            LEFT JOIN {userGroups} ug ON s."Id" = ug."SubjectId"
            LEFT JOIN {agents} ag ON s."Id" = ag."SubjectId"
            WHERE s."Id" = ANY (ARRAY(SELECT jsonb_array_elements_text(p_subject_ids::jsonb)))
              AND (s."SubjectTypeId" <> 'user' OR u."IsActive" = TRUE)
              AND (s."SubjectTypeId" <> 'service_account' OR (sa."SubjectId" IS NOT NULL AND (sa."ExpiresAt" IS NULL OR sa."ExpiresAt" > (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'))))
              AND (s."SubjectTypeId" <> 'group' OR ug."IsActive" = TRUE)
              AND (s."SubjectTypeId" <> 'agent' OR ag."SubjectId" IS NOT NULL)
              AND EXISTS (
                  SELECT 1
                  FROM {subjects} caller
                  LEFT JOIN {users} callerUser ON caller."Id" = callerUser."SubjectId"
                  LEFT JOIN {serviceAccounts} callerSa ON caller."Id" = callerSa."SubjectId"
                  LEFT JOIN {userGroups} callerGroup ON caller."Id" = callerGroup."SubjectId"
                  LEFT JOIN {agents} callerAgent ON caller."Id" = callerAgent."SubjectId"
                  WHERE caller."Id" = (p_subject_ids::jsonb ->> 0)
                    AND (caller."SubjectTypeId" <> 'user' OR callerUser."IsActive" = TRUE)
                    AND (caller."SubjectTypeId" <> 'service_account' OR (callerSa."SubjectId" IS NOT NULL AND (callerSa."ExpiresAt" IS NULL OR callerSa."ExpiresAt" > (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'))))
                    AND (caller."SubjectTypeId" <> 'group' OR callerGroup."IsActive" = TRUE)
                    AND (caller."SubjectTypeId" <> 'agent' OR callerAgent."SubjectId" IS NOT NULL)
              )
            $sqlos$;
            """;
    }

    /// <summary><c>fn_AccessRoots</c>: the caller's access roots, their compact keys and levels (see the SQL Server provider).</summary>
    public string BuildAccessRootsFunctionSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = QuoteIdentifier(options.Schema);
        var tables = options.TableNames;
        var resources = Qualify(options.Schema, tables.Resources);
        var grants = Qualify(options.Schema, tables.Grants);
        var rolePermissions = Qualify(options.Schema, tables.RolePermissions);
        return $"""
            CREATE OR REPLACE FUNCTION {schema}."fn_AccessRoots"(
                p_subject_ids text,
                p_permission_id varchar(450)
            )
            RETURNS TABLE("ResourceSeq" bigint, "Depth" smallint)
            LANGUAGE sql
            STABLE
            AS $sqlos$
            -- The caller's current grants first, found by subject, then their resources by primary key. The CTE is
            -- materialized so the planner cannot start from the resource table; it is read as far as the
            -- statement needs (a count up to a cap stops early), so nothing here deduplicates.
            WITH g AS MATERIALIZED (
                SELECT g."ResourceId"
                FROM {grants} g
                WHERE g."SubjectId" = ANY (ARRAY(SELECT live."SubjectId" FROM {schema}."fn_ActiveSubjects"(p_subject_ids) live))
                  AND g."RoleId" = ANY (ARRAY(SELECT rp."RoleId" FROM {rolePermissions} rp WHERE rp."PermissionId" = p_permission_id))
                  AND (g."EffectiveFrom" IS NULL OR g."EffectiveFrom" <= (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'))
                  AND (g."EffectiveTo" IS NULL OR g."EffectiveTo" >= (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'))
            )
            SELECT r."Seq", r."Depth"
            FROM g
            -- Each grant's resource by its key: a lookup per grant, never a pass over the resources.
            CROSS JOIN LATERAL (
                SELECT x."Seq", x."Depth"
                FROM {resources} x
                WHERE x."Id" = g."ResourceId" AND x."IsActive" = TRUE AND x."Depth" IS NOT NULL
                LIMIT 1
            ) r
            $sqlos$;
            """;
    }

    /// <summary>
    /// <c>fn_ListVisible</c>: the caller's visible resources, listed root by root (see the SQL Server
    /// provider). A SQL function of one SELECT, inlined into the statement that uses it; the lateral join keeps
    /// the roots driving, and each level's branch runs only for roots at that level (a one-time filter).
    /// </summary>
    public string BuildListVisibleFunctionSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = QuoteIdentifier(options.Schema);
        var resources = Qualify(options.Schema, options.TableNames.Resources);
        var branches = string.Join("\n    UNION ALL\n", Enumerable.Range(0, SqlOSFgaLineage.Levels(options)).Select(level =>
        {
            var l = level.ToString(CultureInfo.InvariantCulture);
            var ancestor = "r." + QuoteIdentifier(SqlOSFgaLineage.AncestorColumn(level));
            return $"    SELECT r.\"Id\" FROM {resources} r WHERE a.\"Depth\" = {l} AND {ancestor} = a.\"ResourceSeq\" AND {ancestor} IS NOT NULL AND r.\"Reach\" <= {l} AND (p_type_id IS NULL OR r.\"ResourceTypeId\" = p_type_id)";
        }));
        return $"""
            CREATE OR REPLACE FUNCTION {schema}."fn_ListVisible"(
                p_subject_ids text,
                p_permission_id varchar(450),
                p_type_id varchar(450)
            )
            RETURNS TABLE("ResourceId" varchar(450))
            LANGUAGE sql
            STABLE
            AS $sqlos$
            SELECT v."Id"
            FROM {schema}."fn_AccessRoots"(p_subject_ids, p_permission_id) a
            CROSS JOIN LATERAL (
            {branches}
            ) v
            $sqlos$;
            """;
    }

    /// <summary>
    /// <c>fn_VisibleSet</c>: the rows of <c>fn_ListVisible</c>, each once (see the SQL Server provider). PL/pgSQL,
    /// so the planner never inlines it: it sees a set of about ten rows and starts the statement from it.
    /// </summary>
    public string BuildVisibleSetFunctionSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = QuoteIdentifier(options.Schema);
        return $"""
            CREATE OR REPLACE FUNCTION {schema}."fn_VisibleSet"(
                p_subject_ids text,
                p_permission_id varchar(450),
                p_type_id varchar(450)
            )
            RETURNS TABLE("ResourceId" varchar(450))
            LANGUAGE plpgsql
            STABLE
            ROWS 10
            AS $sqlos$
            BEGIN
                RETURN QUERY
                SELECT DISTINCT l."ResourceId" FROM {schema}."fn_ListVisible"(p_subject_ids, p_permission_id, p_type_id) l;
            END
            $sqlos$;
            """;
    }

    /// <summary>
    /// <c>fn_ListFirst</c>: whether the caller sees fewer resources than the table's cap (see the SQL Server
    /// provider). The table's row count is the planner's (<c>pg_class.reltuples</c>; -1, never analyzed, reads as 0).
    /// </summary>
    public string BuildListFirstFunctionSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = QuoteIdentifier(options.Schema);
        var rows = "greatest(coalesce((SELECT t.reltuples FROM pg_class t WHERE t.oid = to_regclass(p_table)), 0), 0)::float8";
        return $"""
            CREATE OR REPLACE FUNCTION {schema}."fn_ListFirst"(
                p_subject_ids text,
                p_permission_id varchar(450),
                p_type_id varchar(450),
                p_table text
            )
            RETURNS TABLE("ListFirst" boolean)
            LANGUAGE sql
            STABLE
            AS $sqlos$
            SELECT v.visible < c.cap
            FROM (SELECT CAST({SqlOSFgaLineage.ListFirstCapSql(rows, "sqrt")} AS bigint) AS cap) c
            CROSS JOIN LATERAL (
                SELECT count(*) AS visible
                FROM (SELECT 1 FROM {schema}."fn_ListVisible"(p_subject_ids, p_permission_id, p_type_id) LIMIT c.cap) t
            ) v
            $sqlos$;
            """;
    }

    /// <summary>The query over <c>fn_ListFirst</c> (see the SQL Server provider); the type id is cast so a null still resolves the function.</summary>
    public string BuildListFirstQuerySql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"SELECT f.\"ListFirst\" AS \"Value\" FROM {QuoteIdentifier(options.Schema)}.\"fn_ListFirst\"(CAST({{0}} AS text), CAST({{1}} AS varchar(450)), CAST({{2}} AS varchar(450)), CAST({{3}} AS text)) AS f";
    }

    /// <summary>The ancestor columns of the configured depth and the index of each level (see the SQL Server provider).</summary>
    public IReadOnlyList<string> BuildEnsureLineageColumnsSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var resources = Qualify(options.Schema, options.TableNames.Resources);
        var columns = new StringBuilder();
        var indexes = new StringBuilder();
        for (var level = 0; level < SqlOSFgaLineage.Levels(options); level++)
        {
            var column = QuoteIdentifier(SqlOSFgaLineage.AncestorColumn(level));
            var index = QuoteIdentifier(SqlOSFgaLineage.AncestorIndexName(options.TableNames.Resources, level));
            columns.AppendLine(CultureInfo.InvariantCulture, $"ALTER TABLE {resources} ADD COLUMN IF NOT EXISTS {column} bigint NULL;");
            indexes.AppendLine(CultureInfo.InvariantCulture, $"CREATE INDEX IF NOT EXISTS {index} ON {resources} ({column}, \"ResourceTypeId\") INCLUDE (\"{SqlOSFgaLineage.ReachColumn}\", \"Id\") WHERE {column} IS NOT NULL;");
        }

        return [columns.ToString(), indexes.ToString()];
    }

    public IReadOnlyList<string> BuildLineageMaintenanceSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var resourcesTable = options.TableNames.Resources;
        var resources = Qualify(options.Schema, resourcesTable);
        var refresh = Qualify(options.Schema, "fn_" + SqlOSFgaLineage.RefreshRoutineName(resourcesTable));
        var rebuild = Qualify(options.Schema, "fn_" + SqlOSFgaLineage.RebuildRoutineName(resourcesTable));
        var onInsert = Qualify(options.Schema, $"fn_{resourcesTable}_LineageOnInsert");
        var onUpdate = Qualify(options.Schema, $"fn_{resourcesTable}_LineageOnUpdate");
        var triggers = SqlOSFgaLineage.TriggerNames(resourcesTable);
        var levels = SqlOSFgaLineage.Levels(options);
        var maxLevel = (levels - 1).ToString(CultureInfo.InvariantCulture);
        var malformed = $"RAISE EXCEPTION 'SqlOS FGA: the change would create a cycle, or place a resource deeper than the configured maximum hierarchy depth of {maxLevel}.' USING ERRCODE = '{MalformedErrorCode}';";
        var affected = "pg_temp.\"SqlOSLineageAffected\"";
        var lockKey = SqlOSFgaLineage.LineageLockKey(options).ToString(CultureInfo.InvariantCulture);
        var lockShared = $"PERFORM pg_advisory_xact_lock_shared({lockKey});\n{IsolationGuard}";
        var lockExclusive = $"PERFORM pg_advisory_xact_lock({lockKey});\n{IsolationGuard}";

        // The lineage of a row (alias `row`, with ParentId, IsActive, Seq) from its parent `p`, where `depth` is
        // the row's new level (NULL when malformed).
        string Lineage(string row, string p, string depth)
        {
            var set = new StringBuilder();
            set.Append(CultureInfo.InvariantCulture, $"\"Depth\" = {depth},\n");
            set.Append(CultureInfo.InvariantCulture, $"    \"Reach\" = CASE WHEN NOT {row}.\"IsActive\" OR {depth} IS NULL THEN NULL WHEN {row}.\"ParentId\" IS NULL THEN 0 WHEN {p}.\"Reach\" IS NULL THEN {depth} ELSE {p}.\"Reach\" END");
            for (var level = 0; level < levels; level++)
            {
                var column = QuoteIdentifier(SqlOSFgaLineage.AncestorColumn(level));
                set.Append(CultureInfo.InvariantCulture, $",\n    {column} = CASE WHEN {depth} = {level} THEN {row}.\"Seq\" WHEN {depth} > {level} THEN {p}.{column} ELSE NULL END");
            }

            return set.ToString();
        }

        string NewDepth(string row, string p)
            => $"CASE WHEN {row}.\"ParentId\" IS NULL THEN 0 WHEN {p}.\"Depth\" IS NULL OR {p}.\"Depth\" >= {maxLevel} THEN NULL ELSE {p}.\"Depth\" + 1 END";

        var nulls = string.Join(", ", new[] { "\"Depth\" = NULL", "\"Reach\" = NULL" }.Concat(Enumerable.Range(0, levels).Select(l => $"{QuoteIdentifier(SqlOSFgaLineage.AncestorColumn(l))} = NULL")));

        // The chains of the given rows (a FROM item aliased `c` with an "Id" column) that can have become
        // malformed, walked up to the depth limit: a chain still climbing past it lies in a cycle or too deep. For inserted rows,
        // which have no descendants, only chains through a new parent, a parent without lineage, or a parent
        // at the deepest level can be malformed.
        string WalkCheck(string changed, string parentIsChanged)
            => $"""
            WITH RECURSIVE walk AS (
                SELECT r."ParentId" AS "CurrentId", 1 AS "Steps"
                FROM {changed}
                INNER JOIN {resources} r ON r."Id" = c."Id"
                LEFT JOIN {resources} p ON p."Id" = r."ParentId"
                WHERE r."ParentId" IS NOT NULL
                  AND (p."Depth" IS NULL OR p."Depth" >= {maxLevel} OR {parentIsChanged})
                UNION ALL
                SELECT r."ParentId", w."Steps" + 1
                FROM walk w
                INNER JOIN {resources} r ON r."Id" = w."CurrentId"
                WHERE r."ParentId" IS NOT NULL AND w."Steps" <= {maxLevel}
            )
            SELECT count(*) INTO v_bad FROM walk WHERE "Steps" > {maxLevel};
            IF v_bad > 0 THEN
                {malformed}
            END IF;
            """;

        // Recomputes the lineage of a set of changed resources and everything beneath them (the update path).
        var refreshFunction = $"""
            CREATE OR REPLACE FUNCTION {refresh}(p_ids varchar[], p_reject boolean)
            RETURNS void
            LANGUAGE plpgsql
            AS $sqlos$
            DECLARE
                v_bad bigint;
                v_rows bigint;
                v_round int := 0;
                v_wave int := 0;
            BEGIN
                -- 1. A changed row whose chain does not reach a root within the depth limit lies in a cycle or
                --    too deep: reject. Every moved row is walked: its new parent may be its own descendant.
                IF p_reject THEN
                    {WalkCheck("unnest(p_ids) AS c(\"Id\")", "TRUE")}
                END IF;

                -- 2. The affected rows: the changed rows and everything beneath them, level by level.
                DROP TABLE IF EXISTS {affected};
                CREATE TEMP TABLE "SqlOSLineageAffected" ("Id" varchar(450) PRIMARY KEY, "Round" int NOT NULL, "Wave" int NULL) ON COMMIT DROP;
                INSERT INTO {affected} ("Id", "Round") SELECT DISTINCT c."Id", 0 FROM unnest(p_ids) AS c("Id");
                LOOP
                    INSERT INTO {affected} ("Id", "Round")
                    SELECT r."Id", v_round + 1
                    FROM {resources} r
                    INNER JOIN {affected} a ON a."Id" = r."ParentId"
                    WHERE a."Round" = v_round
                      AND NOT EXISTS (SELECT 1 FROM {affected} x WHERE x."Id" = r."Id");
                    GET DIAGNOSTICS v_rows = ROW_COUNT;
                    EXIT WHEN v_rows = 0 OR v_round >= {maxLevel};
                    v_round := v_round + 1;
                END LOOP;
                ANALYZE {affected};

                -- 3. Descend: the rows whose parent is outside the set first, then their children, wave by wave.
                UPDATE {affected} a SET "Wave" = 0
                FROM {resources} r
                WHERE r."Id" = a."Id"
                  AND (r."ParentId" IS NULL OR NOT EXISTS (SELECT 1 FROM {affected} p WHERE p."Id" = r."ParentId"));

                LOOP
                    IF p_reject THEN
                        SELECT count(*) INTO v_bad
                        FROM {affected} a
                        INNER JOIN {resources} r ON r."Id" = a."Id"
                        INNER JOIN {resources} p ON p."Id" = r."ParentId"
                        WHERE a."Wave" = v_wave AND p."Depth" = {maxLevel};
                        IF v_bad > 0 THEN
                            {malformed}
                        END IF;
                    END IF;

                    UPDATE {resources} r SET
                        {Lineage("tgt", "p", "nd.\"Depth\"")}
                    FROM (
                        SELECT x."Id", x."ParentId", x."IsActive", x."Seq"
                        FROM {resources} x
                        INNER JOIN {affected} a ON a."Id" = x."Id"
                        WHERE a."Wave" = v_wave
                    ) tgt
                    LEFT JOIN {resources} p ON p."Id" = tgt."ParentId"
                    CROSS JOIN LATERAL (SELECT {NewDepth("tgt", "p")} AS "Depth") nd
                    WHERE r."Id" = tgt."Id";

                    UPDATE {affected} c SET "Wave" = v_wave + 1
                    FROM {resources} r
                    INNER JOIN {affected} p ON p."Id" = r."ParentId"
                    WHERE r."Id" = c."Id" AND c."Wave" IS NULL AND p."Wave" = v_wave;
                    GET DIAGNOSTICS v_rows = ROW_COUNT;
                    EXIT WHEN v_rows = 0 OR v_wave > {maxLevel};
                    v_wave := v_wave + 1;
                END LOOP;

                -- 4. A row no wave reached has no path to a root within the set: it is malformed.
                UPDATE {resources} r SET {nulls}
                FROM {affected} a
                WHERE r."Id" = a."Id" AND a."Wave" IS NULL;
                DROP TABLE {affected};
            END
            $sqlos$;
            """;

        // The whole table: the internal nodes (every resource that is some row's parent; few next to the
        // leaves) get their lineage level by level in a temp table, then the nodes take it from there and the
        // leaves from their parent node. The function body is one transaction: a failed rebuild leaves the
        // previous lineage.
        var nodes = "pg_temp.\"SqlOSLineageNodes\"";
        var nodeColumns = new[] { "\"Depth\"", "\"Reach\"" }.Concat(Enumerable.Range(0, levels).Select(l => QuoteIdentifier(SqlOSFgaLineage.AncestorColumn(l)))).ToList();
        var rebuildFunction = $"""
            CREATE OR REPLACE FUNCTION {rebuild}()
            RETURNS void
            LANGUAGE plpgsql
            AS $sqlos$
            DECLARE
                v_rows bigint;
                v_level int;
            BEGIN
                {lockExclusive}
                -- 1. The internal nodes, level by level.
                DROP TABLE IF EXISTS {nodes};
                CREATE TEMP TABLE "SqlOSLineageNodes" AS
                SELECT r."Id", r."ParentId", r."IsActive", r."Seq",
                    NULL::smallint AS "Depth", NULL::smallint AS "Reach",
                    {string.Join(", ", Enumerable.Range(0, levels).Select(l => $"NULL::bigint AS {QuoteIdentifier(SqlOSFgaLineage.AncestorColumn(l))}"))}
                FROM {resources} r
                WHERE EXISTS (SELECT 1 FROM {resources} c WHERE c."ParentId" = r."Id");
                CREATE UNIQUE INDEX ON {nodes} ("Id");
                ANALYZE {nodes};

                UPDATE {nodes} n SET
                    "Depth" = 0,
                    "Reach" = CASE WHEN n."IsActive" THEN 0 END,
                    {QuoteIdentifier(SqlOSFgaLineage.AncestorColumn(0))} = n."Seq"
                WHERE n."ParentId" IS NULL;
                FOR v_level IN 1..{maxLevel} LOOP
                    UPDATE {nodes} n SET
                        {Lineage("n", "p", "v_level")}
                    FROM {nodes} p
                    WHERE p."Id" = n."ParentId" AND p."Depth" = v_level - 1;
                    GET DIAGNOSTICS v_rows = ROW_COUNT;
                    EXIT WHEN v_rows = 0;
                END LOOP;

                -- 2. The nodes take their lineage; the leaves take theirs from their parent node, or stand
                --    alone as roots.
                UPDATE {resources} r SET
                    {string.Join(",\n        ", nodeColumns.Select(c => $"{c} = n.{c}"))}
                FROM {nodes} n
                WHERE n."Id" = r."Id";

                UPDATE {resources} r SET
                    {Lineage("r", "p", "nd.\"Depth\"")}
                FROM {nodes} p
                CROSS JOIN LATERAL (SELECT CASE WHEN p."Depth" IS NULL OR p."Depth" >= {maxLevel} THEN NULL ELSE p."Depth" + 1 END AS "Depth") nd
                WHERE p."Id" = r."ParentId"
                  AND NOT EXISTS (SELECT 1 FROM {nodes} n WHERE n."Id" = r."Id");

                UPDATE {resources} r SET
                    {Lineage("r", "p", "0")}
                FROM (SELECT NULL::smallint AS "Reach", {string.Join(", ", Enumerable.Range(0, levels).Select(l => $"NULL::bigint AS {QuoteIdentifier(SqlOSFgaLineage.AncestorColumn(l))}"))}) p
                WHERE r."ParentId" IS NULL
                  AND NOT EXISTS (SELECT 1 FROM {nodes} n WHERE n."Id" = r."Id");

                DROP TABLE {nodes};
                UPDATE {Qualify(options.Schema, "SqlOSFgaSchema")} SET "LineageBuilt" = true;
            END
            $sqlos$;
            """;

        // Inserted rows have no descendants yet, so the insert path needs no temp table: the new rows whose
        // parent is not new come first, then wave by wave the new rows whose parent was just computed.
        var insertFunction = $"""
            CREATE OR REPLACE FUNCTION {onInsert}()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $sqlos$
            DECLARE
                v_bad bigint;
                v_wave varchar[];
                v_next varchar[];
            BEGIN
                {lockShared}
                {WalkCheck("new_rows AS c", "EXISTS (SELECT 1 FROM new_rows x WHERE x.\"Id\" = r.\"ParentId\")")}

                IF EXISTS (
                    SELECT 1 FROM new_rows n INNER JOIN {resources} p ON p."Id" = n."ParentId"
                    WHERE p."Depth" = {maxLevel} AND NOT EXISTS (SELECT 1 FROM new_rows x WHERE x."Id" = n."ParentId")) THEN
                    {malformed}
                END IF;

                WITH computed AS (
                    UPDATE {resources} r SET
                        {Lineage("n", "p", "nd.\"Depth\"")}
                    FROM new_rows n
                    LEFT JOIN {resources} p ON p."Id" = n."ParentId"
                    CROSS JOIN LATERAL (SELECT {NewDepth("n", "p")} AS "Depth") nd
                    WHERE r."Id" = n."Id"
                      AND (n."ParentId" IS NULL OR NOT EXISTS (SELECT 1 FROM new_rows x WHERE x."Id" = n."ParentId"))
                    RETURNING r."Id"
                )
                SELECT array_agg("Id") INTO v_wave FROM computed;

                WHILE v_wave IS NOT NULL AND cardinality(v_wave) > 0 LOOP
                    IF EXISTS (
                        SELECT 1 FROM new_rows n INNER JOIN {resources} p ON p."Id" = n."ParentId"
                        WHERE n."ParentId" = ANY (v_wave) AND p."Depth" = {maxLevel}) THEN
                        {malformed}
                    END IF;

                    WITH computed AS (
                        UPDATE {resources} r SET
                            {Lineage("n", "p", "nd.\"Depth\"")}
                        FROM new_rows n
                        INNER JOIN {resources} p ON p."Id" = n."ParentId"
                        CROSS JOIN LATERAL (SELECT {NewDepth("n", "p")} AS "Depth") nd
                        WHERE r."Id" = n."Id" AND n."ParentId" = ANY (v_wave)
                        RETURNING r."Id"
                    )
                    SELECT array_agg("Id") INTO v_next FROM computed;
                    v_wave := v_next;
                END LOOP;
                RETURN NULL;
            END
            $sqlos$;
            """;

        // A deleted row needs no trigger: a resource with children cannot be deleted, and a leaf is in no
        // other row's lineage.
        var updateFunction = $"""
            CREATE OR REPLACE FUNCTION {onUpdate}()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $sqlos$
            DECLARE
                v_ids varchar[];
            BEGIN
                -- A statement trigger with transition tables cannot name columns, so this fires for every
                -- update of the table, the lineage's own included (and for statements that touched no row);
                -- only a changed parent or activity matters.
                -- Changed rows are found with EXCEPT, a hashed set operation: a join of the two transition
                -- tables has no index or statistics to plan by, and a bulk update would pay for it quadratically.
                SELECT array_agg("Id") INTO v_ids
                FROM (
                    SELECT "Id", "ParentId", "IsActive" FROM new_rows
                    EXCEPT
                    SELECT "Id", "ParentId", "IsActive" FROM old_rows
                ) changed;
                IF v_ids IS NULL THEN
                    RETURN NULL;
                END IF;
                {lockExclusive}
                PERFORM {refresh}(v_ids, true);
                RETURN NULL;
            END
            $sqlos$;
            """;

        var resourceTriggers = $"""
            CREATE OR REPLACE TRIGGER {QuoteIdentifier(triggers[0])}
                AFTER INSERT ON {resources}
                REFERENCING NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION {onInsert}();
            CREATE OR REPLACE TRIGGER {QuoteIdentifier(triggers[1])}
                AFTER UPDATE ON {resources}
                REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION {onUpdate}();
            """;

        return [refreshFunction, rebuildFunction, insertFunction, updateFunction, resourceTriggers];
    }

    public string BuildLineageNeedsBuildSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM {Qualify(options.Schema, "SqlOSFgaSchema")} WHERE "LineageBuilt")
            THEN 0 ELSE 1 END AS "Value"
            """;
    }

    public string BuildLineageRebuildSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"SELECT {Qualify(options.Schema, "fn_" + SqlOSFgaLineage.RebuildRoutineName(options.TableNames.Resources))}();";
    }

    public string BuildSelectRoutinesHashSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = SqlLiteral(options.Schema);
        var resourcesTable = options.TableNames.Resources;
        string Routine(string name)
            => $"EXISTS (SELECT 1 FROM pg_proc p INNER JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = '{schema}' AND p.proname = '{SqlLiteral(name)}')";
        string Trigger(string name)
            => $"EXISTS (SELECT 1 FROM pg_trigger t INNER JOIN pg_class c ON c.oid = t.tgrelid INNER JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = '{schema}' AND c.relname = '{SqlLiteral(resourcesTable)}' AND t.tgname = '{SqlLiteral(name)}' AND NOT t.tgisinternal)";
        string Column(string column)
            => $"EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{schema}' AND table_name = '{SqlLiteral(resourcesTable)}' AND column_name = '{SqlLiteral(column)}')";

        var indexes = SqlOSFgaLineage.AncestorIndexNames(options);
        var conditions = new List<string>
        {
            Routine("fn_ActiveSubjects"),
            Routine("fn_AccessRoots"),
            Routine("fn_ListVisible"),
            Routine("fn_VisibleSet"),
            Routine("fn_ListFirst"),
            Routine("fn_IsResourceAccessible"),
            Routine("fn_" + SqlOSFgaLineage.RefreshRoutineName(resourcesTable)),
            Routine("fn_" + SqlOSFgaLineage.RebuildRoutineName(resourcesTable)),
            Column(SqlOSFgaLineage.AncestorColumn(SqlOSFgaLineage.MaxLevel(options))),
        };
        conditions.AddRange(SqlOSFgaLineage.TriggerNames(resourcesTable).Select(Trigger));
        conditions.Add(
            $"(SELECT count(*) FROM pg_indexes WHERE schemaname = '{schema}' AND tablename = '{SqlLiteral(resourcesTable)}' AND indexname IN ({string.Join(", ", indexes.Select(n => $"'{SqlLiteral(n)}'"))})) = {indexes.Count.ToString(CultureInfo.InvariantCulture)}");

        return $"""
            SELECT "RoutinesHash"
            FROM {Qualify(options.Schema, "SqlOSFgaSchema")}
            WHERE {string.Join("\n  AND ", conditions)}
            LIMIT 1
            """;
    }

    public string BuildStoreRoutinesHashSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"UPDATE {Qualify(options.Schema, "SqlOSFgaSchema")} SET \"RoutinesHash\" = @RoutinesHash";
    }

    /// <summary>
    /// A transaction in REPEATABLE READ keeps reading the snapshot it started with even after waiting for the
    /// lineage lock, so it could compute from state another transaction has since changed. READ COMMITTED (the
    /// default) reads what was committed when each statement starts, and SERIALIZABLE aborts the conflicting
    /// transaction itself; REPEATABLE READ is refused rather than allowed to write a lineage that may be stale.
    /// </summary>
    private const string IsolationGuard = """
        IF current_setting('transaction_isolation') = 'repeatable read' THEN
            RAISE EXCEPTION 'SqlOS FGA: change resources in a READ COMMITTED or SERIALIZABLE transaction, not REPEATABLE READ: a repeatable read can miss a concurrent change to the tree.' USING ERRCODE = 'SQ014';
        END IF;
        """;

    private static string SqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}

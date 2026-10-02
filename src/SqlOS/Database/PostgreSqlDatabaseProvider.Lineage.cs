using System.Globalization;
using System.Text;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;

namespace SqlOS.Database;

/// <summary>
/// The SHRBAC enforcement artifacts around the row filter (see the SQL Server provider): the caller's live
/// subjects and access roots, the resource lineage with the routines and triggers that keep it exact, and the
/// scope columns that copy it onto application tables.
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
                p_permission_id varchar(128)
            )
            RETURNS TABLE("ResourceSeq" bigint, "Depth" smallint)
            LANGUAGE sql
            STABLE
            AS $sqlos$
            -- The caller's current grants first, found by subject, then their resources by primary key. The CTE is
            -- materialized so the planner cannot start from the resource table.
            WITH g AS MATERIALIZED (
                SELECT g."ResourceId"
                FROM {grants} g
                WHERE g."SubjectId" = ANY (ARRAY(SELECT live."SubjectId" FROM {schema}."fn_ActiveSubjects"(p_subject_ids) live))
                  AND g."RoleId" = ANY (ARRAY(SELECT rp."RoleId" FROM {rolePermissions} rp WHERE rp."PermissionId" = p_permission_id))
                  AND (g."EffectiveFrom" IS NULL OR g."EffectiveFrom" <= (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'))
                  AND (g."EffectiveTo" IS NULL OR g."EffectiveTo" >= (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'))
            )
            SELECT DISTINCT r."Seq", r."Depth"
            FROM g
            INNER JOIN {resources} r ON r."Id" = g."ResourceId"
            WHERE r."IsActive" = TRUE AND r."Depth" IS NOT NULL
            $sqlos$;
            """;
    }

    public string BuildActiveSubjectsQuerySql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"SELECT live.\"SubjectId\" FROM {QuoteIdentifier(options.Schema)}.\"fn_ActiveSubjects\"({{0}}) AS live";
    }

    public string BuildAccessRootsQuerySql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"SELECT a.\"ResourceSeq\", a.\"Depth\" FROM {QuoteIdentifier(options.Schema)}.\"fn_AccessRoots\"({{0}}, {{1}}) AS a";
    }

    public IReadOnlyList<string> BuildEnsureLineageColumnsSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var resources = Qualify(options.Schema, options.TableNames.Resources);
        var sql = new StringBuilder();
        for (var level = 0; level < SqlOSFgaLineage.Levels(options); level++)
        {
            var column = QuoteIdentifier(SqlOSFgaLineage.AncestorColumn(level));
            var index = QuoteIdentifier(SqlOSFgaLineage.AncestorIndexName(options.TableNames.Resources, level));
            sql.AppendLine(CultureInfo.InvariantCulture, $"ALTER TABLE {resources} ADD COLUMN IF NOT EXISTS {column} bigint NULL;");
            sql.AppendLine(CultureInfo.InvariantCulture, $"CREATE INDEX IF NOT EXISTS {index} ON {resources} ({column}) INCLUDE (\"{SqlOSFgaLineage.ReachColumn}\") WHERE {column} IS NOT NULL;");
        }

        return [sql.ToString()];
    }

    public IReadOnlyList<string> BuildLineageMaintenanceSql(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> scopeTables)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopeTables);
        var resourcesTable = options.TableNames.Resources;
        var resources = Qualify(options.Schema, resourcesTable);
        var resourceTypes = Qualify(options.Schema, options.TableNames.ResourceTypes);
        var refresh = Qualify(options.Schema, "fn_" + SqlOSFgaLineage.RefreshRoutineName(resourcesTable));
        var rebuild = Qualify(options.Schema, "fn_" + SqlOSFgaLineage.RebuildRoutineName(resourcesTable));
        var onInsert = Qualify(options.Schema, $"fn_{resourcesTable}_LineageOnInsert");
        var onUpdate = Qualify(options.Schema, $"fn_{resourcesTable}_LineageOnUpdate");
        var onDelete = Qualify(options.Schema, $"fn_{resourcesTable}_LineageOnDelete");
        var triggers = SqlOSFgaLineage.TriggerNames(resourcesTable);
        var levels = SqlOSFgaLineage.Levels(options);
        var maxLevel = (levels - 1).ToString(CultureInfo.InvariantCulture);
        var malformed = $"RAISE EXCEPTION 'SqlOS FGA: the change would create a cycle, or place a resource deeper than the configured maximum hierarchy depth of {maxLevel}.' USING ERRCODE = '{MalformedErrorCode}';";
        var affected = "pg_temp.\"SqlOSLineageAffected\"";

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

        // Copies the lineage of the resources whose ids `source` yields (a relation with an "Id" column) onto
        // their rows in each scope table.
        string Propagate(string source)
        {
            var sql = new StringBuilder();
            foreach (var table in scopeTables)
            {
                sql.AppendLine(CultureInfo.InvariantCulture, $"""
                    UPDATE {ScopeTable(table)} t SET {ScopeAssignment(levels, "r", "rt")}
                    FROM {source} s
                    INNER JOIN {resources} r ON r."Id" = s."Id"
                    INNER JOIN {resourceTypes} rt ON rt."Id" = r."ResourceTypeId"
                    WHERE t.{QuoteIdentifier(table.ResourceIdColumn)} = s."Id";
                    """);
            }

            return sql.ToString();
        }

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

                -- 5. The scope columns of the affected rows' application rows.
                {Propagate(affected)}
                DROP TABLE {affected};
            END
            $sqlos$;
            """;

        // The whole table: the internal nodes (every resource that is some row's parent; few next to the
        // leaves) get their lineage level by level in a temp table, then the nodes take it from there, the
        // leaves from their parent node, and the application rows from their resource. The function body is
        // one transaction: a failed rebuild leaves the previous lineage.
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

                -- 3. The scope columns of every application row.
                {string.Concat(scopeTables.Select(t => BuildScopeFillSql(options, t) + ";\n"))}
                DROP TABLE {nodes};
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

                {Propagate("new_rows")}
                RETURN NULL;
            END
            $sqlos$;
            """;

        var typeChanges = new StringBuilder();
        foreach (var table in scopeTables)
        {
            typeChanges.AppendLine(CultureInfo.InvariantCulture, $"""
                UPDATE {ScopeTable(table)} t SET {ScopeAssignment(levels, "r", "rt")}
                FROM (SELECT "Id", "ResourceTypeId" FROM new_rows EXCEPT SELECT "Id", "ResourceTypeId" FROM old_rows) n
                INNER JOIN {resources} r ON r."Id" = n."Id"
                INNER JOIN {resourceTypes} rt ON rt."Id" = r."ResourceTypeId"
                WHERE t.{QuoteIdentifier(table.ResourceIdColumn)} = n."Id";
                """);
        }

        var updateFunction = $"""
            CREATE OR REPLACE FUNCTION {onUpdate}()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $sqlos$
            DECLARE
                v_ids varchar[];
                v_types boolean;
            BEGIN
                -- A statement trigger with transition tables cannot name columns, so this fires for every
                -- update of the table, the lineage's own included (and for statements that touched no row);
                -- only a changed parent, activity, or type matters.
                -- Changed rows are found with EXCEPT, a hashed set operation: a join of the two transition
                -- tables has no index or statistics to plan by, and a bulk update would pay for it quadratically.
                SELECT array_agg("Id") INTO v_ids
                FROM (
                    SELECT "Id", "ParentId", "IsActive" FROM new_rows
                    EXCEPT
                    SELECT "Id", "ParentId", "IsActive" FROM old_rows
                ) changed;
                IF v_ids IS NOT NULL THEN
                    PERFORM {refresh}(v_ids, true);
                END IF;
                SELECT EXISTS (
                    SELECT 1 FROM (
                        SELECT "Id", "ResourceTypeId" FROM new_rows
                        EXCEPT
                        SELECT "Id", "ResourceTypeId" FROM old_rows
                    ) retyped) INTO v_types;
                IF v_types THEN
                    {(typeChanges.Length == 0 ? "NULL;" : typeChanges.ToString())}
                END IF;
                RETURN NULL;
            END
            $sqlos$;
            """;

        var clears = new StringBuilder();
        foreach (var table in scopeTables)
        {
            clears.AppendLine(CultureInfo.InvariantCulture, $"""
                UPDATE {ScopeTable(table)} t SET {QuoteIdentifier(SqlOSFgaLineage.ScopeColumn)} = NULL
                FROM old_rows o
                WHERE t.{QuoteIdentifier(table.ResourceIdColumn)} = o."Id";
                """);
        }

        var deleteFunction = $"""
            CREATE OR REPLACE FUNCTION {onDelete}()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $sqlos$
            BEGIN
                {clears}
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
            CREATE OR REPLACE TRIGGER {QuoteIdentifier(triggers[2])}
                AFTER DELETE ON {resources}
                REFERENCING OLD TABLE AS old_rows
                FOR EACH STATEMENT EXECUTE FUNCTION {onDelete}();
            """;

        var batches = new List<string> { refreshFunction, rebuildFunction, insertFunction, updateFunction, deleteFunction, resourceTriggers };
        foreach (var table in scopeTables)
        {
            batches.Add(ScopeTriggers(options, table, levels));
        }

        return batches;
    }

    /// <summary>
    /// The trigger functions and the two triggers on an application table (see the SQL Server provider). The
    /// update trigger fires for every update of the table (a statement trigger with transition tables cannot
    /// name columns) and copies the lineage only onto rows whose resource id changed.
    /// </summary>
    private string ScopeTriggers(SqlOSFgaOptions options, SqlOSFgaScopeTable table, int levels)
    {
        var resources = Qualify(options.Schema, options.TableNames.Resources);
        var resourceTypes = Qualify(options.Schema, options.TableNames.ResourceTypes);
        var onInsert = Qualify(options.Schema, ScopeFunctionName(table, "Insert"));
        var onUpdate = Qualify(options.Schema, ScopeFunctionName(table, "Update"));
        var triggers = SqlOSFgaLineage.ScopeTriggerNames(table.Table);
        var resourceId = QuoteIdentifier(table.ResourceIdColumn);
        var keys = string.Join(" AND ", table.KeyColumns.Select(k => $"t.{QuoteIdentifier(k)} = n.{QuoteIdentifier(k)}"));
        var keyList = string.Join(", ", table.KeyColumns.Select(QuoteIdentifier));
        return $"""
            CREATE OR REPLACE FUNCTION {onInsert}()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $sqlos$
            BEGIN
                UPDATE {ScopeTable(table)} t SET {ScopeAssignment(levels, "r", "rt")}
                FROM new_rows n
                LEFT JOIN {resources} r ON r."Id" = n.{resourceId}
                LEFT JOIN {resourceTypes} rt ON rt."Id" = r."ResourceTypeId"
                WHERE {keys};
                RETURN NULL;
            END
            $sqlos$;
            CREATE OR REPLACE FUNCTION {onUpdate}()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $sqlos$
            BEGIN
                -- Statement triggers fire for every update of the table, this function's own included and
                -- statements that touched no row: leave unless some row's resource id changed. The changed
                -- rows come from EXCEPT, a hashed set operation, never from a join of the transition tables.
                IF NOT EXISTS (
                    SELECT 1 FROM (
                        SELECT {keyList}, {resourceId} FROM new_rows
                        EXCEPT
                        SELECT {keyList}, {resourceId} FROM old_rows
                    ) changed) THEN
                    RETURN NULL;
                END IF;

                UPDATE {ScopeTable(table)} t SET {ScopeAssignment(levels, "r", "rt")}
                FROM (
                    SELECT {keyList}, {resourceId} FROM new_rows
                    EXCEPT
                    SELECT {keyList}, {resourceId} FROM old_rows
                ) n
                LEFT JOIN {resources} r ON r."Id" = n.{resourceId}
                LEFT JOIN {resourceTypes} rt ON rt."Id" = r."ResourceTypeId"
                WHERE {keys};
                RETURN NULL;
            END
            $sqlos$;
            CREATE OR REPLACE TRIGGER {QuoteIdentifier(triggers[0])}
                AFTER INSERT ON {ScopeTable(table)}
                REFERENCING NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION {onInsert}();
            CREATE OR REPLACE TRIGGER {QuoteIdentifier(triggers[1])}
                AFTER UPDATE ON {ScopeTable(table)}
                REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION {onUpdate}();
            """;
    }

    public string BuildLineageNeedsBuildSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM {Qualify(options.Schema, options.TableNames.Resources)}
                WHERE "ParentId" IS NULL AND "Depth" IS NULL)
            THEN 1 ELSE 0 END AS "Value"
            """;
    }

    public string BuildLineageRebuildSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"SELECT {Qualify(options.Schema, "fn_" + SqlOSFgaLineage.RebuildRoutineName(options.TableNames.Resources))}();";
    }

    public string BuildScopeFillSql(SqlOSFgaOptions options, SqlOSFgaScopeTable table)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(table);
        return $"""
            UPDATE {ScopeTable(table)} t SET {ScopeAssignment(SqlOSFgaLineage.Levels(options), "r", "rt")}
            FROM {Qualify(options.Schema, options.TableNames.Resources)} r
            INNER JOIN {Qualify(options.Schema, options.TableNames.ResourceTypes)} rt ON rt."Id" = r."ResourceTypeId"
            WHERE t.{QuoteIdentifier(table.ResourceIdColumn)} = r."Id"
            """;
    }

    public string BuildSelectRoutinesHashSql(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> scopeTables)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopeTables);
        var schema = SqlLiteral(options.Schema);
        var resourcesTable = options.TableNames.Resources;
        string Routine(string name)
            => $"EXISTS (SELECT 1 FROM pg_proc p INNER JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = '{schema}' AND p.proname = '{SqlLiteral(name)}')";
        string Trigger(string? tableSchema, string table, string name)
            => $"EXISTS (SELECT 1 FROM pg_trigger t INNER JOIN pg_class c ON c.oid = t.tgrelid INNER JOIN pg_namespace n ON n.oid = c.relnamespace WHERE "
               + (tableSchema is null ? "" : $"n.nspname = '{SqlLiteral(tableSchema)}' AND ")
               + $"c.relname = '{SqlLiteral(table)}' AND t.tgname = '{SqlLiteral(name)}' AND NOT t.tgisinternal)";
        string Column(string column)
            => $"EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{schema}' AND table_name = '{SqlLiteral(resourcesTable)}' AND column_name = '{SqlLiteral(column)}')";

        var conditions = new List<string>
        {
            Routine("fn_ActiveSubjects"),
            Routine("fn_AccessRoots"),
            Routine("fn_IsResourceAccessible"),
            Routine("fn_" + SqlOSFgaLineage.RefreshRoutineName(resourcesTable)),
            Routine("fn_" + SqlOSFgaLineage.RebuildRoutineName(resourcesTable)),
            Column(SqlOSFgaLineage.AncestorColumn(SqlOSFgaLineage.MaxLevel(options))),
        };
        conditions.AddRange(SqlOSFgaLineage.TriggerNames(resourcesTable).Select(t => Trigger(options.Schema, resourcesTable, t)));
        foreach (var table in scopeTables)
        {
            conditions.Add(Routine(ScopeFunctionName(table, "Insert")));
            conditions.Add(Routine(ScopeFunctionName(table, "Update")));
            conditions.AddRange(SqlOSFgaLineage.ScopeTriggerNames(table.Table).Select(t => Trigger(table.Schema, table.Table, t)));
            conditions.Add(
                $"EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = {ScopeSchemaLiteral(table)} AND tablename = '{SqlLiteral(table.Table)}' AND indexname = '{SqlLiteral(SqlOSFgaLineage.ScopeIndexName(table.Table, 0, null))}')");
        }

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

    private static string ScopeFunctionName(SqlOSFgaScopeTable table, string @event)
        => $"fn_{SqlOSFgaLineage.ScopePrefix}Scope_{(table.Schema is null ? "" : table.Schema + "_")}{table.Table}_{@event}";

    private string ScopeTable(SqlOSFgaScopeTable table)
        => table.Schema is null ? QuoteIdentifier(table.Table) : Qualify(table.Schema, table.Table);

    /// <summary>
    /// The scope value of a row from its resource row <paramref name="r"/> (which may be NULL: the row then has
    /// no scope and nobody sees it) and the resource's type row <paramref name="rt"/>: the type's compact key,
    /// then the ancestor at each level where access flows down from that level, NULL elsewhere.
    /// </summary>
    private static string ScopeValue(int levels, string r, string rt)
    {
        var elements = new List<string> { $"{rt}.\"{SqlOSFgaLineage.SeqColumn}\"::bigint" };
        for (var level = 0; level < levels; level++)
        {
            elements.Add($"CASE WHEN {r}.\"{SqlOSFgaLineage.ReachColumn}\" <= {level} THEN {r}.\"{SqlOSFgaLineage.AncestorColumn(level)}\" END");
        }

        return $"CASE WHEN {r}.\"Id\" IS NULL THEN NULL ELSE ARRAY[{string.Join(", ", elements)}]::bigint[] END";
    }

    private string ScopeAssignment(int levels, string r, string rt)
        => $"{QuoteIdentifier(SqlOSFgaLineage.ScopeColumn)} = {ScopeValue(levels, r, rt)}";

    private static string ScopeSchemaLiteral(SqlOSFgaScopeTable table)
        => table.Schema is null ? "current_schema()" : $"'{SqlLiteral(table.Schema)}'";

    /// <summary>
    /// The indexes of one application table, per level: an expression index on the level's element of the
    /// scope array followed by the primary key, and one more per order the application declared, each
    /// filtered to the rows that have an ancestor at that level. Indexes of orders no longer declared are
    /// dropped. Also extended statistics on the type element, analyzed at once. Idempotent.
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
            var scope = QuoteIdentifier(SqlOSFgaLineage.ScopeColumn);
            var key = string.Join(", ", table.KeyColumns.Select(QuoteIdentifier));
            var sql = new StringBuilder();
            sql.AppendLine(CultureInfo.InvariantCulture, $"""
                DO $sqlos$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = {ScopeSchemaLiteral(table)} AND table_name = '{SqlLiteral(table.Table)}' AND column_name = '{SqlOSFgaLineage.ScopeColumn}') THEN
                        RAISE EXCEPTION 'SqlOS FGA: {SqlLiteral(target)} has no {SqlOSFgaLineage.ScopeColumn} column. Add and apply the EF Core migration that carries it before starting SqlOS.';
                    END IF;
                END
                $sqlos$;
                """);
            var wanted = new List<string>();
            for (var level = 0; level < levels; level++)
            {
                var element = $"{scope}[{SqlOSFgaLineage.ScopeAncestorElement(level)}]";
                foreach (var (name, columns) in new[] { (SqlOSFgaLineage.ScopeIndexName(table.Table, level, null), key) }
                    .Concat(table.Orders.Select(o => (SqlOSFgaLineage.ScopeIndexName(table.Table, level, o.Suffix), string.Join(", ", o.Columns.Select(QuoteIdentifier))))))
                {
                    wanted.Add(name);
                    sql.AppendLine(CultureInfo.InvariantCulture, $"""
                        CREATE INDEX IF NOT EXISTS {QuoteIdentifier(name)} ON {target} (({element}), {columns}) WHERE {element} IS NOT NULL;
                        """);
                }
            }

            // Statistics on the type element: without them the planner guesses the type test is selective and
            // sorts the caller's whole scope instead of walking the level's index in order. ANALYZE fills them
            // (and the expression indexes') at once.
            sql.AppendLine(CultureInfo.InvariantCulture, $"""
                CREATE STATISTICS IF NOT EXISTS {QuoteIdentifier(SqlOSFgaLineage.ScopeTypeStatisticsName(table.Table))} ON (({scope}[{SqlOSFgaLineage.ScopeTypeElement}])) FROM {target};
                ANALYZE {target};
                """);
            var wantedList = string.Join(", ", wanted.Select(w => $"'{SqlLiteral(w)}'"));
            sql.AppendLine(CultureInfo.InvariantCulture, $"""
                DO $sqlos$
                DECLARE
                    stale record;
                BEGIN
                    FOR stale IN
                        SELECT schemaname, indexname FROM pg_indexes
                        WHERE schemaname = {ScopeSchemaLiteral(table)} AND tablename = '{SqlLiteral(table.Table)}'
                          AND indexname LIKE '{SqlLiteral(SqlOSFgaLineage.ScopeIndexPrefix(table.Table)).Replace("_", "\\_", StringComparison.Ordinal)}%'
                          AND indexname NOT IN ({wantedList})
                    LOOP
                        EXECUTE format('DROP INDEX %I.%I', stale.schemaname, stale.indexname);
                    END LOOP;
                END
                $sqlos$;
                """);
            batches.Add(sql.ToString());
        }

        return batches;
    }

    private static string SqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}

using System.Globalization;
using System.Text;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Paging;

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
                p_permission_id varchar(450)
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
            sql.AppendLine(CultureInfo.InvariantCulture, $"ALTER TABLE {resources} ADD COLUMN IF NOT EXISTS {column} bigint NULL;");
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
                -- 6. The grant counts of the principals holding grants on the affected resources (their chains
                --    changed), and the activity and type of their rows' direct entries.
                {CountsRefreshForResources(options, affected)}
                {string.Concat(scopeTables.Select(t => DirectRefresh(options, t, affected) + "\n"))}
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

                -- 3. The scope columns of every application row.
                {string.Concat(scopeTables.Select(t => ScopeFillUpdate(options, t) + ";\n"))}
                -- 4. The grant counts and the direct indexes, from the grants and the new lineage.
                PERFORM {Qualify(options.Schema, "fn_" + SqlOSFgaPageIndex.RebuildRoutine)}();
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

                {Propagate("new_rows")}
                -- A parent that had no children before this statement is now a container: the grants on it
                -- enter the grant counts (a childless resource's grants are served by the direct index alone).
                {CountsRefreshForResources(options, $"(SELECT DISTINCT n.\"ParentId\" AS \"Id\" FROM new_rows n WHERE n.\"ParentId\" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM {resources} c WHERE c.\"ParentId\" = n.\"ParentId\" AND NOT EXISTS (SELECT 1 FROM new_rows x WHERE x.\"Id\" = c.\"Id\")))")}
                RETURN NULL;
            END
            $sqlos$;
            """;

        // The parents that gained their first children or lost their last ones in an update: their grants
        // enter or leave the grant counts. The moved rows come from EXCEPT, never from a join of the transition tables.
        const string movedIn = "(SELECT \"Id\", \"ParentId\" FROM new_rows EXCEPT SELECT \"Id\", \"ParentId\" FROM old_rows)";
        const string movedOut = "(SELECT \"Id\", \"ParentId\" FROM old_rows EXCEPT SELECT \"Id\", \"ParentId\" FROM new_rows)";
        var transitionedParents = $"""
            (SELECT m."ParentId" AS "Id" FROM {movedIn} m
             WHERE m."ParentId" IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM {resources} c WHERE c."ParentId" = m."ParentId" AND NOT EXISTS (SELECT 1 FROM {movedIn} y WHERE y."Id" = c."Id"))
             UNION
             SELECT o."ParentId" FROM {movedOut} o
             WHERE o."ParentId" IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM {resources} c WHERE c."ParentId" = o."ParentId"))
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
                {DirectRefresh(options, table, "(SELECT n.\"Id\" FROM (SELECT \"Id\", \"ResourceTypeId\" FROM new_rows EXCEPT SELECT \"Id\", \"ResourceTypeId\" FROM old_rows) n)")}
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
                SELECT EXISTS (
                    SELECT 1 FROM (
                        SELECT "Id", "ResourceTypeId" FROM new_rows
                        EXCEPT
                        SELECT "Id", "ResourceTypeId" FROM old_rows
                    ) retyped) INTO v_types;
                IF v_ids IS NULL AND NOT v_types THEN
                    RETURN NULL;
                END IF;
                {lockExclusive}
                IF v_ids IS NOT NULL THEN
                    PERFORM {refresh}(v_ids, true);
                    {CountsRefreshForResources(options, transitionedParents)}
                END IF;
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
                {lockExclusive}
                {clears}
                -- A parent left without children is a container no more: the grants on it leave the grant counts.
                {CountsRefreshForResources(options, $"(SELECT DISTINCT o.\"ParentId\" AS \"Id\" FROM old_rows o WHERE o.\"ParentId\" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM {resources} c WHERE c.\"ParentId\" = o.\"ParentId\"))")}
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

        // The rows of every application table that have no scope yet (see the SQL Server provider), in batches
        // of rows so each statement's transition tables stay small. Shared lock, as an insert takes it.
        var fill = Qualify(options.Schema, "fn_" + SqlOSFgaLineage.ScopeFillRoutineName(resourcesTable));
        var fillBody = new StringBuilder();
        foreach (var table in scopeTables)
        {
            var target = ScopeTable(table);
            var resourceIdColumn = QuoteIdentifier(table.ResourceIdColumn);
            fillBody.AppendLine(CultureInfo.InvariantCulture, $"""
                LOOP
                    UPDATE {target} t SET {ScopeAssignment(levels, "r", "rt")}
                    FROM {resources} r
                    INNER JOIN {resourceTypes} rt ON rt."Id" = r."ResourceTypeId"
                    WHERE t.{resourceIdColumn} = r."Id"
                      AND t.ctid = ANY (ARRAY(
                          SELECT m.ctid FROM {target} m
                          INNER JOIN {resources} mr ON mr."Id" = m.{resourceIdColumn}
                          INNER JOIN {resourceTypes} mrt ON mrt."Id" = mr."ResourceTypeId"
                          WHERE m.{QuoteIdentifier(SqlOSFgaLineage.ScopeColumn)} IS NULL AND mrt."{SqlOSFgaLineage.SeqColumn}" IS NOT NULL
                          LIMIT {SqlOSFgaLineage.ScopeFillBatchRows.ToString(CultureInfo.InvariantCulture)}));
                    GET DIAGNOSTICS v_rows = ROW_COUNT;
                    EXIT WHEN v_rows < {SqlOSFgaLineage.ScopeFillBatchRows.ToString(CultureInfo.InvariantCulture)};
                END LOOP;
                """);
        }

        var fillFunction = $"""
            CREATE OR REPLACE FUNCTION {fill}()
            RETURNS void
            LANGUAGE plpgsql
            AS $sqlos$
            DECLARE
                v_rows bigint;
            BEGIN
                PERFORM pg_advisory_xact_lock_shared({lockKey});
                {fillBody}
            END
            $sqlos$;
            """;

        var batches = new List<string> { refreshFunction, rebuildFunction, fillFunction, insertFunction, updateFunction, deleteFunction, resourceTriggers };
        foreach (var table in scopeTables)
        {
            batches.Add(ScopeTriggers(options, table, levels));
        }

        return batches;
    }

    /// <summary>
    /// The trigger functions and the three triggers on an application table (see the SQL Server provider). The
    /// update trigger fires for every update of the table (a statement trigger with transition tables cannot
    /// name columns) and copies the lineage only onto rows whose resource id changed; the direct index follows
    /// every row whose resource id or sort columns changed, and every row deleted.
    /// </summary>
    private string ScopeTriggers(SqlOSFgaOptions options, SqlOSFgaScopeTable table, int levels)
    {
        var resources = Qualify(options.Schema, options.TableNames.Resources);
        var resourceTypes = Qualify(options.Schema, options.TableNames.ResourceTypes);
        var onInsert = Qualify(options.Schema, ScopeFunctionName(table, "Insert"));
        var onUpdate = Qualify(options.Schema, ScopeFunctionName(table, "Update"));
        var onDelete = Qualify(options.Schema, ScopeFunctionName(table, "Delete"));
        var triggers = SqlOSFgaLineage.ScopeTriggerNames(table.Table);
        var resourceId = QuoteIdentifier(table.ResourceIdColumn);
        var keys = string.Join(" AND ", table.KeyColumns.Select(k => $"t.{QuoteIdentifier(k)} = n.{QuoteIdentifier(k)}"));
        var keyList = string.Join(", ", table.KeyColumns.Select(QuoteIdentifier));
        var directList = string.Join(", ", SqlOSFgaPageIndex.DirectColumns(table).Select(c => c.Column).Append(table.ResourceIdColumn).Distinct().Select(QuoteIdentifier));
        // The rows whose key, sort columns, or resource id changed: a hashed set operation over the transition
        // tables, computed where it is needed rather than kept in a temp table (this function's own update of
        // the scope fires it again, and a nested call must not disturb the outer one).
        var changedKeys = $"(SELECT {directList} FROM new_rows EXCEPT SELECT {directList} FROM old_rows)";
        var changedRows = $"(SELECT n.* FROM new_rows n WHERE EXISTS (SELECT 1 FROM {changedKeys} c WHERE {string.Join(" AND ", table.KeyColumns.Select(k => $"c.{QuoteIdentifier(k)} = n.{QuoteIdentifier(k)}"))}))";
        return $"""
            CREATE OR REPLACE FUNCTION {onInsert}()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $sqlos$
            BEGIN
                PERFORM pg_advisory_xact_lock_shared({SqlOSFgaLineage.LineageLockKey(options).ToString(CultureInfo.InvariantCulture)});
                {IsolationGuard}
                UPDATE {ScopeTable(table)} t SET {ScopeAssignment(levels, "r", "rt")}
                FROM new_rows n
                LEFT JOIN {resources} r ON r."Id" = n.{resourceId}
                LEFT JOIN {resourceTypes} rt ON rt."Id" = r."ResourceTypeId"
                WHERE {keys};
                {DirectInsertFromRows(options, table, "new_rows")}
                RETURN NULL;
            END
            $sqlos$;
            CREATE OR REPLACE FUNCTION {onUpdate}()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $sqlos$
            BEGIN
                -- Statement triggers fire for every update of the table, this function's own included and
                -- statements that touched no row: leave unless some row's resource id, key, or sort column changed.
                -- The changed rows come from EXCEPT, a hashed set operation, never from a join of the transition tables.
                IF NOT EXISTS (SELECT 1 FROM {changedKeys} c) THEN
                    RETURN NULL;
                END IF;

                PERFORM pg_advisory_xact_lock_shared({SqlOSFgaLineage.LineageLockKey(options).ToString(CultureInfo.InvariantCulture)});
                {IsolationGuard}
                UPDATE {ScopeTable(table)} t SET {ScopeAssignment(levels, "r", "rt")}
                FROM (
                    SELECT {keyList}, {resourceId} FROM new_rows
                    EXCEPT
                    SELECT {keyList}, {resourceId} FROM old_rows
                ) n
                LEFT JOIN {resources} r ON r."Id" = n.{resourceId}
                LEFT JOIN {resourceTypes} rt ON rt."Id" = r."ResourceTypeId"
                WHERE {keys};
                {DirectDeleteRows(options, table, changedKeys)}
                {DirectInsertFromRows(options, table, changedRows)}
                RETURN NULL;
            END
            $sqlos$;
            CREATE OR REPLACE FUNCTION {onDelete}()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $sqlos$
            BEGIN
                {DirectDeleteRows(options, table, "old_rows")}
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
            CREATE OR REPLACE TRIGGER {QuoteIdentifier(triggers[2])}
                AFTER DELETE ON {ScopeTable(table)}
                REFERENCING OLD TABLE AS old_rows
                FOR EACH STATEMENT EXECUTE FUNCTION {onDelete}();
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

    /// <summary>The scope column of every row of one application table, set from its resource's lineage (the rebuild).</summary>
    private string ScopeFillUpdate(SqlOSFgaOptions options, SqlOSFgaScopeTable table)
        => $"""
            UPDATE {ScopeTable(table)} t SET {ScopeAssignment(SqlOSFgaLineage.Levels(options), "r", "rt")}
            FROM {Qualify(options.Schema, options.TableNames.Resources)} r
            INNER JOIN {Qualify(options.Schema, options.TableNames.ResourceTypes)} rt ON rt."Id" = r."ResourceTypeId"
            WHERE t.{QuoteIdentifier(table.ResourceIdColumn)} = r."Id"
            """;

    /// <summary>Runs the scope fill: every application row that has no scope gets its resource's.</summary>
    public string BuildScopeFillSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"SELECT {Qualify(options.Schema, "fn_" + SqlOSFgaLineage.ScopeFillRoutineName(options.TableNames.Resources))}();";
    }

    /// <summary>1 when the application table exists and has its scope column (its migration is applied), else 0.</summary>
    public string BuildScopeTableReadySql(SqlOSFgaScopeTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return $"""
            SELECT CASE WHEN EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = {ScopeSchemaLiteral(table)} AND table_name = '{SqlLiteral(table.Table)}' AND column_name = '{SqlOSFgaLineage.ScopeColumn}')
            THEN 1 ELSE 0 END AS "Value"
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
            Routine("fn_" + SqlOSFgaLineage.ScopeFillRoutineName(resourcesTable)),
            Routine("fn_" + SqlOSFgaPageIndex.CountsRebuildRoutine),
            Routine("fn_" + SqlOSFgaPageIndex.CountsAdjustRoutine),
            Routine("fn_" + SqlOSFgaPageIndex.CountsRefreshRoutine),
            Routine("fn_" + SqlOSFgaPageIndex.RebuildRoutine),
            Column(SqlOSFgaLineage.AncestorColumn(SqlOSFgaLineage.MaxLevel(options))),
        };
        conditions.AddRange(SqlOSFgaLineage.TriggerNames(resourcesTable).Select(t => Trigger(options.Schema, resourcesTable, t)));
        conditions.AddRange(SqlOSFgaPageIndex.GrantTriggerNames(options.TableNames.Grants).Select(t => Trigger(options.Schema, options.TableNames.Grants, t)));
        var levels = SqlOSFgaLineage.Levels(options);
        foreach (var table in scopeTables)
        {
            // Every object of the table exists (see the SQL Server provider).
            var indexes = SqlOSFgaLineage.ScopeIndexNames(table, levels);
            conditions.Add(Routine(ScopeFunctionName(table, "Insert")));
            conditions.Add(Routine(ScopeFunctionName(table, "Update")));
            conditions.Add(Routine(ScopeFunctionName(table, "Delete")));
            conditions.Add(Routine("fn_" + SqlOSFgaPageIndex.DirectRebuildRoutine(table)));
            conditions.AddRange(SqlOSFgaLineage.ScopeTriggerNames(table.Table).Select(t => Trigger(table.Schema, table.Table, t)));
            conditions.Add(
                $"(SELECT count(*) FROM pg_indexes WHERE schemaname = {ScopeSchemaLiteral(table)} AND tablename = '{SqlLiteral(table.Table)}' AND indexname IN ({NameList(indexes)})) = {indexes.Count.ToString(CultureInfo.InvariantCulture)}");
            conditions.Add(
                $"EXISTS (SELECT 1 FROM pg_statistic_ext s INNER JOIN pg_namespace n ON n.oid = s.stxnamespace WHERE n.nspname = {ScopeSchemaLiteral(table)} AND s.stxname = '{SqlLiteral(SqlOSFgaLineage.ScopeTypeStatisticsName(table.Table))}')");
            var directIndexes = SqlOSFgaPageIndex.DirectIndexNames(table).Append(SqlOSFgaPageIndex.DirectIndexName(table, "Key")).Append(SqlOSFgaPageIndex.DirectIndexName(table, "Row")).ToList();
            conditions.Add(
                $"(SELECT count(*) FROM pg_indexes WHERE schemaname = '{schema}' AND tablename = '{SqlLiteral(SqlOSFgaPageIndex.DirectTable(table))}' AND indexname IN ({NameList(directIndexes)})) = {directIndexes.Count.ToString(CultureInfo.InvariantCulture)}");
        }

        // And nothing stale anywhere.
        foreach (var stale in new[] { StaleTriggers(scopeTables), StaleFunctions(options, scopeTables), StaleIndexes(scopeTables, levels), StaleStatistics(scopeTables), StaleDirectTables(options, scopeTables) })
        {
            conditions.Add($"NOT EXISTS ({stale})");
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
    /// no scope and nobody sees it) and the resource's type row <paramref name="rt"/>: the depth byte, the four
    /// type bytes, then eight bytes per level holding the ancestor where access flows down to the row from that
    /// level, zero elsewhere. <c>int4send</c> and <c>int8send</c> are big-endian, the same bytes SQL Server
    /// writes and <c>SqlOSFgaScope.Bytes</c> encodes a parameter as.
    /// </summary>
    private static string ScopeValue(int levels, string r, string rt)
    {
        var parts = new List<string>
        {
            $"substring(int2send(COALESCE({r}.\"{SqlOSFgaLineage.DepthColumn}\", 0)::smallint) from 2 for 1)",
            $"int4send({rt}.\"{SqlOSFgaLineage.SeqColumn}\")",
        };
        for (var level = 0; level < levels; level++)
        {
            parts.Add($"int8send(COALESCE(CASE WHEN {r}.\"{SqlOSFgaLineage.ReachColumn}\" <= {level} THEN {r}.\"{SqlOSFgaLineage.AncestorColumn(level)}\" END, 0))");
        }

        return $"CASE WHEN {r}.\"Id\" IS NULL THEN NULL ELSE {string.Join(" || ", parts)} END";
    }

    private string ScopeAssignment(int levels, string r, string rt)
        => $"{QuoteIdentifier(SqlOSFgaLineage.ScopeColumn)} = {ScopeValue(levels, r, rt)}";

    private static string ScopeSchemaLiteral(SqlOSFgaScopeTable table)
        => table.Schema is null ? "current_schema()" : $"'{SqlLiteral(table.Schema)}'";

    /// <summary>
    /// The indexes of one application table, per level: an expression index on the level's eight bytes of the
    /// scope column followed by the primary key, and one more per order the application declared, each
    /// filtered on the depth byte to the rows at or below the level; and a partial index on the rows that have
    /// no scope yet, which keeps the fill an index scan. Also extended statistics on the type bytes, analyzed at
    /// once: without them the planner guesses the type test is selective and sorts the caller's whole scope
    /// instead of walking the level's index. Idempotent. Objects no longer wanted are dropped by
    /// <see cref="BuildScopeCleanupSql"/>.
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
                CREATE INDEX IF NOT EXISTS {QuoteIdentifier(SqlOSFgaLineage.ScopeMissingIndexName(table.Table))} ON {target} ({QuoteIdentifier(table.ResourceIdColumn)}) WHERE {scope} IS NULL;
                """);
            for (var level = 0; level < levels; level++)
            {
                var ancestor = $"SUBSTRING({scope}, {SqlOSFgaLineage.ScopeAncestorOffset(level)}, 8)";
                var filter = $"{scope} >= '\\x{level:x2}'::bytea";
                foreach (var (name, columns) in new[] { (SqlOSFgaLineage.ScopeIndexName(table.Table, level, null), key) }
                    .Concat(table.Orders.Select(o => (SqlOSFgaLineage.ScopeIndexName(table.Table, level, o.Suffix), string.Join(", ", o.Columns.Select(QuoteIdentifier))))))
                {
                    sql.AppendLine(CultureInfo.InvariantCulture, $"""
                        CREATE INDEX IF NOT EXISTS {QuoteIdentifier(name)} ON {target} (({ancestor}), {columns}) WHERE {filter};
                        """);
                }
            }

            sql.AppendLine(CultureInfo.InvariantCulture, $"""
                CREATE STATISTICS IF NOT EXISTS {QuoteIdentifier(SqlOSFgaLineage.ScopeTypeStatisticsName(table.Table))} ON ((SUBSTRING({scope}, {SqlOSFgaLineage.ScopeTypeOffset}, 4))) FROM {target};
                ANALYZE {target};
                """);
            batches.Add(sql.ToString());
        }

        return batches;
    }

    // SqlOS's objects on application tables carry names no application object has (see the SQL Server
    // provider); on PostgreSQL also the trigger functions fn_SqlOSFgaScope_* in SqlOS's schema. Any of them not
    // belonging to a table SqlOS maintains now, under its current name, is stale.

    private static string NameList(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{SqlLiteral(n)}'"));

    private static string Wanted(IReadOnlyList<SqlOSFgaScopeTable> scopeTables, Func<SqlOSFgaScopeTable, string> condition)
        => scopeTables.Count == 0 ? "FALSE" : string.Join(" OR ", scopeTables.Select(t => $"({condition(t)})"));

    private static string StaleTriggers(IReadOnlyList<SqlOSFgaScopeTable> scopeTables)
        => $"""
            SELECT format('DROP TRIGGER %I ON %I.%I', t.tgname, n.nspname, c.relname) AS statement
            FROM pg_trigger t
            INNER JOIN pg_class c ON c.oid = t.tgrelid
            INNER JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE NOT t.tgisinternal AND t.tgname LIKE 'TR\_%\_{SqlOSFgaLineage.ScopePrefix}Scope\_%'
              AND NOT ({Wanted(scopeTables, tb => $"n.nspname = {ScopeSchemaLiteral(tb)} AND c.relname = '{SqlLiteral(tb.Table)}' AND t.tgname IN ({NameList(SqlOSFgaLineage.ScopeTriggerNames(tb.Table))})")})
            """;

    private static string StaleFunctions(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> scopeTables)
        => $"""
            SELECT format('DROP FUNCTION %I.%I()', n.nspname, p.proname) AS statement
            FROM pg_proc p
            INNER JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = '{SqlLiteral(options.Schema)}'
              AND (p.proname LIKE 'fn\_{SqlOSFgaLineage.ScopePrefix}Scope\_%' OR p.proname LIKE 'fn\_{SqlOSFgaPageIndex.DirectPrefix.Replace("_", "\\_", StringComparison.Ordinal)}%')
              AND p.proname NOT IN ({(scopeTables.Count == 0 ? "''" : NameList(scopeTables.SelectMany(t => new[] { ScopeFunctionName(t, "Insert"), ScopeFunctionName(t, "Update"), ScopeFunctionName(t, "Delete"), "fn_" + SqlOSFgaPageIndex.DirectRebuildRoutine(t) })))})
            """;

    /// <summary>A direct index of a table SqlOS no longer maintains (renamed, or no longer protected).</summary>
    private static string StaleDirectTables(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> scopeTables)
        => $"""
            SELECT format('DROP TABLE %I.%I', n.nspname, c.relname) AS statement
            FROM pg_class c
            INNER JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = '{SqlLiteral(options.Schema)}' AND c.relkind = 'r' AND c.relname LIKE '{SqlOSFgaPageIndex.DirectPrefix.Replace("_", "\\_", StringComparison.Ordinal)}%'
              AND c.relname NOT IN ({(scopeTables.Count == 0 ? "''" : NameList(scopeTables.Select(SqlOSFgaPageIndex.DirectTable)))})
            """;

    private static string StaleIndexes(IReadOnlyList<SqlOSFgaScopeTable> scopeTables, int levels)
        => $"""
            SELECT format('DROP INDEX %I.%I', i.schemaname, i.indexname) AS statement
            FROM pg_indexes i
            WHERE i.indexname ~ '^IX_.*_{SqlOSFgaLineage.ScopeColumn}([0-9]|Missing)'
              AND NOT ({Wanted(scopeTables, t => $"i.schemaname = {ScopeSchemaLiteral(t)} AND i.tablename = '{SqlLiteral(t.Table)}' AND i.indexname IN ({NameList(SqlOSFgaLineage.ScopeIndexNames(t, levels))})")})
            """;

    private static string StaleStatistics(IReadOnlyList<SqlOSFgaScopeTable> scopeTables)
        => $"""
            SELECT format('DROP STATISTICS %I.%I', n.nspname, s.stxname) AS statement
            FROM pg_statistic_ext s
            INNER JOIN pg_namespace n ON n.oid = s.stxnamespace
            INNER JOIN pg_class c ON c.oid = s.stxrelid
            WHERE s.stxname LIKE 'ST\_%\_{SqlOSFgaLineage.ScopeTypeColumn}'
              AND NOT ({Wanted(scopeTables, t => $"n.nspname = {ScopeSchemaLiteral(t)} AND c.relname = '{SqlLiteral(t.Table)}' AND s.stxname = '{SqlLiteral(SqlOSFgaLineage.ScopeTypeStatisticsName(t.Table))}'")})
            """;

    /// <summary>Drops SqlOS's stale objects from every table of the database: triggers, their functions, indexes, statistics. Idempotent.</summary>
    public string BuildScopeCleanupSql(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> scopeTables)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopeTables);
        var levels = SqlOSFgaLineage.Levels(options);
        var loops = new StringBuilder();
        foreach (var stale in new[] { StaleTriggers(scopeTables), StaleFunctions(options, scopeTables), StaleIndexes(scopeTables, levels), StaleStatistics(scopeTables), StaleDirectTables(options, scopeTables) })
        {
            loops.AppendLine(CultureInfo.InvariantCulture, $"""
                FOR stale IN {stale}
                LOOP
                    EXECUTE stale.statement;
                END LOOP;
                """);
        }

        return $"""
            DO $sqlos$
            DECLARE
                stale record;
            BEGIN
                {loops}
            END
            $sqlos$;
            """;
    }

    /// <summary>
    /// A transaction in REPEATABLE READ keeps reading the snapshot it started with even after waiting for the
    /// lineage lock, so it could compute from state another transaction has since changed. READ COMMITTED (the
    /// default) reads what was committed when each statement starts, and SERIALIZABLE aborts the conflicting
    /// transaction itself; REPEATABLE READ is refused rather than allowed to write a lineage that may be stale.
    /// </summary>
    private const string IsolationGuard = """
        IF current_setting('transaction_isolation') = 'repeatable read' THEN
            RAISE EXCEPTION 'SqlOS FGA: change resources and protected rows in a READ COMMITTED or SERIALIZABLE transaction, not REPEATABLE READ: a repeatable read can miss a concurrent change to the tree.' USING ERRCODE = 'SQ014';
        END IF;
        """;

    private static string SqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}

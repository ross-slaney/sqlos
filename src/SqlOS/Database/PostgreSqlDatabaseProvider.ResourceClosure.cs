using System.Globalization;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;

namespace SqlOS.Database;

/// <summary>
/// The SHRBAC enforcement artifacts beyond the row filter: the caller's access roots, the ancestor closure
/// that lists every resource under each ancestor, and the authorized page that reads it.
/// </summary>
internal sealed partial class PostgreSqlDatabaseProvider
{
    public string BuildAccessRootsFunctionSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = QuoteIdentifier(options.Schema);
        var tables = options.TableNames;
        var resources = Qualify(options.Schema, tables.Resources);
        var resourceTypes = Qualify(options.Schema, tables.ResourceTypes);
        var grants = Qualify(options.Schema, tables.Grants);
        var rolePermissions = Qualify(options.Schema, tables.RolePermissions);
        var subjects = Qualify(options.Schema, tables.Subjects);
        var users = Qualify(options.Schema, tables.Users);
        var serviceAccounts = Qualify(options.Schema, tables.ServiceAccounts);
        var userGroups = Qualify(options.Schema, tables.UserGroups);
        var agents = Qualify(options.Schema, tables.Agents);
        return $"""
            CREATE OR REPLACE FUNCTION {schema}."fn_AccessRoots"(
                p_subject_ids text,
                p_permission_id varchar(128)
            )
            RETURNS TABLE("ResourceId" varchar(450), "ResourceSeq" bigint, "TypeSeq" integer, "ParentSeq" bigint, "ParentIsActive" boolean)
            LANGUAGE sql
            STABLE
            AS $sqlos$
            -- The caller's live grants first (a handful of rows, found by subject), then their resources by primary
            -- key. The CTE is materialized so the planner cannot start from the resource table: at ten million
            -- resources it has chosen a merge join along the resource index to feed a DISTINCT, scanning tens of
            -- thousands of rows per call. Duplicate roots (two grants on one resource) are fine; the consumers
            -- treat the result as a set.
            WITH g AS MATERIALIZED (
                SELECT g."ResourceId"
                FROM {grants} g
                INNER JOIN {rolePermissions} rp ON g."RoleId" = rp."RoleId"
                INNER JOIN {subjects} s ON g."SubjectId" = s."Id"
                LEFT JOIN {users} u ON s."Id" = u."SubjectId"
                LEFT JOIN {serviceAccounts} sa ON s."Id" = sa."SubjectId"
                LEFT JOIN {userGroups} ug ON s."Id" = ug."SubjectId"
                LEFT JOIN {agents} ag ON s."Id" = ag."SubjectId"
                WHERE g."SubjectId" = ANY (ARRAY(SELECT jsonb_array_elements_text(p_subject_ids::jsonb)))
                  AND rp."PermissionId" = p_permission_id
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
                  AND (g."EffectiveFrom" IS NULL OR g."EffectiveFrom" <= (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'))
                  AND (g."EffectiveTo" IS NULL OR g."EffectiveTo" >= (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'))
            )
            SELECT r."Id", r."Seq", rt."Seq", parent."Seq", parent."IsActive"
            FROM g
            INNER JOIN {resources} r ON r."Id" = g."ResourceId"
            INNER JOIN {resourceTypes} rt ON rt."Id" = r."ResourceTypeId"
            LEFT JOIN {resources} parent ON parent."Id" = r."ParentId"
            WHERE r."IsActive" = TRUE
            $sqlos$;
            """;
    }

    public IReadOnlyList<string> BuildResourceClosureMaintenanceSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = QuoteIdentifier(options.Schema);
        var resources = Qualify(options.Schema, options.TableNames.Resources);
        var resourceTypes = Qualify(options.Schema, options.TableNames.ResourceTypes);
        var closure = Qualify(options.Schema, SqlOSFgaResourceClosure.TableName(options));
        var closureName = SqlOSFgaResourceClosure.TableName(options);
        var triggerNames = SqlOSFgaResourceClosure.TriggerNames(options.TableNames.Resources);
        var apply = Qualify(options.Schema, $"fn_{closureName}_Apply");
        var rebuild = Qualify(options.Schema, $"fn_{closureName}_Rebuild");
        var onInsert = Qualify(options.Schema, $"fn_{closureName}_OnInsert");
        var onUpdate = Qualify(options.Schema, $"fn_{closureName}_OnUpdate");
        var onDelete = Qualify(options.Schema, $"fn_{closureName}_OnDelete");
        var insertTrigger = QuoteIdentifier(triggerNames[0]);
        var updateTrigger = QuoteIdentifier(triggerNames[1]);
        var deleteTrigger = QuoteIdentifier(triggerNames[2]);
        var maxDepth = Math.Max(1, options.MaxResourceHierarchyDepth).ToString(CultureInfo.InvariantCulture);

        // Walks up from the given descendants over a source of (Id, ParentId, IsActive, ResourceTypeId, Seq),
        // exactly as fn_IsResourceAccessible walks: from an active descendant, through active ancestors only.
        // One row per (descendant, ancestor) reached; Active says whether the ancestor itself is active (every
        // node below it on the path is). Depth counts ancestors.
        string PairsCte(string source, string descendants) => $"""
            chain AS (
                SELECT x."Id" AS "DescendantId", x."Seq" AS "DescendantSeq", rt."Seq" AS "TypeSeq",
                       x."ParentId" AS "AncestorId", 1 AS "Depth"
                FROM {source} x
                INNER JOIN {resourceTypes} rt ON rt."Id" = x."ResourceTypeId"
                WHERE {descendants} AND x."ParentId" IS NOT NULL AND x."IsActive"
                UNION ALL
                SELECT c."DescendantId", c."DescendantSeq", c."TypeSeq", p."ParentId", c."Depth" + 1
                FROM chain c
                INNER JOIN {source} p ON p."Id" = c."AncestorId"
                WHERE p."ParentId" IS NOT NULL AND p."IsActive" AND c."Depth" <= {maxDepth}
            ),
            pairs AS (
                SELECT c."DescendantId", c."DescendantSeq", c."TypeSeq", c."AncestorId", p."Seq" AS "AncestorSeq", c."Depth",
                       p."IsActive" AS "Active"
                FROM chain c
                INNER JOIN {source} p ON p."Id" = c."AncestorId"
            )
            """;

        // One statement and one walk per call: the active pairs of the well-formed targets are inserted and the
        // malformed targets are counted; raising afterwards rolls the insert back with the statement. No temp
        // table, so the hot path (every resource insert) leaves no catalog churn behind.
        var applyFunction = $"""
            DROP FUNCTION IF EXISTS {apply}(varchar[]);
            CREATE OR REPLACE FUNCTION {apply}(p_ids varchar[], p_reject boolean)
            RETURNS void
            LANGUAGE plpgsql
            AS $sqlos$
            DECLARE
                v_cycles bigint;
                v_malformed bigint;
            BEGIN
                WITH RECURSIVE {PairsCte(resources, "x.\"Id\" = ANY (p_ids)")},
                malformed AS (
                    SELECT "DescendantId", bool_or("AncestorId" = "DescendantId") AS "Cycle"
                    FROM pairs
                    WHERE "Depth" > {maxDepth} OR "AncestorId" = "DescendantId"
                    GROUP BY "DescendantId"
                ),
                inserted AS (
                    INSERT INTO {closure} ("AncestorSeq", "TypeSeq", "DescendantSeq")
                    SELECT p."AncestorSeq", p."TypeSeq", p."DescendantSeq"
                    FROM pairs p
                    WHERE p."Active"
                      AND NOT EXISTS (SELECT 1 FROM malformed m WHERE m."DescendantId" = p."DescendantId")
                    RETURNING 1
                )
                SELECT count(*) FILTER (WHERE "Cycle"), count(*) INTO v_cycles, v_malformed FROM malformed;

                IF p_reject AND v_cycles > 0 THEN
                    RAISE EXCEPTION 'SqlOS FGA: the resource hierarchy change would create a cycle.' USING ERRCODE = 'SQ011';
                END IF;

                IF p_reject AND v_malformed > 0 THEN
                    RAISE EXCEPTION 'SqlOS FGA: the resource would exceed the configured maximum hierarchy depth of {maxDepth}.' USING ERRCODE = 'SQ012';
                END IF;
            END
            $sqlos$;
            """;

        // Rebuild tolerates malformed data the way the row filter does: a resource with a cycle or more than
        // the allowed ancestors gets no rows, so it is invisible to pages as it is denied by the filter. The
        // function body is one transaction, so a failed rebuild leaves the previous closure, never a partial one.
        var rebuildFunction = $"""
            CREATE OR REPLACE FUNCTION {rebuild}()
            RETURNS bigint
            LANGUAGE plpgsql
            AS $sqlos$
            DECLARE
                v_count bigint;
            BEGIN
                TRUNCATE TABLE {closure};
                WITH RECURSIVE {PairsCte(resources, "TRUE")},
                malformed AS (
                    SELECT DISTINCT "DescendantId" FROM pairs WHERE "Depth" > {maxDepth} OR "AncestorId" = "DescendantId"
                )
                INSERT INTO {closure} ("AncestorSeq", "TypeSeq", "DescendantSeq")
                SELECT p."AncestorSeq", p."TypeSeq", p."DescendantSeq"
                FROM pairs p
                WHERE p."Active"
                  AND NOT EXISTS (SELECT 1 FROM malformed m WHERE m."DescendantId" = p."DescendantId");
                GET DIAGNOSTICS v_count = ROW_COUNT;
                RETURN v_count;
            END
            $sqlos$;
            """;

        var insertFunction = $"""
            CREATE OR REPLACE FUNCTION {onInsert}()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $sqlos$
            BEGIN
                PERFORM {apply}(ARRAY(SELECT n."Id" FROM new_rows n WHERE n."ParentId" IS NOT NULL), true);
                RETURN NULL;
            END
            $sqlos$;
            """;

        // The old images of a statement's rows, copied into an indexed temp table so the walks below probe them
        // like the table instead of scanning the transition table once per probe.
        var oldImages = """
            DROP TABLE IF EXISTS pg_temp."SqlOSClosureOld";
            CREATE TEMP TABLE "SqlOSClosureOld" ON COMMIT DROP AS
            SELECT "Id", "ParentId", "IsActive", "ResourceTypeId", "Seq" FROM old_rows;
            ALTER TABLE pg_temp."SqlOSClosureOld" ADD PRIMARY KEY ("Id");
            ANALYZE pg_temp."SqlOSClosureOld";
            """;

        // The table as it was before an UPDATE: the current rows, with the updated ones replaced by their old images.
        var oldStateForUpdate = $"""
            (SELECT r."Id",
                    CASE WHEN o."Id" IS NULL THEN r."ParentId" ELSE o."ParentId" END AS "ParentId",
                    CASE WHEN o."Id" IS NULL THEN r."IsActive" ELSE o."IsActive" END AS "IsActive",
                    CASE WHEN o."Id" IS NULL THEN r."ResourceTypeId" ELSE o."ResourceTypeId" END AS "ResourceTypeId",
                    r."Seq"
             FROM {resources} r
             LEFT JOIN pg_temp."SqlOSClosureOld" o ON o."Id" = r."Id")
            """;

        var updateFunction = $"""
            CREATE OR REPLACE FUNCTION {onUpdate}()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $sqlos$
            DECLARE
                v_ids varchar[];
            BEGIN
                -- Every resource whose ancestor chain may have changed: the changed rows and everything beneath them.
                WITH RECURSIVE changed AS (
                    SELECT n."Id"
                    FROM new_rows n
                    INNER JOIN old_rows o ON o."Id" = n."Id"
                    WHERE n."ParentId" IS DISTINCT FROM o."ParentId"
                       OR n."IsActive" <> o."IsActive"
                       OR n."ResourceTypeId" <> o."ResourceTypeId"
                ),
                sub AS (
                    SELECT "Id", 0 AS "Depth" FROM changed
                    UNION ALL
                    SELECT r."Id", s."Depth" + 1
                    FROM {resources} r
                    INNER JOIN sub s ON r."ParentId" = s."Id"
                    WHERE s."Depth" <= {maxDepth}
                )
                SELECT array_agg(DISTINCT "Id") INTO v_ids FROM sub;

                IF v_ids IS NULL THEN
                    RETURN NULL;
                END IF;

                {oldImages}
                -- Their pairs as they were before the statement, deleted by exact key.
                WITH RECURSIVE {PairsCte(oldStateForUpdate, "x.\"Id\" = ANY (v_ids)")}
                DELETE FROM {closure} cl
                USING pairs o
                WHERE o."Active"
                  AND cl."AncestorSeq" = o."AncestorSeq" AND cl."TypeSeq" = o."TypeSeq" AND cl."DescendantSeq" = o."DescendantSeq";

                DROP TABLE pg_temp."SqlOSClosureOld";
                PERFORM {apply}(v_ids, true);
                RETURN NULL;
            END
            $sqlos$;
            """;

        // The table as it was before a DELETE: the current rows plus the deleted rows put back.
        var oldStateForDelete = $"""
            (SELECT "Id", "ParentId", "IsActive", "ResourceTypeId", "Seq" FROM {resources}
             UNION ALL
             SELECT "Id", "ParentId", "IsActive", "ResourceTypeId", "Seq" FROM pg_temp."SqlOSClosureOld")
            """;

        var deleteFunction = $"""
            CREATE OR REPLACE FUNCTION {onDelete}()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $sqlos$
            BEGIN
                -- Inactive rows and roots hold no pairs.
                IF NOT EXISTS (SELECT 1 FROM old_rows WHERE "ParentId" IS NOT NULL AND "IsActive") THEN
                    RETURN NULL;
                END IF;

                {oldImages}
                WITH RECURSIVE {PairsCte(oldStateForDelete, "x.\"Id\" IN (SELECT \"Id\" FROM pg_temp.\"SqlOSClosureOld\")")}
                DELETE FROM {closure} cl
                USING pairs o
                WHERE o."Active"
                  AND cl."AncestorSeq" = o."AncestorSeq" AND cl."TypeSeq" = o."TypeSeq" AND cl."DescendantSeq" = o."DescendantSeq";

                DROP TABLE pg_temp."SqlOSClosureOld";
                RETURN NULL;
            END
            $sqlos$;
            """;

        var triggers = $"""
            CREATE OR REPLACE TRIGGER {insertTrigger}
                AFTER INSERT ON {resources}
                REFERENCING NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION {onInsert}();
            CREATE OR REPLACE TRIGGER {updateTrigger}
                AFTER UPDATE ON {resources}
                REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION {onUpdate}();
            CREATE OR REPLACE TRIGGER {deleteTrigger}
                AFTER DELETE ON {resources}
                REFERENCING OLD TABLE AS old_rows
                FOR EACH STATEMENT EXECUTE FUNCTION {onDelete}();
            """;

        return [applyFunction, rebuildFunction, insertFunction, updateFunction, deleteFunction, triggers];
    }

    public string BuildResourceClosureNeedsBuildSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var resources = Qualify(options.Schema, options.TableNames.Resources);
        var closure = Qualify(options.Schema, SqlOSFgaResourceClosure.TableName(options));
        return $"""
            SELECT CASE
                WHEN NOT EXISTS (SELECT 1 FROM {closure})
                 AND EXISTS (SELECT 1 FROM {resources} WHERE "ParentId" IS NOT NULL)
                THEN 1 ELSE 0 END AS "Value"
            """;
    }

    public string BuildResourceClosureRebuildSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var rebuild = Qualify(options.Schema, $"fn_{SqlOSFgaResourceClosure.TableName(options)}_Rebuild");
        return $"SELECT {rebuild}();";
    }

    /// <summary>
    /// One authorized page, as a single composable SELECT: for each access root, the first <c>@PageSize</c>
    /// descendants of the requested type after <c>@Cursor</c> from the closure (one index range each) merged
    /// with the root itself when it is of that type and its own chain is well formed (it has no parent, an
    /// inactive parent, or a closure row under its parent: one point lookup); deduplicated across roots and
    /// cut to <c>@PageSize</c>.
    /// </summary>
    public string BuildVisibleResourcesPageSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = QuoteIdentifier(options.Schema);
        var resources = Qualify(options.Schema, options.TableNames.Resources);
        var resourceTypes = Qualify(options.Schema, options.TableNames.ResourceTypes);
        var closure = Qualify(options.Schema, SqlOSFgaResourceClosure.TableName(options));
        return $"""
            SELECT r."Id" AS "ResourceId", d."Seq"
            FROM {resourceTypes} rt
            CROSS JOIN LATERAL (
                SELECT DISTINCT v."Seq"
                FROM {schema}."fn_AccessRoots"(@SubjectIds, @PermissionId) a
                CROSS JOIN LATERAL (
                    SELECT c."DescendantSeq" AS "Seq"
                    FROM (
                        SELECT c."DescendantSeq"
                        FROM {closure} c
                        WHERE c."AncestorSeq" = a."ResourceSeq" AND c."TypeSeq" = rt."Seq" AND c."DescendantSeq" > @Cursor
                        ORDER BY c."DescendantSeq"
                        LIMIT @PageSize
                    ) c
                    UNION ALL
                    SELECT a."ResourceSeq"
                    WHERE a."TypeSeq" = rt."Seq" AND a."ResourceSeq" > @Cursor
                      AND (a."ParentSeq" IS NULL OR NOT a."ParentIsActive" OR EXISTS (
                          SELECT 1 FROM {closure} s
                          WHERE s."AncestorSeq" = a."ParentSeq" AND s."TypeSeq" = a."TypeSeq" AND s."DescendantSeq" = a."ResourceSeq"))
                ) v
            ) d
            INNER JOIN {resources} r ON r."Seq" = d."Seq"
            WHERE rt."Id" = @ResourceTypeId
            ORDER BY d."Seq"
            LIMIT @PageSize
            """;
    }

    public string BuildSelectRoutinesHashSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = SqlLiteral(options.Schema);
        var resources = SqlLiteral(options.TableNames.Resources);
        var closure = SqlOSFgaResourceClosure.TableName(options);
        var triggers = SqlOSFgaResourceClosure.TriggerNames(options.TableNames.Resources);
        string Routine(string name)
            => $"EXISTS (SELECT 1 FROM pg_proc p INNER JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = '{schema}' AND p.proname = '{SqlLiteral(name)}')";
        string Trigger(string name)
            => $"EXISTS (SELECT 1 FROM pg_trigger t INNER JOIN pg_class c ON c.oid = t.tgrelid INNER JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = '{schema}' AND c.relname = '{resources}' AND t.tgname = '{SqlLiteral(name)}' AND NOT t.tgisinternal)";

        return $"""
            SELECT "RoutinesHash"
            FROM {Qualify(options.Schema, "SqlOSFgaSchema")}
            WHERE {Routine("fn_AccessRoots")}
              AND {Routine("fn_IsResourceAccessible")}
              AND {Routine($"fn_{closure}_Apply")}
              AND {Routine($"fn_{closure}_Rebuild")}
              AND {Trigger(triggers[0])}
              AND {Trigger(triggers[1])}
              AND {Trigger(triggers[2])}
            LIMIT 1
            """;
    }

    public string BuildStoreRoutinesHashSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"UPDATE {Qualify(options.Schema, "SqlOSFgaSchema")} SET \"RoutinesHash\" = @RoutinesHash";
    }

    private static string SqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}

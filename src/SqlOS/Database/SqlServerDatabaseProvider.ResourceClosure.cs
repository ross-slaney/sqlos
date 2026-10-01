using System.Globalization;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;

namespace SqlOS.Database;

/// <summary>
/// The SHRBAC enforcement artifacts beyond the row filter: the caller's access roots, the ancestor closure
/// that lists every resource under each ancestor, and the authorized page that reads it.
/// </summary>
internal sealed partial class SqlServerDatabaseProvider
{
    private const int ClosureRecursionLimit = 110;

    /// <summary>
    /// <c>fn_AccessRoots(@SubjectIds, @PermissionId)</c>: the active resources on which one of the caller's
    /// subjects holds an active grant whose role includes the permission. Every grant condition the row
    /// filter used to evaluate per candidate row lives here, evaluated once per query. The parent's sequence
    /// number and state let the page query confirm a root's own chain is well formed with one closure lookup.
    /// </summary>
    public string BuildAccessRootsFunctionSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        var tables = options.TableNames;
        var resources = Escape(tables.Resources);
        var resourceTypes = Escape(tables.ResourceTypes);
        var grants = Escape(tables.Grants);
        var rolePermissions = Escape(tables.RolePermissions);
        var subjects = Escape(tables.Subjects);
        var users = Escape(tables.Users);
        var serviceAccounts = Escape(tables.ServiceAccounts);
        var userGroups = Escape(tables.UserGroups);
        var agents = Escape(tables.Agents);
        return $"""
            CREATE OR ALTER FUNCTION [{schema}].fn_AccessRoots(
                @SubjectIds NVARCHAR(MAX),
                @PermissionId NVARCHAR(128)
            )
            RETURNS TABLE
            AS
            RETURN
            (
                -- DISTINCT is load-bearing: it makes the root set a blocking subtree that the optimizer builds
                -- once into a spool and rewinds for every candidate row of a query. Without it the inlined set
                -- is recomputed per row (seen in captured plans: the grants seek ran once per row).
                SELECT DISTINCT r.Id AS ResourceId, r.Seq AS ResourceSeq, rt.Seq AS TypeSeq,
                       parent.Seq AS ParentSeq, parent.IsActive AS ParentIsActive
                FROM [{schema}].[{grants}] g
                INNER JOIN [{schema}].[{rolePermissions}] rp ON g.RoleId = rp.RoleId
                INNER JOIN [{schema}].[{resources}] r ON g.ResourceId = r.Id
                INNER JOIN [{schema}].[{resourceTypes}] rt ON r.ResourceTypeId = rt.Id
                LEFT JOIN [{schema}].[{resources}] parent ON parent.Id = r.ParentId
                INNER JOIN [{schema}].[{subjects}] s ON g.SubjectId = s.Id
                LEFT JOIN [{schema}].[{users}] u ON s.Id = u.SubjectId
                LEFT JOIN [{schema}].[{serviceAccounts}] sa ON s.Id = sa.SubjectId
                LEFT JOIN [{schema}].[{userGroups}] ug ON s.Id = ug.SubjectId
                LEFT JOIN [{schema}].[{agents}] ag ON s.Id = ag.SubjectId
                WHERE g.SubjectId IN (SELECT CONVERT(NVARCHAR(450), [value]) FROM OPENJSON(@SubjectIds))
                  AND rp.PermissionId = @PermissionId
                  AND r.IsActive = 1
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
                  AND (g.EffectiveFrom IS NULL OR g.EffectiveFrom <= GETUTCDATE())
                  AND (g.EffectiveTo IS NULL OR g.EffectiveTo >= GETUTCDATE())
            )
            """;
    }

    /// <summary>
    /// The closure's maintenance: an apply procedure (walk up from a set of resources and insert their
    /// active ancestor pairs, rejecting cycles and over-deep nodes), a rebuild procedure, and the three
    /// triggers on the resource table. Each batch is idempotent (<c>CREATE OR ALTER</c>).
    /// </summary>
    public IReadOnlyList<string> BuildResourceClosureMaintenanceSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        var resources = Escape(options.TableNames.Resources);
        var resourceTypes = Escape(options.TableNames.ResourceTypes);
        var closure = Escape(SqlOSFgaResourceClosure.TableName(options));
        var maxDepth = Math.Max(1, options.MaxResourceHierarchyDepth).ToString(CultureInfo.InvariantCulture);

        // Walks up from every row in #SqlOSClosureTargets over the given source of (Id, ParentId, IsActive, Seq)
        // rows, exactly as fn_IsResourceAccessible walks: from an active descendant, through active ancestors
        // only. One row per (descendant, ancestor) reached; Active says whether the ancestor itself is active
        // (every node below it on the path is). Depth counts ancestors; a row deeper than the limit, or an
        // ancestor equal to the descendant, is reported by the caller.
        string PairsCte(string source) => $"""
            chain AS (
                SELECT t.Id AS DescendantId, t.Seq AS DescendantSeq, t.TypeSeq, x.ParentId AS AncestorId, 1 AS Depth
                FROM #SqlOSClosureTargets t
                INNER JOIN {source} x ON x.Id = t.Id
                WHERE x.ParentId IS NOT NULL AND x.IsActive = 1
                UNION ALL
                SELECT c.DescendantId, c.DescendantSeq, c.TypeSeq, p.ParentId, c.Depth + 1
                FROM chain c
                INNER JOIN {source} p ON p.Id = c.AncestorId
                WHERE p.ParentId IS NOT NULL AND p.IsActive = 1 AND c.Depth <= {maxDepth}
            ),
            pairs AS (
                SELECT c.DescendantId, c.DescendantSeq, c.TypeSeq, c.AncestorId, p.Seq AS AncestorSeq, c.Depth, p.IsActive AS Active
                FROM chain c
                INNER JOIN {source} p ON p.Id = c.AncestorId
            )
            """;

        var targetsTable = """
            CREATE TABLE #SqlOSClosureTargets (
                Id NVARCHAR(450) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
                Seq BIGINT NOT NULL,
                TypeSeq INT NOT NULL
            );
            """;

        // Apply inserts the active ancestor pairs of every resource in #SqlOSClosureTargets, from the current
        // table. Triggers call it strictly: a cycle or an over-deep row rejects the statement. The rebuild calls
        // it tolerantly: such rows get no pairs, which is how the row filter treats them (denied).
        var apply = $"""
            CREATE OR ALTER PROCEDURE [{schema}].[sp_{closure}_Apply]
                @RejectMalformed BIT = 1
            AS
            BEGIN
                SET NOCOUNT ON;
                ;WITH {PairsCte($"[{schema}].[{resources}]")}
                SELECT DescendantId, DescendantSeq, TypeSeq, AncestorId, AncestorSeq, Depth, Active
                INTO #SqlOSClosureChain
                FROM pairs
                OPTION (MAXRECURSION {ClosureRecursionLimit});

                IF @RejectMalformed = 1
                BEGIN
                    IF EXISTS (SELECT 1 FROM #SqlOSClosureChain WHERE AncestorId = DescendantId)
                    BEGIN
                        DROP TABLE #SqlOSClosureChain;
                        THROW 51011, 'SqlOS FGA: the resource hierarchy change would create a cycle.', 1;
                    END

                    IF EXISTS (SELECT 1 FROM #SqlOSClosureChain WHERE Depth > {maxDepth})
                    BEGIN
                        DROP TABLE #SqlOSClosureChain;
                        THROW 51012, 'SqlOS FGA: the resource would exceed the configured maximum hierarchy depth of {maxDepth}.', 1;
                    END
                END

                INSERT INTO [{schema}].[{closure}] (AncestorSeq, TypeSeq, DescendantSeq)
                SELECT c.AncestorSeq, c.TypeSeq, c.DescendantSeq
                FROM #SqlOSClosureChain c
                WHERE c.Active = 1
                  AND NOT EXISTS (
                      SELECT 1 FROM #SqlOSClosureChain m
                      WHERE m.DescendantId = c.DescendantId
                        AND (m.Depth > {maxDepth} OR m.AncestorId = m.DescendantId));

                DROP TABLE #SqlOSClosureChain;
            END
            """;

        // One transaction: a failed rebuild leaves the previous closure, never a partial one.
        var rebuild = $"""
            CREATE OR ALTER PROCEDURE [{schema}].[sp_{closure}_Rebuild]
            AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                TRUNCATE TABLE [{schema}].[{closure}];
                {targetsTable}
                INSERT INTO #SqlOSClosureTargets (Id, Seq, TypeSeq)
                SELECT r.Id, r.Seq, rt.Seq
                FROM [{schema}].[{resources}] r
                INNER JOIN [{schema}].[{resourceTypes}] rt ON rt.Id = r.ResourceTypeId
                WHERE r.ParentId IS NOT NULL;
                EXEC [{schema}].[sp_{closure}_Apply] @RejectMalformed = 0;
                DROP TABLE #SqlOSClosureTargets;
                COMMIT TRANSACTION;
            END
            """;

        var insertTrigger = $"""
            CREATE OR ALTER TRIGGER [{schema}].[TR_{closure}_Insert] ON [{schema}].[{resources}]
            AFTER INSERT
            AS
            BEGIN
                SET NOCOUNT ON;
                IF NOT EXISTS (SELECT 1 FROM inserted WHERE ParentId IS NOT NULL) RETURN;
                {targetsTable}
                INSERT INTO #SqlOSClosureTargets (Id, Seq, TypeSeq)
                SELECT i.Id, i.Seq, rt.Seq
                FROM inserted i
                INNER JOIN [{schema}].[{resourceTypes}] rt ON rt.Id = i.ResourceTypeId
                WHERE i.ParentId IS NOT NULL;
                EXEC [{schema}].[sp_{closure}_Apply];
                DROP TABLE #SqlOSClosureTargets;
            END
            """;

        // The old images of a statement's rows, copied into an indexed temp table so the walks below seek them
        // like the table instead of scanning the trigger's pseudo-table once per probe.
        var oldImages = """
            SELECT Id, ParentId, IsActive, ResourceTypeId, Seq INTO #SqlOSClosureOld FROM deleted;
            CREATE UNIQUE CLUSTERED INDEX IX_SqlOSClosureOld ON #SqlOSClosureOld (Id);
            """;

        // The table as it was before the statement, as far as the walk needs it: the current rows plus the old
        // images. For an UPDATE a changed row appears with both parents, so the walk from a target follows every
        // chain that was or is its ancestor chain; the old chain, with its old activity flags, is among them.
        // Deleting a superset of a target's own pairs is safe: Apply reinserts the target's current pairs
        // afterwards. (The recursive member of a SQL Server CTE may not contain an outer join, so the old image
        // cannot be substituted for the current one in place.) For a DELETE the union is the old table exactly.
        var oldState = $"""
            (SELECT Id, ParentId, IsActive, Seq FROM [{schema}].[{resources}]
             UNION ALL
             SELECT Id, ParentId, IsActive, Seq FROM #SqlOSClosureOld)
            """;

        var updateTrigger = $"""
            CREATE OR ALTER TRIGGER [{schema}].[TR_{closure}_Update] ON [{schema}].[{resources}]
            AFTER UPDATE
            AS
            BEGIN
                SET NOCOUNT ON;
                IF NOT (UPDATE(ParentId) OR UPDATE(IsActive) OR UPDATE(ResourceTypeId)) RETURN;
                IF NOT EXISTS (
                    SELECT 1 FROM inserted i INNER JOIN deleted d ON d.Id = i.Id
                    WHERE ISNULL(i.ParentId, N'') <> ISNULL(d.ParentId, N'')
                       OR i.IsActive <> d.IsActive
                       OR i.ResourceTypeId <> d.ResourceTypeId
                ) RETURN;

                {oldImages}
                -- Every resource whose ancestor chain may have changed: the changed rows and everything beneath them.
                {targetsTable}
                ;WITH changed AS (
                    SELECT i.Id FROM inserted i INNER JOIN #SqlOSClosureOld d ON d.Id = i.Id
                    WHERE ISNULL(i.ParentId, N'') <> ISNULL(d.ParentId, N'')
                       OR i.IsActive <> d.IsActive
                       OR i.ResourceTypeId <> d.ResourceTypeId
                ),
                sub AS (
                    SELECT Id, 0 AS Depth FROM changed
                    UNION ALL
                    SELECT r.Id, s.Depth + 1
                    FROM [{schema}].[{resources}] r
                    INNER JOIN sub s ON r.ParentId = s.Id
                    WHERE s.Depth <= {maxDepth}
                )
                INSERT INTO #SqlOSClosureTargets (Id, Seq, TypeSeq)
                SELECT r.Id, r.Seq, rt.Seq
                FROM (SELECT DISTINCT Id FROM sub) s
                INNER JOIN [{schema}].[{resources}] r ON r.Id = s.Id
                INNER JOIN [{schema}].[{resourceTypes}] rt ON rt.Id = r.ResourceTypeId
                OPTION (MAXRECURSION {ClosureRecursionLimit});

                -- Their pairs as they were before the statement (under their old type), deleted by exact key.
                UPDATE t SET TypeSeq = rt.Seq
                FROM #SqlOSClosureTargets t
                INNER JOIN #SqlOSClosureOld d ON d.Id = t.Id
                INNER JOIN [{schema}].[{resourceTypes}] rt ON rt.Id = d.ResourceTypeId;

                ;WITH {PairsCte(oldState)}
                DELETE c
                FROM [{schema}].[{closure}] c
                INNER JOIN pairs o
                    ON o.AncestorSeq = c.AncestorSeq AND o.TypeSeq = c.TypeSeq AND o.DescendantSeq = c.DescendantSeq
                WHERE o.Active = 1
                OPTION (MAXRECURSION {ClosureRecursionLimit});

                -- Their pairs now.
                UPDATE t SET TypeSeq = rt.Seq
                FROM #SqlOSClosureTargets t
                INNER JOIN [{schema}].[{resources}] r ON r.Id = t.Id
                INNER JOIN [{schema}].[{resourceTypes}] rt ON rt.Id = r.ResourceTypeId;

                EXEC [{schema}].[sp_{closure}_Apply];
                DROP TABLE #SqlOSClosureTargets;
                DROP TABLE #SqlOSClosureOld;
            END
            """;

        var deleteTrigger = $"""
            CREATE OR ALTER TRIGGER [{schema}].[TR_{closure}_Delete] ON [{schema}].[{resources}]
            AFTER DELETE
            AS
            BEGIN
                SET NOCOUNT ON;
                -- Inactive rows and roots hold no pairs.
                IF NOT EXISTS (SELECT 1 FROM deleted WHERE ParentId IS NOT NULL AND IsActive = 1) RETURN;
                {oldImages}
                {targetsTable}
                INSERT INTO #SqlOSClosureTargets (Id, Seq, TypeSeq)
                SELECT d.Id, d.Seq, rt.Seq
                FROM #SqlOSClosureOld d
                INNER JOIN [{schema}].[{resourceTypes}] rt ON rt.Id = d.ResourceTypeId
                WHERE d.ParentId IS NOT NULL;

                ;WITH {PairsCte(oldState)}
                DELETE c
                FROM [{schema}].[{closure}] c
                INNER JOIN pairs o
                    ON o.AncestorSeq = c.AncestorSeq AND o.TypeSeq = c.TypeSeq AND o.DescendantSeq = c.DescendantSeq
                WHERE o.Active = 1
                OPTION (MAXRECURSION {ClosureRecursionLimit});

                DROP TABLE #SqlOSClosureTargets;
                DROP TABLE #SqlOSClosureOld;
            END
            """;

        return [apply, rebuild, insertTrigger, updateTrigger, deleteTrigger];
    }

    /// <summary>Returns a row when the closure is empty although resources with parents exist.</summary>
    public string BuildResourceClosureNeedsBuildSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        var resources = Escape(options.TableNames.Resources);
        var closure = Escape(SqlOSFgaResourceClosure.TableName(options));
        return $"""
            SELECT CASE
                WHEN NOT EXISTS (SELECT 1 FROM [{schema}].[{closure}])
                 AND EXISTS (SELECT 1 FROM [{schema}].[{resources}] WHERE ParentId IS NOT NULL)
                THEN 1 ELSE 0 END AS [Value]
            """;
    }

    public string BuildResourceClosureRebuildSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        var closure = Escape(SqlOSFgaResourceClosure.TableName(options));
        return $"EXEC [{schema}].[sp_{closure}_Rebuild];";
    }

    /// <summary>
    /// One authorized page, as a single composable SELECT: for each access root, the first <c>@PageSize</c>
    /// descendants of the requested type after <c>@Cursor</c> from the closure (one index range each) merged
    /// with the root itself when it is of that type and its own chain is well formed (it has no parent, an
    /// inactive parent, or a closure row under its parent: one point lookup); deduplicated across roots and
    /// cut to <c>@PageSize</c>. Parameters: <c>@SubjectIds</c>, <c>@PermissionId</c>, <c>@ResourceTypeId</c>,
    /// <c>@Cursor</c>, <c>@PageSize</c>.
    /// </summary>
    public string BuildVisibleResourcesPageSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        var resources = Escape(options.TableNames.Resources);
        var resourceTypes = Escape(options.TableNames.ResourceTypes);
        var closure = Escape(SqlOSFgaResourceClosure.TableName(options));
        return $"""
            SELECT TOP (@PageSize) r.Id AS ResourceId, d.Seq
            FROM [{schema}].[{resourceTypes}] rt
            CROSS APPLY (
                SELECT DISTINCT v.Seq
                FROM [{schema}].fn_AccessRoots(@SubjectIds, @PermissionId) a
                CROSS APPLY (
                    SELECT c.DescendantSeq AS Seq
                    FROM (
                        SELECT TOP (@PageSize) c.DescendantSeq
                        FROM [{schema}].[{closure}] c
                        WHERE c.AncestorSeq = a.ResourceSeq AND c.TypeSeq = rt.Seq AND c.DescendantSeq > @Cursor
                        ORDER BY c.DescendantSeq
                    ) c
                    UNION ALL
                    SELECT a.ResourceSeq
                    WHERE a.TypeSeq = rt.Seq AND a.ResourceSeq > @Cursor
                      AND (a.ParentSeq IS NULL OR a.ParentIsActive = 0 OR EXISTS (
                          SELECT 1 FROM [{schema}].[{closure}] s
                          WHERE s.AncestorSeq = a.ParentSeq AND s.TypeSeq = a.TypeSeq AND s.DescendantSeq = a.ResourceSeq))
                ) v
            ) d
            INNER JOIN [{schema}].[{resources}] r ON r.Seq = d.Seq
            WHERE rt.Id = @ResourceTypeId
            ORDER BY d.Seq
            """;
    }

    public string BuildSelectRoutinesHashSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var schema = Escape(options.Schema);
        var closure = SqlOSFgaResourceClosure.TableName(options);
        var triggers = SqlOSFgaResourceClosure.TriggerNames(options.TableNames.Resources);
        string Exists(string name, string type)
            => $"OBJECT_ID(N'{SqlLiteral($"[{schema}].[{Escape(name)}]")}', N'{type}') IS NOT NULL";

        return $"""
            SELECT TOP 1 [RoutinesHash]
            FROM [{schema}].[SqlOSFgaSchema]
            WHERE {Exists("fn_AccessRoots", "IF")}
              AND {Exists("fn_IsResourceAccessible", "IF")}
              AND {Exists($"sp_{closure}_Apply", "P")}
              AND {Exists($"sp_{closure}_Rebuild", "P")}
              AND {Exists(triggers[0], "TR")}
              AND {Exists(triggers[1], "TR")}
              AND {Exists(triggers[2], "TR")}
            """;
    }

    public string BuildStoreRoutinesHashSql(SqlOSFgaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"UPDATE [{Escape(options.Schema)}].[SqlOSFgaSchema] SET [RoutinesHash] = @RoutinesHash";
    }

    private static string SqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}

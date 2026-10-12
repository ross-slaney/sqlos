using SqlOS.Fga.Configuration;

namespace SqlOS.Fga.Indexing;

/// <summary>A column in a database-native page order; directions may be mixed.</summary>
internal sealed record SqlOSFgaNativePageOrder(string Column, bool Descending);

/// <summary>
/// Trusted, model-derived SQL inputs for one authorized page. Table SQL and the aliases are already
/// quoted by the relational provider; the predicate comes from EF translation, with its original parameters.
/// </summary>
internal sealed record SqlOSFgaNativePageSqlSpec
{
    public required string ProjectionTableSql { get; init; }
    public required string ApplicationTableSql { get; init; }
    public required string KeyColumn { get; init; }
    public required string ResourceIdColumn { get; init; }
    public required IReadOnlyList<SqlOSFgaNativePageOrder> Order { get; init; }
    public string ProjectionAliasSql { get; init; } = "s";
    public string ApplicationAliasSql { get; init; } = "a";
    public string? PredicateSql { get; init; }

    /// <summary>
    /// Join the application table inside each limited stream when its predicate needs columns that are not
    /// copied to the scope projection. Otherwise the predicate reads the projection directly.
    /// </summary>
    public bool NeedsApplicationJoin { get; init; }

    public string ProjectionAncestorColumn { get; init; } = "_AncestorKey";
    public string ProjectionTypeColumn { get; init; } = "_TypeKey";
    public string ResourceScopeKeyColumn { get; init; } = "ScopeKey";
    public string ResourceTypeKeyColumn { get; init; } = "TypeKey";
}

/// <summary>
/// Produces one ordinary SQL statement: the first k matching rows under every eligible grant, duplicate
/// elimination, the global first k, and the application rows. All comparisons stay in the database. The
/// maintained projection has one row per (ancestor, application row), so any row in the global first k is
/// also in the first k of every stream containing it. Overlapping grants therefore do not invalidate the
/// per-stream limit. There is no retained request state and no grant collection in application memory.
/// </summary>
internal static class SqlOSFgaNativePageSql
{
    internal const string SubjectsParameter = "@__sqlos_subjects";
    internal const string PermissionParameter = "@__sqlos_permission";
    internal const string TakeParameter = "@__sqlos_take";

    /// <summary>
    /// Builds a composable statement. An EF query composed over this statement must reapply its order to the
    /// outer query: an ORDER BY inside a derived table determines page membership, not an outer SQL order.
    /// </summary>
    internal static string Build(bool postgreSql, SqlOSFgaOptions options, SqlOSFgaNativePageSqlSpec spec)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Order.Count == 0)
        {
            throw new ArgumentException("An authorized page requires an order ending in its unique application key.", nameof(spec));
        }

        if (!string.Equals(spec.Order[^1].Column, spec.KeyColumn, StringComparison.Ordinal))
        {
            throw new ArgumentException("The page order must end in its unique application key.", nameof(spec));
        }

        string Q(string name) => Quote(postgreSql, name);
        var scopeAlias = spec.ProjectionAliasSql;
        var grantName = "__sqlos_grant";
        while (string.Equals(spec.ApplicationAliasSql, Q(grantName), StringComparison.OrdinalIgnoreCase)
            || string.Equals(spec.ApplicationAliasSql, grantName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(scopeAlias, Q(grantName), StringComparison.OrdinalIgnoreCase)
            || string.Equals(scopeAlias, grantName, StringComparison.OrdinalIgnoreCase))
            grantName += "_";
        var grantAlias = Q(grantName);
        var streamColumns = new List<string>
        {
            $"{scopeAlias}.{Q(spec.KeyColumn)} AS {Q("__SqlOS_RowKey")}",
            $"{scopeAlias}.{Q(spec.ResourceIdColumn)} AS {Q("__SqlOS_ResourceId")}",
        };
        streamColumns.AddRange(spec.Order.Select((column, i) => $"{scopeAlias}.{Q(column.Column)} AS {Q("__SqlOS_Order" + i)}"));
        var orderWithin = string.Join(", ", spec.Order.Select(column => $"{scopeAlias}.{Q(column.Column)}" + Direction(column)));
        var candidateColumns = new List<string>
        {
            $"candidate.{Q("__SqlOS_RowKey")}",
            $"candidate.{Q("__SqlOS_ResourceId")}",
        };
        candidateColumns.AddRange(spec.Order.Select((_, i) => $"candidate.{Q("__SqlOS_Order" + i)}"));
        var candidateOrder = string.Join(", ", spec.Order.Select((column, i) => $"candidate.{Q("__SqlOS_Order" + i)}" + Direction(column)));
        var pageOrder = string.Join(", ", spec.Order.Select((column, i) => $"page.{Q("__SqlOS_Order" + i)}" + Direction(column)));
        var applicationJoin = spec.NeedsApplicationJoin
            ? $"\nINNER JOIN {spec.ApplicationTableSql} AS {spec.ApplicationAliasSql} ON {spec.ApplicationAliasSql}.{Q(spec.KeyColumn)} = {scopeAlias}.{Q(spec.KeyColumn)} AND {spec.ApplicationAliasSql}.{Q(spec.ResourceIdColumn)} = {scopeAlias}.{Q(spec.ResourceIdColumn)}"
            : string.Empty;
        var predicate = string.IsNullOrWhiteSpace(spec.PredicateSql) ? string.Empty : $"\n  AND ({spec.PredicateSql})";

        string Stream(bool typed)
        {
            var selectLimit = postgreSql ? string.Empty : $"TOP ({TakeParameter}) ";
            var typeCondition = typed
                ? $"{grantAlias}.{Q("PermissionTypeId")} IS NOT NULL\n  AND {scopeAlias}.{Q(spec.ProjectionTypeColumn)} = {grantAlias}.{Q("PermissionTypeKey")}" 
                : $"{grantAlias}.{Q("PermissionTypeId")} IS NULL";
            var limit = postgreSql ? $"\nLIMIT {TakeParameter}" : string.Empty;
            return $"""
                SELECT {selectLimit}{string.Join(", ", streamColumns)}
                FROM {spec.ProjectionTableSql} AS {scopeAlias}{applicationJoin}
                WHERE {typeCondition}
                  AND {scopeAlias}.{Q(spec.ProjectionAncestorColumn)} = {grantAlias}.{Q("AncestorKey")}{predicate}
                ORDER BY {orderWithin}{limit}
                """;
        }

        var streams = postgreSql
            ? $"CROSS JOIN LATERAL (\n({Stream(false)})\nUNION ALL\n({Stream(true)})\n) AS candidate"
            : $"CROSS APPLY (\nSELECT untyped.* FROM (\n{Stream(false)}\n) AS untyped\nUNION ALL\nSELECT typed.* FROM (\n{Stream(true)}\n) AS typed\n) AS candidate";
        var top = postgreSql ? string.Empty : $"TOP ({TakeParameter}) ";
        var tail = postgreSql ? $"\nLIMIT {TakeParameter}" : string.Empty;
        var eligible = EligibleGrants(postgreSql, options, spec.ResourceScopeKeyColumn, spec.ResourceTypeKeyColumn);
        var page = $"""
            SELECT DISTINCT {top}{string.Join(", ", candidateColumns)}
            FROM (
            {eligible}
            ) AS {grantAlias}
            {streams}
            ORDER BY {candidateOrder}{tail}
            """;
        return $"""
            SELECT {top}app.*
            FROM {spec.ApplicationTableSql} AS app
            INNER JOIN (
            {page}
            ) AS page ON app.{Q(spec.KeyColumn)} = page.{Q("__SqlOS_RowKey")}
                AND app.{Q(spec.ResourceIdColumn)} = page.{Q("__SqlOS_ResourceId")}
            ORDER BY {pageOrder}{tail}
            """;
    }

    /// <summary>
    /// Live grants whose principals and caller satisfy the same existence, activity, expiration, and grant
    /// window checks as fn_IsResourceAccessible. The resolved-principal JSON starts with the original caller.
    /// Resource-path eligibility is represented by the transactionally maintained scope projection.
    /// </summary>
    internal static string EligibleGrants(bool postgreSql, SqlOSFgaOptions options, string resourceScopeKeyColumn = "ScopeKey", string resourceTypeKeyColumn = "TypeKey")
    {
        string Q(string name) => Quote(postgreSql, name);
        string Table(string name) => Q(options.Schema) + "." + Q(name);
        var tables = options.TableNames;
        var now = postgreSql ? "(CURRENT_TIMESTAMP AT TIME ZONE 'UTC')" : "GETUTCDATE()";
        var principalIds = postgreSql
            ? $"SELECT jsonb_array_elements_text({SubjectsParameter}::jsonb)"
            : $"SELECT CONVERT(NVARCHAR(450), [value]) FROM OPENJSON({SubjectsParameter})";
        var callerId = postgreSql ? $"({SubjectsParameter}::jsonb ->> 0)" : $"JSON_VALUE({SubjectsParameter}, '$[0]')";
        var trueLiteral = postgreSql ? "TRUE" : "1";
        string LivePrincipal(string subject, string user, string service, string group, string agent) => $"""
            ({subject}.{Q("SubjectTypeId")} <> 'user' OR {user}.{Q("IsActive")} = {trueLiteral})
            AND ({subject}.{Q("SubjectTypeId")} <> 'service_account' OR ({service}.{Q("SubjectId")} IS NOT NULL AND ({service}.{Q("ExpiresAt")} IS NULL OR {service}.{Q("ExpiresAt")} > {now})))
            AND ({subject}.{Q("SubjectTypeId")} <> 'group' OR {group}.{Q("IsActive")} = {trueLiteral})
            AND ({subject}.{Q("SubjectTypeId")} <> 'agent' OR {agent}.{Q("SubjectId")} IS NOT NULL)
            """;
        return $"""
            SELECT DISTINCT resource.{Q(resourceScopeKeyColumn)} AS {Q("AncestorKey")},
                   permission.{Q("ResourceTypeId")} AS {Q("PermissionTypeId")},
                   resource_type.{Q(resourceTypeKeyColumn)} AS {Q("PermissionTypeKey")}
            FROM {Table(tables.Grants)} AS grant_row
            INNER JOIN {Table(tables.Resources)} AS resource ON resource.{Q("Id")} = grant_row.{Q("ResourceId")}
            INNER JOIN {Table(tables.RolePermissions)} AS role_permission ON role_permission.{Q("RoleId")} = grant_row.{Q("RoleId")}
            INNER JOIN {Table(tables.Permissions)} AS permission ON permission.{Q("Id")} = role_permission.{Q("PermissionId")}
            LEFT JOIN {Table(tables.ResourceTypes)} AS resource_type ON resource_type.{Q("Id")} = permission.{Q("ResourceTypeId")}
            INNER JOIN {Table(tables.Subjects)} AS subject ON subject.{Q("Id")} = grant_row.{Q("SubjectId")}
            LEFT JOIN {Table(tables.Users)} AS subject_user ON subject_user.{Q("SubjectId")} = subject.{Q("Id")}
            LEFT JOIN {Table(tables.ServiceAccounts)} AS subject_service ON subject_service.{Q("SubjectId")} = subject.{Q("Id")}
            LEFT JOIN {Table(tables.UserGroups)} AS subject_group ON subject_group.{Q("SubjectId")} = subject.{Q("Id")}
            LEFT JOIN {Table(tables.Agents)} AS subject_agent ON subject_agent.{Q("SubjectId")} = subject.{Q("Id")}
            WHERE grant_row.{Q("SubjectId")} IN ({principalIds})
              AND role_permission.{Q("PermissionId")} = {PermissionParameter}
              AND {LivePrincipal("subject", "subject_user", "subject_service", "subject_group", "subject_agent")}
              AND EXISTS (
                  SELECT 1
                  FROM {Table(tables.Subjects)} AS caller
                  LEFT JOIN {Table(tables.Users)} AS caller_user ON caller_user.{Q("SubjectId")} = caller.{Q("Id")}
                  LEFT JOIN {Table(tables.ServiceAccounts)} AS caller_service ON caller_service.{Q("SubjectId")} = caller.{Q("Id")}
                  LEFT JOIN {Table(tables.UserGroups)} AS caller_group ON caller_group.{Q("SubjectId")} = caller.{Q("Id")}
                  LEFT JOIN {Table(tables.Agents)} AS caller_agent ON caller_agent.{Q("SubjectId")} = caller.{Q("Id")}
                  WHERE caller.{Q("Id")} = {callerId}
                    AND {LivePrincipal("caller", "caller_user", "caller_service", "caller_group", "caller_agent")}
              )
              AND (grant_row.{Q("EffectiveFrom")} IS NULL OR grant_row.{Q("EffectiveFrom")} <= {now})
              AND (grant_row.{Q("EffectiveTo")} IS NULL OR grant_row.{Q("EffectiveTo")} >= {now})
            """;
    }

    private static string Direction(SqlOSFgaNativePageOrder column) => column.Descending ? " DESC" : " ASC";

    private static string Quote(bool postgreSql, string identifier)
        => postgreSql ? "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
}

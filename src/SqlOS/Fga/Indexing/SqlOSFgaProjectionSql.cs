using System.Security.Cryptography;
using System.Text;
using SqlOS.Database;
using SqlOS.Fga.Configuration;

namespace SqlOS.Fga.Indexing;

/// <summary>Builds only SqlOS-owned projection objects; application columns and indexes are unchanged.</summary>
internal static class SqlOSFgaProjectionSql
{
    internal const string CatalogTable = "SqlOSFgaIndexCatalog";
    internal const string ClosureTable = "SqlOSFgaResourceClosure";
    internal const string GateTable = "SqlOSFgaTopologyGate";

    internal static string BuildCreate(SqlOSFgaScopeEntity entity, SqlOSFgaOptions options, string providerName)
    {
        var sql = new Dialect(providerName);
        var projection = sql.Table(options.Schema, entity.ProjectionTable);
        var definitions = new List<string>
        {
            $"{sql.Quote("_AncestorKey")} bigint NOT NULL",
            $"{sql.Quote("_TypeKey")} bigint NOT NULL",
            $"{sql.Quote("_ResourceKey")} bigint NOT NULL"
        };
        definitions.AddRange(entity.Columns.Select(column =>
            $"{sql.Quote(column.Name)} {column.StoreType}{Collation(sql, column)} {(column.IsNullable ? "NULL" : "NOT NULL")}"));
        definitions.Add($"CONSTRAINT {sql.Quote(ObjectName(entity, options, "pk"))} PRIMARY KEY{(sql.PostgreSql ? "" : " NONCLUSTERED")} ({sql.Quote("_AncestorKey")}, {sql.Quote(entity.Key.Name)})");
        var result = new StringBuilder();
        var bindingResource = BindingResource(entity);
        var bindings = sql.Table(options.Schema, BindingTable(entity));
        result.AppendLine($"""
            CREATE TABLE {bindings} (
                {sql.Quote("_ApplicationKey")} {entity.Key.StoreType}{Collation(sql, entity.Key)} NOT NULL,
                {sql.Quote("_ResourceId")} {bindingResource.StoreType}{Collation(sql, bindingResource)} {(entity.ResourceId.IsNullable ? "NULL" : "NOT NULL")},
                CONSTRAINT {sql.Quote(ObjectName(entity, options, "bindings-pk"))} PRIMARY KEY{(sql.PostgreSql ? "" : " NONCLUSTERED")} ({sql.Quote("_ApplicationKey")})
            );
            CREATE INDEX {sql.Quote(ObjectName(entity, options, "bindings-resource"))} ON {bindings} ({sql.Quote("_ResourceId")});
            """);
        result.AppendLine($"CREATE TABLE {projection} (\n    {string.Join(",\n    ", definitions)}\n);");
        result.AppendLine($"CREATE INDEX {sql.Quote(ObjectName(entity, options, "application-key"))} ON {projection} ({sql.Quote(entity.Key.Name)});");
        result.AppendLine($"CREATE INDEX {sql.Quote(ObjectName(entity, options, "resource"))} ON {projection} ({sql.Quote("_ResourceKey")});");
        foreach (var order in entity.Orders)
        {
            var orderedColumns = string.Join(", ", order.Columns.Select(column =>
                $"{sql.Quote(column.Column.Name)} {(column.Descending ? "DESC" : "ASC")}"));
            // The primary key already is the default ancestor/key order. Its reverse scan covers DESC.
            if (order.Columns.Count != 1 || order.Columns[0].Column.Name != entity.Key.Name)
            {
                result.AppendLine($"CREATE INDEX {sql.Quote(ObjectName(entity, options, "order:" + order.Name))} ON {projection} ({sql.Quote("_AncestorKey")}, {orderedColumns});");
            }

            result.AppendLine($"CREATE INDEX {sql.Quote(ObjectName(entity, options, "typed-order:" + order.Name))} ON {projection} ({sql.Quote("_AncestorKey")}, {sql.Quote("_TypeKey")}, {orderedColumns});");
        }

        return result.ToString();
    }

    internal static string BuildDrop(SqlOSFgaScopeEntity entity, SqlOSFgaOptions options, string providerName)
    {
        var sql = new Dialect(providerName);
        var projection = sql.Table(options.Schema, entity.ProjectionTable);
        var bindings = sql.Table(options.Schema, BindingTable(entity));
        var syncName = ObjectName(entity, options, "sync");
        if (!sql.PostgreSql)
        {
            // A source table can have moved schemas since the callback was registered.
            return $"""
                DECLARE @sqlosDropTrigger nvarchar(max);
                SELECT @sqlosDropTrigger = STRING_AGG(CONVERT(nvarchar(max), N'DROP TRIGGER ' + QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name) + N';'), NCHAR(10))
                FROM sys.triggers AS t INNER JOIN sys.objects AS o ON o.object_id = t.object_id
                WHERE t.parent_class = 1 AND o.name = {sql.Literal(syncName)};
                IF @sqlosDropTrigger IS NOT NULL EXEC sys.sp_executesql @sqlosDropTrigger;
                DROP TABLE IF EXISTS {projection};
                DROP TABLE IF EXISTS {bindings};
                """;
        }

        var gateName = ObjectName(entity, options, "gate");
        return $"""
            DROP FUNCTION IF EXISTS {sql.Table(options.Schema, syncName)}() CASCADE;
            DROP FUNCTION IF EXISTS {sql.Table(options.Schema, gateName)}() CASCADE;
            DROP TABLE IF EXISTS {projection};
            DROP TABLE IF EXISTS {bindings};
            """;
    }

    /// <summary>Seeds the application binding map once when its model changes, before closure callbacks run.</summary>
    internal static string BuildRebuildBindings(SqlOSFgaScopeEntity entity, SqlOSFgaOptions options, string providerName)
    {
        var sql = new Dialect(providerName);
        var source = sql.Table(entity.SourceSchema, entity.SourceTable);
        var bindings = sql.Table(options.Schema, BindingTable(entity));
        var id = $"app.{sql.Quote(entity.ResourceId.Name)}";
        var validation = sql.PostgreSql
            ? $"DO $sqlos_binding_bound$ BEGIN IF EXISTS (SELECT 1 FROM {source} AS app WHERE char_length({id}) > 450) THEN RAISE EXCEPTION 'Application ResourceId exceeds the 450-character SqlOS identifier limit.' USING ERRCODE = '22001'; END IF; END $sqlos_binding_bound$;"
            : $"IF EXISTS (SELECT 1 FROM {source} AS app WITH (READCOMMITTEDLOCK) WHERE DATALENGTH(CONVERT(nvarchar(max), {id})) > 900) THROW 51000, 'Application ResourceId exceeds the 450-character SqlOS identifier limit.', 1;";
        var conversionValidation = sql.PostgreSql ? "" : $"""
            IF EXISTS (
                SELECT 1 FROM {source} AS app WITH (READCOMMITTEDLOCK)
                INNER JOIN {bindings} AS binding WITH (READCOMMITTEDLOCK) ON binding.[_ApplicationKey] = app.{sql.Quote(entity.Key.Name)}
                WHERE CONVERT(varbinary(max), CONVERT(nvarchar(max), {id})) <> CONVERT(varbinary(max), CONVERT(nvarchar(max), binding.[_ResourceId]))
            ) THROW 51000, 'Application ResourceId cannot be represented by the SqlOS resource identifier type without changing its value.', 1;
            """;
        return $"""
            {validation}
            TRUNCATE TABLE {bindings};
            INSERT INTO {bindings} ({sql.Quote("_ApplicationKey")}, {sql.Quote("_ResourceId")})
            SELECT app.{sql.Quote(entity.Key.Name)}, {id} FROM {source} AS app{sql.ReadCurrent};
            {conversionValidation}
            """;
    }

    /// <summary>Initializes a new or structurally changed projection. Never run for an unchanged model hash.</summary>
    internal static string BuildRebuild(SqlOSFgaScopeEntity entity, SqlOSFgaOptions options, string providerName)
    {
        var sql = new Dialect(providerName);
        return $"TRUNCATE TABLE {sql.Table(options.Schema, entity.ProjectionTable)};\n" + BuildInsert(entity, options, sql, false);
    }

    /// <summary>Resource-trigger callback. The closure owner creates the affected-resource temporary relation.</summary>
    internal static string BuildRefresh(SqlOSFgaScopeEntity entity, SqlOSFgaOptions options, string providerName)
    {
        var sql = new Dialect(providerName);
        var projection = sql.Table(options.Schema, entity.ProjectionTable);
        var affected = sql.PostgreSql ? "pg_temp.sqlos_fga_affected_resources" : "#SqlOSFgaAffectedResources";
        var key = sql.PostgreSql ? "scope_key" : "[ScopeKey]";
        var delete = sql.PostgreSql
            ? $"DELETE FROM {projection} AS p WHERE EXISTS (SELECT 1 FROM {affected} AS affected WHERE affected.{key} = p.\"_ResourceKey\");"
            : $"DELETE p FROM {projection} AS p WITH (READCOMMITTEDLOCK) WHERE EXISTS (SELECT 1 FROM {affected} AS affected WHERE affected.{key} = p.[_ResourceKey]);";
        return delete + "\n" + BuildInsert(entity, options, sql, true);
    }

    internal static string BuildCatalogRegistration(SqlOSFgaScopeEntity entity, SqlOSFgaOptions options, string providerName)
    {
        var sql = new Dialect(providerName);
        var catalog = sql.Table(options.Schema, CatalogTable);
        var id = sql.Literal(entity.ProjectionTable);
        var hash = sql.Literal(entity.ModelHash);
        var refresh = sql.Literal(BuildRefresh(entity, options, providerName));
        var drop = sql.Literal(BuildDrop(entity, options, providerName));
        if (sql.PostgreSql)
        {
            return $"""
                INSERT INTO {catalog} ("Id", "ModelHash", "RefreshSql", "DropSql") VALUES ({id}, {hash}, {refresh}, {drop})
                ON CONFLICT ("Id") DO UPDATE SET "ModelHash" = EXCLUDED."ModelHash", "RefreshSql" = EXCLUDED."RefreshSql", "DropSql" = EXCLUDED."DropSql";
                """;
        }

        return $"""
            UPDATE {catalog} SET [ModelHash] = {hash}, [RefreshSql] = {refresh}, [DropSql] = {drop} WHERE [Id] = {id};
            IF @@ROWCOUNT = 0 INSERT INTO {catalog} ([Id], [ModelHash], [RefreshSql], [DropSql]) VALUES ({id}, {hash}, {refresh}, {drop});
            """;
    }

    internal static IReadOnlyList<string> BuildTriggers(SqlOSFgaScopeEntity entity, SqlOSFgaOptions options, string providerName)
    {
        var sql = new Dialect(providerName);
        return sql.PostgreSql ? BuildPostgreSqlTriggers(entity, options, sql) : [BuildSqlServerTrigger(entity, options, sql)];
    }

    private static string BuildInsert(SqlOSFgaScopeEntity entity, SqlOSFgaOptions options, Dialect sql, bool affectedOnly)
    {
        var source = sql.Table(entity.SourceSchema, entity.SourceTable);
        var bindings = sql.Table(options.Schema, BindingTable(entity));
        var resources = sql.Table(options.Schema, options.TableNames.Resources);
        var types = sql.Table(options.Schema, options.TableNames.ResourceTypes);
        var closure = sql.Table(options.Schema, ClosureTable);
        var affectedJoin = !affectedOnly ? "" : sql.PostgreSql
            ? "INNER JOIN pg_temp.sqlos_fga_affected_resources AS affected ON affected.scope_key = r.\"ScopeKey\""
            : "INNER JOIN #SqlOSFgaAffectedResources AS affected ON affected.[ScopeKey] = r.[ScopeKey]";
        return $"""
            INSERT INTO {sql.Table(options.Schema, entity.ProjectionTable)} ({ProjectionColumns(entity, sql)})
            SELECT c.{sql.Quote("AncestorKey")}, t.{sql.Quote("TypeKey")}, r.{sql.Quote("ScopeKey")}, {string.Join(", ", entity.Columns.Select(column => $"app.{sql.Quote(column.Name)}"))}
            FROM {bindings} AS binding{sql.ReadCurrent}
            INNER JOIN {source} AS app{sql.ReadCurrent} ON app.{sql.Quote(entity.Key.Name)} = binding.{sql.Quote("_ApplicationKey")}
            INNER JOIN {resources} AS r{sql.ReadCurrent} ON r.{sql.Quote("Id")} = binding.{sql.Quote("_ResourceId")}
            {affectedJoin}
            INNER JOIN {closure} AS c{sql.ReadCurrent} ON c.{sql.Quote("DescendantKey")} = r.{sql.Quote("ScopeKey")}
            INNER JOIN {types} AS t{sql.ReadCurrent} ON t.{sql.Quote("Id")} = r.{sql.Quote("ResourceTypeId")};
            """;
    }

    private static string BuildSqlServerTrigger(SqlOSFgaScopeEntity entity, SqlOSFgaOptions options, Dialect sql)
    {
        var projection = sql.Table(options.Schema, entity.ProjectionTable);
        var bindings = sql.Table(options.Schema, BindingTable(entity));
        var resources = sql.Table(options.Schema, options.TableNames.Resources);
        var types = sql.Table(options.Schema, options.TableNames.ResourceTypes);
        var closure = sql.Table(options.Schema, ClosureTable);
        return $"""
            CREATE OR ALTER TRIGGER {sql.Table(entity.SourceSchema, ObjectName(entity, options, "sync"))}
            ON {sql.Table(entity.SourceSchema, entity.SourceTable)} AFTER INSERT, UPDATE, DELETE
            AS
            BEGIN
                SET NOCOUNT ON;
                DECLARE @sqlosTopologyVersion bigint;
                SELECT @sqlosTopologyVersion = [Version]
                FROM {sql.Table(options.Schema, GateTable)} WITH (HOLDLOCK, READCOMMITTEDLOCK) WHERE [Id] = 1;
                IF @sqlosTopologyVersion IS NULL THROW 51000, 'The SqlOS resource topology gate is missing.', 1;
                IF EXISTS (SELECT 1 FROM inserted WHERE DATALENGTH(CONVERT(nvarchar(max), {sql.Quote(entity.ResourceId.Name)})) > 900)
                    THROW 51000, 'Application ResourceId exceeds the 450-character SqlOS identifier limit.', 1;
                DELETE binding FROM {bindings} AS binding WITH (READCOMMITTEDLOCK)
                INNER JOIN deleted AS old_row ON old_row.{sql.Quote(entity.Key.Name)} = binding.[_ApplicationKey];
                DELETE binding FROM {bindings} AS binding WITH (READCOMMITTEDLOCK)
                INNER JOIN inserted AS new_row ON new_row.{sql.Quote(entity.Key.Name)} = binding.[_ApplicationKey];
                INSERT INTO {bindings} ([_ApplicationKey], [_ResourceId])
                SELECT {sql.Quote(entity.Key.Name)}, {sql.Quote(entity.ResourceId.Name)} FROM inserted;
                IF EXISTS (
                    SELECT 1 FROM inserted AS new_row
                    INNER JOIN {bindings} AS binding WITH (READCOMMITTEDLOCK) ON binding.[_ApplicationKey] = new_row.{sql.Quote(entity.Key.Name)}
                    WHERE CONVERT(varbinary(max), CONVERT(nvarchar(max), new_row.{sql.Quote(entity.ResourceId.Name)})) <> CONVERT(varbinary(max), CONVERT(nvarchar(max), binding.[_ResourceId]))
                ) THROW 51000, 'Application ResourceId cannot be represented by the SqlOS resource identifier type without changing its value.', 1;
                IF EXISTS (
                    SELECT 1 FROM inserted AS new_row
                    INNER JOIN {bindings} AS binding WITH (READCOMMITTEDLOCK) ON binding.[_ApplicationKey] = new_row.{sql.Quote(entity.Key.Name)}
                    LEFT JOIN {resources} AS r WITH (READCOMMITTEDLOCK) ON r.[Id] = binding.[_ResourceId]
                    LEFT JOIN {types} AS t WITH (READCOMMITTEDLOCK) ON t.[Id] = r.[ResourceTypeId]
                    WHERE binding.[_ResourceId] IS NOT NULL AND (r.[Id] IS NULL OR t.[Id] IS NULL)
                ) THROW 51000, 'Application ResourceId must reference an existing SqlOS resource.', 1;
                -- Copy inserted values even when the SET list only names an unprojected input
                -- of a projected computed column. UPDATE(computed_column) is not supported.
                DELETE p FROM {projection} AS p WITH (READCOMMITTEDLOCK)
                INNER JOIN deleted AS old_row ON old_row.{sql.Quote(entity.Key.Name)} = p.{sql.Quote(entity.Key.Name)};
                DELETE p FROM {projection} AS p WITH (READCOMMITTEDLOCK)
                INNER JOIN inserted AS new_row ON new_row.{sql.Quote(entity.Key.Name)} = p.{sql.Quote(entity.Key.Name)};
                INSERT INTO {projection} ({ProjectionColumns(entity, sql)})
                SELECT c.[AncestorKey], t.[TypeKey], r.[ScopeKey], {string.Join(", ", entity.Columns.Select(column => $"new_row.{sql.Quote(column.Name)}"))}
                FROM inserted AS new_row
                INNER JOIN {bindings} AS binding WITH (READCOMMITTEDLOCK) ON binding.[_ApplicationKey] = new_row.{sql.Quote(entity.Key.Name)}
                INNER JOIN {resources} AS r WITH (READCOMMITTEDLOCK) ON r.[Id] = binding.[_ResourceId]
                INNER JOIN {closure} AS c WITH (READCOMMITTEDLOCK) ON c.[DescendantKey] = r.[ScopeKey]
                INNER JOIN {types} AS t WITH (READCOMMITTEDLOCK) ON t.[Id] = r.[ResourceTypeId];
            END;
            """;
    }

    private static IReadOnlyList<string> BuildPostgreSqlTriggers(SqlOSFgaScopeEntity entity, SqlOSFgaOptions options, Dialect sql)
    {
        var source = sql.Table(entity.SourceSchema, entity.SourceTable);
        var projection = sql.Table(options.Schema, entity.ProjectionTable);
        var bindings = sql.Table(options.Schema, BindingTable(entity));
        var resources = sql.Table(options.Schema, options.TableNames.Resources);
        var types = sql.Table(options.Schema, options.TableNames.ResourceTypes);
        var closure = sql.Table(options.Schema, ClosureTable);
        var gateName = ObjectName(entity, options, "gate");
        var syncName = ObjectName(entity, options, "sync");
        var gate = $"""
            CREATE OR REPLACE FUNCTION {sql.Table(options.Schema, gateName)}() RETURNS trigger LANGUAGE plpgsql AS $sqlos_gate$
            BEGIN
                PERFORM "Version" FROM {sql.Table(options.Schema, GateTable)} WHERE "Id" = 1 FOR SHARE;
                IF NOT FOUND THEN RAISE EXCEPTION 'The SqlOS resource topology gate is missing.'; END IF;
                RETURN NULL;
            END;
            $sqlos_gate$;
            DROP TRIGGER IF EXISTS {sql.Quote(gateName)} ON {source};
            CREATE TRIGGER {sql.Quote(gateName)} BEFORE INSERT OR UPDATE OR DELETE ON {source}
            FOR EACH STATEMENT EXECUTE FUNCTION {sql.Table(options.Schema, gateName)}();
            """;
        var sync = $"""
            CREATE OR REPLACE FUNCTION {sql.Table(options.Schema, syncName)}() RETURNS trigger LANGUAGE plpgsql AS $sqlos_projection$
            DECLARE
                old_resource_id text;
                new_resource_id text;
                resource_key bigint;
            BEGIN
                IF TG_OP <> 'INSERT' THEN old_resource_id := OLD.{sql.Quote(entity.ResourceId.Name)}; END IF;
                IF TG_OP <> 'DELETE' THEN new_resource_id := NEW.{sql.Quote(entity.ResourceId.Name)}; END IF;
                IF char_length(new_resource_id) > 450 THEN
                    RAISE EXCEPTION 'Application ResourceId exceeds the 450-character SqlOS identifier limit.' USING ERRCODE = '22001';
                END IF;
                IF old_resource_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM {resources} WHERE "Id" = old_resource_id) THEN
                    IF current_setting('transaction_isolation') IN ('repeatable read', 'serializable') THEN
                        RAISE EXCEPTION 'The prior SqlOS resource is not visible; retry the transaction.' USING ERRCODE = '40001';
                    END IF;
                    -- An orphan has no resource marker. Only this cleanup path updates the gate
                    -- so an older resource-creation snapshot cannot resurrect its removed binding.
                    UPDATE {sql.Table(options.Schema, GateTable)} SET "Version" = "Version" + 1 WHERE "Id" = 1;
                END IF;
                -- A resource marker makes a prior REPEATABLE READ snapshot conflict with topology maintenance.
                -- Lock the old/new resource rows in key order before modifying projection rows.
                FOR resource_key IN
                    SELECT "ScopeKey" FROM {resources}
                    WHERE "Id" = old_resource_id OR "Id" = new_resource_id ORDER BY "ScopeKey"
                LOOP
                    UPDATE {resources} SET "ProjectionVersion" = "ProjectionVersion" + 1 WHERE "ScopeKey" = resource_key;
                END LOOP;
                IF new_resource_id IS NOT NULL AND NOT EXISTS (
                    SELECT 1 FROM {resources} AS r
                    INNER JOIN {types} AS t ON t."Id" = r."ResourceTypeId" WHERE r."Id" = new_resource_id
                ) THEN
                    IF current_setting('transaction_isolation') IN ('repeatable read', 'serializable') THEN
                        RAISE EXCEPTION 'The backing SqlOS resource is not visible; retry the transaction.' USING ERRCODE = '40001';
                    END IF;
                    RAISE EXCEPTION 'Application ResourceId must reference an existing SqlOS resource.' USING ERRCODE = '23503';
                END IF;
                IF TG_OP <> 'INSERT' THEN
                    DELETE FROM {bindings} WHERE "_ApplicationKey" = OLD.{sql.Quote(entity.Key.Name)};
                    DELETE FROM {projection} WHERE {sql.Quote(entity.Key.Name)} = OLD.{sql.Quote(entity.Key.Name)};
                END IF;
                IF TG_OP <> 'DELETE' THEN
                    DELETE FROM {bindings} WHERE "_ApplicationKey" = NEW.{sql.Quote(entity.Key.Name)};
                    INSERT INTO {bindings} ("_ApplicationKey", "_ResourceId") VALUES (NEW.{sql.Quote(entity.Key.Name)}, new_resource_id);
                    DELETE FROM {projection} WHERE {sql.Quote(entity.Key.Name)} = NEW.{sql.Quote(entity.Key.Name)};
                    INSERT INTO {projection} ({ProjectionColumns(entity, sql)})
                    SELECT c."AncestorKey", t."TypeKey", r."ScopeKey", {string.Join(", ", entity.Columns.Select(column => $"NEW.{sql.Quote(column.Name)}"))}
                    FROM {resources} AS r
                    INNER JOIN {closure} AS c ON c."DescendantKey" = r."ScopeKey"
                    INNER JOIN {types} AS t ON t."Id" = r."ResourceTypeId"
                    WHERE r."Id" = new_resource_id;
                END IF;
                RETURN NULL;
            END;
            $sqlos_projection$;
            DROP TRIGGER IF EXISTS {sql.Quote(syncName)} ON {source};
            CREATE TRIGGER {sql.Quote(syncName)} AFTER INSERT OR UPDATE OR DELETE ON {source}
            FOR EACH ROW EXECUTE FUNCTION {sql.Table(options.Schema, syncName)}();
            """;
        return [gate, sync];
    }

    private static string ProjectionColumns(SqlOSFgaScopeEntity entity, Dialect sql)
        => string.Join(", ", new[] { "_AncestorKey", "_TypeKey", "_ResourceKey" }.Concat(entity.Columns.Select(column => column.Name)).Select(sql.Quote));

    private static string BindingTable(SqlOSFgaScopeEntity entity) => entity.ProjectionTable + "_Bindings";

    private static SqlOSFgaScopeColumn BindingResource(SqlOSFgaScopeEntity entity)
        => entity.BindingResourceId ?? throw new InvalidOperationException("Resolve the physical SqlOS resource identifier metadata before creating a native scope projection.");

    private static string Collation(Dialect sql, SqlOSFgaScopeColumn column)
        => column.Collation is null ? "" : " COLLATE " + (sql.PostgreSql ? column.Collation : sql.Quote(column.Collation));

    private static string ObjectName(SqlOSFgaScopeEntity entity, SqlOSFgaOptions options, string purpose)
        => "SqlOSFga_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.Schema + "\0" + entity.ProjectionTable + "\0" + purpose)))[..40].ToLowerInvariant();

    private sealed class Dialect(string providerName)
    {
        internal bool PostgreSql { get; } = SqlOSDatabase.IsPostgreSql(providerName);
        internal string ReadCurrent => PostgreSql ? "" : " WITH (READCOMMITTEDLOCK)";
        internal string Quote(string value) => PostgreSql ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";
        internal string Table(string schema, string table) => Quote(schema) + "." + Quote(table);
        internal string Literal(string value) => (PostgreSql ? "" : "N") + "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}

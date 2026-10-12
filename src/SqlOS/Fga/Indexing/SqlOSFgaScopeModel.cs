using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Indexing;

internal sealed record SqlOSFgaScopeDiscovery(
    IReadOnlyList<SqlOSFgaScopeEntity> Entities,
    IReadOnlyList<string> Skipped);

internal sealed record SqlOSFgaScopeEntity(
    IEntityType EntityType,
    string SourceSchema,
    string SourceTable,
    string ProjectionTable,
    SqlOSFgaScopeColumn Key,
    SqlOSFgaScopeColumn ResourceId,
    IReadOnlyList<SqlOSFgaScopeColumn> Columns,
    IReadOnlyList<SqlOSFgaScopeOrder> Orders,
    string ModelHash)
{
    internal SqlOSFgaScopeColumn? BindingResourceId { get; init; }
}

internal sealed record SqlOSFgaScopeColumn(
    string PropertyName,
    string Name,
    string StoreType,
    string? Collation,
    bool IsNullable,
    int? MaxLengthBytes);

internal sealed record SqlOSFgaScopeOrder(string Name, IReadOnlyList<SqlOSFgaScopeOrderColumn> Columns);
internal sealed record SqlOSFgaScopeOrderColumn(SqlOSFgaScopeColumn Column, bool Descending);

/// <summary>
/// Discovers only ordinary, independently mapped application tables. Catalog resolution reads metadata,
/// never application rows, and preserves the source's actual type, width, nullability, and collation.
/// </summary>
internal static class SqlOSFgaScopeModel
{
    // Bump whenever generated DDL semantics change so persisted callbacks and triggers are rebuilt.
    private const string FormatVersion = "native-scope-5";
    private const string PrimaryOrder = "PrimaryKey";
    private static readonly HashSet<string> ReservedColumns = new(StringComparer.OrdinalIgnoreCase)
        { "_AncestorKey", "_TypeKey", "_ResourceKey" };

    internal static SqlOSFgaScopeDiscovery Discover(IModel model, string providerName)
        => DiscoverCore(model, providerName, validateOrders: true);

    private static SqlOSFgaScopeDiscovery DiscoverCore(IModel model, string providerName, bool validateOrders)
    {
        var entities = new List<SqlOSFgaScopeEntity>();
        var skipped = new List<string>();
        var postgreSql = IsPostgreSql(providerName);
        if (!postgreSql && !providerName.Contains("SqlServer", StringComparison.Ordinal))
            return new(entities, [$"Native scope indexes do not support provider '{providerName}'."]);

        foreach (var entity in model.GetEntityTypes().Where(e => typeof(IHasResourceId).IsAssignableFrom(e.ClrType)).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            void Skip(string reason) => skipped.Add($"{entity.Name}: {reason}");
            var table = entity.GetTableName();
            var schema = entity.GetSchema();
            var key = entity.FindPrimaryKey();
            if (table == null || entity.GetViewName() != null || entity.GetSqlQuery() != null)
            {
                Skip("requires an ordinary table mapping, not a view or SQL query.");
                continue;
            }
            if (entity.BaseType != null || entity.GetDerivedTypes().Any() || entity.IsOwned()
                || entity.GetMappingFragments(StoreObjectType.Table).Any()
                || model.GetEntityTypes().Any(other => other != entity && other.GetTableName() == table && other.GetSchema() == schema))
            {
                Skip("inheritance, owned types, table splitting, and entity splitting are not supported.");
                continue;
            }
            if (key == null || key.Properties.Count != 1)
            {
                Skip("requires a single-column primary key.");
                continue;
            }
            var resource = entity.FindProperty(nameof(IHasResourceId.ResourceId));
            if (resource == null || resource.ClrType != typeof(string) || resource.GetTypeMapping().Converter != null)
            {
                Skip("ResourceId must be a mapped string property without a value converter.");
                continue;
            }
            var store = StoreObjectIdentifier.Table(table, schema);
            var selectedProperties = key.Properties.Concat([resource]).Concat(entity.GetIndexes().SelectMany(i => i.Properties)).Distinct().ToArray();
            var columns = new Dictionary<IProperty, SqlOSFgaScopeColumn>();
            string? failure = null;
            foreach (var property in selectedProperties)
            {
                var name = property.GetColumnName(store);
                var type = property.GetColumnType(store);
                if (name == null || string.IsNullOrWhiteSpace(type))
                {
                    failure = $"column/type mapping for '{property.Name}' is unavailable.";
                    break;
                }
                if (ReservedColumns.Contains(name))
                {
                    failure = $"mapped column '{name}' uses a reserved scope-index name.";
                    break;
                }
                if (columns.Values.Any(c => string.Equals(c.Name, name, postgreSql ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)))
                {
                    failure = $"multiple mapped properties share column '{name}'.";
                    break;
                }
                columns.Add(property, new(property.Name, name, type, property.GetCollation(store), property.IsColumnNullable(store), Width(type, postgreSql)));
            }
            if (failure != null)
            {
                Skip(failure);
                continue;
            }
            var keyColumn = columns[key.Properties[0]];
            var orders = new List<SqlOSFgaScopeOrder> { new(PrimaryOrder, [new(keyColumn, false)]) };
            foreach (var index in entity.GetIndexes().OrderBy(i => i.GetDatabaseName(), StringComparer.Ordinal))
            {
                var directions = index.IsDescending;
                var orderColumns = index.Properties.Select((p, i) => new SqlOSFgaScopeOrderColumn(columns[p], directions != null && (directions.Count == 0 || directions[i]))).ToList();
                if (!index.Properties.Contains(key.Properties[0]))
                    orderColumns.Add(new(keyColumn, false));
                orders.Add(new(index.GetDatabaseName() ?? index.Name ?? string.Join("_", index.Properties.Select(p => p.Name)), orderColumns));
            }
            var sourceSchema = schema ?? (postgreSql ? "public" : "dbo");
            var candidate = new SqlOSFgaScopeEntity(entity, sourceSchema, table, ProjectionName(sourceSchema, table), keyColumn, columns[resource], columns.Values.ToArray(), orders, "");
            if (validateOrders)
                candidate = Validate(candidate, postgreSql, 8192, skipped);
            if (candidate != null)
                entities.Add(candidate with { ModelHash = Hash(candidate, providerName, "model") });
        }
        return new(entities, skipped);
    }

    internal static async Task<SqlOSFgaScopeDiscovery> ResolveAsync(DbContext context, SqlOSFgaOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        var providerName = context.Database.ProviderName ?? "";
        // EF's optimized runtime model omits index directions and relational collation annotations.
        var discovery = DiscoverCore(context.GetService<IDesignTimeModel>().Model, providerName, validateOrders: false);
        var skipped = discovery.Skipped.ToList();
        var entities = new List<SqlOSFgaScopeEntity>();
        if (discovery.Entities.Count == 0)
            return discovery;
        var postgreSql = IsPostgreSql(providerName);
        var connection = context.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen)
            await connection.OpenAsync(cancellationToken);
        try
        {
            var resourceTableSql = Quote(options.Schema, postgreSql) + "." + Quote(options.TableNames.Resources, postgreSql);
            var resourceCatalog = await ReadColumnsAsync(context, resourceTableSql, postgreSql, cancellationToken, "Id");
            if (resourceCatalog.Failure != null || !resourceCatalog.Columns.TryGetValue("Id", out var bindingId)
                || !bindingId.Supported || bindingId.Width == null || bindingId.IsNullable)
            {
                var reason = resourceCatalog.Failure ?? "the configured FGA Resources.Id requires a non-nullable, bounded, supported database type.";
                skipped.AddRange(discovery.Entities.Select(entity => $"{entity.EntityType.Name}: {reason}"));
                return new(entities, skipped);
            }
            foreach (var entity in discovery.Entities)
            {
                var catalog = await ReadColumnsAsync(context, entity, postgreSql, cancellationToken);
                if (catalog.Columns.Count == 0 || catalog.Failure != null)
                {
                    skipped.Add($"{entity.EntityType.Name}: {catalog.Failure ?? "source table was not found in the database catalog."}");
                    continue;
                }
                var resolved = new Dictionary<string, SqlOSFgaScopeColumn>(StringComparer.Ordinal);
                var failure = default(string);
                foreach (var column in entity.Columns)
                {
                    if (!catalog.Columns.TryGetValue(column.Name, out var actual))
                    {
                        failure = $"mapped source column '{column.Name}' was not found in the database catalog.";
                        break;
                    }
                    resolved.Add(column.Name, column with { StoreType = actual.StoreType, Collation = actual.Collation, IsNullable = actual.IsNullable, MaxLengthBytes = actual.Width });
                }
                if (failure != null)
                {
                    skipped.Add($"{entity.EntityType.Name}: {failure}");
                    continue;
                }
                var candidate = entity with
                {
                    SourceSchema = catalog.Schema!,
                    ProjectionTable = ProjectionName(catalog.Schema!, entity.SourceTable),
                    Key = resolved[entity.Key.Name],
                    ResourceId = resolved[entity.ResourceId.Name],
                    BindingResourceId = new(entity.ResourceId.PropertyName, "_ResourceId", bindingId.StoreType, bindingId.Collation, resolved[entity.ResourceId.Name].IsNullable, bindingId.Width),
                    Columns = resolved.Values.ToArray(),
                    Orders = entity.Orders.Select(o => o with { Columns = o.Columns.Select(c => c with { Column = resolved[c.Column.Name] }).ToArray() }).ToArray()
                };
                var unsupported = catalog.Columns.Where(c => !c.Value.Supported).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
                if (unsupported.Contains(candidate.Key.Name) || unsupported.Contains(candidate.ResourceId.Name))
                {
                    skipped.Add($"{entity.EntityType.Name}: the primary key or ResourceId has an unsupported database type.");
                    continue;
                }
                var supportedOrders = new List<SqlOSFgaScopeOrder>();
                foreach (var order in candidate.Orders)
                {
                    if (order.Columns.Any(c => unsupported.Contains(c.Column.Name)))
                        skipped.Add($"{entity.EntityType.Name}, order '{order.Name}': contains an unsupported database type.");
                    else
                        supportedOrders.Add(order);
                }
                candidate = Validate(candidate with { Orders = supportedOrders }, postgreSql, catalog.BlockSize, skipped);
                if (candidate != null)
                {
                    var copiedNames = candidate.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
                    var signature = JsonSerializer.Serialize(new
                    {
                        FgaSchema = options.Schema,
                        Resources = options.TableNames.Resources,
                        ResourceTypes = options.TableNames.ResourceTypes,
                        Columns = catalog.Signature.Where(c => copiedNames.Contains(c.Key)).OrderBy(c => c.Key, StringComparer.Ordinal)
                    });
                    entities.Add(candidate with { ModelHash = Hash(candidate, providerName, signature) });
                }
            }
        }
        finally
        {
            if (!wasOpen)
                await connection.CloseAsync();
        }
        return new(entities, skipped);
    }

    private static SqlOSFgaScopeEntity? Validate(SqlOSFgaScopeEntity entity, bool postgreSql, int blockSize, List<string> skipped)
    {
        if (entity.Key.IsNullable || entity.Key.MaxLengthBytes == null)
        {
            skipped.Add($"{entity.EntityType.Name}: the primary key requires a non-nullable, bounded indexable scalar database type.");
            return null;
        }
        var orders = new List<SqlOSFgaScopeOrder>();
        foreach (var order in entity.Orders)
        {
            var width = order.Columns.Any(c => c.Column.MaxLengthBytes == null) ? (long?)null : order.Columns.Sum(c => (long)c.Column.MaxLengthBytes!.Value);
            string? reason = null;
            if (width == null)
                reason = "requires bounded scalar index keys; an order column is unbounded or not supported.";
            else if (order.Columns.Count + 2 > 32)
                reason = "exceeds the database's 32-column index-key limit after scope keys are added.";
            else if (!postgreSql && (width + 16 > 1700 || entity.Key.MaxLengthBytes + 8 > 1700))
                reason = "exceeds SQL Server's 1700-byte nonclustered index-key limit after scope keys are added.";
            else if (postgreSql)
            {
                // PostgreSQL reserves at least three tuples on a B-tree page. Include tuple/header/null
                // overhead and pessimistic 8-byte datum alignment, rather than relying on compressibility.
                var tupleLimit = ((blockSize - 56) / 3 & ~7) - 8;
                var tupleWidth = 16L + 16L + order.Columns.Sum(c => ((long)c.Column.MaxLengthBytes!.Value + 7) & ~7L);
                if (tupleWidth > tupleLimit)
                    reason = $"its maximum B-tree tuple width ({tupleWidth} bytes) exceeds the database page limit ({tupleLimit} bytes).";
            }
            if (reason == null)
                orders.Add(order);
            else
                skipped.Add($"{entity.EntityType.Name}, order '{order.Name}': {reason}");
        }
        if (!orders.Any(o => o.Name == PrimaryOrder))
        {
            skipped.Add($"{entity.EntityType.Name}: no supported primary-key scope order is available.");
            return null;
        }
        var needed = orders.SelectMany(o => o.Columns.Select(c => c.Column.Name)).Append(entity.Key.Name).Append(entity.ResourceId.Name).ToHashSet(StringComparer.Ordinal);
        return entity with { Orders = orders, Columns = entity.Columns.Where(c => needed.Contains(c.Name)).ToArray() };
    }

    private sealed record CatalogColumn(string StoreType, string? Collation, bool IsNullable, int? Width, bool Supported);
    private sealed record Catalog(string? Schema, Dictionary<string, CatalogColumn> Columns, int BlockSize, Dictionary<string, string> Signature, string? Failure);

    private static Task<Catalog> ReadColumnsAsync(DbContext context, SqlOSFgaScopeEntity entity, bool postgreSql, CancellationToken cancellationToken)
    {
        var explicitSchema = entity.EntityType.GetSchema();
        var tableSql = (explicitSchema == null ? "" : Quote(explicitSchema, postgreSql) + ".") + Quote(entity.SourceTable, postgreSql);
        return ReadColumnsAsync(context, tableSql, postgreSql, cancellationToken);
    }

    private static async Task<Catalog> ReadColumnsAsync(DbContext context, string tableSql, bool postgreSql, CancellationToken cancellationToken, string? selectedColumn = null)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@table";
        parameter.Value = tableSql;
        command.Parameters.Add(parameter);
        var columnParameter = command.CreateParameter();
        columnParameter.ParameterName = "@column";
        columnParameter.DbType = DbType.String;
        columnParameter.Value = (object?)selectedColumn ?? DBNull.Value;
        command.Parameters.Add(columnParameter);
        command.CommandText = postgreSql ? """
            SELECT ns.nspname, a.attname, pg_catalog.format_type(a.atttypid, a.atttypmod),
                   CASE WHEN a.attcollation = 0 THEN NULL ELSE pg_catalog.quote_ident(cns.nspname) || '.' || pg_catalog.quote_ident(coll.collname) END,
                   NOT a.attnotnull, ty.typname, tns.nspname, ty.typtype::text,
                   c.relkind::text, current_setting('block_size')::integer,
                   COALESCE(pg_catalog.pg_get_expr(ad.adbin, ad.adrelid), ''),
                   EXISTS (SELECT 1 FROM pg_catalog.pg_constraint pk
                           WHERE pk.conrelid = c.oid AND pk.contype = 'p' AND pk.condeferrable)
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace ns ON ns.oid = c.relnamespace
            JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            JOIN pg_catalog.pg_type ty ON ty.oid = a.atttypid
            JOIN pg_catalog.pg_namespace tns ON tns.oid = ty.typnamespace
            LEFT JOIN pg_catalog.pg_collation coll ON coll.oid = a.attcollation
            LEFT JOIN pg_catalog.pg_namespace cns ON cns.oid = coll.collnamespace
            LEFT JOIN pg_catalog.pg_attrdef ad ON ad.adrelid = c.oid AND ad.adnum = a.attnum
            WHERE c.oid = pg_catalog.to_regclass(@table)
              AND (@column IS NULL OR a.attname = @column)
            ORDER BY a.attnum
            """ : """
            SELECT s.name, c.name, ty.name, TYPE_NAME(c.system_type_id), SCHEMA_NAME(ty.schema_id),
                   c.max_length, c.precision, c.scale, c.collation_name, c.is_nullable,
                   ty.is_user_defined, ty.is_assembly_type, t.temporal_type, t.is_memory_optimized,
                   c.user_type_id, c.system_type_id, COALESCE(cc.definition, '')
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            WHERE t.object_id = OBJECT_ID(@table, 'U')
              AND (@column IS NULL OR c.name = @column)
            ORDER BY c.column_id
            """;
        var columns = new Dictionary<string, CatalogColumn>(StringComparer.Ordinal);
        var signature = new Dictionary<string, string>(StringComparer.Ordinal);
        string? schema = null;
        string? failure = null;
        var blockSize = 8192;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            schema = reader.GetString(0);
            var name = reader.GetString(1);
            if (postgreSql)
            {
                var type = reader.GetString(2);
                var supported = reader.GetString(6) == "pg_catalog" && reader.GetString(7) == "b" && IsSupportedPostgreSqlType(reader.GetString(5));
                columns.Add(name, new(type, reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4), Width(type, true), supported));
                if (reader.GetString(8) != "r")
                    failure = "only ordinary PostgreSQL tables are supported (not views, partitions, or foreign tables).";
                if (reader.GetBoolean(11))
                    failure = "scope maintenance requires immediate primary-key uniqueness; deferrable PostgreSQL primary keys are not supported.";
                blockSize = reader.GetInt32(9);
                signature.Add(name, reader.GetString(10));
            }
            else
            {
                var baseType = reader.GetString(3).ToLowerInvariant();
                var length = Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture);
                var precision = Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture);
                var scale = Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture);
                var isAlias = Convert.ToInt32(reader.GetValue(14), CultureInfo.InvariantCulture) != Convert.ToInt32(reader.GetValue(15), CultureInfo.InvariantCulture);
                var type = isAlias ? Quote(reader.GetString(4), false) + "." + Quote(reader.GetString(2), false) : SqlServerType(baseType, length, precision, scale);
                var supported = !reader.GetBoolean(11) && IsSupportedSqlServerType(baseType);
                columns.Add(name, new(type, reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetBoolean(9), supported && length > 0 ? length : null, supported));
                if (Convert.ToInt32(reader.GetValue(12), CultureInfo.InvariantCulture) != 0 || reader.GetBoolean(13))
                    failure = "temporal and memory-optimized SQL Server tables are not supported.";
                // Alias names alone do not describe their underlying precision/scale or byte width.
                signature.Add(name, JsonSerializer.Serialize(new { BaseType = baseType, Length = length, Precision = precision, Scale = scale, Computed = reader.GetString(16) }));
            }
        }
        return new(schema, columns, blockSize, signature, failure);
    }

    private static string SqlServerType(string name, int length, int precision, int scale)
        => name switch
        {
            "varchar" or "char" or "binary" or "varbinary" => $"{name}({(length < 0 ? "max" : length.ToString(CultureInfo.InvariantCulture))})",
            "nvarchar" or "nchar" => $"{name}({(length < 0 ? "max" : (length / 2).ToString(CultureInfo.InvariantCulture))})",
            "decimal" or "numeric" => $"{name}({precision},{scale})",
            "time" or "datetime2" or "datetimeoffset" => $"{name}({scale})",
            "float" => $"float({precision})",
            _ => name
        };

    private static bool IsSupportedSqlServerType(string type)
        => type is "bit" or "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric"
            or "real" or "float" or "money" or "smallmoney" or "date" or "time" or "datetime" or "datetime2"
            or "datetimeoffset" or "smalldatetime" or "uniqueidentifier" or "binary" or "varbinary"
            or "char" or "varchar" or "nchar" or "nvarchar";

    private static bool IsSupportedPostgreSqlType(string type)
        => type is "bool" or "int2" or "int4" or "int8" or "float4" or "float8" or "numeric" or "money"
            or "date" or "time" or "timetz" or "timestamp" or "timestamptz" or "interval" or "uuid"
            or "bpchar" or "varchar" or "text" or "bytea";

    private static int? Width(string storeType, bool postgreSql)
    {
        var type = storeType.ToLowerInvariant().Trim();
        var match = Regex.Match(type, @"^(?<name>[a-z ]+)\((?<first>\d+)(?:\s*,\s*(?<second>-?\d+))?\)(?<tail>.*)$", RegexOptions.CultureInvariant);
        var name = match.Success ? match.Groups["name"].Value.Trim() + match.Groups["tail"].Value : type;
        var first = match.Success && int.TryParse(match.Groups["first"].Value, out var number) ? number : (int?)null;
        if (postgreSql)
        {
            if (name is "character varying" or "varchar" or "character" or "char")
                return first is > 0 and <= (int.MaxValue - 4) / 4 ? first * 4 + 4 : null;
            if (name is "numeric" or "decimal")
            {
                if (first == null || first > 1000)
                    return null;
                var scale = match.Groups["second"].Success ? int.Parse(match.Groups["second"].Value, CultureInfo.InvariantCulture) : 0;
                return 8 + ((first.Value + Math.Abs(scale) + 7) / 4) * 2;
            }
            return name switch
            {
                "boolean" or "bool" => 1,
                "smallint" or "int2" => 2,
                "integer" or "int" or "int4" or "real" or "float4" or "date" => 4,
                "bigint" or "int8" or "double precision" or "float8" or "money" or "time" or "time without time zone"
                    or "timestamp" or "timestamp without time zone" or "timestamp with time zone" or "timestamptz" => 8,
                "time with time zone" or "timetz" => 12,
                "uuid" or "interval" => 16,
                _ => null
            };
        }
        if (name is "nvarchar" or "nchar")
            return first is > 0 and <= int.MaxValue / 2 ? first * 2 : null;
        if (name is "varchar" or "char" or "varbinary" or "binary")
            return first is > 0 ? first : null;
        if (name is "decimal" or "numeric")
            return first is <= 9 ? 5 : first is <= 19 ? 9 : first is <= 28 ? 13 : first is <= 38 ? 17 : null;
        return name switch
        {
            "bit" or "tinyint" => 1,
            "smallint" => 2,
            "date" => 3,
            "int" or "real" or "smallmoney" or "smalldatetime" => 4,
            "time" => 5,
            "bigint" or "money" or "datetime" or "datetime2" => 8,
            "float" => first is <= 24 ? 4 : 8,
            "datetimeoffset" => 10,
            "uniqueidentifier" => 16,
            _ => null
        };
    }

    private static string Hash(SqlOSFgaScopeEntity entity, string providerName, string catalogSignature)
    {
        var store = StoreObjectIdentifier.Table(entity.SourceTable, entity.EntityType.GetSchema());
        return Digest(JsonSerializer.Serialize(new
        {
            Version = FormatVersion,
            Provider = providerName,
            entity.SourceSchema,
            entity.SourceTable,
            entity.ProjectionTable,
            entity.Key,
            entity.ResourceId,
            entity.BindingResourceId,
            Columns = entity.Columns.OrderBy(c => c.Name, StringComparer.Ordinal),
            Orders = entity.Orders.OrderBy(o => o.Name, StringComparer.Ordinal),
            Computed = entity.Columns.Select(c => entity.EntityType.FindProperty(c.PropertyName)!.GetComputedColumnSql(store)),
            Catalog = catalogSignature
        }));
    }

    private static string ProjectionName(string schema, string table)
        => "SqlOSFgaScopeIndex_" + Digest(JsonSerializer.Serialize(new[] { schema, table }))[..24];

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool IsPostgreSql(string providerName) => providerName.Contains("Npgsql", StringComparison.Ordinal);
    private static string Quote(string value, bool postgreSql)
        => postgreSql ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";
}

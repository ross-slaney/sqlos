using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using SqlOS.Database;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Fga.Paging;

/// <summary>
/// One declared page order of an entity: its order columns, ending with the primary key, and the
/// entry table that keeps a copy of that order under every node a row sits under.
/// </summary>
internal sealed class SqlOSFgaPageOrder
{
    private SqlOSFgaPageOrderStore? _store;

    internal SqlOSFgaPageOrder(
        IEntityType entityType,
        IEntityType entryType,
        IReadOnlyList<IProperty> columns,
        IProperty resourceId)
    {
        EntityType = entityType;
        EntryType = entryType;
        Columns = columns;
        SortColumns = columns.Take(columns.Count - 1).ToArray();
        ResourceId = resourceId;
    }

    /// <summary>The entity the order is declared on (the root of its hierarchy).</summary>
    public IEntityType EntityType { get; }

    /// <summary>The shared-type entry entity; its name is the entry table name.</summary>
    public IEntityType EntryType { get; }

    /// <summary>The order columns, ending with the primary key.</summary>
    public IReadOnlyList<IProperty> Columns { get; }

    /// <summary>The order columns without the primary key.</summary>
    public IReadOnlyList<IProperty> SortColumns { get; }

    public IProperty Key => Columns[^1];

    public IProperty ResourceId { get; }

    public string Table => EntryType.GetTableName()!;

    public string Description
        => $"{EntityType.ClrType.Name}({string.Join(", ", Columns.Select(column => column.Name))})";

    public bool Matches(IReadOnlyList<IProperty> columns)
        => columns.Count == Columns.Count
            && columns.Select(column => column.Name).SequenceEqual(Columns.Select(column => column.Name), StringComparer.Ordinal);

    /// <summary>The store names the maintenance statements use.</summary>
    public SqlOSFgaPageOrderStore Store(int maxDepth)
    {
        var store = _store ??= CreateStore();
        return store.MaxDepth == maxDepth ? store : store with { MaxDepth = maxDepth };
    }

    private SqlOSFgaPageOrderStore CreateStore()
    {
        var model = EntityType.Model;
        var resources = model.FindEntityType(typeof(SqlOSFgaResource))
            ?? throw new InvalidOperationException("The SqlOS FGA model is not registered on this context.");
        var entryStore = StoreObjectIdentifier.Table(Table, EntryType.GetSchema());
        var appTable = EntityType.GetTableName()!;
        var appSchema = EntityType.GetSchema();
        var appStore = StoreObjectIdentifier.Table(appTable, appSchema);

        return new SqlOSFgaPageOrderStore(
            resources.GetSchema() ?? "dbo",
            resources.GetTableName()!,
            SqlOSFgaHierarchyDepth.Default,
            EntryType.GetSchema(),
            Table,
            EntryColumn(SqlOSFgaPageOrders.NodeIdProperty),
            SortColumns.Select(column => EntryColumn(column.Name)).ToArray(),
            EntryColumn(SqlOSFgaPageOrders.RowKeyProperty),
            appSchema,
            appTable,
            SortColumns.Select(column => column.GetColumnName(appStore)!).ToArray(),
            Key.GetColumnName(appStore)!,
            ResourceId.GetColumnName(appStore)!,
            Key.GetRelationalTypeMapping().StoreType);

        string EntryColumn(string propertyName)
            => EntryType.FindProperty(propertyName)!.GetColumnName(entryStore)!;
    }
}

/// <summary>
/// Declares, builds and reads page orders. <c>HasPageOrder</c> records a declaration on the
/// entity type; <see cref="ApplyModel"/> validates it and adds the entry entity type once the
/// application has configured its model; the runtime reads the result from the
/// <c>SqlOS:PageOrders</c> annotation.
/// </summary>
internal static class SqlOSFgaPageOrders
{
    public const string AnnotationName = "SqlOS:PageOrders";
    public const string NodeIdProperty = "NodeId";
    public const string RowKeyProperty = "RowKey";
    public const string EntryTablePrefix = "SqlOSFgaPage_";
    public const int NodeIdMaxLength = 450;
    public const int SqlServerIdentifierLimit = 128;
    public const int PostgreSqlIdentifierLimit = 63;

    private const string ValueConverterTypeAnnotation = "ValueConverterType";

    private static readonly ConditionalWeakTable<IReadOnlyEntityType, IReadOnlyList<SqlOSFgaPageOrder>> OrdersByEntity = new();
    private static readonly ConditionalWeakTable<IReadOnlyModel, IReadOnlyList<SqlOSFgaPageOrder>> OrdersByModel = new();
    private static readonly ConditionalWeakTable<IReadOnlyModel, ConcurrentDictionary<string, bool>> MissingTables = new();

    /// <summary>Records <c>HasPageOrder</c>; a repeated declaration is a no-op.</summary>
    internal static void Declare(IMutableEntityType entityType, IReadOnlyList<string> columns)
    {
        var declarations = ReadAnnotation(entityType);
        if (!declarations.Any(declaration => declaration.Columns.SequenceEqual(columns, StringComparer.Ordinal)))
        {
            declarations.Add(new Declaration(null, columns.ToArray()));
            WriteAnnotation(entityType, declarations);
        }
    }

    /// <summary>
    /// Validates every declaration and adds its entry entity type. Runs after the application's
    /// model configuration, so the columns' facets, the table name and the key are final.
    /// </summary>
    internal static void ApplyModel(IMutableModel model, string? providerName)
    {
        var identifierLimit = SqlOSDatabase.IsPostgreSql(providerName)
            ? PostgreSqlIdentifierLimit
            : SqlServerIdentifierLimit;
        var fgaSchema = model.FindEntityType(typeof(SqlOSFgaResource))?.GetSchema() ?? "dbo";

        foreach (var entityType in model.GetEntityTypes().ToList())
        {
            var declarations = ReadAnnotation(entityType);
            if (declarations.Count == 0)
            {
                continue;
            }

            var key = ValidateEntity(entityType);
            var resolved = new List<Declaration>();
            foreach (var declaration in declarations)
            {
                var columns = NormalizeColumns(entityType, key, declaration.Columns);
                var names = columns.Select(column => column.Name).ToArray();
                if (resolved.Any(existing => existing.Columns.SequenceEqual(names, StringComparer.Ordinal)))
                {
                    continue;
                }

                var table = EntryTableName(entityType.GetTableName()!, columns, identifierLimit);
                AddEntryType(model, entityType, table, fgaSchema, columns);
                resolved.Add(new Declaration(table, names));
            }

            WriteAnnotation(entityType, resolved);
        }
    }

    /// <summary>The page orders declared on an entity type or the root of its hierarchy.</summary>
    public static IReadOnlyList<SqlOSFgaPageOrder> For(IEntityType entityType)
        => OrdersByEntity.GetValue(entityType.GetRootType(), static root => Read((IEntityType)root));

    /// <summary>Every page order in a model.</summary>
    public static IReadOnlyList<SqlOSFgaPageOrder> All(IModel model)
        => OrdersByModel.GetValue(
            model,
            static readOnlyModel => ((IModel)readOnlyModel).GetEntityTypes()
                .Where(entityType => entityType.BaseType == null)
                .SelectMany(For)
                .ToArray());

    /// <summary>Records whether startup found an order's entry table; a missing table is not read.</summary>
    internal static void SetTableMissing(IModel model, SqlOSFgaPageOrder order, bool missing)
        => MissingTables.GetValue(model, static _ => new ConcurrentDictionary<string, bool>(StringComparer.Ordinal))[order.Table] = missing;

    internal static bool IsTableMissing(IModel model, SqlOSFgaPageOrder order)
        => MissingTables.TryGetValue(model, out var tables)
            && tables.TryGetValue(order.Table, out var missing)
            && missing;

    internal static string EntryTableName(string table, IReadOnlyList<IReadOnlyProperty> columns, int identifierLimit)
    {
        var sortColumns = columns.Take(columns.Count - 1).Select(column => column.Name).ToArray();
        var parts = sortColumns.Length == 0 ? [columns[^1].Name] : sortColumns;
        var name = $"{EntryTablePrefix}{table}_{string.Join("_", parts)}";
        if (name.Length <= identifierLimit)
        {
            return name;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();
        return $"{name[..(identifierLimit - 9)]}_{hash}";
    }

    private static IMutableProperty ValidateEntity(IMutableEntityType entityType)
    {
        var name = entityType.DisplayName();
        if (entityType.BaseType != null)
        {
            throw ModelError(name, $"it derives from {entityType.BaseType.DisplayName()}. Declare the page order on {entityType.GetRootType().DisplayName()}, the root of the hierarchy.");
        }

        if (entityType.IsOwned())
        {
            throw ModelError(name, "it is an owned type. Page orders need an entity mapped to its own table.");
        }

        if (entityType.GetTableName() == null)
        {
            throw ModelError(name, entityType.GetViewName() != null
                ? "it is mapped to a view. Page orders need an entity mapped to a table."
                : "it is not mapped to a table. Page orders need an entity mapped to a table.");
        }

        var strategy = entityType.GetMappingStrategy();
        if (strategy is RelationalAnnotationNames.TptMappingStrategy or RelationalAnnotationNames.TpcMappingStrategy
            || entityType.GetDerivedTypes().Any(derived => derived.GetTableName() != entityType.GetTableName()))
        {
            throw ModelError(name, "it uses TPT or TPC inheritance. Page orders need the hierarchy in one table (TPH, the default).");
        }

        var primaryKey = entityType.FindPrimaryKey();
        if (primaryKey == null)
        {
            throw ModelError(name, "it has no primary key. Page orders need a single-column primary key.");
        }

        if (primaryKey.Properties.Count != 1)
        {
            throw ModelError(name, "its primary key is composite. Page orders need a single-column primary key.");
        }

        if (entityType.FindProperty(nameof(IHasResourceId.ResourceId)) == null)
        {
            throw ModelError(name, $"it has no mapped {nameof(IHasResourceId.ResourceId)} property. Map {nameof(IHasResourceId.ResourceId)} to a column of its table.");
        }

        return primaryKey.Properties[0];
    }

    private static IReadOnlyList<IMutableProperty> NormalizeColumns(
        IMutableEntityType entityType,
        IMutableProperty key,
        IReadOnlyList<string> declared)
    {
        var name = entityType.DisplayName();
        var columns = new List<IMutableProperty>();
        foreach (var columnName in declared)
        {
            var property = entityType.FindProperty(columnName);
            if (property == null || property.IsShadowProperty())
            {
                throw ModelError(name, $"'{columnName}' is not a mapped scalar property of {name}. Order by properties that map to a column of {entityType.GetTableName()}.");
            }

            if (property.IsNullable)
            {
                throw ModelError(name, $"'{columnName}' is nullable. Page orders need non-nullable columns: make '{columnName}' required, or order by a non-nullable column.");
            }

            if (!SqlOSFgaPageCursor.IsSupported(property.ClrType))
            {
                throw ModelError(name, $"'{columnName}' is a {property.ClrType.Name}. Page orders support strings, numbers, Guid, bool, enums and date/time values.");
            }

            if (property.ClrType == typeof(string)
                && property.GetMaxLength() == null
                && property.FindAnnotation(RelationalAnnotationNames.ColumnType)?.Value is not string)
            {
                throw ModelError(name, $"'{columnName}' is a string without a maximum length. Page orders copy it into an index key: set HasMaxLength on '{columnName}', or an explicit column type.");
            }

            if (property != key
                && (string.Equals(columnName, NodeIdProperty, StringComparison.Ordinal)
                    || string.Equals(columnName, RowKeyProperty, StringComparison.Ordinal)))
            {
                throw ModelError(name, $"'{columnName}' is a name the entry table uses for itself. Rename the property or order by another column.");
            }

            if (columns.Contains(property))
            {
                continue;
            }

            columns.Add(property);
            if (property == key)
            {
                // The key is unique: nothing after it changes the order.
                break;
            }
        }

        if (columns.Count == 0)
        {
            throw ModelError(name, "the declaration names no column. Pass one property or an anonymous type of properties.");
        }

        if (columns[^1] != key)
        {
            columns.Add(key);
        }

        return columns;
    }

    private static void AddEntryType(
        IMutableModel model,
        IMutableEntityType entityType,
        string table,
        string schema,
        IReadOnlyList<IMutableProperty> columns)
    {
        if (model.FindEntityType(table) != null)
        {
            throw ModelError(entityType.DisplayName(), $"its entry table name '{table}' is already used by another entity type.");
        }

        var entry = model.AddEntityType(table, typeof(Dictionary<string, object>));
        entry.SetTableName(table);
        entry.SetSchema(schema);

        var nodeId = entry.AddIndexerProperty(NodeIdProperty, typeof(string));
        nodeId.IsNullable = false;
        nodeId.SetMaxLength(NodeIdMaxLength);

        var keyProperties = new List<IMutableProperty> { nodeId };
        foreach (var column in columns.Take(columns.Count - 1))
        {
            keyProperties.Add(CopyColumn(entry, column.Name, column));
        }

        var rowKey = CopyColumn(entry, RowKeyProperty, columns[^1]);
        keyProperties.Add(rowKey);

        entry.SetPrimaryKey(keyProperties);
        entry.AddIndex(rowKey);
    }

    private static IMutableProperty CopyColumn(IMutableEntityType entry, string name, IMutableProperty source)
    {
        var target = entry.AddIndexerProperty(name, source.ClrType);
        target.IsNullable = false;
        target.ValueGenerated = ValueGenerated.Never;
        target.SetMaxLength(source.GetMaxLength());
        target.SetIsUnicode(source.IsUnicode());
        target.SetPrecision(source.GetPrecision());
        target.SetScale(source.GetScale());
        target.SetIsFixedLength(source.IsFixedLength());
        target.SetCollation(source.GetCollation());
        if (source.FindAnnotation(RelationalAnnotationNames.ColumnType)?.Value is string columnType)
        {
            target.SetColumnType(columnType);
        }

        if (source.GetValueConverter() is { } converter)
        {
            target.SetValueConverter(converter);
        }
        else if (source.FindAnnotation(ValueConverterTypeAnnotation)?.Value is Type converterType)
        {
            target.SetValueConverter(converterType);
        }

        if (source.GetProviderClrType() is { } providerClrType)
        {
            target.SetProviderClrType(providerClrType);
        }

        return target;
    }

    private static IReadOnlyList<SqlOSFgaPageOrder> Read(IEntityType entityType)
    {
        var orders = new List<SqlOSFgaPageOrder>();
        var resourceId = entityType.FindProperty(nameof(IHasResourceId.ResourceId));
        if (resourceId == null)
        {
            return orders;
        }

        foreach (var declaration in ReadAnnotation(entityType))
        {
            if (declaration.Table == null || entityType.Model.FindEntityType(declaration.Table) is not { } entryType)
            {
                continue;
            }

            var columns = declaration.Columns.Select(entityType.FindProperty).ToArray();
            if (columns.Any(column => column == null))
            {
                continue;
            }

            orders.Add(new SqlOSFgaPageOrder(entityType, entryType, columns!, resourceId));
        }

        return orders;
    }

    private static List<Declaration> ReadAnnotation(IReadOnlyAnnotatable annotatable)
        => annotatable.FindAnnotation(AnnotationName)?.Value is string json
            ? JsonSerializer.Deserialize<List<Declaration>>(json, JsonOptions) ?? []
            : [];

    private static void WriteAnnotation(IMutableEntityType entityType, IReadOnlyList<Declaration> declarations)
        => entityType.SetAnnotation(AnnotationName, JsonSerializer.Serialize(declarations, JsonOptions));

    private static InvalidOperationException ModelError(string entity, string reason)
        => new($"HasPageOrder on {entity}: {reason}");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record Declaration(string? Table, string[] Columns);
}

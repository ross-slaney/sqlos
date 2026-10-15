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
/// One page order of an entity: its order columns, ending with the primary key, and the entry
/// table that keeps a copy of that order under every node a row sits under.
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

    /// <summary>The entity the order belongs to (the root of its hierarchy).</summary>
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
/// An index, or an entity, that got no page order, and why. Logged once at startup.
/// </summary>
/// <param name="EntityType">The protected entity.</param>
/// <param name="Columns">The index's columns, or <see langword="null"/> when the whole entity got no orders.</param>
/// <param name="Reason">Why, ending with the fix.</param>
internal sealed record SqlOSFgaPageOrderSkip(IEntityType EntityType, IReadOnlyList<string>? Columns, string Reason);

/// <summary>
/// Builds and reads page orders. <see cref="ApplyModel"/> gives every root entity type that
/// implements <see cref="IHasResourceId"/> one page order for its primary key and one for each of
/// its indexes, and adds their entry entity types once the application has configured its model;
/// the runtime reads the result from the <c>SqlOS:PageOrders</c> annotation.
/// </summary>
internal static class SqlOSFgaPageOrders
{
    public const string AnnotationName = "SqlOS:PageOrders";
    public const string SkipsAnnotationName = "SqlOS:PageOrderSkips";
    public const string NodeIdProperty = "NodeId";
    public const string RowKeyProperty = "RowKey";
    public const string EntryTablePrefix = "SqlOSFgaPage_";
    public const int NodeIdMaxLength = 450;
    public const int SqlServerIdentifierLimit = 128;
    public const int PostgreSqlIdentifierLimit = 63;

    private const string ValueConverterTypeAnnotation = "ValueConverterType";

    private static readonly ConditionalWeakTable<IReadOnlyEntityType, IReadOnlyList<SqlOSFgaPageOrder>> OrdersByEntity = new();
    private static readonly ConditionalWeakTable<IReadOnlyModel, IReadOnlyList<SqlOSFgaPageOrder>> OrdersByModel = new();
    private static readonly ConditionalWeakTable<IReadOnlyModel, IReadOnlyList<SqlOSFgaPageOrderSkip>> SkipsByModel = new();
    private static readonly ConditionalWeakTable<IReadOnlyModel, ConcurrentDictionary<string, bool>> MissingTables = new();

    /// <summary>
    /// Gives every root entity type whose CLR type implements <see cref="IHasResourceId"/> a page
    /// order for its primary key and one per index, and adds their entry entity types. Runs after
    /// the application's model configuration, so the columns' facets, the table name, the key and
    /// the indexes are final. Nothing here throws: an entity or an index that cannot have an order
    /// is recorded as a skip, and startup logs it.
    /// </summary>
    /// <remarks>
    /// Two kinds of index are lookups, not sorts, and get no order: one that contains the entity's
    /// <c>ResourceId</c>, and one whose columns are exactly the columns of one of the entity's foreign
    /// keys. An index that ends with the key gives the same order as the index without it.
    /// </remarks>
    internal static void ApplyModel(IMutableModel model, string? providerName)
    {
        var identifierLimit = SqlOSDatabase.IsPostgreSql(providerName)
            ? PostgreSqlIdentifierLimit
            : SqlServerIdentifierLimit;
        var fgaSchema = model.FindEntityType(typeof(SqlOSFgaResource))?.GetSchema() ?? "dbo";

        foreach (var entityType in model.GetEntityTypes().ToList())
        {
            if (!typeof(IHasResourceId).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            var skips = new List<Skip>();
            var resolved = new List<Declaration>();
            if (entityType.BaseType != null)
            {
                // Orders live on the root of a hierarchy; the root's visit makes them.
                var root = entityType.GetRootType();
                if (!typeof(IHasResourceId).IsAssignableFrom(root.ClrType))
                {
                    skips.Add(new Skip(null, $"it derives from {root.DisplayName()}, which does not implement {nameof(IHasResourceId)}. Page orders live on the root of a hierarchy: implement {nameof(IHasResourceId)} on {root.DisplayName()}."));
                }

                WriteAnnotations(entityType, resolved, skips);
                continue;
            }

            if (ValidateEntity(entityType, out var key) is { } entityReason)
            {
                skips.Add(new Skip(null, entityReason));
                WriteAnnotations(entityType, resolved, skips);
                continue;
            }

            foreach (var candidate in Candidates(entityType, key))
            {
                var columns = NormalizeColumns(entityType, key, candidate, out var columnReason);
                if (columns == null)
                {
                    skips.Add(new Skip(candidate.ToArray(), columnReason!));
                    continue;
                }

                var names = columns.Select(column => column.Name).ToArray();
                if (resolved.Any(existing => existing.Columns.SequenceEqual(names, StringComparer.Ordinal)))
                {
                    continue;
                }

                var table = EntryTableName(entityType.GetTableName()!, columns, identifierLimit);
                if (model.FindEntityType(table) != null)
                {
                    skips.Add(new Skip(candidate.ToArray(), $"its entry table name '{table}' is already used by another entity type. Map {entityType.DisplayName()} to a table of its own."));
                    continue;
                }

                AddEntryType(model, table, fgaSchema, columns);
                resolved.Add(new Declaration(table, names));
            }

            WriteAnnotations(entityType, resolved, skips);
        }
    }

    /// <summary>The page orders of an entity type or the root of its hierarchy.</summary>
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

    /// <summary>Every index, and every entity, that got no page order, with the reason.</summary>
    public static IReadOnlyList<SqlOSFgaPageOrderSkip> Skips(IModel model)
        => SkipsByModel.GetValue(
            model,
            static readOnlyModel => ((IModel)readOnlyModel).GetEntityTypes()
                .SelectMany(entityType => ReadSkips(entityType)
                    .Select(skip => new SqlOSFgaPageOrderSkip(entityType, skip.Columns, skip.Reason)))
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

    /// <summary>
    /// The key alone, then each index that is a sort: not one that contains <c>ResourceId</c>, and
    /// not one whose columns are exactly a foreign key's.
    /// </summary>
    private static IEnumerable<IReadOnlyList<string>> Candidates(IMutableEntityType entityType, IMutableProperty key)
    {
        yield return [key.Name];

        var foreignKeys = entityType.GetForeignKeys()
            .Select(foreignKey => foreignKey.Properties.Select(property => property.Name).ToArray())
            .ToList();
        foreach (var index in entityType.GetIndexes())
        {
            var names = index.Properties.Select(property => property.Name).ToArray();
            if (names.Contains(nameof(IHasResourceId.ResourceId), StringComparer.Ordinal)
                || foreignKeys.Any(foreignKey => foreignKey.SequenceEqual(names, StringComparer.Ordinal)))
            {
                continue;
            }

            yield return names;
        }
    }

    private static string? ValidateEntity(IMutableEntityType entityType, out IMutableProperty key)
    {
        key = null!;
        if (entityType.IsOwned())
        {
            return "it is an owned type. Page orders need an entity mapped to its own table.";
        }

        if (entityType.GetTableName() == null)
        {
            return entityType.GetViewName() != null
                ? "it is mapped to a view. Page orders need an entity mapped to a table."
                : "it is not mapped to a table. Page orders need an entity mapped to a table.";
        }

        var strategy = entityType.GetMappingStrategy();
        if (strategy is RelationalAnnotationNames.TptMappingStrategy or RelationalAnnotationNames.TpcMappingStrategy
            || entityType.GetDerivedTypes().Any(derived => derived.GetTableName() != entityType.GetTableName()))
        {
            return "it uses TPT or TPC inheritance. Page orders need the hierarchy in one table (TPH, the default).";
        }

        var primaryKey = entityType.FindPrimaryKey();
        if (primaryKey == null)
        {
            return "it has no primary key. Page orders need a single-column primary key.";
        }

        if (primaryKey.Properties.Count != 1)
        {
            return "its primary key is composite. Page orders need a single-column primary key.";
        }

        if (entityType.FindProperty(nameof(IHasResourceId.ResourceId)) == null)
        {
            return $"it has no mapped {nameof(IHasResourceId.ResourceId)} property. Map {nameof(IHasResourceId.ResourceId)} to a column of its table.";
        }

        key = primaryKey.Properties[0];
        if (!SqlOSFgaPageCursor.IsSupported(key.ClrType))
        {
            return $"its primary key is a {key.ClrType.Name}. Page orders support strings, numbers, Guid, bool, enums and date/time values.";
        }

        return null;
    }

    /// <summary>
    /// The order's columns with the key appended, or <see langword="null"/> and the reason when a
    /// column cannot be an order column. The key passed <see cref="ValidateEntity"/>: it is never
    /// nullable, and a string key without a maximum length is what the engine's key convention bounds.
    /// </summary>
    private static IReadOnlyList<IMutableProperty>? NormalizeColumns(
        IMutableEntityType entityType,
        IMutableProperty key,
        IReadOnlyList<string> candidate,
        out string? reason)
    {
        var name = entityType.DisplayName();
        var columns = new List<IMutableProperty>();
        foreach (var columnName in candidate)
        {
            var property = entityType.FindProperty(columnName);
            if (property == null || property.IsShadowProperty())
            {
                reason = $"'{columnName}' is not a mapped scalar property of {name}. Order by properties that map to a column of {entityType.GetTableName()}.";
                return null;
            }

            if (property == key)
            {
                // The key is unique: nothing after it changes the order.
                columns.Add(property);
                break;
            }

            if (property.IsNullable)
            {
                reason = $"'{columnName}' is nullable. Page orders need non-nullable columns: make '{columnName}' required, or order by a non-nullable column.";
                return null;
            }

            if (!SqlOSFgaPageCursor.IsSupported(property.ClrType))
            {
                reason = $"'{columnName}' is a {property.ClrType.Name}. Page orders support strings, numbers, Guid, bool, enums and date/time values.";
                return null;
            }

            if (property.ClrType == typeof(string)
                && property.GetMaxLength() == null
                && property.FindAnnotation(RelationalAnnotationNames.ColumnType)?.Value is not string)
            {
                reason = $"'{columnName}' is a string without a maximum length. Page orders copy it into an index key: set HasMaxLength on '{columnName}', or an explicit column type.";
                return null;
            }

            if (string.Equals(columnName, NodeIdProperty, StringComparison.Ordinal)
                || string.Equals(columnName, RowKeyProperty, StringComparison.Ordinal))
            {
                reason = $"'{columnName}' is a name the entry table uses for itself. Rename the property or order by another column.";
                return null;
            }

            if (!columns.Contains(property))
            {
                columns.Add(property);
            }
        }

        if (columns[^1] != key)
        {
            columns.Add(key);
        }

        reason = null;
        return columns;
    }

    private static void AddEntryType(
        IMutableModel model,
        string table,
        string schema,
        IReadOnlyList<IMutableProperty> columns)
    {
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

    private static List<Skip> ReadSkips(IReadOnlyAnnotatable annotatable)
        => annotatable.FindAnnotation(SkipsAnnotationName)?.Value is string json
            ? JsonSerializer.Deserialize<List<Skip>>(json, JsonOptions) ?? []
            : [];

    private static void WriteAnnotations(IMutableEntityType entityType, IReadOnlyList<Declaration> declarations, IReadOnlyList<Skip> skips)
    {
        if (declarations.Count == 0)
        {
            entityType.RemoveAnnotation(AnnotationName);
        }
        else
        {
            entityType.SetAnnotation(AnnotationName, JsonSerializer.Serialize(declarations, JsonOptions));
        }

        if (skips.Count == 0)
        {
            entityType.RemoveAnnotation(SkipsAnnotationName);
        }
        else
        {
            entityType.SetAnnotation(SkipsAnnotationName, JsonSerializer.Serialize(skips, JsonOptions));
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record Declaration(string? Table, string[] Columns);

    private sealed record Skip(string[]? Columns, string Reason);
}

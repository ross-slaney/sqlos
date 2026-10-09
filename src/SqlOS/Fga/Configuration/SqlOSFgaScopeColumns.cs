using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Configuration;

/// <summary>
/// The scope column (<see cref="IHasResourceId.FgaScope"/>) of every application entity with a resource id.
/// The entity declares the property; this pass maps the column (from the property, or as a shadow property
/// when the entity implements it explicitly and EF Core does not map it by convention) and configures what
/// the database needs of it: a fixed maximum length (so SQL Server can index pieces of it), that EF Core never
/// writes it, the SQL Server triggers SqlOS keeps it current with (declared so EF Core's update pipeline
/// avoids OUTPUT without INTO), and an index on the resource id the triggers find rows by. The per-level
/// indexes, the triggers themselves, and the fill are created by <c>SqlOSFgaFunctionInitializer</c> at
/// startup, outside the application's migrations.
/// </summary>
internal static class SqlOSFgaScopeColumns
{
    /// <summary>The <see cref="ISqlOSResourceEntity"/> members that describe the backing resource and are never columns of the entity's table.</summary>
    private static readonly string[] ResourceDescriptionMembers =
    [
        nameof(ISqlOSResourceEntity.ResourceTypeId),
        nameof(ISqlOSResourceEntity.ResourceName),
        nameof(ISqlOSResourceEntity.ParentResourceId),
        nameof(ISqlOSResourceEntity.ResourceDescription),
        nameof(ISqlOSResourceEntity.ResourceIsActive),
    ];

    /// <summary>Configures every application entity type in the model that implements <see cref="IHasResourceId"/>.</summary>
    public static void Configure(ModelBuilder modelBuilder, SqlOSFgaOptions options)
    {
        if (SqlOSFgaLineage.Levels(options) > SqlOSFgaLineage.ScopeMaxLevels)
        {
            throw new InvalidOperationException($"SqlOS FGA supports a MaxResourceHierarchyDepth of at most {SqlOSFgaLineage.ScopeMaxLevels - 1}.");
        }

        if (modelBuilder.Model.FindEntityType(typeof(SqlOSResourceEntity)) is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(SqlOSResourceEntity)} is the base class of the application's entities and cannot be "
                + "mapped as an entity itself (an EF Core hierarchy would put every derived entity in one "
                + "table, and SqlOS would protect none of them). Remove its DbSet or mapping and map the "
                + "entities that derive from it.");
        }

        var sqlosAssembly = typeof(SqlOSFgaScopeColumns).Assembly;
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            if (!IsProtected(entityType, sqlosAssembly))
            {
                continue;
            }

            var entity = modelBuilder.Entity(entityType.ClrType);
            if (typeof(SqlOSResourceEntity).IsAssignableFrom(entityType.ClrType))
            {
                // The descriptive members feed resource synchronization, never the table. An override with a
                // backing field would be mapped by convention; an explicit mapping by the app is kept.
                foreach (var member in ResourceDescriptionMembers)
                {
                    if (((IConventionEntityType)entityType).FindProperty(member)?.GetConfigurationSource() == ConfigurationSource.Convention)
                    {
                        entity.Ignore(member);
                    }
                }
            }
            var table = entityType.GetTableName()!;
            var scope = entity.Property<byte[]>(SqlOSFgaLineage.ScopeColumn).HasMaxLength(SqlOSFgaLineage.ScopeMaxLength);
            SqlOSFgaModelConfiguration.DatabaseOwned(scope);

            entity.ToTable(t =>
            {
                foreach (var trigger in SqlOSFgaLineage.ScopeTriggerNames(table))
                {
                    t.HasTrigger(trigger);
                }
            });

            var resourceId = entityType.FindProperty(nameof(IHasResourceId.ResourceId))!;
            if (!entityType.GetIndexes().Any(i => i.Properties[0] == resourceId))
            {
                entity.HasIndex([resourceId.Name], SqlOSFgaLineage.ScopeResourceIdIndexName(table));
            }
        }
    }

    /// <summary>
    /// The application tables of the model that carry the scope column, for the database routines: how to
    /// address each, and the orders it declared indexes for. Per level, SqlOS indexes the level's ancestor
    /// followed by the primary key, and followed by each declared order's columns and then the key (a cursor
    /// page orders by the declared columns, then the key). Every level has at least the key index, which is
    /// what keeps a single-grant caller's page an index seek instead of a table scan.
    /// </summary>
    public static IReadOnlyList<SqlOSFgaScopeTable> Tables(IModel model)
    {
        var tables = new List<SqlOSFgaScopeTable>();
        var defaultSchema = model.GetDefaultSchema();
        var sqlosAssembly = typeof(SqlOSFgaScopeColumns).Assembly;
        foreach (var entityType in model.GetEntityTypes())
        {
            if (!IsProtected(entityType, sqlosAssembly) || entityType.FindProperty(SqlOSFgaLineage.ScopeColumn) is null)
            {
                continue;
            }

            var table = entityType.GetTableName()!;
            var schema = entityType.GetSchema() ?? defaultSchema;
            var store = StoreObjectIdentifier.Table(table, schema);
            var resourceId = entityType.FindProperty(nameof(IHasResourceId.ResourceId))!;
            var key = entityType.FindPrimaryKey()!.Properties.Select(p => p.GetColumnName(store)!).ToList();
            var orders = entityType.GetIndexes()
                .Where(i => IsDeclaredOrder(entityType, i, resourceId))
                .Select(i => new SqlOSFgaScopeOrder(
                    MirrorSuffix(table, i),
                    i.Properties.Select(p => p.GetColumnName(store)!).Concat(key).Distinct().ToList()))
                .OrderBy(o => o.Suffix, StringComparer.Ordinal)
                .ToList();
            var columns = entityType.GetProperties()
                .Where(p => p.GetColumnName(store) is not null)
                .Select(p => new SqlOSFgaScopeColumn(p.GetColumnName(store)!, p.GetColumnType(store), p.IsNullable,
                    p.GetComputedColumnSql(store) is not null || p.ValueGenerated is ValueGenerated.OnAddOrUpdate or ValueGenerated.OnUpdate))
                .ToList();
            tables.Add(new SqlOSFgaScopeTable(schema, table, resourceId.GetColumnName(store)!, key, orders, columns));
        }

        return tables.OrderBy(t => t.Schema, StringComparer.Ordinal).ThenBy(t => t.Table, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// An application entity type with a resource id: it implements <see cref="IHasResourceId"/> (and so
    /// declares the scope column), maps its resource id, is mapped to a table of its own, and is not one of
    /// SqlOS's.
    /// </summary>
    private static bool IsProtected(IReadOnlyEntityType entityType, System.Reflection.Assembly sqlosAssembly)
        => typeof(IHasResourceId).IsAssignableFrom(entityType.ClrType)
           && entityType.ClrType.Assembly != sqlosAssembly
           && !entityType.IsOwned()
           && entityType.FindPrimaryKey() is not null
           && entityType.GetTableName() is not null
           && entityType.BaseType is null
           && entityType.FindProperty(nameof(IHasResourceId.ResourceId)) is not null;

    /// <summary>
    /// An index the application declared as an order it pages in: not unique (an identity, not an order),
    /// not the resource id lookup, not the scope column, and not an index that merely covers a foreign key.
    /// </summary>
    private static bool IsDeclaredOrder(IReadOnlyEntityType entityType, IReadOnlyIndex index, IReadOnlyProperty resourceId)
        => !index.IsUnique
           && index.Properties[0] != resourceId
           && !index.Properties.Any(p => p.Name == SqlOSFgaLineage.ScopeColumn)
           && !entityType.GetForeignKeys().Any(fk => fk.Properties.SequenceEqual(index.Properties));

    /// <summary>What names a mirror of a declared index: the index's name without the conventional <c>IX_{table}_</c> prefix.</summary>
    private static string MirrorSuffix(string table, IReadOnlyIndex index)
    {
        var name = index.GetDatabaseName() ?? string.Join("_", index.Properties.Select(p => p.Name));
        var prefix = $"IX_{table}_";
        return name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length ? name[prefix.Length..] : name;
    }
}

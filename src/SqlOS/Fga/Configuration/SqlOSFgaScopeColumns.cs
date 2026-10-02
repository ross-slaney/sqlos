using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SqlOS.Database;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Configuration;

/// <summary>
/// The scope column: the resource lineage carried onto every application table whose entity implements
/// <see cref="IHasResourceId"/>. This pass adds the one column to the EF model as a database-owned shadow
/// property, so the application's next migration carries it, and indexes the resource id for the triggers.
/// Everything else on the database side (the per-level indexes, SQL Server's computed columns, the triggers
/// that keep the values current, and the one-time fill) is created by <c>SqlOSFgaFunctionInitializer</c>
/// from the tables this pass marks, outside the application's migrations.
/// </summary>
internal static class SqlOSFgaScopeColumns
{
    /// <summary>
    /// Marks and configures every eligible entity type in the model. Must run after the application's own
    /// entity configuration, which is why <c>SqlOSDbContext</c> calls it last and
    /// <c>ApplySqlOSFgaModel</c> documents its place in <c>OnModelCreating</c>.
    /// </summary>
    public static void Configure(ModelBuilder modelBuilder, SqlOSFgaOptions options, string? providerName)
    {
        if (SqlOSFgaLineage.Levels(options) > SqlOSFgaLineage.ScopeMaxLevels)
        {
            throw new InvalidOperationException($"SqlOS FGA supports a MaxResourceHierarchyDepth of at most {SqlOSFgaLineage.ScopeMaxLevels - 1}.");
        }

        var postgres = SqlOSDatabase.IsPostgreSql(providerName);
        var sqlosAssembly = typeof(SqlOSFgaScopeColumns).Assembly;
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            if (!IsEligible(entityType, sqlosAssembly))
            {
                continue;
            }

            var entity = modelBuilder.Entity(entityType.ClrType);
            var table = entityType.GetTableName()!;
            entity.HasAnnotation(SqlOSFgaLineage.ScopeAnnotation, true);

            // The column. Database-owned: EF Core reads it and never writes it.
            if (postgres)
            {
                SqlOSFgaModelConfiguration.DatabaseOwned(entity.Property<long?[]>(SqlOSFgaLineage.ScopeColumn));
            }
            else
            {
                var scope = entity.Property<byte[]>(SqlOSFgaLineage.ScopeColumn);
                if (providerName == SqlOSDatabase.SqlServerProviderName)
                {
                    scope.HasColumnType(SqlOSFgaLineage.ScopeBinaryType);
                }

                SqlOSFgaModelConfiguration.DatabaseOwned(scope);
            }

            // The triggers (declared so EF Core's SQL Server update pipeline avoids OUTPUT without INTO).
            entity.ToTable(t =>
            {
                foreach (var trigger in SqlOSFgaLineage.ScopeTriggerNames(table))
                {
                    t.HasTrigger(trigger);
                }
            });

            // The resource id must be indexed: the triggers find a resource's rows by it.
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
        foreach (var entityType in model.GetEntityTypes())
        {
            if (!Has(entityType))
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
            tables.Add(new SqlOSFgaScopeTable(schema, table, resourceId.GetColumnName(store)!, key, orders));
        }

        return tables.OrderBy(t => t.Schema, StringComparer.Ordinal).ThenBy(t => t.Table, StringComparer.Ordinal).ToList();
    }

    /// <summary>Whether an entity type's table carries the scope column.</summary>
    public static bool Has(IReadOnlyEntityType entityType)
        => entityType.FindAnnotation(SqlOSFgaLineage.ScopeAnnotation)?.Value is true;

    private static bool IsEligible(IMutableEntityType entityType, System.Reflection.Assembly sqlosAssembly)
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
           && !index.Properties.Any(p => p.Name.StartsWith(SqlOSFgaLineage.ScopePrefix, StringComparison.Ordinal))
           && !entityType.GetForeignKeys().Any(fk => fk.Properties.SequenceEqual(index.Properties));

    /// <summary>What names a mirror of a declared index: the index's name without the conventional <c>IX_{table}_</c> prefix.</summary>
    private static string MirrorSuffix(string table, IReadOnlyIndex index)
    {
        var name = index.GetDatabaseName() ?? string.Join("_", index.Properties.Select(p => p.Name));
        var prefix = $"IX_{table}_";
        return name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length ? name[prefix.Length..] : name;
    }
}

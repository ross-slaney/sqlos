using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SqlOS.Database;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Configuration;

/// <summary>
/// The scope columns (<see cref="SqlOSFgaOptions.ScopeColumns"/>): the resource lineage copied onto every
/// application table whose entity implements <see cref="IHasResourceId"/>. This pass adds them to the EF
/// model as shadow properties, with one filtered index per level over the table's primary key and over every
/// index the application declared, so the application's next migration carries them. The database side (the
/// triggers that keep the values current and the one-time fill) is created by
/// <c>SqlOSFgaFunctionInitializer</c> from the tables this pass marks.
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
        if (!options.ScopeColumns)
        {
            return;
        }

        var provider = SqlOSDatabase.Resolve(providerName
            ?? throw new InvalidOperationException(
                "SqlOS FGA scope columns need the database provider to build their indexes. Pass Database.ProviderName to ApplySqlOSFgaModel, or derive the context from SqlOSDbContext."));
        var levels = SqlOSFgaLineage.Levels(options);
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

            // The columns. Database-owned: EF Core reads them and never writes them.
            for (var level = 0; level < levels; level++)
            {
                SqlOSFgaModelConfiguration.DatabaseOwned(entity.Property<long?>(SqlOSFgaLineage.ScopeAncestorColumn(level)));
            }

            SqlOSFgaModelConfiguration.DatabaseOwned(entity.Property<short?>(SqlOSFgaLineage.ScopeReachColumn));
            SqlOSFgaModelConfiguration.DatabaseOwned(entity.Property<int?>(SqlOSFgaLineage.ScopeTypeSeqColumn));

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

            // The indexes: per level, the ancestor column followed by the primary key, and the ancestor
            // column followed by each declared index's columns and then the key (a cursor page orders by the
            // declared columns, then the key). Filtered to rows at or below the level, so a level deeper than
            // any row of the table costs nothing. Every level has at least the key index, which is what keeps
            // a single-grant caller's page an index seek instead of a table scan.
            var key = entityType.FindPrimaryKey()!.Properties.Select(p => p.Name).ToArray();
            var mirrored = entityType.GetIndexes()
                .Where(i => IsDeclaredOrder(entityType, i, resourceId))
                .Select(i => (Name: MirrorSuffix(table, i), Columns: i.Properties.Select(p => p.Name).Concat(key).Distinct().ToArray()))
                .ToList();
            for (var level = 0; level < levels; level++)
            {
                var ancestor = SqlOSFgaLineage.ScopeAncestorColumn(level);
                AddIndex(entity, provider, ancestor, key, SqlOSFgaLineage.ScopeIndexName(table, level, null));
                foreach (var (name, columns) in mirrored)
                {
                    AddIndex(entity, provider, ancestor, columns, SqlOSFgaLineage.ScopeIndexName(table, level, name));
                }
            }
        }
    }

    /// <summary>The application tables of the model that carry the scope columns, for the database routines.</summary>
    public static IReadOnlyList<SqlOSFgaScopeTable> Tables(IModel model)
    {
        var tables = new List<SqlOSFgaScopeTable>();
        var defaultSchema = model.GetDefaultSchema();
        foreach (var entityType in model.GetEntityTypes())
        {
            if (entityType.FindAnnotation(SqlOSFgaLineage.ScopeAnnotation)?.Value is not true)
            {
                continue;
            }

            var table = entityType.GetTableName()!;
            var schema = entityType.GetSchema() ?? defaultSchema;
            var store = StoreObjectIdentifier.Table(table, schema);
            var resourceId = entityType.FindProperty(nameof(IHasResourceId.ResourceId))!.GetColumnName(store)!;
            var key = entityType.FindPrimaryKey()!.Properties.Select(p => p.GetColumnName(store)!).ToList();
            tables.Add(new SqlOSFgaScopeTable(schema, table, resourceId, key));
        }

        return tables.OrderBy(t => t.Schema, StringComparer.Ordinal).ThenBy(t => t.Table, StringComparer.Ordinal).ToList();
    }

    /// <summary>Whether an entity type's table carries the scope columns.</summary>
    public static bool Has(IEntityType entityType)
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
    /// not the resource id lookup, and not an index that merely covers a foreign key.
    /// </summary>
    private static bool IsDeclaredOrder(IMutableEntityType entityType, IMutableIndex index, IMutableProperty resourceId)
        => !index.IsUnique
           && index.Properties[0] != resourceId
           && !index.Properties.Any(p => p.Name.StartsWith(SqlOSFgaLineage.ScopePrefix, StringComparison.Ordinal))
           && !entityType.GetForeignKeys().Any(fk => fk.Properties.SequenceEqual(index.Properties));

    /// <summary>What names a mirror of a declared index: the index's name without the conventional <c>IX_{table}_</c> prefix.</summary>
    private static string MirrorSuffix(string table, IMutableIndex index)
    {
        var name = index.GetDatabaseName() ?? string.Join("_", index.Properties.Select(p => p.Name));
        var prefix = $"IX_{table}_";
        return name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length ? name[prefix.Length..] : name;
    }

    private static void AddIndex(EntityTypeBuilder entity, ISqlOSDatabaseProvider provider, string ancestor, string[] columns, string name)
    {
        var index = entity.HasIndex([ancestor, .. columns.Where(c => c != ancestor)], name)
            .HasFilter(provider.FilteredIndexIsNotNull(ancestor));
        if (provider.EfProviderName == SqlOSDatabase.PostgreSqlProviderName)
        {
            NpgsqlIndexBuilderExtensions.IncludeProperties(index, SqlOSFgaLineage.ScopeReachColumn, SqlOSFgaLineage.ScopeTypeSeqColumn);
        }
        else
        {
            SqlServerIndexBuilderExtensions.IncludeProperties(index, SqlOSFgaLineage.ScopeReachColumn, SqlOSFgaLineage.ScopeTypeSeqColumn);
        }
    }
}

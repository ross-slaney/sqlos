using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Configuration;

/// <summary>
/// Every application entity with a resource id (<see cref="IHasResourceId"/>), as SqlOS configures it: the
/// descriptive members of a <see cref="SqlOSResourceEntity"/> feed resource synchronization and are never
/// columns, the base class itself is never mapped, and the resource id carries an index, which a filtered
/// query joins the visible resources to. Nothing else of SqlOS's is on the table.
/// </summary>
internal static class SqlOSFgaResourceEntities
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
    public static void Configure(ModelBuilder modelBuilder)
    {
        if (modelBuilder.Model.FindEntityType(typeof(SqlOSResourceEntity)) is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(SqlOSResourceEntity)} is the base class of the application's entities and cannot be "
                + "mapped as an entity itself (an EF Core hierarchy would put every derived entity in one "
                + "table, and SqlOS would protect none of them). Remove its DbSet or mapping and map the "
                + "entities that derive from it.");
        }

        var sqlosAssembly = typeof(SqlOSFgaResourceEntities).Assembly;
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

            var resourceId = entityType.FindProperty(nameof(IHasResourceId.ResourceId))!;
            if (!entityType.GetIndexes().Any(i => i.Properties[0] == resourceId))
            {
                entity.HasIndex([resourceId.Name], SqlOSFgaLineage.ResourceIdIndexName(entityType.GetTableName()!));
            }
        }
    }

    /// <summary>
    /// An application entity type with a resource id: it implements <see cref="IHasResourceId"/>, maps its
    /// resource id, is mapped to a table of its own, and is not one of SqlOS's.
    /// </summary>
    private static bool IsProtected(IReadOnlyEntityType entityType, System.Reflection.Assembly sqlosAssembly)
        => typeof(IHasResourceId).IsAssignableFrom(entityType.ClrType)
           && entityType.ClrType.Assembly != sqlosAssembly
           && !entityType.IsOwned()
           && entityType.FindPrimaryKey() is not null
           && entityType.GetTableName() is not null
           && entityType.BaseType is null
           && entityType.FindProperty(nameof(IHasResourceId.ResourceId)) is not null;
}

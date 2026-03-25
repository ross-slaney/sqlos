using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Configuration;

/// <summary>
/// Applies SqlOS FGA model conventions to consumer entities.
/// </summary>
internal static class SqlOSFgaConventionsApplier
{
    private const string ResourceIdPropertyName = nameof(IHasResourceId.ResourceId);

    /// <summary>
    /// Iterates all entity types in the model, finds those that implement
    /// <see cref="IHasResourceId"/>, and optionally adds a <c>ResourceId</c> index
    /// and/or validates that one exists.
    /// </summary>
    internal static void Apply(ModelBuilder modelBuilder, SqlOSFgaConventionsOptions options)
    {
        if (!options.AutoIndexResourceIds && !options.ValidateResourceIdIndexes)
            return;

        var resourceIdInterface = typeof(IHasResourceId);

        // Snapshot the entity list before any modifications to avoid
        // mutating a collection we are iterating over.
        var entityTypes = modelBuilder.Model.GetEntityTypes().ToList();

        // Phase 1: auto-index
        if (options.AutoIndexResourceIds)
        {
            foreach (var entityType in entityTypes)
            {
                if (!resourceIdInterface.IsAssignableFrom(entityType.ClrType))
                    continue;

                if (entityType.FindProperty(ResourceIdPropertyName) == null)
                    continue;

                if (!HasCompatibleIndex(entityType))
                {
                    modelBuilder.Entity(entityType.ClrType)
                        .HasIndex(ResourceIdPropertyName);
                }
            }
        }

        // Phase 2: validate (runs after auto-indexing, so if both flags are true
        // the auto-added indexes satisfy the validation and no exception is thrown)
        if (options.ValidateResourceIdIndexes)
        {
            var missing = new List<string>();

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                if (!resourceIdInterface.IsAssignableFrom(entityType.ClrType))
                    continue;

                if (entityType.FindProperty(ResourceIdPropertyName) == null)
                    continue;

                if (!HasCompatibleIndex(entityType))
                    missing.Add(entityType.ClrType.Name);
            }

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The following entities implement {nameof(IHasResourceId)} but have no index on " +
                    $"'{ResourceIdPropertyName}': {string.Join(", ", missing)}. " +
                    $"Add an index manually or enable {nameof(SqlOSFgaConventionsOptions.AutoIndexResourceIds)}.");
            }
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the entity already has an index whose leading column is
    /// <c>ResourceId</c> (covers both single-column and composite indexes).
    /// </summary>
    private static bool HasCompatibleIndex(IReadOnlyEntityType entityType)
        => entityType.GetIndexes()
            .Any(idx => idx.Properties.Count > 0 &&
                        string.Equals(
                            idx.Properties[0].Name,
                            ResourceIdPropertyName,
                            StringComparison.Ordinal));
}

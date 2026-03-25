using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Extensions;

public static class ModelBuilderExtensions
{
    /// <summary>
    /// Applies the SqlOSFga entity model configuration and registers the TVF.
    /// Call this from your DbContext's OnModelCreating method.
    /// Pass GetType() so the TVF can be registered on the correct DbContext type.
    /// </summary>
    /// <example>
    /// modelBuilder.ApplySqlOSFgaModel(GetType());
    /// </example>
    public static ModelBuilder ApplySqlOSFgaModel(
        this ModelBuilder modelBuilder,
        Type contextType,
        Action<SqlOSFgaOptions>? configure = null)
    {
        var options = new SqlOSFgaOptions();
        configure?.Invoke(options);

        SqlOSFgaModelConfiguration.Configure(modelBuilder, options, contextType);

        return modelBuilder;
    }

    /// <summary>
    /// Applies the SqlOSFga entity model configuration WITHOUT TVF registration.
    /// Use this for InMemory/unit test contexts where TVFs are not supported.
    /// </summary>
    public static ModelBuilder ApplySqlOSFgaModel(
        this ModelBuilder modelBuilder,
        Action<SqlOSFgaOptions>? configure = null)
    {
        var options = new SqlOSFgaOptions();
        configure?.Invoke(options);

        SqlOSFgaModelConfiguration.Configure(modelBuilder, options, contextType: null);

        return modelBuilder;
    }

    /// <summary>
    /// Applies SqlOS FGA model conventions to consumer entities that implement
    /// <see cref="SqlOS.Fga.Interfaces.IHasResourceId"/>.
    /// <para>
    /// Call this method at the <strong>end</strong> of <c>OnModelCreating</c>, after all
    /// consumer entity types have been registered, so that every mapped
    /// <see cref="SqlOS.Fga.Interfaces.IHasResourceId"/> entity is visible to the scanner.
    /// </para>
    /// <para>
    /// By default (<see cref="SqlOSFgaConventionsOptions.AutoIndexResourceIds"/> = <c>true</c>),
    /// a <c>ResourceId</c> index is added to every entity that does not already have a compatible
    /// index (one whose leading column is <c>ResourceId</c>).  Set
    /// <see cref="SqlOSFgaConventionsOptions.ValidateResourceIdIndexes"/> = <c>true</c> to throw
    /// at startup when an entity is still unindexed after conventions are applied.
    /// </para>
    /// </summary>
    /// <example>
    /// protected override void OnModelCreating(ModelBuilder modelBuilder)
    /// {
    ///     modelBuilder.UseSqlOS(GetType());
    ///
    ///     modelBuilder.Entity&lt;Chain&gt;(e => { e.HasKey(x => x.Id); /* ... */ });
    ///
    ///     // Call last so all entities are registered before scanning.
    ///     modelBuilder.ApplySqlOSFgaConventions();
    /// }
    /// </example>
    public static ModelBuilder ApplySqlOSFgaConventions(
        this ModelBuilder modelBuilder,
        Action<SqlOSFgaConventionsOptions>? configure = null)
    {
        var options = new SqlOSFgaConventionsOptions();
        configure?.Invoke(options);

        SqlOSFgaConventionsApplier.Apply(modelBuilder, options);

        return modelBuilder;
    }
}

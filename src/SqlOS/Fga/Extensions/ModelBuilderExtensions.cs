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
        => modelBuilder.ApplySqlOSFgaModel(contextType, providerName: null, configure);

    /// <summary>
    /// Applies the SqlOSFga entity model configuration, registers the TVF, and, when
    /// <see cref="SqlOSFgaOptions.ScopeColumns"/> is on, adds the scope columns to every entity configured so
    /// far whose type implements <c>IHasResourceId</c>. With scope columns on, call this after configuring
    /// the application's entities and pass <c>Database.ProviderName</c>.
    /// </summary>
    public static ModelBuilder ApplySqlOSFgaModel(
        this ModelBuilder modelBuilder,
        Type contextType,
        string? providerName,
        Action<SqlOSFgaOptions>? configure = null)
    {
        var options = new SqlOSFgaOptions();
        configure?.Invoke(options);

        SqlOSFgaModelConfiguration.Configure(modelBuilder, options, contextType);
        SqlOSFgaScopeColumns.Configure(modelBuilder, options, providerName);

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
}

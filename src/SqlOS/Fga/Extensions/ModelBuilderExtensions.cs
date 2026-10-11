using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga.Extensions;

public static class ModelBuilderExtensions
{
    /// <summary>
    /// Applies the SqlOS FGA entity model (without the auth server's) and indexes the resource id of every
    /// application entity that implements <c>IHasResourceId</c>. Call it last in <c>OnModelCreating</c>,
    /// after your own entities. Most applications call <c>UseSqlOS</c>, which applies this and the auth
    /// server model, or derive from <c>SqlOSDbContext&lt;TContext&gt;</c>, which calls it.
    /// </summary>
    /// <example>
    /// modelBuilder.ApplySqlOSFgaModel();
    /// </example>
    public static ModelBuilder ApplySqlOSFgaModel(
        this ModelBuilder modelBuilder,
        Action<SqlOSFgaOptions>? configure = null)
    {
        var options = new SqlOSFgaOptions();
        configure?.Invoke(options);

        SqlOSFgaModelConfiguration.Configure(modelBuilder, options);
        SqlOSFgaResourceEntities.Configure(modelBuilder);

        return modelBuilder;
    }
}

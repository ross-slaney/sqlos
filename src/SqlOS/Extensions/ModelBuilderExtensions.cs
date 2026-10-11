using Microsoft.EntityFrameworkCore;
using SqlOS.Configuration;
using SqlOS.AuthServer.Configuration;
using SqlOS.Calendar.Configuration;
using SqlOS.Database;
using SqlOS.Email.Configuration;
using SqlOS.Fga.Configuration;

namespace SqlOS.Extensions;

public static class ModelBuilderExtensions
{
    /// <summary>
    /// Registers the SqlOS auth server and FGA EF models, and indexes the resource id of every application
    /// entity that implements <c>IHasResourceId</c>. Call it last in <c>OnModelCreating</c>, after your own
    /// entities, and pass <c>Database.ProviderName</c> so SqlOS maps its columns for that provider.
    /// </summary>
    public static ModelBuilder UseSqlOS(this ModelBuilder modelBuilder, string? providerName = null)
        => modelBuilder.UseSqlOS(providerName, new SqlOSFgaOptions());

    /// <summary>
    /// Registers the SqlOS auth server and FGA EF models with the given FGA options (their schema and table
    /// names), and indexes the resource id of every application entity that implements <c>IHasResourceId</c>.
    /// </summary>
    public static ModelBuilder UseSqlOS(this ModelBuilder modelBuilder, string? providerName, SqlOSFgaOptions fgaOptions)
    {
        ArgumentNullException.ThrowIfNull(fgaOptions);
        SqlOSAuthServerModelConfiguration.Configure(modelBuilder, new SqlOSAuthServerOptions(), providerName);
        SqlOSEmailModelConfiguration.Configure(modelBuilder, new SqlOSAuthServerOptions().Schema, providerName);
        SqlOSCalendarModelConfiguration.Configure(modelBuilder, new SqlOSAuthServerOptions().Schema);
        SqlOSFgaModelConfiguration.Configure(modelBuilder, fgaOptions);
        SqlOSFgaResourceEntities.Configure(modelBuilder);
        if (SqlOSDatabase.IsPostgreSql(providerName))
        {
            SqlOSDatabase.EnablePostgreSqlTimestampCompatibility();
            var sqlosAssembly = typeof(SqlOSDatabase).Assembly;
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                if (entityType.ClrType.Assembly != sqlosAssembly)
                {
                    continue;
                }

                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                    {
                        property.SetColumnType("timestamp without time zone");
                    }
                }
            }
        }

        return modelBuilder;
    }
}

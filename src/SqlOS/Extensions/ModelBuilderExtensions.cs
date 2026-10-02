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
    /// Registers SqlOS auth server and FGA EF models.
    /// </summary>
    public static ModelBuilder UseSqlOS(this ModelBuilder modelBuilder, Type? contextType = null, string? providerName = null)
        => modelBuilder.UseSqlOS(contextType, providerName, new SqlOSFgaOptions());

    /// <summary>
    /// Registers SqlOS auth server and FGA EF models with the given FGA options (the configured hierarchy
    /// depth sets the number of ancestor columns the model declares).
    /// </summary>
    public static ModelBuilder UseSqlOS(this ModelBuilder modelBuilder, Type? contextType, string? providerName, SqlOSFgaOptions fgaOptions)
    {
        ArgumentNullException.ThrowIfNull(fgaOptions);
        SqlOSAuthServerModelConfiguration.Configure(modelBuilder, new SqlOSAuthServerOptions(), providerName);
        SqlOSEmailModelConfiguration.Configure(modelBuilder, new SqlOSAuthServerOptions().Schema, providerName);
        SqlOSCalendarModelConfiguration.Configure(modelBuilder, new SqlOSAuthServerOptions().Schema);
        SqlOSFgaModelConfiguration.Configure(modelBuilder, fgaOptions, contextType);
        SqlOSFgaScopeColumns.Configure(modelBuilder, fgaOptions);
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

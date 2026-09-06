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
    {
        SqlOSAuthServerModelConfiguration.Configure(modelBuilder, new SqlOSAuthServerOptions(), providerName);
        SqlOSEmailModelConfiguration.Configure(modelBuilder, new SqlOSAuthServerOptions().Schema, providerName);
        SqlOSCalendarModelConfiguration.Configure(modelBuilder, new SqlOSAuthServerOptions().Schema);
        SqlOSFgaModelConfiguration.Configure(modelBuilder, new SqlOSFgaOptions(), contextType);
        if (SqlOSDatabase.IsPostgreSql(providerName))
        {
            SqlOSDatabase.EnablePostgreSqlTimestampCompatibility();
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
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

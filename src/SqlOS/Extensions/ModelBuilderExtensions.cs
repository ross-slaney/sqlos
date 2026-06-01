using Microsoft.EntityFrameworkCore;
using SqlOS.Configuration;
using SqlOS.AuthServer.Configuration;
using SqlOS.Email.Configuration;
using SqlOS.Fga.Configuration;

namespace SqlOS.Extensions;

public static class ModelBuilderExtensions
{
    /// <summary>
    /// Registers SqlOS auth server and FGA EF models.
    /// </summary>
    public static ModelBuilder UseSqlOS(this ModelBuilder modelBuilder, Type? contextType = null)
    {
        SqlOSAuthServerModelConfiguration.Configure(modelBuilder, new SqlOSAuthServerOptions());
        SqlOSEmailModelConfiguration.Configure(modelBuilder, new SqlOSAuthServerOptions().Schema);
        SqlOSFgaModelConfiguration.Configure(modelBuilder, new SqlOSFgaOptions(), contextType);
        return modelBuilder;
    }
}

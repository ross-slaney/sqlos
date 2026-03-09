using Microsoft.EntityFrameworkCore;
using SqlOS.Configuration;
using SqlOS.AuthServer.Configuration;
using SqlOS.Fga.Configuration;

namespace SqlOS.Extensions;

public static class ModelBuilderExtensions
{
    public static ModelBuilder UseAuthServer(
        this ModelBuilder modelBuilder,
        Action<SqlOSAuthServerOptions>? configure = null)
    {
        var options = new SqlOSAuthServerOptions();
        configure?.Invoke(options);

        SqlOSAuthServerModelConfiguration.Configure(modelBuilder, options);
        return modelBuilder;
    }

    public static ModelBuilder UseFGA(
        this ModelBuilder modelBuilder,
        Type contextType,
        Action<SqlOSFgaOptions>? configure = null)
    {
        var options = new SqlOSFgaOptions();
        configure?.Invoke(options);

        SqlOSFgaModelConfiguration.Configure(modelBuilder, options, contextType);
        return modelBuilder;
    }

    public static ModelBuilder UseFGA(
        this ModelBuilder modelBuilder,
        Action<SqlOSFgaOptions>? configure = null)
    {
        var options = new SqlOSFgaOptions();
        configure?.Invoke(options);

        SqlOSFgaModelConfiguration.Configure(modelBuilder, options, contextType: null);
        return modelBuilder;
    }
}

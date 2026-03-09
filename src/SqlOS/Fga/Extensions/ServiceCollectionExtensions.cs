using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Services;

namespace SqlOS.Fga.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSqlOSFga<TContext>(
        this IServiceCollection services,
        Action<SqlOSFgaOptions>? configure = null)
        where TContext : DbContext, ISqlOSFgaDbContext
    {
        var options = new SqlOSFgaOptions();
        configure?.Invoke(options);

        services.Configure<SqlOSFgaOptions>(o =>
        {
            o.Schema = options.Schema;
            o.RootResourceId = options.RootResourceId;
            o.RootResourceName = options.RootResourceName;
            o.InitializeFunctions = options.InitializeFunctions;
            o.SeedCoreData = options.SeedCoreData;
            o.SchemaYamlPath = options.SchemaYamlPath;
            o.DashboardPathPrefix = options.DashboardPathPrefix;
            o.TableNames = options.TableNames;
        });

        services.AddScoped<ISqlOSFgaDbContext>(sp => sp.GetRequiredService<TContext>());
        services.AddScoped<ISqlOSFgaAuthService, SqlOSFgaAuthService>();
        services.AddScoped<ISqlOSFgaSubjectService, SqlOSFgaSubjectService>();
        services.AddScoped<ISpecificationExecutor, SpecificationExecutor>();
        services.AddScoped<SqlOSFgaSeedService>();
        services.AddScoped<SqlOSFgaFunctionInitializer>();
        services.AddScoped<SqlOSFgaSchemaInitializer>();

        return services;
    }
}

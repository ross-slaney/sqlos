using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sqlzibar.Configuration;
using Sqlzibar.Interfaces;
using Sqlzibar.Services;

namespace Sqlzibar.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSqlzibar<TContext>(
        this IServiceCollection services,
        Action<SqlzibarOptions>? configure = null)
        where TContext : DbContext, ISqlzibarDbContext
    {
        var options = new SqlzibarOptions();
        configure?.Invoke(options);

        services.Configure<SqlzibarOptions>(o =>
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

        services.AddScoped<ISqlzibarDbContext>(sp => sp.GetRequiredService<TContext>());
        services.AddScoped<ISqlzibarAuthService, SqlzibarAuthService>();
        services.AddScoped<ISqlzibarSubjectService, SqlzibarSubjectService>();
        services.AddScoped<ISpecificationExecutor, SpecificationExecutor>();
        services.AddScoped<SqlzibarSeedService>();
        services.AddScoped<SqlzibarFunctionInitializer>();
        services.AddScoped<SqlzibarSchemaInitializer>();

        return services;
    }
}

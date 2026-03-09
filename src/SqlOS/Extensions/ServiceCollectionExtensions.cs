using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SqlOS.Configuration;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Services;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Services;
using SqlOS.Services;

namespace SqlOS.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSqlOS<TContext>(
        this IServiceCollection services,
        Action<SqlOSOptions>? configure = null)
        where TContext : DbContext, ISqlOSAuthServerDbContext, ISqlOSFgaDbContext
    {
        var options = new SqlOSOptions();
        configure?.Invoke(options);

        if (options.Dashboard.AuthorizationCallback != null)
        {
            options.AuthServer.Dashboard.AuthorizationCallback ??= options.Dashboard.AuthorizationCallback;
            options.Fga.Dashboard.AuthorizationCallback ??= options.Dashboard.AuthorizationCallback;
        }

        services.AddSingleton(Options.Create(options));
        services.AddSingleton(Options.Create(options.AuthServer));
        services.AddSingleton(Options.Create(options.Fga));

        services.AddScoped<ISqlOSAuthServerDbContext>(sp => sp.GetRequiredService<TContext>());
        services.AddScoped<ISqlOSFgaDbContext>(sp => sp.GetRequiredService<TContext>());

        services.AddScoped<SqlOSSchemaInitializer>();
        services.AddScoped<SqlOSBootstrapper>();
        services.AddScoped<SqlOSCryptoService>();
        services.AddScoped<SqlOSSettingsService>();
        services.AddScoped<SqlOSAdminService>();
        services.AddScoped<SqlOSAuthService>();
        services.AddScoped<SqlOSHomeRealmDiscoveryService>();
        services.AddScoped<SqlOSSamlService>();
        services.AddScoped<SqlOSSsoAuthorizationService>();
        services.AddScoped<ISqlOSFgaAuthService, SqlOSFgaAuthService>();
        services.AddScoped<ISqlOSFgaSubjectService, SqlOSFgaSubjectService>();
        services.AddScoped<ISpecificationExecutor, SpecificationExecutor>();
        services.AddScoped<SqlOSFgaSeedService>();
        services.AddScoped<SqlOSFgaFunctionInitializer>();
        services.AddScoped<SqlOSFgaSchemaInitializer>();

        return services;
    }
}

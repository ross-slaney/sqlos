using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SqlOS.Configuration;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Services;
using SqlOS.Dashboard;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Services;
using SqlOS.Hosting;
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

        SqlOSPathDefaults.Apply(options);
        options.AuthServer.Dashboard = options.Dashboard;
        options.Fga.Dashboard = options.Dashboard;
        SqlOSOptionsValidator.ValidateOrThrow(options);

        services.AddSingleton(Options.Create(options));
        services.AddSingleton(Options.Create(options.AuthServer));
        services.AddSingleton(Options.Create(options.Fga));
        services.AddDataProtection();
        services.AddHttpClient();
        services.AddSingleton<SqlOSDashboardSessionService>();

        services.AddScoped<ISqlOSAuthServerDbContext>(sp => sp.GetRequiredService<TContext>());
        services.AddScoped<ISqlOSFgaDbContext>(sp => sp.GetRequiredService<TContext>());

        services.AddScoped<SqlOSSchemaInitializer>();
        services.AddScoped<SqlOSBootstrapper>();
        services.AddScoped<SqlOSCryptoService>();
        services.AddScoped<SqlOSSettingsService>();
        services.AddScoped<SqlOSAdminService>();
        services.AddScoped<SqlOSAuthService>();
        services.AddScoped<SqlOSAuthPageSessionService>();
        services.AddScoped<SqlOSAuthorizationServerService>();
        services.AddScoped<SqlOSHeadlessAuthService>();
        services.AddScoped<SqlOSHomeRealmDiscoveryService>();
        services.AddScoped<SqlOSOidcAuthService>();
        services.AddScoped<SqlOSOidcBrowserAuthService>();
        services.AddScoped<SqlOSSamlService>();
        services.AddScoped<SqlOSSsoAuthorizationService>();
        services.AddScoped<ISqlOSFgaAuthService, SqlOSFgaAuthService>();
        services.AddScoped<ISqlOSFgaSubjectService, SqlOSFgaSubjectService>();
        services.AddScoped<ISpecificationExecutor, SpecificationExecutor>();
        services.AddScoped<SqlOSFgaSeedService>();
        services.AddScoped<SqlOSFgaFunctionInitializer>();
        services.AddScoped<SqlOSFgaSchemaInitializer>();
        services.AddHostedService<SqlOSSigningKeyRotationService>();
        services.AddHostedService<SqlOSBootstrapHostedService>();
        services.AddSingleton<IStartupFilter, SqlOSPipelineStartupFilter>();

        return services;
    }
}

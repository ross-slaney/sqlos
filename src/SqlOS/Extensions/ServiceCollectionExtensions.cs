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

        if (options.Dashboard.AuthorizationCallback != null)
        {
            options.AuthServer.Dashboard.AuthorizationCallback ??= options.Dashboard.AuthorizationCallback;
            options.Fga.Dashboard.AuthorizationCallback ??= options.Dashboard.AuthorizationCallback;
        }

        if (options.Dashboard.AuthMode != SqlOSDashboardAuthMode.DevelopmentOnly
            && options.AuthServer.Dashboard.AuthMode == SqlOSDashboardAuthMode.DevelopmentOnly)
        {
            options.AuthServer.Dashboard.AuthMode = options.Dashboard.AuthMode;
        }

        if (options.Dashboard.AuthMode != SqlOSDashboardAuthMode.DevelopmentOnly
            && options.Fga.Dashboard.AuthMode == SqlOSDashboardAuthMode.DevelopmentOnly)
        {
            options.Fga.Dashboard.AuthMode = options.Dashboard.AuthMode;
        }

        if (!string.IsNullOrWhiteSpace(options.Dashboard.Password))
        {
            options.AuthServer.Dashboard.Password ??= options.Dashboard.Password;
            options.Fga.Dashboard.Password ??= options.Dashboard.Password;
        }

        if (options.Dashboard.SessionLifetime != SqlOSDashboardOptions.DefaultSessionLifetime)
        {
            if (options.AuthServer.Dashboard.SessionLifetime == SqlOSDashboardOptions.DefaultSessionLifetime)
            {
                options.AuthServer.Dashboard.SessionLifetime = options.Dashboard.SessionLifetime;
            }

            if (options.Fga.Dashboard.SessionLifetime == SqlOSDashboardOptions.DefaultSessionLifetime)
            {
                options.Fga.Dashboard.SessionLifetime = options.Dashboard.SessionLifetime;
            }
        }

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

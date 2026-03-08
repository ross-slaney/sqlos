using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Sqlzibar.Configuration;
using Sqlzibar.Dashboard;
using Sqlzibar.Services;

namespace Sqlzibar.Extensions;

public static class ApplicationBuilderExtensions
{
    public static async Task UseSqlzibarAsync(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<SqlzibarOptions>>().Value;

        var schemaInitializer = scope.ServiceProvider.GetRequiredService<SqlzibarSchemaInitializer>();
        await schemaInitializer.EnsureSchemaAsync();

        if (options.InitializeFunctions)
        {
            var initializer = scope.ServiceProvider.GetRequiredService<SqlzibarFunctionInitializer>();
            await initializer.EnsureFunctionsExistAsync();
        }

        if (options.SeedCoreData)
        {
            var seeder = scope.ServiceProvider.GetRequiredService<SqlzibarSeedService>();
            await seeder.SeedCoreAsync();
        }

        if (!string.IsNullOrEmpty(options.SchemaYamlPath) && File.Exists(options.SchemaYamlPath))
        {
            var seeder = scope.ServiceProvider.GetRequiredService<SqlzibarSeedService>();
            await seeder.SeedFromYamlFileAsync(options.SchemaYamlPath);
        }
    }

    public static IApplicationBuilder UseSqlzibarDashboard(
        this IApplicationBuilder app,
        string? pathPrefix = null)
    {
        var options = app.ApplicationServices.GetRequiredService<IOptions<SqlzibarOptions>>().Value;
        var environment = app.ApplicationServices.GetRequiredService<IHostEnvironment>();
        var prefix = pathPrefix ?? options.DashboardPathPrefix;

        app.UseMiddleware<SqlzibarDashboardMiddleware>(prefix, environment, options.Dashboard);

        return app;
    }
}

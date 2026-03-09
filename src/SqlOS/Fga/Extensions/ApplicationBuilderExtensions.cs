using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Dashboard;
using SqlOS.Fga.Services;

namespace SqlOS.Fga.Extensions;

public static class ApplicationBuilderExtensions
{
    public static async Task UseSqlOSFgaAsync(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<SqlOSFgaOptions>>().Value;

        var schemaInitializer = scope.ServiceProvider.GetRequiredService<SqlOSFgaSchemaInitializer>();
        await schemaInitializer.EnsureSchemaAsync();

        if (options.InitializeFunctions)
        {
            var initializer = scope.ServiceProvider.GetRequiredService<SqlOSFgaFunctionInitializer>();
            await initializer.EnsureFunctionsExistAsync();
        }

        if (options.SeedCoreData)
        {
            var seeder = scope.ServiceProvider.GetRequiredService<SqlOSFgaSeedService>();
            await seeder.SeedCoreAsync();
        }

        if (!string.IsNullOrEmpty(options.SchemaYamlPath) && File.Exists(options.SchemaYamlPath))
        {
            var seeder = scope.ServiceProvider.GetRequiredService<SqlOSFgaSeedService>();
            await seeder.SeedFromYamlFileAsync(options.SchemaYamlPath);
        }
    }

    public static IApplicationBuilder UseSqlOSFgaDashboard(
        this IApplicationBuilder app,
        string? pathPrefix = null)
    {
        var options = app.ApplicationServices.GetRequiredService<IOptions<SqlOSFgaOptions>>().Value;
        var environment = app.ApplicationServices.GetRequiredService<IHostEnvironment>();
        var prefix = pathPrefix ?? options.DashboardPathPrefix;

        app.UseMiddleware<SqlOSFgaDashboardMiddleware>(prefix, environment, options.Dashboard);

        return app;
    }
}

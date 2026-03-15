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
            o.DashboardPathPrefix = options.DashboardPathPrefix;
            o.TableNames = options.TableNames;
            if (options.StartupSeedData != null)
            {
                o.Seed(builder =>
                {
                    foreach (var resourceType in options.StartupSeedData.ResourceTypes ?? [])
                    {
                        builder.ResourceType(resourceType.Id, resourceType.Name, resourceType.Description);
                    }

                    foreach (var permission in options.StartupSeedData.Permissions ?? [])
                    {
                        builder.Permission(
                            permission.Id,
                            permission.Key,
                            permission.Name,
                            permission.ResourceTypeId ?? throw new InvalidOperationException($"Seeded permission '{permission.Key}' is missing a resource type."),
                            permission.Description);
                    }

                    foreach (var role in options.StartupSeedData.Roles ?? [])
                    {
                        builder.Role(role.Id, role.Key, role.Name, role.Description, role.IsVirtual);
                    }

                    foreach (var (roleKey, permissionKeys) in options.StartupSeedData.RolePermissions ?? [])
                    {
                        foreach (var permissionKey in permissionKeys)
                        {
                            builder.RolePermission(roleKey, permissionKey);
                        }
                    }
                });
            }
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

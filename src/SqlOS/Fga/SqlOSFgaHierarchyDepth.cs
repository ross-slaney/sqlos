using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga;

internal static class SqlOSFgaHierarchyDepth
{
    public const int Default = 10;
    public const int SqlServerRecursiveCteMaximum = 100;
    public const string ModelAnnotationName = "SqlOS:Fga:MaxResourceHierarchyDepth";

    public static int Resolve(ISqlOSFgaDbContext context)
        => Resolve(context.Database, context as DbContext);

    /// <summary>
    /// Resolves the configured FGA hierarchy depth for any SqlOS DbContext, including one used
    /// through the AuthServer interface, so every ancestor walk applies the same bound.
    /// </summary>
    public static int Resolve(DatabaseFacade database, DbContext? dbContext)
    {
        try
        {
            return Normalize(database
                .GetService<IOptions<SqlOSFgaOptions>>()
                .Value
                .MaxResourceHierarchyDepth);
        }
        catch (InvalidOperationException)
        {
            // Manually constructed DbContexts do not always have application services.
        }

        if (dbContext?.Model.FindAnnotation(ModelAnnotationName)?.Value is int annotated)
        {
            return Normalize(annotated);
        }

        return Default;
    }

    public static int Normalize(int configured)
        => Math.Max(1, configured);
}

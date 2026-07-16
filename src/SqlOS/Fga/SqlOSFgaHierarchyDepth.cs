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
    {
        try
        {
            return Normalize(context.Database
                .GetService<IOptions<SqlOSFgaOptions>>()
                .Value
                .MaxResourceHierarchyDepth);
        }
        catch (InvalidOperationException)
        {
            // Manually constructed DbContexts do not always have application services.
        }

        if (context is DbContext dbContext
            && dbContext.Model.FindAnnotation(ModelAnnotationName)?.Value is int annotated)
        {
            return Normalize(annotated);
        }

        return Default;
    }

    public static int Normalize(int configured)
        => Math.Max(1, configured);
}

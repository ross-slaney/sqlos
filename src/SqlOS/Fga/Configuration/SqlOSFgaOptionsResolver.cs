using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;

namespace SqlOS.Fga.Configuration;

/// <summary>The FGA options registered with the application, when the context can reach them; defaults otherwise.</summary>
internal static class SqlOSFgaOptionsResolver
{
    public static SqlOSFgaOptions Resolve(DatabaseFacade database)
    {
        try
        {
            return database.GetService<IOptions<SqlOSFgaOptions>>().Value;
        }
        catch (InvalidOperationException)
        {
            // Manually constructed DbContexts do not always have application services.
            return new SqlOSFgaOptions();
        }
    }
}

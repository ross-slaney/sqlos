using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Fga;

/// <summary>What the FGA write paths share: the host's clock and the 7.x argument rules.</summary>
internal static class SqlOSFgaWrites
{
    /// <summary>The host's <see cref="TimeProvider"/> as the context's services hold it, else the system clock.</summary>
    public static DateTime Now(DbContext context)
    {
        var services = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;
        return (services?.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime;
    }

    public static DateTime Now(ISqlOSFgaDbContext context) => Now((DbContext)context);

    public static string RequireValue(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{paramName} is required.");
        }

        return value.Trim();
    }

    public static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The deterministic identifier of a subject's typed record, such as <c>usr::…</c> for a user.</summary>
    public static string TypedRecordId(string prefix, string subjectId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{prefix}\n{subjectId}"));
        return $"{prefix}::{Convert.ToHexString(bytes).ToLowerInvariant()[..32]}";
    }

    /// <summary>
    /// Whether <paramref name="resourceId"/> belongs to an <see cref="ISqlOSResourceEntity"/> this
    /// unit adds or changes and a <c>SqlOSDbContext&lt;TContext&gt;</c> save will synchronize.
    /// </summary>
    public static bool IsPendingResourceEntity(ISqlOSFgaDbContext context, string resourceId)
        => context is DbContext dbContext
            && IsResourceEntitySyncContext(dbContext)
            && dbContext.ChangeTracker.Entries().Any(entry =>
                entry.Entity is ISqlOSResourceEntity resourceEntity
                && entry.State is EntityState.Added or EntityState.Modified
                && string.Equals(NormalizeOptional(resourceEntity.ResourceId), resourceId, StringComparison.Ordinal));

    private static bool IsResourceEntitySyncContext(DbContext context)
    {
        for (var type = context.GetType(); type != null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SqlOSDbContext<>))
            {
                return true;
            }
        }

        return false;
    }
}

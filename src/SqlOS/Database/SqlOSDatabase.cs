using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace SqlOS.Database;

internal static class SqlOSDatabase
{
    public const string SqlServerProviderName = "Microsoft.EntityFrameworkCore.SqlServer";
    public const string PostgreSqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";
    public const string InMemoryProviderName = "Microsoft.EntityFrameworkCore.InMemory";

    public static bool IsSqlServer(string? providerName)
        => string.Equals(providerName, SqlServerProviderName, StringComparison.Ordinal);

    public static bool IsPostgreSql(string? providerName)
        => string.Equals(providerName, PostgreSqlProviderName, StringComparison.Ordinal);

    public static bool IsInMemory(string? providerName)
        => string.Equals(providerName, InMemoryProviderName, StringComparison.Ordinal);

    public static void EnablePostgreSqlTimestampCompatibility()
        => AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

    public static ISqlOSDatabaseProvider Resolve(string? providerName)
    {
        if (IsSqlServer(providerName))
        {
            return SqlServerDatabaseProvider.Instance;
        }

        if (IsPostgreSql(providerName))
        {
            EnablePostgreSqlTimestampCompatibility();
            return PostgreSqlDatabaseProvider.Instance;
        }

        throw new InvalidOperationException(
            $"SqlOS does not support the EF Core provider '{providerName ?? "(null)"}'. " +
            "Configure UseSqlServer(...) or UseNpgsql(...) on the application DbContext.");
    }

    public static ISqlOSDatabaseProvider Resolve(DatabaseFacade database)
        => Resolve(database.ProviderName);

    public static IsolationLevel ExclusiveWorkIsolationLevel(DatabaseFacade database)
        => IsPostgreSql(database.ProviderName)
            ? IsolationLevel.ReadCommitted
            : IsolationLevel.Serializable;

    public static Task AcquireExclusiveTransactionLockAsync(
        DatabaseFacade database,
        string resource,
        TimeSpan timeout,
        string failureMessage,
        CancellationToken cancellationToken = default)
    {
        if (!database.IsRelational())
        {
            return Task.CompletedTask;
        }

        return Resolve(database).AcquireTransactionLockAsync(
            database,
            resource,
            timeout,
            failureMessage,
            cancellationToken);
    }
}

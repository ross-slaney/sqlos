using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

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

    /// <summary>
    /// PostgreSQL cannot store NUL in text, so composite stored keys use US there.
    /// SQL Server keeps NUL so existing MFA device lockout rows still match.
    /// </summary>
    public static char CompositeKeySeparator(string? providerName)
        => IsPostgreSql(providerName) ? '\u001F' : '\0';

    public static bool IsPostgreSql(DbContextOptionsBuilder options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var extension in options.Options.Extensions)
        {
            var typeName = extension.GetType().FullName;
            if (typeName is not null
                && typeName.StartsWith("Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static void EnablePostgreSqlTimestampCompatibility()
        => AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

    public static void EnablePostgreSqlTimestampCompatibilityIfNeeded(DbContextOptionsBuilder options)
    {
        if (IsPostgreSql(options))
        {
            EnablePostgreSqlTimestampCompatibility();
        }
    }

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

    /// <summary>
    /// The database's current UTC time. Use it for a value that must already have passed when the next
    /// access check runs, such as a service account ended now: the checks compare with the database's
    /// clock, which can read behind the app's. Reads in the current transaction. Without a relational
    /// database it is the app's clock.
    /// </summary>
    public static async Task<DateTime> UtcNowAsync(DatabaseFacade database, CancellationToken cancellationToken = default)
    {
        if (!database.IsRelational())
        {
            return DateTime.UtcNow;
        }

        await database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = database.GetDbConnection().CreateCommand();
            command.Transaction = database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = Resolve(database).BuildSelectUtcNowSql();
            var value = (DateTime)(await command.ExecuteScalarAsync(cancellationToken))!;
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }
        finally
        {
            await database.CloseConnectionAsync();
        }
    }

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

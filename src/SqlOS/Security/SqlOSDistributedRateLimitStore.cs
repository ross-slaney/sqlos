using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;
using SqlOS.Database;

namespace SqlOS.Security;

internal sealed class SqlOSDistributedRateLimitStore : ISqlOSRateLimitStore
{
    private const int CleanupBatchSize = 100;
    private const int MaximumReservationBuckets = 8;
    private static readonly TimeSpan StaleBucketRetention = TimeSpan.FromDays(1);
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly string _schema;

    public SqlOSDistributedRateLimitStore(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options)
    {
        _context = context;
        _schema = options.Value.Schema;
    }

    public async Task<SqlOSRateLimitBucketState> IncrementAsync(
        string scope,
        string key,
        int lockThreshold,
        TimeSpan window,
        TimeSpan lockoutDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var sql = SqlOSDatabase.Resolve(_context.Database).BuildRateLimitIncrementSql(_schema);

        return await ExecuteStateAsync(
            sql,
            scope,
            key,
            lockThreshold,
            window,
            lockoutDuration,
            now,
            cancellationToken,
            lockResources: [RateLimitLockResource(scope, key)])
            ?? throw new InvalidOperationException("SqlOS rate-limit state was not returned by the database.");
    }

    public async Task<SqlOSRateLimitPairReservationState> ReservePairAsync(
        SqlOSRateLimitBucketRequest first,
        SqlOSRateLimitBucketRequest second,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var sql = SqlOSDatabase.Resolve(_context.Database).BuildRateLimitReservePairSql(_schema);
        return await ExecutePairStateAsync(
            sql,
            first,
            second,
            now,
            cancellationToken,
            lockResources: [PairLockResource]);
    }

    public async Task<SqlOSRateLimitReservationState> ReserveManyAsync(
        IReadOnlyList<SqlOSRateLimitBucketRequest> buckets,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        if (buckets.Count == 0)
        {
            return new SqlOSRateLimitReservationState([], null, null);
        }

        if (buckets.Count > MaximumReservationBuckets)
        {
            throw new ArgumentOutOfRangeException(
                nameof(buckets),
                buckets.Count,
                $"SqlOS rate-limit reservations support at most {MaximumReservationBuckets} buckets.");
        }

        return await ExecuteReservationStateAsync(
            SqlOSDatabase.Resolve(_context.Database).BuildRateLimitReserveManySql(_schema, buckets.Count),
            buckets,
            now,
            cancellationToken,
            lockResources: SortedRateLimitLockResources(buckets.Select(bucket => (bucket.Scope, bucket.Key))));
    }

    public async Task<SqlOSRateLimitBucketState?> GetAsync(
        string scope,
        string key,
        DateTimeOffset now,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        var sql = SqlOSDatabase.Resolve(_context.Database).BuildRateLimitGetSql(_schema);

        return await ExecuteStateAsync(
            sql,
            scope,
            key,
            lockThreshold: int.MaxValue,
            window,
            lockoutDuration: TimeSpan.Zero,
            now,
            cancellationToken,
            allowMissing: true);
    }

    public Task DeleteAsync(
        string scope,
        string key,
        CancellationToken cancellationToken = default)
        => ExecuteNonQueryAsync(
            SqlOSDatabase.Resolve(_context.Database).BuildRateLimitDeleteSql(_schema),
            scope,
            key,
            now: null,
            cancellationToken);

    public Task DecrementAsync(
        string scope,
        string key,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
        => ExecuteNonQueryAsync(
            SqlOSDatabase.Resolve(_context.Database).BuildRateLimitDecrementSql(_schema),
            scope,
            key,
            now,
            cancellationToken);

    public Task ReleaseAsync(
        string scope,
        string key,
        int lockThreshold,
        DateTimeOffset windowStartedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
        => ExecuteNonQueryAsync(
            SqlOSDatabase.Resolve(_context.Database).BuildRateLimitReleaseSql(_schema),
            scope,
            key,
            now,
            cancellationToken,
            lockThreshold,
            windowStartedAt,
            lockResources: [RateLimitLockResource(scope, key)]);

    public Task ReleaseManyAsync(
        IReadOnlyList<SqlOSRateLimitReservationRelease> releases,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releases);
        if (releases.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (releases.Count > MaximumReservationBuckets)
        {
            throw new ArgumentOutOfRangeException(
                nameof(releases),
                releases.Count,
                $"SqlOS rate-limit reservations support at most {MaximumReservationBuckets} buckets.");
        }

        return ExecuteReleaseManyAsync(
            SqlOSDatabase.Resolve(_context.Database).BuildRateLimitReleaseManySql(_schema, releases.Count),
            releases,
            now,
            cancellationToken,
            lockResources: SortedRateLimitLockResources(releases.Select(release => (release.Scope, release.Key))));
    }

    private async Task ExecuteNonQueryAsync(
        string sql,
        string scope,
        string key,
        DateTimeOffset? now,
        CancellationToken cancellationToken,
        int? lockThreshold = null,
        DateTimeOffset? windowStartedAt = null,
        IReadOnlyList<string>? lockResources = null)
    {
        await ExecuteWithOptionalPostgreSqlLocksAsync(
            lockResources,
            async (connection, transaction) =>
            {
                await using var command = CreateCommand(connection, transaction, sql);
                AddParameter(command, "@scope", scope);
                AddParameter(command, "@key", NormalizeKey(key));
                if (now.HasValue)
                {
                    AddParameter(command, "@now", now.Value.UtcDateTime);
                }
                if (lockThreshold.HasValue)
                {
                    AddParameter(command, "@lockThreshold", lockThreshold.Value);
                }
                if (windowStartedAt.HasValue)
                {
                    AddParameter(command, "@windowStartedAt", windowStartedAt.Value.UtcDateTime);
                }

                await command.ExecuteNonQueryAsync(cancellationToken);
                return 0;
            },
            cancellationToken);
    }

    private async Task<SqlOSRateLimitBucketState?> ExecuteStateAsync(
        string sql,
        string scope,
        string key,
        int lockThreshold,
        TimeSpan window,
        TimeSpan lockoutDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        bool allowMissing = false,
        IReadOnlyList<string>? lockResources = null)
    {
        return await ExecuteWithOptionalPostgreSqlLocksAsync(
            lockResources,
            async (connection, transaction) =>
            {
                await using var command = CreateCommand(connection, transaction, sql);
                AddParameter(command, "@scope", scope);
                AddParameter(command, "@key", NormalizeKey(key));
                AddParameter(command, "@lockThreshold", lockThreshold);
                AddParameter(command, "@now", now.UtcDateTime);
                AddParameter(command, "@windowStartedBefore", now.Subtract(window).UtcDateTime);
                AddParameter(command, "@lockedUntil", now.Add(lockoutDuration).UtcDateTime);
                AddParameter(command, "@cleanupBatchSize", CleanupBatchSize);
                AddParameter(command, "@staleBefore", now.Subtract(StaleBucketRetention).UtcDateTime);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    if (allowMissing)
                    {
                        return null;
                    }

                    throw new InvalidOperationException("SqlOS rate-limit state was not returned by the database.");
                }

                return new SqlOSRateLimitBucketState(
                    reader.GetInt32(0),
                    reader.IsDBNull(1)
                        ? null
                        : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc)),
                    reader.FieldCount < 3 || reader.GetBoolean(2),
                    reader.FieldCount < 4 || reader.IsDBNull(3)
                        ? null
                        : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)));
            },
            cancellationToken);
    }

    private async Task<SqlOSRateLimitPairReservationState> ExecutePairStateAsync(
        string sql,
        SqlOSRateLimitBucketRequest first,
        SqlOSRateLimitBucketRequest second,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? lockResources = null)
    {
        return await ExecuteWithOptionalPostgreSqlLocksAsync(
            lockResources,
            async (connection, transaction) =>
            {
                await using var command = CreateCommand(connection, transaction, sql);
                AddPairParameters(command, "first", first, now);
                AddPairParameters(command, "second", second, now);
                AddParameter(command, "@now", now.UtcDateTime);
                AddParameter(command, "@cleanupBatchSize", CleanupBatchSize);
                AddParameter(command, "@staleBefore", now.Subtract(StaleBucketRetention).UtcDateTime);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException("SqlOS paired rate-limit state was not returned by the database.");
                }

                int? rejectedIndex = reader.IsDBNull(0) ? null : reader.GetInt32(0);
                var rejectedUntil = ReadDateTimeOffset(reader, 1);
                return new SqlOSRateLimitPairReservationState(
                    ReadPairBucketState(reader, 2),
                    ReadPairBucketState(reader, 5),
                    rejectedIndex,
                    rejectedUntil);
            },
            cancellationToken);
    }

    private static void AddPairParameters(
        DbCommand command,
        string prefix,
        SqlOSRateLimitBucketRequest request,
        DateTimeOffset now)
    {
        AddParameter(command, $"@{prefix}Scope", request.Scope);
        AddParameter(command, $"@{prefix}Key", NormalizeKey(request.Key));
        AddParameter(command, $"@{prefix}Threshold", request.LockThreshold);
        AddParameter(command, $"@{prefix}WindowStartedBefore", now.Subtract(request.Window).UtcDateTime);
        AddParameter(command, $"@{prefix}LockedUntil", now.Add(request.LockoutDuration).UtcDateTime);
    }

    private static SqlOSRateLimitBucketState? ReadPairBucketState(DbDataReader reader, int offset)
        => reader.IsDBNull(offset)
            ? null
            : new SqlOSRateLimitBucketState(
                reader.GetInt32(offset),
                ReadDateTimeOffset(reader, offset + 1),
                WindowStartedAt: ReadDateTimeOffset(reader, offset + 2));

    private static DateTimeOffset? ReadDateTimeOffset(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string NormalizeKey(string key)
        => key.Length <= 384 ? key : key[..384];

    private async Task<SqlOSRateLimitReservationState> ExecuteReservationStateAsync(
        string sql,
        IReadOnlyList<SqlOSRateLimitBucketRequest> buckets,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? lockResources = null)
    {
        return await ExecuteWithOptionalPostgreSqlLocksAsync(
            lockResources,
            async (connection, transaction) =>
            {
                await using var command = CreateCommand(connection, transaction, sql);
                for (var index = 0; index < buckets.Count; index++)
                {
                    AddReservationParameters(command, index, buckets[index], now);
                }

                AddParameter(command, "@now", now.UtcDateTime);
                AddParameter(command, "@cleanupBatchSize", CleanupBatchSize);
                AddParameter(command, "@staleBefore", now.Subtract(StaleBucketRetention).UtcDateTime);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException("SqlOS reserved rate-limit state was not returned by the database.");
                }

                int? rejectedIndex = reader.IsDBNull(0) ? null : reader.GetInt32(0);
                var rejectedUntil = ReadDateTimeOffset(reader, 1);
                var states = new SqlOSRateLimitBucketState?[buckets.Count];
                for (var index = 0; index < buckets.Count; index++)
                {
                    states[index] = ReadPairBucketState(reader, 2 + (index * 3));
                }

                return new SqlOSRateLimitReservationState(states, rejectedIndex, rejectedUntil);
            },
            cancellationToken);
    }

    private async Task ExecuteReleaseManyAsync(
        string sql,
        IReadOnlyList<SqlOSRateLimitReservationRelease> releases,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? lockResources = null)
    {
        await ExecuteWithOptionalPostgreSqlLocksAsync(
            lockResources,
            async (connection, transaction) =>
            {
                await using var command = CreateCommand(connection, transaction, sql);
                for (var index = 0; index < releases.Count; index++)
                {
                    var release = releases[index];
                    AddParameter(command, $"@scope{index}", release.Scope);
                    AddParameter(command, $"@key{index}", NormalizeKey(release.Key));
                    AddParameter(command, $"@threshold{index}", release.LockThreshold);
                    AddParameter(command, $"@windowStartedAt{index}", release.WindowStartedAt.UtcDateTime);
                }

                AddParameter(command, "@now", now.UtcDateTime);
                await command.ExecuteNonQueryAsync(cancellationToken);
                return 0;
            },
            cancellationToken);
    }

    private const string PairLockResource = "SqlOS:rate-limit-pair-reservation";

    private static string RateLimitLockResource(string scope, string key)
        => $"SqlOS:rate-limit:{scope}:{NormalizeKey(key)}";

    private static IReadOnlyList<string> SortedRateLimitLockResources(
        IEnumerable<(string Scope, string Key)> buckets)
        => buckets
            .Select(bucket => RateLimitLockResource(bucket.Scope, bucket.Key))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(resource => resource, StringComparer.Ordinal)
            .ToArray();

    private static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    private async Task<T> ExecuteWithOptionalPostgreSqlLocksAsync<T>(
        IReadOnlyList<string>? lockResources,
        Func<DbConnection, DbTransaction?, Task<T>> execute,
        CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            if (lockResources is not { Count: > 0 }
                || !SqlOSDatabase.IsPostgreSql(_context.Database.ProviderName))
            {
                return await execute(connection, _context.Database.CurrentTransaction?.GetDbTransaction());
            }

            var existing = _context.Database.CurrentTransaction?.GetDbTransaction();
            if (existing != null)
            {
                await AcquirePostgreSqlLocksAsync(connection, existing, lockResources, cancellationToken);
                return await execute(connection, existing);
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await AcquirePostgreSqlLocksAsync(connection, transaction, lockResources, cancellationToken);
            var result = await execute(connection, transaction);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task AcquirePostgreSqlLocksAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<string> resources,
        CancellationToken cancellationToken)
    {
        await using (var timeout = CreateCommand(connection, transaction, "SET LOCAL lock_timeout = '10000ms'"))
        {
            await timeout.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            foreach (var resource in resources)
            {
                await using var command = CreateCommand(
                    connection,
                    transaction,
                    """
                    SELECT pg_advisory_xact_lock(
                        ('x' || substr(md5(@resource), 1, 8))::bit(32)::int,
                        ('x' || substr(md5(@resource), 9, 8))::bit(32)::int)
                    """);
                AddParameter(command, "@resource", resource);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is Npgsql.PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable or "57014" })
        {
            throw new InvalidOperationException("Unable to acquire the SqlOS rate-limit lock.", ex);
        }
    }

    private static void AddReservationParameters(
        DbCommand command,
        int index,
        SqlOSRateLimitBucketRequest request,
        DateTimeOffset now)
    {
        AddParameter(command, $"@scope{index}", request.Scope);
        AddParameter(command, $"@key{index}", NormalizeKey(request.Key));
        AddParameter(command, $"@threshold{index}", request.LockThreshold);
        AddParameter(command, $"@windowStartedBefore{index}", now.Subtract(request.Window).UtcDateTime);
        AddParameter(command, $"@lockedUntil{index}", now.Add(request.LockoutDuration).UtcDateTime);
    }
}

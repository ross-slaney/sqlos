using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Interfaces;

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
        _schema = EscapeIdentifier(options.Value.Schema);
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
        var sql = $"""
            SET XACT_ABORT ON;
            SET NOCOUNT ON;
            BEGIN TRANSACTION;

            DECLARE @admitted BIT = 0;

            DECLARE @applicationLockResult INT;
            DECLARE @applicationLockResource NVARCHAR(255) =
                N'SqlOS:rate-limit:' + CONVERT(NVARCHAR(64), HASHBYTES('SHA2_256', @scope + N':' + @key), 2);
            EXEC @applicationLockResult = sys.sp_getapplock
                @Resource = @applicationLockResource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 10000;
            IF @applicationLockResult < 0
                THROW 51000, 'Unable to acquire the SqlOS rate-limit lock.', 1;

            DELETE FROM [{_schema}].[SqlOSRateLimitBuckets]
            WHERE [Scope] = @scope
              AND [BucketKey] = @key
              AND ([LockedUntil] IS NULL OR [LockedUntil] <= @now)
              AND [WindowStartedAt] <= @windowStartedBefore;

            IF EXISTS (
                SELECT 1
                FROM [{_schema}].[SqlOSRateLimitBuckets] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Scope] = @scope AND [BucketKey] = @key)
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM [{_schema}].[SqlOSRateLimitBuckets]
                    WHERE [Scope] = @scope
                      AND [BucketKey] = @key
                      AND ([LockedUntil] IS NULL OR [LockedUntil] <= @now))
                    SET @admitted = 1;

                UPDATE [{_schema}].[SqlOSRateLimitBuckets]
                SET
                    [Count] = CASE WHEN [LockedUntil] IS NOT NULL AND [LockedUntil] > @now
                        THEN [Count] ELSE [Count] + 1 END,
                    [LockedUntil] = CASE
                        WHEN [LockedUntil] IS NOT NULL AND [LockedUntil] > @now THEN [LockedUntil]
                        WHEN [Count] + 1 >= @lockThreshold
                            THEN @lockedUntil
                        ELSE NULL
                    END,
                    [UpdatedAt] = @now
                WHERE [Scope] = @scope AND [BucketKey] = @key;
            END
            ELSE
            BEGIN
                SET @admitted = 1;
                INSERT INTO [{_schema}].[SqlOSRateLimitBuckets]
                    ([Scope], [BucketKey], [WindowStartedAt], [Count], [LockedUntil], [UpdatedAt])
                VALUES
                    (@scope, @key, @now, 1,
                     CASE WHEN @lockThreshold <= 1
                        THEN @lockedUntil
                        ELSE NULL END,
                     @now);
            END

            DELETE FROM [{_schema}].[SqlOSRateLimitBuckets]
            WHERE [Scope] = @scope
              AND [BucketKey] IN (
                  SELECT TOP (@cleanupBatchSize) [BucketKey]
                  FROM [{_schema}].[SqlOSRateLimitBuckets]
                  WHERE [Scope] = @scope
                    AND [UpdatedAt] < @staleBefore
                    AND ([LockedUntil] IS NULL OR [LockedUntil] <= @now)
                  ORDER BY [UpdatedAt])
              AND [UpdatedAt] < @staleBefore
              AND ([LockedUntil] IS NULL OR [LockedUntil] <= @now);

            SELECT [Count], [LockedUntil], @admitted
            FROM [{_schema}].[SqlOSRateLimitBuckets]
            WHERE [Scope] = @scope AND [BucketKey] = @key;

            COMMIT TRANSACTION;
            """;

        return await ExecuteStateAsync(
            sql,
            scope,
            key,
            lockThreshold,
            window,
            lockoutDuration,
            now,
            cancellationToken)
            ?? throw new InvalidOperationException("SqlOS rate-limit state was not returned by SQL Server.");
    }

    public async Task<SqlOSRateLimitPairReservationState> ReservePairAsync(
        SqlOSRateLimitBucketRequest first,
        SqlOSRateLimitBucketRequest second,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SET XACT_ABORT ON;
            SET NOCOUNT ON;
            BEGIN TRANSACTION;

            DECLARE @applicationLockResult INT;
            EXEC @applicationLockResult = sys.sp_getapplock
                @Resource = N'SqlOS:rate-limit-pair-reservation',
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 10000;
            IF @applicationLockResult < 0
                THROW 51000, 'Unable to acquire the SqlOS rate-limit pair lock.', 1;

            DELETE FROM [{_schema}].[SqlOSRateLimitBuckets]
            WHERE ([Scope] = @firstScope AND [BucketKey] = @firstKey
                   AND ([LockedUntil] IS NULL OR [LockedUntil] <= @now)
                   AND [WindowStartedAt] <= @firstWindowStartedBefore)
               OR ([Scope] = @secondScope AND [BucketKey] = @secondKey
                   AND ([LockedUntil] IS NULL OR [LockedUntil] <= @now)
                   AND [WindowStartedAt] <= @secondWindowStartedBefore);

            DECLARE @rejectedIndex INT = NULL;
            DECLARE @rejectedLockedUntil DATETIME2 = NULL;
            SELECT TOP (1) @rejectedIndex = 0, @rejectedLockedUntil = [LockedUntil]
            FROM [{_schema}].[SqlOSRateLimitBuckets] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Scope] = @firstScope AND [BucketKey] = @firstKey AND [LockedUntil] > @now;
            IF @rejectedIndex IS NULL
                SELECT TOP (1) @rejectedIndex = 1, @rejectedLockedUntil = [LockedUntil]
                FROM [{_schema}].[SqlOSRateLimitBuckets] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Scope] = @secondScope AND [BucketKey] = @secondKey AND [LockedUntil] > @now;

            IF @rejectedIndex IS NULL
            BEGIN
                UPDATE [{_schema}].[SqlOSRateLimitBuckets]
                SET [Count] = [Count] + 1,
                    [LockedUntil] = CASE WHEN [Count] + 1 >= @firstThreshold
                        THEN @firstLockedUntil ELSE NULL END,
                    [UpdatedAt] = @now
                WHERE [Scope] = @firstScope AND [BucketKey] = @firstKey;
                IF @@ROWCOUNT = 0
                    INSERT INTO [{_schema}].[SqlOSRateLimitBuckets]
                        ([Scope], [BucketKey], [WindowStartedAt], [Count], [LockedUntil], [UpdatedAt])
                    VALUES (@firstScope, @firstKey, @now, 1,
                        CASE WHEN @firstThreshold <= 1 THEN @firstLockedUntil ELSE NULL END, @now);

                UPDATE [{_schema}].[SqlOSRateLimitBuckets]
                SET [Count] = [Count] + 1,
                    [LockedUntil] = CASE WHEN [Count] + 1 >= @secondThreshold
                        THEN @secondLockedUntil ELSE NULL END,
                    [UpdatedAt] = @now
                WHERE [Scope] = @secondScope AND [BucketKey] = @secondKey;
                IF @@ROWCOUNT = 0
                    INSERT INTO [{_schema}].[SqlOSRateLimitBuckets]
                        ([Scope], [BucketKey], [WindowStartedAt], [Count], [LockedUntil], [UpdatedAt])
                    VALUES (@secondScope, @secondKey, @now, 1,
                        CASE WHEN @secondThreshold <= 1 THEN @secondLockedUntil ELSE NULL END, @now);
            END

            ;WITH staleBuckets AS (
                SELECT TOP (@cleanupBatchSize) *
                FROM [{_schema}].[SqlOSRateLimitBuckets]
                WHERE [Scope] IN (@firstScope, @secondScope)
                  AND [UpdatedAt] < @staleBefore
                  AND ([LockedUntil] IS NULL OR [LockedUntil] <= @now)
                ORDER BY [UpdatedAt]
            )
            DELETE FROM staleBuckets;

            SELECT @rejectedIndex, @rejectedLockedUntil,
                   firstBucket.[Count], firstBucket.[LockedUntil], firstBucket.[WindowStartedAt],
                   secondBucket.[Count], secondBucket.[LockedUntil], secondBucket.[WindowStartedAt]
            FROM (VALUES (1)) AS anchor([Value])
            LEFT JOIN [{_schema}].[SqlOSRateLimitBuckets] firstBucket
              ON firstBucket.[Scope] = @firstScope AND firstBucket.[BucketKey] = @firstKey
            LEFT JOIN [{_schema}].[SqlOSRateLimitBuckets] secondBucket
              ON secondBucket.[Scope] = @secondScope AND secondBucket.[BucketKey] = @secondKey;

            COMMIT TRANSACTION;
            """;

        return await ExecutePairStateAsync(sql, first, second, now, cancellationToken);
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
            BuildReserveManySql(buckets.Count),
            buckets,
            now,
            cancellationToken);
    }

    public async Task<SqlOSRateLimitBucketState?> GetAsync(
        string scope,
        string key,
        DateTimeOffset now,
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SET NOCOUNT ON;

            DELETE FROM [{_schema}].[SqlOSRateLimitBuckets]
            WHERE [Scope] = @scope
              AND [BucketKey] = @key
              AND ([LockedUntil] IS NULL OR [LockedUntil] <= @now)
              AND [WindowStartedAt] <= @windowStartedBefore;

            SELECT [Count], [LockedUntil]
            FROM [{_schema}].[SqlOSRateLimitBuckets]
            WHERE [Scope] = @scope AND [BucketKey] = @key;
            """;

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
            $"DELETE FROM [{_schema}].[SqlOSRateLimitBuckets] WHERE [Scope] = @scope AND [BucketKey] = @key",
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
            $"""
            UPDATE [{_schema}].[SqlOSRateLimitBuckets]
            SET [Count] = CASE WHEN [Count] > 0 THEN [Count] - 1 ELSE 0 END,
                [UpdatedAt] = @now
            WHERE [Scope] = @scope
              AND [BucketKey] = @key
              AND ([LockedUntil] IS NULL OR [LockedUntil] <= @now);

            DELETE FROM [{_schema}].[SqlOSRateLimitBuckets]
            WHERE [Scope] = @scope AND [BucketKey] = @key AND [Count] = 0;
            """,
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
            $"""
            SET XACT_ABORT ON;
            SET NOCOUNT ON;
            BEGIN TRANSACTION;

            DECLARE @applicationLockResult INT;
            DECLARE @applicationLockResource NVARCHAR(255) =
                N'SqlOS:rate-limit:' + CONVERT(NVARCHAR(64), HASHBYTES('SHA2_256', @scope + N':' + @key), 2);
            EXEC @applicationLockResult = sys.sp_getapplock
                @Resource = @applicationLockResource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 10000;
            IF @applicationLockResult < 0
                THROW 51000, 'Unable to acquire the SqlOS rate-limit lock.', 1;

            UPDATE [{_schema}].[SqlOSRateLimitBuckets]
            SET [Count] = CASE WHEN [Count] > 0 THEN [Count] - 1 ELSE 0 END,
                [LockedUntil] = CASE WHEN [Count] - 1 < @lockThreshold THEN NULL ELSE [LockedUntil] END,
                [UpdatedAt] = @now
            WHERE [Scope] = @scope AND [BucketKey] = @key
              AND [WindowStartedAt] = @windowStartedAt;

            DELETE FROM [{_schema}].[SqlOSRateLimitBuckets]
            WHERE [Scope] = @scope AND [BucketKey] = @key
              AND [WindowStartedAt] = @windowStartedAt
              AND [Count] = 0;

            COMMIT TRANSACTION;
            """,
            scope,
            key,
            now,
            cancellationToken,
            lockThreshold,
            windowStartedAt);

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

        return ExecuteReleaseManyAsync(BuildReleaseManySql(releases.Count), releases, now, cancellationToken);
    }

    private async Task ExecuteNonQueryAsync(
        string sql,
        string scope,
        string key,
        DateTimeOffset? now,
        CancellationToken cancellationToken,
        int? lockThreshold = null,
        DateTimeOffset? windowStartedAt = null)
    {
        var connection = _context.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
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
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
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
        bool allowMissing = false)
    {
        var connection = _context.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
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

                throw new InvalidOperationException("SqlOS rate-limit state was not returned by SQL Server.");
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
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task<SqlOSRateLimitPairReservationState> ExecutePairStateAsync(
        string sql,
        SqlOSRateLimitBucketRequest first,
        SqlOSRateLimitBucketRequest second,
        DateTimeOffset now,
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
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddPairParameters(command, "first", first, now);
            AddPairParameters(command, "second", second, now);
            AddParameter(command, "@now", now.UtcDateTime);
            AddParameter(command, "@cleanupBatchSize", CleanupBatchSize);
            AddParameter(command, "@staleBefore", now.Subtract(StaleBucketRetention).UtcDateTime);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("SqlOS paired rate-limit state was not returned by SQL Server.");
            }

            int? rejectedIndex = reader.IsDBNull(0) ? null : reader.GetInt32(0);
            var rejectedUntil = ReadDateTimeOffset(reader, 1);
            return new SqlOSRateLimitPairReservationState(
                ReadPairBucketState(reader, 2),
                ReadPairBucketState(reader, 5),
                rejectedIndex,
                rejectedUntil);
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
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

    private static string EscapeIdentifier(string identifier)
        => identifier.Replace("]", "]]", StringComparison.Ordinal);

    private string BuildReserveManySql(int count)
    {
        var sql = new System.Text.StringBuilder();
        sql.AppendLine("SET XACT_ABORT ON;");
        sql.AppendLine("SET NOCOUNT ON;");
        sql.AppendLine("BEGIN TRANSACTION;");
        sql.AppendLine();
        sql.AppendLine("DECLARE @applicationLockResult INT;");
        for (var index = 0; index < count; index++)
        {
            sql.AppendLine($"""
                DECLARE @applicationLockResource{index} NVARCHAR(255) =
                    N'SqlOS:rate-limit:' + CONVERT(NVARCHAR(64), HASHBYTES('SHA2_256', @scope{index} + N':' + @key{index}), 2);
                """);
        }

        // Acquire per-key locks in deterministic (scope, key) order. The parameter values are
        // compared in SQL so C# does not need the normalized keys before command execution.
        sql.AppendLine("""
            DECLARE @lockCursor TABLE ([Ordinal] INT NOT NULL, [Scope] NVARCHAR(64) NOT NULL, [BucketKey] NVARCHAR(384) NOT NULL);
            """);
        for (var index = 0; index < count; index++)
        {
            sql.AppendLine($"INSERT INTO @lockCursor ([Ordinal], [Scope], [BucketKey]) VALUES ({index}, @scope{index}, @key{index});");
        }

        sql.AppendLine("""
            DECLARE lock_cursor CURSOR LOCAL FAST_FORWARD FOR
                SELECT [Ordinal] FROM @lockCursor ORDER BY [Scope], [BucketKey], [Ordinal];
            DECLARE @lockOrdinal INT;
            OPEN lock_cursor;
            FETCH NEXT FROM lock_cursor INTO @lockOrdinal;
            WHILE @@FETCH_STATUS = 0
            BEGIN
            """);
        for (var index = 0; index < count; index++)
        {
            sql.AppendLine($"""
                IF @lockOrdinal = {index}
                BEGIN
                    EXEC @applicationLockResult = sys.sp_getapplock
                        @Resource = @applicationLockResource{index},
                        @LockMode = 'Exclusive',
                        @LockOwner = 'Transaction',
                        @LockTimeout = 10000;
                    IF @applicationLockResult < 0
                        THROW 51000, 'Unable to acquire the SqlOS rate-limit lock.', 1;
                END
                """);
        }

        sql.AppendLine("""
            FETCH NEXT FROM lock_cursor INTO @lockOrdinal;
            END
            CLOSE lock_cursor;
            DEALLOCATE lock_cursor;
            """);

        for (var index = 0; index < count; index++)
        {
            sql.AppendLine($"""
                DELETE FROM [{_schema}].[SqlOSRateLimitBuckets]
                WHERE [Scope] = @scope{index}
                  AND [BucketKey] = @key{index}
                  AND ([LockedUntil] IS NULL OR [LockedUntil] <= @now)
                  AND [WindowStartedAt] <= @windowStartedBefore{index};
                """);
        }

        sql.AppendLine("DECLARE @rejectedIndex INT = NULL;");
        sql.AppendLine("DECLARE @rejectedLockedUntil DATETIME2 = NULL;");
        for (var index = 0; index < count; index++)
        {
            sql.AppendLine($"""
                IF @rejectedIndex IS NULL
                    SELECT TOP (1) @rejectedIndex = {index}, @rejectedLockedUntil = [LockedUntil]
                    FROM [{_schema}].[SqlOSRateLimitBuckets] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Scope] = @scope{index} AND [BucketKey] = @key{index} AND [LockedUntil] > @now;
                """);
        }

        sql.AppendLine("IF @rejectedIndex IS NULL");
        sql.AppendLine("BEGIN");
        for (var index = 0; index < count; index++)
        {
            sql.AppendLine($"""
                    UPDATE [{_schema}].[SqlOSRateLimitBuckets]
                    SET [Count] = [Count] + 1,
                        [LockedUntil] = CASE WHEN [Count] + 1 >= @threshold{index}
                            THEN @lockedUntil{index} ELSE NULL END,
                        [UpdatedAt] = @now
                    WHERE [Scope] = @scope{index} AND [BucketKey] = @key{index};
                    IF @@ROWCOUNT = 0
                        INSERT INTO [{_schema}].[SqlOSRateLimitBuckets]
                            ([Scope], [BucketKey], [WindowStartedAt], [Count], [LockedUntil], [UpdatedAt])
                        VALUES (@scope{index}, @key{index}, @now, 1,
                            CASE WHEN @threshold{index} <= 1 THEN @lockedUntil{index} ELSE NULL END, @now);
                """);
        }

        sql.AppendLine("END");
        sql.AppendLine($"""
            ;WITH staleBuckets AS (
                SELECT TOP (@cleanupBatchSize) *
                FROM [{_schema}].[SqlOSRateLimitBuckets]
                WHERE [Scope] IN ({string.Join(", ", Enumerable.Range(0, count).Select(index => $"@scope{index}"))})
                  AND [UpdatedAt] < @staleBefore
                  AND ([LockedUntil] IS NULL OR [LockedUntil] <= @now)
                ORDER BY [UpdatedAt]
            )
            DELETE FROM staleBuckets;
            """);

        sql.Append("SELECT @rejectedIndex, @rejectedLockedUntil");
        for (var index = 0; index < count; index++)
        {
            sql.Append($", bucket{index}.[Count], bucket{index}.[LockedUntil], bucket{index}.[WindowStartedAt]");
        }

        sql.AppendLine();
        sql.AppendLine("FROM (VALUES (1)) AS anchor([Value])");
        for (var index = 0; index < count; index++)
        {
            sql.AppendLine($"""
                LEFT JOIN [{_schema}].[SqlOSRateLimitBuckets] bucket{index}
                  ON bucket{index}.[Scope] = @scope{index} AND bucket{index}.[BucketKey] = @key{index}
                """);
        }

        sql.AppendLine("COMMIT TRANSACTION;");
        return sql.ToString();
    }

    private string BuildReleaseManySql(int count)
    {
        var sql = new System.Text.StringBuilder();
        sql.AppendLine("SET XACT_ABORT ON;");
        sql.AppendLine("SET NOCOUNT ON;");
        sql.AppendLine("BEGIN TRANSACTION;");
        sql.AppendLine("DECLARE @applicationLockResult INT;");
        sql.AppendLine("""
            DECLARE @lockCursor TABLE ([Ordinal] INT NOT NULL, [Scope] NVARCHAR(64) NOT NULL, [BucketKey] NVARCHAR(384) NOT NULL);
            """);
        for (var index = 0; index < count; index++)
        {
            sql.AppendLine($"""
                DECLARE @applicationLockResource{index} NVARCHAR(255) =
                    N'SqlOS:rate-limit:' + CONVERT(NVARCHAR(64), HASHBYTES('SHA2_256', @scope{index} + N':' + @key{index}), 2);
                INSERT INTO @lockCursor ([Ordinal], [Scope], [BucketKey]) VALUES ({index}, @scope{index}, @key{index});
                """);
        }

        sql.AppendLine("""
            DECLARE lock_cursor CURSOR LOCAL FAST_FORWARD FOR
                SELECT [Ordinal] FROM @lockCursor ORDER BY [Scope], [BucketKey], [Ordinal];
            DECLARE @lockOrdinal INT;
            OPEN lock_cursor;
            FETCH NEXT FROM lock_cursor INTO @lockOrdinal;
            WHILE @@FETCH_STATUS = 0
            BEGIN
            """);
        for (var index = 0; index < count; index++)
        {
            sql.AppendLine($"""
                IF @lockOrdinal = {index}
                BEGIN
                    EXEC @applicationLockResult = sys.sp_getapplock
                        @Resource = @applicationLockResource{index},
                        @LockMode = 'Exclusive',
                        @LockOwner = 'Transaction',
                        @LockTimeout = 10000;
                    IF @applicationLockResult < 0
                        THROW 51000, 'Unable to acquire the SqlOS rate-limit lock.', 1;
                END
                """);
        }

        sql.AppendLine("""
            FETCH NEXT FROM lock_cursor INTO @lockOrdinal;
            END
            CLOSE lock_cursor;
            DEALLOCATE lock_cursor;
            """);

        for (var index = 0; index < count; index++)
        {
            sql.AppendLine($"""
                UPDATE [{_schema}].[SqlOSRateLimitBuckets]
                SET [Count] = CASE WHEN [Count] > 0 THEN [Count] - 1 ELSE 0 END,
                    [LockedUntil] = CASE WHEN [Count] - 1 < @threshold{index} THEN NULL ELSE [LockedUntil] END,
                    [UpdatedAt] = @now
                WHERE [Scope] = @scope{index} AND [BucketKey] = @key{index}
                  AND [WindowStartedAt] = @windowStartedAt{index};

                DELETE FROM [{_schema}].[SqlOSRateLimitBuckets]
                WHERE [Scope] = @scope{index} AND [BucketKey] = @key{index}
                  AND [WindowStartedAt] = @windowStartedAt{index}
                  AND [Count] = 0;
                """);
        }

        sql.AppendLine("COMMIT TRANSACTION;");
        return sql.ToString();
    }

    private async Task<SqlOSRateLimitReservationState> ExecuteReservationStateAsync(
        string sql,
        IReadOnlyList<SqlOSRateLimitBucketRequest> buckets,
        DateTimeOffset now,
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
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
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
                throw new InvalidOperationException("SqlOS reserved rate-limit state was not returned by SQL Server.");
            }

            int? rejectedIndex = reader.IsDBNull(0) ? null : reader.GetInt32(0);
            var rejectedUntil = ReadDateTimeOffset(reader, 1);
            var states = new SqlOSRateLimitBucketState?[buckets.Count];
            for (var index = 0; index < buckets.Count; index++)
            {
                states[index] = ReadPairBucketState(reader, 2 + (index * 3));
            }

            return new SqlOSRateLimitReservationState(states, rejectedIndex, rejectedUntil);
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task ExecuteReleaseManyAsync(
        string sql,
        IReadOnlyList<SqlOSRateLimitReservationRelease> releases,
        DateTimeOffset now,
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
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
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
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
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

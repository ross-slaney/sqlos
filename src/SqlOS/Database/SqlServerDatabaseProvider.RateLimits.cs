namespace SqlOS.Database;

internal sealed partial class SqlServerDatabaseProvider
{
    public string BuildRateLimitIncrementSql(string schema)
    {
        var table = Qualify(schema, "SqlOSRateLimitBuckets");
        return $"""
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

            DELETE FROM {table}
            WHERE [Scope] = @scope
              AND [BucketKey] = @key
              AND ([LockedUntil] IS NULL OR [LockedUntil] <= @now)
              AND [WindowStartedAt] <= @windowStartedBefore;

            IF EXISTS (
                SELECT 1
                FROM {table} WITH (UPDLOCK, HOLDLOCK)
                WHERE [Scope] = @scope AND [BucketKey] = @key)
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM {table}
                    WHERE [Scope] = @scope
                      AND [BucketKey] = @key
                      AND ([LockedUntil] IS NULL OR [LockedUntil] <= @now))
                    SET @admitted = 1;

                UPDATE {table}
                SET
                    [Count] = CASE WHEN [LockedUntil] IS NOT NULL AND [LockedUntil] > @now
                        THEN [Count] ELSE [Count] + 1 END,
                    [LockedUntil] = CASE
                        WHEN [LockedUntil] IS NOT NULL AND [LockedUntil] > @now THEN [LockedUntil]
                        WHEN [Count] + 1 >= @lockThreshold THEN @lockedUntil
                        ELSE NULL
                    END,
                    [UpdatedAt] = @now
                WHERE [Scope] = @scope AND [BucketKey] = @key;
            END
            ELSE
            BEGIN
                SET @admitted = 1;
                INSERT INTO {table}
                    ([Scope], [BucketKey], [WindowStartedAt], [Count], [LockedUntil], [UpdatedAt])
                VALUES
                    (@scope, @key, @now, 1,
                     CASE WHEN @lockThreshold <= 1 THEN @lockedUntil ELSE NULL END,
                     @now);
            END

            DELETE FROM {table}
            WHERE [Scope] = @scope
              AND [BucketKey] IN (
                  SELECT TOP (@cleanupBatchSize) [BucketKey]
                  FROM {table}
                  WHERE [Scope] = @scope
                    AND [UpdatedAt] < @staleBefore
                    AND ([LockedUntil] IS NULL OR [LockedUntil] <= @now)
                  ORDER BY [UpdatedAt])
              AND [UpdatedAt] < @staleBefore
              AND ([LockedUntil] IS NULL OR [LockedUntil] <= @now);

            SELECT [Count], [LockedUntil], @admitted
            FROM {table}
            WHERE [Scope] = @scope AND [BucketKey] = @key;

            COMMIT TRANSACTION;
            """;
    }

    public string BuildRateLimitGetSql(string schema)
    {
        var table = Qualify(schema, "SqlOSRateLimitBuckets");
        return $"""
            SET NOCOUNT ON;
            DELETE FROM {table}
            WHERE [Scope] = @scope
              AND [BucketKey] = @key
              AND ([LockedUntil] IS NULL OR [LockedUntil] <= @now)
              AND [WindowStartedAt] <= @windowStartedBefore;
            SELECT [Count], [LockedUntil]
            FROM {table}
            WHERE [Scope] = @scope AND [BucketKey] = @key;
            """;
    }

    public string BuildRateLimitDeleteSql(string schema)
        => $"DELETE FROM {Qualify(schema, "SqlOSRateLimitBuckets")} WHERE [Scope] = @scope AND [BucketKey] = @key";

    public string BuildRateLimitDecrementSql(string schema)
    {
        var table = Qualify(schema, "SqlOSRateLimitBuckets");
        return $"""
            UPDATE {table}
            SET [Count] = CASE WHEN [Count] > 0 THEN [Count] - 1 ELSE 0 END,
                [UpdatedAt] = @now
            WHERE [Scope] = @scope
              AND [BucketKey] = @key
              AND ([LockedUntil] IS NULL OR [LockedUntil] <= @now);
            DELETE FROM {table}
            WHERE [Scope] = @scope AND [BucketKey] = @key AND [Count] = 0;
            """;
    }

    public string BuildRateLimitReleaseSql(string schema)
    {
        var table = Qualify(schema, "SqlOSRateLimitBuckets");
        return $"""
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
            UPDATE {table}
            SET [Count] = CASE WHEN [Count] > 0 THEN [Count] - 1 ELSE 0 END,
                [LockedUntil] = CASE WHEN [Count] - 1 < @lockThreshold THEN NULL ELSE [LockedUntil] END,
                [UpdatedAt] = @now
            WHERE [Scope] = @scope AND [BucketKey] = @key
              AND [WindowStartedAt] = @windowStartedAt;
            DELETE FROM {table}
            WHERE [Scope] = @scope AND [BucketKey] = @key
              AND [WindowStartedAt] = @windowStartedAt
              AND [Count] = 0;
            COMMIT TRANSACTION;
            """;
    }

    public string BuildRateLimitReservePairSql(string schema)
    {
        var table = Qualify(schema, "SqlOSRateLimitBuckets");
        return $"""
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
            DELETE FROM {table}
            WHERE ([Scope] = @firstScope AND [BucketKey] = @firstKey
                   AND ([LockedUntil] IS NULL OR [LockedUntil] <= @now)
                   AND [WindowStartedAt] <= @firstWindowStartedBefore)
               OR ([Scope] = @secondScope AND [BucketKey] = @secondKey
                   AND ([LockedUntil] IS NULL OR [LockedUntil] <= @now)
                   AND [WindowStartedAt] <= @secondWindowStartedBefore);
            DECLARE @rejectedIndex INT = NULL;
            DECLARE @rejectedLockedUntil DATETIME2 = NULL;
            SELECT TOP (1) @rejectedIndex = 0, @rejectedLockedUntil = [LockedUntil]
            FROM {table} WITH (UPDLOCK, HOLDLOCK)
            WHERE [Scope] = @firstScope AND [BucketKey] = @firstKey AND [LockedUntil] > @now;
            IF @rejectedIndex IS NULL
                SELECT TOP (1) @rejectedIndex = 1, @rejectedLockedUntil = [LockedUntil]
                FROM {table} WITH (UPDLOCK, HOLDLOCK)
                WHERE [Scope] = @secondScope AND [BucketKey] = @secondKey AND [LockedUntil] > @now;
            IF @rejectedIndex IS NULL
            BEGIN
                UPDATE {table}
                SET [Count] = [Count] + 1,
                    [LockedUntil] = CASE WHEN [Count] + 1 >= @firstThreshold THEN @firstLockedUntil ELSE NULL END,
                    [UpdatedAt] = @now
                WHERE [Scope] = @firstScope AND [BucketKey] = @firstKey;
                IF @@ROWCOUNT = 0
                    INSERT INTO {table}
                        ([Scope], [BucketKey], [WindowStartedAt], [Count], [LockedUntil], [UpdatedAt])
                    VALUES (@firstScope, @firstKey, @now, 1,
                        CASE WHEN @firstThreshold <= 1 THEN @firstLockedUntil ELSE NULL END, @now);
                UPDATE {table}
                SET [Count] = [Count] + 1,
                    [LockedUntil] = CASE WHEN [Count] + 1 >= @secondThreshold THEN @secondLockedUntil ELSE NULL END,
                    [UpdatedAt] = @now
                WHERE [Scope] = @secondScope AND [BucketKey] = @secondKey;
                IF @@ROWCOUNT = 0
                    INSERT INTO {table}
                        ([Scope], [BucketKey], [WindowStartedAt], [Count], [LockedUntil], [UpdatedAt])
                    VALUES (@secondScope, @secondKey, @now, 1,
                        CASE WHEN @secondThreshold <= 1 THEN @secondLockedUntil ELSE NULL END, @now);
            END
            ;WITH staleBuckets AS (
                SELECT TOP (@cleanupBatchSize) *
                FROM {table}
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
            LEFT JOIN {table} firstBucket
              ON firstBucket.[Scope] = @firstScope AND firstBucket.[BucketKey] = @firstKey
            LEFT JOIN {table} secondBucket
              ON secondBucket.[Scope] = @secondScope AND secondBucket.[BucketKey] = @secondKey;
            COMMIT TRANSACTION;
            """;
    }

    public string BuildRateLimitReserveManySql(string schema, int count)
    {
        var table = Qualify(schema, "SqlOSRateLimitBuckets");
        var sql = new System.Text.StringBuilder();
        sql.AppendLine("SET XACT_ABORT ON;");
        sql.AppendLine("SET NOCOUNT ON;");
        sql.AppendLine("BEGIN TRANSACTION;");
        sql.AppendLine("DECLARE @applicationLockResult INT;");
        for (var index = 0; index < count; index++)
        {
            sql.AppendLine($"""
                DECLARE @applicationLockResource{index} NVARCHAR(255) =
                    N'SqlOS:rate-limit:' + CONVERT(NVARCHAR(64), HASHBYTES('SHA2_256', @scope{index} + N':' + @key{index}), 2);
                """);
        }

        sql.AppendLine("DECLARE @lockCursor TABLE ([Ordinal] INT NOT NULL, [Scope] NVARCHAR(64) NOT NULL, [BucketKey] NVARCHAR(384) NOT NULL);");
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
                DELETE FROM {table}
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
                    FROM {table} WITH (UPDLOCK, HOLDLOCK)
                    WHERE [Scope] = @scope{index} AND [BucketKey] = @key{index} AND [LockedUntil] > @now;
                """);
        }

        sql.AppendLine("IF @rejectedIndex IS NULL");
        sql.AppendLine("BEGIN");
        for (var index = 0; index < count; index++)
        {
            sql.AppendLine($"""
                    UPDATE {table}
                    SET [Count] = [Count] + 1,
                        [LockedUntil] = CASE WHEN [Count] + 1 >= @threshold{index}
                            THEN @lockedUntil{index} ELSE NULL END,
                        [UpdatedAt] = @now
                    WHERE [Scope] = @scope{index} AND [BucketKey] = @key{index};
                    IF @@ROWCOUNT = 0
                        INSERT INTO {table}
                            ([Scope], [BucketKey], [WindowStartedAt], [Count], [LockedUntil], [UpdatedAt])
                        VALUES (@scope{index}, @key{index}, @now, 1,
                            CASE WHEN @threshold{index} <= 1 THEN @lockedUntil{index} ELSE NULL END, @now);
                """);
        }

        sql.AppendLine("END");
        sql.AppendLine($"""
            ;WITH staleBuckets AS (
                SELECT TOP (@cleanupBatchSize) *
                FROM {table}
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
                LEFT JOIN {table} bucket{index}
                  ON bucket{index}.[Scope] = @scope{index} AND bucket{index}.[BucketKey] = @key{index}
                """);
        }

        sql.AppendLine("COMMIT TRANSACTION;");
        return sql.ToString();
    }

    public string BuildRateLimitReleaseManySql(string schema, int count)
    {
        var table = Qualify(schema, "SqlOSRateLimitBuckets");
        var sql = new System.Text.StringBuilder();
        sql.AppendLine("SET XACT_ABORT ON;");
        sql.AppendLine("SET NOCOUNT ON;");
        sql.AppendLine("BEGIN TRANSACTION;");
        sql.AppendLine("DECLARE @applicationLockResult INT;");
        sql.AppendLine("DECLARE @lockCursor TABLE ([Ordinal] INT NOT NULL, [Scope] NVARCHAR(64) NOT NULL, [BucketKey] NVARCHAR(384) NOT NULL);");
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
                UPDATE {table}
                SET [Count] = CASE WHEN [Count] > 0 THEN [Count] - 1 ELSE 0 END,
                    [LockedUntil] = CASE WHEN [Count] - 1 < @threshold{index} THEN NULL ELSE [LockedUntil] END,
                    [UpdatedAt] = @now
                WHERE [Scope] = @scope{index} AND [BucketKey] = @key{index}
                  AND [WindowStartedAt] = @windowStartedAt{index};
                DELETE FROM {table}
                WHERE [Scope] = @scope{index} AND [BucketKey] = @key{index}
                  AND [WindowStartedAt] = @windowStartedAt{index}
                  AND [Count] = 0;
                """);
        }

        sql.AppendLine("COMMIT TRANSACTION;");
        return sql.ToString();
    }
}

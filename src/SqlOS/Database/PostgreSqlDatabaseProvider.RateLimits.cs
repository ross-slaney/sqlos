namespace SqlOS.Database;

internal sealed partial class PostgreSqlDatabaseProvider
{
    public string BuildRateLimitIncrementSql(string schema)
    {
        var table = Qualify(schema, "SqlOSRateLimitBuckets");
        return $"""
            WITH expired AS (
              DELETE FROM {table}
              WHERE "Scope" = @scope
                AND "BucketKey" = @key
                AND ("LockedUntil" IS NULL OR "LockedUntil" <= @now)
                AND "WindowStartedAt" <= @windowStartedBefore
              RETURNING 1
            ),
            stale AS (
              DELETE FROM {table}
              WHERE ctid IN (
                  SELECT ctid
                  FROM {table}
                  WHERE "Scope" = @scope
                    AND "UpdatedAt" < @staleBefore
                    AND ("LockedUntil" IS NULL OR "LockedUntil" <= @now)
                  ORDER BY "UpdatedAt"
                  LIMIT @cleanupBatchSize)
              RETURNING 1
            ),
            existing AS (
              SELECT t.*,
                     CASE WHEN t."LockedUntil" IS NULL OR t."LockedUntil" <= @now THEN TRUE ELSE FALSE END AS admitted
              FROM {table} t
              WHERE t."Scope" = @scope AND t."BucketKey" = @key
            ),
            inserted AS (
              INSERT INTO {table} ("Scope", "BucketKey", "WindowStartedAt", "Count", "LockedUntil", "UpdatedAt")
              SELECT @scope, @key, @now, 1,
                     CASE WHEN @lockThreshold <= 1 THEN @lockedUntil ELSE NULL END,
                     @now
              WHERE NOT EXISTS (SELECT 1 FROM existing)
                AND NOT EXISTS (SELECT 1 FROM stale WHERE FALSE)
                AND NOT EXISTS (SELECT 1 FROM expired WHERE FALSE)
              RETURNING "Count", "LockedUntil", TRUE AS admitted
            ),
            updated AS (
              UPDATE {table} AS t
              SET
                "Count" = CASE WHEN t."LockedUntil" IS NOT NULL AND t."LockedUntil" > @now
                    THEN t."Count" ELSE t."Count" + 1 END,
                "LockedUntil" = CASE
                    WHEN t."LockedUntil" IS NOT NULL AND t."LockedUntil" > @now THEN t."LockedUntil"
                    WHEN t."Count" + 1 >= @lockThreshold THEN @lockedUntil
                    ELSE NULL
                END,
                "UpdatedAt" = @now
              FROM existing e
              WHERE t."Scope" = e."Scope" AND t."BucketKey" = e."BucketKey"
              RETURNING t."Count", t."LockedUntil", e.admitted
            )
            SELECT "Count", "LockedUntil", admitted FROM inserted
            UNION ALL
            SELECT "Count", "LockedUntil", admitted FROM updated;
            """;
    }

    public string BuildRateLimitGetSql(string schema)
    {
        var table = Qualify(schema, "SqlOSRateLimitBuckets");
        return $"""
            WITH expired AS (
              DELETE FROM {table}
              WHERE "Scope" = @scope
                AND "BucketKey" = @key
                AND ("LockedUntil" IS NULL OR "LockedUntil" <= @now)
                AND "WindowStartedAt" <= @windowStartedBefore
              RETURNING 1
            )
            SELECT t."Count", t."LockedUntil"
            FROM {table} t
            WHERE t."Scope" = @scope AND t."BucketKey" = @key
              AND NOT EXISTS (SELECT 1 FROM expired WHERE FALSE);
            """;
    }

    public string BuildRateLimitDeleteSql(string schema)
        => $"""DELETE FROM {Qualify(schema, "SqlOSRateLimitBuckets")} WHERE "Scope" = @scope AND "BucketKey" = @key""";

    public string BuildRateLimitDecrementSql(string schema)
    {
        var table = Qualify(schema, "SqlOSRateLimitBuckets");
        return $"""
            WITH updated AS (
              UPDATE {table}
              SET "Count" = CASE WHEN "Count" > 0 THEN "Count" - 1 ELSE 0 END,
                  "UpdatedAt" = @now
              WHERE "Scope" = @scope
                AND "BucketKey" = @key
                AND ("LockedUntil" IS NULL OR "LockedUntil" <= @now)
              RETURNING "Scope", "BucketKey", "Count"
            )
            DELETE FROM {table} t
            USING updated u
            WHERE t."Scope" = u."Scope" AND t."BucketKey" = u."BucketKey" AND u."Count" = 0;
            """;
    }

    public string BuildRateLimitReleaseSql(string schema)
    {
        var table = Qualify(schema, "SqlOSRateLimitBuckets");
        return $"""
            WITH updated AS (
              UPDATE {table} AS t
              SET "Count" = CASE WHEN t."Count" > 0 THEN t."Count" - 1 ELSE 0 END,
                  "LockedUntil" = CASE WHEN t."Count" - 1 < @lockThreshold THEN NULL ELSE t."LockedUntil" END,
                  "UpdatedAt" = @now
              WHERE t."Scope" = @scope AND t."BucketKey" = @key
                AND t."WindowStartedAt" = @windowStartedAt
              RETURNING t."Scope", t."BucketKey", t."Count", t."WindowStartedAt"
            )
            DELETE FROM {table} t
            USING updated u
            WHERE t."Scope" = u."Scope" AND t."BucketKey" = u."BucketKey"
              AND t."WindowStartedAt" = u."WindowStartedAt"
              AND u."Count" = 0;
            """;
    }

    public string BuildRateLimitReservePairSql(string schema)
    {
        var table = Qualify(schema, "SqlOSRateLimitBuckets");
        return $"""
            WITH expired AS (
              DELETE FROM {table}
              WHERE (
                    ("Scope" = @firstScope AND "BucketKey" = @firstKey
                     AND ("LockedUntil" IS NULL OR "LockedUntil" <= @now)
                     AND "WindowStartedAt" <= @firstWindowStartedBefore)
                 OR ("Scope" = @secondScope AND "BucketKey" = @secondKey
                     AND ("LockedUntil" IS NULL OR "LockedUntil" <= @now)
                     AND "WindowStartedAt" <= @secondWindowStartedBefore)
              )
              RETURNING 1
            ),
            stale AS (
              DELETE FROM {table}
              WHERE ctid IN (
                  SELECT ctid
                  FROM {table}
                  WHERE "Scope" IN (@firstScope, @secondScope)
                    AND "UpdatedAt" < @staleBefore
                    AND ("LockedUntil" IS NULL OR "LockedUntil" <= @now)
                  ORDER BY "UpdatedAt"
                  LIMIT @cleanupBatchSize)
              RETURNING 1
            ),
            first_existing AS (
              SELECT t.*
              FROM {table} t
              WHERE t."Scope" = @firstScope AND t."BucketKey" = @firstKey
            ),
            second_existing AS (
              SELECT t.*
              FROM {table} t
              WHERE t."Scope" = @secondScope AND t."BucketKey" = @secondKey
            ),
            rejected AS (
              SELECT 0 AS rejected_index, "LockedUntil" AS rejected_locked_until
              FROM first_existing
              WHERE "LockedUntil" > @now
              UNION ALL
              SELECT 1, "LockedUntil"
              FROM second_existing
              WHERE "LockedUntil" > @now
                AND NOT EXISTS (SELECT 1 FROM first_existing WHERE "LockedUntil" > @now)
              LIMIT 1
            ),
            first_upsert AS (
              INSERT INTO {table} ("Scope", "BucketKey", "WindowStartedAt", "Count", "LockedUntil", "UpdatedAt")
              SELECT @firstScope, @firstKey, @now,
                     COALESCE((SELECT "Count" FROM first_existing), 0) + 1,
                     CASE WHEN COALESCE((SELECT "Count" FROM first_existing), 0) + 1 >= @firstThreshold
                          THEN @firstLockedUntil ELSE NULL END,
                     @now
              WHERE NOT EXISTS (SELECT 1 FROM rejected)
                AND NOT EXISTS (SELECT 1 FROM expired WHERE FALSE)
                AND NOT EXISTS (SELECT 1 FROM stale WHERE FALSE)
              ON CONFLICT ("Scope", "BucketKey") DO UPDATE
              SET "Count" = {table}."Count" + 1,
                  "LockedUntil" = CASE WHEN {table}."Count" + 1 >= @firstThreshold
                      THEN @firstLockedUntil ELSE NULL END,
                  "UpdatedAt" = @now
              RETURNING "Count", "LockedUntil", "WindowStartedAt"
            ),
            second_upsert AS (
              INSERT INTO {table} ("Scope", "BucketKey", "WindowStartedAt", "Count", "LockedUntil", "UpdatedAt")
              SELECT @secondScope, @secondKey, @now,
                     COALESCE((SELECT "Count" FROM second_existing), 0) + 1,
                     CASE WHEN COALESCE((SELECT "Count" FROM second_existing), 0) + 1 >= @secondThreshold
                          THEN @secondLockedUntil ELSE NULL END,
                     @now
              WHERE NOT EXISTS (SELECT 1 FROM rejected)
              ON CONFLICT ("Scope", "BucketKey") DO UPDATE
              SET "Count" = {table}."Count" + 1,
                  "LockedUntil" = CASE WHEN {table}."Count" + 1 >= @secondThreshold
                      THEN @secondLockedUntil ELSE NULL END,
                  "UpdatedAt" = @now
              RETURNING "Count", "LockedUntil", "WindowStartedAt"
            )
            SELECT r.rejected_index, r.rejected_locked_until,
                   f."Count", f."LockedUntil", f."WindowStartedAt",
                   s."Count", s."LockedUntil", s."WindowStartedAt"
            FROM (SELECT 1) AS anchor
            LEFT JOIN rejected r ON TRUE
            LEFT JOIN first_upsert f ON TRUE
            LEFT JOIN second_upsert s ON TRUE
            LEFT JOIN {table} first_fallback
              ON first_fallback."Scope" = @firstScope AND first_fallback."BucketKey" = @firstKey
                 AND f."Count" IS NULL
            LEFT JOIN {table} second_fallback
              ON second_fallback."Scope" = @secondScope AND second_fallback."BucketKey" = @secondKey
                 AND s."Count" IS NULL;
            """;
    }

    public string BuildRateLimitReserveManySql(string schema, int count)
    {
        var table = Qualify(schema, "SqlOSRateLimitBuckets");
        var sql = new System.Text.StringBuilder();
        sql.AppendLine("WITH");

        for (var index = 0; index < count; index++)
        {
            sql.AppendLine($"""
                expired{index} AS (
                  DELETE FROM {table}
                  WHERE "Scope" = @scope{index}
                    AND "BucketKey" = @key{index}
                    AND ("LockedUntil" IS NULL OR "LockedUntil" <= @now)
                    AND "WindowStartedAt" <= @windowStartedBefore{index}
                  RETURNING 1
                ),
                """);
        }

        sql.AppendLine($"""
            stale AS (
              DELETE FROM {table}
              WHERE ctid IN (
                  SELECT ctid
                  FROM {table}
                  WHERE "Scope" IN ({string.Join(", ", Enumerable.Range(0, count).Select(index => $"@scope{index}"))})
                    AND "UpdatedAt" < @staleBefore
                    AND ("LockedUntil" IS NULL OR "LockedUntil" <= @now)
                  ORDER BY "UpdatedAt"
                  LIMIT @cleanupBatchSize)
              RETURNING 1
            ),
            """);

        for (var index = 0; index < count; index++)
        {
            sql.AppendLine($"""
                existing{index} AS (
                  SELECT t.*
                  FROM {table} t
                  WHERE t."Scope" = @scope{index} AND t."BucketKey" = @key{index}
                ),
                """);
        }

        sql.AppendLine("rejected AS (");
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
            {
                sql.AppendLine("  UNION ALL");
            }

            var priorLocks = index == 0
                ? "TRUE"
                : string.Join(
                    " AND ",
                    Enumerable.Range(0, index).Select(prior =>
                        $"NOT EXISTS (SELECT 1 FROM existing{prior} WHERE \"LockedUntil\" > @now)"));
            sql.AppendLine($"""
                  SELECT {index} AS rejected_index, "LockedUntil" AS rejected_locked_until
                  FROM existing{index}
                  WHERE "LockedUntil" > @now AND {priorLocks}
                """);
        }

        sql.AppendLine("  LIMIT 1");
        sql.AppendLine("),");

        for (var index = 0; index < count; index++)
        {
            var comma = index < count - 1 ? "," : string.Empty;
            sql.AppendLine($"""
                upsert{index} AS (
                  INSERT INTO {table} ("Scope", "BucketKey", "WindowStartedAt", "Count", "LockedUntil", "UpdatedAt")
                  SELECT @scope{index}, @key{index}, @now,
                         COALESCE((SELECT "Count" FROM existing{index}), 0) + 1,
                         CASE WHEN COALESCE((SELECT "Count" FROM existing{index}), 0) + 1 >= @threshold{index}
                              THEN @lockedUntil{index} ELSE NULL END,
                         @now
                  WHERE NOT EXISTS (SELECT 1 FROM rejected)
                    AND NOT EXISTS (SELECT 1 FROM stale WHERE FALSE)
                    AND NOT EXISTS (SELECT 1 FROM expired{index} WHERE FALSE)
                  ON CONFLICT ("Scope", "BucketKey") DO UPDATE
                  SET "Count" = {table}."Count" + 1,
                      "LockedUntil" = CASE WHEN {table}."Count" + 1 >= @threshold{index}
                          THEN @lockedUntil{index} ELSE NULL END,
                      "UpdatedAt" = @now
                  RETURNING "Count", "LockedUntil", "WindowStartedAt"
                ){comma}
                """);
        }

        sql.Append("SELECT r.rejected_index, r.rejected_locked_until");
        for (var index = 0; index < count; index++)
        {
            sql.Append($", u{index}.\"Count\", u{index}.\"LockedUntil\", u{index}.\"WindowStartedAt\"");
        }

        sql.AppendLine();
        sql.AppendLine("FROM (SELECT 1) AS anchor");
        sql.AppendLine("LEFT JOIN rejected r ON TRUE");
        for (var index = 0; index < count; index++)
        {
            sql.AppendLine($"LEFT JOIN upsert{index} u{index} ON TRUE");
        }

        return sql.ToString();
    }

    public string BuildRateLimitReleaseManySql(string schema, int count)
    {
        var table = Qualify(schema, "SqlOSRateLimitBuckets");
        var sql = new System.Text.StringBuilder();
        sql.AppendLine("WITH");

        for (var index = 0; index < count; index++)
        {
            var comma = index < count - 1 ? "," : string.Empty;
            sql.AppendLine($"""
                updated{index} AS (
                  UPDATE {table} AS t
                  SET "Count" = CASE WHEN t."Count" > 0 THEN t."Count" - 1 ELSE 0 END,
                      "LockedUntil" = CASE WHEN t."Count" - 1 < @threshold{index} THEN NULL ELSE t."LockedUntil" END,
                      "UpdatedAt" = @now
                  WHERE t."Scope" = @scope{index} AND t."BucketKey" = @key{index}
                    AND t."WindowStartedAt" = @windowStartedAt{index}
                  RETURNING t."Scope", t."BucketKey", t."Count", t."WindowStartedAt"
                ),
                deleted{index} AS (
                  DELETE FROM {table} t
                  USING updated{index} u
                  WHERE t."Scope" = u."Scope" AND t."BucketKey" = u."BucketKey"
                    AND t."WindowStartedAt" = u."WindowStartedAt"
                    AND u."Count" = 0
                  RETURNING 1
                ){comma}
                """);
        }

        sql.Append("SELECT ");
        sql.Append(string.Join(", ", Enumerable.Range(0, count).Select(index => $"(SELECT COUNT(*) FROM deleted{index})")));
        sql.AppendLine(";");
        return sql.ToString();
    }
}

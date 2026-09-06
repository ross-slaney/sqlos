-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSRateLimitBuckets" (
        "Scope" varchar(64) NOT NULL,
        "BucketKey" varchar(384) NOT NULL,
        "WindowStartedAt" timestamp NOT NULL,
        "Count" INT NOT NULL,
        "LockedUntil" timestamp NULL,
        "UpdatedAt" timestamp NOT NULL,
        CONSTRAINT "PK_SqlOSRateLimitBuckets" PRIMARY KEY ("Scope", "BucketKey"),
        CONSTRAINT "CK_SqlOSRateLimitBuckets_Count" CHECK ("Count" >= 0)
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSRateLimitBuckets')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSRateLimitBuckets' AND column_name = 'UpdatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSRateLimitBuckets_UpdatedAt"
        ON "{Schema}"."SqlOSRateLimitBuckets" ("UpdatedAt");
  END IF;
END
$sqlos_guard$;

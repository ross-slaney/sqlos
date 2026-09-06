-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSPasswordLoginBuckets" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "Scope" varchar(40) NOT NULL,
        "BucketKey" varchar(512) NOT NULL,
        "NormalizedEmail" varchar(320) NULL,
        "UserId" varchar(64) NULL,
        "ClientKey" varchar(850) NULL,
        "IpAddress" varchar(128) NULL,
        "UserAgentHash" varchar(128) NULL,
        "FailureCount" INT NOT NULL CONSTRAINT "DF_SqlOSPasswordLoginBuckets_FailureCount" DEFAULT 0,
        "WindowStartedAt" timestamp NULL,
        "LastFailureAt" timestamp NULL,
        "LastSuccessAt" timestamp NULL,
        "LockedUntil" timestamp NULL,
        "LockoutReason" varchar(120) NULL,
        "CreatedAt" timestamp NOT NULL,
        "UpdatedAt" timestamp NOT NULL
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSPasswordLoginBuckets')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPasswordLoginBuckets' AND column_name = 'Scope') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPasswordLoginBuckets' AND column_name = 'BucketKey') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSPasswordLoginBuckets_Scope_BucketKey"
        ON "{Schema}"."SqlOSPasswordLoginBuckets"("Scope", "BucketKey");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSPasswordLoginBuckets')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPasswordLoginBuckets' AND column_name = 'NormalizedEmail') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPasswordLoginBuckets' AND column_name = 'UpdatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSPasswordLoginBuckets_NormalizedEmail_UpdatedAt"
        ON "{Schema}"."SqlOSPasswordLoginBuckets"("NormalizedEmail", "UpdatedAt");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSPasswordLoginBuckets')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPasswordLoginBuckets' AND column_name = 'UserId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPasswordLoginBuckets' AND column_name = 'UpdatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSPasswordLoginBuckets_UserId_UpdatedAt"
        ON "{Schema}"."SqlOSPasswordLoginBuckets"("UserId", "UpdatedAt");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSPasswordLoginBuckets')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPasswordLoginBuckets' AND column_name = 'IpAddress') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPasswordLoginBuckets' AND column_name = 'UpdatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSPasswordLoginBuckets_IpAddress_UpdatedAt"
        ON "{Schema}"."SqlOSPasswordLoginBuckets"("IpAddress", "UpdatedAt");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSPasswordLoginBuckets')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPasswordLoginBuckets' AND column_name = 'ClientKey') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPasswordLoginBuckets' AND column_name = 'UpdatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSPasswordLoginBuckets_ClientKey_UpdatedAt"
        ON "{Schema}"."SqlOSPasswordLoginBuckets"("ClientKey", "UpdatedAt");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSPasswordLoginBuckets')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPasswordLoginBuckets' AND column_name = 'LockedUntil') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSPasswordLoginBuckets_LockedUntil"
        ON "{Schema}"."SqlOSPasswordLoginBuckets"("LockedUntil");
  END IF;
END
$sqlos_guard$;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSPasswordLoginBuckets"
        ADD CONSTRAINT "FK_SqlOSPasswordLoginBuckets_Users_UserId"
            FOREIGN KEY ("UserId") REFERENCES "{Schema}"."SqlOSUsers"("Id");

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (17);

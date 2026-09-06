-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
-- Older test/repair installations can carry a schema version without the historical
-- password bucket table. Repair it when the user prerequisite exists; otherwise keep
-- this migration compatible with intentionally partial schemas used for upgrade checks.
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
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSPasswordLoginBuckets')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPasswordLoginBuckets' AND column_name = 'LockedUntil') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSPasswordLoginBuckets_LockedUntil"
        ON "{Schema}"."SqlOSPasswordLoginBuckets"("LockedUntil");
  END IF;
END
$sqlos_guard$;

DO $sqlos$
BEGIN
    IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSPasswordLoginBuckets')) IS NOT NULL
       AND to_regclass(format('%I.%I', '{Schema}', 'SqlOSUsers')) IS NOT NULL THEN
        ALTER TABLE IF EXISTS "{Schema}"."SqlOSPasswordLoginBuckets"
            ADD CONSTRAINT "FK_SqlOSPasswordLoginBuckets_Users_UserId"
                FOREIGN KEY ("UserId") REFERENCES "{Schema}"."SqlOSUsers"("Id");
    END IF;
EXCEPTION
    WHEN duplicate_object THEN NULL;
END
$sqlos$;

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSPasswordLoginReservations" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "CreatedAt" timestamp NOT NULL,
        "ExpiresAt" timestamp NOT NULL
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSPasswordLoginReservations')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPasswordLoginReservations' AND column_name = 'ExpiresAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSPasswordLoginReservations_ExpiresAt"
        ON "{Schema}"."SqlOSPasswordLoginReservations"("ExpiresAt");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSPasswordLoginReservations')) IS NOT NULL AND to_regclass(format('%I.%I', '{Schema}', 'SqlOSPasswordLoginBuckets')) IS NOT NULL THEN
    CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSPasswordLoginReservationBuckets" (
        "ReservationId" varchar(64) NOT NULL,
        "BucketId" varchar(64) NOT NULL,
        CONSTRAINT "PK_SqlOSPasswordLoginReservationBuckets" PRIMARY KEY ("ReservationId", "BucketId"),
        CONSTRAINT "FK_SqlOSPasswordLoginReservationBuckets_Reservations_ReservationId"
            FOREIGN KEY ("ReservationId") REFERENCES "{Schema}"."SqlOSPasswordLoginReservations"("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_SqlOSPasswordLoginReservationBuckets_Buckets_BucketId"
            FOREIGN KEY ("BucketId") REFERENCES "{Schema}"."SqlOSPasswordLoginBuckets"("Id") ON DELETE CASCADE
    );
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSPasswordLoginReservationBuckets')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPasswordLoginReservationBuckets' AND column_name = 'BucketId') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSPasswordLoginReservationBuckets_BucketId"
        ON "{Schema}"."SqlOSPasswordLoginReservationBuckets"("BucketId");
  END IF;
END
$sqlos_guard$;

-- Client identifiers may use the full 850-character registration limit. Keeping that
-- diagnostic value in a composite SQL Server index can exceed the 1,700-byte key limit.
DROP INDEX IF EXISTS "{Schema}"."IX_SqlOSPasswordLoginBuckets_ClientKey_UpdatedAt";

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (39);

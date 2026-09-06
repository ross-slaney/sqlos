-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSMfaAttemptBuckets" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "Scope" varchar(40) NOT NULL,
        "BucketKey" varchar(512) NOT NULL,
        "AttemptCount" INT NOT NULL CONSTRAINT "DF_SqlOSMfaAttemptBuckets_AttemptCount" DEFAULT 0,
        "WindowStartedAt" timestamp NULL,
        "CreatedAt" timestamp NOT NULL,
        "UpdatedAt" timestamp NOT NULL
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSMfaAttemptBuckets')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSMfaAttemptBuckets' AND column_name = 'Scope') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSMfaAttemptBuckets' AND column_name = 'BucketKey') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSMfaAttemptBuckets_Scope_BucketKey"
        ON "{Schema}"."SqlOSMfaAttemptBuckets"("Scope", "BucketKey");
  END IF;
END
$sqlos_guard$;

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSMfaAttemptReservations" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "CreatedAt" timestamp NOT NULL,
        "ExpiresAt" timestamp NOT NULL
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSMfaAttemptReservations')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSMfaAttemptReservations' AND column_name = 'ExpiresAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSMfaAttemptReservations_ExpiresAt"
        ON "{Schema}"."SqlOSMfaAttemptReservations"("ExpiresAt");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSMfaAttemptReservations')) IS NOT NULL AND to_regclass(format('%I.%I', '{Schema}', 'SqlOSMfaAttemptBuckets')) IS NOT NULL THEN
    CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSMfaAttemptReservationBuckets" (
        "ReservationId" varchar(64) NOT NULL,
        "BucketId" varchar(64) NOT NULL,
        CONSTRAINT "PK_SqlOSMfaAttemptReservationBuckets" PRIMARY KEY ("ReservationId", "BucketId"),
        CONSTRAINT "FK_SqlOSMfaAttemptReservationBuckets_Reservations_ReservationId"
            FOREIGN KEY ("ReservationId") REFERENCES "{Schema}"."SqlOSMfaAttemptReservations"("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_SqlOSMfaAttemptReservationBuckets_Buckets_BucketId"
            FOREIGN KEY ("BucketId") REFERENCES "{Schema}"."SqlOSMfaAttemptBuckets"("Id") ON DELETE CASCADE
    );
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSMfaAttemptReservationBuckets')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSMfaAttemptReservationBuckets' AND column_name = 'BucketId') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSMfaAttemptReservationBuckets_BucketId"
        ON "{Schema}"."SqlOSMfaAttemptReservationBuckets"("BucketId");
  END IF;
END
$sqlos_guard$;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (40);

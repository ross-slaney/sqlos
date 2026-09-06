-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuditEvents" ADD COLUMN IF NOT EXISTS "ApplicationId" varchar(64) NULL;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuditEvents" ADD COLUMN IF NOT EXISTS "ApplicationKey" varchar(200) NULL;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuditEvents" ADD COLUMN IF NOT EXISTS "Source" varchar(80) NOT NULL CONSTRAINT "DF_SqlOSAuditEvents_Source" DEFAULT ('authserver');

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuditEvents" ADD COLUMN IF NOT EXISTS "Action" varchar(160) NOT NULL CONSTRAINT "DF_SqlOSAuditEvents_Action" DEFAULT ('');

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuditEvents" ADD COLUMN IF NOT EXISTS "ActorDisplayName" varchar(320) NULL;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuditEvents" ADD COLUMN IF NOT EXISTS "TargetsJson" text NOT NULL CONSTRAINT "DF_SqlOSAuditEvents_TargetsJson" DEFAULT ('[]');

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuditEvents" ADD COLUMN IF NOT EXISTS "ContextJson" text NULL;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuditEvents" ADD COLUMN IF NOT EXISTS "MetadataJson" text NULL;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuditEvents" ADD COLUMN IF NOT EXISTS "IngestedAt" timestamp NOT NULL CONSTRAINT "DF_SqlOSAuditEvents_IngestedAt" DEFAULT ((CURRENT_TIMESTAMP AT TIME ZONE 'UTC'));

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuditEvents" ADD COLUMN IF NOT EXISTS "UserAgent" varchar(512) NULL;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuditEvents" ADD COLUMN IF NOT EXISTS "RequestId" varchar(128) NULL;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuditEvents" ADD COLUMN IF NOT EXISTS "CorrelationId" varchar(128) NULL;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuditEvents" ADD COLUMN IF NOT EXISTS "IdempotencyKeyHash" varchar(128) NULL;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuditEvents')) IS NOT NULL THEN
    UPDATE "{Schema}"."SqlOSAuditEvents"
    SET "Action" = "EventType"
    WHERE NULLIF("Action", '') IS NULL;
  END IF;
END
$sqlos_guard$;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuditEvents" ALTER COLUMN "EventType" TYPE varchar(160);
ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuditEvents" ALTER COLUMN "EventType" SET NOT NULL;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuditEvents" ALTER COLUMN "ActorId" TYPE varchar(128);
ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuditEvents" ALTER COLUMN "ActorId" DROP NOT NULL;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuditEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'OccurredAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSAuditEvents_OccurredAt" ON "{Schema}"."SqlOSAuditEvents" ("OccurredAt" DESC);
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuditEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'OrganizationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'OccurredAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSAuditEvents_OrganizationId_OccurredAt" ON "{Schema}"."SqlOSAuditEvents" ("OrganizationId", "OccurredAt" DESC);
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuditEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'ApplicationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'OccurredAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSAuditEvents_ApplicationId_OccurredAt" ON "{Schema}"."SqlOSAuditEvents" ("ApplicationId", "OccurredAt" DESC);
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuditEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'ApplicationKey') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'OccurredAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSAuditEvents_ApplicationKey_OccurredAt" ON "{Schema}"."SqlOSAuditEvents" ("ApplicationKey", "OccurredAt" DESC);
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuditEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'Source') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'OccurredAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSAuditEvents_Source_OccurredAt" ON "{Schema}"."SqlOSAuditEvents" ("Source", "OccurredAt" DESC);
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuditEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'Action') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'OccurredAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSAuditEvents_Action_OccurredAt" ON "{Schema}"."SqlOSAuditEvents" ("Action", "OccurredAt" DESC);
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuditEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'ActorType') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'ActorId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'OccurredAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSAuditEvents_Actor_OccurredAt" ON "{Schema}"."SqlOSAuditEvents" ("ActorType", "ActorId", "OccurredAt" DESC);
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuditEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'IdempotencyKeyHash') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "UX_SqlOSAuditEvents_IdempotencyKeyHash" ON "{Schema}"."SqlOSAuditEvents" ("IdempotencyKeyHash") WHERE "IdempotencyKeyHash" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (23);

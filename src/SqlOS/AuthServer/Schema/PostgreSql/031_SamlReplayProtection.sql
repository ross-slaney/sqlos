-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSSamlReplays" (
        "Id" varchar(64) NOT NULL CONSTRAINT "PK_SqlOSSamlReplays" PRIMARY KEY,
        "ConnectionId" varchar(64) NOT NULL,
        "ResponseId" varchar(450) NOT NULL,
        "AssertionId" varchar(450) NOT NULL,
        "ConsumedAt" timestamp NOT NULL,
        "ExpiresAt" timestamp NOT NULL
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSSamlReplays')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSamlReplays' AND column_name = 'ConnectionId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSamlReplays' AND column_name = 'ResponseId') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "UX_SqlOSSamlReplays_Connection_Response"
        ON "{Schema}"."SqlOSSamlReplays"("ConnectionId", "ResponseId");
  END IF;
END
$sqlos_guard$;
    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSSamlReplays')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSamlReplays' AND column_name = 'ConnectionId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSamlReplays' AND column_name = 'AssertionId') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "UX_SqlOSSamlReplays_Connection_Assertion"
        ON "{Schema}"."SqlOSSamlReplays"("ConnectionId", "AssertionId");
  END IF;
END
$sqlos_guard$;
    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSSamlReplays')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSamlReplays' AND column_name = 'ExpiresAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSSamlReplays_ExpiresAt"
        ON "{Schema}"."SqlOSSamlReplays"("ExpiresAt");
  END IF;
END
$sqlos_guard$;

UPDATE "{Schema}"."SqlOSSchema" SET "Version" = 31;
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") SELECT 31 WHERE NOT EXISTS (SELECT 1 FROM "{Schema}"."SqlOSSchema");

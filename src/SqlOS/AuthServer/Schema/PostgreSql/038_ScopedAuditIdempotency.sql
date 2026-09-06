-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuditEvents"
    ADD COLUMN IF NOT EXISTS "IdempotencyScopeHash" varchar(128) NULL;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuditEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'IdempotencyScopeHash') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "UX_SqlOSAuditEvents_IdempotencyScopeHash"
    ON "{Schema}"."SqlOSAuditEvents" ("IdempotencyScopeHash")
    WHERE "IdempotencyScopeHash" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (38);

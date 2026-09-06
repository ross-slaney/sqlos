-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSApplicationAssignments"
    ADD COLUMN IF NOT EXISTS "ConfigurationOwner" varchar(40) NOT NULL CONSTRAINT "DF_SqlOSApplicationAssignments_ConfigurationOwner" DEFAULT 'dashboard',
    ADD COLUMN IF NOT EXISTS "ConfigurationSourceKey" varchar(160) NULL,
    ADD COLUMN IF NOT EXISTS "ConfigurationFingerprint" varchar(64) NULL,
    ADD COLUMN IF NOT EXISTS "LastReconciledAt" timestamp NULL,
    ADD COLUMN IF NOT EXISTS "ConfigurationOrphanedAt" timestamp NULL;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSApplicationAssignments')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSApplicationAssignments' AND column_name = 'ClientApplicationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSApplicationAssignments' AND column_name = 'ConfigurationOwner') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSApplicationAssignments' AND column_name = 'ConfigurationSourceKey') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "UX_SqlOSApplicationAssignments_Client_Owner_SourceKey"
    ON "{Schema}"."SqlOSApplicationAssignments"("ClientApplicationId", "ConfigurationOwner", "ConfigurationSourceKey")
    WHERE "ConfigurationSourceKey" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

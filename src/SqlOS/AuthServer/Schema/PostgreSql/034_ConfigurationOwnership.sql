-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "ConfigurationOwner" varchar(40) NOT NULL CONSTRAINT "DF_SqlOSClientApplications_ConfigurationOwner" DEFAULT 'dashboard',
    ADD COLUMN IF NOT EXISTS "ConfigurationSourceKey" varchar(160) NULL,
    ADD COLUMN IF NOT EXISTS "ConfigurationFingerprint" varchar(64) NULL,
    ADD COLUMN IF NOT EXISTS "LastReconciledAt" timestamp NULL,
    ADD COLUMN IF NOT EXISTS "ConfigurationOrphanedAt" timestamp NULL;

DO $sqlos$
BEGIN
  IF EXISTS (
      SELECT 1
      FROM information_schema.columns
      WHERE table_schema = '{Schema}'
        AND table_name = 'SqlOSClientApplications'
        AND column_name = 'RegistrationSource')
  THEN
    UPDATE "{Schema}"."SqlOSClientApplications"
    SET
        "ConfigurationOwner" = CASE
            WHEN "RegistrationSource" = 'seeded' THEN 'code'
            WHEN "RegistrationSource" IN ('dcr', 'cimd') THEN 'dynamic'
            ELSE 'dashboard'
        END,
        "ConfigurationSourceKey" = CASE WHEN "RegistrationSource" = 'seeded' THEN "ClientId" ELSE NULL END;
  END IF;
END
$sqlos$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSClientApplications')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSClientApplications' AND column_name = 'ConfigurationOwner') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSClientApplications' AND column_name = 'ConfigurationSourceKey') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "UX_SqlOSClientApplications_ConfigurationOwner_SourceKey"
    ON "{Schema}"."SqlOSClientApplications"("ConfigurationOwner", "ConfigurationSourceKey")
    WHERE "ConfigurationSourceKey" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthOidcConnections"
    ADD COLUMN IF NOT EXISTS "ConfigurationOwner" varchar(40) NOT NULL CONSTRAINT "DF_SqlOSAuthOidcConnections_ConfigurationOwner" DEFAULT 'dashboard',
    ADD COLUMN IF NOT EXISTS "ConfigurationSourceKey" varchar(160) NULL,
    ADD COLUMN IF NOT EXISTS "ConfigurationFingerprint" varchar(64) NULL,
    ADD COLUMN IF NOT EXISTS "LastReconciledAt" timestamp NULL,
    ADD COLUMN IF NOT EXISTS "ConfigurationOrphanedAt" timestamp NULL;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuthOidcConnections')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuthOidcConnections' AND column_name = 'ConfigurationOwner') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuthOidcConnections' AND column_name = 'ConfigurationSourceKey') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "UX_SqlOSAuthOidcConnections_ConfigurationOwner_SourceKey"
    ON "{Schema}"."SqlOSAuthOidcConnections"("ConfigurationOwner", "ConfigurationSourceKey")
    WHERE "ConfigurationSourceKey" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimConnections"
    ADD COLUMN IF NOT EXISTS "ConfigurationOwner" varchar(40) NOT NULL CONSTRAINT "DF_SqlOSScimConnections_ConfigurationOwner" DEFAULT 'dashboard',
    ADD COLUMN IF NOT EXISTS "ConfigurationSourceKey" varchar(160) NULL,
    ADD COLUMN IF NOT EXISTS "ConfigurationFingerprint" varchar(64) NULL,
    ADD COLUMN IF NOT EXISTS "LastReconciledAt" timestamp NULL,
    ADD COLUMN IF NOT EXISTS "ConfigurationOrphanedAt" timestamp NULL;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimConnections')) IS NOT NULL THEN
    UPDATE "{Schema}"."SqlOSScimConnections"
SET
    "ConfigurationOwner" = CASE WHEN "Source" = 'seeded' THEN 'code' ELSE 'dashboard' END,
    "ConfigurationSourceKey" = CASE WHEN "Source" = 'seeded' THEN "SeedKey" ELSE NULL END;
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimConnections')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimConnections' AND column_name = 'OrganizationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimConnections' AND column_name = 'ConfigurationOwner') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimConnections' AND column_name = 'ConfigurationSourceKey') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "UX_SqlOSScimConnections_Organization_Owner_SourceKey"
    ON "{Schema}"."SqlOSScimConnections"("OrganizationId", "ConfigurationOwner", "ConfigurationSourceKey")
    WHERE "ConfigurationSourceKey" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSMfaSettings"
    ADD COLUMN IF NOT EXISTS "ConfigurationOwner" varchar(40) NOT NULL CONSTRAINT "DF_SqlOSMfaSettings_ConfigurationOwner" DEFAULT 'system',
    ADD COLUMN IF NOT EXISTS "ConfigurationSourceKey" varchar(160) NULL,
    ADD COLUMN IF NOT EXISTS "ConfigurationFingerprint" varchar(64) NULL,
    ADD COLUMN IF NOT EXISTS "LastReconciledAt" timestamp NULL,
    ADD COLUMN IF NOT EXISTS "ConfigurationOrphanedAt" timestamp NULL;

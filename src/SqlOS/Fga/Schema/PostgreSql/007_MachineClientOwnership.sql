-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE "{Schema}"."{ServiceAccounts}"
    ADD COLUMN IF NOT EXISTS "ConfigurationOwner" varchar(32) NOT NULL CONSTRAINT "DF_{ServiceAccounts}_ConfigurationOwner" DEFAULT 'dashboard',
    ADD COLUMN IF NOT EXISTS "ConfigurationSourceKey" varchar(200) NULL,
    ADD COLUMN IF NOT EXISTS "ConfigurationFingerprint" varchar(128) NULL,
    ADD COLUMN IF NOT EXISTS "LastReconciledAt" timestamp NULL,
    ADD COLUMN IF NOT EXISTS "ConfigurationOrphanedAt" timestamp NULL;

ALTER TABLE "{Schema}"."{ServiceAccounts}" ALTER COLUMN "ClientId" TYPE varchar(450);
ALTER TABLE "{Schema}"."{ServiceAccounts}" ALTER COLUMN "ClientId" SET NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS "UX_{ServiceAccounts}_ClientId"
    ON "{Schema}"."{ServiceAccounts}" ("ClientId");

CREATE UNIQUE INDEX IF NOT EXISTS "UX_{ServiceAccounts}_ConfigurationSource"
    ON "{Schema}"."{ServiceAccounts}" ("ConfigurationOwner", "ConfigurationSourceKey")
    WHERE "ConfigurationSourceKey" IS NOT NULL;

UPDATE "{Schema}"."SqlOSFgaSchema" SET "Version" = 7 WHERE "Version" < 7;

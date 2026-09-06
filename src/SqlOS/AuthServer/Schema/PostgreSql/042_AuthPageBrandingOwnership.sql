-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthPageSettings"
    ADD COLUMN IF NOT EXISTS "AuthPageConfigurationOwner" varchar(40) NOT NULL CONSTRAINT "DF_SqlOSAuthPageSettings_AuthPageConfigurationOwner" DEFAULT 'system',
    ADD COLUMN IF NOT EXISTS "AuthPageConfigurationSourceKey" varchar(160) NULL,
    ADD COLUMN IF NOT EXISTS "AuthPageConfigurationFingerprint" varchar(64) NULL,
    ADD COLUMN IF NOT EXISTS "AuthPageLastReconciledAt" timestamp NULL,
    ADD COLUMN IF NOT EXISTS "AuthPageConfigurationOrphanedAt" timestamp NULL,
    ADD COLUMN IF NOT EXISTS "EmailConfigurationOwner" varchar(40) NOT NULL CONSTRAINT "DF_SqlOSAuthPageSettings_EmailConfigurationOwner" DEFAULT 'system',
    ADD COLUMN IF NOT EXISTS "EmailConfigurationSourceKey" varchar(160) NULL,
    ADD COLUMN IF NOT EXISTS "EmailConfigurationFingerprint" varchar(64) NULL,
    ADD COLUMN IF NOT EXISTS "EmailLastReconciledAt" timestamp NULL,
    ADD COLUMN IF NOT EXISTS "EmailConfigurationOrphanedAt" timestamp NULL;

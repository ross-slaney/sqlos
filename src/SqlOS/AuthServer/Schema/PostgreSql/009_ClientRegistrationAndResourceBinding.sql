-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications" ALTER COLUMN "ClientId" TYPE varchar(850);
ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications" ALTER COLUMN "ClientId" SET NOT NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications" ALTER COLUMN "Audience" TYPE varchar(850);
ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications" ALTER COLUMN "Audience" SET NOT NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "RegistrationSource" varchar(20) NOT NULL
        CONSTRAINT "DF_SqlOSClientApplications_RegistrationSource" DEFAULT 'manual';

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "TokenEndpointAuthMethod" varchar(60) NOT NULL
        CONSTRAINT "DF_SqlOSClientApplications_TokenEndpointAuthMethod" DEFAULT 'none';

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "GrantTypesJson" text NOT NULL
        CONSTRAINT "DF_SqlOSClientApplications_GrantTypesJson" DEFAULT '"""authorization_code"",""refresh_token"""';

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "ResponseTypesJson" text NOT NULL
        CONSTRAINT "DF_SqlOSClientApplications_ResponseTypesJson" DEFAULT '"""code"""';

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "MetadataDocumentUrl" varchar(850) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "ClientUri" varchar(850) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "LogoUri" varchar(850) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "SoftwareId" varchar(200) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "SoftwareVersion" varchar(120) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "MetadataJson" text NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "MetadataFetchedAt" timestamp NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "MetadataExpiresAt" timestamp NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "MetadataEtag" varchar(256) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "MetadataLastModifiedAt" timestamp NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "LastSeenAt" timestamp NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "DisabledAt" timestamp NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "DisabledReason" varchar(500) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSSessions"
    ADD COLUMN IF NOT EXISTS "Resource" varchar(2048) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSSessions"
    ADD COLUMN IF NOT EXISTS "EffectiveAudience" varchar(2048) NULL;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSClientApplications')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSClientApplications' AND column_name = 'RegistrationSource') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSClientApplications_RegistrationSource"
        ON "{Schema}"."SqlOSClientApplications" ("RegistrationSource");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSClientApplications')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSClientApplications' AND column_name = 'IsActive') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSClientApplications' AND column_name = 'RegistrationSource') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSClientApplications_IsActive_RegistrationSource"
        ON "{Schema}"."SqlOSClientApplications" ("IsActive", "RegistrationSource");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSClientApplications')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSClientApplications' AND column_name = 'MetadataDocumentUrl') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSClientApplications_MetadataDocumentUrl"
        ON "{Schema}"."SqlOSClientApplications" ("MetadataDocumentUrl");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSClientApplications')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSClientApplications' AND column_name = 'LastSeenAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSClientApplications_LastSeenAt"
        ON "{Schema}"."SqlOSClientApplications" ("LastSeenAt");
  END IF;
END
$sqlos_guard$;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (9);

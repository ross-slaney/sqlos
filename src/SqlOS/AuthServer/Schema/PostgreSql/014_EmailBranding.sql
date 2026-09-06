-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthPageSettings"
    ADD COLUMN IF NOT EXISTS "EmailApplicationName" varchar(200) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthPageSettings"
    ADD COLUMN IF NOT EXISTS "EmailLogoBase64" text NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthPageSettings"
    ADD COLUMN IF NOT EXISTS "EmailPrimaryColor" varchar(32) NOT NULL
        CONSTRAINT "DF_SqlOSAuthPageSettings_EmailPrimaryColor" DEFAULT '#2563eb';

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthPageSettings"
    ADD COLUMN IF NOT EXISTS "EmailAccentColor" varchar(32) NOT NULL
        CONSTRAINT "DF_SqlOSAuthPageSettings_EmailAccentColor" DEFAULT '#0f172a';

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthPageSettings"
    ADD COLUMN IF NOT EXISTS "EmailBackgroundColor" varchar(32) NOT NULL
        CONSTRAINT "DF_SqlOSAuthPageSettings_EmailBackgroundColor" DEFAULT '#f8fafc';

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (14);

-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests"
    ADD COLUMN IF NOT EXISTS "PresentationMode" varchar(32) NOT NULL
        CONSTRAINT "DF_SqlOSAuthorizationRequests_PresentationMode" DEFAULT 'hosted';

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests"
    ADD COLUMN IF NOT EXISTS "UiContextJson" text NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthPageSettings"
    DROP COLUMN IF EXISTS "PresentationMode";

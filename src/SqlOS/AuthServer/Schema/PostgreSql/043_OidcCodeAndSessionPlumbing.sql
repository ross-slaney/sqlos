-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationCodes"
    ADD COLUMN IF NOT EXISTS "Nonce" varchar(256) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationCodes"
    ADD COLUMN IF NOT EXISTS "AuthTime" timestamp NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSSessions"
    ADD COLUMN IF NOT EXISTS "Scope" varchar(1000) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSSessions"
    ADD COLUMN IF NOT EXISTS "AuthenticatedAt" timestamp NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSDeviceAuthorizations"
    ADD COLUMN IF NOT EXISTS "AuthTime" timestamp NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests"
    ADD COLUMN IF NOT EXISTS "MaxAgeSeconds" BIGINT NULL;

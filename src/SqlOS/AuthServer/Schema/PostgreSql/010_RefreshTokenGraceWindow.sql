-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSSettings"
    ADD COLUMN IF NOT EXISTS "RefreshTokenGraceWindowSeconds" INT NOT NULL CONSTRAINT "DF_SqlOSSettings_RefreshTokenGraceWindowSeconds" DEFAULT 30;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSRefreshTokens"
    ADD COLUMN IF NOT EXISTS "ReplacementAccessToken" text NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSRefreshTokens"
    ADD COLUMN IF NOT EXISTS "ReplacementOrganizationId" varchar(64) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSRefreshTokens"
    ADD COLUMN IF NOT EXISTS "ReplacementAccessTokenExpiresAt" timestamp NULL;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (10);

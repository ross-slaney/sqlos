-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSSettings"
    ADD COLUMN IF NOT EXISTS "SigningKeyRotationIntervalDays" INT NOT NULL CONSTRAINT "DF_SqlOSSettings_RotationInterval" DEFAULT 90;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSSettings"
    ADD COLUMN IF NOT EXISTS "SigningKeyGraceWindowDays" INT NOT NULL CONSTRAINT "DF_SqlOSSettings_GraceWindow" DEFAULT 7;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSSettings"
    ADD COLUMN IF NOT EXISTS "SigningKeyRetiredCleanupDays" INT NOT NULL CONSTRAINT "DF_SqlOSSettings_RetiredCleanup" DEFAULT 30;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (6);

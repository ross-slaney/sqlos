-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "AllowNativeHeadlessAuth" boolean NOT NULL CONSTRAINT "DF_SqlOSClientApplications_AllowNativeHeadlessAuth" DEFAULT FALSE;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (11);

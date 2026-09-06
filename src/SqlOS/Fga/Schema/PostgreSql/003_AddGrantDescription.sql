-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
-- SqlOSFga Schema v3: Add Description column to Grants table
-- Example migration demonstrating the pattern for future schema changes.

-- Add Description column to Grants table (it doesn't exist)
ALTER TABLE "{Schema}"."{Grants}" ADD COLUMN IF NOT EXISTS "Description" text NULL;

-- Update schema version to 3
UPDATE "{Schema}"."SqlOSFgaSchema" SET "Version" = 3 WHERE "Version" < 3;

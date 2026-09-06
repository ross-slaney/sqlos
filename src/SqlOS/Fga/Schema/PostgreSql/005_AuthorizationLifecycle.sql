-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
-- SqlOSFga Schema v5: explicit group lifecycle used by authorization enforcement.

ALTER TABLE "{Schema}"."{UserGroups}"
        ADD COLUMN IF NOT EXISTS "IsActive" boolean NOT NULL
            CONSTRAINT "DF_{UserGroups}_IsActive" DEFAULT TRUE;

UPDATE "{Schema}"."SqlOSFgaSchema" SET "Version" = 5 WHERE "Version" < 5;

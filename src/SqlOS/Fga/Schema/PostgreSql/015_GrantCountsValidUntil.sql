-- PostgreSQL translation of the matching SQL Server script.
-- SqlOSFga Schema v15: when a principal's grant counts fall behind the clock (see the SQL Server script).

ALTER TABLE "{Schema}"."SqlOSFgaGrantCounts" ADD COLUMN IF NOT EXISTS "ValidUntil" timestamp NULL;

UPDATE "{Schema}"."SqlOSFgaSchema" SET "Version" = 15 WHERE "Version" < 15;

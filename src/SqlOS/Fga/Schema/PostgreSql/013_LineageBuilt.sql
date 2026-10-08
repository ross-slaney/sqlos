-- PostgreSQL translation of the matching SQL Server script.
-- SqlOSFga Schema v13: whether the resource lineage has been built in full.
--
-- The rebuild sets this flag when it finishes, and startup rebuilds while it is clear. PostgreSQL rebuilds in
-- one transaction, so a failed rebuild leaves the flag as it was, with the lineage it describes.

ALTER TABLE "{Schema}"."SqlOSFgaSchema" ADD COLUMN IF NOT EXISTS "LineageBuilt" boolean NOT NULL DEFAULT false;

UPDATE "{Schema}"."SqlOSFgaSchema" SET "Version" = 13 WHERE "Version" < 13;

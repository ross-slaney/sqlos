-- PostgreSQL translation of the matching SQL Server script.
-- SqlOSFga Schema v16: find one subject's grants on one resource with one seek.
--
-- The point check reads, on each ancestor of a resource, the grants of the caller's live subjects. Keyed on the
-- resource, it reads every grant on the ancestor, whoever holds it. ResourceSubjectHash is a 64-bit hash of the
-- two ids (the resource id's, seeded with the subject id's), so one seek finds one subject's grants on one
-- resource, and only that index can answer it: the planner cannot trade it for the grants by resource. Different
-- pairs may share a hash; the lookup compares the ids. The grants by subject carry what the access roots read.

ALTER TABLE "{Schema}"."{Grants}" ADD COLUMN IF NOT EXISTS "ResourceSubjectHash" bigint
    GENERATED ALWAYS AS (hashtextextended("ResourceId", hashtextextended("SubjectId", 0))) STORED;

CREATE INDEX IF NOT EXISTS "IX_{Grants}_ResourceSubjectHash"
    ON "{Schema}"."{Grants}"("ResourceSubjectHash")
    INCLUDE ("Id", "ResourceId", "SubjectId", "RoleId", "EffectiveFrom", "EffectiveTo");

DROP INDEX IF EXISTS "{Schema}"."IX_{Grants}_ResourceId_SubjectId";

DROP INDEX IF EXISTS "{Schema}"."IX_{Grants}_SubjectId";

CREATE INDEX IF NOT EXISTS "IX_{Grants}_SubjectId"
    ON "{Schema}"."{Grants}"("SubjectId")
    INCLUDE ("ResourceId", "RoleId", "EffectiveFrom", "EffectiveTo");

UPDATE "{Schema}"."SqlOSFgaSchema" SET "Version" = 16 WHERE "Version" < 16;

-- PostgreSQL translation of the matching SQL Server script.
-- SqlOSFga Schema v16: find one subject's grants on one resource with one seek.
--
-- Keyed on the resource, the point check reads every grant on each ancestor, whoever holds it. ResourceSeq is
-- the grant's copy of its resource's Seq (schema v11), which never changes; the point check reads the
-- ancestors' Seqs from the lineage and seeks (ResourceSeq, SubjectId), the caller's grants only. A trigger
-- (SqlOSFgaFunctionInitializer) fills the column when a grant is written; the lineage rebuild refreshes it.

ALTER TABLE "{Schema}"."{Grants}" ADD COLUMN IF NOT EXISTS "ResourceSeq" bigint NULL;

UPDATE "{Schema}"."{Grants}" g SET "ResourceSeq" = r."Seq"
FROM "{Schema}"."{Resources}" r
WHERE r."Id" = g."ResourceId" AND g."ResourceSeq" IS DISTINCT FROM r."Seq";

CREATE INDEX IF NOT EXISTS "IX_{Grants}_ResourceSeq_SubjectId"
    ON "{Schema}"."{Grants}"("ResourceSeq", "SubjectId")
    INCLUDE ("Id", "ResourceId", "RoleId", "EffectiveFrom", "EffectiveTo");

DROP INDEX IF EXISTS "{Schema}"."IX_{Grants}_ResourceId_SubjectId";

UPDATE "{Schema}"."SqlOSFgaSchema" SET "Version" = 16 WHERE "Version" < 16;

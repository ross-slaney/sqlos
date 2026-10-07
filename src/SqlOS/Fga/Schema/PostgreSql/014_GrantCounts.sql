-- PostgreSQL translation of the matching SQL Server script.
-- SqlOSFga Schema v14: the grant counts behind authorized pages (see the SQL Server script).

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSFgaGrantCounts" (
    "SubjectId" varchar(450) NOT NULL,
    "ResourceSeq" bigint NOT NULL,
    "ParentSeq" bigint NULL,
    "Grants" integer NOT NULL DEFAULT 0,
    "GrantChildren" integer NOT NULL DEFAULT 0,
    "CutGrants" integer NOT NULL DEFAULT 0,
    CONSTRAINT "PK_SqlOSFgaGrantCounts" PRIMARY KEY ("SubjectId", "ResourceSeq")
);

CREATE INDEX IF NOT EXISTS "IX_SqlOSFgaGrantCounts_Parent" ON "{Schema}"."SqlOSFgaGrantCounts" ("SubjectId", "ParentSeq", "ResourceSeq") INCLUDE ("Grants", "GrantChildren", "CutGrants");

UPDATE "{Schema}"."SqlOSFgaSchema" SET "Version" = 14 WHERE "Version" < 14;

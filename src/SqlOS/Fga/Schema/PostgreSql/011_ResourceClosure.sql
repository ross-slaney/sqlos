-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
-- SqlOSFga Schema v11: resource sequence numbers and the ancestor closure.
--
-- Resources.Seq numbers resources in creation order. It is the cursor key for authorized pages and the
-- compact key of the closure. ResourceTypes.Seq keys the closure by the descendant's type, so a page of
-- one entity type under one ancestor is a single index range.
--
-- The closure holds one row per (proper ancestor, descendant) pair whose path is fully active. The triggers
-- that maintain it, and the function that rebuilds it, are created by SqlOSFgaFunctionInitializer, which
-- also fills it once after this migration.

-- 1. ResourceTypes.Seq
ALTER TABLE "{Schema}"."{ResourceTypes}" ADD COLUMN IF NOT EXISTS "Seq" integer NULL;

CREATE SEQUENCE IF NOT EXISTS "{Schema}"."{ResourceTypes}_Seq" AS integer START WITH 1 INCREMENT BY 1;

UPDATE "{Schema}"."{ResourceTypes}" AS t
SET "Seq" = numbered.rn + COALESCE((SELECT MAX("Seq") FROM "{Schema}"."{ResourceTypes}"), 0)
FROM (
    SELECT "Id", ROW_NUMBER() OVER (ORDER BY "Id") AS rn
    FROM "{Schema}"."{ResourceTypes}"
    WHERE "Seq" IS NULL
) AS numbered
WHERE t."Id" = numbered."Id";

SELECT setval('"{Schema}"."{ResourceTypes}_Seq"', COALESCE((SELECT MAX("Seq") FROM "{Schema}"."{ResourceTypes}"), 0) + 1, false);

ALTER TABLE "{Schema}"."{ResourceTypes}" ALTER COLUMN "Seq" SET NOT NULL;
ALTER TABLE "{Schema}"."{ResourceTypes}" ALTER COLUMN "Seq" SET DEFAULT nextval('"{Schema}"."{ResourceTypes}_Seq"');

CREATE UNIQUE INDEX IF NOT EXISTS "UX_{ResourceTypes}_Seq" ON "{Schema}"."{ResourceTypes}"("Seq");

-- 2. Resources.Seq
ALTER TABLE "{Schema}"."{Resources}" ADD COLUMN IF NOT EXISTS "Seq" bigint NULL;

CREATE SEQUENCE IF NOT EXISTS "{Schema}"."{Resources}_Seq" AS bigint START WITH 1 INCREMENT BY 1 CACHE 1000;

UPDATE "{Schema}"."{Resources}" AS r
SET "Seq" = numbered.rn + COALESCE((SELECT MAX("Seq") FROM "{Schema}"."{Resources}"), 0)
FROM (
    SELECT "Id", ROW_NUMBER() OVER (ORDER BY "CreatedAt", "Id") AS rn
    FROM "{Schema}"."{Resources}"
    WHERE "Seq" IS NULL
) AS numbered
WHERE r."Id" = numbered."Id";

SELECT setval('"{Schema}"."{Resources}_Seq"', COALESCE((SELECT MAX("Seq") FROM "{Schema}"."{Resources}"), 0) + 1, false);

ALTER TABLE "{Schema}"."{Resources}" ALTER COLUMN "Seq" SET NOT NULL;
ALTER TABLE "{Schema}"."{Resources}" ALTER COLUMN "Seq" SET DEFAULT nextval('"{Schema}"."{Resources}_Seq"');

CREATE UNIQUE INDEX IF NOT EXISTS "UX_{Resources}_Seq" ON "{Schema}"."{Resources}"("Seq");

-- 3. The closure
CREATE TABLE IF NOT EXISTS "{Schema}"."{Resources}Closure" (
    "AncestorSeq"   bigint  NOT NULL,
    "TypeSeq"       integer NOT NULL,
    "DescendantSeq" bigint  NOT NULL,
    CONSTRAINT "PK_{Resources}Closure" PRIMARY KEY ("AncestorSeq", "TypeSeq", "DescendantSeq")
);

-- 4. The hash of the enforcement routines (functions, closure functions, triggers) last applied by
--    SqlOSFgaFunctionInitializer, so unchanged definitions are not re-created on every startup.
ALTER TABLE "{Schema}"."SqlOSFgaSchema" ADD COLUMN IF NOT EXISTS "RoutinesHash" varchar(64) NULL;

UPDATE "{Schema}"."SqlOSFgaSchema" SET "Version" = 11 WHERE "Version" < 11;

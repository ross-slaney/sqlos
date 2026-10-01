-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
-- SqlOSFga Schema v11: resource sequence numbers and the resource lineage.
--
-- Resources.Seq numbers resources in creation order; it is the compact key the lineage columns hold.
-- ResourceTypes.Seq is the compact key of a resource's type, copied to application tables that carry
-- scope columns.
--
-- The lineage of a resource is its ancestor at every level of the tree (Ancestor0 ... AncestorD, one
-- column per level: the default depth's here, deeper ones added by SqlOSFgaFunctionInitializer when
-- MaxResourceHierarchyDepth exceeds 10), its Depth, and its Reach: the highest level from which access flows down
-- to it without crossing an inactive resource. An inactive resource, or one in a cycle or deeper than the
-- limit, has no Reach and no Depth, which denies it exactly as fn_IsResourceAccessible always has.
-- Triggers created by SqlOSFgaFunctionInitializer keep the columns exact; it also fills them once after
-- this migration.

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

-- 3. Depth and Reach.
ALTER TABLE "{Schema}"."{Resources}" ADD COLUMN IF NOT EXISTS "Depth" smallint NULL;
ALTER TABLE "{Schema}"."{Resources}" ADD COLUMN IF NOT EXISTS "Reach" smallint NULL;

-- 4. The ancestor columns of the default depth (levels 0..10), each with a filtered index. A larger
--    configured depth adds the columns above level 10 through SqlOSFgaFunctionInitializer.
ALTER TABLE "{Schema}"."{Resources}" ADD COLUMN IF NOT EXISTS "Ancestor0" bigint NULL;
CREATE INDEX IF NOT EXISTS "IX_{Resources}_Ancestor0" ON "{Schema}"."{Resources}" ("Ancestor0") INCLUDE ("Id", "Reach") WHERE "Ancestor0" IS NOT NULL;
ALTER TABLE "{Schema}"."{Resources}" ADD COLUMN IF NOT EXISTS "Ancestor1" bigint NULL;
CREATE INDEX IF NOT EXISTS "IX_{Resources}_Ancestor1" ON "{Schema}"."{Resources}" ("Ancestor1") INCLUDE ("Id", "Reach") WHERE "Ancestor1" IS NOT NULL;
ALTER TABLE "{Schema}"."{Resources}" ADD COLUMN IF NOT EXISTS "Ancestor2" bigint NULL;
CREATE INDEX IF NOT EXISTS "IX_{Resources}_Ancestor2" ON "{Schema}"."{Resources}" ("Ancestor2") INCLUDE ("Id", "Reach") WHERE "Ancestor2" IS NOT NULL;
ALTER TABLE "{Schema}"."{Resources}" ADD COLUMN IF NOT EXISTS "Ancestor3" bigint NULL;
CREATE INDEX IF NOT EXISTS "IX_{Resources}_Ancestor3" ON "{Schema}"."{Resources}" ("Ancestor3") INCLUDE ("Id", "Reach") WHERE "Ancestor3" IS NOT NULL;
ALTER TABLE "{Schema}"."{Resources}" ADD COLUMN IF NOT EXISTS "Ancestor4" bigint NULL;
CREATE INDEX IF NOT EXISTS "IX_{Resources}_Ancestor4" ON "{Schema}"."{Resources}" ("Ancestor4") INCLUDE ("Id", "Reach") WHERE "Ancestor4" IS NOT NULL;
ALTER TABLE "{Schema}"."{Resources}" ADD COLUMN IF NOT EXISTS "Ancestor5" bigint NULL;
CREATE INDEX IF NOT EXISTS "IX_{Resources}_Ancestor5" ON "{Schema}"."{Resources}" ("Ancestor5") INCLUDE ("Id", "Reach") WHERE "Ancestor5" IS NOT NULL;
ALTER TABLE "{Schema}"."{Resources}" ADD COLUMN IF NOT EXISTS "Ancestor6" bigint NULL;
CREATE INDEX IF NOT EXISTS "IX_{Resources}_Ancestor6" ON "{Schema}"."{Resources}" ("Ancestor6") INCLUDE ("Id", "Reach") WHERE "Ancestor6" IS NOT NULL;
ALTER TABLE "{Schema}"."{Resources}" ADD COLUMN IF NOT EXISTS "Ancestor7" bigint NULL;
CREATE INDEX IF NOT EXISTS "IX_{Resources}_Ancestor7" ON "{Schema}"."{Resources}" ("Ancestor7") INCLUDE ("Id", "Reach") WHERE "Ancestor7" IS NOT NULL;
ALTER TABLE "{Schema}"."{Resources}" ADD COLUMN IF NOT EXISTS "Ancestor8" bigint NULL;
CREATE INDEX IF NOT EXISTS "IX_{Resources}_Ancestor8" ON "{Schema}"."{Resources}" ("Ancestor8") INCLUDE ("Id", "Reach") WHERE "Ancestor8" IS NOT NULL;
ALTER TABLE "{Schema}"."{Resources}" ADD COLUMN IF NOT EXISTS "Ancestor9" bigint NULL;
CREATE INDEX IF NOT EXISTS "IX_{Resources}_Ancestor9" ON "{Schema}"."{Resources}" ("Ancestor9") INCLUDE ("Id", "Reach") WHERE "Ancestor9" IS NOT NULL;
ALTER TABLE "{Schema}"."{Resources}" ADD COLUMN IF NOT EXISTS "Ancestor10" bigint NULL;
CREATE INDEX IF NOT EXISTS "IX_{Resources}_Ancestor10" ON "{Schema}"."{Resources}" ("Ancestor10") INCLUDE ("Id", "Reach") WHERE "Ancestor10" IS NOT NULL;

-- 5. The hash of the enforcement routines (functions, lineage functions, triggers) last applied by
--    SqlOSFgaFunctionInitializer, so unchanged definitions are not re-created on every startup.
ALTER TABLE "{Schema}"."SqlOSFgaSchema" ADD COLUMN IF NOT EXISTS "RoutinesHash" varchar(64) NULL;

UPDATE "{Schema}"."SqlOSFgaSchema" SET "Version" = 11 WHERE "Version" < 11;

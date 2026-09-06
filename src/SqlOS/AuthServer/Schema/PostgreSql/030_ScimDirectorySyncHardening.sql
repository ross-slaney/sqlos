-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimExternalIds" ADD COLUMN IF NOT EXISTS "UserName" varchar(450) NULL;
ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimExternalIds" ADD COLUMN IF NOT EXISTS "PrimaryEmail" varchar(320) NULL;
ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimExternalIds" ADD COLUMN IF NOT EXISTS "GivenName" varchar(150) NULL;
ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimExternalIds" ADD COLUMN IF NOT EXISTS "FormattedName" varchar(300) NULL;
ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimExternalIds" ADD COLUMN IF NOT EXISTS "FamilyName" varchar(150) NULL;
ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimExternalIds" ADD COLUMN IF NOT EXISTS "DeletedAt" timestamp NULL;
ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimExternalIds"
    ADD COLUMN IF NOT EXISTS "OwnsUserLifecycle" boolean NOT NULL
        CONSTRAINT "DF_SqlOSScimExternalIds_OwnsUserLifecycle" DEFAULT FALSE;

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSScimOperationCommits" (
        "Id" varchar(64) NOT NULL CONSTRAINT "PK_SqlOSScimOperationCommits" PRIMARY KEY,
        "OccurredAt" timestamp NOT NULL
    );
DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimOperationCommits')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimOperationCommits' AND column_name = 'OccurredAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSScimOperationCommits_OccurredAt"
    ON "{Schema}"."SqlOSScimOperationCommits"("OccurredAt");
  END IF;
END
$sqlos_guard$;

DROP INDEX IF EXISTS "{Schema}"."IX_SqlOSScimExternalIds_Connection_Resource_External";

ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimExternalIds" ALTER COLUMN "ExternalId" TYPE varchar(450) COLLATE "C";
ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimExternalIds" ALTER COLUMN "ExternalId" DROP NOT NULL;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimExternalIds')) IS NOT NULL AND to_regclass(format('%I.%I', '{Schema}', 'SqlOSUsers')) IS NOT NULL THEN
    UPDATE "{Schema}"."SqlOSScimExternalIds" AS externalIds
SET
    "UserName" = COALESCE(
        externalIds."UserName",
        NULLIF(btrim(users."DefaultEmail"), ''),
        NULLIF(btrim(externalIds."ExternalId"), '')),
    "PrimaryEmail" = COALESCE(externalIds."PrimaryEmail", NULLIF(btrim(users."DefaultEmail"), '')),
    "DisplayName" = COALESCE(externalIds."DisplayName", users."DisplayName"),
    "FormattedName" = COALESCE(externalIds."FormattedName", externalIds."DisplayName", users."DisplayName")
FROM "{Schema}"."SqlOSUsers" AS users
WHERE users."Id" = externalIds."EntityId"
  AND externalIds."ResourceType" = 'User'
  AND (externalIds."UserName" IS NULL
    OR externalIds."PrimaryEmail" IS NULL
    OR externalIds."DisplayName" IS NULL
    OR externalIds."FormattedName" IS NULL);
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimExternalIds')) IS NOT NULL THEN
    DELETE FROM "{Schema}"."SqlOSScimExternalIds"
WHERE "Id" IN (
    SELECT "Id" FROM (
        SELECT "Id", ROW_NUMBER() OVER (
            PARTITION BY "ConnectionId", "ResourceType", "EntityId"
            ORDER BY "UpdatedAt" DESC, "CreatedAt" DESC, "Id" DESC) AS "rowNumber"
        FROM "{Schema}"."SqlOSScimExternalIds"
    ) ranked
    WHERE "rowNumber" > 1
);
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimExternalIds')) IS NOT NULL THEN
    DELETE FROM "{Schema}"."SqlOSScimExternalIds"
WHERE "Id" IN (
    SELECT "Id" FROM (
        SELECT "Id", ROW_NUMBER() OVER (
            PARTITION BY "ConnectionId", "ResourceType", "ExternalId" COLLATE "C"
            ORDER BY "UpdatedAt" DESC, "CreatedAt" DESC, "Id" DESC) AS "rowNumber"
        FROM "{Schema}"."SqlOSScimExternalIds"
        WHERE "ExternalId" IS NOT NULL
    ) ranked
    WHERE "rowNumber" > 1
);
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimExternalIds')) IS NOT NULL THEN
    UPDATE "{Schema}"."SqlOSScimExternalIds" AS externalIds
SET "UserName" = CONCAT('sqlos-migrated-', externalIds."Id")
FROM (
    SELECT "Id", ROW_NUMBER() OVER (
        PARTITION BY "ConnectionId", "ResourceType", "UserName"
        ORDER BY "UpdatedAt" DESC, "CreatedAt" DESC, "Id" DESC) AS "rowNumber"
    FROM "{Schema}"."SqlOSScimExternalIds"
    WHERE "UserName" IS NOT NULL
) duplicates
WHERE duplicates."Id" = externalIds."Id"
  AND duplicates."rowNumber" > 1;
  END IF;
END
$sqlos_guard$;

DROP INDEX IF EXISTS "{Schema}"."IX_SqlOSScimExternalIds_Connection_Resource_External";
ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimExternalIds" ALTER COLUMN "ExternalId" TYPE varchar(450) COLLATE "C";
ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimExternalIds" ALTER COLUMN "ExternalId" DROP NOT NULL;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimExternalIds')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimExternalIds' AND column_name = 'ConnectionId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimExternalIds' AND column_name = 'ResourceType') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimExternalIds' AND column_name = 'ExternalId') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSScimExternalIds_Connection_Resource_External"
    ON "{Schema}"."SqlOSScimExternalIds"("ConnectionId", "ResourceType", "ExternalId")
    WHERE "ExternalId" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

DROP INDEX IF EXISTS "{Schema}"."IX_SqlOSScimExternalIds_Connection_Resource_Entity";
DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimExternalIds')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimExternalIds' AND column_name = 'ConnectionId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimExternalIds' AND column_name = 'ResourceType') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimExternalIds' AND column_name = 'EntityId') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSScimExternalIds_Connection_Resource_Entity"
    ON "{Schema}"."SqlOSScimExternalIds"("ConnectionId", "ResourceType", "EntityId");
  END IF;
END
$sqlos_guard$;

DROP INDEX IF EXISTS "{Schema}"."IX_SqlOSScimExternalIds_Connection_Resource_UserName";
ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimExternalIds" ALTER COLUMN "UserName" TYPE varchar(450);
ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimExternalIds" ALTER COLUMN "UserName" DROP NOT NULL;
ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimExternalIds" ALTER COLUMN "PrimaryEmail" TYPE varchar(320);
ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimExternalIds" ALTER COLUMN "PrimaryEmail" DROP NOT NULL;
ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimExternalIds" ALTER COLUMN "DisplayName" TYPE varchar(300);
ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimExternalIds" ALTER COLUMN "DisplayName" DROP NOT NULL;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimExternalIds')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimExternalIds' AND column_name = 'ConnectionId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimExternalIds' AND column_name = 'ResourceType') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimExternalIds' AND column_name = 'UserName') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSScimExternalIds_Connection_Resource_UserName"
    ON "{Schema}"."SqlOSScimExternalIds"("ConnectionId", "ResourceType", "UserName")
    WHERE "UserName" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimConnections')) IS NOT NULL THEN
    UPDATE "{Schema}"."SqlOSScimConnections" AS connections
SET
    "IsEnabled" = FALSE,
    "UpdatedAt" = CURRENT_TIMESTAMP
FROM (
    SELECT "Id", ROW_NUMBER() OVER (
        PARTITION BY "OrganizationId"
        ORDER BY "UpdatedAt" DESC, "CreatedAt" DESC, "Id" DESC) AS "rowNumber"
    FROM "{Schema}"."SqlOSScimConnections"
    WHERE "IsEnabled" = TRUE
) ranked
WHERE ranked."Id" = connections."Id"
  AND ranked."rowNumber" > 1;
  END IF;
END
$sqlos_guard$;

DROP INDEX IF EXISTS "{Schema}"."UX_SqlOSScimConnections_OneEnabledPerOrganization";
DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimConnections')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimConnections' AND column_name = 'OrganizationId') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "UX_SqlOSScimConnections_OneEnabledPerOrganization"
    ON "{Schema}"."SqlOSScimConnections"("OrganizationId")
    WHERE "IsEnabled" = TRUE;
  END IF;
END
$sqlos_guard$;

UPDATE "{Schema}"."SqlOSSchema" SET "Version" = 30;
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") SELECT 30 WHERE NOT EXISTS (SELECT 1 FROM "{Schema}"."SqlOSSchema");

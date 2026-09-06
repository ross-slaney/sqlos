-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSScimConnections" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "OrganizationId" varchar(64) NOT NULL,
        "SeedKey" varchar(160) NULL,
        "DisplayName" varchar(200) NOT NULL,
        "IsEnabled" boolean NOT NULL,
        "TokenHash" varchar(128) NULL,
        "TokenPrefix" varchar(24) NULL,
        "TokenRotatedAt" timestamp NULL,
        "TokenLastUsedAt" timestamp NULL,
        "LastSyncAt" timestamp NULL,
        "Source" varchar(40) NOT NULL,
        "CreatedAt" timestamp NOT NULL,
        "UpdatedAt" timestamp NOT NULL
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimConnections')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimConnections' AND column_name = 'OrganizationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimConnections' AND column_name = 'SeedKey') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSScimConnections_OrganizationId_SeedKey"
        ON "{Schema}"."SqlOSScimConnections"("OrganizationId", "SeedKey")
        WHERE "SeedKey" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimConnections')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimConnections' AND column_name = 'TokenHash') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSScimConnections_TokenHash"
        ON "{Schema}"."SqlOSScimConnections"("TokenHash")
        WHERE "TokenHash" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimConnections')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimConnections' AND column_name = 'OrganizationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimConnections' AND column_name = 'IsEnabled') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSScimConnections_OrganizationId_IsEnabled"
        ON "{Schema}"."SqlOSScimConnections"("OrganizationId", "IsEnabled");
  END IF;
END
$sqlos_guard$;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimConnections"
        ADD CONSTRAINT "FK_SqlOSScimConnections_Organizations_OrganizationId"
            FOREIGN KEY ("OrganizationId") REFERENCES "{Schema}"."SqlOSOrganizations"("Id");

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSScimExternalIds" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "ConnectionId" varchar(64) NOT NULL,
        "ResourceType" varchar(20) NOT NULL,
        "ExternalId" varchar(450) NOT NULL,
        "EntityId" varchar(128) NOT NULL,
        "FgaSubjectId" varchar(128) NULL,
        "DisplayName" varchar(300) NULL,
        "IsActive" boolean NOT NULL,
        "CreatedAt" timestamp NOT NULL,
        "UpdatedAt" timestamp NOT NULL,
        "LastSyncedAt" timestamp NOT NULL
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimExternalIds')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimExternalIds' AND column_name = 'ConnectionId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimExternalIds' AND column_name = 'ResourceType') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimExternalIds' AND column_name = 'ExternalId') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSScimExternalIds_Connection_Resource_External"
        ON "{Schema}"."SqlOSScimExternalIds"("ConnectionId", "ResourceType", "ExternalId");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimExternalIds')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimExternalIds' AND column_name = 'ConnectionId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimExternalIds' AND column_name = 'ResourceType') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimExternalIds' AND column_name = 'EntityId') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSScimExternalIds_Connection_Resource_Entity"
        ON "{Schema}"."SqlOSScimExternalIds"("ConnectionId", "ResourceType", "EntityId");
  END IF;
END
$sqlos_guard$;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimExternalIds"
        ADD CONSTRAINT "FK_SqlOSScimExternalIds_Connections_ConnectionId"
            FOREIGN KEY ("ConnectionId") REFERENCES "{Schema}"."SqlOSScimConnections"("Id");

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSScimGroupMappings" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "ConnectionId" varchar(64) NOT NULL,
        "SourceKey" varchar(300) NULL,
        "Source" varchar(40) NOT NULL,
        "MatchType" varchar(40) NOT NULL,
        "GroupDisplayName" varchar(300) NULL,
        "GroupExternalId" varchar(450) NULL,
        "GroupPattern" varchar(500) NULL,
        "RoleKey" varchar(120) NOT NULL,
        "ResourceId" varchar(256) NULL,
        "ResourceIdTemplate" varchar(500) NULL,
        "Description" varchar(500) NULL,
        "IsEnabled" boolean NOT NULL,
        "CreatedAt" timestamp NOT NULL,
        "UpdatedAt" timestamp NOT NULL
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimGroupMappings')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimGroupMappings' AND column_name = 'ConnectionId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimGroupMappings' AND column_name = 'SourceKey') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSScimGroupMappings_ConnectionId_SourceKey"
        ON "{Schema}"."SqlOSScimGroupMappings"("ConnectionId", "SourceKey")
        WHERE "SourceKey" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimGroupMappings')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimGroupMappings' AND column_name = 'ConnectionId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimGroupMappings' AND column_name = 'IsEnabled') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSScimGroupMappings_ConnectionId_IsEnabled"
        ON "{Schema}"."SqlOSScimGroupMappings"("ConnectionId", "IsEnabled");
  END IF;
END
$sqlos_guard$;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimGroupMappings"
        ADD CONSTRAINT "FK_SqlOSScimGroupMappings_Connections_ConnectionId"
            FOREIGN KEY ("ConnectionId") REFERENCES "{Schema}"."SqlOSScimConnections"("Id");

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSScimManagedGrants" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "ConnectionId" varchar(64) NOT NULL,
        "MappingId" varchar(64) NOT NULL,
        "GroupExternalId" varchar(450) NOT NULL,
        "FgaGroupId" varchar(128) NOT NULL,
        "FgaGroupSubjectId" varchar(128) NOT NULL,
        "GrantId" varchar(128) NOT NULL,
        "RoleId" varchar(128) NOT NULL,
        "ResourceId" varchar(256) NOT NULL,
        "CreatedAt" timestamp NOT NULL,
        "RevokedAt" timestamp NULL
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimManagedGrants')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimManagedGrants' AND column_name = 'ConnectionId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimManagedGrants' AND column_name = 'MappingId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimManagedGrants' AND column_name = 'GroupExternalId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimManagedGrants' AND column_name = 'ResourceId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimManagedGrants' AND column_name = 'RoleId') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSScimManagedGrants_Reconcile"
        ON "{Schema}"."SqlOSScimManagedGrants"("ConnectionId", "MappingId", "GroupExternalId", "ResourceId", "RoleId");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimManagedGrants')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimManagedGrants' AND column_name = 'GrantId') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSScimManagedGrants_GrantId"
        ON "{Schema}"."SqlOSScimManagedGrants"("GrantId");
  END IF;
END
$sqlos_guard$;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimManagedGrants"
        ADD CONSTRAINT "FK_SqlOSScimManagedGrants_Connections_ConnectionId"
            FOREIGN KEY ("ConnectionId") REFERENCES "{Schema}"."SqlOSScimConnections"("Id");

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimManagedGrants"
        ADD CONSTRAINT "FK_SqlOSScimManagedGrants_Mappings_MappingId"
            FOREIGN KEY ("MappingId") REFERENCES "{Schema}"."SqlOSScimGroupMappings"("Id");

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSScimSyncEvents" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "ConnectionId" varchar(64) NOT NULL,
        "OrganizationId" varchar(64) NOT NULL,
        "ResourceType" varchar(20) NOT NULL,
        "ResourceId" varchar(128) NULL,
        "ExternalId" varchar(450) NULL,
        "Action" varchar(80) NOT NULL,
        "Result" varchar(40) NOT NULL,
        "Error" varchar(1000) NULL,
        "DataJson" text NULL,
        "RequestId" varchar(128) NULL,
        "OccurredAt" timestamp NOT NULL
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimSyncEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimSyncEvents' AND column_name = 'ConnectionId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimSyncEvents' AND column_name = 'OccurredAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSScimSyncEvents_ConnectionId_OccurredAt"
        ON "{Schema}"."SqlOSScimSyncEvents"("ConnectionId", "OccurredAt" DESC);
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScimSyncEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimSyncEvents' AND column_name = 'OrganizationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScimSyncEvents' AND column_name = 'OccurredAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSScimSyncEvents_OrganizationId_OccurredAt"
        ON "{Schema}"."SqlOSScimSyncEvents"("OrganizationId", "OccurredAt" DESC);
  END IF;
END
$sqlos_guard$;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimSyncEvents"
        ADD CONSTRAINT "FK_SqlOSScimSyncEvents_Connections_ConnectionId"
            FOREIGN KEY ("ConnectionId") REFERENCES "{Schema}"."SqlOSScimConnections"("Id");

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSScimSyncEvents"
        ADD CONSTRAINT "FK_SqlOSScimSyncEvents_Organizations_OrganizationId"
            FOREIGN KEY ("OrganizationId") REFERENCES "{Schema}"."SqlOSOrganizations"("Id");

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (29);

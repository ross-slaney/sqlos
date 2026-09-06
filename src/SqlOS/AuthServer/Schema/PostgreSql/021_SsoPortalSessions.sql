-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSSsoPortalSessions" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "OrganizationId" varchar(64) NOT NULL,
        "ConnectionId" varchar(64) NULL,
        "LinkTokenHash" varchar(128) NOT NULL,
        "SessionTokenHash" varchar(128) NULL,
        "Provider" varchar(40) NULL,
        "ReturnUrl" varchar(1000) NULL,
        "ActorType" varchar(80) NOT NULL,
        "CreatedByUserId" varchar(64) NULL,
        "CreatedAt" timestamp NOT NULL,
        "ExpiresAt" timestamp NOT NULL,
        "OpenedAt" timestamp NULL,
        "LastSeenAt" timestamp NULL,
        "RevokedAt" timestamp NULL,
        "RevokedReason" varchar(160) NULL,
        "IpAddress" varchar(128) NULL,
        "UserAgent" varchar(512) NULL,
        "LastTestedAt" timestamp NULL,
        "LastTestStatus" varchar(40) NULL,
        "LastTestMessage" varchar(500) NULL
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSSsoPortalSessions')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSsoPortalSessions' AND column_name = 'LinkTokenHash') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSSsoPortalSessions_LinkTokenHash"
        ON "{Schema}"."SqlOSSsoPortalSessions"("LinkTokenHash");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSSsoPortalSessions')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSsoPortalSessions' AND column_name = 'SessionTokenHash') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSSsoPortalSessions_SessionTokenHash"
        ON "{Schema}"."SqlOSSsoPortalSessions"("SessionTokenHash")
        WHERE "SessionTokenHash" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSSsoPortalSessions')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSsoPortalSessions' AND column_name = 'OrganizationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSsoPortalSessions' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSSsoPortalSessions_OrganizationId_CreatedAt"
        ON "{Schema}"."SqlOSSsoPortalSessions"("OrganizationId", "CreatedAt");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSSsoPortalSessions')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSsoPortalSessions' AND column_name = 'OrganizationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSsoPortalSessions' AND column_name = 'RevokedAt') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSsoPortalSessions' AND column_name = 'ExpiresAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSSsoPortalSessions_OrganizationId_RevokedAt_ExpiresAt"
        ON "{Schema}"."SqlOSSsoPortalSessions"("OrganizationId", "RevokedAt", "ExpiresAt");
  END IF;
END
$sqlos_guard$;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSSsoPortalSessions"
        ADD CONSTRAINT "FK_SqlOSSsoPortalSessions_Organizations_OrganizationId"
            FOREIGN KEY ("OrganizationId") REFERENCES "{Schema}"."SqlOSOrganizations"("Id");

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSSsoPortalSessions"
        ADD CONSTRAINT "FK_SqlOSSsoPortalSessions_SsoConnections_ConnectionId"
            FOREIGN KEY ("ConnectionId") REFERENCES "{Schema}"."SqlOSSsoConnections"("Id");

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (21);

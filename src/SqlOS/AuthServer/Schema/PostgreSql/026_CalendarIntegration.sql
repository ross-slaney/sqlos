-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSUsers')) IS NOT NULL AND to_regclass(format('%I.%I', '{Schema}', 'SqlOSOrganizations')) IS NOT NULL AND to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuthOidcConnections')) IS NOT NULL THEN
    CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSCalendarConnections" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "ProviderType" varchar(40) NOT NULL,
        "Mode" varchar(40) NOT NULL,
        "Status" varchar(40) NOT NULL,
        "OidcConnectionId" varchar(64) NOT NULL,
        "UserId" varchar(64) NULL,
        "OrganizationId" varchar(64) NULL,
        "DisplayName" varchar(200) NOT NULL,
        "ProviderAccountEmail" varchar(320) NULL,
        "ProviderAccountSubject" varchar(256) NULL,
        "ScopesJson" text NOT NULL,
        "AccessTokenEncrypted" text NULL,
        "RefreshTokenEncrypted" text NULL,
        "AccessTokenExpiresAt" timestamp NULL,
        "LastSyncAt" timestamp NULL,
        "LastError" varchar(1000) NULL,
        "LastErrorAt" timestamp NULL,
        "CreatedAt" timestamp NOT NULL,
        "UpdatedAt" timestamp NOT NULL,
        "RevokedAt" timestamp NULL,
        "RevokedReason" varchar(160) NULL,
        CONSTRAINT "FK_SqlOSCalendarConnections_Users"
            FOREIGN KEY ("UserId") REFERENCES "{Schema}"."SqlOSUsers"("Id"),
        CONSTRAINT "FK_SqlOSCalendarConnections_Organizations"
            FOREIGN KEY ("OrganizationId") REFERENCES "{Schema}"."SqlOSOrganizations"("Id"),
        CONSTRAINT "FK_SqlOSCalendarConnections_OidcConnections"
            FOREIGN KEY ("OidcConnectionId") REFERENCES "{Schema}"."SqlOSAuthOidcConnections"("Id")
    );
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSCalendarConnections')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSCalendarConnections' AND column_name = 'UserId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSCalendarConnections' AND column_name = 'RevokedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSCalendarConnections_UserId_RevokedAt"
        ON "{Schema}"."SqlOSCalendarConnections" ("UserId", "RevokedAt");
  END IF;
END
$sqlos_guard$;
    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSCalendarConnections')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSCalendarConnections' AND column_name = 'OrganizationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSCalendarConnections' AND column_name = 'RevokedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSCalendarConnections_OrganizationId_RevokedAt"
        ON "{Schema}"."SqlOSCalendarConnections" ("OrganizationId", "RevokedAt");
  END IF;
END
$sqlos_guard$;
    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSCalendarConnections')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSCalendarConnections' AND column_name = 'Mode') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSCalendarConnections' AND column_name = 'Status') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSCalendarConnections_Mode_Status"
        ON "{Schema}"."SqlOSCalendarConnections" ("Mode", "Status");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSCalendarConnections')) IS NOT NULL THEN
    CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSCalendarSyncStates" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "CalendarConnectionId" varchar(64) NOT NULL,
        "ProviderCalendarId" varchar(256) NOT NULL,
        "DisplayName" varchar(200) NULL,
        "IsSyncEnabled" boolean NOT NULL,
        "SyncCursor" text NULL,
        "LastSyncStartedAt" timestamp NULL,
        "LastSyncCompletedAt" timestamp NULL,
        "LastSyncStatus" varchar(40) NULL,
        "LastSyncError" varchar(1000) NULL,
        "EventCount" INT NOT NULL CONSTRAINT "DF_SqlOSCalendarSyncStates_EventCount" DEFAULT 0,
        "CreatedAt" timestamp NOT NULL,
        "UpdatedAt" timestamp NOT NULL,
        CONSTRAINT "FK_SqlOSCalendarSyncStates_Connections"
            FOREIGN KEY ("CalendarConnectionId") REFERENCES "{Schema}"."SqlOSCalendarConnections"("Id") ON DELETE CASCADE
    );
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSCalendarSyncStates')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSCalendarSyncStates' AND column_name = 'CalendarConnectionId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSCalendarSyncStates' AND column_name = 'ProviderCalendarId') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSCalendarSyncStates_Connection_Calendar"
        ON "{Schema}"."SqlOSCalendarSyncStates" ("CalendarConnectionId", "ProviderCalendarId");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSCalendarConnections')) IS NOT NULL THEN
    CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSCalendarEvents" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "CalendarConnectionId" varchar(64) NOT NULL,
        "ProviderCalendarId" varchar(256) NOT NULL,
        "ProviderEventId" varchar(512) NOT NULL,
        "Subject" varchar(500) NULL,
        "StartsAtUtc" timestamp NOT NULL,
        "EndsAtUtc" timestamp NOT NULL,
        "IsAllDay" boolean NOT NULL,
        "ShowAs" varchar(20) NOT NULL,
        "Status" varchar(20) NOT NULL,
        "Location" varchar(500) NULL,
        "Origin" varchar(20) NOT NULL,
        "CreatedAt" timestamp NOT NULL,
        "UpdatedAt" timestamp NOT NULL,
        CONSTRAINT "FK_SqlOSCalendarEvents_Connections"
            FOREIGN KEY ("CalendarConnectionId") REFERENCES "{Schema}"."SqlOSCalendarConnections"("Id") ON DELETE CASCADE
    );
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSCalendarEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSCalendarEvents' AND column_name = 'CalendarConnectionId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSCalendarEvents' AND column_name = 'ProviderCalendarId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSCalendarEvents' AND column_name = 'ProviderEventId') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSCalendarEvents_ProviderEvent"
        ON "{Schema}"."SqlOSCalendarEvents" ("CalendarConnectionId", "ProviderCalendarId", "ProviderEventId");
  END IF;
END
$sqlos_guard$;
    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSCalendarEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSCalendarEvents' AND column_name = 'CalendarConnectionId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSCalendarEvents' AND column_name = 'StartsAtUtc') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSCalendarEvents_Connection_StartsAtUtc"
        ON "{Schema}"."SqlOSCalendarEvents" ("CalendarConnectionId", "StartsAtUtc");
  END IF;
END
$sqlos_guard$;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (26);

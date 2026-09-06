-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSInvitations" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "OrganizationId" varchar(64) NOT NULL,
        "InvitedEmail" varchar(320) NOT NULL,
        "NormalizedEmail" varchar(320) NOT NULL,
        "Role" varchar(50) NOT NULL,
        "TokenHash" varchar(128) NOT NULL,
        "InvitedByUserId" varchar(64) NULL,
        "ClientApplicationId" varchar(64) NULL,
        "RedirectUri" varchar(2048) NULL,
        "Scope" varchar(1000) NULL,
        "Resource" varchar(2048) NULL,
        "CustomFieldsJson" text NULL,
        "CreatedAt" timestamp NOT NULL,
        "ExpiresAt" timestamp NOT NULL,
        "LastSentAt" timestamp NULL,
        "LastSendError" varchar(500) NULL,
        "AcceptedAt" timestamp NULL,
        "AcceptedByUserId" varchar(64) NULL,
        "RevokedAt" timestamp NULL,
        "RevokedReason" varchar(120) NULL,
        "IpAddress" varchar(128) NULL,
        "UserAgent" varchar(512) NULL
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSInvitations')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSInvitations' AND column_name = 'TokenHash') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSInvitations_TokenHash"
        ON "{Schema}"."SqlOSInvitations"("TokenHash");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSInvitations')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSInvitations' AND column_name = 'OrganizationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSInvitations' AND column_name = 'NormalizedEmail') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSInvitations' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSInvitations_Organization_NormalizedEmail_CreatedAt"
        ON "{Schema}"."SqlOSInvitations"("OrganizationId", "NormalizedEmail", "CreatedAt");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSInvitations')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSInvitations' AND column_name = 'NormalizedEmail') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSInvitations' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSInvitations_NormalizedEmail_CreatedAt"
        ON "{Schema}"."SqlOSInvitations"("NormalizedEmail", "CreatedAt");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSInvitations')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSInvitations' AND column_name = 'IpAddress') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSInvitations' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSInvitations_IpAddress_CreatedAt"
        ON "{Schema}"."SqlOSInvitations"("IpAddress", "CreatedAt");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSInvitations')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSInvitations' AND column_name = 'InvitedByUserId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSInvitations' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSInvitations_InvitedByUserId_CreatedAt"
        ON "{Schema}"."SqlOSInvitations"("InvitedByUserId", "CreatedAt");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSInvitations')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSInvitations' AND column_name = 'ExpiresAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSInvitations_ExpiresAt"
        ON "{Schema}"."SqlOSInvitations"("ExpiresAt");
  END IF;
END
$sqlos_guard$;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSInvitations"
        ADD CONSTRAINT "FK_SqlOSInvitations_Organizations_OrganizationId"
            FOREIGN KEY ("OrganizationId") REFERENCES "{Schema}"."SqlOSOrganizations"("Id");

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSInvitations"
        ADD CONSTRAINT "FK_SqlOSInvitations_Users_InvitedByUserId"
            FOREIGN KEY ("InvitedByUserId") REFERENCES "{Schema}"."SqlOSUsers"("Id");

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSInvitations"
        ADD CONSTRAINT "FK_SqlOSInvitations_Users_AcceptedByUserId"
            FOREIGN KEY ("AcceptedByUserId") REFERENCES "{Schema}"."SqlOSUsers"("Id");

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSInvitations"
        ADD CONSTRAINT "FK_SqlOSInvitations_ClientApplications_ClientApplicationId"
            FOREIGN KEY ("ClientApplicationId") REFERENCES "{Schema}"."SqlOSClientApplications"("Id");

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests"
        ADD COLUMN IF NOT EXISTS "InvitationId" varchar(64) NULL;

    DO $sqlos$
BEGIN
  ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests" ADD CONSTRAINT "FK_SqlOSAuthorizationRequests_Invitations_InvitationId" FOREIGN KEY ("InvitationId") REFERENCES "{Schema}"."SqlOSInvitations"("Id");
EXCEPTION WHEN duplicate_object THEN NULL;
END
$sqlos$;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (13);

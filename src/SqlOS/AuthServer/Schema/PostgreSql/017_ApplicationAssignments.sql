-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "AccessMode" varchar(40) NOT NULL
        CONSTRAINT "DF_SqlOSClientApplications_AccessMode" DEFAULT 'all_organizations';

ALTER TABLE IF EXISTS "{Schema}"."SqlOSSessions"
    ADD COLUMN IF NOT EXISTS "OrganizationId" varchar(64) NULL;

DO $sqlos$
BEGIN
  ALTER TABLE IF EXISTS "{Schema}"."SqlOSSessions" ADD CONSTRAINT "FK_SqlOSSessions_Organizations" FOREIGN KEY ("OrganizationId") REFERENCES "{Schema}"."SqlOSOrganizations"("Id");
EXCEPTION WHEN duplicate_object THEN NULL;
END
$sqlos$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSClientApplications')) IS NOT NULL AND to_regclass(format('%I.%I', '{Schema}', 'SqlOSOrganizations')) IS NOT NULL THEN
    CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSApplicationAssignments"
    (
        "Id" varchar(64) NOT NULL,
        "ClientApplicationId" varchar(64) NOT NULL,
        "OrganizationId" varchar(64) NULL,
        "PrincipalType" varchar(40) NOT NULL,
        "PrincipalId" varchar(128) NULL,
        "RoleKey" varchar(80) NULL,
        "Access" varchar(20) NOT NULL,
        "Reason" varchar(500) NULL,
        "CreatedAt" timestamp NOT NULL,
        "CreatedByActorType" varchar(80) NULL,
        "CreatedByActorId" varchar(128) NULL,
        "RevokedAt" timestamp NULL,
        "RevokedByActorType" varchar(80) NULL,
        "RevokedByActorId" varchar(128) NULL,
        CONSTRAINT "PK_SqlOSApplicationAssignments" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_SqlOSApplicationAssignments_ClientApplication" FOREIGN KEY ("ClientApplicationId") REFERENCES "{Schema}"."SqlOSClientApplications"("Id"),
        CONSTRAINT "FK_SqlOSApplicationAssignments_Organization" FOREIGN KEY ("OrganizationId") REFERENCES "{Schema}"."SqlOSOrganizations"("Id")
    );
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSClientApplications')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSClientApplications' AND column_name = 'AccessMode') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSClientApplications_AccessMode"
    ON "{Schema}"."SqlOSClientApplications"("AccessMode");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSApplicationAssignments')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSApplicationAssignments' AND column_name = 'ClientApplicationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSApplicationAssignments' AND column_name = 'PrincipalType') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSApplicationAssignments' AND column_name = 'PrincipalId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSApplicationAssignments' AND column_name = 'OrganizationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSApplicationAssignments' AND column_name = 'RoleKey') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSApplicationAssignments' AND column_name = 'RevokedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSApplicationAssignments_Target"
    ON "{Schema}"."SqlOSApplicationAssignments"("ClientApplicationId", "PrincipalType", "PrincipalId", "OrganizationId", "RoleKey", "RevokedAt");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSApplicationAssignments')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSApplicationAssignments' AND column_name = 'ClientApplicationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSApplicationAssignments' AND column_name = 'RevokedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSApplicationAssignments_ClientApplicationId_RevokedAt"
    ON "{Schema}"."SqlOSApplicationAssignments"("ClientApplicationId", "RevokedAt");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSApplicationAssignments')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSApplicationAssignments' AND column_name = 'OrganizationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSApplicationAssignments' AND column_name = 'RevokedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSApplicationAssignments_OrganizationId_RevokedAt"
    ON "{Schema}"."SqlOSApplicationAssignments"("OrganizationId", "RevokedAt");
  END IF;
END
$sqlos_guard$;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (17);

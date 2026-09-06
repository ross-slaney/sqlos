-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSOrganizationDomains" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "OrganizationId" varchar(64) NOT NULL,
        "Domain" varchar(320) NOT NULL,
        "Status" varchar(50) NOT NULL,
        "VerificationToken" varchar(160) NULL,
        "CreatedByUserId" varchar(64) NULL,
        "CreatedAt" timestamp NOT NULL,
        "UpdatedAt" timestamp NOT NULL,
        "VerifiedAt" timestamp NULL,
        "LastCheckedAt" timestamp NULL,
        "RevokedAt" timestamp NULL,
        "LastError" varchar(1000) NULL
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSOrganizationDomains')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSOrganizationDomains' AND column_name = 'OrganizationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSOrganizationDomains' AND column_name = 'Domain') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSOrganizationDomains_OrganizationId_Domain"
        ON "{Schema}"."SqlOSOrganizationDomains"("OrganizationId", "Domain")
        WHERE "RevokedAt" IS NULL;
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSOrganizationDomains')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSOrganizationDomains' AND column_name = 'Domain') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSOrganizationDomains' AND column_name = 'Status') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSOrganizationDomains_Domain_Status"
        ON "{Schema}"."SqlOSOrganizationDomains"("Domain", "Status");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSOrganizationDomains')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSOrganizationDomains' AND column_name = 'OrganizationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSOrganizationDomains' AND column_name = 'Status') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSOrganizationDomains_OrganizationId_Status"
        ON "{Schema}"."SqlOSOrganizationDomains"("OrganizationId", "Status");
  END IF;
END
$sqlos_guard$;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSOrganizationDomains"
        ADD CONSTRAINT "FK_SqlOSOrganizationDomains_Organizations_OrganizationId"
            FOREIGN KEY ("OrganizationId") REFERENCES "{Schema}"."SqlOSOrganizations"("Id");

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (22);

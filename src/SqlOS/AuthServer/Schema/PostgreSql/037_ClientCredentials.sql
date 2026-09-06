-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSClientApplications')) IS NOT NULL THEN
    CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSClientCredentials" (
        "Id" varchar(64) NOT NULL,
        "ClientApplicationId" varchar(64) NOT NULL,
        "SecretHash" text NOT NULL,
        "DisplayName" varchar(200) NULL,
        "CreatedAt" timestamp NOT NULL,
        "ExpiresAt" timestamp NULL,
        "RevokedAt" timestamp NULL,
        "LastUsedAt" timestamp NULL,
        "ConfigurationOwner" varchar(40) NOT NULL
            CONSTRAINT "DF_SqlOSClientCredentials_ConfigurationOwner" DEFAULT 'dashboard',
        "ConfigurationSourceKey" varchar(160) NULL,
        "LastReconciledAt" timestamp NULL,
        CONSTRAINT "PK_SqlOSClientCredentials" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_SqlOSClientCredentials_ClientApplications"
            FOREIGN KEY ("ClientApplicationId")
            REFERENCES "{Schema}"."SqlOSClientApplications"("Id")
    );
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSClientCredentials')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSClientCredentials' AND column_name = 'ClientApplicationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSClientCredentials' AND column_name = 'RevokedAt') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSClientCredentials' AND column_name = 'ExpiresAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSClientCredentials_Active"
        ON "{Schema}"."SqlOSClientCredentials" ("ClientApplicationId", "RevokedAt", "ExpiresAt");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSClientCredentials')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSClientCredentials' AND column_name = 'ClientApplicationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSClientCredentials' AND column_name = 'ConfigurationOwner') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSClientCredentials' AND column_name = 'ConfigurationSourceKey') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "UX_SqlOSClientCredentials_Client_Owner_SourceKey"
        ON "{Schema}"."SqlOSClientCredentials" (
            "ClientApplicationId",
            "ConfigurationOwner",
            "ConfigurationSourceKey"
        )
        WHERE "ConfigurationSourceKey" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (37);

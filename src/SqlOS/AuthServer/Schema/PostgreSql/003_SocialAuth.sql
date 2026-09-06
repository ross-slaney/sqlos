-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSAuthSocialConnections" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "ProviderType" varchar(40) NOT NULL,
        "DisplayName" varchar(200) NOT NULL,
        "ClientId" varchar(300) NOT NULL,
        "ClientSecretEncrypted" text NOT NULL,
        "AllowedCallbackUrisJson" text NOT NULL,
        "MicrosoftTenant" varchar(200) NULL,
        "ScopesJson" text NOT NULL,
        "IsEnabled" boolean NOT NULL,
        "CreatedAt" timestamp NOT NULL,
        "UpdatedAt" timestamp NOT NULL
    );

ALTER TABLE IF EXISTS "{Schema}"."SqlOSExternalIdentities"
    ADD COLUMN IF NOT EXISTS "SocialConnectionId" varchar(64) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSExternalIdentities"
    DROP CONSTRAINT IF EXISTS "UQ_SqlOSExternalIdentities_Connection_Subject";

DROP INDEX IF EXISTS "{Schema}"."IX_SqlOSExternalIdentities_ConnectionId_Subject";

ALTER TABLE IF EXISTS "{Schema}"."SqlOSExternalIdentities"
    DROP CONSTRAINT IF EXISTS "FK_SqlOSExternalIdentities_SsoConnections";

ALTER TABLE IF EXISTS "{Schema}"."SqlOSExternalIdentities" ALTER COLUMN "ConnectionId" TYPE varchar(64);
ALTER TABLE IF EXISTS "{Schema}"."SqlOSExternalIdentities" ALTER COLUMN "ConnectionId" DROP NOT NULL;

DO $sqlos$
BEGIN
  ALTER TABLE IF EXISTS "{Schema}"."SqlOSExternalIdentities" ADD CONSTRAINT "FK_SqlOSExternalIdentities_SsoConnections" FOREIGN KEY ("ConnectionId") REFERENCES "{Schema}"."SqlOSSsoConnections"("Id");
EXCEPTION WHEN duplicate_object THEN NULL;
END
$sqlos$;

DO $sqlos$
BEGIN
  ALTER TABLE IF EXISTS "{Schema}"."SqlOSExternalIdentities" ADD CONSTRAINT "FK_SqlOSExternalIdentities_SocialConnections" FOREIGN KEY ("SocialConnectionId") REFERENCES "{Schema}"."SqlOSAuthSocialConnections"("Id");
EXCEPTION WHEN duplicate_object THEN NULL;
END
$sqlos$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSExternalIdentities')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSExternalIdentities' AND column_name = 'ConnectionId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSExternalIdentities' AND column_name = 'Subject') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSExternalIdentities_SsoConnectionId_Subject"
    ON "{Schema}"."SqlOSExternalIdentities"("ConnectionId", "Subject")
    WHERE "ConnectionId" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSExternalIdentities')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSExternalIdentities' AND column_name = 'SocialConnectionId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSExternalIdentities' AND column_name = 'Subject') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSExternalIdentities_SocialConnectionId_Subject"
    ON "{Schema}"."SqlOSExternalIdentities"("SocialConnectionId", "Subject")
    WHERE "SocialConnectionId" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (3);

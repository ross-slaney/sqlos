-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "AllowDeviceAuthorization" boolean NOT NULL
        CONSTRAINT "DF_SqlOSClientApplications_AllowDeviceAuthorization" DEFAULT FALSE;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSClientApplications')) IS NOT NULL AND to_regclass(format('%I.%I', '{Schema}', 'SqlOSUsers')) IS NOT NULL AND to_regclass(format('%I.%I', '{Schema}', 'SqlOSOrganizations')) IS NOT NULL THEN
    CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSDeviceAuthorizations"
    (
        "Id" varchar(64) NOT NULL,
        "DeviceCodeHash" varchar(512) NOT NULL,
        "UserCodeHash" varchar(512) NOT NULL,
        "UserCode" varchar(32) NOT NULL,
        "ClientApplicationId" varchar(64) NOT NULL,
        "Scope" varchar(1000) NOT NULL,
        "Resource" varchar(2048) NULL,
        "Status" varchar(32) NOT NULL,
        "PollingIntervalSeconds" INT NOT NULL,
        "CreatedAt" timestamp NOT NULL,
        "ExpiresAt" timestamp NOT NULL,
        "LastPolledAt" timestamp NULL,
        "PollCount" INT NOT NULL,
        "SlowDownCount" INT NOT NULL,
        "ApprovedUserId" varchar(64) NULL,
        "ApprovedOrganizationId" varchar(64) NULL,
        "AuthenticationMethod" varchar(50) NULL,
        "ApprovedAt" timestamp NULL,
        "DeniedAt" timestamp NULL,
        "ConsumedAt" timestamp NULL,
        "IpAddress" varchar(64) NULL,
        "UserAgent" varchar(500) NULL,
        CONSTRAINT "PK_SqlOSDeviceAuthorizations" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_SqlOSDeviceAuthorizations_ClientApplication" FOREIGN KEY ("ClientApplicationId") REFERENCES "{Schema}"."SqlOSClientApplications"("Id"),
        CONSTRAINT "FK_SqlOSDeviceAuthorizations_User" FOREIGN KEY ("ApprovedUserId") REFERENCES "{Schema}"."SqlOSUsers"("Id"),
        CONSTRAINT "FK_SqlOSDeviceAuthorizations_Organization" FOREIGN KEY ("ApprovedOrganizationId") REFERENCES "{Schema}"."SqlOSOrganizations"("Id")
    );
  END IF;
END
$sqlos_guard$;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests"
    ADD COLUMN IF NOT EXISTS "DeviceAuthorizationId" varchar(64) NULL;

DO $sqlos$
BEGIN
  ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests" ADD CONSTRAINT "FK_SqlOSAuthorizationRequests_DeviceAuthorization" FOREIGN KEY ("DeviceAuthorizationId") REFERENCES "{Schema}"."SqlOSDeviceAuthorizations"("Id");
EXCEPTION WHEN duplicate_object THEN NULL;
END
$sqlos$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSDeviceAuthorizations')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSDeviceAuthorizations' AND column_name = 'DeviceCodeHash') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSDeviceAuthorizations_DeviceCodeHash"
    ON "{Schema}"."SqlOSDeviceAuthorizations"("DeviceCodeHash");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSDeviceAuthorizations')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSDeviceAuthorizations' AND column_name = 'UserCodeHash') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSDeviceAuthorizations_UserCodeHash"
    ON "{Schema}"."SqlOSDeviceAuthorizations"("UserCodeHash");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSDeviceAuthorizations')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSDeviceAuthorizations' AND column_name = 'ClientApplicationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSDeviceAuthorizations' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSDeviceAuthorizations_ClientCreatedAt"
    ON "{Schema}"."SqlOSDeviceAuthorizations"("ClientApplicationId", "CreatedAt");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSDeviceAuthorizations')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSDeviceAuthorizations' AND column_name = 'ClientApplicationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSDeviceAuthorizations' AND column_name = 'Status') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSDeviceAuthorizations' AND column_name = 'ExpiresAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSDeviceAuthorizations_ClientStatusExpiresAt"
    ON "{Schema}"."SqlOSDeviceAuthorizations"("ClientApplicationId", "Status", "ExpiresAt");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSDeviceAuthorizations')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSDeviceAuthorizations' AND column_name = 'IpAddress') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSDeviceAuthorizations' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSDeviceAuthorizations_IpCreatedAt"
    ON "{Schema}"."SqlOSDeviceAuthorizations"("IpAddress", "CreatedAt");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSDeviceAuthorizations')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSDeviceAuthorizations' AND column_name = 'ExpiresAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSDeviceAuthorizations_ExpiresAt"
    ON "{Schema}"."SqlOSDeviceAuthorizations"("ExpiresAt");
  END IF;
END
$sqlos_guard$;

DROP INDEX IF EXISTS "{Schema}"."IX_SqlOSAuthorizationRequests_DeviceAuthorizationId";

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuthorizationRequests')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuthorizationRequests' AND column_name = 'DeviceAuthorizationId') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSAuthorizationRequests_DeviceAuthorizationId"
    ON "{Schema}"."SqlOSAuthorizationRequests"("DeviceAuthorizationId")
    WHERE "DeviceAuthorizationId" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (15);

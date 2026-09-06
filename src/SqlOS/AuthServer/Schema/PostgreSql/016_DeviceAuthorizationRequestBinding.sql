-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests"
    ADD COLUMN IF NOT EXISTS "DeviceAuthorizationId" varchar(64) NULL;

DO $sqlos$
BEGIN
  ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests" ADD CONSTRAINT "FK_SqlOSAuthorizationRequests_DeviceAuthorization" FOREIGN KEY ("DeviceAuthorizationId") REFERENCES "{Schema}"."SqlOSDeviceAuthorizations"("Id");
EXCEPTION WHEN duplicate_object THEN NULL;
END
$sqlos$;

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
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (16);

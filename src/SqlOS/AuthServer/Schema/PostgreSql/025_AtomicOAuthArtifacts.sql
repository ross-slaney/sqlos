-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuthorizationCodes')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuthorizationCodes' AND column_name = 'AuthorizationRequestId') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSAuthorizationCodes_AuthorizationRequestId"
    ON "{Schema}"."SqlOSAuthorizationCodes"("AuthorizationRequestId");
  END IF;
END
$sqlos_guard$;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (25);

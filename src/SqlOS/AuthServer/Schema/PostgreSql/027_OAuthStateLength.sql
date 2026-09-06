-- Widen OAuth state only when the current character length is below 2048.
-- text / already-wider varchar columns stay put, matching the SQL Server COL_LENGTH guard.
DO $sqlos$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = '{Schema}'
          AND table_name = 'SqlOSAuthorizationRequests'
          AND column_name = 'State'
          AND character_maximum_length IS NOT NULL
          AND character_maximum_length < 2048
    ) THEN
        ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests" ALTER COLUMN "State" TYPE varchar(2048);
        ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests" ALTER COLUMN "State" SET NOT NULL;
    END IF;
END
$sqlos$;

DO $sqlos$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = '{Schema}'
          AND table_name = 'SqlOSAuthorizationCodes'
          AND column_name = 'State'
          AND character_maximum_length IS NOT NULL
          AND character_maximum_length < 2048
    ) THEN
        ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationCodes" ALTER COLUMN "State" TYPE varchar(2048);
        ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationCodes" ALTER COLUMN "State" SET NOT NULL;
    END IF;
END
$sqlos$;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (27);

-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
DO $sqlos$
BEGIN
  IF EXISTS (
      SELECT "Key"
      FROM "{Schema}"."{Permissions}"
      GROUP BY "Key"
      HAVING COUNT(*) > 1)
  THEN
    RAISE EXCEPTION 'SqlOS cannot enforce unique FGA permission keys because duplicate keys already exist. Remove or rename duplicate permissions and restart.';
  END IF;

  IF EXISTS (
      SELECT 1
      FROM "{Schema}"."{Permissions}"
      WHERE length("Key") > 450)
  THEN
    RAISE EXCEPTION 'SqlOS cannot index FGA permission keys longer than 450 characters. Shorten those permission keys and restart.';
  END IF;
END
$sqlos$;

ALTER TABLE "{Schema}"."{Permissions}" ALTER COLUMN "Key" TYPE varchar(450);
ALTER TABLE "{Schema}"."{Permissions}" ALTER COLUMN "Key" SET NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS "UX_{Permissions}_Key"
    ON "{Schema}"."{Permissions}"("Key");

UPDATE "{Schema}"."SqlOSFgaSchema" SET "Version" = 6 WHERE "Version" < 6;

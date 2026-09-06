-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSSigningKeys" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "Kid" varchar(120) NOT NULL UNIQUE,
        "Algorithm" varchar(20) NOT NULL,
        "PublicKeyPem" text NOT NULL,
        "CustodyProvider" varchar(120) NOT NULL,
        "KeyReference" text NOT NULL,
        "IsActive" boolean NOT NULL,
        "ActivatedAt" timestamp NOT NULL,
        "RetiredAt" timestamp NULL,
        CONSTRAINT "CK_SqlOSSigningKeys_Lifecycle" CHECK (
            ("IsActive" = TRUE AND "RetiredAt" IS NULL)
            OR ("IsActive" = FALSE AND "RetiredAt" IS NOT NULL))
    );

DO $sqlos$
BEGIN
  ALTER TABLE IF EXISTS "{Schema}"."SqlOSSigningKeys"
    ADD CONSTRAINT "CK_SqlOSSigningKeys_Lifecycle" CHECK (
      ("IsActive" = TRUE AND "RetiredAt" IS NULL)
      OR ("IsActive" = FALSE AND "RetiredAt" IS NOT NULL));
EXCEPTION
  WHEN duplicate_object THEN NULL;
END
$sqlos$;

DO $sqlos$
BEGIN
  IF EXISTS (
      SELECT 1
      FROM information_schema.columns
      WHERE table_schema = '{Schema}'
        AND table_name = 'SqlOSSigningKeys'
        AND column_name = 'PrivateKeyPem')
     AND NOT EXISTS (
      SELECT 1
      FROM information_schema.columns
      WHERE table_schema = '{Schema}'
        AND table_name = 'SqlOSSigningKeys'
        AND column_name = 'KeyReference')
  THEN
    ALTER TABLE IF EXISTS "{Schema}"."SqlOSSigningKeys" RENAME COLUMN "PrivateKeyPem" TO "KeyReference";
  ELSIF NOT EXISTS (
      SELECT 1
      FROM information_schema.columns
      WHERE table_schema = '{Schema}'
        AND table_name = 'SqlOSSigningKeys'
        AND column_name = 'KeyReference')
  THEN
    ALTER TABLE IF EXISTS "{Schema}"."SqlOSSigningKeys"
      ADD COLUMN "KeyReference" text NOT NULL DEFAULT '';
  END IF;
END
$sqlos$;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSSigningKeys"
    ADD COLUMN IF NOT EXISTS "CustodyProvider" varchar(120) NOT NULL
        CONSTRAINT "DF_SqlOSSigningKeys_CustodyProvider" DEFAULT 'legacy-unprotected';

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSSigningKeys')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSigningKeys' AND column_name = 'IsActive') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "UX_SqlOSSigningKeys_OneActive"
    ON "{Schema}"."SqlOSSigningKeys" ("IsActive")
    WHERE "IsActive" = TRUE;
  END IF;
END
$sqlos_guard$;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (28);

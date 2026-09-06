-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSEmailTemplates" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "Key" varchar(120) NOT NULL,
        "DisplayName" varchar(200) NOT NULL,
        "SubjectTemplate" varchar(500) NOT NULL,
        "HtmlBodyTemplate" text NOT NULL,
        "TextBodyTemplate" text NOT NULL,
        "VariablesJson" text NOT NULL
            CONSTRAINT "DF_SqlOSEmailTemplates_VariablesJson" DEFAULT '{{}}',
        "IsActive" boolean NOT NULL
            CONSTRAINT "DF_SqlOSEmailTemplates_IsActive" DEFAULT TRUE,
        "Version" INT NOT NULL
            CONSTRAINT "DF_SqlOSEmailTemplates_Version" DEFAULT 1,
        "CreatedAt" timestamp NOT NULL,
        "UpdatedAt" timestamp NOT NULL
    );

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSEmailTemplates')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailTemplates' AND column_name = 'Key') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSEmailTemplates_Key"
    ON "{Schema}"."SqlOSEmailTemplates"("Key");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSEmailTemplates')) IS NOT NULL THEN
    CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSEmailDeliveries" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "TemplateId" varchar(64) NULL,
        "TemplateKey" varchar(120) NOT NULL,
        "TemplateVersion" INT NOT NULL,
        "To" varchar(320) NOT NULL,
        "Status" varchar(32) NOT NULL,
        "ProviderMessageId" varchar(200) NULL,
        "SanitizedError" varchar(500) NULL,
        "RenderedSubject" varchar(500) NOT NULL,
        "RenderedTextPreview" text NOT NULL,
        "RenderedHtmlPreview" text NULL,
        "IdempotencyKey" varchar(200) NULL,
        "CreatedAt" timestamp NOT NULL,
        "UpdatedAt" timestamp NOT NULL,
        "SentAt" timestamp NULL,
        "FailedAt" timestamp NULL,
        CONSTRAINT "FK_SqlOSEmailDeliveries_SqlOSEmailTemplates_TemplateId"
            FOREIGN KEY ("TemplateId")
            REFERENCES "{Schema}"."SqlOSEmailTemplates"("Id")
            ON DELETE SET NULL
    );
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSEmailDeliveries')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailDeliveries' AND column_name = 'TemplateKey') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailDeliveries' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSEmailDeliveries_TemplateKeyCreatedAt"
    ON "{Schema}"."SqlOSEmailDeliveries"("TemplateKey", "CreatedAt");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSEmailDeliveries')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailDeliveries' AND column_name = 'Status') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailDeliveries' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSEmailDeliveries_StatusCreatedAt"
    ON "{Schema}"."SqlOSEmailDeliveries"("Status", "CreatedAt");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSEmailDeliveries')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailDeliveries' AND column_name = 'To') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailDeliveries' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSEmailDeliveries_ToCreatedAt"
    ON "{Schema}"."SqlOSEmailDeliveries"("To", "CreatedAt");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSEmailDeliveries')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailDeliveries' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSEmailDeliveries_CreatedAt"
    ON "{Schema}"."SqlOSEmailDeliveries"("CreatedAt");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSEmailDeliveries')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailDeliveries' AND column_name = 'IdempotencyKey') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSEmailDeliveries_IdempotencyKey"
    ON "{Schema}"."SqlOSEmailDeliveries"("IdempotencyKey")
    WHERE "IdempotencyKey" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (17);

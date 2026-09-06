-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSEmailOtpChallenges" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "ChallengeTokenHash" varchar(128) NOT NULL,
        "CodeHash" varchar(128) NOT NULL,
        "Email" varchar(320) NOT NULL,
        "NormalizedEmail" varchar(320) NOT NULL,
        "UserId" varchar(64) NULL,
        "UserEmailId" varchar(64) NULL,
        "AuthorizationRequestId" varchar(64) NULL,
        "ClientApplicationId" varchar(64) NULL,
        "RequestedOrganizationId" varchar(64) NULL,
        "AttemptCount" INT NOT NULL CONSTRAINT "DF_SqlOSEmailOtpChallenges_AttemptCount" DEFAULT 0,
        "MaxAttempts" INT NOT NULL CONSTRAINT "DF_SqlOSEmailOtpChallenges_MaxAttempts" DEFAULT 5,
        "CreatedAt" timestamp NOT NULL,
        "ExpiresAt" timestamp NOT NULL,
        "LastSentAt" timestamp NOT NULL,
        "ConsumedAt" timestamp NULL,
        "InvalidatedAt" timestamp NULL,
        "InvalidatedReason" varchar(120) NULL,
        "IpAddress" varchar(128) NULL,
        "UserAgent" varchar(512) NULL
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSEmailOtpChallenges')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailOtpChallenges' AND column_name = 'ChallengeTokenHash') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSEmailOtpChallenges_ChallengeTokenHash"
        ON "{Schema}"."SqlOSEmailOtpChallenges"("ChallengeTokenHash");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSEmailOtpChallenges')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailOtpChallenges' AND column_name = 'NormalizedEmail') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailOtpChallenges' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSEmailOtpChallenges_NormalizedEmail_CreatedAt"
        ON "{Schema}"."SqlOSEmailOtpChallenges"("NormalizedEmail", "CreatedAt");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSEmailOtpChallenges')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailOtpChallenges' AND column_name = 'IpAddress') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailOtpChallenges' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSEmailOtpChallenges_IpAddress_CreatedAt"
        ON "{Schema}"."SqlOSEmailOtpChallenges"("IpAddress", "CreatedAt");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSEmailOtpChallenges')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailOtpChallenges' AND column_name = 'ClientApplicationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailOtpChallenges' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSEmailOtpChallenges_ClientApplicationId_CreatedAt"
        ON "{Schema}"."SqlOSEmailOtpChallenges"("ClientApplicationId", "CreatedAt");
  END IF;
END
$sqlos_guard$;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSEmailOtpChallenges"
        ADD CONSTRAINT "FK_SqlOSEmailOtpChallenges_Users_UserId"
            FOREIGN KEY ("UserId") REFERENCES "{Schema}"."SqlOSUsers"("Id");

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSEmailOtpChallenges"
        ADD CONSTRAINT "FK_SqlOSEmailOtpChallenges_UserEmails_UserEmailId"
            FOREIGN KEY ("UserEmailId") REFERENCES "{Schema}"."SqlOSUserEmails"("Id");

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSEmailOtpChallenges"
        ADD CONSTRAINT "FK_SqlOSEmailOtpChallenges_AuthorizationRequests_AuthorizationRequestId"
            FOREIGN KEY ("AuthorizationRequestId") REFERENCES "{Schema}"."SqlOSAuthorizationRequests"("Id");

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSEmailOtpChallenges"
        ADD CONSTRAINT "FK_SqlOSEmailOtpChallenges_ClientApplications_ClientApplicationId"
            FOREIGN KEY ("ClientApplicationId") REFERENCES "{Schema}"."SqlOSClientApplications"("Id");

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSEmailOtpChallenges')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailOtpChallenges' AND column_name = 'IpAddress') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailOtpChallenges' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSEmailOtpChallenges_IpAddress_CreatedAt"
        ON "{Schema}"."SqlOSEmailOtpChallenges"("IpAddress", "CreatedAt");
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSEmailOtpChallenges')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailOtpChallenges' AND column_name = 'ClientApplicationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSEmailOtpChallenges' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSEmailOtpChallenges_ClientApplicationId_CreatedAt"
        ON "{Schema}"."SqlOSEmailOtpChallenges"("ClientApplicationId", "CreatedAt");
  END IF;
END
$sqlos_guard$;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (12);

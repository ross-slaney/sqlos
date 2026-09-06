-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSUserPhoneNumbers" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "UserId" varchar(64) NOT NULL,
        "PhoneNumber" varchar(32) NOT NULL,
        "PhoneNumberHash" varchar(128) NOT NULL,
        "DisplayValueEncrypted" varchar(2048) NULL,
        "IsPrimary" boolean NOT NULL CONSTRAINT "DF_SqlOSUserPhoneNumbers_IsPrimary" DEFAULT FALSE,
        "IsVerified" boolean NOT NULL CONSTRAINT "DF_SqlOSUserPhoneNumbers_IsVerified" DEFAULT FALSE,
        "VerifiedAt" timestamp NULL,
        "CreatedAt" timestamp NOT NULL,
        "UpdatedAt" timestamp NOT NULL,
        "LastUsedAt" timestamp NULL,
        "RemovedAt" timestamp NULL,
        "RemovalReason" varchar(120) NULL
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSUserPhoneNumbers')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSUserPhoneNumbers' AND column_name = 'PhoneNumberHash') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSUserPhoneNumbers_PhoneNumberHash"
        ON "{Schema}"."SqlOSUserPhoneNumbers"("PhoneNumberHash")
        WHERE "RemovedAt" IS NULL;
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSUserPhoneNumbers')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSUserPhoneNumbers' AND column_name = 'UserId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSUserPhoneNumbers' AND column_name = 'RemovedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSUserPhoneNumbers_UserId_RemovedAt"
        ON "{Schema}"."SqlOSUserPhoneNumbers"("UserId", "RemovedAt");
  END IF;
END
$sqlos_guard$;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSUserPhoneNumbers"
        ADD CONSTRAINT "FK_SqlOSUserPhoneNumbers_Users_UserId"
            FOREIGN KEY ("UserId") REFERENCES "{Schema}"."SqlOSUsers"("Id");

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSPhoneOtpChallenges" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "ChallengeTokenHash" varchar(128) NOT NULL,
        "PhoneNumberHash" varchar(128) NOT NULL,
        "PhoneNumberEncrypted" varchar(2048) NOT NULL,
        "MaskedPhoneNumber" varchar(32) NOT NULL,
        "Purpose" varchar(32) NOT NULL,
        "UserId" varchar(64) NULL,
        "UserPhoneNumberId" varchar(64) NULL,
        "AuthorizationRequestId" varchar(64) NULL,
        "ClientApplicationId" varchar(64) NULL,
        "RequestedOrganizationId" varchar(64) NULL,
        "ProviderStarted" boolean NOT NULL CONSTRAINT "DF_SqlOSPhoneOtpChallenges_ProviderStarted" DEFAULT FALSE,
        "Provider" varchar(40) NOT NULL,
        "ProviderChallengeId" varchar(128) NULL,
        "ProviderStatus" varchar(80) NULL,
        "AttemptCount" INT NOT NULL CONSTRAINT "DF_SqlOSPhoneOtpChallenges_AttemptCount" DEFAULT 0,
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
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSPhoneOtpChallenges')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPhoneOtpChallenges' AND column_name = 'ChallengeTokenHash') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSPhoneOtpChallenges_ChallengeTokenHash"
        ON "{Schema}"."SqlOSPhoneOtpChallenges"("ChallengeTokenHash");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSPhoneOtpChallenges')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPhoneOtpChallenges' AND column_name = 'PhoneNumberHash') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPhoneOtpChallenges' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSPhoneOtpChallenges_PhoneNumberHash_CreatedAt"
        ON "{Schema}"."SqlOSPhoneOtpChallenges"("PhoneNumberHash", "CreatedAt");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSPhoneOtpChallenges')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPhoneOtpChallenges' AND column_name = 'UserId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPhoneOtpChallenges' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSPhoneOtpChallenges_UserId_CreatedAt"
        ON "{Schema}"."SqlOSPhoneOtpChallenges"("UserId", "CreatedAt");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSPhoneOtpChallenges')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPhoneOtpChallenges' AND column_name = 'IpAddress') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPhoneOtpChallenges' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSPhoneOtpChallenges_IpAddress_CreatedAt"
        ON "{Schema}"."SqlOSPhoneOtpChallenges"("IpAddress", "CreatedAt");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSPhoneOtpChallenges')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPhoneOtpChallenges' AND column_name = 'ClientApplicationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSPhoneOtpChallenges' AND column_name = 'CreatedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSPhoneOtpChallenges_ClientApplicationId_CreatedAt"
        ON "{Schema}"."SqlOSPhoneOtpChallenges"("ClientApplicationId", "CreatedAt");
  END IF;
END
$sqlos_guard$;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSPhoneOtpChallenges"
        ADD CONSTRAINT "FK_SqlOSPhoneOtpChallenges_Users_UserId"
            FOREIGN KEY ("UserId") REFERENCES "{Schema}"."SqlOSUsers"("Id");

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSPhoneOtpChallenges"
        ADD CONSTRAINT "FK_SqlOSPhoneOtpChallenges_UserPhoneNumbers_UserPhoneNumberId"
            FOREIGN KEY ("UserPhoneNumberId") REFERENCES "{Schema}"."SqlOSUserPhoneNumbers"("Id");

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSPhoneOtpChallenges"
        ADD CONSTRAINT "FK_SqlOSPhoneOtpChallenges_AuthorizationRequests_AuthorizationRequestId"
            FOREIGN KEY ("AuthorizationRequestId") REFERENCES "{Schema}"."SqlOSAuthorizationRequests"("Id");

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSPhoneOtpChallenges"
        ADD CONSTRAINT "FK_SqlOSPhoneOtpChallenges_ClientApplications_ClientApplicationId"
            FOREIGN KEY ("ClientApplicationId") REFERENCES "{Schema}"."SqlOSClientApplications"("Id");

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (18);

-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSMfaSettings" (
        "Id" varchar(64) NOT NULL CONSTRAINT "PK_SqlOSMfaSettings" PRIMARY KEY,
        "Enabled" boolean NOT NULL,
        "TotpEnabled" boolean NOT NULL,
        "UserSelfEnrollmentEnabled" boolean NOT NULL,
        "RecoveryCodesEnabled" boolean NOT NULL,
        "RequireForAllUsers" boolean NOT NULL,
        "RequireForOwnersAndAdmins" boolean NOT NULL,
        "RequiredRolesJson" text NOT NULL,
        "AvailableFactorsJson" text NOT NULL,
        "UpdatedAt" timestamp NOT NULL
    );

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSOrganizationMfaPolicies" (
        "OrganizationId" varchar(64) NOT NULL CONSTRAINT "PK_SqlOSOrganizationMfaPolicies" PRIMARY KEY,
        "IsEnabled" boolean NOT NULL,
        "RequireMfaForAllUsers" boolean NOT NULL,
        "RequireMfaForOwnersAndAdmins" boolean NOT NULL,
        "UserSelfEnrollmentEnabled" boolean NOT NULL,
        "RecoveryCodesEnabled" boolean NOT NULL,
        "RequiredRolesJson" text NOT NULL,
        "AvailableFactorsJson" text NOT NULL,
        "UpdatedAt" timestamp NOT NULL
    );

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSOrganizationMfaPolicies"
        ADD CONSTRAINT "FK_SqlOSOrganizationMfaPolicies_Organizations_OrganizationId"
            FOREIGN KEY ("OrganizationId") REFERENCES "{Schema}"."SqlOSOrganizations"("Id");

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSUserMfaPolicyOverrides" (
        "UserId" varchar(64) NOT NULL CONSTRAINT "PK_SqlOSUserMfaPolicyOverrides" PRIMARY KEY,
        "RequireMfa" boolean NULL,
        "UserSelfEnrollmentEnabled" boolean NULL,
        "UpdatedAt" timestamp NOT NULL
    );

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSUserMfaPolicyOverrides"
        ADD CONSTRAINT "FK_SqlOSUserMfaPolicyOverrides_Users_UserId"
            FOREIGN KEY ("UserId") REFERENCES "{Schema}"."SqlOSUsers"("Id");

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSUserAuthenticators" (
        "Id" varchar(64) NOT NULL CONSTRAINT "PK_SqlOSUserAuthenticators" PRIMARY KEY,
        "UserId" varchar(64) NOT NULL,
        "Type" varchar(40) NOT NULL,
        "DisplayName" varchar(120) NOT NULL,
        "SecretProtected" varchar(2048) NOT NULL,
        "SecretVersion" INT NOT NULL,
        "Algorithm" varchar(20) NOT NULL,
        "Digits" INT NOT NULL,
        "PeriodSeconds" INT NOT NULL,
        "IsConfirmed" boolean NOT NULL,
        "CreatedAt" timestamp NOT NULL,
        "ConfirmedAt" timestamp NULL,
        "LastUsedAt" timestamp NULL,
        "RevokedAt" timestamp NULL,
        "RevocationReason" varchar(120) NULL,
        "LastAcceptedTimeStep" BIGINT NULL
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSUserAuthenticators')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSUserAuthenticators' AND column_name = 'UserId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSUserAuthenticators' AND column_name = 'Type') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSUserAuthenticators' AND column_name = 'RevokedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSUserAuthenticators_User_Type_Revoked"
        ON "{Schema}"."SqlOSUserAuthenticators"("UserId", "Type", "RevokedAt");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSUserAuthenticators')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSUserAuthenticators' AND column_name = 'UserId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSUserAuthenticators' AND column_name = 'IsConfirmed') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSUserAuthenticators' AND column_name = 'RevokedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSUserAuthenticators_User_Confirmed_Revoked"
        ON "{Schema}"."SqlOSUserAuthenticators"("UserId", "IsConfirmed", "RevokedAt");
  END IF;
END
$sqlos_guard$;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSUserAuthenticators"
        ADD CONSTRAINT "FK_SqlOSUserAuthenticators_Users_UserId"
            FOREIGN KEY ("UserId") REFERENCES "{Schema}"."SqlOSUsers"("Id");

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSRecoveryCodes" (
        "Id" varchar(64) NOT NULL CONSTRAINT "PK_SqlOSRecoveryCodes" PRIMARY KEY,
        "UserId" varchar(64) NOT NULL,
        "CodeHash" varchar(128) NOT NULL,
        "CreatedAt" timestamp NOT NULL,
        "ConsumedAt" timestamp NULL,
        "RevokedAt" timestamp NULL
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSRecoveryCodes')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSRecoveryCodes' AND column_name = 'CodeHash') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSRecoveryCodes_CodeHash"
        ON "{Schema}"."SqlOSRecoveryCodes"("CodeHash");
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSRecoveryCodes')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSRecoveryCodes' AND column_name = 'UserId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSRecoveryCodes' AND column_name = 'ConsumedAt') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSRecoveryCodes' AND column_name = 'RevokedAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSRecoveryCodes_User_Consumed_Revoked"
        ON "{Schema}"."SqlOSRecoveryCodes"("UserId", "ConsumedAt", "RevokedAt");
  END IF;
END
$sqlos_guard$;

    ALTER TABLE IF EXISTS "{Schema}"."SqlOSRecoveryCodes"
        ADD CONSTRAINT "FK_SqlOSRecoveryCodes_Users_UserId"
            FOREIGN KEY ("UserId") REFERENCES "{Schema}"."SqlOSUsers"("Id");

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (19);

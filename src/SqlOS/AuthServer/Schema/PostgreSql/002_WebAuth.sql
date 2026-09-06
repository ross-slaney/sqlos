-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSOrganizations"
    ADD COLUMN IF NOT EXISTS "PrimaryDomain" varchar(320) NULL;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSOrganizations')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSOrganizations' AND column_name = 'PrimaryDomain') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSOrganizations_PrimaryDomain"
    ON "{Schema}"."SqlOSOrganizations"("PrimaryDomain")
    WHERE "PrimaryDomain" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSSettings" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "RefreshTokenLifetimeMinutes" INT NOT NULL,
        "SessionIdleTimeoutMinutes" INT NOT NULL,
        "SessionAbsoluteLifetimeMinutes" INT NOT NULL,
        "UpdatedAt" timestamp NOT NULL
    );

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSClientApplications')) IS NOT NULL AND to_regclass(format('%I.%I', '{Schema}', 'SqlOSOrganizations')) IS NOT NULL AND to_regclass(format('%I.%I', '{Schema}', 'SqlOSSsoConnections')) IS NOT NULL THEN
    CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSAuthorizationRequests" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "ClientApplicationId" varchar(64) NOT NULL,
        "OrganizationId" varchar(64) NOT NULL,
        "ConnectionId" varchar(64) NOT NULL,
        "LoginHintEmail" varchar(320) NOT NULL,
        "RedirectUri" varchar(2048) NOT NULL,
        "State" varchar(256) NOT NULL,
        "CodeChallenge" varchar(256) NOT NULL,
        "CodeChallengeMethod" varchar(32) NOT NULL,
        "CreatedAt" timestamp NOT NULL,
        "ExpiresAt" timestamp NOT NULL,
        "CompletedAt" timestamp NULL,
        "CancelledAt" timestamp NULL,
        CONSTRAINT "FK_SqlOSAuthorizationRequests_Clients" FOREIGN KEY ("ClientApplicationId") REFERENCES "{Schema}"."SqlOSClientApplications"("Id"),
        CONSTRAINT "FK_SqlOSAuthorizationRequests_Organizations" FOREIGN KEY ("OrganizationId") REFERENCES "{Schema}"."SqlOSOrganizations"("Id"),
        CONSTRAINT "FK_SqlOSAuthorizationRequests_SsoConnections" FOREIGN KEY ("ConnectionId") REFERENCES "{Schema}"."SqlOSSsoConnections"("Id")
    );
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuthorizationRequests')) IS NOT NULL AND to_regclass(format('%I.%I', '{Schema}', 'SqlOSUsers')) IS NOT NULL AND to_regclass(format('%I.%I', '{Schema}', 'SqlOSClientApplications')) IS NOT NULL AND to_regclass(format('%I.%I', '{Schema}', 'SqlOSOrganizations')) IS NOT NULL THEN
    CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSAuthorizationCodes" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "AuthorizationRequestId" varchar(64) NOT NULL,
        "UserId" varchar(64) NOT NULL,
        "ClientApplicationId" varchar(64) NOT NULL,
        "OrganizationId" varchar(64) NOT NULL,
        "RedirectUri" varchar(2048) NOT NULL,
        "State" varchar(256) NOT NULL,
        "CodeHash" varchar(128) NOT NULL UNIQUE,
        "CodeChallenge" varchar(256) NOT NULL,
        "CodeChallengeMethod" varchar(32) NOT NULL,
        "AuthenticationMethod" varchar(50) NOT NULL,
        "CreatedAt" timestamp NOT NULL,
        "ExpiresAt" timestamp NOT NULL,
        "ConsumedAt" timestamp NULL,
        CONSTRAINT "FK_SqlOSAuthorizationCodes_Requests" FOREIGN KEY ("AuthorizationRequestId") REFERENCES "{Schema}"."SqlOSAuthorizationRequests"("Id"),
        CONSTRAINT "FK_SqlOSAuthorizationCodes_Users" FOREIGN KEY ("UserId") REFERENCES "{Schema}"."SqlOSUsers"("Id"),
        CONSTRAINT "FK_SqlOSAuthorizationCodes_Clients" FOREIGN KEY ("ClientApplicationId") REFERENCES "{Schema}"."SqlOSClientApplications"("Id"),
        CONSTRAINT "FK_SqlOSAuthorizationCodes_Organizations" FOREIGN KEY ("OrganizationId") REFERENCES "{Schema}"."SqlOSOrganizations"("Id")
    );
  END IF;
END
$sqlos_guard$;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (2);

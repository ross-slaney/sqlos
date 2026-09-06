-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
CREATE SCHEMA IF NOT EXISTS "{Schema}";

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSOrganizations" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "Slug" varchar(120) NOT NULL UNIQUE,
        "Name" varchar(200) NOT NULL,
        "IsActive" boolean NOT NULL,
        "CreatedAt" timestamp NOT NULL
    );

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSUsers" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "DisplayName" varchar(200) NOT NULL,
        "DefaultEmail" varchar(320) NULL,
        "IsActive" boolean NOT NULL,
        "CreatedAt" timestamp NOT NULL,
        "UpdatedAt" timestamp NOT NULL
    );

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSUsers')) IS NOT NULL THEN
    CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSUserEmails" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "UserId" varchar(64) NOT NULL,
        "Email" varchar(320) NOT NULL,
        "NormalizedEmail" varchar(320) NOT NULL UNIQUE,
        "IsPrimary" boolean NOT NULL,
        "IsVerified" boolean NOT NULL,
        "VerifiedAt" timestamp NULL,
        "CreatedAt" timestamp NOT NULL,
        CONSTRAINT "FK_SqlOSUserEmails_Users" FOREIGN KEY ("UserId") REFERENCES "{Schema}"."SqlOSUsers"("Id")
    );
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSUsers')) IS NOT NULL THEN
    CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSCredentials" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "UserId" varchar(64) NOT NULL,
        "Type" varchar(50) NOT NULL,
        "SecretHash" text NOT NULL,
        "SecretVersion" INT NOT NULL,
        "LastUsedAt" timestamp NULL,
        "CreatedAt" timestamp NOT NULL,
        "RevokedAt" timestamp NULL,
        CONSTRAINT "FK_SqlOSCredentials_Users" FOREIGN KEY ("UserId") REFERENCES "{Schema}"."SqlOSUsers"("Id")
    );
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSOrganizations')) IS NOT NULL AND to_regclass(format('%I.%I', '{Schema}', 'SqlOSUsers')) IS NOT NULL THEN
    CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSMemberships" (
        "OrganizationId" varchar(64) NOT NULL,
        "UserId" varchar(64) NOT NULL,
        "Role" varchar(50) NOT NULL,
        "IsActive" boolean NOT NULL,
        "CreatedAt" timestamp NOT NULL,
        CONSTRAINT "PK_SqlOSMemberships" PRIMARY KEY ("OrganizationId", "UserId"),
        CONSTRAINT "FK_SqlOSMemberships_Organizations" FOREIGN KEY ("OrganizationId") REFERENCES "{Schema}"."SqlOSOrganizations"("Id"),
        CONSTRAINT "FK_SqlOSMemberships_Users" FOREIGN KEY ("UserId") REFERENCES "{Schema}"."SqlOSUsers"("Id")
    );
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSOrganizations')) IS NOT NULL THEN
    CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSSsoConnections" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "OrganizationId" varchar(64) NOT NULL,
        "DisplayName" varchar(200) NOT NULL,
        "IsEnabled" boolean NOT NULL,
        "IdentityProviderEntityId" varchar(400) NOT NULL,
        "SingleSignOnUrl" varchar(1024) NOT NULL,
        "X509CertificatePem" text NOT NULL,
        "NameIdFormat" varchar(256) NULL,
        "EmailAttributeName" varchar(128) NOT NULL,
        "FirstNameAttributeName" varchar(128) NOT NULL,
        "LastNameAttributeName" varchar(128) NOT NULL,
        "AutoProvisionUsers" boolean NOT NULL,
        "AutoLinkByEmail" boolean NOT NULL,
        "CreatedAt" timestamp NOT NULL,
        "UpdatedAt" timestamp NOT NULL,
        CONSTRAINT "FK_SqlOSSsoConnections_Organizations" FOREIGN KEY ("OrganizationId") REFERENCES "{Schema}"."SqlOSOrganizations"("Id")
    );
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSUsers')) IS NOT NULL AND to_regclass(format('%I.%I', '{Schema}', 'SqlOSSsoConnections')) IS NOT NULL THEN
    CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSExternalIdentities" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "UserId" varchar(64) NOT NULL,
        "ConnectionId" varchar(64) NOT NULL,
        "Issuer" varchar(400) NOT NULL,
        "Subject" varchar(400) NOT NULL,
        "Email" varchar(320) NULL,
        "CreatedAt" timestamp NOT NULL,
        CONSTRAINT "UQ_SqlOSExternalIdentities_Connection_Subject" UNIQUE ("ConnectionId", "Subject"),
        CONSTRAINT "FK_SqlOSExternalIdentities_Users" FOREIGN KEY ("UserId") REFERENCES "{Schema}"."SqlOSUsers"("Id"),
        CONSTRAINT "FK_SqlOSExternalIdentities_SsoConnections" FOREIGN KEY ("ConnectionId") REFERENCES "{Schema}"."SqlOSSsoConnections"("Id")
    );
  END IF;
END
$sqlos_guard$;

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSClientApplications" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "ClientId" varchar(120) NOT NULL UNIQUE,
        "Name" varchar(200) NOT NULL,
        "Audience" varchar(200) NOT NULL,
        "RedirectUrisJson" text NOT NULL,
        "IsActive" boolean NOT NULL,
        "CreatedAt" timestamp NOT NULL
    );

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSUsers')) IS NOT NULL AND to_regclass(format('%I.%I', '{Schema}', 'SqlOSClientApplications')) IS NOT NULL THEN
    CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSSessions" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "UserId" varchar(64) NOT NULL,
        "AuthenticationMethod" varchar(50) NULL,
        "ClientApplicationId" varchar(64) NULL,
        "CreatedAt" timestamp NOT NULL,
        "LastSeenAt" timestamp NOT NULL,
        "IdleExpiresAt" timestamp NOT NULL,
        "AbsoluteExpiresAt" timestamp NOT NULL,
        "RevokedAt" timestamp NULL,
        "RevocationReason" varchar(200) NULL,
        "UserAgent" varchar(1024) NULL,
        "IpAddress" varchar(128) NULL,
        CONSTRAINT "FK_SqlOSSessions_Users" FOREIGN KEY ("UserId") REFERENCES "{Schema}"."SqlOSUsers"("Id"),
        CONSTRAINT "FK_SqlOSSessions_Clients" FOREIGN KEY ("ClientApplicationId") REFERENCES "{Schema}"."SqlOSClientApplications"("Id")
    );
  END IF;
END
$sqlos_guard$;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSSessions')) IS NOT NULL THEN
    CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSRefreshTokens" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "SessionId" varchar(64) NOT NULL,
        "TokenHash" varchar(128) NOT NULL UNIQUE,
        "FamilyId" varchar(64) NOT NULL,
        "CreatedAt" timestamp NOT NULL,
        "ExpiresAt" timestamp NOT NULL,
        "ConsumedAt" timestamp NULL,
        "RevokedAt" timestamp NULL,
        "ReplacedByTokenId" varchar(64) NULL,
        CONSTRAINT "FK_SqlOSRefreshTokens_Sessions" FOREIGN KEY ("SessionId") REFERENCES "{Schema}"."SqlOSSessions"("Id")
    );
  END IF;
END
$sqlos_guard$;

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

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSSigningKeys')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSSigningKeys' AND column_name = 'IsActive') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "UX_SqlOSSigningKeys_OneActive"
    ON "{Schema}"."SqlOSSigningKeys" ("IsActive")
    WHERE "IsActive" = TRUE;
  END IF;
END
$sqlos_guard$;

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSTemporaryTokens" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "Purpose" varchar(80) NOT NULL,
        "TokenHash" varchar(128) NOT NULL UNIQUE,
        "UserId" varchar(64) NULL,
        "ClientApplicationId" varchar(64) NULL,
        "OrganizationId" varchar(64) NULL,
        "PayloadJson" text NULL,
        "CreatedAt" timestamp NOT NULL,
        "ExpiresAt" timestamp NOT NULL,
        "ConsumedAt" timestamp NULL
    );

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSAuditEvents" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "OrganizationId" varchar(64) NULL,
        "ApplicationId" varchar(64) NULL,
        "ApplicationKey" varchar(200) NULL,
        "UserId" varchar(64) NULL,
        "SessionId" varchar(64) NULL,
        "EventType" varchar(160) NOT NULL,
        "Source" varchar(80) NOT NULL CONSTRAINT "DF_SqlOSAuditEvents_Source" DEFAULT ('authserver'),
        "Action" varchar(160) NOT NULL CONSTRAINT "DF_SqlOSAuditEvents_Action" DEFAULT (''),
        "ActorType" varchar(80) NOT NULL,
        "ActorId" varchar(128) NULL,
        "ActorDisplayName" varchar(320) NULL,
        "TargetsJson" text NOT NULL CONSTRAINT "DF_SqlOSAuditEvents_TargetsJson" DEFAULT ('[]'),
        "ContextJson" text NULL,
        "MetadataJson" text NULL,
        "OccurredAt" timestamp NOT NULL,
        "IngestedAt" timestamp NOT NULL CONSTRAINT "DF_SqlOSAuditEvents_IngestedAt" DEFAULT ((CURRENT_TIMESTAMP AT TIME ZONE 'UTC')),
        "IpAddress" varchar(128) NULL,
        "UserAgent" varchar(512) NULL,
        "RequestId" varchar(128) NULL,
        "CorrelationId" varchar(128) NULL,
        "IdempotencyKeyHash" varchar(128) NULL,
        "DataJson" text NULL
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuditEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'OccurredAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSAuditEvents_OccurredAt" ON "{Schema}"."SqlOSAuditEvents" ("OccurredAt" DESC);
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuditEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'OrganizationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'OccurredAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSAuditEvents_OrganizationId_OccurredAt" ON "{Schema}"."SqlOSAuditEvents" ("OrganizationId", "OccurredAt" DESC);
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuditEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'ApplicationId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'OccurredAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSAuditEvents_ApplicationId_OccurredAt" ON "{Schema}"."SqlOSAuditEvents" ("ApplicationId", "OccurredAt" DESC);
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuditEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'ApplicationKey') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'OccurredAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSAuditEvents_ApplicationKey_OccurredAt" ON "{Schema}"."SqlOSAuditEvents" ("ApplicationKey", "OccurredAt" DESC);
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuditEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'Source') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'OccurredAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSAuditEvents_Source_OccurredAt" ON "{Schema}"."SqlOSAuditEvents" ("Source", "OccurredAt" DESC);
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuditEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'Action') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'OccurredAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSAuditEvents_Action_OccurredAt" ON "{Schema}"."SqlOSAuditEvents" ("Action", "OccurredAt" DESC);
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuditEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'ActorType') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'ActorId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'OccurredAt') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSAuditEvents_Actor_OccurredAt" ON "{Schema}"."SqlOSAuditEvents" ("ActorType", "ActorId", "OccurredAt" DESC);
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuditEvents')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSAuditEvents' AND column_name = 'IdempotencyKeyHash') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "UX_SqlOSAuditEvents_IdempotencyKeyHash" ON "{Schema}"."SqlOSAuditEvents" ("IdempotencyKeyHash") WHERE "IdempotencyKeyHash" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (1);

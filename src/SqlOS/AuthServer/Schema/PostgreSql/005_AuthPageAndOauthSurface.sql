-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSAuthPageSettings" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "LogoBase64" text NULL,
        "PrimaryColor" varchar(32) NOT NULL,
        "AccentColor" varchar(32) NOT NULL,
        "BackgroundColor" varchar(32) NOT NULL,
        "Layout" varchar(32) NOT NULL,
        "PageTitle" varchar(200) NOT NULL,
        "PageSubtitle" varchar(500) NOT NULL,
        "EnablePasswordSignup" boolean NOT NULL,
        "EnabledCredentialTypesJson" text NOT NULL,
        "UpdatedAt" timestamp NOT NULL
    );

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "Description" varchar(500) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "ClientType" varchar(40) NOT NULL CONSTRAINT "DF_SqlOSClientApplications_ClientType" DEFAULT 'public_pkce';

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "RequirePkce" boolean NOT NULL CONSTRAINT "DF_SqlOSClientApplications_RequirePkce" DEFAULT TRUE;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "AllowedScopesJson" text NOT NULL CONSTRAINT "DF_SqlOSClientApplications_AllowedScopesJson" DEFAULT '[]';

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "IsFirstParty" boolean NOT NULL CONSTRAINT "DF_SqlOSClientApplications_IsFirstParty" DEFAULT FALSE;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSClientApplications"
    ADD COLUMN IF NOT EXISTS "AllowNativeHeadlessAuth" boolean NOT NULL CONSTRAINT "DF_SqlOSClientApplications_AllowNativeHeadlessAuth" DEFAULT FALSE;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests" ALTER COLUMN "OrganizationId" TYPE varchar(64);
ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests" ALTER COLUMN "OrganizationId" DROP NOT NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests" ALTER COLUMN "ConnectionId" TYPE varchar(64);
ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests" ALTER COLUMN "ConnectionId" DROP NOT NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests" ALTER COLUMN "LoginHintEmail" TYPE varchar(320);
ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests" ALTER COLUMN "LoginHintEmail" DROP NOT NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests"
    ADD COLUMN IF NOT EXISTS "Scope" varchar(1000) NOT NULL CONSTRAINT "DF_SqlOSAuthorizationRequests_Scope" DEFAULT '';

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests"
    ADD COLUMN IF NOT EXISTS "Resource" varchar(2048) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests"
    ADD COLUMN IF NOT EXISTS "Nonce" varchar(256) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests"
    ADD COLUMN IF NOT EXISTS "Prompt" varchar(256) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests"
    ADD COLUMN IF NOT EXISTS "ResolvedAuthMethod" varchar(50) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests"
    ADD COLUMN IF NOT EXISTS "ResolvedOrganizationId" varchar(64) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests"
    ADD COLUMN IF NOT EXISTS "ResolvedConnectionId" varchar(64) NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationCodes" ALTER COLUMN "OrganizationId" TYPE varchar(64);
ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationCodes" ALTER COLUMN "OrganizationId" DROP NOT NULL;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationCodes"
    ADD COLUMN IF NOT EXISTS "Scope" varchar(1000) NOT NULL CONSTRAINT "DF_SqlOSAuthorizationCodes_Scope" DEFAULT '';

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationCodes"
    ADD COLUMN IF NOT EXISTS "Resource" varchar(2048) NULL;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (5);

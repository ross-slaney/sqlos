-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSUsers')) IS NOT NULL AND to_regclass(format('%I.%I', '{Schema}', 'SqlOSClientApplications')) IS NOT NULL THEN
    CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSConsentGrants"
    (
        "Id" varchar(64) NOT NULL,
        "UserId" varchar(64) NOT NULL,
        "ClientApplicationId" varchar(64) NOT NULL,
        -- Approvals union scopes across requests, so the stored scope can grow well past a
        -- single request's scope; SqlOSConsentService guards the 4000-char ceiling before save.
        "Scope" varchar(4000) NOT NULL CONSTRAINT "DF_SqlOSConsentGrants_Scope" DEFAULT (''),
        "GrantedAt" timestamp NOT NULL,
        "UpdatedAt" timestamp NOT NULL,
        "RevokedAt" timestamp NULL,
        "RevocationReason" varchar(200) NULL,
        -- Fingerprint of the client's security-sensitive metadata the approval was granted
        -- against. Coverage checks reject a grant whose stored fingerprint no longer matches
        -- the client's current metadata, closing the approve/CIMD-refresh race; NULL is
        -- accepted as legacy (pre-fingerprint) data.
        "ClientMetadataFingerprint" varchar(64) NULL,
        CONSTRAINT "PK_SqlOSConsentGrants" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_SqlOSConsentGrants_User" FOREIGN KEY ("UserId") REFERENCES "{Schema}"."SqlOSUsers"("Id"),
        CONSTRAINT "FK_SqlOSConsentGrants_ClientApplication" FOREIGN KEY ("ClientApplicationId") REFERENCES "{Schema}"."SqlOSClientApplications"("Id")
    );
  END IF;
END
$sqlos_guard$;

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSConsentGrants')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSConsentGrants' AND column_name = 'UserId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSConsentGrants' AND column_name = 'ClientApplicationId') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "UX_SqlOSConsentGrants_ActiveUserClient"
        ON "{Schema}"."SqlOSConsentGrants"("UserId", "ClientApplicationId")
        WHERE "RevokedAt" IS NULL;
  END IF;
END
$sqlos_guard$;
    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSConsentGrants')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSConsentGrants' AND column_name = 'ClientApplicationId') THEN
    CREATE INDEX IF NOT EXISTS "IX_SqlOSConsentGrants_ClientApplicationId"
        ON "{Schema}"."SqlOSConsentGrants"("ClientApplicationId");
  END IF;
END
$sqlos_guard$;

CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSScopeDisplayNames"
    (
        "Id" varchar(64) NOT NULL,
        -- Binary collation so the unique Scope key distinguishes scopes the way scope
        -- policy compares them (ordinal); case-insensitive server collations would
        -- conflate e.g. 'files.read' and 'FILES.READ'.
        "Scope" varchar(200) COLLATE "C" NOT NULL,
        "DisplayName" varchar(200) NOT NULL,
        "Description" varchar(1000) NULL,
        "ConfigurationOwner" varchar(40) NOT NULL CONSTRAINT "DF_SqlOSScopeDisplayNames_ConfigurationOwner" DEFAULT ('dashboard'),
        -- The scope string is the configuration source key, so it must fit any Scope value
        -- and share Scope's binary collation: the orphan-sweep SQL compares it against the
        -- in-memory seed set ordinally.
        "ConfigurationSourceKey" varchar(200) COLLATE "C" NULL,
        "ConfigurationFingerprint" varchar(64) NULL,
        "LastReconciledAt" timestamp NULL,
        "ConfigurationOrphanedAt" timestamp NULL,
        "CreatedAt" timestamp NOT NULL,
        "UpdatedAt" timestamp NOT NULL,
        CONSTRAINT "PK_SqlOSScopeDisplayNames" PRIMARY KEY ("Id")
    );

    DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSScopeDisplayNames')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSScopeDisplayNames' AND column_name = 'Scope') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "UX_SqlOSScopeDisplayNames_Scope"
        ON "{Schema}"."SqlOSScopeDisplayNames"("Scope");
  END IF;
END
$sqlos_guard$;

-- Binds a pending consent interstitial to the user who reached it. Reload re-minting
-- must not follow the browser's mutable auth-page session cookie to a different user.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthorizationRequests"
    ADD COLUMN IF NOT EXISTS "PendingConsentUserId" varchar(64) NULL;

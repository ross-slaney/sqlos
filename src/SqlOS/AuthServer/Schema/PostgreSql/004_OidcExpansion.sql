-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
CREATE TABLE IF NOT EXISTS "{Schema}"."SqlOSAuthOidcConnections" (
        "Id" varchar(64) NOT NULL PRIMARY KEY,
        "ProviderType" varchar(40) NOT NULL,
        "DisplayName" varchar(200) NOT NULL,
        "ClientId" varchar(300) NOT NULL,
        "ClientSecretEncrypted" text NULL,
        "AllowedCallbackUrisJson" text NOT NULL,
        "UseDiscovery" boolean NOT NULL,
        "DiscoveryUrl" varchar(500) NULL,
        "Issuer" varchar(500) NULL,
        "AuthorizationEndpoint" varchar(1000) NULL,
        "TokenEndpoint" varchar(1000) NULL,
        "UserInfoEndpoint" varchar(1000) NULL,
        "JwksUri" varchar(1000) NULL,
        "MicrosoftTenant" varchar(200) NULL,
        "ScopesJson" text NOT NULL,
        "ClaimMappingJson" text NOT NULL,
        "ClientAuthMethod" varchar(40) NOT NULL,
        "UseUserInfo" boolean NOT NULL,
        "AppleTeamId" varchar(100) NULL,
        "AppleKeyId" varchar(100) NULL,
        "ApplePrivateKeyEncrypted" text NULL,
        "IsEnabled" boolean NOT NULL,
        "CreatedAt" timestamp NOT NULL,
        "UpdatedAt" timestamp NOT NULL
    );

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuthOidcConnections')) IS NOT NULL AND to_regclass(format('%I.%I', '{Schema}', 'SqlOSAuthSocialConnections')) IS NOT NULL THEN
    INSERT INTO "{Schema}"."SqlOSAuthOidcConnections" (
        "Id",
        "ProviderType",
        "DisplayName",
        "ClientId",
        "ClientSecretEncrypted",
        "AllowedCallbackUrisJson",
        "UseDiscovery",
        "DiscoveryUrl",
        "Issuer",
        "AuthorizationEndpoint",
        "TokenEndpoint",
        "UserInfoEndpoint",
        "JwksUri",
        "MicrosoftTenant",
        "ScopesJson",
        "ClaimMappingJson",
        "ClientAuthMethod",
        "UseUserInfo",
        "AppleTeamId",
        "AppleKeyId",
        "ApplePrivateKeyEncrypted",
        "IsEnabled",
        "CreatedAt",
        "UpdatedAt"
    )
    SELECT
        "Id",
        "ProviderType",
        "DisplayName",
        "ClientId",
        "ClientSecretEncrypted",
        "AllowedCallbackUrisJson",
        TRUE,
        CASE
            WHEN "ProviderType" = 'Google' THEN 'https://accounts.google.com/.well-known/openid-configuration'
            WHEN "ProviderType" = 'Microsoft' THEN CONCAT('https://login.microsoftonline.com/', COALESCE(NULLIF("MicrosoftTenant", ''), 'common'), '/v2.0/.well-known/openid-configuration')
            ELSE NULL
        END,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        "MicrosoftTenant",
        "ScopesJson",
        '{{"SubjectClaim":"sub","EmailClaim":"email","EmailVerifiedClaim":"email_verified","DisplayNameClaim":"name","FirstNameClaim":"given_name","LastNameClaim":"family_name","PreferredUsernameClaim":"preferred_username"}}',
        'ClientSecretPost',
        TRUE,
        NULL,
        NULL,
        NULL,
        "IsEnabled",
        "CreatedAt",
        "UpdatedAt"
    FROM "{Schema}"."SqlOSAuthSocialConnections";
  END IF;
END
$sqlos_guard$;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSExternalIdentities"
    ADD COLUMN IF NOT EXISTS "OidcConnectionId" varchar(64) NULL;

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSExternalIdentities')) IS NOT NULL THEN
    UPDATE "{Schema}"."SqlOSExternalIdentities"
    SET "OidcConnectionId" = "SocialConnectionId"
    WHERE "OidcConnectionId" IS NULL
      AND "SocialConnectionId" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSExternalIdentities"
    DROP CONSTRAINT IF EXISTS "FK_SqlOSExternalIdentities_SocialConnections";

DO $sqlos$
BEGIN
  ALTER TABLE IF EXISTS "{Schema}"."SqlOSExternalIdentities" ADD CONSTRAINT "FK_SqlOSExternalIdentities_OidcConnections" FOREIGN KEY ("OidcConnectionId") REFERENCES "{Schema}"."SqlOSAuthOidcConnections"("Id");
EXCEPTION WHEN duplicate_object THEN NULL;
END
$sqlos$;

DROP INDEX IF EXISTS "{Schema}"."IX_SqlOSExternalIdentities_SocialConnectionId_Subject";

DO $sqlos_guard$
BEGIN
  IF to_regclass(format('%I.%I', '{Schema}', 'SqlOSExternalIdentities')) IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSExternalIdentities' AND column_name = 'OidcConnectionId') AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = '{Schema}' AND table_name = 'SqlOSExternalIdentities' AND column_name = 'Subject') THEN
    CREATE UNIQUE INDEX IF NOT EXISTS "IX_SqlOSExternalIdentities_OidcConnectionId_Subject"
    ON "{Schema}"."SqlOSExternalIdentities"("OidcConnectionId", "Subject")
    WHERE "OidcConnectionId" IS NOT NULL;
  END IF;
END
$sqlos_guard$;

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (4);

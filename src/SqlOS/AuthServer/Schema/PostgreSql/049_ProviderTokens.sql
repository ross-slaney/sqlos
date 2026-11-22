-- PostgreSQL translation of the matching SQL Server script.
-- A social login connection can keep the provider's tokens beside each person's
-- external identity (off by default). Tokens are protected with Data Protection.

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthOidcConnections"
        ADD COLUMN IF NOT EXISTS "StoreProviderTokens" boolean NOT NULL
            CONSTRAINT "DF_SqlOSAuthOidcConnections_StoreProviderTokens" DEFAULT FALSE;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSExternalIdentities" ADD COLUMN IF NOT EXISTS "ProviderAccessTokenEncrypted" text NULL;
ALTER TABLE IF EXISTS "{Schema}"."SqlOSExternalIdentities" ADD COLUMN IF NOT EXISTS "ProviderRefreshTokenEncrypted" text NULL;
ALTER TABLE IF EXISTS "{Schema}"."SqlOSExternalIdentities" ADD COLUMN IF NOT EXISTS "ProviderAccessTokenExpiresAt" timestamp NULL;
ALTER TABLE IF EXISTS "{Schema}"."SqlOSExternalIdentities" ADD COLUMN IF NOT EXISTS "ProviderGrantedScopesJson" text NULL;
ALTER TABLE IF EXISTS "{Schema}"."SqlOSExternalIdentities" ADD COLUMN IF NOT EXISTS "ProviderTokensUpdatedAt" timestamp NULL;

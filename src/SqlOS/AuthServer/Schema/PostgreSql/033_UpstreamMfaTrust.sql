-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSSsoConnections"
        ADD COLUMN IF NOT EXISTS "TrustUpstreamMfa" boolean NOT NULL
            CONSTRAINT "DF_SqlOSSsoConnections_TrustUpstreamMfa" DEFAULT FALSE;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSSsoConnections"
        ADD COLUMN IF NOT EXISTS "AcceptedAuthnContextClassRefsJson" text NOT NULL
            CONSTRAINT "DF_SqlOSSsoConnections_AcceptedAuthnContextClassRefsJson" DEFAULT '[]';

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthOidcConnections"
        ADD COLUMN IF NOT EXISTS "TrustUpstreamMfa" boolean NOT NULL
            CONSTRAINT "DF_SqlOSAuthOidcConnections_TrustUpstreamMfa" DEFAULT FALSE;

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthOidcConnections"
        ADD COLUMN IF NOT EXISTS "AcceptedAmrValuesJson" text NOT NULL
            CONSTRAINT "DF_SqlOSAuthOidcConnections_AcceptedAmrValuesJson" DEFAULT '[]';

ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthOidcConnections"
        ADD COLUMN IF NOT EXISTS "AcceptedAcrValuesJson" text NOT NULL
            CONSTRAINT "DF_SqlOSAuthOidcConnections_AcceptedAcrValuesJson" DEFAULT '[]';

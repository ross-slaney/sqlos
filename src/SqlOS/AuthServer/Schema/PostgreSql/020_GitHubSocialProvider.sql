-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
ALTER TABLE IF EXISTS "{Schema}"."SqlOSAuthOidcConnections"
    ADD COLUMN IF NOT EXISTS "Protocol" varchar(40) NOT NULL
        CONSTRAINT "DF_SqlOSAuthOidcConnections_Protocol" DEFAULT ('Oidc');

DELETE FROM "{Schema}"."SqlOSSchema";
INSERT INTO "{Schema}"."SqlOSSchema" ("Version") VALUES (20);

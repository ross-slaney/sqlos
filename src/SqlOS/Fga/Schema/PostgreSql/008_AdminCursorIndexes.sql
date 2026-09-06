-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
-- SqlOSFga Schema v8: admin cursor-pagination indexes.
-- Name/key columns remain text and cannot host a full keyset index.
-- Id is varchar(450). Putting two of those in one nonclustered key is 1,800
-- bytes and exceeds SQL Server's 1,700-byte limit, so the unique tiebreaker
-- is INCLUDE'd instead of keyed.

CREATE INDEX IF NOT EXISTS "IX_{Resources}_ParentId_Id"
        ON "{Schema}"."{Resources}"("ParentId")
        INCLUDE ("Id");

CREATE INDEX IF NOT EXISTS "IX_{Grants}_CreatedAt_Id"
        ON "{Schema}"."{Grants}"("CreatedAt" DESC, "Id" DESC);

CREATE INDEX IF NOT EXISTS "IX_{Grants}_SubjectId_CreatedAt_Id"
        ON "{Schema}"."{Grants}"("SubjectId", "CreatedAt" DESC)
        INCLUDE ("Id");

CREATE INDEX IF NOT EXISTS "IX_{Grants}_ResourceId_CreatedAt_Id"
        ON "{Schema}"."{Grants}"("ResourceId", "CreatedAt" DESC)
        INCLUDE ("Id");

UPDATE "{Schema}"."SqlOSFgaSchema" SET "Version" = 8 WHERE "Version" < 8;

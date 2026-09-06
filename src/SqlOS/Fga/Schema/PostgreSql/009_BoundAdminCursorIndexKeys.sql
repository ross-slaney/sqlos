-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
-- SqlOSFga Schema v9: keep admin cursor index keys under SQL Server's 1,700-byte limit.
-- v8 keyed two varchar(450) columns (1,800 bytes). Recreate those indexes with the
-- unique Id tiebreaker INCLUDE'd so max-length resource and grant writes stay valid.

DROP INDEX IF EXISTS "{Schema}"."IX_{Resources}_ParentId_Id";

CREATE INDEX IF NOT EXISTS "IX_{Resources}_ParentId_Id"
    ON "{Schema}"."{Resources}"("ParentId")
    INCLUDE ("Id");

DROP INDEX IF EXISTS "{Schema}"."IX_{Grants}_SubjectId_CreatedAt_Id";

CREATE INDEX IF NOT EXISTS "IX_{Grants}_SubjectId_CreatedAt_Id"
    ON "{Schema}"."{Grants}"("SubjectId", "CreatedAt" DESC)
    INCLUDE ("Id");

DROP INDEX IF EXISTS "{Schema}"."IX_{Grants}_ResourceId_CreatedAt_Id";

CREATE INDEX IF NOT EXISTS "IX_{Grants}_ResourceId_CreatedAt_Id"
    ON "{Schema}"."{Grants}"("ResourceId", "CreatedAt" DESC)
    INCLUDE ("Id");

UPDATE "{Schema}"."SqlOSFgaSchema" SET "Version" = 9 WHERE "Version" < 9;

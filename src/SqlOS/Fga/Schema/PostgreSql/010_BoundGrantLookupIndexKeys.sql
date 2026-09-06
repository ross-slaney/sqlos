-- PostgreSQL translation of the matching SQL Server script.
-- CREATE/ALTER statements are idempotent via IF NOT EXISTS.
-- SqlOSFga Schema v10: keep the v4 grant/role-permission lookup indexes under
-- SQL Server's 1,700-byte nonclustered key limit. Two varchar(450) key columns
-- are 1,800 bytes and reject otherwise-valid max-length identifiers.

DROP INDEX IF EXISTS "{Schema}"."IX_{Grants}_ResourceId_SubjectId";

CREATE INDEX IF NOT EXISTS "IX_{Grants}_ResourceId_SubjectId"
    ON "{Schema}"."{Grants}"("ResourceId")
    INCLUDE ("SubjectId", "RoleId", "EffectiveFrom", "EffectiveTo");

DROP INDEX IF EXISTS "{Schema}"."IX_{RolePermissions}_PermissionId_RoleId";

CREATE INDEX IF NOT EXISTS "IX_{RolePermissions}_PermissionId_RoleId"
    ON "{Schema}"."{RolePermissions}"("PermissionId")
    INCLUDE ("RoleId");

UPDATE "{Schema}"."SqlOSFgaSchema" SET "Version" = 10 WHERE "Version" < 10;
